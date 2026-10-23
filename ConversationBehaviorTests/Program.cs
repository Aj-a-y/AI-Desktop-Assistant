using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http;
using System.IO;
using DesktopAssistant;

var cases = new (string Name, Action Test)[]
{
    ("casual questions use a natural, concise style", CasualQuestionPolicy),
    ("follow-up questions retain earlier turns", FollowUpContext),
    ("corrections remain in chronological context", CorrectionContext),
    ("ambiguous instructions ask for clarification", AmbiguousInstructionPolicy),
    ("tool requests use assistant-call and tool-result roles", ToolMessageOrder),
    ("history stays within its configured token estimate", BoundedHistory),
    ("registered tool validates and executes a valid command", ValidRegisteredTool),
    ("invalid tool arguments are rejected before execution", InvalidToolArguments),
    ("unsupported tools return a structured error", UnsupportedTool),
    ("ambiguous targets are rejected and left for clarification", AmbiguousTarget),
    ("speech chunks remove Markdown and wait for complete sentences", SpeechChunksAreSafe),
    ("time-sensitive questions are sent to web search", CurrentQuestionsUseWebSearch),
    ("web search caches bounded public source excerpts", WebSearchIsCachedAndBounded),
    ("web search rejects an oversized provider response", WebSearchRejectsOversizedResponse),
    ("web search honors caller cancellation", WebSearchHonorsCancellation),
    ("local SQLite persistence reloads ordered conversation messages", MemoryPersistence),
    ("saved preferences retrieve relevant items and support deletion", PreferenceMemory),
    ("conversation memory deletion and bounded loading work", MemoryDeletionAndBounds),
    ("sensitive content is rejected from saved preferences", MemoryPrivacy),
    ("runtime defaults are conservative for limited hardware", ConservativeRuntimeDefaults),
    ("exact installed app name outranks a longer prefix match", ExactAppNameWins),
    ("WhatsApp composer fallback stays inside the chat input area", WhatsAppComposerFallbackBounds),
    ("complete WhatsApp commands retain recipient and message", WhatsAppCommandParsing),
    ("WhatsApp contact matching ignores emoji but rejects partial names", WhatsAppContactMatching),
    ("WhatsApp reuses only a visibly matching active chat header", WhatsAppActiveChatHeaderMatching),
    ("Gwen greetings and chat clear/restore commands are recognized", GwenAndChatViewCommands),
    ("WhatsApp reports sent only when chat text appears and composer clears", WhatsAppSendVerification)
};

var failures = 0;
foreach (var testCase in cases)
{
    try
    {
        testCase.Test();
        Console.WriteLine($"PASS {testCase.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {testCase.Name}: {ex.Message}");
    }
}

Console.WriteLine($"{cases.Length - failures}/{cases.Length} conversation behavior checks passed.");
return failures == 0 ? 0 : 1;

static void CasualQuestionPolicy()
{
    var prompt = ConversationContextBuilder.DefaultSystemPrompt;
    Assert(prompt.Contains("natural, friendly everyday language", StringComparison.Ordinal), "Prompt should ask for everyday speech.");
    Assert(prompt.Contains("one or two short spoken sentences", StringComparison.Ordinal), "Prompt should keep simple replies brief.");
    Assert(prompt.Contains("repetitive greetings", StringComparison.Ordinal), "Prompt should discourage repeated greetings.");
}

static void FollowUpContext()
{
    var messages = new[]
    {
        new OllamaChatMessage("user", "I'm planning a weekend in Galway."),
        new OllamaChatMessage("assistant", "Sounds good. What kind of activities do you like?"),
        new OllamaChatMessage("user", "Mostly museums and quiet cafés."),
        new OllamaChatMessage("assistant", "I'll keep the plan focused on museums and quiet cafés."),
        new OllamaChatMessage("user", "Can you suggest two?"),
    };
    var context = ConversationContextBuilder.Build(ConversationContextBuilder.DefaultSystemPrompt, messages, 1400).ToArray();
    Assert(context.Any(message => message.Content.Contains("weekend in Galway", StringComparison.Ordinal)), "The first turn should remain available to interpret the follow-up.");
    Assert(context[^1].Content == "Can you suggest two?", "The newest follow-up should be last.");
}

