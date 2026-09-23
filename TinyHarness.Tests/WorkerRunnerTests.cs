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
        MaxModelResponseCharacters = 64_000,
    };

    private static string DraftJson(string conclusion, string path) => $$"""
        {"conclusion":"{{conclusion}}","evidence":[{"path":"{{path}}","lineStart":2,"lineEnd":3,"note":"States it."}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}
        """;

    /// <summary>
    /// 取单个请求里的 tool 消息内容；模型的最后一次请求携带完整最终历史，
    /// 因此单 run 测试传 <c>fake.RequestLog[^1]</c>，多 run 测试传各 run 的最后请求。
    /// Takes the tool-message contents of one request. The model's last request carries the complete
    /// final history, so single-run tests pass <c>fake.RequestLog[^1]</c> and multi-run tests pass
    /// each run's final request.
    /// </summary>
    private static IReadOnlyList<string> ToolMessages(ChatCompletionRequest request) => request.Messages
       .Where(message => message.Role == ChatRole.Tool)
       .Select(message => message.Content)
       .ToArray();

    private static IReadOnlyList<ChatStreamEvent> MultiToolCall(params (string Name, string Arguments)[] calls)
    {
        var events = new List<ChatStreamEvent>();
        for (var i = 0; i < calls.Length; i++)
        {
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = i,
                ToolCallId             = $"call_{i}",
                ToolCallFunctionName   = calls[i].Name,
                ToolCallArgumentsDelta = calls[i].Arguments,
            });
        }

        events.Add(new ChatStreamEvent { Kind = ChatStreamEventKind.End });
        return events;
    }

    [Fact]
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
            FocusPaths = ["docs"],
        }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal("The retry budget is documented in docs/note.md.", result.Conclusion);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("docs/note.md", evidence.Path);
        Assert.Equal(2, evidence.LineStart);
        Assert.Equal(3, evidence.LineEnd);
        Assert.Equal("States it.", evidence.Note);
        Assert.Empty(result.TestSuggestions);
        Assert.Empty(result.Uncertainties);
        Assert.Equal(2, result.Statistics.ModelRequests);
        Assert.Equal(1, result.Statistics.ToolCalls);
        Assert.Equal("line1\nline2\nline3".Length, result.Statistics.ToolOutputCharacters);
        Assert.True(result.Statistics.Elapsed >= TimeSpan.Zero);

        var offered = fake.RequestLog[0].Tools!.Select(t => t.Name)
                          .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "list_files", "read_file", "search_text" }, offered);
        Assert.Equal("fake-model", fake.LastRequestModel);
    }

    [Fact]
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
            FocusPaths = ["docs"],
        }, CancellationToken.None);

        var messages = fake.RequestLog[0].Messages;
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.DoesNotContain("retry budget", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Contains("Where is the retry budget documented?", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("KNOWN FACTS", messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("- docs", messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidRequest_FailsWithoutAnyModelRequest()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "   " }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Failed, result.Status);
        Assert.Equal(0, fake.Requests);
        Assert.Equal(0, result.Statistics.ModelRequests);
        Assert.Equal(0, result.Statistics.ToolCalls);
        Assert.False(string.IsNullOrEmpty(result.StatusDetail));
        Assert.Empty(result.Evidence);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal("original content", await File.ReadAllTextAsync(target));
        Assert.Equal(0, result.Statistics.ToolCalls);
        Assert.Equal(2, result.Statistics.ModelRequests);
        Assert.True(result.Statistics.ToolOutputCharacters > 0);
        Assert.Equal("src/a.cs", Assert.Single(result.Evidence).Path);
    }

    [Fact]
    public async Task ModelRequestsPathOutsideWorkspace_NoExecution()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"../../secrets.txt"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/note.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read the secrets." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(0, result.Statistics.ToolCalls);
        Assert.Equal("docs/note.md", Assert.Single(result.Evidence).Path);
    }

    [Fact]
    public async Task MalformedFinalJson_MapsToFailedWithDetail()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("The answer is in docs/note.md, that is all I found."));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Failed, result.Status);
        Assert.Contains("JSON", result.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Statistics.ModelRequests);
    }

    [Fact]
    public async Task DraftWithInvalidEvidence_FailedKeepingOnlyStructurallyValidEvidence()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient
                        .Text("""{"conclusion":"Done.","evidence":[{"path":"docs/good.md","lineStart":1},{"path":"C:\\outside.md"},{"path":"../escape.md"}],"suggestedChanges":[],"testSuggestions":[],"uncertainties":[]}"""));
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Failed, result.Status);
        Assert.Contains("validation", result.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Conclusion);
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("docs/good.md", evidence.Path);
    }

    [Fact]
    public async Task ModelFailure_MapsToFailedWithShortDetail()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.StatusDetail));
        Assert.DoesNotContain("scripted response", result.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal(1, result.Statistics.ModelRequests);
    }

    [Fact]
    public async Task ModelFailure_StatusDetailWithholdsExceptionContentAndStaysValid()
    {
        using var dir = new TestTempDir();
        var fake = new FakeChatClient
        {
            ThrownError =
                new
                    HttpRequestException("GET https://secret-endpoint.example/v1/models failed sk-live-SENTINEL-abc123 " +
                                         "[response body: internal quota secrets for tenant-42]"),
        };
        var runner = new WorkerRunner(fake, dir.Root, Options());

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Failed, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.StatusDetail));
        Assert.DoesNotContain("SENTINEL", result.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-endpoint", result.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("response body", result.StatusDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant-42", result.StatusDetail, StringComparison.Ordinal);
        Assert.True(result.StatusDetail.Length <= WorkerResultLimits.MaxStatusDetailLength);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Empty(result.Evidence);
        Assert.Equal(1, result.Statistics.ModelRequests);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
    public async Task StepLimit_MapsToIncompleteWithoutConclusion()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("list_files", "{}", "call_a"));
        fake.Enqueue(FakeChatClient.ToolCall("list_files", "{}", "call_b"));
        var runner = new WorkerRunner(fake, dir.Root, Options(maxSteps : 2));

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Contains("step limit", result.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal(2, result.Statistics.ModelRequests);
        Assert.Equal(2, result.Statistics.ToolCalls);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Cancelled, result.Status);
        Assert.Contains("cancel", result.StatusDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal(0, result.Statistics.ModelRequests);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Contains("timed out", result.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal(1, result.Statistics.ModelRequests);
        Assert.True(result.Statistics.Elapsed >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Completed, first.Status);
        Assert.Equal("First conclusion.", first.Conclusion);
        Assert.Equal(WorkerResultStatus.Completed, second.Status);
        Assert.Equal("Second conclusion.", second.Conclusion);

        Assert.Equal(4, fake.RequestLog.Count);
        var secondRunFirstRequest = fake.RequestLog[2];
        Assert.Equal(2, secondRunFirstRequest.Messages.Count);
        Assert.Equal(ChatRole.System, secondRunFirstRequest.Messages[0].Role);
        Assert.Equal(ChatRole.User, secondRunFirstRequest.Messages[1].Role);
        Assert.DoesNotContain("unique marker alpha", secondRunFirstRequest.Messages[1].Content,
                              StringComparison.Ordinal);
        Assert.Contains("unique marker beta", secondRunFirstRequest.Messages[1].Content, StringComparison.Ordinal);
        var offered = secondRunFirstRequest.Tools!.Select(t => t.Name)
                                           .OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "list_files", "read_file", "search_text" }, offered);
    }

    [Fact]
    public void Constructor_RejectsInvalidTrustedOptions()
    {
        var       fake = new FakeChatClient();
        using var dir  = new TestTempDir();

        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { RunTimeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root, Options() with { MaxAgentSteps = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { DefaultToolTimeoutSeconds = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxTaskPackageCharacters = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root, Options() with { MaxToolCalls = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxToolOutputCharacters = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxCumulativeContextTokens = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxContextTokensPerRequest = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxModelResponseCharacters = 0 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { MaxModelResponseCharacters = 64_001 }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, dir.Root,
                                                                Options() with { Model = " " }));
        Assert.Throws<ArgumentException>(() => new WorkerRunner(fake, " ", Options()));
    }

    // ---- focus scope ------------------------------------------------------------------

    [Fact]
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
            FocusPaths = ["docs"],
        }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(1, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(5, messages.Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Contains("focused read scope", messages[i], StringComparison.Ordinal);
        }

        Assert.Contains("needle in docs", messages[4], StringComparison.Ordinal);
        Assert.DoesNotContain("TOPSECRET", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Fact]
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
        var runner = new WorkerRunner(fake, dir.Root, Options(maxSteps : 12));

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Look around." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(1, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(8, messages.Count);
        for (var i = 0; i < 7; i++)
        {
            Assert.Contains("excluded by the worker read policy", messages[i], StringComparison.Ordinal);
        }

        Assert.Contains("plain text", messages[7], StringComparison.Ordinal);
        var joined = string.Join('\n', messages);
        Assert.DoesNotContain("SECRET_ENV", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_LOCAL", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("AWSKEY123", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENSSH", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("commandRules", joined, StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(2, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(2, messages.Count);
        var listOutput = messages[0];
        Assert.Contains("app/main.cs", listOutput, StringComparison.Ordinal);
        Assert.Contains("readme.md", listOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", listOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.json", listOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(".ssh", listOutput, StringComparison.Ordinal);
        var searchOutput = messages[1];
        Assert.Contains("class Program", searchOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", searchOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY MATERIAL", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(2, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(3, messages.Count);
        Assert.Contains("excluded by the worker read policy", messages[0], StringComparison.Ordinal);
        Assert.Contains("SAMPLE_OK", messages[1], StringComparison.Ordinal);
        Assert.Contains("{}", messages[2], StringComparison.Ordinal);
        Assert.DoesNotContain("SAMPLE_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Completed, first.Status);
        Assert.Equal(WorkerResultStatus.Completed, second.Status);
        Assert.Equal(WorkerResultStatus.Completed, third.Status);
        Assert.Equal(6, fake.RequestLog.Count);
        // 每个 run 的最终历史只包含本 run 的工具消息：run 间互不继承。
        // Each run's final history only holds its own tool messages: nothing is inherited across runs.
        var runOne   = Assert.Single(ToolMessages(fake.RequestLog[1]));
        var runTwo   = Assert.Single(ToolMessages(fake.RequestLog[3]));
        var runThree = Assert.Single(ToolMessages(fake.RequestLog[5]));
        Assert.Contains("DOCSFILE", runOne, StringComparison.Ordinal);
        Assert.Contains("SRCFILE", runTwo, StringComparison.Ordinal);
        Assert.Contains("focused read scope", runThree, StringComparison.Ordinal);
        Assert.Equal(1, second.Statistics.ToolCalls);
        Assert.Equal(0, third.Statistics.ToolCalls);
    }

    private static string AbsolutePath(string root, string relative)
        => root.Replace('\\', '/') + "/" + relative;

    // ---- link boundary ------------------------------------------------------------------

    /// <summary>
    /// 用 mklink /J 创建目录 junction（不需要管理员权限）。
    /// Creates a directory junction via mklink /J (no administrator privileges required).
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow        = true,
            UseShellExecute       = false,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)!;
        var       error   = process.StandardError.ReadToEnd().Trim();
        Assert.True(process.WaitForExit(10_000), $"mklink /J did not finish in time: {error}");
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {error}");
    }

    [Fact]
    public async Task FocusScope_LinkedPathResolvingOutsideFocus_IsDeniedBeforeRead()
    {
        // 回归：focus 内的目录 junction 指向工作区内 focus 外目录时，最终解析落点
        // 必须在真正打开文件前被拒绝。junction 仅在 Windows 上创建；其他平台的
        // symlink 行为由相同的逐段解析代码覆盖，但本测试未在非 Windows 运行时验证。
        // Regression: a directory junction inside the focus pointing at another workspace directory
        // must be denied at its resolved landing path before the file is opened. Junctions are
        // Windows-only here; non-Windows symlinks run the same segment-wise resolution code, but
        // this test has not been executed on a non-Windows runtime.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
            FocusPaths = ["docs"],
        }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        // ToolCalls 统计"工具已运行"：链接读取在 Execute 阶段被策略拦截、搜索和成功读取
        // 各计一次；.env 尝试在 Prepare 即被拦截，不计入。全部三次都无敏感内容返回。
        // ToolCalls counts "the tool ran": the linked read was stopped by the policy inside
        // Execute, the search and the successful read ran; the .env attempt was stopped in Prepare
        // and does not count. None of the three returned sensitive content.
        Assert.Equal(3, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(4, messages.Count);

        // 链接落点在 focus 外：打开前被拒，内容不回传。
        // The link lands outside the focus: denied before opening, content never returned.
        Assert.Contains("outside the worker read scope", messages[0], StringComparison.Ordinal);
        // 链接落点是敏感文件：按敏感排除拒绝（名称规则在词法阶段即命中）。
        // The link lands on a sensitive file: rejected by the sensitive exclusion.
        Assert.Contains("excluded by the worker read policy", messages[1], StringComparison.Ordinal);
        // 枚举不下钻 junction：搜索只扫 focus 内普通文件，链接后的内容不可达。
        // Enumeration never descends through the junction: the search only scans in-focus files.
        Assert.Contains("no matches", messages[2], StringComparison.Ordinal);
        Assert.DoesNotContain("link", messages[2], StringComparison.Ordinal);
        Assert.Contains("focus content", messages[3], StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE-FOCUS-CONTENT", string.Join('\n', messages), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FocusScope_ListFilesJunctionRootDeniedAndLandingNamesNotExposed()
    {
        // 复核回归 1+2：显式列举 junction 目录被策略拒绝且不列出落点内容；递归列举
        // 不暴露落点在 focus 外/敏感目录的链接条目名称；普通 focus 内文件仍可列出。
        // Review regressions 1+2: explicitly listing a junction is denied by the policy without
        // enumerating the landing; a recursive listing never exposes link entries whose landing is
        // outside the focus or sensitive; ordinary in-focus files still list.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
            FocusPaths = ["docs"],
        }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        // 被拒的列举在 Execute 阶段拦截但仍计入一次工具运行。
        // The denied listing is stopped inside Execute and still counts as one tool run.
        Assert.Equal(3, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(3, messages.Count);

        // 显式列举 junction：按落点在 focus 外拒绝，且未枚举任何落点名称。
        // Explicit junction listing: denied by its landing, with no landing names enumerated.
        Assert.Contains("outside the worker read scope", messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain("rootsecret", messages[0], StringComparison.Ordinal);
        Assert.DoesNotContain(".env", messages[0], StringComparison.Ordinal);

        // 递归列举：链接条目按落点整体排除，名称不出现；focus 内文件照常列出。
        // Recursive listing: the link entry is excluded by its landing; in-focus files list normally.
        var listing = messages[1];
        Assert.Contains("a.md", listing, StringComparison.Ordinal);
        Assert.Contains("normal.md", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("link", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("rootsecret.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", listing, StringComparison.Ordinal);

        Assert.Contains("focus content", messages[2], StringComparison.Ordinal);
        Assert.DoesNotContain("OUTSIDE-FOCUS-CONTENT", string.Join('\n', messages), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_ENV", string.Join('\n', messages), StringComparison.Ordinal);
    }

    // ---- execution budgets ----------------------------------------------------------

    [Fact]
    public async Task TaskPackageOverBudget_IncompleteWithZeroModelRequests()
    {
        using var dir    = new TestTempDir();
        var       fake   = new FakeChatClient();
        var       runner = new WorkerRunner(fake, dir.Root, Options() with { MaxTaskPackageCharacters = 60 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = new string('t', 120) },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("The task package exceeds the configured worker input budget.", result.StatusDetail);
        Assert.Equal(0, fake.Requests);
        Assert.Equal(0, result.Statistics.ModelRequests);
        Assert.Equal(0, result.Statistics.ToolCalls);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
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
                                      Options(maxSteps : 8) with { MaxToolCalls = 2 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read repeatedly." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("Stopped at the configured tool-call budget before the model produced a conclusion.",
                     result.StatusDetail);
        Assert.Equal(2, result.Statistics.ToolCalls);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(3, messages.Count);
        Assert.Equal("AAA", messages[0]);
        Assert.Equal("AAA", messages[1]);
        Assert.Equal(string.Empty, messages[2]);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("Stopped at the configured tool-output budget before the model produced a conclusion.",
                     result.StatusDetail);
        var messages = ToolMessages(fake.RequestLog[^1]);
        Assert.Equal(2, messages.Count);
        Assert.Equal(new string('A', 20), messages[0]);
        Assert.Equal(string.Empty, messages[1]);
        Assert.Equal(1, result.Statistics.ToolCalls);
        Assert.Equal(20, result.Statistics.ToolOutputCharacters);
        Assert.Equal(string.Empty, result.Conclusion);
    }

    [Fact]
    public async Task ContextTokenBudget_BlockedRequestNeverReachesClient()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("docs/a.md", new string('B', 4_000));

        // 第一遍用宽松预算测出首个请求的精确 token 估算值；第二遍以该值作上限重跑：
        // 第一个请求恰好等于上限被允许，第二个请求（历史增长）在真实 client 之前被拦截。
        var measureFake = new FakeChatClient();
        measureFake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "c1"));
        measureFake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var measureRunner = new WorkerRunner(measureFake, dir.Root, Options(maxSteps : 4));
        await measureRunner.RunAsync(new WorkerRequest { TaskPrompt = "Read the big file." },
                                     CancellationToken.None);
        var firstRequest = measureFake.RequestLog[0];
        var firstEstimate = TokenEstimator.EstimateMessages(firstRequest.Messages)
                          + (firstRequest.Tools is { Count: > 0 }
                                ? TokenEstimator.EstimateToolDefinitions(firstRequest.Tools)
                                : 0);
        Assert.True(firstEstimate > 0);

        var fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"docs/a.md"}""", "c1"));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root,
                                      Options(maxSteps : 4) with { MaxCumulativeContextTokens = firstEstimate });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Read the big file." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("Stopped at the configured context-token budget before the model produced a conclusion.",
                     result.StatusDetail);
        Assert.Equal(1, fake.Requests);
        Assert.Equal(1, result.Statistics.ModelRequests);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
    public async Task PerRequestContextBudget_ExactFirstRequestAllowed_OverLimitNeverReachesClient()
    {
        using var dir = new TestTempDir();
        var measureFake = new FakeChatClient();
        measureFake.Enqueue(FakeChatClient.Text(DraftJson("Measured.", "docs/a.md")));
        var measureRunner = new WorkerRunner(measureFake, dir.Root, Options(maxSteps : 2));
        await measureRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." }, CancellationToken.None);
        var request = measureFake.RequestLog[0];
        var estimate = TokenEstimator.EstimateMessages(request.Messages)
                     + TokenEstimator.EstimateToolDefinitions(request.Tools!);

        var exactFake = new FakeChatClient();
        exactFake.Enqueue(FakeChatClient.Text(DraftJson("Exact boundary.", "docs/a.md")));
        var exactRunner = new WorkerRunner(exactFake, dir.Root,
            Options(maxSteps : 2) with { MaxContextTokensPerRequest = estimate });
        var exact = await exactRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." },
                                               CancellationToken.None);
        Assert.Equal(WorkerResultStatus.Completed, exact.Status);
        Assert.Equal(1, exactFake.Requests);

        var blockedFake = new FakeChatClient();
        blockedFake.Enqueue(FakeChatClient.Text(DraftJson("Must not run.", "docs/a.md")));
        var blockedRunner = new WorkerRunner(blockedFake, dir.Root,
            Options(maxSteps : 2) with { MaxContextTokensPerRequest = estimate - 1 });
        var blocked = await blockedRunner.RunAsync(new WorkerRequest { TaskPrompt = "Measure context." },
                                                   CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, blocked.Status);
        Assert.Equal(0, blockedFake.Requests);
        Assert.Equal(0, blocked.Statistics.ModelRequests);
        Assert.Equal(string.Empty, blocked.Conclusion);
        Assert.True(WorkerResultValidator.Validate(blocked).IsValid);
    }

    [Fact]
    public async Task ModelResponseBudget_TextStreamStopsAtBoundary_MapsToIncomplete()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeChatClient();
        fake.Enqueue(FakeChatClient.Text("This response is deliberately long and is not JSON at all."));
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxModelResponseCharacters = 30 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Investigate." },
                                           CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("Stopped at the configured model-response budget before the model produced a conclusion.",
                     result.StatusDetail);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal(1, result.Statistics.ModelRequests);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
    public async Task ModelResponseBudget_IncompleteToolCallNotExecuted()
    {
        using var dir         = new TestTempDir();
        var       fake        = new FakeChatClient();
        var       longPattern = new string('A', 300);
        fake.Enqueue(FakeChatClient.ToolCall("search_text", $$"""{"pattern":"{{longPattern}}"}"""));
        fake.Enqueue(FakeChatClient.Text(DraftJson("Done.", "docs/a.md")));
        var runner = new WorkerRunner(fake, dir.Root, Options() with { MaxModelResponseCharacters = 200 });

        var result = await runner.RunAsync(new WorkerRequest { TaskPrompt = "Search." }, CancellationToken.None);

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("Stopped at the configured model-response budget before the model produced a conclusion.",
                     result.StatusDetail);
        // 截断的工具调用参数是非法 JSON，AgentLoop 在组装阶段即失败——调用没有执行，
        // 历史里没有任何 tool 消息，第二个脚本响应不会被消费。
        // The truncated tool-call arguments are invalid JSON, so the AgentLoop fails while
        // assembling the message — the call never executes, the history holds no tool messages and
        // the second scripted response is never consumed.
        Assert.Equal(0, result.Statistics.ToolCalls);
        Assert.Empty(ToolMessages(fake.RequestLog[^1]));
        Assert.Equal(1, fake.Requests);
        Assert.Equal(string.Empty, result.Conclusion);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("The worker result budget was exhausted before a conclusion could be delivered.",
                     result.StatusDetail);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Equal("docs/a.md", Assert.Single(result.Evidence).Path);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }

    [Fact]
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

        Assert.Equal(WorkerResultStatus.Incomplete, result.Status);
        Assert.Equal("The worker result budget was exhausted before a conclusion could be delivered.",
                     result.StatusDetail);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.Empty(result.Evidence);
        Assert.True(WorkerResultValidator.Validate(result).IsValid);
    }
}
