using System.Globalization;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         授权执行禁用状态的持久化口：恢复 refresh 失败或进程回收结果未知时写入，后续授权执行
///         在入口读取并阻断；仅经后续成功的恢复刷新清除。实现必须写入真实用户可写、沙箱组不可写
///         的受保护位置，防止沙箱内命令自解禁。
///     </para>
///     <para>
///         The persistence port for the authorized-execution disable state: written when a restore
///         refresh fails or process reclamation is unknown, read at the entry of later authorized
///         executions to block them, and cleared only by a later successful restore refresh.
///         Implementations must write to a location the real user can write while the sandbox group
///         cannot, so sandboxed commands can never un-disable themselves.
///     </para>
/// </summary>
public interface IAuthorizedReadDisableStore
{
    /// <summary>读取当前禁用状态；未禁用返回 false。 Reads the current disable state; false when not disabled.</summary>
    Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken);

    /// <summary>以给定原因持久化禁用状态。 Persists the disable state with the given reason.</summary>
    Task DisableAsync(string reason, CancellationToken cancellationToken);

    /// <summary>清除禁用状态（仅在恢复刷新成功后调用）。 Clears the disable state (only after a successful restore refresh).</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         <see cref="IAuthorizedReadDisableStore" /> 的文件实现：状态写入凭据目录下的
///         authorized_read_state.json（provisioning 对沙箱组永久 DENY，应用以真实用户写入）。
///         文件缺失为未禁用；内容损坏按禁用处理（fail closed），恢复成功后删除文件即清除。
///     </para>
///     <para>
///         The file-backed implementation of <see cref="IAuthorizedReadDisableStore" />: the state
///         lives in authorized_read_state.json under the credentials directory (permanently denied to
///         the sandbox group by provisioning, written by the app as the real user). A missing file
///         means not disabled; corrupt content counts as disabled (fail closed), and a successful
///         recovery deletes the file to clear the state.
///     </para>
/// </summary>
public sealed class AuthorizedReadDisableFile(WindowsSandboxComponents components) : IAuthorizedReadDisableStore
{
    /// <summary>状态文件固定位置：&lt;home&gt;\.sandbox-secrets\authorized_read_state.json。 The fixed state file location.</summary>
    public string StatePath => Path.Combine(components.CredentialsDirectory, "authorized_read_state.json");

    /// <inheritdoc />
    public async Task<(bool Disabled, string? Reason)> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath)) return (false, null);

        try
        {
            var content = await File.ReadAllTextAsync(StatePath, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxAuthorizedReadState);
            return state is { Disabled: true }
                ? (true, state.Reason ?? "the persisted disable state carries no reason")
                : (false, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 状态文件不可读按禁用处理（fail closed）；恢复成功后删除文件即可清除。
            return (true, $"the disable state at '{StatePath}' is unreadable ({ex.GetType().Name})");
        }
    }

    /// <inheritdoc />
    public async Task DisableAsync(string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var state = new SandboxAuthorizedReadState
        {
            Disabled     = true,
            Reason       = reason,
            DisabledAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
        };
        Directory.CreateDirectory(components.CredentialsDirectory);
        await File.WriteAllTextAsync(StatePath,
                                     JsonSerializer.Serialize(state,
                                                              WindowsSandboxJsonContext.Default
                                                                 .SandboxAuthorizedReadState),
                                     cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(StatePath)) File.Delete(StatePath);

        return Task.CompletedTask;
    }
}
