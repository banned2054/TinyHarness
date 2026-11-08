using System.Text.Json.Nodes;
using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         组合授权指纹守卫契约：指纹覆盖成分与稳定性、发送入口与工具派发检查点的失配动作
///         （撤销授权、闩死会话、中文提示）、取值失败同路径、无关配置变化不失效。
///     </para>
///     <para>
///         The composition-guard contract: fingerprint coverage and stability, mismatch actions at the
///         send-entry and per-dispatch checkpoints (grant revocation, session latch, end-to-end notice),
///         evaluator failures sharing the mismatch path, and unrelated config changes never invalidating.
///     </para>
/// </summary>
public class PermissionCompositionGuardTests
{
    private const string SystemPrompt = "system prompt";

    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private AgentOptions Options() => new()
    {
        Model = Identity.ModelId, MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
    };

    // ---- 指纹派发检查点：失配撤销授权并闩死会话 ----

    [Test]
    public async Task FingerprintMismatchAtDispatch_BlocksSecondDispatch_AndInvalidatesSession()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));

        // 预置活动 Session 授权与一次性派发凭据：失配撤销后必须公开可观察地失效。
        var grantPreparation = new ApplyPatchTool(dir.Workspace)
                              .Prepare(new ChatToolCall("prep", "apply_patch", CreatePatchJson("grant-holder.txt")));
        engine.Grants.Add(GrantScope.Session, grantPreparation, "additional-path-write");
        Assert.That(engine.Grants.SnapshotGrants(), Has.Count.EqualTo(1));
        var dispatchCredential = engine.CreateDispatchAuthorization(grantPreparation,
                                                                    engine.AuthorizationRevision);

        // 取值器：首个工具的哨兵文件出现即代表已真实执行，此后返回漂移指纹。
        var sentinel = Path.Combine(dir.Root, "first-patch.txt");
        const string baseline = "composition-baseline";
        const string drifted  = "drifted-fingerprint";
        string Evaluate() => File.Exists(sentinel) ? drifted : baseline;

        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", CreatePatchJson("first-patch.txt"), "p1"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", CreatePatchJson("second-patch.txt"), "p2"));
        client.Enqueue(FakeChatClient.Text("done"));

        using var host = new ChatSessionHost(dir.Root, Identity, Options(), client,
                                             new ToolRegistry([new ApplyPatchTool(dir.Workspace)]),
                                             SystemPrompt, engine,
                                             compositionGuard : new PermissionCompositionGuard(baseline, Evaluate));

        var run = await host.SubmitTurnAsync("apply two patches", CancellationToken.None);

        Assert.Multiple(() =>
        {
            // 第二次派发被阻断：运行失败、提示端到端可见、第二个工具未执行。
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
            Assert.That(run.Result.Error, Does.Contain(ChatSessionHost.CompositionInvalidatedNotice));
            Assert.That(File.Exists(sentinel), Is.True, "首个工具必须已真实执行。");
            Assert.That(File.Exists(Path.Combine(dir.Root, "second-patch.txt")), Is.False,
                        "第二个工具不得执行。");
            Assert.That(run.Result.ToolCalls!.Single(record => record.CallId == "p2").NotStartedReason,
                        Does.Contain(ChatSessionHost.CompositionInvalidatedNotice),
                        "被阻断调用须向会话携带中文提示。");
            // 引擎公开行为：活动 grant 被清（快照含历史条目，按状态过滤）；一次性派发凭据复核拒绝。
            Assert.That(engine.Grants.SnapshotGrants().Where(grant => grant.State == GrantState.Active),
                        Is.Empty, "活动 grant 必须已被撤销。");
            Assert.That(engine.RevalidateForDispatch(grantPreparation, null, dispatchCredential).Decision,
                        Is.EqualTo(PermissionDecision.Deny),
                        "已签发的一次性派发凭据必须在撤销后失效。");
        });

        // 闩置位：后续发送快速失败，不再创建回合记录。
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        host.SubmitTurnAsync("再发一次", CancellationToken.None));
        Assert.Multiple(() =>
        {
            Assert.That(again!.Message, Is.EqualTo(ChatSessionHost.CompositionInvalidatedNotice));
            Assert.That(host.Session.Turns, Has.Count.EqualTo(1), "闩死后不得再创建回合记录。");
        });
    }

    // ---- 发送入口检查点：失配阻断本次发送 ----

    [Test]
    public async Task FingerprintMismatchAtSendEntry_BlocksSubmission_WithNotice()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("should never run"));

        using var host = new ChatSessionHost(dir.Root, Identity, Options(), client, new ToolRegistry([]),
                                             SystemPrompt, engine,
                                             compositionGuard : new PermissionCompositionGuard(
                                                 "composition-baseline", () => "drifted-fingerprint"));

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                          host.SubmitTurnAsync("hello", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(blocked!.Message, Is.EqualTo(ChatSessionHost.CompositionInvalidatedNotice));
            Assert.That(host.Session.Turns, Is.Empty, "失配发送不得创建回合记录。");
            Assert.That(engine.AuthorizationRevision, Is.GreaterThan(0), "失配必须撤销授权（递增修订）。");
        });
    }

    // ---- 取值失败：异常或 null 一律按失配处理 ----

    [Test]
    public async Task EvaluatorFailure_IsTreatedAsMismatch(
        [Values(EvaluatorFailureKind.Throws, EvaluatorFailureKind.ReturnsNull)] EvaluatorFailureKind kind)
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.Text("should never run"));

        Func<string?> evaluator = kind == EvaluatorFailureKind.Throws
            ? () => throw new IOException("config unavailable")
            : () => null;
        using var host = new ChatSessionHost(dir.Root, Identity, Options(), client, new ToolRegistry([]),
                                             SystemPrompt, engine,
                                             compositionGuard : new PermissionCompositionGuard(
                                                 "composition-baseline", evaluator));

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                          host.SubmitTurnAsync("hello", CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(blocked!.Message, Is.EqualTo(ChatSessionHost.CompositionInvalidatedNotice));
            Assert.That(engine.AuthorizationRevision, Is.GreaterThan(0));
        });
    }

    // ---- 指纹一致：发送与派发不受影响（含多次） ----

    [Test]
    public async Task StableFingerprint_AllowsRepeatedTurnsAndDispatches()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", CreatePatchJson("a.txt"), "p1"));
        client.Enqueue(FakeChatClient.Text("first done"));
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", CreatePatchJson("b.txt"), "p2"));
        client.Enqueue(FakeChatClient.Text("second done"));

        const string fingerprint = "stable-fingerprint";
        using var host = new ChatSessionHost(dir.Root, Identity, Options(), client,
                                             new ToolRegistry([new ApplyPatchTool(dir.Workspace)]),
                                             SystemPrompt, engine,
                                             compositionGuard : new PermissionCompositionGuard(
                                                 fingerprint, () => fingerprint));

        var first  = await host.SubmitTurnAsync("write a", CancellationToken.None);
        var second = await host.SubmitTurnAsync("write b", CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Result!.Status,  Is.EqualTo(AgentStatus.Completed));
            Assert.That(second.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(File.Exists(Path.Combine(dir.Root, "a.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(dir.Root, "b.txt")), Is.True);
            Assert.That(engine.AuthorizationRevision, Is.EqualTo(0), "指纹一致不得撤销授权。");
        });
    }

    // ---- 无关配置变化：指纹稳定 ----

    [Test]
    public void UnrelatedPermissionAndCatalogChanges_KeepFingerprintStable()
    {
        var workspace = NewTempPath("fingerprint-ws");
        var sandbox   = new WindowsSandboxSettings
        {
            Enabled              = true,
            Policy               = SandboxPolicyKind.WorkspaceWrite,
            NetworkPolicy        = SandboxNetworkPolicy.Restricted,
            AdditionalWriteRoots = [workspace]
        };
        var plain = new PermissionSettings { DefaultApprovalMode = SessionApprovalMode.AIAutoReview };
        var edited = new PermissionSettings
        {
            DefaultApprovalMode = SessionApprovalMode.HumanApproval,
            ReviewerProfile     = "reviewer",
            ReviewerModel       = "review-model"
        };

        var baseline = PermissionCompositionFingerprint.Compute(sandbox, plain, null, workspace,
                                                                sandboxComposed : false, [], null);
        var afterEdit = PermissionCompositionFingerprint.Compute(sandbox, edited, null, workspace,
                                                                 sandboxComposed : false, [], null);

        // 审批模式选择与评审模型不属于常设授权：变化不得触发失效。
        Assert.That(afterEdit, Is.EqualTo(baseline));
    }

    [Test]
    public async Task UnrelatedConfigFileEdits_KeepEvaluatorStable()
    {
        var configPath = Path.Combine(NewTempPath("fingerprint-cfg"), "user-config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await UserConfigStore.SaveAsync(configPath, ConfigWithModel("model-a"), CancellationToken.None);
        var evaluator = PermissionCompositionFingerprint.CreateEvaluator(configPath,
                                                                         Path.GetDirectoryName(configPath)!,
                                                                         sandboxComposed : false, [],
                                                                         trackSessionStoreDirectory : false);
        var baseline = evaluator();

        // 无关段落（模型列表）与显示性权限字段变化：取值器输出必须逐字节稳定。
        await UserConfigStore.SaveAsync(configPath, ConfigWithModel("model-b"), CancellationToken.None);
        var afterCatalogEdit = evaluator();
        await UserConfigStore.SaveAsync(configPath, ConfigWithModel("model-a", SessionApprovalMode.HumanApproval),
                                        CancellationToken.None);
        var afterPermissionEdit = evaluator();

        Assert.Multiple(() =>
        {
            Assert.That(afterCatalogEdit, Is.EqualTo(baseline));
            Assert.That(afterPermissionEdit, Is.EqualTo(baseline));
        });
    }

    // ---- 真实链路：常设信任确认记录变化使指纹不同 ----

    [Test]
    public void StandingTrustConfirmationTransition_ChangesFingerprint()
    {
        var workspace = NewTempPath("standing-trust-ws");
        var sandbox   = new WindowsSandboxSettings
        {
            Enabled              = true,
            NetworkPolicy        = SandboxNetworkPolicy.Enabled,
            SetupExecutablePath  = Path.Combine(workspace, "setup.exe"),
            RunnerExecutablePath = Path.Combine(workspace, "runner.exe")
        };
        var unconfirmed = new PermissionSettings();
        var confirmed   = new PermissionSettings
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                      StandingTrustEvaluation.ScopeRevision(
                                                                          sandbox))
        };

        var before = PermissionCompositionFingerprint.Compute(sandbox, unconfirmed, null, workspace,
                                                              sandboxComposed : false, [], null);
        var after = PermissionCompositionFingerprint.Compute(sandbox, confirmed, null, workspace,
                                                             sandboxComposed : false, [], null);

        // 链路一致性：删除确认记录使 Confirmed→Unconfirmed，指纹随之不同（撤销常设授权即触发守卫）。
        Assert.Multiple(() =>
        {
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox, confirmed),
                        Is.EqualTo(StandingTrustState.Confirmed));
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox, unconfirmed),
                        Is.EqualTo(StandingTrustState.Unconfirmed));
            Assert.That(after, Is.Not.EqualTo(before));
        });
    }

    [Test]
    public void RootOrderAndCasing_DoNotChangeFingerprint()
    {
        var workspace = NewTempPath("root-normalization-ws");
        var ordered = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = [workspace, Path.Combine(workspace, "sub")],
            AdditionalReadRoots  = []
        };
        var shuffled = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = [workspace.ToLowerInvariant(), Path.Combine(workspace, "SUB").ToUpperInvariant()],
            AdditionalReadRoots  = []
        };

        var left  = PermissionCompositionFingerprint.Compute(ordered, null, null, workspace,
                                                             sandboxComposed : false, [], null);
        var right = PermissionCompositionFingerprint.Compute(shuffled, null, null, workspace,
                                                             sandboxComposed : false, [], null);

        Assert.That(right, Is.EqualTo(left));
    }

    // ---- 助手 ----

    private static string CreatePatchJson(string relativePath) => new JsonObject
    {
        ["patch"] = $"--- /dev/null\n+++ b/{relativePath}\n@@ -0,0 +1 @@\n+guard-content"
    }.ToJsonString();

    private static string NewTempPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"tinyharness-{label}-{Guid.NewGuid():N}");

    private static UserConfig ConfigWithModel(
        string modelId, SessionApprovalMode approvalMode = SessionApprovalMode.AIAutoReview) => new()
    {
        DefaultProfile = "offline",
        Profiles =
        [
            new UserProfile
            {
                Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                DefaultModel = modelId,
                Models = [new UserProfileModel { Id = modelId, ContextWindowTokens = 128_000 }]
            }
        ],
        Settings = new UserConfigSettings
        {
            Permissions = new PermissionSettings { DefaultApprovalMode = approvalMode }
        }
    };

    /// <summary>取值器失败形态：抛异常或返回 null，两者都必须走失配路径。</summary>
    public enum EvaluatorFailureKind
    {
        Throws,
        ReturnsNull
    }
}
