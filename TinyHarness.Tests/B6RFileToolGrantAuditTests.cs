using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         B6R 文件工具 grant 审计回归：四个文件工具的 preparation 恒携带
///         backend=workspace-file-tool 文件工具策略身份（backend=workspace-file-tool + 工作区规范根，
///         守卫路径追加 workspace-access 段）；持久会话批准 apply_patch 的 L2 元数据写 → 授权执行与
///         permission-grant-event/1 落盘兼得，记录的 policyIdentityRevision 以文件工具身份开头；
///         Shell 路径策略身份构造零改动（既有 ShellTool 断言回归佐证）。
///     </para>
///     <para>
///         The B6R file-tool grant audit regression: all four file tools' preparations always carry the
///         workspace-file-tool policy identity (backend=workspace-file-tool plus the canonical workspace
///         root; the guard path appends the workspace-access section); a persisted session approving an
///         apply_patch L2 metadata write gains both the authorized execution and the persisted
///         permission-grant-event/1, whose policyIdentityRevision starts with the file-tool identity; the
///         shell path's identity construction is unchanged (pinned by the existing ShellTool assertions).
///     </para>
/// </summary>
public class B6RFileToolGrantAuditTests
{
    private const string IdentityPrefix = "backend=workspace-file-tool\nworkspace=";

    private const string SystemPrompt = "system prompt";

    private static readonly SessionModelIdentity Identity =
        new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
    {
        Model = Identity.ModelId, MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
    };

    // ---- 四文件工具 preparation 身份恒非空且同形态 ----

    [Test]
    public void FourFileTools_CarryTheWorkspaceFileToolIdentity_StableAndWorkspaceBound()
    {
        using var dir  = new TestTempDir();
        var       root = dir.Root;
        var applyPatch = new ApplyPatchTool(dir.Workspace);
        var readFile = new ReadFileTool(dir.Workspace);
        var listFiles = new ListFilesTool(dir.Workspace);
        var searchText = new SearchTextTool(dir.Workspace);

        var canonical = SandboxCapabilitySidStore.CanonicalRootKey(root);
        var applyPreparation = applyPatch.Prepare(PatchCall("call_1", """
                                                                      --- /dev/null
                                                                      +++ b/b6r.txt
                                                                      @@ -0,0 +1 @@
                                                                      +ok
                                                                      """));
        var readPreparation = readFile.Prepare(
            new ChatToolCall("call_2", "read_file", new JsonObject { ["path"] = "b6r.txt" }.ToJsonString()));
        var listPreparation = listFiles.Prepare(
            new ChatToolCall("call_3", "list_files", new JsonObject { ["path"] = "." }.ToJsonString()));
        var searchPreparation = searchText.Prepare(
            new ChatToolCall("call_4", "search_text",
                             new JsonObject { ["pattern"] = "needle", ["path"] = "." }.ToJsonString()));

        foreach (var preparation in new[] { applyPreparation, readPreparation, listPreparation, searchPreparation })
        {
            Assert.Multiple(() =>
            {
                Assert.That(preparation.ExecutionPolicy, Is.Not.Null.And.Not.Empty,
                            $"{preparation.ToolName} 的 preparation 必须携带策略身份。");
                Assert.That(preparation.ExecutionPolicy, Does.StartWith(IdentityPrefix));
                Assert.That(preparation.ExecutionPolicy, Does.Contain(canonical),
                            "身份绑定规范化的工作区根。");
            });
        }

        // 同工作区稳定，跨工作区变化（失效矩阵的绑定面）。
        Assert.That(readFile.Prepare(new ChatToolCall("again", "read_file",
                                                      new JsonObject { ["path"] = "b6r.txt" }.ToJsonString()))
                         .ExecutionPolicy,
                    Is.EqualTo(readPreparation.ExecutionPolicy));
        using var other = new TestTempDir();
        Assert.That(new ReadFileTool(other.Workspace)
                    .Prepare(new ChatToolCall("other", "read_file",
                                              new JsonObject { ["path"] = "x.txt" }.ToJsonString()))
                    .ExecutionPolicy,
                    Is.Not.EqualTo(readPreparation.ExecutionPolicy));
    }

    [Test]
    public void GuardedFileTool_AppendsTheWorkspaceAccessSection()
    {
        using var dir   = new TestTempDir();
        var       state = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var tool = state.Guard(() => new ApplyPatchTool(dir.Workspace));

        var preparation = tool.Prepare(PatchCall("call_1", """
                                                           --- /dev/null
                                                           +++ b/b6r.txt
                                                           @@ -0,0 +1 @@
                                                           +ok
                                                           """));

        Assert.Multiple(() =>
        {
            Assert.That(preparation.ExecutionPolicy, Does.StartWith(IdentityPrefix),
                        "守卫路径保留文件工具身份段。");
            Assert.That(preparation.ExecutionPolicy, Does.Contain("workspace-access="),
                        "守卫段追加 workspace-access 修订。");
        });
    }

