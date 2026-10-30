namespace TinyHarness.Desktop.Runtime;

/// <summary>
/// <para>拥有应用中的多个会话；只串行化打开与关闭，不串行化不同会话的运行。</para>
/// <para>Owns multiple application sessions, serializing opening and closing without serializing their runs.</para>
/// </summary>
public sealed class DesktopSessionManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DesktopSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<Exception> _openCleanupErrors = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _shutdownSync = new();
    private Task? _shutdown;

    /// <summary>
    /// <para>返回已打开的宿主，或创建并接管一个宿主；打开失败不会替换既有会话。</para>
    /// <para>Returns an open host or creates and takes ownership of one; opening failures never replace existing sessions.</para>
    /// </summary>
    public async Task<DesktopSession> OpenAsync(string? sessionId,
                                               Func<CancellationToken, Task<DesktopSession>> factory,
                                               CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (sessionId is not null && _sessions.TryGetValue(sessionId, out var existing)) return existing;
            var created = await factory(linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested || _sessions.ContainsKey(created.Id))
            {
                try
                {
                    await created.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _openCleanupErrors.Add(exception);
                    throw;
                }

                linked.Token.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The session is already open.");
            }
            _sessions.Add(created.Id, created);
            return created;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// <para>取消所有会话并等待每个宿主收尾；重复调用等待同一次关闭。</para>
    /// <para>Cancels every session and awaits all hosts; repeated calls await the same shutdown.</para>
    /// </summary>
    /// <remarks>
    ///     <para>取消异常不跳过会话释放；全部尝试结束后汇总异常及未可靠保存的警告。</para>
    ///     <para>Cancellation failures do not skip session disposal; aggregates failures and persistence warnings after all attempts finish.</para>
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        lock (_shutdownSync)
        {
            _shutdown ??= Task.Run(ShutdownAsync);
            return new ValueTask(_shutdown);
        }
    }

    private async Task ShutdownAsync()
    {
        List<Exception> errors = [];
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            errors.AddRange(_openCleanupErrors);
            _openCleanupErrors.Clear();
            var outcomes = await Task.WhenAll(_sessions.Values.Select(async session =>
            {
                List<Exception> failures = [];
                try
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (session.Host.PersistenceWarning is { } warning)
                    failures.Add(new IOException($"会话 {session.Id} 未可靠保存：{warning}"));
                return failures;
            })).ConfigureAwait(false);
            errors.AddRange(outcomes.SelectMany(outcome => outcome));
        }
        finally
        {
            _sessions.Clear();
            _gate.Release();
        }

        if (errors.Count > 0) throw new AggregateException("关闭会话时发生错误；已尝试完成所有会话清理。", errors);
    }
}
