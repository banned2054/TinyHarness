using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         审批展示与 Reviewer 授权上下文单一来源（B6-2/B6-4）：GrantHistoryEntry.Describe 的字段顺序、
///         目标摘要与预算截断、约束截断、有效期与策略修订（身份串 SHA-256 前 16 hex）；
///         ApprovalDisplay.DescribeLines 的后端隔离声明（L2 同身份/L3 无隔离）、文件权限（广泛读基线+
///         申请写根）、网络权限 F6 精确措辞、有效期与活动授权摘要；CLI fact 行打印（脱敏前缀）；
///         ApprovalRequest/DesktopApproval 可选 DisplayNotes 的旧构造兼容。
///     </para>
///     <para>
///         The single sources of approval display and the reviewer authorization context (B6-2/B6-4):
///         GrantHistoryEntry.Describe field order, target summaries and budget truncation, the constraint cut,
///         expiry and policy revision (the first 16 hex of the identity's SHA-256); ApprovalDisplay.DescribeLines
///         backend isolation declarations (L2 same identity / L3 no isolation), file permissions (broad read
///         baseline plus requested write roots), the exact F6 network wording, validity and active grant
///         summaries; the CLI fact-line printing (redaction prefix); and optional DisplayNotes constructor
///         compatibility on ApprovalRequest/DesktopApproval.
///     </para>
/// </summary>
public class ApprovalDisplayContractTests
{
    private static DateTimeOffset CreatedUtc =>
        DateTimeOffset.Parse("2026-11-08T01:02:03+00:00", CultureInfo.InvariantCulture);

    // ---- GrantHistoryEntry.Describe ----

    [Test]
    public void Describe_ShowsScopeCapabilityStateTargetsConstraintExpiryAndPolicyRevision()
    {
        var grant = Grant.Active("grant-1", GrantScope.Turn, Preparation([@"C:\outside\a.txt"],
                                 constraint : "cmd-identity\nexecution-policy: backend=windows-sandbox"),
                                 CreatedUtc, TimeSpan.FromHours(2)) with
        {
            PolicyIdentity = "backend=windows-sandbox\npolicyVersion=6\nkind=workspace-write"
        };

        var line = GrantHistoryEntry.Describe(grant);

        Assert.Multiple(() =>
        {
            Assert.That(line, Does.StartWith("grant turn process.execute state=Active;"));
            Assert.That(line, Does.Contain("targets=[C:\\outside\\a.txt]; "));
            Assert.That(line, Does.Contain("constraint=cmd-identity\nexecution-policy: backend=windows-sandbox; "));
            Assert.That(line, Does.Contain("expires=2026-11-08 03:02 UTC;"));
            Assert.That(line, Does.Contain("policyRevision=" +
                                           Revision(grant.PolicyIdentity)),
                        "策略修订=身份串 SHA-256 前 16 个十六进制字符。");
            Assert.That(line, Does.Not.Contain("…"), "预算内字段不截断。");
        });
    }

    [Test]
    public void Describe_TruncatesTargetsConstraintsAndMarksOverBudget()
    {
        var longTarget = @"C:\" + new string('t', 200);
        // 长目标放在前 6 条内参与展示；总数 7 条触发计数标注。
        var targets = new[] { longTarget }.Concat(Enumerable.Range(0, 6)
                                                            .Select(i => $@"C:\outside\dir{i}"))
                                   .ToArray();
        var grant = Grant.Active("grant-1", GrantScope.Session,
                                 Preparation(targets, constraint : new string('c', 200)), CreatedUtc) with
        {
            PolicyIdentity = null
        };

        var line = GrantHistoryEntry.Describe(grant);

        Assert.Multiple(() =>
        {
            Assert.That(line, Does.Contain("(+1 more)"), "超 6 条目标以计数标注。");
            Assert.That(line, Does.Contain(new string('c', 159) + "…"), "约束按 160 字符预算截断。");
            Assert.That(line, Does.Contain("policyRevision=(none)"), "缺失身份显示 (none)。");
            Assert.That(line, Does.Contain("expires=(never)"), "无到期显示 (never)。");
            // 超长目标按 120 字符预算截断（前 119 字符保留）。
            Assert.That(line, Does.Contain(longTarget[..119] + "…"));
        });
    }

