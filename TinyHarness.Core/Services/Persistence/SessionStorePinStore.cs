using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>读取并更新会话存储目录中的置顶项目与会话状态；更新使用跨进程锁和同目录原子替换。</para>
///     <para>
///         Loads and updates pinned project and session state in a session store using a cross-process lock and
///         same-directory replacement.
///     </para>
/// </summary>
/// <remarks>
///     <para>调用方须先解析并校验受保护的 session store；本类型校验状态、锁和临时文件叶子，不替代目录保护。</para>
///     <para>
///         Callers must resolve and validate the protected session store first; this type validates the state, lock, and
///         temporary file leaves but does not replace directory protection.
///     </para>
/// </remarks>
public static class SessionStorePinStore
{
    /// <summary>
    ///     <para>每个会话存储目录中的置顶状态文件名。</para>
    ///     <para>The pin-state file name inside each session store.</para>
    /// </summary>
    public const string FileName = "pinned-items.json";

    /// <summary>
    ///     <para>保存锁文件名后缀。</para>
    ///     <para>The save-lock file-name suffix.</para>
    /// </summary>
    public const string SaveLockSuffix = ".lock";

    /// <summary>
    ///     <para>置顶状态文件的最大字节数。</para>
    ///     <para>The maximum pin-state file size in bytes.</para>
    /// </summary>
    public const int MaxFileBytes = 1 << 20;

    /// <summary>
    ///     <para>每个会话存储目录允许的置顶项目数上限。</para>
    ///     <para>The maximum number of pinned projects per session store.</para>
    /// </summary>
    public const int MaxProjectIds = SessionStorePins.MaxProjectIds;

    /// <summary>
    ///     <para>每个会话存储目录允许的置顶会话数上限。</para>
    ///     <para>The maximum number of pinned sessions per session store.</para>
    /// </summary>
    public const int MaxSessionIds = SessionStorePins.MaxSessionIds;

    /// <summary>
    ///     <para>单个置顶 ID 的字符数上限。</para>
    ///     <para>The maximum character count for a pinned item ID.</para>
    /// </summary>
    public const int MaxIdentifierLength = SessionStorePins.MaxIdentifierLength;

    private const int SaveLockTimeoutMs       = 5000;
    private const int SaveLockRetryDelayMs    = 25;
    private const int CommitRetryTimeoutMs    = 2000;
    private const int CommitRetryDelayMs      = 25;
    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult    = unchecked((int)0x80070021);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>返回指定会话存储目录中的置顶状态文件路径。</para>
    ///     <para>Returns the pin-state file path inside the specified session store.</para>
    /// </summary>
    public static string FilePath(string storeDirectory) =>
        Path.Combine(NormalizeStoreDirectory(storeDirectory), FileName);