static void CorrectionContext()
{
    var messages = new[]
    {
        new OllamaChatMessage("user", "Call me Alex."),
        new OllamaChatMessage("assistant", "Got it."),
        new OllamaChatMessage("user", "Correction: my name is Sam."),
        new OllamaChatMessage("assistant", "Thanks for correcting me, Sam."),
        new OllamaChatMessage("user", "What name should you use?"),
    };
    var context = ConversationContextBuilder.Build(ConversationContextBuilder.DefaultSystemPrompt, messages, 1400).ToArray();
    var corrections = context.Where(message => message.Role == "user").Select(message => message.Content).ToArray();
    Assert(corrections[^2].Contains("Correction: my name is Sam", StringComparison.Ordinal), "The correction must be preserved before the follow-up.");
    Assert(context.Select(message => message.Role).SequenceEqual(new[] { "system", "user", "assistant", "user", "assistant", "user" }), "Turns must stay in original order.");
}

static void AmbiguousInstructionPolicy()
{
    Assert(ConversationContextBuilder.DefaultSystemPrompt.Contains("ask the smallest useful clarifying question before acting", StringComparison.Ordinal),
        "Ambiguous requests should be clarified before an action is taken.");
    Assert(ConversationContextBuilder.DefaultSystemPrompt.Contains("Never claim an action is complete unless its tool result confirms it", StringComparison.Ordinal),
        "The assistant should not invent completed actions.");
}

static void ToolMessageOrder()
{
    var messages = new[]
    {
        new OllamaChatMessage("user", "Open Chrome."),
        ConversationContextBuilder.CreateToolCall("open_application", new { appName = "Chrome" }),
        new OllamaChatMessage("tool", "Opened Google Chrome.", ToolName: "open_application"),
        new OllamaChatMessage("assistant", "Chrome is open.")
    };
    var context = ConversationContextBuilder.Build(ConversationContextBuilder.DefaultSystemPrompt, messages, 1400).ToArray();
    Assert(context.Select(message => message.Role).SequenceEqual(new[] { "system", "user", "assistant", "tool", "assistant" }),
        "Tool call and tool result roles should occur between user input and assistant response.");
    Assert(context[3].ToolName == "open_application", "Tool result should identify its tool.");
    var json = JsonSerializer.Serialize(context, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    Assert(json.Contains("\"tool_calls\"", StringComparison.Ordinal), "Assistant tool-call metadata should serialize using Ollama's API field name.");
    Assert(json.Contains("\"tool_name\":\"open_application\"", StringComparison.Ordinal), "Tool result should serialize its tool name.");
}

static void BoundedHistory()
{
    var messages = Enumerable.Range(0, 12)
        .SelectMany(index => new[]
        {
            new OllamaChatMessage("user", $"Turn {index}: {new string('u', 120)}"),
            new OllamaChatMessage("assistant", $"Answer {index}: {new string('a', 120)}")
        }).ToArray();
    const int budget = 512;
    var context = ConversationContextBuilder.Build(ConversationContextBuilder.DefaultSystemPrompt, messages, budget).ToArray();
    Assert(context.Sum(ConversationContextBuilder.EstimateTokens) <= budget, "Estimated prompt tokens must not exceed the configured budget.");
    Assert(context[^1].Content.StartsWith("Answer 11:", StringComparison.Ordinal), "The newest assistant response should be retained.");
    Assert(!context.Any(message => message.Content.StartsWith("Turn 0:", StringComparison.Ordinal)), "Old turns should be dropped first.");
}

static void ValidRegisteredTool()
{
    var registry = new LocalToolRegistry();
    var executed = false;
    registry.Register(new LocalTool(
        "test_echo", "Return a validated test value.",
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["value"] = new JsonObject { ["type"] = "string", ["description"] = "Value to echo" }
            },
            ["required"] = new JsonArray(JsonValue.Create("value")),
            ["additionalProperties"] = false
        },
        ["value"], false,
        (args, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            executed = true;
            return Task.FromResult<object>(new { value = args.GetProperty("value").GetString() });
        }));
    var result = registry.ExecuteAsync(
        "test_echo", JsonDocument.Parse("{\"value\":\"hello\"}").RootElement,
        TestExecutionContext(), CancellationToken.None).GetAwaiter().GetResult();
    Assert(executed && result.Success, "The valid registered handler should execute successfully.");
    Assert(result.ToJson().Contains("hello", StringComparison.Ordinal), "The tool result should be serialized as structured JSON.");
    var definitions = JsonSerializer.Serialize(registry.Definitions);
    Assert(definitions.Contains("test_echo", StringComparison.Ordinal) && definitions.Contains("parameters", StringComparison.Ordinal),
        "Registered tools should expose a name and parameter schema to Ollama.");
}

