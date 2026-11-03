using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

[NonParallelizable]
public class ProjectInstructionDiagnosticLogTests
{
    [SetUp]
    public void Reset() => ProjectInstructionDiagnosticLog.ResetForTests();

    [TearDown]
    public void Clear() => ProjectInstructionDiagnosticLog.ResetForTests();

    [Test]
    public async Task DisabledLibraryUseDoesNotCreateFiles()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        await ProjectInstructionDiagnosticLog.EventAsync("event=ignored");
        Assert.That(Directory.GetFiles(logs.Root), Is.Empty);
    }

    [Test]
    public async Task DebugLevelIsFilteredBeforeCreatingAReleaseFile()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        await ProjectInstructionDiagnosticLog.EventAsync("event=debug-only", level: ProjectInstructionLogLevel.Debug);
#if DEBUG
        Assert.That(Directory.GetFiles(logs.Root), Has.Length.EqualTo(1));
#else
        Assert.That(Directory.GetFiles(logs.Root), Is.Empty);
#endif
        await ProjectInstructionDiagnosticLog.EventAsync("event=important", level: ProjectInstructionLogLevel.Warning);
        var text = await File.ReadAllTextAsync(Directory.GetFiles(logs.Root).Single());
        Assert.That(text, Does.Contain("level=Warning|event=important"));
#if !DEBUG
        Assert.That(text, Does.Not.Contain("level=Debug").And.Not.Contain("debug-only"));
        Assert.That(text, Does.Contain("seq=0|level=Warning"));
