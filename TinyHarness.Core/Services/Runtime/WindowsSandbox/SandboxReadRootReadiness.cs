using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         组读根就绪门（冷启动收口）：每命令 refresh 成功返回后、命令启动前，有界轮询校验
///         payload read_roots 通道的每个组读根已在 DACL 中获得 Allow 读 ACE——要么对本机沙箱
///         组（<c>TinyHarnessUsers</c>，helper 的授予主体），要么对内置主体
///         （BUILTIN\Users、Authenticated Users、Everyone；helper 对已有内置读会跳过授予，
///         该形态必须被接受，否则永远误报）。全部就绪即放行（稳态 = 每读根一次 DACL 查询）；
///         超出预算 fail-closed：抛出启动期异常并列出未就绪读根与阶段（read-root readiness）。
///     </para>
///     <para>
///         The group read-root readiness gate (the cold-start close-out): after a per-command
///         refresh returns and before the command starts, poll within a bounded budget until every
///         group read root on the payload's read_roots channel carries an Allow read ACE in its
///         DACL — either for the machine's sandbox group (<c>TinyHarnessUsers</c>, the helper's
///         grant subject) or for the built-in principals (BUILTIN\Users, Authenticated Users,
///         Everyone; the helper skips granting when built-in read already exists, and that shape
///         must be accepted or the gate would misreport forever). All ready releases the command
///         (steady state = one DACL query per read root); exceeding the budget fails closed by
///         throwing the start exception listing the unready roots and the stage (read-root
///         readiness).
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         只校验组读根（read_roots 通道）；授权读专用根（C2 获批路径，授权组面）与 deny 侧不
///         在门内——deny 下发即时生效，而 junction 词法路径自身通常无 deny ACE，把它纳入门控会
///         永久误报。残余风险（如实）：若 helper 的组授予因目标路径 ACL 异常而完全失败，门在
///         预算耗尽后 fail-closed 阻断命令；这是刻意的关闭语义，与"授权范围与执行范围一致"
///         的既有契约一致。单一读根的 DACL 查询失败（IO/权限/路径消失）按"未就绪"参与轮询，
///         最终由预算超界错误统一列出，不改变 fail-closed 结论。
///     </para>
///     <para>
///         Only the group read roots (the read_roots channel) are gated; the authorized-read
///         dedicated roots (the C2 approved paths on the authorization group's face) and the deny
///         side are not — deny application is synchronous, and a junction's lexical path itself
///         usually carries no deny ACE, so gating it would misreport forever. Residual risk
///         (stated honestly): if the helper's group grant fails outright because of an ACL
///         anomaly on the target path, the gate fails closed at budget exhaustion and blocks the
///         command; this is deliberate fail-closed behavior, consistent with the existing
///         "authorization and execution ranges agree" contract. A per-root DACL query failure
///         (I/O, permission, vanished path) joins the poll as "not ready" and is listed by the
///         budget-exceeded error, never changing the fail-closed conclusion.
///     </para>
/// </remarks>
internal static class SandboxReadRootReadiness
{
    /// <summary>
    ///     <para>阶段名：出现在 fail-closed 错误消息中，与写 ACL 校验阶段在诊断上可区分。</para>
    ///     <para>The stage name inside the fail-closed error message, diagnosably distinct from the write-ACL validation stage.</para>
    /// </summary>
    private const string StageName = "read-root readiness";

    /// <summary>
    ///     <para>默认总预算（毫秒）：覆盖 helper 后台授予的实测落地窗口（秒级），同时把冷启动
    ///     最坏等待限制在可感知范围内。</para>
    ///     <para>
    ///         The default total budget in milliseconds: covers the observed landing window (seconds)
    ///         of the helper's background grants while keeping the worst-case cold-start wait
    ///         perceptibly bounded.
    ///     </para>
    /// </summary>
    internal const int DefaultTotalBudgetMilliseconds = 15_000;

    /// <summary>
    ///     <para>默认轮询间隔（毫秒）：远小于授予落地延迟，稳态（已就绪）不产生任何等待。</para>
    ///     <para>
    ///         The default poll interval in milliseconds: far below the grant landing latency; the
    ///         steady state (already ready) never waits.
    ///     </para>
    /// </summary>
    internal const int DefaultPollIntervalMilliseconds = 250;

