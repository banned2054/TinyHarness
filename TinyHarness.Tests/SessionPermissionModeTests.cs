using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Tests;

public class SessionPermissionModeTests
{
    [TestCase(SessionPermissionMode.ConfirmEveryAction, PermissionDecision.Ask, PermissionDecision.Ask,
              PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ReadOnly, PermissionDecision.Allow, PermissionDecision.Ask, PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ProjectWrite, PermissionDecision.Allow, PermissionDecision.Allow,
              PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.FullAccess, PermissionDecision.Allow, PermissionDecision.Allow,
              PermissionDecision.Allow)]
    [TestCase(SessionPermissionMode.ReviewEveryAction, PermissionDecision.Ask, PermissionDecision.Ask,
              PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ReadOnlyAutoReview, PermissionDecision.Allow, PermissionDecision.Ask,
              PermissionDecision.Ask)]
    [TestCase(SessionPermissionMode.ProjectWriteAutoReview, PermissionDecision.Allow, PermissionDecision.Allow,
              PermissionDecision.Ask)]
    public void ModesApplyDistinctReadWriteAndProcessPolicies(SessionPermissionMode mode,
                                                              PermissionDecision    read, PermissionDecision write,
                                                              PermissionDecision    process)
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        engine.SetMode(mode);
        Assert.That(engine.Decide(Preparation("filesystem.read", root.Root)), Is.EqualTo(read));
        Assert.That(engine.Decide(Preparation("filesystem.write", root.Root)), Is.EqualTo(write));
        Assert.That(engine.Decide(Preparation("process.execute", root.Root)), Is.EqualTo(process));
        Assert.That(engine.Decide(Preparation("filesystem.read", Path.GetDirectoryName(root.Root)!)),
                    Is.EqualTo(mode == SessionPermissionMode.FullAccess
                                   ? PermissionDecision.Allow
                                   : PermissionDecision.Ask));
    }

    [Test]
    public void PresetsNeverBypassInspectionWorkerRestrictionOrExplicitDenial()
    {
        using var root   = new TestTempDir();
        var       write  = Preparation("filesystem.write", root.Root);
        var       worker = new PermissionEngine(root.Root, denyNonReadOnlyCapabilities : true);
        worker.SetMode(SessionPermissionMode.FullAccess);
        Assert.That(worker.Decide(write), Is.EqualTo(PermissionDecision.Deny));
        var engine = new PermissionEngine(root.Root);
        engine.SetMode(SessionPermissionMode.FullAccess);
        engine.SetNonReadOnlyBlock("unknown effects");
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Deny));
        engine.SetNonReadOnlyBlock(null);
        engine.DenySession(write);
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SwitchingBackToReadOnlyDoesNotReuseEarlierGrants()
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        var       write  = Preparation("filesystem.write", root.Root);
        engine.GrantOnce(write);
        engine.GrantSession(write);
        engine.SetMode(SessionPermissionMode.ProjectWrite);
        engine.SetMode(SessionPermissionMode.ReadOnly);
        Assert.That(engine.TryConsumeOnce(write), Is.False);
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Ask));
        engine.GrantSession(write);
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Ask), "预设中的逐次审批不应被范围授权绕过。");
    }

    [Test]
    public async Task SelectionCommitsWithoutTurnAndSurvivesReopen()
    {
        using var root   = new TestTempDir();
        using var store  = new TestTempDir();
        var       engine = new PermissionEngine(root.Root);
        var       model  = new AnswerModel();
        using var host   = Host(root.Root, model, engine, id => new SessionStoreWriter(store.Root, id));
        await host.ChangePermissionModeAsync(SessionPermissionMode.ReadOnly);
        await host.ChangePermissionModeAsync(SessionPermissionMode.ProjectWriteAutoReview);
        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.That(snapshot!.PermissionSelection!.Mode, Is.EqualTo(SessionPermissionMode.ProjectWriteAutoReview));
        Assert.That(snapshot.PermissionSelection.Revision, Is.EqualTo(2));
        Assert.That(snapshot.Turns, Is.Empty);
        Assert.That(model.Calls, Is.Zero);
        var restored = SessionResume.Restore(snapshot, root.Root, "permission test");
        Assert.That(restored.Session.PermissionSelection, Is.EqualTo(snapshot.PermissionSelection));
        var revision = snapshot.CommitSequence;
        await host.ChangePermissionModeAsync(SessionPermissionMode.ProjectWriteAutoReview);
        Assert.That((await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None))!
                   .CommitSequence,
                    Is.EqualTo(revision), "相同模式不重复追加修订。");
    }

    [Test]
    public async Task FailedCommitDoesNotInstallNewMode()
    {
        using var root   = new TestTempDir();
        using var store  = new TestTempDir();
        var       fail   = false;
        var       engine = new PermissionEngine(root.Root);
        using var host = Host(root.Root, new AnswerModel(), engine,
                              id => new SessionStoreWriter(store.Root, id)
                              {
                                  InjectWriteFault = (_, phase) =>
                                  {
                                      if (fail && phase == "before-write")
                                          throw new IOException("injected permission save failure");
                                  }
                              });
        await host.ChangePermissionModeAsync(SessionPermissionMode.ReadOnly);
        fail = true;
        await Assert.CatchAsync(async () => await host.ChangePermissionModeAsync(SessionPermissionMode.FullAccess));
        Assert.That(host.Session.PermissionSelection!.Mode, Is.EqualTo(SessionPermissionMode.ReadOnly));
        Assert.That(engine.Mode, Is.EqualTo(SessionPermissionMode.ReadOnly));
    }

    [Test]
    public async Task ConfigurationRoundTripPreservesAllModesAndReviewer()
    {
        using var directory = new TestTempDir();
        var       path      = Path.Combine(directory.Root, "user.json");
        var settings = new PermissionSettings
        {
            DefaultMode     = SessionPermissionMode.ProjectWriteAutoReview,
            UseCurrentModel = false, ReviewerProfile = "review", ReviewerModel = "review-model"
        };
        await UserConfigStore.SaveAsync(path,
                                        new UserConfig { Settings = new UserConfigSettings { Permissions = settings } },
                                        default);
        var saved = (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!;
        Assert.That(saved.ComposerModes, Is.EqualTo(SessionPermissionModes.All));
        Assert.That(saved.DefaultMode, Is.EqualTo(settings.DefaultMode));
        Assert.That(saved.ReviewerModel, Is.EqualTo("review-model"));
        Assert.That(saved.UseCurrentModel, Is.False);
        await File.WriteAllTextAsync(path, """{"settings":{"permissions":{"defaultMode":"Unknown"}}}""");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await UserConfigStore.LoadAsync(path, default));
    }

    private static ToolPreparation Preparation(string capability, string target) => new()
    {
        ToolName  = "test", CallId                = "test-call", Capability = capability, Summary = "test",
        Arguments = new JsonObject(), TargetPaths = [target]
    };

    [Test]
    public async Task FullAccessDoesNotBypassRestoredInspectionWithoutInjectedEngine()
    {
        using var root     = new TestTempDir();
        var       tool     = new WriteTool();
        var       identity = new SessionModelIdentity("offline", "https://offline.invalid", default);
        var       session  = new ChatSession(Guid.NewGuid().ToString("N"), root.Root, identity);
        var       options  = new AgentOptions { Model = "offline", MaxAgentSteps = 3, DefaultToolTimeoutSeconds = 10 };
        using var host = new ChatSessionHost(session, new ConversationContext(), options,
                                             new WriteModel(), new ToolRegistry([tool]), "permission test",
                                             needsInspection : "unknown effects");
        await host.ChangePermissionModeAsync(SessionPermissionMode.FullAccess);
        await host.SubmitTurnAsync("write", default);
        Assert.That(host.InspectionPending, Is.True);
        Assert.That(tool.Executions, Is.Zero);
    }

    private static ChatSessionHost Host(string root, AnswerModel model, PermissionEngine engine,
                                        Func<string, ISessionStoreWriter> writer) => new(root,
        new SessionModelIdentity("offline", "https://offline.invalid", default),
        new AgentOptions { Model = "offline", MaxAgentSteps = 4, DefaultToolTimeoutSeconds = 10 }, model,
        new ToolRegistry([]), "permission test",
        engine, sessionWriterFactory : writer);

    private sealed class WriteTool : ITool
    {
        public int Executions { get; private set; }

        public ToolDefinition Definition { get; } = new()
            { Name = "write", Description = "test", Parameters = new JsonObject() };

        public ToolPreparation Prepare(ChatToolCall call) => Preparation("filesystem.write", Path.GetTempPath());

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(new ToolResult { Succeeded = true, Content = "written" });
        }
    }

    private sealed class WriteModel : IChatCompletionClient
    {
        private bool _called;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (!_called)
            {
                _called = true;
                yield return new ChatStreamEvent
                {
                    Kind       = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                    ToolCallId = "test-call", ToolCallFunctionName = "write", ToolCallArgumentsDelta = "{}"
                };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
            }
            else
            {
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
            }
        }
    }

    private sealed class AnswerModel : IChatCompletionClient
    {
        public int Calls { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            Calls++;
            await Task.CompletedTask;
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
        }
    }
}