static void InvalidToolArguments()
{
    var registry = new LocalToolRegistry();
    var invalidRange = registry.ExecuteAsync(
        "set_system_volume", JsonDocument.Parse("{\"percent\":101}").RootElement,
        TestExecutionContext(), CancellationToken.None).GetAwaiter().GetResult();
    Assert(!invalidRange.Success && invalidRange.Error?.Contains("100", StringComparison.Ordinal) == true,
        "Out-of-range volume values must be rejected before touching system volume.");

    var extraArgument = registry.ExecuteAsync(
        "get_current_datetime", JsonDocument.Parse("{\"command\":\"whoami\"}").RootElement,
        TestExecutionContext(), CancellationToken.None).GetAwaiter().GetResult();
    Assert(!extraArgument.Success, "Unexpected arguments must be rejected.");
}

static void UnsupportedTool()
{
    var result = new LocalToolRegistry().ExecuteAsync(
        "run_powershell", JsonDocument.Parse("{}").RootElement,
        TestExecutionContext(), CancellationToken.None).GetAwaiter().GetResult();
    Assert(!result.Success && result.Error?.Contains("Unsupported tool", StringComparison.Ordinal) == true,
        "Unknown model-provided tool names must return an error without execution.");
}

static void AmbiguousTarget()
{
    var confirmations = 0;
    var context = new ToolExecutionContext(new ComputerModeService(), new OllamaRuntimeSettings(), _ =>
    {
        confirmations++;
        return true;
    });
    var result = new LocalToolRegistry().ExecuteAsync(
        "open_application", JsonDocument.Parse("{}").RootElement,
        context, CancellationToken.None).GetAwaiter().GetResult();
    Assert(!result.Success && result.Error?.Contains("app_name", StringComparison.Ordinal) == true,
        "When no application target is specified, the action must be rejected so the assistant can ask which app.");
    Assert(confirmations == 0, "An ambiguous action must not prompt or run before a target is supplied.");
}

static ToolExecutionContext TestExecutionContext() =>
    new(new ComputerModeService(), new OllamaRuntimeSettings(), _ => true);

static void SpeechChunksAreSafe()
{
    var parser = new SpeechChunkParser();
    var first = parser.Append("# Hello **there**. [OpenAI](https://openai.com) is local. ");
    Assert(first.SequenceEqual(new[] { "Hello there.", "OpenAI is local." }),
        $"Markdown headings, emphasis, and link syntax should not reach speech. Got: {string.Join(" | ", first)}");

    var codeParser = new SpeechChunkParser();
    var codeChunks = codeParser.Append("```csharp\nConsole.WriteLine(\"do not speak\");\n```\nThis is spoken. ");
    var tail = codeParser.Complete();
    Assert(!string.Join(' ', codeChunks.Append(tail)).Contains("do not speak", StringComparison.Ordinal),
        "Fenced code blocks must be omitted from spoken text.");
    Assert(codeChunks.Append(tail).Contains("This is spoken."), "Readable prose after a code block should remain speakable.");

    var incompleteParser = new SpeechChunkParser();
    Assert(incompleteParser.Append("This sentence is complete.").Count == 0,
        "A punctuation mark at a token boundary should wait until the next character confirms the sentence boundary.");
    Assert(incompleteParser.Append(" Next sentence.").Single() == "This sentence is complete.",
        "Only the complete sentence should be queued during streaming.");
    Assert(incompleteParser.Complete() == "Next sentence.", "The final response tail should be flushed only at turn completion.");
}

