using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>读取并更新 session store 中的会话归档状态；更新使用跨进程锁和同目录替换。</para>
///     <para>Loads and updates session archive state using a cross-process lock and same-directory replacement.</para>
/// </summary>
/// <remarks>
///     <para>调用方须先校验受保护的 session store；归档状态不改写会话日志或对话历史。</para>
///     <para>
///         Callers must validate the protected session store first; archive state never rewrites session logs or
///         history.
///     </para>
/// </remarks>
public static class SessionStoreArchiveStore
{
    public const string FileName       = "archived-sessions.json";
    public const string SaveLockSuffix = ".lock";
    public const int    MaxFileBytes   = 1 << 20;

    private const int SaveLockTimeoutMs       = 5000;
    private const int RetryDelayMs            = 25;
    private const int CommitRetryTimeoutMs    = 2000;
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult    = unchecked((int)0x80070021);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>返回指定 session store 中的归档状态路径。</para>
    ///     <para>Returns the archive-state path in the specified session store.</para>
    /// </summary>
    public static string FilePath(string storeDirectory) =>
        Path.Combine(NormalizeStoreDirectory(storeDirectory), FileName);

    /// <summary>
    ///     <para>读取归档状态并将会话 ID 规范为小写；文件缺失时返回空状态，不创建文件或目录。</para>
    ///     <para>
    ///         Loads archive state and normalizes session IDs to lowercase; a missing file returns an empty state without
    ///         creating files or directories.
    ///     </para>
    /// </summary>
    public static async Task<SessionStoreArchives> LoadAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        var filePath = FilePath(storeDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        return await LoadCoreAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>校验 32 位十六进制会话 ID 并将其规范为小写；归档状态重复设置幂等，并在锁内重读最新文件。</para>
    ///     <para>
    ///         Validates a 32-character hexadecimal session ID and normalizes it to lowercase; archive-state updates are
    ///         idempotent and reload under the save lock.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     <para>会话 ID 不是 32 位十六进制标识符。</para>
    ///     <para>The session ID is not a 32-character hexadecimal identifier.</para>
    /// </exception>
    public static async Task<SessionStoreArchives> SetArchivedAsync(
        string storeDirectory, string sessionId, bool isArchived, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("Session ID must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));
        sessionId = sessionId.ToLowerInvariant();

        var filePath = FilePath(storeDirectory);
        EnsureExistingStoreDirectory(filePath);
        await using var saveLock = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        var             current  = await LoadCoreAsync(filePath, cancellationToken).ConfigureAwait(false);
        var             ids      = current.ArchivedSessionIds.ToList();
        var             contains = ids.Contains(sessionId, StringComparer.Ordinal);
        if (contains == isArchived) return current;
        if (isArchived) ids.Add(sessionId);
        else ids.Remove(sessionId);

        var candidate = current with { ArchivedSessionIds = ids };
        await SaveAsync(filePath, candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    private static async Task<SessionStoreArchives> LoadCoreAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath)!;
        SessionStorePaths.EnsureStoreLeaf(directory, filePath);
        EnsureRegularLeaf(filePath, "Session-store archive state");
        if (TryGetAttributes(filePath) is null) return SessionStoreArchives.Empty();

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096,
                                                    FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes)
                throw new SessionStoreArchiveException(
                                                       $"Archive state '{filePath}' exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");
            using var buffer = new MemoryStream((int)stream.Length);
            var       chunk  = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxFileBytes)
                    throw new SessionStoreArchiveException(
                                                           $"Archive state '{filePath}' exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");
                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionStoreArchiveException($"Archive state '{filePath}' disappeared while it was being read.",
                                                   ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionStoreArchiveException(
                                                   $"Archive-state directory for '{filePath}' disappeared while it was being read.",
                                                   ex);
        }
        catch (IOException ex)
        {
            throw new SessionStoreArchiveException($"Could not read archive state '{filePath}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SessionStoreArchiveException($"Could not read archive state '{filePath}': {ex.Message}", ex);
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SessionStoreArchiveException($"Archive state '{filePath}' is not valid UTF-8: {ex.Message}", ex);
        }

        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        SessionStoreArchives? archives;
        try
        {
            archives = JsonSerializer.Deserialize(json, SessionStoreArchiveJsonContext.Default.SessionStoreArchives);
        }
        catch (JsonException ex)
        {
            throw new SessionStoreArchiveException($"Archive state '{filePath}' is damaged: {ex.Message}", ex);
        }

        if (archives is null)
            throw new SessionStoreArchiveException($"Archive state '{filePath}' must contain a JSON object.");
        archives.Validate(filePath);
        return archives with
        {
            ArchivedSessionIds = archives.ArchivedSessionIds.Select(id => id.ToLowerInvariant()).ToArray()
        };
    }

    private static async Task SaveAsync(
        string filePath, SessionStoreArchives archives, CancellationToken cancellationToken)
    {
        archives.Validate(filePath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
                                                        archives,
                                                        SessionStoreArchiveJsonContext.Default.SessionStoreArchives);
        if (bytes.Length > MaxFileBytes)
            throw new SessionStoreArchiveException(
                                                   $"Serialized archive state '{filePath}' exceeds its budget of {MaxFileBytes} bytes.");

        var tempPath  = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created   = false;
        var committed = false;
        try
        {
            EnsureExistingStoreDirectory(filePath);
            SessionStorePaths.EnsureStoreLeaf(Path.GetDirectoryName(filePath)!, tempPath);
            EnsureRegularLeaf(tempPath, "Session-store archive temporary file");
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureRegularLeaf(filePath, "Session-store archive state");
            await CommitTempAsync(tempPath, filePath, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            if (created && !committed) TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static async Task CommitTempAsync(
        string tempPath, string filePath, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + CommitRetryTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularLeaf(filePath, "Session-store archive state");
            try
            {
                if (File.Exists(filePath)) File.Replace(tempPath, filePath, null);
                else File.Move(tempPath, filePath);
                return;
            }
            catch (IOException ex) when (
                ex.HResult is SharingViolationHResult or LockViolationHResult &&
                Environment.TickCount64 < deadline)
            {
                await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<FileStream> OpenSaveLockAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath + SaveLockSuffix;
        EnsureExistingStoreDirectory(filePath);
        SessionStorePaths.EnsureStoreLeaf(Path.GetDirectoryName(filePath)!, lockPath);
        EnsureRegularLeaf(lockPath, "Session-store archive save lock");
        var deadline = Environment.TickCount64 + SaveLockTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                      FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(RetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new SessionStoreArchiveException(
                                                       $"Could not acquire archive-state save lock '{lockPath}' within {SaveLockTimeoutMs} ms; retry the operation.",
                                                       ex);
            }
        }
    }

    private static string NormalizeStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new
                SessionStoreArchiveException($"Session-store directory '{storeDirectory}' must be an absolute path.");
        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
            if (fullPath.Length > 32_768)
                throw
                    new SessionStoreArchiveException($"Session-store directory '{fullPath}' exceeds its path budget.");
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new
                SessionStoreArchiveException($"Session-store directory '{storeDirectory}' is invalid: {ex.Message}",
                                             ex);
        }
    }

    private static void EnsureExistingStoreDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            throw new SessionStoreArchiveException(
                                                   $"Session-store directory for archive state '{filePath}' no longer exists.");

        SessionStorePaths.EnsureStoreLeaf(directory, filePath);
    }

    private static void EnsureRegularLeaf(string path, string description)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new
                SessionStoreArchiveException($"{description} '{path}' is a link or reparse point and is not allowed.");
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new SessionStoreArchiveException($"{description} '{path}' is a directory, not a file.");
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 临时文件清理失败时保留原始保存异常。
        }
    }
}

[JsonSerializable(typeof(SessionStoreArchives))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SessionStoreArchiveJsonContext : JsonSerializerContext;
