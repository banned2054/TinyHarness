using System.Diagnostics;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

public class WorkerRunnerTests
{
    private static WorkerExecutionOptions Options(int maxSteps = 6, TimeSpan? runTimeout = null) => new()
    {
        Model                      = "fake-model",
        RunTimeout                 = runTimeout ?? TimeSpan.FromSeconds(30),
        MaxAgentSteps              = maxSteps,
        DefaultToolTimeoutSeconds  = 10,
        MaxTaskPackageCharacters   = 8_000,
        MaxToolCalls               = 12,
        MaxToolOutputCharacters    = 100_000,
        MaxCumulativeContextTokens = 200_000,
        MaxContextTokensPerRequest = 200_000,
        MaxModelResponseCharacters = 64_000
    };

    private static string DraftJson(string conclusion, string path) =>
        $$"""
        {"conclusion":"{{conclusion}}","evidence":[{"path":"{{path}}","lineStart":2,"lineEnd":3,"note":"States it."}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}
        """;

    /// <summary>
    ///     <para>
    ///         取单个请求里的 tool 消息内容；模型的最后一次请求携带完整最终历史，
    ///         因此单 run 测试传 <c>fake.RequestLog[^1]</c>，多 run 测试传各 run 的最后请求。
    ///     </para>
    ///     <para>
    ///         Takes the tool-message contents of one request. The model's last request carries the complete
    ///         final history, so single-run tests pass <c>fake.RequestLog[^1]</c> and multi-run tests pass
    ///         each run's final request.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ToolMessages(ChatCompletionRequest request)
    {
        return request.Messages
                      .Where(message => message.Role == ChatRole.Tool)
                      .Select(message => message.Content)
                      .ToArray();
    }