    // ---- 持久会话：批准 apply_patch 的 .git 元数据写 → 执行与 grant-event 兼得 ----

    [Test]
    public async Task PersistedSession_ApprovedMetadataPatch_ExecutesAndPersistsGrantEvent()
    {
        using var dir    = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, ".git"));
        var tool = new ApplyPatchTool(dir.Workspace);
        var engine = new PermissionEngine(dir.Root, resourcePolicy : new TestResourcePolicy(dir.Root));
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("apply_patch", PatchJson("""
                                                                       --- /dev/null
                                                                       +++ b/.git/b6r-patched.txt
                                                                       @@ -0,0 +1 @@
                                                                       +B6R-CONTENT
                                                                       """)));
        client.Enqueue(FakeChatClient.Text("patched"));
        var approver = new FixedApprover(ApprovalResolution.Granted(GrantScope.Turn));
        // 显式会话/上下文 + 写入端绑定 recorder：commitInitialContext 先提交身份批，run 记录才能追加
        // （与生产 RunAsync 的接线同形）。
        var session = new ChatSession(Guid.NewGuid().ToString("N"), dir.Root, Identity);
        var context = new ConversationContext();
        context.Append(ChatMessage.System(SystemPrompt));
        var writer = new SessionStoreWriter(dir.Root, session.Id);
        // 生产接线：宿主经 sessionWriter 自建运行 recorder（轮边界与终态 grant-event drain 通道）。
        using var host = new ChatSessionHost(session, context, Options(), client, new ToolRegistry([tool]),
                                             SystemPrompt, engine, approver, sessionWriter : writer);

        var run = await host.SubmitTurnAsync("patch the metadata file", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed),
                    $"status={run.Result.Status} error={run.Result.Error} warning={host.DisplayWarning}");
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, ".git", "b6r-patched.txt")),
                    Does.Contain("B6R-CONTENT"),
                    "授权执行必须成功（B6R 断路修复的执行面）。");
        host.Dispose();

        var log = await File.ReadAllTextAsync(Path.Combine(dir.Root,
                                                           host.Session.Id + SessionStoreWriter.LogFileSuffix));
        var events = log.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(line => JsonSerializer.Deserialize(line,
                                             SessionLogJsonContext.Default.SessionLogBatch)!)
                        .SelectMany(batch => batch.Records)
                        .Where(record => record.Kind == "permission-grant-event")
                        .Select(record => record.Payload.Deserialize(PermissionPersistenceJsonContext.Default
                                                                        .PermissionGrantEventRecord)!)
                        .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(events.Any(record => record.Event == "granted" && record.Scope == "turn" &&
                                             record.Capability == "filesystem.write" &&
                                             record.PolicyIdentityRevision.StartsWith(IdentityPrefix,
                                                 StringComparison.Ordinal)),
                        Is.True, "批准文件工具授权必须落盘 granted 且策略修订以文件工具身份开头。");
            Assert.That(events.Any(record => record.Event == "expired" && record.Scope == "turn" &&
                                             record.PolicyIdentityRevision.StartsWith(IdentityPrefix,
                                                 StringComparison.Ordinal)),
                        Is.True, "运行终态到期事件同样携带文件工具身份。");
        });
    }

    // ---- Shell 路径零改动回归 ----

    [Test]
    public void ShellTool_IdentityConstruction_UnchangedByB6R()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, executionPolicy : new ProcessExecutionPolicy
        {
            Backend        = ProcessExecutionBackendKind.WindowsSandbox,
            DisplayName    = "windows-sandbox: workspace-write",
            PolicyIdentity = "backend=windows-sandbox\npolicyVersion=6\nkind=workspace-write"
        });

        var preparation = tool.Prepare(new ChatToolCall("call_1", "shell",
                                                        new JsonObject
                                                        {
                                                            ["mode"]       = "direct",
                                                            ["executable"] = "cmd.exe"
                                                        }.ToJsonString()));

        Assert.Multiple(() =>
        {
            Assert.That(preparation.ExecutionPolicy, Does.StartWith("backend=windows-sandbox"),
                        "Shell 策略身份构造不受 B6R 影响。");
            Assert.That(preparation.ExecutionPolicy, Does.Not.Contain("workspace-file-tool"));
        });
    }

    // ---- 工具 ----

    private static ChatToolCall PatchCall(string id, string patch) =>
        new(id, "apply_patch", PatchJson(patch));

    private static string PatchJson(string patch) =>
        new JsonObject { ["patch"] = patch }.ToJsonString();

    /// <summary>按脚本回复固定审批结论的审批器；null 表示自动拒绝（deny）。</summary>
    private sealed class FixedApprover(ApprovalResolution? resolution) : IApprovalProvider
    {
        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(resolution ?? ApprovalResolution.Denied("denied by test"));
    }
}
