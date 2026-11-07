using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
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
    [TestCase(SessionApprovalMode.HumanApproval)]
    [TestCase(SessionApprovalMode.AIAutoReview)]
    public void ApprovalModesDoNotChangeAuthorizationBaselines(SessionApprovalMode mode)
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root, resourcePolicy : new TestResourcePolicy(root.Root));
        engine.SetApprovalMode(mode);
        Assert.Multiple(() =>
        {
            // 基线 Level1 与审批模式无关：读写在沙箱基线内自动放行。
            Assert.That(engine.Evaluate(Preparation("filesystem.read", root.Root)).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Evaluate(Preparation("filesystem.write", root.Root)).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            Assert.That(engine.Evaluate(Preparation("process.execute", root.Root)).Decision,
                        Is.EqualTo(PermissionDecision.Allow));
            // 越界与宿主申请始终审批；宿主仅可单次授权。
            var outside = engine.Evaluate(Preparation("filesystem.write",
                                                      Path.GetDirectoryName(root.Root)!));
            Assert.Multiple(() =>
            {
                Assert.That(outside.Decision, Is.EqualTo(PermissionDecision.Ask));
                Assert.That(outside.Level, Is.EqualTo(PermissionLevel.Level2));
            });
            var host = engine.Evaluate(Preparation("process.execute", root.Root,
                                                   ProcessExecutionBackendKind.Host));
            Assert.Multiple(() =>
            {
                Assert.That(host.Decision, Is.EqualTo(PermissionDecision.Ask));
                Assert.That(host.Level, Is.EqualTo(PermissionLevel.Level3));
            });
        });
    }

    [Test]
    public void ApprovalModesNeverBypassInspectionWorkerRestrictionOrDenialMemory()
    {
        using var root   = new TestTempDir();
        var       policy = new TestResourcePolicy(root.Root);
        var       write  = Preparation("filesystem.write", root.Root);
        var worker = new PermissionEngine(root.Root, true,
                                          resourcePolicy : policy);
        worker.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        Assert.That(worker.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
        var engine = new PermissionEngine(root.Root, resourcePolicy : policy);
        engine.SetApprovalMode(SessionApprovalMode.HumanApproval);
        engine.SetNonReadOnlyBlock("unknown effects");
        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
        engine.SetNonReadOnlyBlock(null);
        engine.RecordDenial(write);
        Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SwitchingApprovalModeRevokesEarlierGrants()
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root, resourcePolicy : new TestResourcePolicy(root.Root));
        var       write  = Preparation("filesystem.write", Path.GetDirectoryName(root.Root)!);
        engine.Grants.Add(GrantScope.Turn, write);
        engine.Grants.Add(GrantScope.Session, write);
        engine.SetApprovalMode(SessionApprovalMode.AIAutoReview);
        engine.SetApprovalMode(SessionApprovalMode.HumanApproval);
        Assert.Multiple(() =>
        {
            Assert.That(engine.Evaluate(write).Decision, Is.EqualTo(PermissionDecision.Ask),
                        "切换审批模式撤销活动授权。");
            Assert.That(engine.Grants.SnapshotGrants().Where(grant => grant.State == GrantState.Active),
                        Is.Empty);
        });
    }

    [Test]
    public async Task SelectionCommitsWithoutTurnAndSurvivesReopen()
    {
        using var root   = new TestTempDir();
        using var store  = new TestTempDir();
        var       engine = new PermissionEngine(root.Root, resourcePolicy : new TestResourcePolicy(root.Root));
        var       model  = new AnswerModel();
        using var host   = Host(root.Root, model, engine, id => new SessionStoreWriter(store.Root, id));
        await host.ChangeApprovalModeAsync(SessionApprovalMode.HumanApproval);
        await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);
        var snapshot = await SessionStoreReader.ReadAsync(store.Root, host.Session.Id, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.PermissionSelectionV2!.ApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(snapshot.PermissionSelectionV2.Revision, Is.EqualTo(2));
            Assert.That(snapshot.Turns, Is.Empty);
            Assert.That(model.Calls, Is.Zero);
        });
        var restored = SessionResume.Restore(snapshot!, root.Root, "permission test");
        Assert.That(restored.Session.PermissionSelectionV2, Is.EqualTo(snapshot!.PermissionSelectionV2));
        var revision = snapshot.CommitSequence;
        await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);
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
        var       engine = new PermissionEngine(root.Root, resourcePolicy : new TestResourcePolicy(root.Root));
        using var host = Host(root.Root, new AnswerModel(), engine,
                              id => new SessionStoreWriter(store.Root, id)
                              {
                                  InjectWriteFault = (_, phase) =>
                                  {
                                      if (fail && phase == "before-write")
                                          throw new IOException("injected permission save failure");
                                  }
                              });
        await host.ChangeApprovalModeAsync(SessionApprovalMode.HumanApproval);
        fail = true;
        await Assert.CatchAsync(async () =>
                                    await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview));
        Assert.Multiple(() =>
        {
            Assert.That(host.Session.PermissionSelectionV2!.ApprovalMode,
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(engine.ApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        });
    }

    [Test]
    public async Task ConfigurationRoundTripPreservesAllModesAndReviewer()
    {
        using var directory = new TestTempDir();
        var       path      = Path.Combine(directory.Root, "user.json");
        var settings = new PermissionSettings
        {
            DefaultApprovalMode = SessionApprovalMode.AIAutoReview,
            UseCurrentModel     = false,
            ReviewerProfile     = "review",
            ReviewerModel       = "review-model"
        };
        await UserConfigStore.SaveAsync(path,
                                        new UserConfig { Settings = new UserConfigSettings { Permissions = settings } },
                                        default);
        var saved = (await UserConfigStore.LoadAsync(path, default)).Settings!.Permissions!;
        Assert.That(saved.DefaultApprovalMode, Is.EqualTo(settings.DefaultApprovalMode));
        Assert.That(saved.ReviewerModel, Is.EqualTo("review-model"));
        Assert.That(saved.UseCurrentModel, Is.False);
        await File.WriteAllTextAsync(path, """{"settings":{"permissions":{"defaultMode":"Unknown"}}}""");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await UserConfigStore.LoadAsync(path, default));
    }

    private static ToolPreparation Preparation(string                       capability, string target,
                                               ProcessExecutionBackendKind? requestedBackend = null) => new()
    {
        ToolName         = "test",
        CallId           = "test-call",
        Capability       = capability,
        Summary          = "test",
        Arguments        = new JsonObject(),
        RequestedBackend = requestedBackend,
        TargetPaths      = [target]
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
        await host.ChangeApprovalModeAsync(SessionApprovalMode.HumanApproval);
        await host.SubmitTurnAsync("write", default);
        Assert.That(host.InspectionPending, Is.True);
        Assert.That(tool.Executions, Is.Zero);
    }

    private static ChatSessionHost Host(string root, AnswerModel model, PermissionEngine engine,
                                        Func<string, ISessionStoreWriter>? writer) => new(root,
        new SessionModelIdentity("offline", "https://offline.invalid", default),
        new AgentOptions { Model = "offline", MaxAgentSteps = 4, DefaultToolTimeoutSeconds = 10 }, model,
        new ToolRegistry([]), "permission test",
        engine, sessionWriterFactory : writer);

    [Test]
    public async Task ChangeApprovalModeSwitchesEngineWithoutModelCalls()
    {
        using var root   = new TestTempDir();
        var       engine = new PermissionEngine(root.Root, resourcePolicy : new TestResourcePolicy(root.Root));
        var       model  = new AnswerModel();
        using var host   = Host(root.Root, model, engine, null);
        var       mode   = await host.ChangeApprovalModeAsync(SessionApprovalMode.AIAutoReview);
        Assert.Multiple(() =>
        {
            Assert.That(mode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(engine.ApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(model.Calls, Is.Zero);
        });
    }

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
