using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         常设信任状态评估：检测常设网络信任或附加写根并对照显式确认记录的三态判定，以及
///         确认时固化范围修订（网络策略与附加读写根的稳定指纹）的纯函数。检测命中而记录缺失
///         或范围修订不一致均为 Unconfirmed；无可常设扩大的设置为 None。
///     </para>
///     <para>
///         Standing-trust state evaluation: the pure functions behind the three-state decision that
///         detects standing network trust or additional write roots against the explicit confirmation
///         record, and fixes the scope revision (a stable fingerprint of the network policy and
///         additional read/write roots) at confirmation time. A detection hit without a record, or with
///         a divergent scope revision, is Unconfirmed; settings that cannot expand the baseline are None.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         检测维度为常设网络信任与附加写根（逐次审批的扩大面）；附加读根不触发确认门禁
///         （应用层文件工具读在广泛读基线下不变），但纳入范围修订并随 Confirmed 注入——
///         注入后 profile 外的读根在 OS 层真实扩大沙箱读面，确认展示如实列出。
///         沙箱未启用时整体为 None。
///     </para>
///     <para>
///         Detection covers standing network trust and additional write roots (the per-call approval
///         dimensions); additional read roots do not trigger the confirmation gate (host-side file-tool reads
///         stay on the broad baseline), but they enter the scope revision and are injected once Confirmed —
///         injected read roots outside the profile genuinely widen the sandbox's OS read surface and are
///         listed in the confirmation display. A disabled sandbox is None overall.
///     </para>
/// </remarks>
public static class StandingTrustEvaluation
{
    /// <summary>
    ///     <para>
    ///         判定常设信任状态：无可扩大设置为 None；命中检测且确认记录存在且范围修订一致为
    ///         Confirmed；其余（记录缺失或修订漂移）为 Unconfirmed。
    ///     </para>
    ///     <para>
    ///         Evaluates the standing-trust state: None when nothing can expand the baseline; Confirmed
    ///         when detection hits and a confirmation record exists with a matching scope revision;
    ///         Unconfirmed otherwise (record missing or revision diverged).
    ///     </para>
    /// </summary>
    public static StandingTrustState Evaluate(WindowsSandboxSettings? sandbox, PermissionSettings? permissions)
    {
        if (!HasStandingExpansion(sandbox)) return StandingTrustState.None;

        var confirmation = permissions?.StandingTrustConfirmation;
        return confirmation is { } record && record.ScopeRevision == ScopeRevision(sandbox!)
            ? StandingTrustState.Confirmed
            : StandingTrustState.Unconfirmed;
    }

    /// <summary>
    ///     <para>
    ///         是否存在可常设扩大的可信设置：沙箱启用且（常设网络信任为 enabled 或附加写根非空）。
    ///         附加读根不触发门禁。
    ///     </para>
    ///     <para>
    ///         Whether trusted settings that can silently expand exist: the sandbox is enabled and
    ///         (standing network trust is enabled or additional write roots are non-empty). Additional
    ///         read roots do not trigger the gate.
    ///     </para>
    /// </summary>
    public static bool HasStandingExpansion(WindowsSandboxSettings? sandbox) =>
        sandbox is { Enabled: true } settings &&
        (settings.NetworkPolicy == SandboxNetworkPolicy.Enabled || settings.AdditionalWriteRoots.Count > 0);

    /// <summary>
    ///     <para>
    ///         计算沙箱组合实际消费的设置：仅 Confirmed 沿用原值；其余状态把常设网络回落为
    ///         restricted 并清空附加读写根（OS 基线=profile 根+工作区，写根仅经 grant 通道生效）。
    ///     </para>
    ///     <para>
    ///         Computes the settings the sandbox composition actually consumes: only Confirmed keeps the
    ///         original values; every other state falls back the standing network to restricted and empties
    ///         the additional read/write roots (the OS baseline is the profile root plus the workspace, and
    ///         write roots take effect only through the grant channel).
    ///     </para>
    /// </summary>
    public static WindowsSandboxSettings? EffectiveSandboxSettings(WindowsSandboxSettings? sandbox,
                                                                   StandingTrustState      state)
    {
        if (sandbox is null || state == StandingTrustState.Confirmed) return sandbox;

        return sandbox with
        {
            NetworkPolicy = SandboxNetworkPolicy.Restricted,
            AdditionalWriteRoots = [],
            AdditionalReadRoots = []
        };
    }

    /// <summary>
    ///     <para>
    ///         计算确认时的范围修订：网络策略与附加读写根（规范化绝对路径、序数排序）的稳定
    ///         SHA-256 指纹；设置任一变化都会改变修订并使既有确认回到未确认态。
    ///     </para>
    ///     <para>
    ///         Computes the scope revision fixed at confirmation: a stable SHA-256 fingerprint over the
    ///         network policy and the additional read/write roots (normalized absolute paths, ordinal
    ///         order); any settings change alters the revision and returns the existing confirmation to
    ///         the unconfirmed state.
    ///     </para>
    /// </summary>
    public static string ScopeRevision(WindowsSandboxSettings sandbox)
    {
        var canonical = new StringBuilder();
        canonical.Append("network=").Append(SandboxNetworkPolicyParser.ToProfileValue(sandbox.NetworkPolicy))
                 .Append('\n');
        AppendRoots(canonical, "readRoots", sandbox.AdditionalReadRoots);
        AppendRoots(canonical, "writeRoots", sandbox.AdditionalWriteRoots);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private static void AppendRoots(StringBuilder canonical, string name, IReadOnlyList<string> roots)
    {
        canonical.Append(name).Append('=');
        if (roots.Count == 0)
        {
            canonical.Append("none").Append('\n');
            return;
        }

        var normalized = roots.Select(Path.GetFullPath)
                              .Distinct(StringComparer.OrdinalIgnoreCase)
                              .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        canonical.Append(string.Join("|", normalized)).Append('\n');
    }
}
