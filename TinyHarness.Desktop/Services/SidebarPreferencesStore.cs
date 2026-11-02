using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Desktop.Models;

namespace TinyHarness.Desktop.Services;

/// <summary>
///     <para>读取并原子保存桌面侧栏偏好设置，使用源生成 JSON 元数据以支持 NativeAOT。</para>
///     <para>Loads and atomically saves desktop sidebar preferences using source-generated JSON metadata for NativeAOT.</para>
/// </summary>
internal static class SidebarPreferencesStore
{
    /// <summary>
    ///     <para>侧栏偏好文件名。</para>
    ///     <para>The sidebar-preferences file name.</para>
    /// </summary>
    private const string FileName = "desktop-sidebar.json";

    /// <summary>
    ///     <para>跨进程保存锁的文件名后缀。</para>
    ///     <para>The file-name suffix for the cross-process save lock.</para>
    /// </summary>
    private const string SaveLockSuffix = ".lock";

    /// <summary>
    ///     <para>偏好文件的最大字节数。</para>
    ///     <para>The maximum sidebar-preferences file size in bytes.</para>
    /// </summary>
    private const int MaxFileBytes = 1 << 20;

    /// <summary>
    ///     <para>单个文件允许保存的存储目录数上限。</para>
    ///     <para>The maximum number of store directories allowed in one file.</para>
    /// </summary>
    private const int MaxStores = 1000;

    /// <summary>
    ///     <para>每个项目顺序列表的条目上限。</para>
    ///     <para>The entry limit for each project-order list.</para>
    /// </summary>
    private const int MaxProjectsPerOrder = 1000;

    /// <summary>
    ///     <para>每个存储目录的置顶会话数上限。</para>
    ///     <para>The pinned-session limit per store directory.</para>
    /// </summary>
    private const int MaxPinnedSessionsPerStore = 10_000;

    /// <summary>
    ///     <para>每个存储目录的手动会话顺序条目上限。</para>
    ///     <para>The manual-session-order entry limit per store directory.</para>
    /// </summary>
    internal const int MaxSessionOrderEntries = 10_000;

    /// <summary>
    ///     <para>单个文件中项目和会话 ID 的总条目上限。</para>
    ///     <para>The total project and session ID entry limit per file.</para>
    /// </summary>
    private const int MaxTotalEntries = 30_000;

    /// <summary>
    ///     <para>项目和会话 ID 的最大字符数。</para>
    ///     <para>The maximum character count for project and session IDs.</para>
    /// </summary>
    private const int MaxIdentifierLength = 256;

    /// <summary>
    ///     <para>存储目录路径的最大字符数。</para>
    ///     <para>The maximum character count for a store-directory path.</para>
    /// </summary>
    private const int MaxStoreDirectoryLength = 32_768;

    /// <summary>
    ///     <para>获取保存锁的最大等待时间。</para>
    ///     <para>The maximum wait time for acquiring the save lock.</para>
    /// </summary>
    private const int SaveLockTimeoutMs = 5000;

    /// <summary>
    ///     <para>获取保存锁时的重试间隔。</para>
    ///     <para>The retry interval when acquiring the save lock.</para>
    /// </summary>
    private const int SaveLockRetryDelayMs = 25;

    /// <summary>
    ///     <para>提交临时文件时的共享冲突重试时限。</para>
    ///     <para>The retry window for sharing conflicts while committing the temporary file.</para>
    /// </summary>
    private const int CommitRetryTimeoutMs = 2000;

    /// <summary>
    ///     <para>提交临时文件时的重试间隔。</para>
    ///     <para>The retry interval while committing the temporary file.</para>
    /// </summary>
    private const int CommitRetryDelayMs = 25;

    /// <summary>
    ///     <para>Windows 文件共享冲突的 HRESULT。</para>
    ///     <para>The HRESULT for a Windows file-sharing violation.</para>
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    /// <summary>
    ///     <para>Windows 文件锁冲突的 HRESULT。</para>
    ///     <para>The HRESULT for a Windows file-lock violation.</para>
    /// </summary>
    private const int LockViolationHResult = unchecked((int)0x80070021);

