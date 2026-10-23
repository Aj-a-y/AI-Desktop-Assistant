using System.Speech.Synthesis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace DesktopAssistant;

internal sealed class TtsSettingsWindow : Window
{
    private static readonly string[] KokoroVoices =
    [
        "af_heart", "af_bella", "af_nicole", "af_sarah", "af_sky",
        "am_adam", "am_michael", "bf_emma", "bf_isabella", "bm_george", "bm_lewis"
    ];

    private readonly OllamaRuntimeSettings _settings;
    private readonly ComboBox _engine = new();
    private readonly ComboBox _voice = new();
    private readonly ComboBox _microphone = new();
    private readonly ComboBox _contextSize = new();
    private readonly ComboBox _generationLimit = new();
    private readonly ComboBox _keepAlive = new();
    private readonly ComboBox _whisperThreads = new();
    private readonly Slider _speed = new() { Minimum = 0.8, Maximum = 1.2, TickFrequency = 0.05, IsSnapToTickEnabled = false };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true };
    private readonly Slider _silence = new() { Minimum = 400, Maximum = 2500, TickFrequency = 100, IsSnapToTickEnabled = true };
    private readonly TextBlock _speedValue = new();
    private readonly TextBlock _volumeValue = new();
    private readonly TextBlock _silenceValue = new();

    public TtsSettingsWindow(OllamaRuntimeSettings settings)
    {
        _settings = settings;
        Title = "Voice settings";
        Width = 420;
        Height = 760;
        MinHeight = 680;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = "Speech output",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        AddLabeledControl(root, "Engine", _engine);
        AddLabeledControl(root, "Voice", _voice);
        AddLabeledControl(root, "Microphone", _microphone);

        var speedPanel = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        speedPanel.Children.Add(_speedValue);
        DockPanel.SetDock(_speedValue, Dock.Right);
        AddLabeledControl(root, "Speed", speedPanel);
        root.Children.Add(_speed);

        var volumePanel = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        volumePanel.Children.Add(_volumeValue);
        DockPanel.SetDock(_volumeValue, Dock.Right);
        AddLabeledControl(root, "Volume", volumePanel);
        root.Children.Add(_volume);

        var silencePanel = new DockPanel { Margin = new Thickness(0, 8, 0, 4) };
        silencePanel.Children.Add(_silenceValue);
        DockPanel.SetDock(_silenceValue, Dock.Right);
        AddLabeledControl(root, "Pause before the assistant responds", silencePanel);
        root.Children.Add(_silence);

        root.Children.Add(new TextBlock { Text = "Model performance", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        AddLabeledControl(root, "Ollama context window (tokens)", _contextSize);
        AddLabeledControl(root, "Maximum generated tokens", _generationLimit);
        AddLabeledControl(root, "Keep model loaded", _keepAlive);
        AddLabeledControl(root, "Whisper CPU threads", _whisperThreads);
        root.Children.Add(new TextBlock
        {
            Text = "Larger context windows use more memory. A 2,048 token context and four Whisper threads are conservative starting points for this laptop. Whisper thread changes apply when its local recognizer is next initialized.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 7, 0, 10)
        });

        root.Children.Add(new TextBlock
        {
            Text = "Kokoro runs locally on the CPU when its Python runtime and model files are installed. Windows speech remains the automatic fallback.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 14, 0, 14)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", MinWidth = 82, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var save = new Button { Content = "Save", MinWidth = 82, IsDefault = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        save.Click += (_, _) => SaveSettings();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        root.Children.Add(buttons);
        Content = root;

        _engine.ItemsSource = new[]
        {
            new EngineChoice("Automatic (Kokoro if installed)", "auto"),
            new EngineChoice("Kokoro ONNX", "kokoro"),
            new EngineChoice("Windows system voice", "windows")
        };
        _engine.DisplayMemberPath = nameof(EngineChoice.Label);
        _engine.SelectedValuePath = nameof(EngineChoice.Value);
        _engine.SelectedValue = settings.TtsEngine;
        _engine.SelectionChanged += (_, _) => RefreshVoices();
        _microphone.ItemsSource = VoiceService.GetMicrophones();
        _microphone.DisplayMemberPath = nameof(MicrophoneChoice.Name);
        _microphone.SelectedValuePath = nameof(MicrophoneChoice.DeviceNumber);
        _microphone.SelectedValue = settings.MicrophoneDeviceNumber;
        ConfigureChoices(_contextSize, new[] { 1024, 2048, 3072, 4096, 6144, 8192 }, settings.NumCtx);
        ConfigureChoices(_generationLimit, new[] { 64, 96, 128, 192, 256, 384, 512 }, settings.NumPredict);
        ConfigureChoices(_keepAlive, new[] { "30s", "1m", "2m", "5m", "10m", "0" }, settings.KeepAlive);
        ConfigureChoices(_whisperThreads, new[] { 1, 2, 3, 4, 6, 8 }, settings.WhisperThreads);
        _speed.Value = Math.Clamp(settings.TtsSpeed, 0.8, 1.2);
        _volume.Value = Math.Clamp(settings.TtsVolume, 0, 100);
        _silence.Value = Math.Clamp(settings.VoiceSilenceMs, 400, 2500);
        _speed.ValueChanged += (_, _) => _speedValue.Text = $"{_speed.Value:0.00}×";
        _volume.ValueChanged += (_, _) => _volumeValue.Text = $"{_volume.Value:0}%";
        _silence.ValueChanged += (_, _) => _silenceValue.Text = $"{_silence.Value / 1000.0:0.0}s";
        _speedValue.Text = $"{_speed.Value:0.00}×";
        _volumeValue.Text = $"{_volume.Value:0}%";
        _silenceValue.Text = $"{_silence.Value / 1000.0:0.0}s";
        RefreshVoices();
    }

    private static void AddLabeledControl(Panel root, string label, UIElement control)
    {
        root.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 7, 0, 4) });
        root.Children.Add(control);
    }

    private static void ConfigureChoices<T>(ComboBox control, IEnumerable<T> values, T selected)
    {
        control.ItemsSource = values.ToArray();
        control.SelectedItem = control.Items.Cast<T>().FirstOrDefault(value => EqualityComparer<T>.Default.Equals(value, selected));
        if (control.SelectedIndex < 0) control.SelectedIndex = 0;
    }

    private void RefreshVoices()
    {
        var selectedVoice = _settings.TtsVoice;
        var voices = Equals(_engine.SelectedValue, "windows")
            ? GetWindowsVoices()
            : KokoroVoices;
        _voice.ItemsSource = voices;
        if (voices.Contains(selectedVoice, StringComparer.OrdinalIgnoreCase))
            _voice.SelectedItem = voices.First(voice => string.Equals(voice, selectedVoice, StringComparison.OrdinalIgnoreCase));
        else if (voices.Length > 0)
            _voice.SelectedIndex = 0;
    }

    private static string[] GetWindowsVoices()
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            return synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo.Name)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch { return ["Default Windows voice"]; }
    }

    private void SaveSettings()
    {
        _settings.TtsEngine = _engine.SelectedValue as string ?? "auto";
        _settings.TtsVoice = _voice.SelectedItem as string ?? "af_heart";
        _settings.TtsSpeed = _speed.Value;
        _settings.TtsVolume = (int)_volume.Value;
        _settings.MicrophoneDeviceNumber = _microphone.SelectedValue is int deviceNumber ? deviceNumber : -1;
        _settings.VoiceSilenceMs = (int)_silence.Value;
        _settings.NumCtx = SelectedInt(_contextSize, _settings.NumCtx);
        _settings.NumPredict = SelectedInt(_generationLimit, _settings.NumPredict);
        _settings.KeepAlive = _keepAlive.SelectedItem as string ?? "2m";
        _settings.WhisperThreads = Math.Clamp(SelectedInt(_whisperThreads, 4), 1, 8);
        _settings.ConversationTokenBudget = Math.Clamp(_settings.ConversationTokenBudget, 512, Math.Max(512, _settings.NumCtx - 128));
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private static int SelectedInt(ComboBox control, int fallback) => control.SelectedItem is int value ? value : fallback;

    private sealed record EngineChoice(string Label, string Value);
}