#endif
    }

    [Test]
    public async Task RoutineToolExecutionKeepsOnlyImportantEventsInRelease()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "root rule");
        workspace.WriteFile("data.txt", "value");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"data.txt"}""", "read"));
        client.Enqueue(FakeChatClient.Text("done"));
        using var host = CreateHost(workspace, client);
        var run = await host.SubmitTurnAsync("task", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
        var lines = await File.ReadAllLinesAsync(Directory.GetFiles(logs.Root).Single());
        var text = string.Join('\n', lines);
        Assert.That(text, Does.Contain("event=applied|").And.Contain("decision=Completed"));
#if DEBUG
        Assert.That(text, Does.Contain("decision=proceed").And.Contain("decision=reset-deferrals"));
#else
        Assert.That(lines, Has.Length.EqualTo(3));
        Assert.That(text, Does.Not.Contain("level=Debug").And.Not.Contain("event=check|"));
        Assert.That(text, Does.Not.Contain("requested_scopes=").And.Not.Contain("sha256="));
        Assert.That(text, Does.Not.Contain("decision=proceed").And.Not.Contain("decision=reset-deferrals"));
#endif
    }

    [Test]
    public async Task ConcurrentEventsKeepSingleLinesSequenceAndRedactionInBothConfigurations()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        var redactor = new SecretRedactor(new Dictionary<string, string> { ["fixture"] = "synthetic-value" });
        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
                                                              ProjectInstructionDiagnosticLog
                                                                 .EventAsync($"event=test|index={index}|path=synthetic-value\r\nsecond-line",
                                                                             redactor)));
        var file = Directory.GetFiles(logs.Root, "project-instructions-*.log").Single();
        var lines = await File.ReadAllLinesAsync(file);
        Assert.That(lines.Length, Is.EqualTo(21));
#if DEBUG
        Assert.That(lines[0], Does.Contain("build=Debug"));
#else
        Assert.That(lines[0], Does.Contain("build=Release"));
#endif
        for (var index = 0; index < 20; index++) Assert.That(lines[index + 1], Does.Contain($"|seq={index}|"));
        Assert.That(string.Join('\n', lines),
                    Does.Contain("[REDACTED]\\r\\nsecond-line").And.Not.Contain("synthetic-value"));
        await ProjectInstructionDiagnosticLog.EventAsync("event=test|password=private-value");
        Assert.That(await File.ReadAllTextAsync(file), Does.Not.Contain("private-value"));
    }

    [Test]
    public async Task ReadOnlyOrInvalidDestinationDoesNotChangeRunOutcome()
    {
        using var directory = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests =
            directory.WriteFile("not-a-directory", "fixture");
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "rule");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        using var host = CreateHost(workspace, client);
        var run = await host.SubmitTurnAsync("task", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(ProjectInstructionDiagnosticLog.IsEnabled, Is.False);
        Assert.That(ProjectInstructionDiagnosticLog.FailureType, Is.Not.Null);
    }

    [Test]
    public async Task LineAndFileBudgetsStopLoggingWithAnExplicitMarker()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        for (var index = 0; index < 270; index++)
            await ProjectInstructionDiagnosticLog.EventAsync("event=bounded|value=" + new string('x', 40_000));
        var file = Directory.GetFiles(logs.Root, "project-instructions-*.log").Single();
        Assert.That(new FileInfo(file).Length, Is.LessThanOrEqualTo(8 * 1024 * 1024));
        var lines = await File.ReadAllLinesAsync(file);
        Assert.That(lines.Any(line => line.EndsWith("...<truncated>", StringComparison.Ordinal)), Is.True);
        Assert.That(lines.Last(), Does.Contain("event=log-limit").And.Contain("logging_stopped=true"));
    }

    [Test]
    public async Task ScopeSwitchRecordsBothRevisionsAndTheActualStopWithoutLoggingBodiesOrSearchTerms()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        const string key = "synthetic-key";
        workspace.WriteFile("AGENTS.md", "ROOT_RULE_BODY_MUST_NOT_BE_LOGGED");
        workspace.WriteFile(key + "/AGENTS.md", "LOCAL_RULE_BODY_MUST_NOT_BE_LOGGED");
        workspace.WriteFile(key + "/data.txt", "FILE_BODY_MUST_NOT_BE_LOGGED");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"synthetic-key/data.txt"}""", "local"));
        client.Enqueue(FakeChatClient.ToolCall("search_text",
                                               """{"path":".","pattern":"SEARCH_TERM_MUST_NOT_BE_LOGGED"}""", "root"));
        using var host = CreateHost(workspace, client, new Dictionary<string, string> { ["fixture"] = key });
        var run = await host.SubmitTurnAsync("USER_MESSAGE_MUST_NOT_BE_LOGGED", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(run.Result.Error, Does.Contain("already deferred once"));
        var lines = await File.ReadAllLinesAsync(Directory.GetFiles(logs.Root, "project-instructions-*.log").Single());
        var checks = lines.Where(line => line.Contains("event=check|") && line.Contains("phase=tool-preflight|"))
                          .ToArray();
#if DEBUG
        Assert.That(checks.Length, Is.EqualTo(2));
        Assert.That(checks.All(line => line.Contains("scope_set_changed=True|")), Is.True);
        Assert.That(checks.All(line => line.Contains("shared_content_changed=|")), Is.True);
        Assert.That(checks[0], Does.Contain("read_file:File:[REDACTED]").And.Contain("sha256="));
        Assert.That(checks[1], Does.Contain("search_text:RecursiveDirectory:.").And.Contain("previous_revision="));
#else
        Assert.That(checks, Is.Empty);
        Assert.That(string.Join('\n', lines), Does.Not.Contain("level=Debug").And.Not.Contain("sha256="));
        Assert.That(string.Join('\n', lines), Does.Not.Contain("previous_sources=").And.Not.Contain("candidate_sources="));
#endif
        var text = string.Join('\n', lines);
        Assert.That(text, Does.Contain($"session={host.Session.Id}").And.Contain($"run={run.Id}"));
        Assert.That(text,
                    Does.Contain("decision=retry|deferrals=0").And.Contain("decision=stop-deferral-limit|deferrals=1"));
        Assert.That(lines.Last(), Does.Contain("phase=run-finished|decision=Failed"));
        Assert.That(lines.Last(), Does.Contain("level=Error"));
        Assert.That(text, Does.Not.Contain("MUST_NOT_BE_LOGGED").And.Not.Contain(key));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BudgetAdjustmentsAreImportantEventsInBothConfigurations(bool sourceLimit)
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        workspace.WriteFile("AGENTS.md", new string('x', sourceLimit ? 1024 * 1024 + 1 : 100_000));
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("done"));
        using var host = CreateHost(workspace, client);
        var run = await host.SubmitTurnAsync("task", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        var lines = await File.ReadAllLinesAsync(Directory.GetFiles(logs.Root).Single());
        Assert.That(lines.Single(line => line.Contains("event=budget|")),
                    Does.Contain("level=Warning").And
                        .Contain(sourceLimit ? "source-byte-limit" : "truncated AGENTS.md"));
        Assert.That(lines.Last(), Does.Contain("level=Information").And.Contain("decision=Completed"));
#if !DEBUG
        Assert.That(string.Join('\n', lines), Does.Not.Contain("event=check|").And.Not.Contain("sha256="));
#endif
    }

    [Test]
    public async Task RootLoadFailureRecordsItsPhaseAndTypeWithoutCallingTheModel()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        workspace.WriteBytes("AGENTS.md", [0xFF]);
        var client = new FakeChatClient();
        using var host = CreateHost(workspace, client);
        var run = await host.SubmitTurnAsync("task", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(client.Requests, Is.Zero);
        var text = await File.ReadAllTextAsync(Directory.GetFiles(logs.Root, "project-instructions-*.log").Single());
        Assert.That(text, Does.Contain("phase=turn-start|status=failed|error_type=InvalidDataException"));
        Assert.That(text, Does.Contain("phase=run-finished|decision=Failed"));
    }

    [Test]
    public async Task ContentHashChangesAreDistinctFromScopeChanges()
    {
        using var logs = new TestTempDir();
        ProjectInstructionDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        ProjectInstructionDiagnosticLog.Initialize();
        using var workspace = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "FIRST_PRIVATE_BODY");
        workspace.WriteFile("data.txt", "value");
        var client = new FakeChatClient
        { CancelMidStream = () => workspace.WriteFile("AGENTS.md", "SECOND_PRIVATE_BODY") };
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"data.txt"}""", "old"));
        client.Enqueue(FakeChatClient.Text("done"));
        using var host = CreateHost(workspace, client);
        await host.SubmitTurnAsync("task", CancellationToken.None);
        var text = await File.ReadAllTextAsync(Directory.GetFiles(logs.Root, "project-instructions-*.log").Single());
#if DEBUG
        Assert.That(text, Does.Contain("scope_set_changed=False|").And.Contain("shared_content_changed=AGENTS.md|"));
#else
        Assert.That(text, Does.Not.Contain("event=check|").And.Not.Contain("shared_content_changed="));
        Assert.That(text, Does.Contain("event=applied|").And.Contain("decision=retry"));
        Assert.That(text, Does.Not.Contain("decision=proceed").And.Not.Contain("decision=reset-deferrals"));
#endif
        Assert.That(text, Does.Not.Contain("PRIVATE_BODY"));
    }

    private static ChatSessionHost CreateHost(TestTempDir workspace, FakeChatClient client,
                                              IReadOnlyDictionary<string, string>? secrets = null) =>
        new(workspace.Root,
            new SessionModelIdentity("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions),
            new AgentOptions { Model = "test-model", MaxAgentSteps = 5, DefaultToolTimeoutSeconds = 30 }, client,
            new ToolRegistry([new ReadFileTool(workspace.Workspace), new SearchTextTool(workspace.Workspace)]),
            "system",
            instructionLoader: new ProjectInstructionLoader(workspace.Workspace), knownSecrets: secrets);
}
