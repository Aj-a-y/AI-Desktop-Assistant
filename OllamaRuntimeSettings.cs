using System.IO;
using System.Text.Json;

namespace DesktopAssistant;

internal sealed class OllamaRuntimeSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int NumCtx { get; set; } = 2048;
    public int NumPredict { get; set; } = 128;
    public string KeepAlive { get; set; } = "2m";
    public int RequestTimeoutSeconds { get; set; } = 180;
    public int UiUpdateIntervalMs { get; set; } = 100;
    public int ConversationTokenBudget { get; set; } = 1400;
    public int MaxToolCallIterations { get; set; } = 3;
    public int ToolTimeoutSeconds { get; set; } = 15;
    public List<string> ApprovedWebsiteHosts { get; set; } = ["google.com", "youtube.com", "wikipedia.org", "netflix.com"];
    public string TtsEngine { get; set; } = "auto";
    public string TtsVoice { get; set; } = "af_bella";
    public double TtsSpeed { get; set; } = 1.0;
    public int TtsVolume { get; set; } = 100;
    public int VoiceSilenceMs { get; set; } = 800;
    public int WhisperThreads { get; set; } = 4;
    public int MicrophoneDeviceNumber { get; set; } = -1;
    public string KokoroPythonPath { get; set; } = "python";
    public string KokoroModelPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAssistant", "Tts", "kokoro-v1.0.int8.onnx");
    public string KokoroVoicesPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAssistant", "Tts", "voices-v1.0.bin");
    public string SystemPrompt { get; set; } = ConversationContextBuilder.DefaultSystemPrompt;
    public bool EnableThinking { get; set; }

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopAssistant",
        "ollama-settings.json");

    public static OllamaRuntimeSettings Load()
    {
        OllamaRuntimeSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<OllamaRuntimeSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new();
        }

        settings.NumCtx = Math.Clamp(settings.NumCtx, 1024, 8192);
        settings.NumPredict = Math.Clamp(settings.NumPredict, 32, 512);
        settings.RequestTimeoutSeconds = Math.Clamp(settings.RequestTimeoutSeconds, 30, 600);
        settings.UiUpdateIntervalMs = Math.Clamp(settings.UiUpdateIntervalMs, 50, 500);
        settings.ConversationTokenBudget = Math.Clamp(settings.ConversationTokenBudget, 512, Math.Max(512, settings.NumCtx - 128));
        settings.MaxToolCallIterations = Math.Clamp(settings.MaxToolCallIterations, 1, 5);
        settings.ToolTimeoutSeconds = Math.Clamp(settings.ToolTimeoutSeconds, 3, 60);
        settings.ApprovedWebsiteHosts = (settings.ApprovedWebsiteHosts ?? [])
            .Where(host => !string.IsNullOrWhiteSpace(host)
                && host.Length <= 253
                && host.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
            .Select(host => host.Trim().Trim('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        if (settings.TtsEngine is not ("auto" or "kokoro" or "windows")) settings.TtsEngine = "auto";
        if (string.IsNullOrWhiteSpace(settings.TtsVoice) || settings.TtsVoice.Length > 100) settings.TtsVoice = "af_bella";
        settings.TtsSpeed = double.IsFinite(settings.TtsSpeed) ? Math.Clamp(settings.TtsSpeed, 0.8, 1.2) : 1.0;
        settings.TtsVolume = Math.Clamp(settings.TtsVolume, 0, 100);
        settings.VoiceSilenceMs = Math.Clamp(settings.VoiceSilenceMs, 400, 2500);
        settings.WhisperThreads = Math.Clamp(settings.WhisperThreads, 1, 8);
        if (settings.MicrophoneDeviceNumber < -1) settings.MicrophoneDeviceNumber = -1;
        if (string.IsNullOrWhiteSpace(settings.KokoroPythonPath)) settings.KokoroPythonPath = "python";
        if (string.IsNullOrWhiteSpace(settings.KokoroModelPath)) settings.KokoroModelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAssistant", "Tts", "kokoro-v1.0.int8.onnx");
        if (string.IsNullOrWhiteSpace(settings.KokoroVoicesPath)) settings.KokoroVoicesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopAssistant", "Tts", "voices-v1.0.bin");
        if (string.IsNullOrWhiteSpace(settings.SystemPrompt)) settings.SystemPrompt = ConversationContextBuilder.DefaultSystemPrompt;
        if (settings.SystemPrompt.Length > 8000) settings.SystemPrompt = settings.SystemPrompt[..8000];
        if (!IsValidKeepAlive(settings.KeepAlive)) settings.KeepAlive = "2m";

        settings.Save();

        return settings;
    }

    private static bool IsValidKeepAlive(string? value)
    {
        if (string.Equals(value, "0", StringComparison.Ordinal)) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var suffix = value.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? "ms" : value[^1..];
        if (suffix is not ("ms" or "s" or "m" or "h")) return false;
        var number = value[..^suffix.Length];
        return int.TryParse(number, out var amount) && amount > 0;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings remain usable for this run if persistence is unavailable.
        }
    }
}