static void CurrentQuestionsUseWebSearch()
{
    Assert(WebSearchService.TryGetSearchQuery("What is the latest news from France?", out var query),
        "A latest-news question should request current sources.");
    Assert(query.Contains("France", StringComparison.Ordinal), "The original subject should be used as the query.");
    Assert(WebSearchService.IsTimeSensitiveQuery(query), "The provider should use its current-information search mode.");
    Assert(!WebSearchService.TryGetSearchQuery("Tell me a joke", out _), "Ordinary conversation should not require internet access.");
}

static void WebSearchIsCachedAndBounded()
{
    var requests = 0;
    var responseBody = """
        {"results":[
          {"title":"Current event","url":"https://example.org/story","published_date":"2026-10-23","raw_content":"Useful excerpt."},
          {"title":"Invalid scheme","url":"file:///private","raw_content":"must be rejected"}
        ]}
        """;
    using var client = new HttpClient(new TestHttpHandler((_, _) =>
    {
        requests++;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody)
        });
    }));
    var service = new TavilyWebSearchService(client, () => "test-key");
    var first = service.SearchAsync("latest news", CancellationToken.None).GetAwaiter().GetResult();
    var second = service.SearchAsync(" LATEST   NEWS ", CancellationToken.None).GetAwaiter().GetResult();
    Assert(requests == 1, "Equivalent recent queries should use the bounded in-memory cache.");
    Assert(first.Count == 1 && second.Count == 1, "Only safe HTTP(S) sources should be returned.");
    Assert(first[0].Content == "Useful excerpt." && first[0].PublishedDate == "2026-10-23",
        "The source excerpt and provider-supplied date should be preserved.");
    using var toolResult = JsonDocument.Parse(WebSearchService.BuildSourceToolResult(first));
    Assert(toolResult.RootElement.GetProperty("evidence_policy").GetString()!.Contains("untrusted", StringComparison.Ordinal),
        "The model should receive an explicit untrusted-source policy.");
    Assert(WebSearchService.BuildSourcesFooter(first).Contains("https://example.org/story", StringComparison.Ordinal),
        "The UI footer should include a clickable public source link.");
}

static void WebSearchRejectsOversizedResponse()
{
    using var client = new HttpClient(new TestHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(new string('x', 1024 * 1024 + 1))
    })));
    var service = new TavilyWebSearchService(client, () => "test-key");
    try
    {
        _ = service.SearchAsync("test query", CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Oversized responses should be rejected.");
    }
    catch (InvalidDataException ex)
    {
        Assert(ex.Message.Contains("1 MB", StringComparison.Ordinal), "The failure should identify the response limit.");
    }
}

static void WebSearchHonorsCancellation()
{
    using var client = new HttpClient(new TestHttpHandler(async (_, cancellationToken) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }));
    var service = new TavilyWebSearchService(client, () => "test-key");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
    try
    {
        _ = service.SearchAsync("a cancellable query", cancellation.Token).GetAwaiter().GetResult();
        throw new InvalidOperationException("The request should observe caller cancellation.");
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
}

static void MemoryPersistence()
{
    WithMemoryStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var session = store.CreateSessionAsync().GetAwaiter().GetResult();
        store.AppendMessageAsync(session, "user", "I'm planning a trip to Galway.").GetAwaiter().GetResult();
        store.AppendMessageAsync(session, "assistant", "I can help with the Galway trip.").GetAwaiter().GetResult();
        var restored = store.GetMessagesAsync(session).GetAwaiter().GetResult();
        Assert(restored.Count == 2 && restored[0].Role == "user" && restored[1].Role == "assistant",
            "Messages should persist in chronological order with their roles and content.");
        Assert(restored[0].Content.Contains("Galway", StringComparison.Ordinal), "Message text should round-trip from SQLite.");
    });
}

static void PreferenceMemory()
{
    WithMemoryStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        store.SavePreferenceAsync("I prefer concise answers and vegetarian recipes.").GetAwaiter().GetResult();
        store.SavePreferenceAsync("I enjoy quiet museums.").GetAwaiter().GetResult();
        var relevant = store.GetRelevantPreferencesAsync("Suggest a vegetarian dinner", maxItems: 2).GetAwaiter().GetResult();
        Assert(relevant.Count == 1 && relevant[0].Content.Contains("vegetarian", StringComparison.Ordinal),
            "Only preferences sharing useful terms with the request should be retrieved.");
        Assert(store.DeleteMatchingPreferenceAsync("vegetarian recipes").GetAwaiter().GetResult(), "A unique matching preference should be forgettable.");
        Assert(store.GetPreferencesAsync().GetAwaiter().GetResult().Count == 1, "Deleting a preference should leave unrelated preferences intact.");
    });
}