    /// <summary>
    ///     <para>读取置顶状态；文件缺失时返回空状态，不创建存储目录或文件。</para>
    ///     <para>Loads pin state; a missing file returns an empty state without creating the store directory or file.</para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>已校验的绝对 session store 路径。</para>
    ///     <para>The validated absolute session-store path.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消文件打开与读取。</para>
    ///     <para>Cancels file opening and reading.</para>
    /// </param>
    public static async Task<SessionStorePins> LoadAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var filePath = FilePath(storeDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        return await LoadCoreAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>读取当前置顶状态；旧侧栏置顶列表尚未导入时与 Core 状态合并并记录迁移标记。</para>
    ///     <para>
    ///         Loads current pin state, merging non-empty legacy sidebar pin lists once while recording an import marker.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>已校验的绝对 session store 路径。</para>
    ///     <para>The validated absolute session-store path.</para>
    /// </param>
    /// <param name="legacyPinnedProjectIds">
    ///     <para>旧 Desktop 偏好中的有序项目 ID。</para>
    ///     <para>Ordered project IDs from legacy Desktop preferences.</para>
    /// </param>
    /// <param name="legacyPinnedSessionIds">
    ///     <para>旧 Desktop 偏好中的会话 ID。</para>
    ///     <para>Session IDs from legacy Desktop preferences.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消锁等待、文件读取与保存。</para>
    ///     <para>Cancels lock waiting, file reads, and writes.</para>
    /// </param>
    public static async Task<SessionStorePins> LoadOrMigrateAsync(
        string                storeDirectory,         IReadOnlyList<string> legacyPinnedProjectIds,
        IReadOnlyList<string> legacyPinnedSessionIds, CancellationToken     cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(legacyPinnedProjectIds);
        ArgumentNullException.ThrowIfNull(legacyPinnedSessionIds);

        var filePath = FilePath(storeDirectory);
        var legacy = new SessionStorePins
        {
            Schema           = SessionStorePins.CurrentSchema,
            PinnedProjectIds = legacyPinnedProjectIds.ToArray(),
            PinnedSessionIds = legacyPinnedSessionIds.ToArray()
        };
        legacy.Validate(filePath);

        var attributes = TryGetAttributes(filePath);
        if (attributes is null                 &&
            legacy.PinnedProjectIds.Count == 0 &&
            legacy.PinnedSessionIds.Count == 0)
            return SessionStorePins.Empty();

        EnsureParentDirectory(filePath);
        await using var saveLock = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        var             current  = await LoadCoreAsync(filePath, cancellationToken).ConfigureAwait(false);
        if (current.LegacySidebarPinsImported ||
            (legacy.PinnedProjectIds.Count == 0 && legacy.PinnedSessionIds.Count == 0))
            return current;

        var migrated = current with
        {
            PinnedProjectIds = MergeIds(current.PinnedProjectIds, legacy.PinnedProjectIds),
            PinnedSessionIds = MergeIds(current.PinnedSessionIds, legacy.PinnedSessionIds),
            LegacySidebarPinsImported = true
        };
        await SaveAsync(filePath, migrated, cancellationToken).ConfigureAwait(false);
        return migrated;
    }

    /// <summary>
    ///     <para>切换项目置顶状态；新置顶项目进入列表顶部，取消置顶时移除。</para>
    ///     <para>Toggles a project's pin state; newly pinned projects enter at the top and unpinning removes them.</para>
    /// </summary>
    public static Task<SessionStorePins> ToggleProjectPinAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken) =>
        UpdateAsync(storeDirectory, projectId, static (pins, id) =>
        {
            var projectIds = pins.PinnedProjectIds.ToList();
            if (!projectIds.Remove(id)) projectIds.Insert(0, id);
            return pins with { PinnedProjectIds = projectIds };
        }, cancellationToken);

    /// <summary>
    ///     <para>将项目置顶状态设为指定值；重复设置不会改变现有顺序。</para>
    ///     <para>Sets a project's pin state; repeating the same value preserves its current order.</para>
    /// </summary>
    public static Task<SessionStorePins> SetProjectPinnedAsync(
        string storeDirectory, string projectId, bool isPinned, CancellationToken cancellationToken) =>
        UpdateAsync(storeDirectory, projectId, (pins, id) =>
        {
            var projectIds        = pins.PinnedProjectIds.ToList();
            var isCurrentlyPinned = projectIds.Contains(id, StringComparer.Ordinal);
            if (isPinned == isCurrentlyPinned) return pins;
            if (isPinned) projectIds.Insert(0, id);
            else projectIds.Remove(id);
            return pins with { PinnedProjectIds = projectIds };
        }, cancellationToken);

