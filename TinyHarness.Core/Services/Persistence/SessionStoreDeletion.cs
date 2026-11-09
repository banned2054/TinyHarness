using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>在会话 writer 锁保护下永久删除一份会话日志。</para>
///     <para>Permanently deletes a session log while holding its writer lock.</para>
/// </summary>
public static class SessionStoreDeletion
{
    /// <summary>
    ///     <para>校验受保护 store、会话 ID 与日志叶子，并持有同一 writer 锁直到日志删除完成。</para>
    ///     <para>Validates the protected store, session ID, and log leaf, holding the writer lock until deletion completes.</para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>会话 store 目录。</para>
    ///     <para>The session-store directory.</para>
    /// </param>
    /// <param name="workspaceRoot">
    ///     <para>必须与会话 store 保持隔离的工作区根目录。</para>
    ///     <para>The workspace root that the session store must remain isolated from.</para>
    /// </param>
    /// <param name="sessionId">
    ///     <para>32 位十六进制会话 ID。</para>
    ///     <para>The 32-character hexadecimal session ID.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消锁获取前的等待；进入删除临界区后不再取消。</para>
    ///     <para>Cancels before acquiring the lock; deletion is not cancelled after entering the critical section.</para>
    /// </param>
    public static async Task DeleteAsync(string storeDirectory, string workspaceRoot, string sessionId,
                                         CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));

        var directory = SessionStorePaths.ResolveProtected(storeDirectory, workspaceRoot);
        if (!Directory.Exists(directory))
            throw new SessionStoreException($"Session store directory '{directory}' no longer exists.");

        sessionId = sessionId.ToLowerInvariant();
        var lockPath = Path.Combine(directory, sessionId + ".lock");
        var logPath  = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
        SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
        SessionStorePaths.EnsureStoreLeaf(directory, logPath);

        FileStream lockStream;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new SessionStoreException($"Session '{sessionId}' is currently open and cannot be deleted.",
                                            exception);
        }

        await using (lockStream.ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionStorePaths.EnsureStoreLeaf(directory, lockPath);
            SessionStorePaths.EnsureStoreLeaf(directory, logPath);
            if (!File.Exists(logPath))
                throw new SessionStoreException($"Session log '{logPath}' no longer exists.");

            try
            {
                File.Delete(logPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SessionStoreException($"Could not delete session log '{logPath}': {exception.Message}",
                                                exception);
            }
        }
    }
}
