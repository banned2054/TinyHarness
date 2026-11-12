using System.Text.Json;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.Session;

public sealed partial class ChatSessionHost
{
    private readonly SemaphoreSlim _titleGate = new(1, 1);

    /// <summary>
    ///     <para>用户回合已提交；订阅者只排队后台命名，不阻塞会话执行。</para>
    ///     <para>A user turn was committed; subscribers only queue background naming without blocking execution.</para>
    /// </summary>
    public event Action? UserTurnCommitted;

    /// <summary>
    ///     <para>在第一次命名前持久化待重试标记；旧日志在首次尝试时补记。</para>
    ///     <para>Persists the retry marker before the first attempt, including the first attempt on legacy journals.</para>
    /// </summary>
    public async Task<bool> PrepareTitleGenerationAsync(CancellationToken cancellationToken)
    {
        await _titleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Session.Title.Pending || Session.Turns.Count == 0) return false;
            await AppendTitleAsync(Session.Title, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _titleGate.Release();
        }
    }

    /// <summary>
    ///     <para>仅在仍待命名时保存生成名称；已有名称不会被迟到结果覆盖。</para>
    ///     <para>Saves a generated name only while pending; late results never overwrite an existing name.</para>
    /// </summary>
    public async Task<bool> TrySetGeneratedTitleAsync(string name, CancellationToken cancellationToken)
    {
        var title = new SessionTitle { Name = SessionTitle.Normalize(name), Pending = false };
        title.Validate();
        await _titleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Session.Title.Pending) return false;
            await AppendTitleAsync(title, cancellationToken).ConfigureAwait(false);
            Session.Title = title;
            return true;
        }
        finally
        {
            _titleGate.Release();
        }
    }

    private async Task AppendTitleAsync(SessionTitle title, CancellationToken cancellationToken)
    {
        if (_sessionWriter is null) return;
        await _sessionWriter.AppendRecordsAsync([
            new SessionLogRecord(string.Empty, "session-title",
                                 JsonSerializer.SerializeToElement(title, SessionLogJsonContext.Default.SessionTitle))
        ], cancellationToken).ConfigureAwait(false);
    }
}
