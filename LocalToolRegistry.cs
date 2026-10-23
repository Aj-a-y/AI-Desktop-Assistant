using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;

namespace DesktopAssistant;

internal sealed record ToolExecutionResult(bool Success, object? Data, string? Error)
{
    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

internal sealed record ToolExecutionContext(
    ComputerModeService ComputerModeService,
    OllamaRuntimeSettings Settings,
    Func<string, bool> ConfirmAction);

internal sealed class LocalToolRegistry
{
    private readonly Dictionary<string, LocalTool> _tools = new(StringComparer.Ordinal);

    public LocalToolRegistry()
    {
        Register(new LocalTool(
            "open_application", "Open an installed Windows application by its installed display name.",
            Schema(("app_name", "string", "Installed application name", null, null)),
            ["app_name"], true,
            (args, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                var name = ReadString(args, "app_name", 80);
                var result = context.ComputerModeService.OpenApplicationByName(name, token);
                return Task.FromResult<object>(new { message = result });
            }));

        Register(new LocalTool(
            "get_current_datetime", "Get the current local date, time, and time zone.",
            EmptySchema(), [], false,
            (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                var now = DateTimeOffset.Now;
                return Task.FromResult<object>(new
                {
                    local = now.ToString("O", CultureInfo.InvariantCulture),
                    date = now.ToString("D", CultureInfo.CurrentCulture),
                    time = now.ToString("T", CultureInfo.CurrentCulture),
                    timeZone = TimeZoneInfo.Local.DisplayName
                });
            }));

        Register(new LocalTool(
            "get_system_information", "Get basic Windows, device architecture, processor count, and memory information.",
            EmptySchema(), [], false,
            (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                var hasMemory = GlobalMemoryStatusEx(ref memory);
                var osVersion = Environment.OSVersion.Version;
                return Task.FromResult<object>(new
                {
                    operatingSystem = osVersion.Build >= 22000 ? $"Windows 11 ({osVersion})" : Environment.OSVersion.VersionString,
                    architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
                    processorCount = Environment.ProcessorCount,
                    machineName = Environment.MachineName,
                    totalMemoryGiB = hasMemory ? Math.Round(memory.TotalPhysical / 1073741824d, 1) : (double?)null,
                    availableMemoryGiB = hasMemory ? Math.Round(memory.AvailablePhysical / 1073741824d, 1) : (double?)null
                });
            }));

        Register(new LocalTool(
            "get_system_volume", "Get the current Windows master output volume as a percentage and mute state.",
            EmptySchema(), [], false,
            (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                using var endpoint = GetDefaultAudioEndpoint();
                return Task.FromResult<object>(new
                {
                    percent = (int)Math.Round(endpoint.AudioEndpointVolume.MasterVolumeLevelScalar * 100),
                    muted = endpoint.AudioEndpointVolume.Mute
                });
            }));

        Register(new LocalTool(
            "set_system_volume", "Set Windows master output volume to an absolute percentage.",
            Schema(("percent", "integer", "Volume percentage from 0 to 100", 0, 100)),
            ["percent"], false,
            (args, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                var percent = ReadInt(args, "percent", 0, 100);
                using var endpoint = GetDefaultAudioEndpoint();
                endpoint.AudioEndpointVolume.Mute = false;
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
                return Task.FromResult<object>(new { percent, muted = false });
            }));

        Register(new LocalTool(
            "adjust_system_volume", "Raise or lower Windows master output volume by a small relative percentage.",
            Schema(("delta_percent", "integer", "Relative change from -25 to 25 percentage points", -25, 25)),
            ["delta_percent"], false,
            (args, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                var delta = ReadInt(args, "delta_percent", -25, 25);
                using var endpoint = GetDefaultAudioEndpoint();
                var current = endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
                var target = Math.Clamp(current + delta / 100f, 0f, 1f);
                endpoint.AudioEndpointVolume.Mute = false;
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar = target;
                return Task.FromResult<object>(new { percent = (int)Math.Round(target * 100), muted = false });
            }));

        Register(new LocalTool(
            "open_website", "Open an approved HTTPS website in the default browser. The host must be in the approved website list in settings.",
            Schema(("url", "string", "An HTTPS URL on an approved website host", null, null)),
            ["url"], true,
            (args, context, token) =>
            {
                token.ThrowIfCancellationRequested();
                var url = ReadString(args, "url", 2048);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps
                    || !string.IsNullOrEmpty(uri.UserInfo)
                    || uri.Port != 443)
                    throw new ArgumentException("Only HTTPS website addresses on the standard secure port are allowed.");
                var approvedHosts = context.Settings.ApprovedWebsiteHosts;
                if (!approvedHosts.Any(host => HostMatches(uri.Host, host)))
                    throw new ArgumentException($"The website host '{uri.Host}' is not approved. Add it to ApprovedWebsiteHosts in {OllamaRuntimeSettings.SettingsPath} first.");
                token.ThrowIfCancellationRequested();
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return Task.FromResult<object>(new { message = "Opened the approved website.", url = uri.AbsoluteUri });
            }));
    }

