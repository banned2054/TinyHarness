namespace TinyHarness.Core.Models.Runtime;

/// <summary>
/// 进程执行后端种类。host 表示既有宿主直接执行（无 OS 隔离）；windows-sandbox 表示经
/// Windows 沙箱编排执行。授权指纹按后端种类隔离，同一命令不能复用旧授权切换宿主。
///
/// The process execution backend kind. Host is the existing direct host
/// execution (no OS isolation); WindowsSandbox is the orchestrated sandbox
/// path. Authorization fingerprints isolate by backend kind so the same
/// command cannot reuse an old approval to switch hosts.
/// </summary>
public enum ProcessExecutionBackendKind
{
    Host = 0,

    WindowsSandbox = 1,
}

/// <summary>
/// 进程工具的执行策略描述：后端种类、面向用户的展示名和用于授权绑定的规范化策略身份。
/// 策略身份由可信设置在组合期计算，绑定策略版本、有效读写范围、deny、网络、临时根、
/// 元数据保护和目标环境身份；同一身份下的会话授权才可复用。它只是描述，不授予任何权限。
///
/// The execution policy of process tools: the backend kind, a user-facing
/// display name, and the normalized policy identity used for authorization
/// binding. The identity is computed once at composition from trusted
/// settings and binds the policy version, effective read/write scopes, deny
/// paths, network, temp roots, metadata protection, and the target
/// environment's identity; only session grants under the same identity are
/// reusable. This is a description, never a permission grant.
/// </summary>
public sealed record ProcessExecutionPolicy
{
    /// <summary>
    /// 未经沙箱强化的默认宿主执行策略：授权与审计都以 backend=host 身份记录，展示名明确
    /// 说明没有 OS 隔离。
    ///
    /// The default, non-hardened host execution policy: authorizations and
    /// audit records use the backend=host identity and the display name states
    /// plainly that there is no OS isolation.
    /// </summary>
    public static ProcessExecutionPolicy Host() => new()
    {
        Backend       = ProcessExecutionBackendKind.Host,
        DisplayName   = "host execution (no OS isolation)",
        PolicyIdentity = "backend=host",
    };

    public required ProcessExecutionBackendKind Backend { get; init; }

    /// <summary>
    /// 展示给用户的一行描述（CLI banner、审批摘要）。必须如实反映真实隔离，不宣传不存在的边界。
    ///
    /// One-line description shown to users (CLI banner, approval summaries).
    /// It must state the real isolation and never advertise boundaries that do not exist.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// 规范化策略身份串。作为不透明相等令牌进入会话授权约束、一次性授权指纹和审计记录；
    /// 只保证同配置稳定、异配置互异，不保证跨版本兼容。
    ///
    /// The normalized policy identity string. It flows as an opaque equality
    /// token into session grant constraints, one-shot fingerprints, and audit
    /// records; it is stable for identical configuration, distinct for
    /// different ones, and not a compatibility contract across versions.
    /// </summary>
    public required string PolicyIdentity { get; init; }
}
