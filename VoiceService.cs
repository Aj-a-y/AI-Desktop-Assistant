using System.IO;
using System.Buffers.Binary;
using System.Speech.Synthesis;
using System.Threading.Channels;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace DesktopAssistant;

internal enum VoiceInteractionState { Idle, Listening, Transcribing, Thinking, Speaking, Error }
internal sealed record MicrophoneChoice(int DeviceNumber, string Name);

internal sealed class VoiceService : IDisposable
{
    private const int SpeechThreshold = 900;
    private const int RequiredSpeechFrames = 4;
    private readonly TimeSpan _endOfUtteranceSilence;
    private static readonly HashSet<string> FillerOnlyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ah", "aah", "ahem", "eh", "er", "erm", "hmm", "hm", "huh", "mm", "mmm", "uh", "uhh", "um", "umm"
    };
    private readonly WaveFormat _format = new(16000, 16, 1);
    private WaveIn _input;
    private const int SpeechQueueCapacity = 6;
    private readonly OllamaRuntimeSettings _settings;
    private readonly ITtsService _ttsService;
    private readonly Channel<QueuedSpeech> _speechQueue = Channel.CreateBounded<QueuedSpeech>(new BoundedChannelOptions(SpeechQueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _speechShutdown = new();
    private CancellationTokenSource _speechTurnCancellation = new();
    private readonly Task _speechPump;
    private readonly object _audioLock = new();
    private readonly object _modelLock = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly Queue<byte[]> _preRoll = new();
    private readonly MemoryStream _currentUtterance = new();
    private Task? _prepareTask;
    private readonly CancellationTokenSource _recognizerShutdown = new();
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private bool _listening;
    private bool _speechDetected;
    private int _consecutiveSpeechFrames;
    private double _noiseFloor;
    private bool _isSpeaking;
    private bool _speechQueueOpen;
    private bool _speechCompletionRaised;
    private bool _voiceOutputMuted;
    private bool _pushToTalkActive;
    private int _queuedSpeechCount;
    private DateTime _lastSpeechAt;
    private readonly MemoryStream _pushToTalkAudio = new();
    private const int MaximumUtteranceBytes = 16000 * 2 * 45;
    private static readonly int MaxPushToTalkBytes = MaximumUtteranceBytes;
    private VoiceInteractionState _voiceState = VoiceInteractionState.Idle;
    private TimeSpan _lastRecognitionLatency;

    public event Action<byte[], bool>? UtteranceReady;
    public event Action<bool>? SpeechStateChanged;
    public event Action<VoiceInteractionState>? VoiceStateChanged;
    public event Action? SpeechTurnCompleted;
    public event Action<Exception>? AudioError;
    public event Action<string>? TtsStatusChanged;

    public VoiceService(OllamaRuntimeSettings? settings = null)
    {
        _settings = settings ?? OllamaRuntimeSettings.Load();
        _endOfUtteranceSilence = TimeSpan.FromMilliseconds(_settings.VoiceSilenceMs);
        _ttsService = new FallbackTtsService(_settings);
        _ttsService.PlaybackStateChanged += SetSpeaking;
        _ttsService.StatusChanged += message => TtsStatusChanged?.Invoke(message);
        _speechPump = Task.Run(ProcessSpeechQueueAsync);
        _input = CreateInput(_settings.MicrophoneDeviceNumber);
    }

    public bool IsListening => _listening;
    public bool IsSpeaking => _isSpeaking;
    public bool IsVoiceOutputMuted => _voiceOutputMuted;
    public bool HasPendingSpeech { get { lock (_audioLock) return _speechQueueOpen || _queuedSpeechCount > 0; } }
    public VoiceInteractionState State => _voiceState;
    public TimeSpan LastRecognitionLatency => _lastRecognitionLatency;

    private WaveIn CreateInput(int deviceNumber)
    {
        var input = new WaveIn { WaveFormat = _format, BufferMilliseconds = 100, DeviceNumber = deviceNumber };
        input.DataAvailable += Input_DataAvailable;
        input.RecordingStopped += (_, args) =>
        {
            lock (_audioLock) _inputStarted = false;
            if (args.Exception is not null)
            {
                SetVoiceState(VoiceInteractionState.Error);
                AudioError?.Invoke(args.Exception);
            }
        };
        return input;
    }

    public static IReadOnlyList<MicrophoneChoice> GetMicrophones()
    {
        var devices = new List<MicrophoneChoice> { new(-1, "Windows default microphone") };
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            try { devices.Add(new MicrophoneChoice(index, WaveIn.GetCapabilities(index).ProductName)); }
            catch { /* A device can disappear while Windows enumerates audio hardware. */ }
        }
        return devices;
    }

    public void ApplyAudioSettings()
    {
        lock (_audioLock)
        {
            if (_inputStarted) return;
            _input.Dispose();
            _input = CreateInput(_settings.MicrophoneDeviceNumber);
        }
    }

    public void StartListening()
    {
        lock (_audioLock)
        {
            if (_inputStarted) return;
            _listening = true;
            _input.StartRecording();
            _inputStarted = true;
        }
        SetVoiceState(VoiceInteractionState.Listening);
        _ = WarmRecognizerAsync();
    }

    public void StartPushToTalk()
    {
        lock (_audioLock)
        {
            if (_pushToTalkActive) return;
            if (!_inputStarted)
            {
                _input.StartRecording();
                _inputStarted = true;
            }
            ResetUtterance();
            _pushToTalkAudio.SetLength(0);
            _pushToTalkActive = true;
            _listening = true;
        }
        SetVoiceState(VoiceInteractionState.Listening);
        _ = WarmRecognizerAsync();
    }

    public void StopPushToTalk()
    {
        byte[]? audio = null;
        lock (_audioLock)
        {
            if (!_pushToTalkActive) return;
            _pushToTalkActive = false;
            _listening = false;
            if (_pushToTalkAudio.Length >= _format.AverageBytesPerSecond / 2)
                audio = _pushToTalkAudio.ToArray();
            _pushToTalkAudio.SetLength(0);
            if (_inputStarted)
            {
                _input.StopRecording();
                _inputStarted = false;
            }
        }
        if (audio is null) SetVoiceState(VoiceInteractionState.Idle);
        else
        {
            SetVoiceState(VoiceInteractionState.Transcribing);
            UtteranceReady?.Invoke(audio, true);
        }
    }

    private bool _inputStarted;

    public void PauseListening()
    {
        lock (_audioLock)
        {
            _listening = false;
            _pushToTalkActive = false;
            ResetUtterance();
        }
        SetVoiceState(VoiceInteractionState.Idle);
    }

    public void StopListening()
    {
        lock (_audioLock)
        {
            _listening = false;
            _pushToTalkActive = false;
            ResetUtterance();
            if (_inputStarted)
            {
                _input.StopRecording();
                _inputStarted = false;
            }
        }
        SetVoiceState(VoiceInteractionState.Idle);
    }

    public void ResumeListening()
    {
        try
        {
            lock (_audioLock)
            {
                if (!_inputStarted)
                {
                    _input.StartRecording();
                    _inputStarted = true;
                }
                _listening = true;
            }
            if (!_isSpeaking) SetVoiceState(VoiceInteractionState.Listening);
        }
        catch (Exception ex)
        {
            SetVoiceState(VoiceInteractionState.Error);
            AudioError?.Invoke(ex);
        }
    }

    public void BeginSpeechTurn()
    {
        var shouldStopOldTurn = false;
        CancellationTokenSource oldTurn;
        lock (_audioLock)
        {
            ResetUtterance();
            _listening = false; // Half duplex: ignore microphone input for the complete assistant response.
            oldTurn = _speechTurnCancellation;
            shouldStopOldTurn = _queuedSpeechCount > 0;
            _speechTurnCancellation = new CancellationTokenSource();
            _speechQueueOpen = true;
            _speechCompletionRaised = false;
        }
        oldTurn.Cancel();
        oldTurn.Dispose();
        if (shouldStopOldTurn) _ = StopTtsSafelyAsync();
    }

    public async Task QueueSpeechAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_voiceOutputMuted || string.IsNullOrWhiteSpace(text)) return;
        CancellationToken turnToken;
        lock (_audioLock)
        {
            if (!_speechQueueOpen)
            {
                _speechQueueOpen = true;
                _speechCompletionRaised = false;
            }
            _queuedSpeechCount++;
            turnToken = _speechTurnCancellation.Token;
        }
        try
        {
            using var enqueueCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, turnToken);
            await _speechQueue.Writer.WriteAsync(new QueuedSpeech(text, turnToken), enqueueCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            FinishSpeechItem();
            throw;
        }
    }

    public void CompleteSpeechTurn()
    {
        var completed = false;
        lock (_audioLock)
        {
            _speechQueueOpen = false;
            completed = CompleteSpeechIfReady();
        }
        if (completed) SetVoiceState(VoiceInteractionState.Idle);
        if (completed) SpeechTurnCompleted?.Invoke();
    }

    public async Task SpeakOneAsync(string text, CancellationToken cancellationToken = default)
    {
        BeginSpeechTurn();
        try { await QueueSpeechAsync(text, cancellationToken).ConfigureAwait(false); }
        finally { CompleteSpeechTurn(); }
    }

    public void SetVoiceOutputMuted(bool muted)
    {
        _voiceOutputMuted = muted;
        if (muted) StopSpeaking();
    }

    private bool CompleteSpeechIfReady()
    {
        if (_speechQueueOpen || _queuedSpeechCount != 0 || _speechCompletionRaised) return false;
        _speechCompletionRaised = true;
        return true;
    }

    private void Input_DataAvailable(object? sender, WaveInEventArgs args)
    {
        byte[]? completedUtterance = null;
        lock (_audioLock)
        {
            if (!_listening || args.BytesRecorded < 2) return;
            if (_pushToTalkActive)
            {
                var remaining = MaxPushToTalkBytes - (int)_pushToTalkAudio.Length;
                var bytesToCopy = Math.Min(args.BytesRecorded, Math.Max(0, remaining));
                if (bytesToCopy > 0) _pushToTalkAudio.Write(args.Buffer, 0, bytesToCopy);
                return;
            }
            var block = new byte[args.BytesRecorded];
            Buffer.BlockCopy(args.Buffer, 0, block, 0, args.BytesRecorded);
            var rms = CalculateRms(block);
            if (!_speechDetected && rms < SpeechThreshold * 1.5)
                _noiseFloor = _noiseFloor == 0 ? rms : _noiseFloor * 0.95 + rms * 0.05;
            var threshold = Math.Max(SpeechThreshold, _noiseFloor * 2.5);
            // The laptop speakers can leak the assistant's own voice back into the microphone.
            // Require a stronger signal during playback while still allowing the user to barge in.
            if (_isSpeaking) threshold *= 1.8;
            var speech = rms >= threshold;

            if (_speechDetected)
            {
                _currentUtterance.Write(block);
                if (speech) _lastSpeechAt = DateTime.UtcNow;
                if (_currentUtterance.Length >= MaximumUtteranceBytes
                    || (!speech && DateTime.UtcNow - _lastSpeechAt >= _endOfUtteranceSilence))
                {
                    if (_currentUtterance.Length >= _format.AverageBytesPerSecond / 2)
                        completedUtterance = _currentUtterance.ToArray();
                    _listening = false;
                    ResetUtterance();
                }
            }
            else
            {
                _preRoll.Enqueue(block);
                while (_preRoll.Count > 5) _preRoll.Dequeue();
                if (speech)
                {
                    _consecutiveSpeechFrames++;
                    if (_consecutiveSpeechFrames >= RequiredSpeechFrames)
                    {
                        _speechDetected = true;
                        while (_preRoll.TryDequeue(out var previous)) _currentUtterance.Write(previous);
                        _lastSpeechAt = DateTime.UtcNow;
                    }
                }
                else _consecutiveSpeechFrames = 0;
            }
        }

        if (completedUtterance is not null)
        {
            SetVoiceState(VoiceInteractionState.Transcribing);
            UtteranceReady?.Invoke(completedUtterance, false);
        }
    }

    private static double CalculateRms(byte[] pcm)
    {
        double sum = 0;
        var count = pcm.Length / 2;
        for (var index = 0; index + 1 < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8);
            sum += (double)sample * sample;
        }
        return count == 0 ? 0 : Math.Sqrt(sum / count);
    }

    public async Task<string> TranscribeAsync(byte[] pcmAudio, CancellationToken cancellationToken)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        if (pcmAudio.Length < _format.AverageBytesPerSecond / 2)
        {
            _lastRecognitionLatency = timer.Elapsed;
            SetVoiceState(VoiceInteractionState.Idle);
            return string.Empty;
        }
        await _recognitionGate.WaitAsync(cancellationToken);
        try
        {
            await PrepareRecognizerAsync(cancellationToken);
            var wavBytes = WrapPcmAsWave(pcmAudio);
            await using var wav = new MemoryStream(wavBytes, writable: false);
            var transcript = new System.Text.StringBuilder();
            await foreach (var result in _processor!.ProcessAsync(wav, cancellationToken)) transcript.Append(result.Text);
            var recognized = transcript.ToString().Trim();
            return HasMeaningfulWords(recognized) ? recognized : string.Empty;
        }
        finally
        {
            _lastRecognitionLatency = timer.Elapsed;
            if (_voiceState == VoiceInteractionState.Transcribing) SetVoiceState(VoiceInteractionState.Idle);
            _recognitionGate.Release();
        }
    }

    private static bool HasMeaningfulWords(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim('.', ',', '!', '?', ':', ';', '\'', '"', '(', ')', '[', ']'))
            .Where(word => word.Length > 0)
            .ToArray();
        return words.Any(word => word.Length > 1 && !FillerOnlyWords.Contains(word));
    }

    private static byte[] WrapPcmAsWave(byte[] pcm)
    {
        var wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), 32000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wave.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wave, 44);
        return wave;
    }

    public void StopSpeaking()
    {
        CancellationTokenSource cancelledTurn;
        var drainedItems = 0;
        var completed = false;
        lock (_audioLock)
        {
            cancelledTurn = _speechTurnCancellation;
            _speechTurnCancellation = new CancellationTokenSource();
            _speechQueueOpen = false;
            while (_speechQueue.Reader.TryRead(out _)) drainedItems++;
            _queuedSpeechCount = Math.Max(0, _queuedSpeechCount - drainedItems);
            completed = CompleteSpeechIfReady();
        }
        cancelledTurn.Cancel();
        cancelledTurn.Dispose();
        _ = StopTtsSafelyAsync();
        SetSpeaking(false);
        if (completed) SpeechTurnCompleted?.Invoke();
    }

    public void SetVoiceState(VoiceInteractionState state)
    {
        if (_voiceState == state) return;
        _voiceState = state;
        VoiceStateChanged?.Invoke(state);
    }

    private async Task ProcessSpeechQueueAsync()
    {
        try
        {
            await foreach (var item in _speechQueue.Reader.ReadAllAsync(_speechShutdown.Token).ConfigureAwait(false))
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    FinishSpeechItem();
                    continue;
                }
                try
                {
                    await _ttsService.SpeakAsync(item.Text,
                        new TtsSpeechOptions(_settings.TtsVoice, _settings.TtsSpeed, _settings.TtsVolume),
                        item.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (item.CancellationToken.IsCancellationRequested) { }
                catch (Exception ex) { AudioError?.Invoke(ex); }
                finally { FinishSpeechItem(); }
            }
        }
        catch (OperationCanceledException) when (_speechShutdown.IsCancellationRequested) { }
    }

    private void FinishSpeechItem()
    {
        var completed = false;
        lock (_audioLock)
        {
            if (_queuedSpeechCount > 0) _queuedSpeechCount--;
            completed = CompleteSpeechIfReady();
        }
        if (completed) SetVoiceState(VoiceInteractionState.Idle);
        if (completed) SpeechTurnCompleted?.Invoke();
    }

    private async Task StopTtsSafelyAsync()
    {
        try { await _ttsService.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { AudioError?.Invoke(ex); }
    }

    public string TtsEngineName => _ttsService.Name;
    public TimeSpan LastTtsStartupLatency => _ttsService.LastStartupLatency;
    public TimeSpan LastTtsFirstAudioLatency => _ttsService.LastFirstAudioLatency;

    public static IReadOnlyList<string> GetInstalledWindowsVoices()
    {
        using var synthesizer = new SpeechSynthesizer();
        return synthesizer.GetInstalledVoices().Select(voice => voice.VoiceInfo.Name).ToArray();
    }

    private void SetSpeaking(bool value)
    {
        _isSpeaking = value;
        if (value) SetVoiceState(VoiceInteractionState.Speaking);
        else if (_voiceState == VoiceInteractionState.Speaking) SetVoiceState(VoiceInteractionState.Idle);
        SpeechStateChanged?.Invoke(value);
    }

    private void ResetUtterance()
    {
        _speechDetected = false;
        _consecutiveSpeechFrames = 0;
        _currentUtterance.SetLength(0);
        _preRoll.Clear();
    }

    private Task PrepareRecognizerAsync(CancellationToken cancellationToken)
    {
        Task preparation;
        lock (_modelLock)
        {
            if (_prepareTask is { IsFaulted: true } or { IsCanceled: true }) _prepareTask = null;
            _prepareTask ??= PrepareRecognizerCoreAsync(_recognizerShutdown.Token);
            preparation = _prepareTask;
        }
        return preparation.WaitAsync(cancellationToken);
    }

    private async Task WarmRecognizerAsync()
    {
        try { await PrepareRecognizerAsync(_recognizerShutdown.Token); }
        catch (Exception ex) { AudioError?.Invoke(ex); }
    }

    private async Task PrepareRecognizerCoreAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAssistant", "Models");
        Directory.CreateDirectory(directory);
        var modelPath = Path.Combine(directory, "ggml-tiny.en.bin");
        if (!File.Exists(modelPath))
        {
            var partial = modelPath + ".download";
            using var stream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.TinyEn, cancellationToken: cancellationToken);
            await using (var output = File.Create(partial)) await stream.CopyToAsync(output, cancellationToken);
            File.Move(partial, modelPath, overwrite: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var recognizer = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var factory = WhisperFactory.FromPath(modelPath);
            try
            {
                var processor = factory.CreateBuilder()
                    .WithLanguage("en")
                    .WithThreads(_settings.WhisperThreads)
                    .WithNoSpeechThreshold(0.4f)
                    .WithLogProbThreshold(-0.75f)
                    .Build();
                return (factory, processor);
            }
            catch { factory.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
        _factory = recognizer.factory;
        _processor = recognizer.processor;
    }

    public void Dispose()
    {
        _recognizerShutdown.Cancel();
        lock (_audioLock)
        {
            _listening = false;
            _pushToTalkActive = false;
            if (_inputStarted) _input.StopRecording();
            _inputStarted = false;
        }
        StopSpeaking();
        _speechQueue.Writer.TryComplete();
        _speechShutdown.Cancel();
        _input.Dispose();
        _currentUtterance.Dispose();
        _pushToTalkAudio.Dispose();
        _ = DisposeSpeechOutputAsync();
    }

    private async Task DisposeSpeechOutputAsync()
    {
        try { await _speechPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        catch (OperationCanceledException) { }
        Task? recognizerPreparation;
        lock (_modelLock) recognizerPreparation = _prepareTask;
        if (recognizerPreparation is not null)
        {
            try { await recognizerPreparation.ConfigureAwait(false); }
            catch (Exception) { }
        }
        await _recognitionGate.WaitAsync().ConfigureAwait(false);
        lock (_modelLock)
        {
            _processor?.Dispose();
            _factory?.Dispose();
            _processor = null;
            _factory = null;
        }
        _recognitionGate.Release();
        _recognitionGate.Dispose();
        _ttsService.Dispose();
        _recognizerShutdown.Dispose();
        _speechTurnCancellation.Dispose();
        _speechShutdown.Dispose();
    }
}