    /// <summary>
    ///     <para>本机沙箱组名（helper 的授予主体；与 <c>WorkspaceSandboxAclPreparation</c> 同源）。</para>
    ///     <para>
    ///         The machine's sandbox group name (the helper's grant subject; same source as
    ///         <c>WorkspaceSandboxAclPreparation</c>).
    ///     </para>
    /// </summary>
    private const string SandboxGroupName = "TinyHarnessUsers";

    /// <summary>
    ///     <para>生产默认就绪门：按默认总预算与间隔校验 payload 的全部组读根。</para>
    ///     <para>
    ///         The production default readiness gate: validates every group read root of the payload
    ///         with the default total budget and poll interval.
    ///     </para>
    /// </summary>
    /// <exception cref="ProcessExecutionStartException">
    ///     <para>预算耗尽仍有未就绪读根：fail-closed，错误列出未就绪读根与阶段。</para>
    ///     <para>
    ///         Roots remain unready at budget exhaustion: fails closed with the unready roots and
    ///         the stage in the error.
    ///     </para>
    /// </exception>
    public static Task ValidateAsync(SandboxSetupPayload payload, CancellationToken cancellationToken) =>
        ValidateAsync(payload,
                      TimeSpan.FromMilliseconds(DefaultTotalBudgetMilliseconds),
                      TimeSpan.FromMilliseconds(DefaultPollIntervalMilliseconds),
                      cancellationToken);

    /// <summary>
    ///     <para>
    ///         有界轮询校验全部组读根的就绪状态；首轮立即检查（已就绪时零等待），未就绪按
    ///         间隔轮询直至预算耗尽。测试经 internal 重载注入更短预算与间隔。
    ///     </para>
    ///     <para>
    ///         Polls the readiness of every group read root within a bounded budget; the first
    ///         check runs immediately (zero wait when already ready) and unready roots poll until
    ///         the budget expires. Tests inject shorter budgets and intervals through the internal
    ///         overload.
    ///     </para>
    /// </summary>
    /// <exception cref="ProcessExecutionStartException">
    ///     <para>预算耗尽仍有未就绪读根：fail-closed，错误列出未就绪读根与阶段。</para>
    ///     <para>
    ///         Roots remain unready at budget exhaustion: fails closed with the unready roots and
    ///         the stage in the error.
    ///     </para>
    /// </exception>
    internal static async Task ValidateAsync(
        SandboxSetupPayload payload, TimeSpan totalBudget, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        // 就绪判定读取真实 DACL，仅 Windows 可用；其他平台按既有 ACL 检查口径显式失败。
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL readiness inspection is required.");
        ArgumentNullException.ThrowIfNull(payload);
        var sandboxGroupSid  = ResolveSandboxGroupSid();
        var builtInReaderSids = BuiltInReaderSidValues();
        List<string> unreadyRoots;
        // TickCount64 单调，不受系统时间调整影响；预算判定与轮询等待都基于它。
        var deadline = Environment.TickCount64 + (long)totalBudget.TotalMilliseconds;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            unreadyRoots = InspectUnreadyRoots(payload.ReadRoots, sandboxGroupSid, builtInReaderSids);
            if (unreadyRoots.Count == 0) return;

            if (Environment.TickCount64 >= deadline) break;

            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new ProcessExecutionStartException(
            $"The sandbox group read roots were not ready after the ACL refresh (stage '{StageName}'): " +
            $"no Allow read ACE for the sandbox group or a built-in reader appeared on [{string.Join("; ", unreadyRoots)}] " +
            $"within {(int)totalBudget.TotalMilliseconds}ms; refusing to start the command (fail closed). " +
            "Re-run the command; if the error persists, inspect the helper's read-root grants and the group ACLs on these roots.",
            new TimeoutException($"{StageName} budget of {(int)totalBudget.TotalMilliseconds}ms elapsed with {unreadyRoots.Count} root(s) unready."));
    }