static void MemoryDeletionAndBounds()
{
    WithMemoryStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var session = store.CreateSessionAsync().GetAwaiter().GetResult();
        for (var i = 0; i < 8; i++) store.AppendMessageAsync(session, "user", $"Question {i}").GetAwaiter().GetResult();
        var latest = store.GetMessagesAsync(session, limit: 3).GetAwaiter().GetResult();
        Assert(latest.Count == 3 && latest[0].Content == "Question 5", "Recent conversation loading must have a hard limit.");
        store.DeleteSessionAsync(session).GetAwaiter().GetResult();
        Assert(store.GetSessionsAsync().GetAwaiter().GetResult().Count == 0, "Deleting a session should remove it and its messages.");
        var other = store.CreateSessionAsync().GetAwaiter().GetResult();
        store.ClearConversationsAsync().GetAwaiter().GetResult();
        Assert(store.GetMessagesAsync(other).GetAwaiter().GetResult().Count == 0, "Clear history should remove conversation data.");
    });
}

static void MemoryPrivacy()
{
    Assert(MemoryPrivacyFilter.IsSensitive("My password is abc123"), "Password-like content should be marked sensitive.");
    Assert(MemoryPrivacyFilter.IsSensitive("Email me at ajay@example.com"), "Email addresses should be marked sensitive.");
    Assert(!MemoryPrivacyFilter.IsSensitive("I prefer short answers"), "Ordinary preferences should remain saveable.");
    WithMemoryStore(store =>
    {
        store.InitializeAsync().GetAwaiter().GetResult();
        var rejected = false;
        try { store.SavePreferenceAsync("My password is abc123").GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected && store.GetPreferencesAsync().GetAwaiter().GetResult().Count == 0,
            "The persistence API itself must reject sensitive preference text.");
    });
}

static void ConservativeRuntimeDefaults()
{
    var settings = new OllamaRuntimeSettings();
    Assert(settings.NumCtx == 2048, "The default Ollama context should remain at 2,048 tokens.");
    Assert(settings.NumPredict == 128 && settings.KeepAlive == "2m", "Generation and idle retention should stay moderate by default.");
    Assert(settings.WhisperThreads == 4, "Whisper should leave CPU capacity available for Ollama and the UI.");
}

static void ExactAppNameWins()
{
    var spotify = new ComputerModeService.InstalledApp("Spotify",
        new System.Diagnostics.ProcessStartInfo { FileName = @"C:\Apps\Spotify\Spotify.exe" }, "spotify");
    var spotifyCli = new ComputerModeService.InstalledApp("Spotify CLI",
        new System.Diagnostics.ProcessStartInfo { FileName = @"C:\Tools\Spotify CLI\spotify.exe" }, "spotify-cli");
    var matches = ComputerModeService.FindMatchingInstalledApps("spotify", [spotify, spotifyCli]);
    Assert(matches.Count == 1 && matches[0].Name == "Spotify",
        "A unique exact Spotify display name should be selected even when Spotify CLI shares the prefix.");
    var explicitCli = ComputerModeService.FindMatchingInstalledApps("spotifycli", [spotify, spotifyCli]);
    Assert(explicitCli.Count == 1 && explicitCli[0].Name == "Spotify CLI",
        "The secondary app should still open when specifically requested by name.");
}

static void WhatsAppSendVerification()
{
    Assert(ComputerModeService.IsWhatsAppSendVerified(1, 2, true), "A new visible message and cleared composer confirm it appeared in the chat.");
    Assert(!ComputerModeService.IsWhatsAppSendVerified(1, 1, true), "A cleared composer alone must not be treated as a sent message.");
    Assert(!ComputerModeService.IsWhatsAppSendVerified(1, 2, false), "A visible matching message with uncleared composer is not enough to claim success.");
}

static void WhatsAppComposerFallbackBounds()
{
    var point = ComputerModeService.GetWhatsAppComposerClickPoint(787, 594);
    Assert(point.X == 550 && point.Y == 553, "A standard WhatsApp window should target the lower message input row.");

    var rejected = false;
    try { _ = ComputerModeService.GetWhatsAppComposerClickPoint(120, 90); }
    catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "A too-small WhatsApp window must be rejected instead of risking a click elsewhere.");
}