    /// <summary>
    ///     <para>拒绝非法字节的严格 UTF-8 解码器。</para>
    ///     <para>A strict UTF-8 decoder that rejects invalid byte sequences.</para>
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>返回与用户配置文件同目录的侧栏偏好文件绝对路径。</para>
    ///     <para>Returns the absolute sidebar-preferences path beside the user configuration file.</para>
    /// </summary>
    /// <param name="userConfigPath">
    ///     <para>用户配置文件路径。</para>
    ///     <para>The user configuration file path.</para>
    /// </param>
    /// <returns>
    ///     <para>侧栏偏好文件的绝对路径。</para>
    ///     <para>The absolute path to the sidebar-preferences file.</para>
    /// </returns>
    public static string FilePathForUserConfig(string userConfigPath)
    {
        var fullConfigPath = NormalizeFilePath(userConfigPath, nameof(userConfigPath));
        var directory      = Path.GetDirectoryName(fullConfigPath);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException($"User configuration path '{userConfigPath}' has no parent directory.",
                                        nameof(userConfigPath));

        return Path.Combine(directory, FileName);
    }

    /// <summary>
    ///     <para>读取并校验侧栏偏好；文件缺失时返回默认设置，文件损坏或超预算时保留原文件并报错。</para>
    ///     <para>
    ///         Loads and validates sidebar preferences; returns defaults when missing and preserves damaged or oversized
    ///         files.
    ///     </para>
    /// </summary>
    /// <param name="filePath">
    ///     <para>侧栏偏好文件路径。</para>
    ///     <para>The sidebar-preferences file path.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消读取。</para>
    ///     <para>Cancels the read.</para>
    /// </param>
    /// <returns>
    ///     <para>已校验的侧栏偏好设置。</para>
    ///     <para>The validated sidebar preferences.</para>
    /// </returns>
    public static async Task<SidebarPreferences> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        var fullPath = NormalizeFilePath(filePath, nameof(filePath));
        cancellationToken.ThrowIfCancellationRequested();

        byte[] bytes;
        try
        {
            var attributes = TryGetAttributes(fullPath);
            if (attributes is null) return new SidebarPreferences();

            EnsureRegularLeaf(fullPath, "Sidebar preferences");
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096,
                                                    FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes)
                throw InvalidPreferences(fullPath,
                                         $"The file exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");

