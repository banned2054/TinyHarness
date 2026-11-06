namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     <para>沙箱子进程代理环境的选择模式；独立于应用自身 HTTP 请求的路由。</para>
///     <para>
///         Selects the proxy environment of sandboxed child processes; independent
///         of the route used by the app's own HTTP requests.
///     </para>
/// </summary>
public enum SandboxProxyMode
{
    FollowAppDefault,
    System,
    Direct,
    Named
}

/// <summary>
///     <para>
///         沙箱子进程代理环境（HTTP_PROXY 等）的可信设置。Mode 为 Named 时 ProxyName 必填，按名称
///         在 NetworkSettings.Proxies 中查找，找不到时拒绝执行；绝不静默直连。
///     </para>
///     <para>
///         Trusted settings for the sandbox child-process proxy environment (HTTP_PROXY
///         and friends). With the Named mode, ProxyName is required and looked up by
///         name in NetworkSettings.Proxies; a missing entry refuses execution and
///         never silently falls back to a direct connection.
///     </para>
/// </summary>
public sealed record SandboxProxyPolicy
{
    public SandboxProxyMode Mode { get; init; } = SandboxProxyMode.FollowAppDefault;

    /// <summary>
    ///     <para>Named 模式引用的用户可见代理名；其他模式必须留空。</para>
    ///     <para>The user-visible proxy name referenced by the Named mode; it must stay empty otherwise.</para>
    /// </summary>
    public string? ProxyName { get; init; }
}
