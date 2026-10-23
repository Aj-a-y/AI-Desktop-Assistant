using System.Diagnostics;
using System.IO;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows.Threading;
using NAudio.Wave;

namespace DesktopAssistant;

internal sealed record TtsSpeechOptions(string Voice, double Speed, int Volume);

internal interface ITtsService : IDisposable
{
    string Name { get; }
    TimeSpan LastStartupLatency { get; }
    TimeSpan LastFirstAudioLatency { get; }
    event Action<bool>? PlaybackStateChanged;
    event Action<string>? StatusChanged;
    Task SpeakAsync(string text, TtsSpeechOptions options, CancellationToken cancellationToken);
    Task StopAsync();
}

internal sealed class SystemSpeechTtsService : ITtsService
{
    private readonly SpeechSynthesizer _speaker;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource? _currentSpeech;
    private CancellationToken _currentToken;
    private Stopwatch? _speechTimer;
    private bool _reportedFirstAudio;

    public string Name => "Windows system voice";
    public TimeSpan LastStartupLatency { get; }
    public TimeSpan LastFirstAudioLatency { get; private set; }
    public event Action<bool>? PlaybackStateChanged;
    public event Action<string>? StatusChanged;

    public SystemSpeechTtsService()
    {
        var timer = Stopwatch.StartNew();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _speaker = new SpeechSynthesizer();
        LastStartupLatency = timer.Elapsed;
        _speaker.SpeakStarted += (_, _) =>
        {
            LastFirstAudioLatency = _speechTimer?.Elapsed ?? TimeSpan.Zero;
            if (!_reportedFirstAudio)
            {
                _reportedFirstAudio = true;
                StatusChanged?.Invoke($"Windows speech first audio in {LastFirstAudioLatency.TotalMilliseconds:F0} ms.");
            }
            PlaybackStateChanged?.Invoke(true);
        };
        _speaker.SpeakCompleted += (_, args) =>
        {
            PlaybackStateChanged?.Invoke(false);
            if (args.Cancelled || _currentToken.IsCancellationRequested)
                _currentSpeech?.TrySetCanceled(_currentToken.IsCancellationRequested ? _currentToken : new CancellationToken(true));
            else
                _currentSpeech?.TrySetResult();
        };
    }

    public async Task SpeakAsync(string text, TtsSpeechOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _currentToken = cancellationToken;
            _currentSpeech = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _speechTimer = Stopwatch.StartNew();
            using var registration = cancellationToken.Register(() => _ = RunOnDispatcherAsync(_speaker.SpeakAsyncCancelAll));
            await RunOnDispatcherAsync(() =>
            {
                SelectVoiceIfAvailable(options.Voice);
                _speaker.Volume = Math.Clamp(options.Volume, 0, 100);
                _speaker.Rate = Math.Clamp((int)Math.Round((options.Speed - 1.0) * 10), -10, 10);
                _speaker.SpeakAsync(text);
            }).ConfigureAwait(false);
            await _currentSpeech.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            _currentSpeech = null;
            _gate.Release();
        }
    }

    private void SelectVoiceIfAvailable(string requestedVoice)
    {
        try
        {
            if (_speaker.GetInstalledVoices().Any(voice => string.Equals(voice.VoiceInfo.Name, requestedVoice, StringComparison.OrdinalIgnoreCase)))
                _speaker.SelectVoice(requestedVoice);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            StatusChanged?.Invoke($"The selected Windows voice is unavailable. Using the default Windows voice. {ex.Message}");
        }
    }

    public Task StopAsync()
    {
        return RunOnDispatcherAsync(_speaker.SpeakAsyncCancelAll);
    }

    public void Dispose()
    {
        _ = RunOnDispatcherAsync(() =>
        {
            _speaker.SpeakAsyncCancelAll();
            _speaker.Dispose();
        });
    }

    private Task RunOnDispatcherAsync(Action action) => _dispatcher.CheckAccess()
        ? RunAction(action)
        : _dispatcher.InvokeAsync(action).Task;

    private static Task RunAction(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}

internal sealed class KokoroWorkerTtsService : ITtsService
{
    private readonly OllamaRuntimeSettings _settings;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _stateLock = new();
    private Process? _worker;
    private WaveOut? _player;
    private string? _workerError;
    private bool _reportedFirstAudio;

    public string Name => "Kokoro ONNX (CPU)";
    public TimeSpan LastStartupLatency { get; private set; }
    public TimeSpan LastFirstAudioLatency { get; private set; }
    public event Action<bool>? PlaybackStateChanged;
    public event Action<string>? StatusChanged;

    public KokoroWorkerTtsService(OllamaRuntimeSettings settings) => _settings = settings;