    public IReadOnlyCollection<object> Definitions => _tools.Values
        .Select(tool => (object)new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters } })
        .ToArray();

    public bool Contains(string name) => _tools.ContainsKey(name);

    public void Register(LocalTool tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 64
            || tool.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Tool names must use only ASCII letters, digits, or underscores.", nameof(tool));
        if (!_tools.TryAdd(tool.Name, tool)) throw new InvalidOperationException($"A tool named '{tool.Name}' is already registered.");
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        string name, JsonElement arguments, ToolExecutionContext context, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
            return new(false, null, $"Unsupported tool '{name}'. No action was taken.");
        try
        {
            ValidateArguments(tool, arguments);
            cancellationToken.ThrowIfCancellationRequested();
            if (tool.RequiresConfirmation
                && !context.ConfirmAction($"Allow the assistant to {tool.Name.Replace('_', ' ')}?\n\nRequested arguments:\n{JsonSerializer.Serialize(arguments, new JsonSerializerOptions(JsonSerializerDefaults.Web))}"))
                return new(false, null, "The user declined confirmation. No action was taken.");
            using var toolTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            toolTimeout.CancelAfter(TimeSpan.FromSeconds(context.Settings.ToolTimeoutSeconds));
            var data = await Task.Run(() => tool.Handler(arguments, context, toolTimeout.Token), toolTimeout.Token);
            return new(true, data, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(false, null, $"Tool '{name}' timed out after {context.Settings.ToolTimeoutSeconds} seconds. Its action did not complete.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
        {
            return new(false, null, ex.Message);
        }
    }

    internal static void ValidateArguments(LocalTool tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.");
        foreach (var required in tool.Required)
            if (!arguments.TryGetProperty(required, out _)) throw new ArgumentException($"Missing required argument '{required}'.");
        var schemas = tool.Parameters["properties"]?.AsObject() ?? new JsonObject();
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seenProperties.Add(property.Name)) throw new ArgumentException($"Argument '{property.Name}' was supplied more than once.");
            if (!schemas.TryGetPropertyValue(property.Name, out var schema) || schema is not JsonObject definition)
                throw new ArgumentException($"Unsupported argument '{property.Name}'.");
            var type = definition["type"]?.GetValue<string>();
            if (type == "string")
            {
                if (property.Value.ValueKind != JsonValueKind.String) throw new ArgumentException($"Argument '{property.Name}' must be text.");
                var value = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 2048) throw new ArgumentException($"Argument '{property.Name}' is empty or too long.");
            }
            else if (type == "integer")
            {
                if (!property.Value.TryGetInt32(out var number)) throw new ArgumentException($"Argument '{property.Name}' must be an integer.");
                var minimum = definition["minimum"]!.GetValue<int>();
                var maximum = definition["maximum"]!.GetValue<int>();
                if (number < minimum || number > maximum) throw new ArgumentOutOfRangeException(property.Name, $"Argument '{property.Name}' must be from {minimum} to {maximum}.");
            }
            else
            {
                throw new InvalidOperationException($"Tool argument schema type '{type}' is not supported by the local validator.");
            }
        }
    }

    private static string ReadString(JsonElement args, string name, int maxLength)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Argument '{name}' must be text.");
        var result = value.GetString()?.Trim() ?? "";
        if (result.Length == 0 || result.Length > maxLength) throw new ArgumentException($"Argument '{name}' must contain 1 to {maxLength} characters.");
        return result;
    }

    private static int ReadInt(JsonElement args, string name, int min, int max)
    {
        if (!args.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result) || result < min || result > max)
            throw new ArgumentOutOfRangeException(name, $"Argument '{name}' must be an integer from {min} to {max}.");
        return result;
    }

    private static bool HostMatches(string requestedHost, string approvedHost)
    {
        var host = approvedHost.Trim().TrimEnd('.');
        return host.Length > 0 && (requestedHost.Equals(host, StringComparison.OrdinalIgnoreCase)
            || requestedHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    private static MMDevice GetDefaultAudioEndpoint()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static JsonObject EmptySchema() => new() { ["type"] = "object", ["properties"] = new JsonObject(), ["additionalProperties"] = false };

    private static JsonObject Schema((string Name, string Type, string Description, int? Minimum, int? Maximum) property)
    {
        var definition = new JsonObject { ["type"] = property.Type, ["description"] = property.Description };
        if (property.Minimum is { } minimum) definition["minimum"] = minimum;
        if (property.Maximum is { } maximum) definition["maximum"] = maximum;
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [property.Name] = definition },
            ["required"] = new JsonArray(property.Name),
            ["additionalProperties"] = false
        };
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
}

internal sealed record LocalTool(
    string Name,
    string Description,
    JsonObject Parameters,
    IReadOnlyList<string> Required,
    bool RequiresConfirmation,
    Func<JsonElement, ToolExecutionContext, CancellationToken, Task<object>> Handler);
