using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    /// <summary>
    ///     <para>空闲时可靠保存权限预设，再撤销临时授权并更新执行策略；不发送模型请求。</para>
    ///     <para>
    ///         Durably saves an idle session's preset, then revokes temporary grants and updates execution policy without
    ///         model calls.
    ///     </para>
    /// </summary>
    public async Task<SessionPermissionSelection> ChangePermissionModeAsync(
        SessionPermissionMode mode, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        cancellationToken.ThrowIfCancellationRequested();
        EnsureModelOperationsAllowed();
        if (Interlocked.CompareExchange(ref _activeRuns, 2, 0) != 0)
            throw new InvalidOperationException("请先停止当前运行，再切换权限。");
        try
        {
            if (Session.PermissionSelection is { } current && current.Mode == mode)
            {
                _permissions?.SetMode(mode);
                return current;
            }

            var selection = new SessionPermissionSelection
            {
                Revision = checked((Session.PermissionSelection?.Revision ?? 0) + 1), Mode = mode
            };
            if (_sessionWriter is not null)
            {
                if (_context.Messages.Count == 0) _context.Append(ChatMessage.System(_systemPrompt));
                var record = new SessionLogRecord(string.Empty, "permission-selection",
                                                  JsonSerializer.SerializeToElement(selection,
                                                           SessionLogJsonContext.Default.SessionPermissionSelection));
                await _sessionWriter.CommitControlAsync(Session, _context, [record], cancellationToken)
                                    .ConfigureAwait(false);
            }

            Session.PermissionSelection = selection;
            _permissions?.SetMode(mode);
            return selection;
        }
        finally
        {
            Interlocked.Exchange(ref _activeRuns, 0);
        }
    }
}