    /// <summary>
    ///     <para>切换会话置顶状态；新置顶会话插入列表顶部，取消置顶时移除。</para>
    ///     <para>Toggles a session's pin state; newly pinned sessions enter at the top and unpinning removes them.</para>
    /// </summary>
    public static Task<SessionStorePins> ToggleSessionPinAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken) =>
        UpdateAsync(storeDirectory, sessionId, static (pins, id) =>
        {
            var sessionIds = pins.PinnedSessionIds.ToList();
            if (!sessionIds.Remove(id)) sessionIds.Insert(0, id);
            return pins with { PinnedSessionIds = sessionIds };
        }, cancellationToken);

    /// <summary>
    ///     <para>将会话置顶状态设为指定值；重复设置不会改变其他会话状态。</para>
    ///     <para>Sets a session's pin state; repeating the same value leaves other session states unchanged.</para>
    /// </summary>
    public static Task<SessionStorePins> SetSessionPinnedAsync(
        string storeDirectory, string sessionId, bool isPinned, CancellationToken cancellationToken) =>
        UpdateAsync(storeDirectory, sessionId, (pins, id) =>
        {
            var sessionIds        = pins.PinnedSessionIds.ToList();
            var isCurrentlyPinned = sessionIds.Contains(id, StringComparer.Ordinal);
            if (isPinned == isCurrentlyPinned) return pins;
            if (isPinned) sessionIds.Insert(0, id);
            else sessionIds.Remove(id);
            return pins with { PinnedSessionIds = sessionIds };
        }, cancellationToken);

    /// <summary>
    ///     <para>按拖放位置调整置顶项目顺序；任一项目不在置顶列表时不写入。</para>
    ///     <para>Reorders pinned projects by a drop position; no write occurs unless both projects are pinned.</para>
    /// </summary>
    public static Task<SessionStorePins> MovePinnedProjectAsync(
        string            storeDirectory, string sourceProjectId, string targetProjectId, bool insertAfter,
        CancellationToken cancellationToken) =>
        UpdateAsync(storeDirectory, sourceProjectId, (pins, sourceId) =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetProjectId);
            var projectIds  = pins.PinnedProjectIds.ToList();
            var sourceIndex = projectIds.IndexOf(sourceId);
            var targetIndex = projectIds.IndexOf(targetProjectId);
            if (sourceIndex < 0 || targetIndex < 0) return pins;

            projectIds.RemoveAt(sourceIndex);
            var insertIndex = targetIndex + (insertAfter ? 1 : 0);
            if (sourceIndex < insertIndex) insertIndex--;
            projectIds.Insert(Math.Clamp(insertIndex, 0, projectIds.Count), sourceId);
            return pins with { PinnedProjectIds = projectIds };
        }, cancellationToken);

    /// <summary>
    ///     <para>在跨进程锁内重读最新状态、应用变更，并仅保存有差异的结果。</para>
    ///     <para>Reads the latest state under the cross-process lock, applies a mutation, and saves only changes.</para>
    /// </summary>
    private static async Task<SessionStorePins> UpdateAsync(
        string            storeDirectory, string id, Func<SessionStorePins, string, SessionStorePins> update,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > MaxIdentifierLength)
            throw new ArgumentException($"Pinned item ID exceeds its {MaxIdentifierLength}-character budget.",
                                        nameof(id));

        var filePath = FilePath(storeDirectory);
        EnsureParentDirectory(filePath);
        await using var saveLock  = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        var             current   = await LoadCoreAsync(filePath, cancellationToken).ConfigureAwait(false);
        var             candidate = update(current, id);
        if (ReferenceEquals(candidate, current) ||
            (candidate.PinnedProjectIds.SequenceEqual(current.PinnedProjectIds, StringComparer.Ordinal) &&
             candidate.PinnedSessionIds.SequenceEqual(current.PinnedSessionIds, StringComparer.Ordinal)))
            return current;

        await SaveAsync(filePath, candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>
    ///     <para>严格读取并校验状态文件；损坏或不支持的文件保持不变并报告。</para>
    ///     <para>Strictly reads and validates the state file, preserving and reporting damaged or unsupported files.</para>
    /// </summary>
    private static async Task<SessionStorePins> LoadCoreAsync(string filePath, CancellationToken cancellationToken)
    {
        EnsureRegularLeaf(filePath, "Session-store pin state");
        var attributes = TryGetAttributes(filePath);
        if (attributes is null) return SessionStorePins.Empty();

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096,
                                                    FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes)
                throw new SessionStorePinException(
                                                   $"Pin state '{filePath}' exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");

            bytes = await ReadBoundedAsync(stream, filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionStorePinException($"Pin state '{filePath}' disappeared while it was being read.", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new
                SessionStorePinException($"Pin-state directory for '{filePath}' disappeared while it was being read.",
                                         ex);
        }
        catch (IOException ex)
        {
            throw new SessionStorePinException($"Could not read pin state '{filePath}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SessionStorePinException($"Could not read pin state '{filePath}': {ex.Message}", ex);
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new SessionStorePinException($"Pin state '{filePath}' is not valid UTF-8: {ex.Message}", ex);
        }

        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        SessionStorePins? pins;
        try
        {
            pins = JsonSerializer.Deserialize(json, SessionStorePinJsonContext.Default.SessionStorePins);
        }
        catch (JsonException ex)
        {
            throw new SessionStorePinException($"Pin state '{filePath}' is damaged: {ex.Message}", ex);
        }

        if (pins is null)
            throw new SessionStorePinException($"Pin state '{filePath}' must contain a JSON object.");

        pins.Validate(filePath);
        return pins;
    }

    /// <summary>
    ///     <para>读取不超过状态文件预算的字节。</para>
    ///     <para>Reads bytes without exceeding the state-file budget.</para>
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        FileStream stream, string filePath, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxFileBytes));
        var       chunk  = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxFileBytes)
                throw new SessionStorePinException(
                                                   $"Pin state '{filePath}' exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     <para>保留 Core 顺序，并按旧列表顺序追加其中尚未存在的 ID。</para>
    ///     <para>Preserves Core order and appends missing IDs in their legacy order.</para>
    /// </summary>
    private static IReadOnlyList<string> MergeIds(IReadOnlyList<string> current, IReadOnlyList<string> legacy)
    {
        var merged = current.ToList();
        var seen   = new HashSet<string>(current, StringComparer.Ordinal);
        foreach (var id in legacy)
            if (seen.Add(id))
                merged.Add(id);
        return merged;
    }

    /// <summary>
    ///     <para>序列化已校验状态到同目录临时文件，再原子提交。</para>
    ///     <para>Serializes validated state to a same-directory temporary file and commits it atomically.</para>
    /// </summary>
    private static async Task SaveAsync(
        string filePath, SessionStorePins pins, CancellationToken cancellationToken)
    {
        pins.Validate(filePath);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(pins, SessionStorePinJsonContext.Default.SessionStorePins);
        if (bytes.Length > MaxFileBytes)
            throw new SessionStorePinException(
                                               $"Serialized pin state '{filePath}' exceeds its budget of {MaxFileBytes} bytes; reduce stored pin entries.");

        var tempPath  = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created   = false;
        var committed = false;
        try
        {
            EnsureRegularLeaf(tempPath, "Session-store pin-state temporary file");
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            EnsureRegularLeaf(filePath, "Session-store pin state");
            await CommitTempAsync(tempPath, filePath, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            if (created && !committed) TryDeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>
    ///     <para>以替换或移动提交临时文件，并短暂重试共享冲突。</para>
    ///     <para>Commits the temporary file by replace or move, briefly retrying sharing conflicts.</para>
    /// </summary>
    private static async Task CommitTempAsync(
        string tempPath, string filePath, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + CommitRetryTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularLeaf(filePath, "Session-store pin state");
            try
            {
                if (File.Exists(filePath)) File.Replace(tempPath, filePath, null);
                else File.Move(tempPath, filePath);
                return;
            }
            catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult &&
                                         Environment.TickCount64 < deadline)
            {
                await Task.Delay(CommitRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     <para>在有限期限内获取独占保存锁。</para>
    ///     <para>Acquires the exclusive save lock within a bounded timeout.</para>
    /// </summary>
    private static async Task<FileStream> OpenSaveLockAsync(string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath + SaveLockSuffix;
        EnsureRegularLeaf(lockPath, "Session-store pin-state save lock");
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
                await Task.Delay(SaveLockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new SessionStorePinException(
                                                   $"Could not acquire pin-state save lock '{lockPath}' within {SaveLockTimeoutMs} ms; retry the operation.",
                                                   ex);
            }
        }
    }

    /// <summary>
    ///     <para>要求绝对路径并返回规范化的存储目录。</para>
    ///     <para>Requires an absolute path and returns the normalized store directory.</para>
    /// </summary>
    private static string NormalizeStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new SessionStorePinException($"Session-store directory '{storeDirectory}' must be an absolute path.");

        try
        {
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
            if (fullPath.Length > 32_768)
                throw new
                    SessionStorePinException($"Session-store directory '{fullPath}' exceeds its 32768-character budget.");

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SessionStorePinException($"Session-store directory '{storeDirectory}' is invalid: {ex.Message}",
                                               ex);
        }
    }

    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>
    ///     <para>拒绝链接、reparse point 或目录形式的状态文件叶子。</para>
    ///     <para>Rejects a state-file leaf that is a link, reparse point, or directory.</para>
    /// </summary>
    private static void EnsureRegularLeaf(string path, string description)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw
                new SessionStorePinException($"{description} '{path}' is a link or reparse point and is not allowed.");
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new SessionStorePinException($"{description} '{path}' is a directory, not a file.");
    }

    /// <summary>
    ///     <para>读取文件属性；文件或父目录不存在时返回 null。</para>
    ///     <para>Reads file attributes and returns null when the file or parent directory is absent.</para>
    /// </summary>
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

    /// <summary>
    ///     <para>尽力清理未提交的临时文件，并保留原始保存异常。</para>
    ///     <para>Best-effort deletes an uncommitted temporary file while preserving the original save error.</para>
    /// </summary>
    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 临时文件清理失败时保留原始保存异常。
        }
    }
}

/// <summary>
///     <para>为置顶状态生成 NativeAOT 可用的 JSON 元数据。</para>
///     <para>Generates NativeAOT-compatible JSON metadata for pin state.</para>
/// </summary>
[JsonSerializable(typeof(SessionStorePins))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SessionStorePinJsonContext : JsonSerializerContext;
