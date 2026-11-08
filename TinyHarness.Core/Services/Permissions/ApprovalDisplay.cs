using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         审批展示补齐的单一来源：按冻结计划与执行分类生成展示事实行（真实执行后端与隔离声明、
///         实际文件权限含本次申请写根、网络权限精确措辞、可授权范围的有效期、已有活动授权摘要），
///         供 CLI 审批器与 Desktop 审批卡一致呈现。展示不授权，授权判定仍属于权限引擎。
///     </para>
///     <para>
///         The single source of the completed approval display: it derives the display fact lines (the real
///         execution backend with its isolation declaration, the effective file permissions including this
///         call's requested write roots, the exact network wording, the scope validity, and existing active
///         grant summaries) from the frozen plan and level, for identical rendering by the CLI approver and the
///         Desktop approval card. Display never grants authority; the permission engine still decides.
///     </para>
/// </summary>
public static class ApprovalDisplay
{
    /// <summary>
    ///     <para>
    ///         生成展示事实行：申请层级与真实执行后端、L2/L3 隔离声明、文件权限（广泛读基线、
    ///         本次申请写根）、网络权限（F6 精确措辞）、有效期（可授权范围）与活动授权摘要。
    ///     </para>
    ///     <para>
    ///         Builds the display fact lines: the requested level with the real execution backend, the L2/L3
    ///         isolation declarations, file permissions (the broad-read baseline and this call's requested write
    ///         roots), the network permission (exact F6 wording), scope validity, and active grant summaries.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<string> DescribeLines(
        ToolPreparation           preparation,
        PermissionLevel           level,
        IReadOnlyList<GrantScope> allowedScopes,
        IReadOnlyList<Grant>      activeGrants)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(allowedScopes);
        ArgumentNullException.ThrowIfNull(activeGrants);

        var lines            = new List<string>(8);
        var isHostRequest    = preparation.RequestedBackend == ProcessExecutionBackendKind.Host;
        var isProcessExecute = preparation.Capability       == "process.execute";
        // 后端三分展示（H1）：沙箱命令、显式宿主申请与文件工具各自如实；文件工具运行在宿主
        // 进程内、按应用层可信边界检查（D7），展示不宣称 OS 强制。
        lines.Add(isProcessExecute switch
        {
            true when isHostRequest =>
                "backend: HostProcess — no OS sandbox isolation; runs with the host account's actual file and " +
                "network permissions; no UAC elevation",
            true =>
                "backend: WindowsSandbox — same sandbox account identity, non-administrator",
            _ =>
                "backend: host process (workspace file tools) — executes inside the TinyHarness host process " +
                "at the application-layer trusted boundary; not OS-enforced"
        });

        var requestedWriteRoots = preparation.RequestedWriteRoots;
        if (isProcessExecute && isHostRequest)
        {
            // 显式宿主申请（M1）：路由层清除沙箱网络标志，文件与网络权限如实按宿主账户展示。
            lines.Add("file permissions: the host account's actual file permissions (no OS sandbox)");
            lines.Add("network: not sandboxed — follows the host account's actual network permissions");
        }
        else
        {
            lines.Add(requestedWriteRoots.Count > 0
                          ? "file permissions: broad read baseline (not limited to the workspace); requested write roots: " +
                            string.Join(", ", requestedWriteRoots)
                          : "file permissions: broad read baseline (not limited to the workspace); no write roots requested");
            lines.Add(preparation.RequiresNetworkAccess
                          ? "network: full network inside the sandbox — through the configured proxy when one is set " +
                            "(a broken proxy fails fast without direct fallback); direct full network when no proxy is configured"
                          : "network: none (sandbox network stays restricted)");
        }

        var validity = allowedScopes.Count == 0
            ? "(no grantable scope)"
            : string.Join(" / ", allowedScopes.Select(scope => scope switch
            {
                GrantScope.SingleOperation => "allow once (this attempt only)",
                GrantScope.Turn            => "turn (until this run ends)",
                GrantScope.Session         => "session (until this session closes)",
                _                          => scope.ToString()
            }));
        lines.Add($"validity: {validity}");

        var active = activeGrants.Where(grant => grant.State == GrantState.Active).ToArray();
        if (active.Length > 0)
        {
            lines.Add("active grants:");
            foreach (var grant in active) lines.Add("  " + GrantHistoryEntry.Describe(grant));
        }
        else
        {
            lines.Add("active grants: (none)");
        }

        return lines;
    }
}
