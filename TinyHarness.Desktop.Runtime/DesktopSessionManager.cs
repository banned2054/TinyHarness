namespace TinyHarness.Desktop.Runtime;

/// <summary>
/// <para>拥有应用中的多个会话；只串行化打开与关闭，不串行化不同会话的运行。</para>
/// <para>Owns multiple application sessions, serializing opening and closing without serializing their runs.</para>
/// </summary>
public sealed class DesktopSessionManager : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DesktopSession> _sessions = new(StringComparer.Ordinal);
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
                await created.DisposeAsync().ConfigureAwait(false);
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
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_sessions.Values.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
            _sessions.Clear();
        }
        finally { _gate.Release(); }
    }
}
