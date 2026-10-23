using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DesktopAssistant;

internal sealed record OllamaReplyDiagnostics(
    TimeSpan FirstToken,
    TimeSpan Total,
    TimeSpan ModelLoad,
    int? GeneratedTokens,
    double? TokensPerSecond);

internal sealed class OllamaChatClient : IDisposable
{
    // Keep this exact identifier aligned with the model installed by the user.
    public const string ModelName = "qwen3.5:9b";
    private readonly HttpClient _httpClient = new()
    {
        BaseAddress = new Uri("http://localhost:11434/"),
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly OllamaRuntimeSettings _settings = OllamaRuntimeSettings.Load();

    public OllamaRuntimeSettings Settings => _settings;
    public OllamaReplyDiagnostics? LastDiagnostics { get; private set; }

    public async IAsyncEnumerable<string> StreamReplyAsync(
        IReadOnlyCollection<OllamaChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        IReadOnlyCollection<object>? tools = null,
        ICollection<OllamaToolCall>? toolCalls = null)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        LastDiagnostics = null;
        var firstToken = TimeSpan.Zero;
        var modelLoad = TimeSpan.Zero;
        int? generatedTokens = null;
        long? evaluationNanoseconds = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));

        try
        {
            var requestBody = new Dictionary<string, object?>
            {
                ["model"] = ModelName,
                ["messages"] = messages,
                ["stream"] = true,
                ["keep_alive"] = _settings.KeepAlive,
                ["think"] = _settings.EnableThinking,
                ["options"] = new
                {
                    num_ctx = _settings.NumCtx,
                    num_predict = _settings.NumPredict
                }
            };
            if (tools is { Count: > 0 }) requestBody["tools"] = tools;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
            {
                Content = JsonContent.Create(requestBody)
            };

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                throw CreateHttpError(response.StatusCode, errorBody);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Ollama returned a malformed streaming response. Retry the request; if it repeats, restart Ollama.", ex);
                }

                using (document)
                {
                    var root = document.RootElement;
                    if (root.TryGetProperty("error", out var error))
                        throw CreateOllamaError(error.GetString() ?? "Ollama returned an unspecified error.");

                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content))
                    {
                        var fragment = content.GetString();
                        if (!string.IsNullOrEmpty(fragment))
                        {
                            if (firstToken == TimeSpan.Zero) firstToken = timer.Elapsed;
                            yield return fragment;
                        }
                    }

                    if (toolCalls is not null && root.TryGetProperty("message", out var toolMessage)
                        && toolMessage.TryGetProperty("tool_calls", out var calls)
                        && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray())
                        {
                            if (!call.TryGetProperty("function", out var function)
                                || !function.TryGetProperty("name", out var nameElement)
                                || nameElement.ValueKind != JsonValueKind.String
                                || !function.TryGetProperty("arguments", out var arguments)
                                || arguments.ValueKind != JsonValueKind.Object)
                                throw new InvalidDataException("Ollama returned a malformed function call. No tool was executed.");

                            var parsedCall = new OllamaToolCall(
                                call.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                                    ? typeElement.GetString() ?? "function"
                                    : "function",
                                new OllamaFunctionCall(nameElement.GetString()!, arguments.Clone()));
                            var signature = $"{parsedCall.Function.Name}:{JsonSerializer.Serialize(parsedCall.Function.Arguments)}";
                            if (!toolCalls.Any(existing => $"{existing.Function.Name}:{JsonSerializer.Serialize(existing.Function.Arguments)}" == signature))
                                toolCalls.Add(parsedCall);
                        }
                    }

                    if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    {
                        modelLoad = ReadDuration(root, "load_duration");
                        generatedTokens = ReadInt(root, "eval_count");
                        evaluationNanoseconds = ReadLong(root, "eval_duration");
                    }
                }
            }

            timer.Stop();
            double? throughput = generatedTokens is > 0 && evaluationNanoseconds is > 0
                ? generatedTokens.Value / (evaluationNanoseconds.Value / 1_000_000_000d)
                : null;
            LastDiagnostics = new OllamaReplyDiagnostics(firstToken, timer.Elapsed, modelLoad, generatedTokens, throughput);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task UnloadModelAsync(CancellationToken cancellationToken = default)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds));
            using var psResponse = await _httpClient.GetAsync("api/ps", timeout.Token).ConfigureAwait(false);
            if (!psResponse.IsSuccessStatusCode)
            {
                var psBody = await psResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                throw CreateHttpError(psResponse.StatusCode, psBody);
            }

            using var processDocument = await JsonDocument.ParseAsync(
                await psResponse.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false),
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var isLoaded = processDocument.RootElement.TryGetProperty("models", out var loadedModels)
                && loadedModels.EnumerateArray().Any(model =>
                    model.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), ModelName, StringComparison.OrdinalIgnoreCase));
            if (!isLoaded) return;

            using var response = await _httpClient.PostAsJsonAsync("api/generate", new
            {
                model = ModelName,
                prompt = string.Empty,
                stream = false,
                keep_alive = 0
            }, timeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                throw CreateHttpError(response.StatusCode, body);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Ollama did not confirm that the model was unloaded before the request timeout.");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static Exception CreateHttpError(System.Net.HttpStatusCode statusCode, string body)
    {
        if ((int)statusCode == 404 && body.Contains("model", StringComparison.OrdinalIgnoreCase))
            return new InvalidOperationException($"Ollama does not have the configured model '{ModelName}'. Install that exact model with `ollama pull {ModelName}`. Details: {body}");
        return CreateOllamaError($"Ollama returned {(int)statusCode} ({statusCode}). {body}");
    }

    private static Exception CreateOllamaError(string message)
    {
        if (ContainsAny(message, "out of memory", "cuda out of memory", "failed to allocate", "allocation failed", "not enough memory"))
            return new InvalidOperationException($"Ollama ran out of available memory while loading {ModelName}. Close memory-heavy apps or lower num_ctx in {OllamaRuntimeSettings.SettingsPath}. Details: {message}");
        if (message.Contains("model", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(message, "not found", "does not exist", "pull"))
            return new InvalidOperationException($"The configured Ollama model '{ModelName}' is unavailable. Install that exact identifier with `ollama pull {ModelName}`. Details: {message}");
        return new InvalidOperationException(message);
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static TimeSpan ReadDuration(JsonElement root, string property) =>
        ReadLong(root, property) is { } nanoseconds && nanoseconds >= 0
            ? TimeSpan.FromMilliseconds(nanoseconds / 1_000_000d)
            : TimeSpan.Zero;

    private static int? ReadInt(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? ReadLong(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : null;

    public void Dispose()
    {
        _httpClient.Dispose();
        _requestGate.Dispose();
    }
}