    /// <summary>
    ///     <para>
    ///         解析本机沙箱组 SID；组不存在（未 provisioning 的机器）返回 null，就绪判定退化为
    ///         仅按内置读 ACE——此时 helper 也无法向该组授予。
    ///     </para>
    ///     <para>
    ///         Resolves the machine sandbox group SID; an unmapped group (a machine without
    ///         provisioning) returns null and the readiness check degrades to built-in read ACEs
    ///         only — the helper cannot grant to that group either.
    ///     </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? ResolveSandboxGroupSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, SandboxGroupName)
               .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>内置可读主体的 SID 字符串集（大小写不敏感比较在此口径下等价于精确比较）。 The built-in reader principals' SID strings as a set.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlySet<string> BuiltInReaderSidValues() =>
        BuiltInReaderSids().Select(sid => sid.Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>内置可读主体：helper 对已有内置读跳过授予时，读根以这些 ACE 的形态就绪。 Built-in reader principals; when the helper skips granting because built-in read exists, read roots are ready through these ACEs.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<SecurityIdentifier> BuiltInReaderSids() =>
    [
        new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        new SecurityIdentifier(WellKnownSidType.WorldSid, null)
    ];

    /// <summary>
    ///     <para>逐读根检查就绪状态并返回未就绪清单（保持 payload 顺序、按大小写不敏感去重）。</para>
    ///     <para>
    ///         Checks every read root and returns the unready list (payload order preserved,
    ///         case-insensitively deduplicated).
    ///     </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static List<string> InspectUnreadyRoots(
        IReadOnlyList<string> readRoots, SecurityIdentifier? sandboxGroupSid, IReadOnlySet<string> builtInReaderSids)
    {
        var unreadyRoots    = new List<string>();
        var inspectedRoots  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var readRoot in readRoots)
        {
            if (!inspectedRoots.Add(readRoot)) continue;

            if (!IsReadRootReady(readRoot, sandboxGroupSid, builtInReaderSids)) unreadyRoots.Add(readRoot);
        }

        return unreadyRoots;
    }

    /// <summary>
    ///     <para>
    ///         单个读根的就绪判定：路径存在且 DACL（含继承）中存在对沙箱组或内置主体的
    ///         Allow 读 ACE（非 InheritOnly、含读数据位）。查询失败按未就绪处理，交由预算
    ///         超界的 fail-closed 统一报告。
    ///     </para>
    ///     <para>
    ///         The per-root readiness verdict: the path exists and its DACL (inherited included)
    ///         carries an Allow read ACE for the sandbox group or a built-in principal (not
    ///         InheritOnly, carrying the read-data bit). A query failure counts as not ready and is
    ///         reported by the budget-exceeded fail-closed error.
    ///     </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsReadRootReady(
        string readRoot, SecurityIdentifier? sandboxGroupSid, IReadOnlySet<string> builtInReaderSids)
    {
        try
        {
            var directoryExists = Directory.Exists(readRoot);
            if (!directoryExists && !File.Exists(readRoot)) return false;

            // 读根可为目录或文件（顶层规划把普通顶层文件也作为读根）；检查使用与既有 ACL 检查
            // 相同的 GetAccessControl/AuthorizationRuleCollection 模式，无反射、无新依赖。
            FileSystemSecurity security = directoryExists
                ? new DirectoryInfo(readRoot).GetAccessControl(AccessControlSections.Access)
                : new FileInfo(readRoot).GetAccessControl(AccessControlSections.Access);
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                                         .Cast<FileSystemAccessRule>())
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;

                // InheritOnly ACE 不授予对象自身；只读根的首批读取包含列目录/读内容本身。
                if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                if (!GrantsRead(rule)) continue;

                var identity = rule.IdentityReference.Value;
                if (sandboxGroupSid is not null &&
                    string.Equals(identity, sandboxGroupSid.Value, StringComparison.Ordinal))
                    return true;
                if (builtInReaderSids.Contains(identity)) return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     <para>
    ///         判断 ACE 是否授予读：通用位先展开为具体读位（口径与既有继承检查的通用位展开
    ///         一致），再要求读数据位（ListDirectory 同位）——能列目录/读内容即视为读。
    ///     </para>
    ///     <para>
    ///         Whether an ACE grants read: generic bits are expanded into specific read bits first
    ///         (the same expansion criteria as the existing inheritance check), then the read-data
    ///         bit (ListDirectory shares the value) is required — listing a directory or reading its
    ///         content counts as read.
    ///     </para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool GrantsRead(FileSystemAccessRule rule)
    {
        var rights = unchecked((uint)rule.FileSystemRights);
        if ((rights & 0x80000000) != 0) rights |= 0x120089; // GenericRead → FILE_GENERIC_READ。
        if ((rights & 0x20000000) != 0) rights |= 0x1200A0; // GenericExecute → FILE_GENERIC_EXECUTE。
        return (rights & (uint)FileSystemRights.ReadData) != 0;
    }
}