    [Test]
    public void Describe_ShowsUnscopedTargetsForGrantsWithoutTargets()
    {
        var grant = Grant.Active("grant-1", GrantScope.SingleOperation,
                                 Preparation([], constraint : "cmd"), CreatedUtc);

        Assert.That(GrantHistoryEntry.Describe(grant), Does.Contain("targets=[(unscoped)]"));
    }

    // ---- ApprovalDisplay.DescribeLines ----

    [Test]
    public void DescribeLines_ShowsSandboxBackendWriteRootsF6NetworkValidityAndActiveGrants()
    {
        var preparation = Preparation([@"C:\workspace\cmd"], constraint : "cmd",
                                      requiresNetwork : true, backend : null,
                                      writeRoots : [@"C:\outside\root-one", @"C:\outside\root-two"]);
        var registry = new GrantRegistry();
        registry.Add(GrantScope.Turn, Preparation([@"C:\outside\granted"], constraint : "cmd"),
                     TimeSpan.FromHours(1));

        var lines = ApprovalDisplay.DescribeLines(preparation, PermissionLevel.Level2,
                                                  [GrantScope.SingleOperation, GrantScope.Turn],
                                                  registry.SnapshotGrants());

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo("backend: WindowsSandbox — same sandbox account identity, " +
                                             "non-administrator"),
                        "L2 隔离声明：同一沙箱身份、非管理员。");
            Assert.That(lines[1], Does.Contain("broad read baseline (not limited to the workspace)"));
            Assert.That(lines[1], Does.Contain("requested write roots: C:\\outside\\root-one, C:\\outside\\root-two"));
            Assert.That(lines[2], Does.Contain("full network inside the sandbox"));
            Assert.That(lines[2], Does.Contain("a broken proxy fails fast without direct fallback"));
            Assert.That(lines[2], Does.Contain("direct full network when no proxy is configured"),
                        "F6 精确措辞：经代理与直连全网两态。");
            Assert.That(lines[3], Does.Contain("allow once (this attempt only)"));
            Assert.That(lines[3], Does.Contain("turn (until this run ends)"));
            Assert.That(lines.Any(line => line == "active grants:"), Is.True);
            Assert.That(lines.Any(line => line.StartsWith("  grant turn ", StringComparison.Ordinal)),
                        Is.True, "活动授权摘要经 GrantHistoryEntry 单一来源。");
        });
    }

    [Test]
    public void DescribeLines_ShowsHostIsolationNoWriteRootsNoNetworkAndNoActiveGrants()
    {
        var preparation = Preparation([@"C:\workspace\a.txt"], constraint : "cmd",
                                      requiresNetwork : false, backend : ProcessExecutionBackendKind.Host);

        var lines = ApprovalDisplay.DescribeLines(preparation, PermissionLevel.Level3,
                                                  [GrantScope.SingleOperation], []);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Does.Contain("backend: HostProcess"));
            Assert.That(lines[0], Does.Contain("no OS sandbox isolation"));
            Assert.That(lines[0], Does.Contain("no UAC elevation"),
                        "L3 隔离声明：无 OS 隔离、按宿主账户实际权限、无 UAC。");
            // M1：显式宿主申请档的文件与网络行按宿主账户实际权限如实展示，
            // 不再输出沙箱网络行（PermissionShellTool host 路由已清除网络标志）。
            Assert.That(lines[1], Is.EqualTo(
                "file permissions: the host account's actual file permissions (no OS sandbox)"));
            Assert.That(lines[2], Is.EqualTo(
                "network: not sandboxed — follows the host account's actual network permissions"));
            Assert.That(lines.Any(line => line.Contains("sandbox network stays restricted")), Is.False,
                        "宿主申请档不得输出沙箱网络行。");
            Assert.That(lines[3], Is.EqualTo("validity: allow once (this attempt only)"));
            Assert.That(lines[4], Is.EqualTo("active grants: (none)"));
        });
    }

    // ---- H1 文件工具档：应用层可信边界声明 ----

    [Test]
    public void DescribeLines_FileToolTier_DeclaresApplicationLayerTrustedBoundary()
    {
        // 文件工具档：capability 非 process.execute（backend 语义不适用）。
        var preparation = Preparation([@"C:\workspace\a.cs"], constraint : "cmd",
                                      requiresNetwork : false, backend : null,
                                      capability : "filesystem.write");

        var lines = ApprovalDisplay.DescribeLines(preparation, PermissionLevel.Level1,
                                                  [GrantScope.SingleOperation], []);

        Assert.Multiple(() =>
        {
            Assert.That(lines[0], Is.EqualTo(
                "backend: host process (workspace file tools) — executes inside the TinyHarness host process " +
                "at the application-layer trusted boundary; not OS-enforced"),
                "H1 文件工具档：宿主进程内执行、应用层可信边界、非 OS 强制。");
            // 文件/网络行保持沙箱措辞（广泛读基线 + 受限网络）。
            Assert.That(lines[1], Does.Contain("broad read baseline"));
            Assert.That(lines[2], Is.EqualTo("network: none (sandbox network stays restricted)"));
        });
    }

    // ---- CLI fact 行与可选参数兼容 ----

    [Test]
    public async Task ConsoleApprovalProvider_PrintsEachDisplayNoteAsARedactedFactLine()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var preparation = new ApplyPatchTool(dir.Workspace).Prepare(
            new ChatToolCall("p", "apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString()));
        var notes = new[] { "backend: WindowsSandbox — same sandbox account identity, non-administrator",
                            "network: none (sandbox network stays restricted)" };
        var request = new ApprovalRequest(preparation, PermissionLevel.Level2,
                                          [GrantScope.SingleOperation, GrantScope.Turn], null, null, notes);
        using var output = new StringWriter();

        var resolution = await new ConsoleApprovalProvider(new StringReader("d"), output)
                             .PromptAsync(request, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Denied));
            foreach (var note in notes)
                Assert.That(output.ToString(), Does.Contain($"  fact: {note}"),
                            "CLI 审批输出必须逐行打印 DisplayNotes 事实行。");
        });
    }

    [Test]
    public async Task ConsoleApprovalProvider_WithoutDisplayNotes_PrintsNoFactLines()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var preparation = new ApplyPatchTool(dir.Workspace).Prepare(
            new ChatToolCall("p", "apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString()));
        // 5 参位置构造兼容（DisplayNotes 缺省 null）。
        ApprovalRequest request = new(preparation, PermissionLevel.Level2,
                                      [GrantScope.SingleOperation, GrantScope.Turn], null, null);
        using var output = new StringWriter();

        await new ConsoleApprovalProvider(new StringReader("d"), output)
            .PromptAsync(request, CancellationToken.None);

        Assert.That(output.ToString(), Does.Not.Contain("  fact: "));
    }

    // ---- 工具 ----

    private static string Revision(string policyIdentity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(policyIdentity)))[..16];

    private static ToolPreparation Preparation(IReadOnlyList<string> targets, string constraint,
                                               bool requiresNetwork = false,
                                               ProcessExecutionBackendKind? backend = null,
                                               IReadOnlyList<string>? writeRoots = null,
                                               string capability = "process.execute") => new()
    {
        ToolName              = "shell",
        CallId                = "call_1",
        Arguments             = new JsonObject { ["mode"] = "direct" },
        Capability            = capability,
        Summary               = "shell command",
        RequiresNetworkAccess = requiresNetwork,
        RequestedBackend      = backend,
        SessionConstraint     = constraint,
        RequestedWriteRoots   = writeRoots ?? [],
        TargetPaths           = targets
    };
}