    private static IReadOnlyList<ChatStreamEvent> MultiToolCall(params (string Name, string Arguments)[] calls)
    {
        var events = new List<ChatStreamEvent>();
        for (var i = 0; i < calls.Length; i++)
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = i,
                ToolCallId             = $"call_{i}",
                ToolCallFunctionName   = calls[i].Name,
                ToolCallArgumentsDelta = calls[i].Arguments
            });

        events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" });
        return events;
    }

    [Test]
    public async Task ValidRequest_CompletesWithEvidenceMappingStatsAndReadonlyToolset()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/note.md", "line1\nline2\nline3\n");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/note.md"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("The retry budget is documented in docs/note.md.",
                                                   "docs/note.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "Where is the retry budget documented?",
            KnownFacts = ["docs/note.md exists"],
            FocusPaths = ["docs"]
        }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Conclusion, Is.EqualTo("The retry budget is documented in docs/note.md."));
        var evidence = TestAssert.Single(result.Evidence);
        Assert.That(evidence.Path, Is.EqualTo("docs/note.md"));
        Assert.That(evidence.LineStart, Is.EqualTo(2));
        Assert.That(evidence.LineEnd, Is.EqualTo(3));
        Assert.That(evidence.Note, Is.EqualTo("States it."));
        Assert.That(result.TestSuggestions, Is.Empty);
        Assert.That(result.Uncertainties, Is.Empty);
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(2));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(1));
        Assert.That(result.Statistics.ToolOutputCharacters, Is.EqualTo("line1\nline2\nline3".Length));
        Assert.That(result.Statistics.Elapsed >= TimeSpan.Zero, Is.True);

        var offered = fake.RequestLog[0].Tools!.Select(t => t.Name)
                          .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.That(offered, Is.EqualTo(new[] { "list_files", "read_file", "search_text" }));
        Assert.That(fake.LastRequestModel, Is.EqualTo("fake-model"));
    }

    [Test]
    public async Task TaskPackage_EntersUserMessageNotSystemInstructions()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/note.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "Where is the retry budget documented?",
            KnownFacts = ["docs/note.md exists"],
            FocusPaths = ["docs"]
        }, CancellationToken.None);

        var messages = fake.RequestLog[0].Messages;
        Assert.That(messages.Count, Is.EqualTo(2));
        Assert.That(messages[0].Role, Is.EqualTo(ChatRole.System));
        TestAssert.DoesNotContain("retry budget", messages[0].Content, StringComparison.Ordinal);
        Assert.That(messages[1].Role, Is.EqualTo(ChatRole.User));
        TestAssert.Contains("Where is the retry budget documented?", messages[1].Content, StringComparison.Ordinal);
        TestAssert.Contains("KNOWN FACTS", messages[1].Content, StringComparison.Ordinal);
        TestAssert.Contains("- docs", messages[1].Content, StringComparison.Ordinal);
    }

    [Test]
    public async Task InvalidRequest_FailsWithoutAnyModelRequest()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "   " }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Failed));
        Assert.That(fake.Requests, Is.EqualTo(0));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(0));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(0));
        Assert.That(string.IsNullOrEmpty(result.StatusDetail), Is.False);
        Assert.That(result.Evidence, Is.Empty);
    }

    [Test]
    public async Task ModelRequestsWriteAndProcessTools_RoundGatedWithoutSideEffects()
    {
        using var dir    = new TestTempDir();
        var       target = dir.WriteFile("src/a.cs", "original content");
        var       fake   = new FakeChatClient();
        fake.Enqueue(MultiToolCall(
                                   ("apply_patch", """{"path":"src/a.cs","diff":"*** Begin Patch"}"""),
                                   ("shell", """{"command":"delete everything"}"""),
                                   ("read_file", """{"path":"src/a.cs"}""")));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "src/a.cs")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "Inspect src/a.cs and also patch and run things for me."
        }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(await File.ReadAllTextAsync(target), Is.EqualTo("original content"));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(0));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(2));
        Assert.That(result.Statistics.ToolOutputCharacters > 0, Is.True);
        Assert.That(TestAssert.Single(result.Evidence).Path, Is.EqualTo("src/a.cs"));
    }

    [Test]
    public async Task ModelRequestsPathOutsideWorkspace_NoExecution()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"../../secrets.txt"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/note.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read the secrets." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(0));
        Assert.That(TestAssert.Single(result.Evidence).Path, Is.EqualTo("docs/note.md"));
    }

    [Test]
    public async Task MalformedFinalJson_MapsToFailedWithDetail()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("The answer is in docs/note.md, that is all I found."));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Failed));
        TestAssert.Contains("JSON", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Evidence, Is.Empty);
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task DraftWithInvalidEvidence_FailedKeepingOnlyStructurallyValidEvidence()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient
                        .Text("""{"conclusion":"Done.","evidence":[{"path":"docs/good.md","lineStart":1},{"path":"C:\\outside.md"},{"path":"../escape.md"}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}"""));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Failed));
        TestAssert.Contains("validation", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        var evidence = TestAssert.Single(result.Evidence);
        Assert.That(evidence.Path, Is.EqualTo("docs/good.md"));
    }

    [Test]
    public async Task ModelFailure_MapsToFailedWithShortDetail()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Failed));
        Assert.That(string.IsNullOrEmpty(result.StatusDetail), Is.False);
        TestAssert.DoesNotContain("scripted response", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task ModelFailure_StatusDetailWithholdsExceptionContentAndStaysValid()
    {
        using var dir = new TestTempDir();
        var fake = new FakeChatClient
        {
            ThrownError =
                new
                    HttpRequestException("GET https://secret-endpoint.example/v1/models failed sk-live-SENTINEL-abc123 " +
                                         "[response body: internal quota secrets for tenant-42]")
        };
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Failed));
        Assert.That(string.IsNullOrWhiteSpace(result.StatusDetail), Is.False);
        TestAssert.DoesNotContain("SENTINEL", result.StatusDetail, StringComparison.Ordinal);
        TestAssert.DoesNotContain("secret-endpoint", result.StatusDetail, StringComparison.Ordinal);
        TestAssert.DoesNotContain("response body", result.StatusDetail, StringComparison.Ordinal);
        TestAssert.DoesNotContain("tenant-42", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.StatusDetail.Length <= WorkerResultLimits.MaxStatusDetailLength, Is.True);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Evidence, Is.Empty);
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task StepLimit_MapsToIncompleteWithoutConclusion()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", "{}", "call_a"));
        fake.Enqueue(FakeChatClient.ToolCall("list_files", "{}", "call_b"));
        var runner = new WorkerRunner(fake, dir.Root, Options(2));

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        TestAssert.Contains("step limit", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(2));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task CallerCancellation_MapsToCancelled()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("unused"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           cancellation.Token);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Cancelled));
        TestAssert.Contains("cancel", result.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(0));
    }

    [Test]
    public async Task RunTimeout_MapsToIncomplete()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("working..."));
        fake.CancelMidStream = () => Thread.Sleep(500);
        var runner = new WorkerRunner(fake, dir.Root,
                                      Options(runTimeout : TimeSpan.FromMilliseconds(50)));

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        TestAssert.Contains("timed out", result.StatusDetail, StringComparison.Ordinal);
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
        Assert.That(result.Statistics.Elapsed >= TimeSpan.FromMilliseconds(50), Is.True);
    }

    [Test]
    public async Task ConsecutiveCalls_DoNotInheritHistoryMessagesOrConclusions()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/note.md", "content\n");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/note.md"}""", "r1"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("First conclusion.", "docs/note.md")));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/note.md"}""", "r2"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Second conclusion.", "other.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var first = await runner.RunAsync(new WorkerRequest { TaskPrompt = "TASK ONE unique marker alpha" },
                                          CancellationToken.None);
        var second = await runner.RunAsync(new WorkerRequest { TaskPrompt = "TASK TWO unique marker beta" },
                                           CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(first.Conclusion, Is.EqualTo("First conclusion."));
        Assert.That(second.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(second.Conclusion, Is.EqualTo("Second conclusion."));

        Assert.That(fake.RequestLog.Count, Is.EqualTo(4));
        var secondRunFirstRequest = fake.RequestLog[2];
        Assert.That(secondRunFirstRequest.Messages.Count, Is.EqualTo(2));
        Assert.That(secondRunFirstRequest.Messages[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(secondRunFirstRequest.Messages[1].Role, Is.EqualTo(ChatRole.User));
        TestAssert.DoesNotContain("unique marker alpha", secondRunFirstRequest.Messages[1].Content,
                                  StringComparison.Ordinal);
        TestAssert.Contains("unique marker beta", secondRunFirstRequest.Messages[1].Content, StringComparison.Ordinal);
        var offered = secondRunFirstRequest.Tools!.Select(t => t.Name)
                                           .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.That(offered, Is.EqualTo(new[] { "list_files", "read_file", "search_text" }));
    }

    [Test]
    public void Constructor_RejectsInvalidTrustedOptions()
    {
        var       fake = new FakeChatClient();
        using var dir  = new TestTempDir();

        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { RunTimeout = TimeSpan.Zero }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxAgentSteps = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { DefaultToolTimeoutSeconds = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxTaskPackageCharacters = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxToolCalls = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxToolOutputCharacters = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxCumulativeContextTokens = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxContextTokensPerRequest = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { MaxModelResponseCharacters = 0 }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with
                                                                    {
                                                                        MaxModelResponseCharacters = 64_001
                                                                    }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                    Options() with { Model = " " }));
        TestAssert.Throws<ArgumentException>(() => new WorkerRunner(fake, " ", Options()));
    }

    // ---- focus scope ------------------------------------------------------------------

    [Test]
    public async Task FocusPaths_AncestorSiblingAndAbsoluteExtensionsAreDenied()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", "needle in docs");
        dir.WriteFile("src/b.cs", "TOPSECRET code");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"."}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"src/b.cs"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file",
                                             $$"""{"path":"{{AbsolutePath(dir.Root, "src/b.cs")}}"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("list_files", "{}"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "Find the needle.",
            FocusPaths = ["docs"]
        }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(1));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(5));
        for (var i = 0; i < 4; i++) TestAssert.Contains("focused read scope", messages[i], StringComparison.Ordinal);

        TestAssert.Contains("needle in docs", messages[4], StringComparison.Ordinal);
        TestAssert.DoesNotContain("TOPSECRET", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Test]
    public async Task SensitiveFiles_DirectAccessDeniedWithoutContentLeak()
    {
        using var dir = new TestTempDir();
        dir.WriteFile(".env", "SECRET_ENV=1");
        dir.WriteFile(".env.local", "SECRET_LOCAL=2");
        dir.WriteFile("certs/server.pem", "-----BEGIN PRIVATE KEY-----");
        dir.WriteFile(".aws/credentials", "[default]\naws_secret_access_key = AWSKEY123");
        dir.WriteFile("id_ed25519", "OPENSSH PRIVATE KEY");
        dir.WriteFile("tinyharness.json", """{"commandRules":[]}""");
        dir.WriteFile("docs/normal.md", "plain text");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":".env"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":".env.local"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"certs/server.pem"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":".aws/credentials"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"id_ed25519"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"tinyharness.json"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"SECRET_ENV","path":".env"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/normal.md"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/normal.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options(12));

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Look around." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(1));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(8));
        for (var i = 0; i < 7; i++)
            TestAssert.Contains("excluded by the worker read policy", messages[i], StringComparison.Ordinal);

        TestAssert.Contains("plain text", messages[7], StringComparison.Ordinal);
        var joined = string.Join('\n', messages);
        TestAssert.DoesNotContain("SECRET_ENV", joined, StringComparison.Ordinal);
        TestAssert.DoesNotContain("SECRET_LOCAL", joined, StringComparison.Ordinal);
        TestAssert.DoesNotContain("BEGIN PRIVATE KEY", joined, StringComparison.Ordinal);
        TestAssert.DoesNotContain("AWSKEY123", joined, StringComparison.Ordinal);
        TestAssert.DoesNotContain("OPENSSH", joined, StringComparison.Ordinal);
        TestAssert.DoesNotContain("commandRules", joined, StringComparison.Ordinal);
    }

    [Test]
    public async Task SensitivePaths_HiddenFromRecursiveListAndSearch()
    {
        using var dir = new TestTempDir();
        dir.WriteFile(".env", "SECRET_ENV=1");
        dir.WriteFile("config/secrets.json", """{"apiKey":"s3cr3t"}""");
        dir.WriteFile(".ssh/id_rsa", "PRIVATE KEY MATERIAL");
        dir.WriteFile("app/main.cs", "class Program { }");
        dir.WriteFile("readme.md", "hello");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":".","recursive":true}"""));
        fake.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"class","path":"."}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "app/main.cs")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Map the repo." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(2));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(2));
        var listOutput = messages[0];
        TestAssert.Contains("app/main.cs", listOutput, StringComparison.Ordinal);
        TestAssert.Contains("readme.md", listOutput, StringComparison.Ordinal);
        TestAssert.DoesNotContain(".env", listOutput, StringComparison.Ordinal);
        TestAssert.DoesNotContain("secrets.json", listOutput, StringComparison.Ordinal);
        TestAssert.DoesNotContain(".ssh", listOutput, StringComparison.Ordinal);
        var searchOutput = messages[1];
        TestAssert.Contains("class Program", searchOutput, StringComparison.Ordinal);
        TestAssert.DoesNotContain("s3cr3t", searchOutput, StringComparison.Ordinal);
        TestAssert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
        TestAssert.DoesNotContain("PRIVATE KEY MATERIAL", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Test]
    public async Task SimilarLegitimateFilesRemainReadable()
    {
        using var dir = new TestTempDir();
        dir.WriteFile(".env.sample", "SAMPLE_ENV=1");
        dir.WriteFile("env.sample", "SAMPLE_OK=1");
        dir.WriteFile("tinyharness.config.json", "{}");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":".env.sample"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"env.sample"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"tinyharness.config.json"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "env.sample")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Inspect samples." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(2));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(3));
        TestAssert.Contains("excluded by the worker read policy", messages[0], StringComparison.Ordinal);
        TestAssert.Contains("SAMPLE_OK", messages[1], StringComparison.Ordinal);
        TestAssert.Contains("{}", messages[2], StringComparison.Ordinal);
        TestAssert.DoesNotContain("SAMPLE_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Test]
    public async Task ReadPolicy_IsBuiltPerRunAndNeverInherited()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", "DOCSFILE docs-only");
        dir.WriteFile("src/b.cs", "SRCFILE src-only");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "p1"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("First.", "docs/a.md")));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"src/b.cs"}""", "p2"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Second.", "src/b.cs")));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "p3"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Third.", "src/b.cs")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var first = await runner.RunAsync(new WorkerRequest { TaskPrompt = "one", FocusPaths = ["docs"] },
                                          CancellationToken.None);
        var second = await runner.RunAsync(new WorkerRequest { TaskPrompt = "two" }, CancellationToken.None);
        var third = await runner.RunAsync(new WorkerRequest { TaskPrompt = "three", FocusPaths = ["src"] },
                                          CancellationToken.None);

        Assert.That(first.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(second.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(third.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(fake.RequestLog.Count, Is.EqualTo(6));
        // 每个 run 的最终历史只包含本 run 的工具消息：run 间互不继承。
        // 每个 run 的最终历史只含自己的工具消息：跨 run 不继承任何内容。
        var runOne   = TestAssert.Single(ToolMessages(fake.RequestLog[1]));
        var runTwo   = TestAssert.Single(ToolMessages(fake.RequestLog[3]));
        var runThree = TestAssert.Single(ToolMessages(fake.RequestLog[5]));
        TestAssert.Contains("DOCSFILE", runOne, StringComparison.Ordinal);
        TestAssert.Contains("SRCFILE", runTwo, StringComparison.Ordinal);
        TestAssert.Contains("focused read scope", runThree, StringComparison.Ordinal);
        Assert.That(second.Statistics.ToolCalls, Is.EqualTo(1));
        Assert.That(third.Statistics.ToolCalls, Is.EqualTo(0));
    }

    private static string AbsolutePath(string root, string relative) => root.Replace('\\', '/') + "/" + relative;

    // ---- link boundary ------------------------------------------------------------------

    /// <summary>
    ///     <para>用 mklink /J 创建目录 junction（不需要管理员权限）。</para>
    ///     <para>Creates a directory junction via mklink /J (no administrator privileges required).</para>
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow        = true,
            UseShellExecute       = false,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo)!;
        var       error   = process.StandardError.ReadToEnd().Trim();
        Assert.That(process.WaitForExit(10_000), Is.True, $"mklink /J did not finish in time: {error}");
        Assert.That(process.ExitCode == 0, Is.True, $"mklink /J failed: {error}");
    }

    [Test]
    public async Task FocusScope_LinkedPathResolvingOutsideFocus_IsDeniedBeforeRead()
    {
        // 回归：focus 内的目录 junction 指向工作区内 focus 外目录时，最终解析落点
        // 必须在真正打开文件前被拒绝。junction 仅在 Windows 上创建；其他平台的
        // symlink 行为由相同的逐段解析代码覆盖，但本测试未在非 Windows 运行时验证。
        // focus 内指向另一工作区目录的 junction 必须在打开文件前按解析后的落点路径拒绝。
        // junction 仅限 Windows；非 Windows 符号链接走相同的逐段解析代码，但本测试未在
        // 非 Windows 运行时上执行过。
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", "focus content");
        dir.WriteFile("rootsecret.txt", "OUTSIDE-FOCUS-CONTENT");
        dir.WriteFile(".env", "SECRET_ENV=9");
        CreateJunction(Path.Combine(dir.Root, "docs", "link"), dir.Root);

        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/link/rootsecret.txt"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/link/.env"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("search_text", """{"pattern":"OUTSIDE","path":"docs"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "read through the link",
            FocusPaths = ["docs"]
        }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        // ToolCalls 统计"工具已运行"：链接读取在 Execute 阶段被策略拦截、搜索和成功读取
        // 各计一次；.env 尝试在 Prepare 即被拦截，不计入。全部三次都无敏感内容返回。
        // ToolCalls 统计"工具已运行"：链接读取在 Execute 内被策略拦下，搜索与成功读取已运行；
        // .env 尝试在 Prepare 被拦下、不计入。三者都未返回敏感内容。
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(3));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(4));

        // 链接落点在 focus 外：打开前被拒，内容不回传。
        // 链接落在 focus 外：打开前拒绝，内容从未返回。
        TestAssert.Contains("outside the worker read scope", messages[0], StringComparison.Ordinal);
        // 链接落点是敏感文件：按敏感排除拒绝（名称规则在词法阶段即命中）。
        // 链接落在敏感文件上：被敏感排除规则拒绝。
        TestAssert.Contains("excluded by the worker read policy", messages[1], StringComparison.Ordinal);
        // 枚举不下钻 junction：搜索只扫 focus 内普通文件，链接后的内容不可达。
        // 枚举绝不穿过 junction 下钻：搜索只扫描 focus 内文件。
        TestAssert.Contains("no matches", messages[2], StringComparison.Ordinal);
        TestAssert.DoesNotContain("link", messages[2], StringComparison.Ordinal);
        TestAssert.Contains("focus content", messages[3], StringComparison.Ordinal);
        TestAssert.DoesNotContain("OUTSIDE-FOCUS-CONTENT", string.Join('\n', messages), StringComparison.Ordinal);
        TestAssert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Test]
    public async Task FocusScope_ListFilesJunctionRootDeniedAndLandingNamesNotExposed()
    {
        // 复核回归 1+2：显式列举 junction 目录被策略拒绝且不列出落点内容；递归列举
        // 不暴露落点在 focus 外/敏感目录的链接条目名称；普通 focus 内文件仍可列出。
        // 显式列出 junction 时策略在枚举落点内容前即拒绝；递归列出不暴露落点在 focus 外或
        // 敏感的链接项；focus 内普通文件正常列出。
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", "focus content");
        dir.WriteFile("docs/normal.md", "another focus file");
        dir.WriteFile("rootsecret.txt", "OUTSIDE-FOCUS-CONTENT");
        dir.WriteFile(".env", "SECRET_ENV=9");
        CreateJunction(Path.Combine(dir.Root, "docs", "link"), dir.Root);

        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":"docs/link"}"""));
        fake.Enqueue(FakeChatClient.ToolCall("list_files", """{"path":"docs","recursive":true}"""));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest
        {
            TaskPrompt = "list through the link",
            FocusPaths = ["docs"]
        }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        // 被拒的列举在 Execute 阶段拦截但仍计入一次工具运行。
        // 被拒的列目录在 Execute 内被拦下，仍计为一次工具运行。
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(3));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(3));

        // 显式列举 junction：按落点在 focus 外拒绝，且未枚举任何落点名称。
        // 显式列 junction：按落点拒绝，未枚举任何落点名。
        TestAssert.Contains("outside the worker read scope", messages[0], StringComparison.Ordinal);
        TestAssert.DoesNotContain("rootsecret", messages[0], StringComparison.Ordinal);
        TestAssert.DoesNotContain(".env", messages[0], StringComparison.Ordinal);

        // 递归列举：链接条目按落点整体排除，名称不出现；focus 内文件照常列出。
        // 递归列出：链接项按落点被排除；focus 内文件正常列出。
        var listing = messages[1];
        TestAssert.Contains("a.md", listing, StringComparison.Ordinal);
        TestAssert.Contains("normal.md", listing, StringComparison.Ordinal);
        TestAssert.DoesNotContain("link", listing, StringComparison.Ordinal);
        TestAssert.DoesNotContain("rootsecret.txt", listing, StringComparison.Ordinal);
        TestAssert.DoesNotContain(".env", listing, StringComparison.Ordinal);

        TestAssert.Contains("focus content", messages[2], StringComparison.Ordinal);
        TestAssert.DoesNotContain("OUTSIDE-FOCUS-CONTENT", string.Join('\n', messages), StringComparison.Ordinal);
        TestAssert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    // ---- execution budgets ----------------------------------------------------------

    [Test]
    public async Task TaskPackageOverBudget_IncompleteWithZeroModelRequests()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options() with { MaxTaskPackageCharacters = 60 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = new string('t', 120) },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail, Is.EqualTo("The task package exceeds the configured worker input budget."));
        Assert.That(fake.Requests, Is.EqualTo(0));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(0));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(0));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task ToolCallBudget_AtLimitAllowed_OverLimitRefusedWithoutExecution()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", "AAA");
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "b1"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "b2"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "b3"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root,
                                      Options(8) with { MaxToolCalls = 2 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read repeatedly." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("Stopped at the configured tool-call budget before the model produced a conclusion."));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(2));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(3));
        Assert.That(messages[0], Is.EqualTo("AAA"));
        Assert.That(messages[1], Is.EqualTo("AAA"));
        Assert.That(messages[2], Is.EqualTo(string.Empty));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task ToolOutputBudget_ExactlyAtLimitAllowed_ThenRefused()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", new string('A', 20));
        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "o1"));
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "o2"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxToolOutputCharacters = 20 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read the file." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("Stopped at the configured tool-output budget before the model produced a conclusion."));
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.That(messages.Count, Is.EqualTo(2));
        Assert.That(messages[0], Is.EqualTo(new string('A', 20)));
        Assert.That(messages[1], Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(1));
        Assert.That(result.Statistics.ToolOutputCharacters, Is.EqualTo(20));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task ContextTokenBudget_BlockedRequestNeverReachesClient()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", new string('B', 4_000));

        // 第一遍用宽松预算测出首个请求的精确 token 估算值；第二遍以该值作上限重跑：
        // 第一个请求恰好等于上限被允许，第二个请求（历史增长）在真实 client 之前被拦截。
        var measureFake = new FakeChatClient();
        measureFake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "c1"));
        measureFake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var measureRunner = new WorkerRunner(measureFake, dir.Root, Options(4));
        await measureRunner.RunAsync(new WorkerRequest { TaskPrompt = "Read the big file." },
                                     CancellationToken.None);
        var firstRequest = measureFake.RequestLog[0];
        var firstEstimate = TokenEstimator.EstimateMessages(firstRequest.Messages)
                          + (firstRequest.Tools is { Count: > 0 }
                                ? TokenEstimator.EstimateToolDefinitions(firstRequest.Tools)
                                : 0);
        Assert.That(firstEstimate > 0, Is.True);

        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "c1"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root,
                                      Options(4) with { MaxCumulativeContextTokens = firstEstimate });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read the big file." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("Stopped at the configured context-token budget before the model produced a conclusion."));
        Assert.That(fake.Requests, Is.EqualTo(1));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task PerRequestContextBudget_ExactFirstRequestAllowed_OverLimitNeverReachesClient()
    {
        using var dir         = new TestTempDir();
        var       measureFake = new FakeChatClient();
        measureFake.Enqueue(FakeChatClient.Text(DraftJson("Measured.", "docs/a.md")));
        var measureRunner = new WorkerRunner(measureFake, dir.Root, Options(2));
        await measureRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." }, CancellationToken.None);
        var request = measureFake.RequestLog[0];
        var estimate = TokenEstimator.EstimateMessages(request.Messages)
                     + TokenEstimator.EstimateToolDefinitions(request.Tools!);

        var exactFake = new FakeChatClient();
        exactFake.Enqueue(FakeChatClient.Text(DraftJson("Exact boundary.", "docs/a.md")));
        var exactRunner = new WorkerRunner(exactFake, dir.Root,
                                           Options(2) with { MaxContextTokensPerRequest = estimate });
        var exact = await exactRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." },
                                               CancellationToken.None);
        Assert.That(exact.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(exactFake.Requests, Is.EqualTo(1));

        var blockedFake = new FakeChatClient();
        blockedFake.Enqueue(FakeChatClient.Text(DraftJson("Must not run.", "docs/a.md")));
        var blockedRunner = new WorkerRunner(blockedFake, dir.Root,
                                             Options(2) with { MaxContextTokensPerRequest = estimate - 1 });
        var blocked = await blockedRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." },
                                                   CancellationToken.None);

        Assert.That(blocked.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(blockedFake.Requests, Is.EqualTo(0));
        Assert.That(blocked.Statistics.ModelRequests, Is.EqualTo(0));
        Assert.That(blocked.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(WorkerResultValidator.Validate(blocked).IsValid, Is.True);
    }

    [Test]
    public async Task ModelResponseBudget_TextStreamStopsAtBoundary_MapsToIncomplete()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("This response is deliberately long and is not JSON at all."));
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxModelResponseCharacters = 30 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("Stopped at the configured model-response budget before the model produced a conclusion."));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task ModelResponseBudget_IncompleteToolCallNotExecuted()
    {
        using var dir         = new TestTempDir();
        var       fake        = new FakeChatClient();
        var       longPattern = new string('A', 300);
        fake.Enqueue(FakeChatClient.ToolCall("search_text", $$"""{"pattern":"{{longPattern}}"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxModelResponseCharacters = 200 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Search." }, CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("Stopped at the configured model-response budget before the model produced a conclusion."));
        // 截断的工具调用参数是非法 JSON，AgentLoop 在组装阶段即失败——调用没有执行，
        // 历史里没有任何 tool 消息，第二个脚本响应不会被消费。
        // 被截断的工具调用参数是非法 JSON，AgentLoop 在拼装消息时失败——调用从未执行，历史
        // 不含工具消息，第二段脚本响应也从未被消费。
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(0));
        Assert.That(ToolMessages(fake.RequestLog[^1]), Is.Empty);
        Assert.That(fake.Requests, Is.EqualTo(1));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task ModelResponseBudget_ReasoningItemFullTextIsNotDoubleCounted()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();

        // ReasoningItem 事件携带的 ReasoningDelta 是该条目的完整文本，与先前流出的增量重复：
        // 预算若把它重复计入，本流在预算 700 下于条目事件处即超限（500 增量 + 507 条目 > 700），
        // 响应被截断并映射为 Incomplete；按增量口径计费后整流 670 字符，在预算内完成。
        // ReasoningItem 事件在 ReasoningDelta 中携带条目完整文本，与已流式增量重复：重复计费
        // 会在 item 事件处撑爆 700 字符预算（500 增量 + 507 条目 > 700），把响应截断成
        // Incomplete；只按增量计费时整条 670 字符流都在预算内。
        var reasoning = new string('R', 500);
        fake.Enqueue(
        [
            new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = reasoning },
            new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ReasoningItem,
                ReasoningDelta         = reasoning,
                ReasoningProtectedData = "enc",
                ReasoningItemId        = "rs_1"
            },
            new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.ContentDelta,
                ContentDelta = DraftJson("Done.", "docs/a.md")
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" }
        ]);
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxModelResponseCharacters = 700 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Conclusion, Is.EqualTo("Done."));
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task ResultConclusionOverBudget_IncompleteWithoutConclusion()
    {
        using var dir            = new TestTempDir();
        var       fake           = new FakeChatClient();
        var       longConclusion = new string('C', 2_500);
        fake.Enqueue(FakeChatClient.Text($$"""
                                         {"conclusion":"{{longConclusion}}","evidence":[{"path":"docs/a.md"}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}
                                         """));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("The worker result budget was exhausted before a conclusion could be delivered."));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(TestAssert.Single(result.Evidence).Path, Is.EqualTo("docs/a.md"));
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }

    [Test]
    public async Task ResultTotalTextOverBudget_IncompleteWithoutConclusion()
    {
        using var dir     = new TestTempDir();
        var       fake    = new FakeChatClient();
        var       entries = string.Join(",", Enumerable.Repeat($"\"{new string('x', 1_000)}\"", 8));
        fake.Enqueue(FakeChatClient.Text($$"""
                                         {"conclusion":"Done.","evidence":[],"suggestedChanges":[{{entries}}],"testSuggestions":[{{entries}}],"uncertainties":[{{entries}}]}
                                         """));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Incomplete));
        Assert.That(result.StatusDetail,
                    Is.EqualTo("The worker result budget was exhausted before a conclusion could be delivered."));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(result.Evidence, Is.Empty);
        Assert.That(WorkerResultValidator.Validate(result).IsValid, Is.True);
    }
}