            bytes = await ReadBoundedAsync(stream, fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return new SidebarPreferences();
        }
        catch (DirectoryNotFoundException)
        {
            return new SidebarPreferences();
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not load sidebar preferences '{fullPath}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Could not load sidebar preferences '{fullPath}': {ex.Message}", ex);
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw InvalidPreferences(fullPath, $"The file is not valid UTF-8: {ex.Message}", ex);
        }

        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];

        SidebarPreferences? preferences;
        try
        {
            preferences = JsonSerializer.Deserialize(json, SidebarPreferencesJsonContext.Default.SidebarPreferences);
        }
        catch (JsonException ex)
        {
            throw InvalidPreferences(fullPath, $"The file contains invalid JSON: {ex.Message}", ex);
        }

        if (preferences is null)
            throw InvalidPreferences(fullPath, "The file must contain a JSON object.");

        ValidatePreferences(preferences, fullPath);
        return preferences;
    }

    /// <summary>
    ///     <para>校验设置与大小预算，在同目录临时文件中写入并 flush 后原子替换目标文件。</para>
    ///     <para>
    ///         Validates preferences and size limits, then flushes a same-directory temporary file before atomically
    ///         replacing the target.
    ///     </para>
    /// </summary>
    /// <param name="filePath">
    ///     <para>侧栏偏好文件路径。</para>
    ///     <para>The sidebar-preferences file path.</para>
    /// </param>
    /// <param name="preferences">
    ///     <para>待保存的偏好设置。</para>
    ///     <para>The preferences to save.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消锁等待与文件写入。</para>
    ///     <para>Cancels lock waiting and file writes.</para>
    /// </param>
    public static async Task SaveAsync(
        string filePath, SidebarPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var fullPath = NormalizeFilePath(filePath, nameof(filePath));
        ValidatePreferences(preferences, fullPath);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences,
                                                        SidebarPreferencesJsonContext.Default.SidebarPreferences);
        if (bytes.Length > MaxFileBytes)
            throw InvalidPreferences(fullPath,
                                     $"Serialized preferences exceed the budget of {MaxFileBytes} bytes; reduce stored sidebar entries.");

        var directory = Path.GetDirectoryName(fullPath)!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);

            await using var saveLock = await OpenSaveLockAsync(fullPath, cancellationToken).ConfigureAwait(false);
            EnsureRegularLeaf(fullPath, "Sidebar preferences");

            var tempPath  = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var created   = false;
            var committed = false;
            try
            {
                await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                       FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    created = true;
                    await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                await CommitTempAsync(tempPath, fullPath, cancellationToken).ConfigureAwait(false);
                committed = true;
            }
            catch
            {
                if (created && !committed) TryDeleteTempFile(tempPath);

                throw;
            }
        }
        catch (IOException ex)
        {
            throw new IOException($"Could not save sidebar preferences '{fullPath}': {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException($"Could not save sidebar preferences '{fullPath}': {ex.Message}", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new NotSupportedException($"Could not save sidebar preferences '{fullPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>读取至多 1 MiB 加一个字节，以检测文件在长度检查后增长的情况。</para>
    ///     <para>Reads at most 1 MiB plus one byte to detect growth after the initial length check.</para>
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        FileStream stream, string filePath, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxFileBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            length += read;
        }

        if (length > MaxFileBytes)
            throw InvalidPreferences(filePath,
                                     $"The file exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");

        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>
    ///     <para>校验 schema、存储目录、列表内容、重复项和条目预算。</para>
    ///     <para>Validates the schema, store directories, list contents, duplicates, and entry budgets.</para>
    /// </summary>
    private static void ValidatePreferences(SidebarPreferences preferences, string filePath)
    {
        if (preferences.Schema is null)
            throw InvalidPreferences(filePath, "Field 'schema' must not be null.");

        if (preferences.Schema != "tinyharness.desktop-sidebar/1")
            throw InvalidPreferences(filePath,
                                     $"Unsupported schema '{preferences.Schema}'; expected 'tinyharness.desktop-sidebar/1'.");

        if (preferences.Stores is null)
            throw InvalidPreferences(filePath, "Field 'stores' must not be null.");

        if (preferences.Stores.Count > MaxStores)
            throw InvalidPreferences(filePath, $"The file exceeds its budget of {MaxStores} stores.");

        var pathComparer     = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var storeDirectories = new HashSet<string>(pathComparer);
        var totalEntries     = 0;
        for (var i = 0; i < preferences.Stores.Count; i++)
        {
            var store = preferences.Stores[i];
            if (store is null)
                throw InvalidPreferences(filePath, $"Field 'stores[{i}]' must not be null.");

            var normalizedStoreDirectory = NormalizeStoreDirectory(store.StoreDirectory, filePath, i);
            if (!storeDirectories.Add(normalizedStoreDirectory))
                throw InvalidPreferences(filePath,
                                         $"The file contains duplicate store directory '{store.StoreDirectory}'.");

            totalEntries += ValidateIds(store.ManualProjectOrder, MaxProjectsPerOrder,
                                        $"stores[{i}].manualProjectOrder", filePath);
            totalEntries += ValidateIds(store.ManualSessionOrder, MaxSessionOrderEntries,
                                        $"stores[{i}].manualSessionOrder", filePath);
            totalEntries += ValidateIds(store.LegacyPinnedProjectOrder, MaxProjectsPerOrder,
                                        $"stores[{i}].pinnedProjectOrder", filePath);
            totalEntries += ValidateIds(store.LegacyPinnedSessionIds, MaxPinnedSessionsPerStore,
                                        $"stores[{i}].pinnedSessionIds", filePath);

            if (totalEntries > MaxTotalEntries)
                throw InvalidPreferences(filePath, $"The file exceeds its budget of {MaxTotalEntries} total entries.");
        }
    }

    /// <summary>
    ///     <para>校验并规范化一个偏好记录中的会话存储目录。</para>
    ///     <para>Validates and normalizes a session-store directory in a preference entry.</para>
    /// </summary>
    private static string NormalizeStoreDirectory(string? storeDirectory, string filePath, int index)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw InvalidPreferences(filePath, $"Field 'stores[{index}].storeDirectory' must not be blank.");

        if (storeDirectory.Length > MaxStoreDirectoryLength)
            throw InvalidPreferences(filePath,
                                     $"Field 'stores[{index}].storeDirectory' exceeds its {MaxStoreDirectoryLength}-character budget.");

        try
        {
            if (!Path.IsPathFullyQualified(storeDirectory))
                throw InvalidPreferences(filePath,
                                         $"Field 'stores[{index}].storeDirectory' must be an absolute path.");

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPreferences(filePath,
                                     $"Field 'stores[{index}].storeDirectory' is not a valid path: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>校验 ID 列表的数量、非空值、长度和唯一性，并返回条目数。</para>
    ///     <para>Validates an ID list's count, nonblank values, lengths, and uniqueness, then returns its entry count.</para>
    /// </summary>
    private static int ValidateIds(
        IReadOnlyList<string>? values, int maxCount, string field, string filePath)
    {
        if (values is null)
            throw InvalidPreferences(filePath, $"Field '{field}' must not be null.");

        if (values.Count > maxCount)
            throw InvalidPreferences(filePath, $"Field '{field}' exceeds its budget of {maxCount} entries.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            var id = values[i];
            if (string.IsNullOrWhiteSpace(id))
                throw InvalidPreferences(filePath, $"Field '{field}[{i}]' must not be blank.");

            if (id.Length > MaxIdentifierLength)
                throw InvalidPreferences(filePath,
                                         $"Field '{field}[{i}]' exceeds its {MaxIdentifierLength}-character budget.");

            if (!ids.Add(id))
                throw InvalidPreferences(filePath, $"Field '{field}' contains duplicate ID '{id}'.");
        }

        return values.Count;
    }

    /// <summary>
    ///     <para>获取侧栏偏好文件的跨进程独占锁；有限等待期间响应取消。</para>
    ///     <para>Acquires the sidebar-preferences cross-process exclusive lock with bounded, cancellable waiting.</para>
    /// </summary>
    private static async Task<FileStream> OpenSaveLockAsync(string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath                + SaveLockSuffix;
        var deadline = Environment.TickCount64 + SaveLockTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularLeaf(lockPath, "Sidebar preferences save lock");
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                                      1, FileOptions.Asynchronous);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(SaveLockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new IOException($"Could not acquire sidebar-preferences save lock '{lockPath}' within " +
                                      $"{SaveLockTimeoutMs} ms; another process may be saving preferences.", ex);
            }
        }
    }

    /// <summary>
    ///     <para>把已 flush 的临时文件原子提交为偏好文件，并有限重试 Windows 文件共享冲突。</para>
    ///     <para>Atomically commits the flushed temporary file and briefly retries Windows sharing conflicts.</para>
    /// </summary>
    private static async Task CommitTempAsync(
        string tempPath, string filePath, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + CommitRetryTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRegularLeaf(filePath, "Sidebar preferences");
            try
            {
                if (File.Exists(filePath))
                    File.Replace(tempPath, filePath, null);
                else
                    File.Move(tempPath, filePath);

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
    ///     <para>校验文件路径并返回绝对路径；路径必须指向文件而非根目录。</para>
    ///     <para>
    ///         Validates a file path and returns its absolute form; the path must identify a file rather than a root
    ///         directory.
    ///     </para>
    /// </summary>
    private static string NormalizeFilePath(string filePath, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath, parameterName);
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
                throw new ArgumentException($"File path '{filePath}' must identify a file.", parameterName);

            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"File path '{filePath}' is invalid: {ex.Message}", parameterName, ex);
        }
    }

    /// <summary>
    ///     <para>拒绝文件、锁或临时目标为符号链接、reparse point 或目录占位。</para>
    ///     <para>Rejects symbolic links, reparse points, and directory placeholders for files, locks, and temporary targets.</para>
    /// </summary>
    private static void EnsureRegularLeaf(string path, string description)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is null) return;

        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw InvalidPreferences(path, $"{description} must not be a link or reparse point.");

        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw InvalidPreferences(path, $"{description} must be a file, not a directory.");
    }

    /// <summary>
    ///     <para>按名字读取文件属性；文件或父目录缺失时返回 null，其他错误继续传播。</para>
    ///     <para>
    ///         Reads file attributes by name; returns null when the file or parent directory is missing and propagates other
    ///         errors.
    ///     </para>
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
    ///     <para>创建包含文件路径的无效偏好数据异常。</para>
    ///     <para>Creates an invalid-preferences exception that includes the file path.</para>
    /// </summary>
    private static InvalidDataException InvalidPreferences(string     filePath, string message,
                                                           Exception? innerException = null) =>
        new($"Sidebar preferences '{filePath}' are invalid: {message}", innerException);

    /// <summary>
    ///     <para>尽力删除本次保存创建但尚未提交的唯一临时文件，不覆盖原始保存错误。</para>
    ///     <para>Best-effort deletes this save's unique, uncommitted temporary file without masking the original failure.</para>
    /// </summary>
    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清理失败时保留原始保存异常；临时文件名独立且不参与读取。
        }
    }
}

/// <summary>
///     <para>为侧栏偏好类型生成 NativeAOT 可用的 JSON 元数据。</para>
///     <para>Generates NativeAOT-compatible JSON metadata for sidebar-preference types.</para>
/// </summary>
[JsonSerializable(typeof(SidebarPreferences))]
[JsonSerializable(typeof(SidebarStorePreferences))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SidebarPreferencesJsonContext : JsonSerializerContext;