static void WhatsAppCommandParsing()
{
    Assert(MainWindow.TryParseWhatsAppMessage("Send a message to AJ on whatsapp saying How are you", out var recipient, out var message),
        "The reported command with 'on WhatsApp' after the contact should parse in one turn.");
    Assert(recipient == "AJ" && message == "How are you", "The reported command should preserve the requested recipient and message exactly.");

    Assert(MainWindow.TryParseWhatsAppMessage("Can you send a message on WhatsApp to AJ saying \"how are you?\"", out recipient, out message),
        "A command with WhatsApp before 'to AJ' should parse in one turn.");
    Assert(recipient == "AJ" && message == "how are you?", "The platform-first command should preserve recipient and punctuation.");

    Assert(MainWindow.TryParseWhatsAppMessage("send a whatsapp message to Anushka saying Baby <3", out recipient, out message),
        "A complete recipient-and-message command should parse in one turn.");
    Assert(recipient == "Anushka" && message == "Baby <3", "The parser should preserve the exact requested recipient and message.");

    Assert(MainWindow.TryParseWhatsAppMessage("Please send a WhatsApp message to Anushka ❤️ saying \"How are you?\"", out recipient, out message),
        "A polite complete WhatsApp command with an emoji contact should parse.");
    Assert(recipient == "Anushka ❤️" && message == "How are you?", "The parser should retain recipient emoji and message punctuation.");
}

static void WhatsAppContactMatching()
{
    Assert(ComputerModeService.ContactNamesMatchExactly("Anushka", "Anushka ❤️"), "Emoji should not prevent exact contact matching.");
    Assert(!ComputerModeService.ContactNamesMatchExactly("AJ", "Ajay"), "A partial name must not be accepted as an exact recipient.");
}

static void WhatsAppActiveChatHeaderMatching()
{
    Assert(ComputerModeService.IsActiveWhatsAppChatHeaderCandidate("AJ", "AJ", 550, 170, 100, 140, 1000),
        "A matching chat header in the conversation pane should allow reusing the open chat.");
    Assert(ComputerModeService.IsActiveWhatsAppChatHeaderCandidate("Anushka ❤️", "Anushka", 550, 170, 100, 140, 1000),
        "Emoji in a visible active chat header should not prevent a match.");
    Assert(!ComputerModeService.IsActiveWhatsAppChatHeaderCandidate("Ajay", "AJ", 550, 170, 100, 140, 1000),
        "A different active chat must not be reused.");
    Assert(!ComputerModeService.IsActiveWhatsAppChatHeaderCandidate("AJ", "AJ", 180, 170, 100, 140, 1000),
        "A matching contact in the left chat list must not count as the active chat header.");
}

static void GwenAndChatViewCommands()
{
    Assert(MainWindow.IsGwenGreeting("Hi!"), "A simple Hi greeting should trigger Gwen's named greeting.");
    Assert(MainWindow.IsGwenGreeting("hello Gwen"), "Gwen should recognize a greeting addressed to her.");
    Assert(!MainWindow.IsGwenGreeting("Hi, can you check the weather?"), "A greeting with a real request should continue to normal intent handling.");
    Assert(MainWindow.IsClearChatRequest("clear chat"), "The clear chat command should clear only the current screen view.");
    Assert(MainWindow.IsClearChatRequest("clear all the chats from screen"), "A request to clear visible chats should be recognized.");
    Assert(MainWindow.IsRestoreChatRequest("retrieve chat"), "The retrieve chat command should restore the current conversation.");
    Assert(MainWindow.IsRestoreChatRequest("show all the chats"), "A request to show chats should be recognized.");
    Assert(!MainWindow.IsClearChatRequest("delete all conversation history"), "Deleting saved history must remain a separate explicit action.");
}

static void WithMemoryStore(Action<ConversationMemoryStore> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "DesktopAssistant-memory-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(new ConversationMemoryStore(Path.Combine(directory, "memory.db"))); }
    finally { Directory.Delete(directory, recursive: true); }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        send(request, cancellationToken);
}