    public async Task SpeakAsync(string text, TtsSpeechOptions options, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var firstAudioTimer = Stopwatch.StartNew();
        string? audioPath = null;
        try
        {
            await EnsureWorkerReadyAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var cacheDirectory = Path.Combine(Path.GetDirectoryName(_settings.KokoroModelPath)!, "audio-cache");
            Directory.CreateDirectory(cacheDirectory);
            audioPath = Path.Combine(cacheDirectory, $"speech-{Guid.NewGuid():N}.wav");
            var request = JsonSerializer.Serialize(new
            {
                text,
                voice = options.Voice,
                speed = Math.Clamp(options.Speed, 0.8, 1.2),
                output_wav = audioPath
            });
            var worker = _worker ?? throw new InvalidOperationException("The Kokoro worker is not running.");
            await worker.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken).ConfigureAwait(false);
            await worker.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            var responseLine = await worker.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (responseLine is null)
                throw new InvalidOperationException(_workerError is { Length: > 0 }
                    ? $"The Kokoro worker stopped: {_workerError}"
                    : "The Kokoro worker exited without returning audio.");
            using var response = JsonDocument.Parse(responseLine);
            var root = response.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException(root.TryGetProperty("error", out var error)
                    ? error.GetString() ?? "Kokoro could not generate speech."
                    : "Kokoro returned an invalid worker response.");
            if (!File.Exists(audioPath)) throw new FileNotFoundException("Kokoro reported success but its audio file is missing.", audioPath);

            LastFirstAudioLatency = firstAudioTimer.Elapsed;
            if (!_reportedFirstAudio)
            {
                _reportedFirstAudio = true;
                StatusChanged?.Invoke($"Kokoro first audio in {LastFirstAudioLatency.TotalSeconds:F1}s (worker startup {LastStartupLatency.TotalSeconds:F1}s).");
            }
            await PlayWavAsync(audioPath, options.Volume, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            if (audioPath is not null)
            {
                try { File.Delete(audioPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _requestGate.Release();
        }
    }

    private async Task EnsureWorkerReadyAsync(CancellationToken cancellationToken)
    {
        if (_worker is { HasExited: false }) return;
        if (!File.Exists(_settings.KokoroModelPath))
            throw new FileNotFoundException($"Kokoro model not found at '{_settings.KokoroModelPath}'. See TTS_SETUP.md for the manual setup steps.", _settings.KokoroModelPath);
        if (!File.Exists(_settings.KokoroVoicesPath))
            throw new FileNotFoundException($"Kokoro voice pack not found at '{_settings.KokoroVoicesPath}'. See TTS_SETUP.md for the manual setup steps.", _settings.KokoroVoicesPath);
        var workerScript = Path.Combine(AppContext.BaseDirectory, "Tts", "kokoro_worker.py");
        if (!File.Exists(workerScript)) throw new FileNotFoundException("The bundled Kokoro worker script is missing.", workerScript);

        var timer = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = _settings.KokoroPythonPath,
            WorkingDirectory = Path.GetDirectoryName(_settings.KokoroModelPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(workerScript);
        startInfo.ArgumentList.Add(_settings.KokoroModelPath);
        startInfo.ArgumentList.Add(_settings.KokoroVoicesPath);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) _workerError = args.Data;
        };
        if (!process.Start()) throw new InvalidOperationException("Windows could not start the Kokoro Python worker.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (System.ComponentModel.Win32Exception) { }
        process.BeginErrorReadLine();
        _worker = process;

        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        var readyLine = await process.StandardOutput.ReadLineAsync(startupTimeout.Token).ConfigureAwait(false);
        if (readyLine is null)
        {
            var details = _workerError is { Length: > 0 } ? $" Details: {_workerError}" : "";
            StopWorker();
            throw new InvalidOperationException($"Kokoro did not initialize. Check the Python packages and model paths.{details}");
        }
        using var ready = JsonDocument.Parse(readyLine);
        if (!ready.RootElement.TryGetProperty("ready", out var isReady) || isReady.ValueKind != JsonValueKind.True)
        {
            var reason = ready.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
            StopWorker();
            throw new InvalidOperationException(reason ?? "Kokoro worker returned an invalid startup response.");
        }
        LastStartupLatency = timer.Elapsed;
        StatusChanged?.Invoke($"Kokoro local voice ready in {LastStartupLatency.TotalSeconds:F1}s.");
    }

    private async Task PlayWavAsync(string audioPath, int volume, CancellationToken cancellationToken)
    {
        using var reader = new WaveFileReader(audioPath);
        var player = new WaveOut();
        var completed = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PlaybackStopped += (_, args) => completed.TrySetResult(args);
        player.Volume = Math.Clamp(volume, 0, 100) / 100f;
        player.Init(reader);
        lock (_stateLock) _player = player;
        using var registration = cancellationToken.Register(() => player.Stop());
        try
        {
            player.Play();
            PlaybackStateChanged?.Invoke(true);
            var result = await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Exception is not null) throw result.Exception;
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            PlaybackStateChanged?.Invoke(false);
            lock (_stateLock) _player = null;
            player.Dispose();
        }
    }

    public Task StopAsync()
    {
        lock (_stateLock) _player?.Stop();
        StopWorker();
        return Task.CompletedTask;
    }

    private void StopWorker()
    {
        var process = _worker;
        _worker = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose()
    {
        _ = StopAsync();
        _requestGate.Dispose();
    }
}

internal sealed class FallbackTtsService : ITtsService
{
    private readonly OllamaRuntimeSettings _settings;
    private readonly ITtsService _kokoro;
    private readonly ITtsService _windows;
    private bool _kokoroFailed;
    private bool _reportedMissingAssets;

    private bool HasKokoroAssets => File.Exists(_settings.KokoroModelPath) && File.Exists(_settings.KokoroVoicesPath);
    private bool UsingWindows => _kokoroFailed || _settings.TtsEngine == "windows" || !HasKokoroAssets;
    public string Name => UsingWindows ? _windows.Name : _kokoro.Name;
    public TimeSpan LastStartupLatency => UsingWindows ? _windows.LastStartupLatency : _kokoro.LastStartupLatency;
    public TimeSpan LastFirstAudioLatency => UsingWindows ? _windows.LastFirstAudioLatency : _kokoro.LastFirstAudioLatency;
    public event Action<bool>? PlaybackStateChanged;
    public event Action<string>? StatusChanged;

    public FallbackTtsService(OllamaRuntimeSettings settings)
    {
        _settings = settings;
        _kokoro = new KokoroWorkerTtsService(settings);
        _windows = new SystemSpeechTtsService();
        _kokoro.PlaybackStateChanged += value => PlaybackStateChanged?.Invoke(value);
        _windows.PlaybackStateChanged += value => PlaybackStateChanged?.Invoke(value);
        _kokoro.StatusChanged += message => StatusChanged?.Invoke(message);
        _windows.StatusChanged += message => StatusChanged?.Invoke(message);
    }

    public async Task SpeakAsync(string text, TtsSpeechOptions options, CancellationToken cancellationToken)
    {
        if (_settings.TtsEngine == "windows" || _kokoroFailed || !HasKokoroAssets)
        {
            if (!HasKokoroAssets && !_reportedMissingAssets)
            {
                _reportedMissingAssets = true;
                StatusChanged?.Invoke("Kokoro model or voice files are missing. Using the Windows system voice. See TTS_SETUP.md for optional local setup.");
            }
            await _windows.SpeakAsync(text, options, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _kokoro.SpeakAsync(text, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _kokoroFailed = true;
            await _kokoro.StopAsync().ConfigureAwait(false);
            StatusChanged?.Invoke($"Kokoro is unavailable ({ex.Message}). Falling back to the Windows system voice.");
            await _windows.SpeakAsync(text, options, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        await Task.WhenAll(_kokoro.StopAsync(), _windows.StopAsync()).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _kokoro.Dispose();
        _windows.Dispose();
    }
}

internal sealed record QueuedSpeech(string Text, CancellationToken CancellationToken);

internal sealed class SpeechChunkParser
{
    private readonly System.Text.StringBuilder _pending = new();
    private bool _insideCodeFence;

    public IReadOnlyList<string> Append(string fragment)
    {
        _pending.Append(fragment);
        var completed = new List<string>();
        while (TryFindBoundary(out var boundary))
        {
            var segment = _pending.ToString(0, boundary);
            _pending.Remove(0, boundary);
            var spoken = CleanMarkup(segment);
            if (spoken.Length > 0) completed.Add(spoken);
        }
        return completed;
    }

    public string Complete()
    {
        var remaining = _pending.ToString();
        _pending.Clear();
        return CleanMarkup(remaining);
    }

    private bool TryFindBoundary(out int boundary)
    {
        for (var index = 0; index < _pending.Length; index++)
        {
            var character = _pending[index];
            if (character == '\n')
            {
                boundary = index + 1;
                return true;
            }
            if (character is '.' or '!' or '?'
                && (index + 1 < _pending.Length && char.IsWhiteSpace(_pending[index + 1])))
            {
                boundary = index + 1;
                while (boundary < _pending.Length && char.IsWhiteSpace(_pending[boundary])) boundary++;
                return true;
            }
        }
        boundary = 0;
        return false;
    }

    private string CleanMarkup(string segment)
    {
        var plain = new System.Text.StringBuilder(segment.Length);
        var position = 0;
        while (position < segment.Length)
        {
            var fence = segment.IndexOf("```", position, StringComparison.Ordinal);
            if (_insideCodeFence)
            {
                if (fence < 0) return string.Empty;
                _insideCodeFence = false;
                position = fence + 3;
                continue;
            }
            if (fence < 0)
            {
                plain.Append(segment, position, segment.Length - position);
                break;
            }
            plain.Append(segment, position, fence - position);
            _insideCodeFence = true;
            position = fence + 3;
        }

        var text = System.Text.RegularExpressions.Regex.Replace(plain.ToString(), "`[^`]*`", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"!?\[([^\]]*)\]\([^)]*\)", "$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s{0,3}(?:#{1,6}\s*|>\s*|[-*+]\s+|\d+[.)]\s+)", "");
        text = text.Replace('|', ' ');
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[*_~]", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
