using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>在会话 writer 锁保护下删除一份会话日志。</para>
///     <para>Deletes a session log while holding its writer lock.</para>
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
    public static async Task DeleteAsync(
        string storeDirectory, string workspaceRoot, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var directory = SessionStorePaths.ResolveProtected(storeDirectory, workspaceRoot);
        await DeleteCoreAsync(directory, sessionId, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         在已解析的受保护 store 上删除日志；日志已不存在时按幂等返回 false，用于删除墓碑的续作与收尾。
    ///     </para>
    ///     <para>
    ///         Deletes the log inside an already-resolved protected store; a log that no longer exists returns false
    ///         idempotently, which deletion-tombstone continuation and cleanup rely on.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         调用方必须传入已经由受保护路径解析校验过的 store 目录；本重载不再重复解析工作区，也不放宽叶子校验
    ///         与 writer 锁。日志被占用时抛出 <see cref="SessionStoreException" />，墓碑保持待收尾。
    ///     </para>
    ///     <para>
    ///         The caller must pass a store directory already validated through protected-path resolution; this
    ///         overload neither re-resolves the workspace nor relaxes the leaf checks or the writer lock. A held log
    ///         throws <see cref="SessionStoreException" /> and leaves the tombstone pending.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     <para>是否删除了日志；false 表示日志原本就不存在。</para>
    ///     <para>Whether a log was deleted; false means it was already absent.</para>
    /// </returns>
    internal static Task<bool> TryDeleteResolvedAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken) =>
        DeleteCoreAsync(Path.GetFullPath(storeDirectory), sessionId, false, cancellationToken);

    /// <summary>
    ///     <para>
    ///         校验会话 ID 与 store 目录，取得 writer 锁后删除日志；<paramref name="requireLog" /> 为 true 时日志
    ///         缺失按失败报告，否则按幂等成功返回 false。
    ///     </para>
    ///     <para>
    ///         Validates the session id and store directory, then deletes the log under its writer lock; a missing log
    ///         fails when <paramref name="requireLog" /> is true and returns false idempotently otherwise.
    ///     </para>
    /// </summary>
    private static async Task<bool> DeleteCoreAsync(
        string directory, string sessionId, bool requireLog, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));

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
            {
                if (requireLog)
                    throw new SessionStoreException($"Session log '{logPath}' no longer exists.");
                return false;
            }

            try
            {
                File.Delete(logPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new SessionStoreException($"Could not delete session log '{logPath}': {exception.Message}",
                                                exception);
            }

            return true;
        }
    }
}
