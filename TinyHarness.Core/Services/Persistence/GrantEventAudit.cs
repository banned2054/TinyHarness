using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         授权事件与审计记录之间的单一映射：GrantRegistry 事件映射为
///         <see cref="PermissionGrantEventRecord" />（superseded 归 invalidated），可信授权变更
///         事件由调用方经 <see cref="AuthorizationChanged" /> 构造。目标与原因由调用方注入的
///         脱敏函数处理，沿用会话日志脱敏边界。
///     </para>
///     <para>
///         The single mapping between grant events and audit records: a GrantRegistry event maps to a
///         <see cref="PermissionGrantEventRecord" /> (superseded lands as invalidated), and trusted
///         authorization changes are built through <see cref="AuthorizationChanged" />. Targets and reasons go
///         through the caller-injected redaction function under the session-log redaction boundary.
///     </para>
/// </summary>
public static class GrantEventAudit
{
    /// <summary>
    ///     <para>把一条授权登记表事件映射为审计记录；目标路径经给定脱敏函数处理。</para>
    ///     <para>
    ///         Maps one grant-registry event to an audit record; target paths go through the given redaction
    ///         function.
    ///     </para>
    /// </summary>
    public static PermissionGrantEventRecord FromEvent(
        GrantRegistry.GrantEvent grantEvent, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(grantEvent);
        ArgumentNullException.ThrowIfNull(redact);
        var grant = grantEvent.Grant;
        return new PermissionGrantEventRecord
        {
            Schema                 = PermissionGrantEventRecord.CurrentSchema,
            GrantId                = grant.GrantId,
            Event                  = EventName(grantEvent.Action),
            Scope                  = ScopeName(grant.Scope),
            Capability             = grant.Capability,
            Targets                = grant.Targets.Select(redact).ToArray(),
            PolicyIdentityRevision = grant.PolicyIdentity ?? string.Empty,
            Utc                    = grantEvent.OccurredUtc
        };
    }

    /// <summary>
    ///     <para>构造可信授权变更事件（拒绝记忆解除）；原因必填并经脱敏函数处理。</para>
    ///     <para>
    ///         Builds a trusted authorization-changed event (a denial-memory release); the mandatory reason goes
    ///         through the redaction function.
    ///     </para>
    /// </summary>
    public static PermissionGrantEventRecord AuthorizationChanged(
        string reason, DateTimeOffset occurredUtc, Func<string, string> redact)
    {
        ArgumentNullException.ThrowIfNull(redact);
        return new PermissionGrantEventRecord
        {
            Schema                 = PermissionGrantEventRecord.CurrentSchema,
            GrantId                = string.Empty,
            Event                  = "authorization-changed",
            Scope                  = "none",
            Capability             = "authorization",
            Targets                = [],
            PolicyIdentityRevision = string.Empty,
            Reason                 = redact(reason),
            Utc                    = occurredUtc
        };
    }

    /// <summary>把登记表动作映射为稳定事件名；未知动作 fail-closed 抛出。</summary>
    private static string EventName(string action) => action switch
    {
        "granted"               => "granted",
        "consumed"              => "consumed",
        "revoked"               => "revoked",
        "expired"               => "expired",
        "superseded"            => "invalidated",
        "authorization-changed" => "authorization-changed",
        _ => throw new
            InvalidOperationException($"Unknown grant action '{action}'; refusing to audit it as a grant event.")
    };

    private static string ScopeName(GrantScope scope) => scope switch
    {
        GrantScope.SingleOperation => "single-operation",
        GrantScope.Turn            => "turn",
        GrantScope.Session         => "session",
        _ => throw new
            InvalidOperationException($"Unknown grant scope '{scope}'; refusing to audit it as a grant event.")
    };
}
