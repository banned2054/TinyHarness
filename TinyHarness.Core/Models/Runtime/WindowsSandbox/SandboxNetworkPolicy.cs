namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         沙箱命令的常设网络策略。Restricted 保持外部网络阻断，单个命令仍可经逐次审批临时在线执行；
///         Enabled 表示所有沙箱命令以在线账户执行，不再逐次审批。只来自可信用户设置。
///     </para>
///     <para>
///         The standing network policy for sandboxed commands. Restricted keeps
///         external networking blocked while a single command may still be granted
///         online execution through per-command approval; Enabled runs every
///         sandboxed command under the online account without per-command approval.
///         It only comes from trusted user settings.
///     </para>
/// </summary>
public enum SandboxNetworkPolicy
{
    Restricted,
    Enabled
}

/// <summary>
///     <para>把常设网络策略映射为 spawn_request permission_profile.network 的稳定小写字符串。</para>
///     <para>
///         Maps the standing network policy to the stable lowercase string of the
///         spawn_request permission_profile.network field.
///     </para>
/// </summary>
public static class SandboxNetworkPolicyParser
{
    public static string ToProfileValue(SandboxNetworkPolicy policy) => policy switch
    {
        SandboxNetworkPolicy.Restricted => "restricted",
        SandboxNetworkPolicy.Enabled    => "enabled",
        _                               => throw new
            ArgumentOutOfRangeException(nameof(policy), policy, "Unknown sandbox network policy.")
    };
}
