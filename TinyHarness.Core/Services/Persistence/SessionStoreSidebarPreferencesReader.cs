using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         从桌面侧栏偏好文件 <c>desktop-sidebar.json</c> 中只读取迁移所需的一个 store 条目：手动项目顺序、
///         手工会话顺序、折叠分组键，以及旧版置顶列表。其余应用偏好不参与迁移。
///     </para>
///     <para>
///         Reads only the migration-relevant store entry from the desktop sidebar preferences file
///         <c>desktop-sidebar.json</c>: the manual project order, the manual session order, the collapsed group
///         keys, and the legacy pin lists. The remaining application preferences do not participate in migration.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         读取端不创建、不修复也不改写偏好文件；未知成员被忽略，因为该文件属于桌面应用且会继续演进。结构、
///      schema、编码或大小不合法时按损坏拒绝，绝不静默返回空顺序而丢掉用户组织状态。
///     </para>
///     <para>
///         The reader never creates, repairs, or rewrites the preferences file; unknown members are ignored because
///         the file belongs to the desktop application and keeps evolving. Invalid structure, schema, encoding, or
///         size is refused as damage rather than silently yielding empty orders and dropping user organization state.
///     </para>
/// </remarks>
/// <param name="ManualProjectOrder">
///     <para>普通分类中的项目手动顺序，文件顺序即位置顺序。</para>
///     <para>The manual project order of the regular category; file order is position order.</para>
/// </param>
/// <param name="ManualSessionOrder">
///     <para>手工会话顺序，文件顺序即位置顺序。</para>
///     <para>The manual session order; file order is position order.</para>
/// </param>
/// <param name="CollapsedGroupKeys">
///     <para>已折叠的项目或展示组键。</para>
///     <para>The collapsed project or display-group keys.</para>
/// </param>
/// <param name="LegacyPinnedProjectOrder">
///     <para>旧版桌面偏好中的置顶项目顺序；迁入 Core 后由桌面侧清空。</para>
///     <para>The legacy pinned-project order from the desktop preferences; cleared by the desktop after import into Core.</para>
/// </param>
/// <param name="LegacyPinnedSessionIds">
///     <para>旧版桌面偏好中的置顶会话 ID。</para>
///     <para>The legacy pinned session ids from the desktop preferences.</para>
/// </param>
internal sealed record SessionStoreSidebarPreferences(
    IReadOnlyList<string> ManualProjectOrder,
    IReadOnlyList<string> ManualSessionOrder,
    IReadOnlyList<string> CollapsedGroupKeys,
    IReadOnlyList<string> LegacyPinnedProjectOrder,
    IReadOnlyList<string> LegacyPinnedSessionIds)
{
    /// <summary>
    ///     <para>没有对应 store 条目时的空偏好。</para>
    ///     <para>The empty preferences used when the file has no entry for the store.</para>
    /// </summary>
    public static SessionStoreSidebarPreferences Empty() => new([], [], [], [], []);
}

/// <summary>
///     <para>
///         迁移专用的桌面侧栏偏好读取器：严格解析 JSON，按平台路径比较定位目标 store 条目，并校验各列表的
///         条目预算、空白值与重复项。
///     </para>
///     <para>
///         The migration-only desktop sidebar preferences reader: it strictly parses the JSON, locates the target
///         store entry under platform path comparison, and validates each list's entry budget, blank values, and
///         duplicates.
///     </para>
/// </summary>
internal static class SessionStoreSidebarPreferencesReader
{
    /// <summary>
    ///     <para>桌面侧栏偏好文件的 schema 标识。</para>
    ///     <para>The schema identifier of the desktop sidebar preferences file.</para>
    /// </summary>
    public const string CurrentSchema = "tinyharness.desktop-sidebar/1";

    /// <summary>
    ///     <para>迁移使用的文件名，仅用于诊断消息。</para>
    ///     <para>The file name used by migration, for diagnostic messages only.</para>
    /// </summary>
    public const string FileName = "desktop-sidebar.json";

    private const int MaxFileBytes = 1 << 20;

    private const int MaxStores               = 1000;
    private const int MaxProjectsPerOrder     = 1000;
    private const int MaxSessionOrderEntries  = 10_000;
    private const int MaxCollapsedGroupKeys   = 10_000;
    private const int MaxPinnedSessions       = 10_000;
    private const int MaxTotalEntries         = 50_000;
    private const int MaxIdentifierLength     = 256;
    private const int MaxStoreDirectoryLength = 32_768;

    /// <summary>
    ///     <para>拒绝非法字节的严格 UTF-8 解码器。</para>
    ///     <para>A strict UTF-8 decoder that rejects invalid byte sequences.</para>
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>
    ///         读取并严格校验侧栏偏好；目标 store 没有条目时返回空偏好。文件缺失、不可读、编码非法、
    ///         schema 不符、JSON 结构非法或预算超限都以 <see cref="SessionStoreMigrationException" />
    ///         （损坏）拒绝，绝不创建或修复文件。
    ///     </para>
    ///     <para>
    ///         Reads and strictly validates the sidebar preferences, returning empty preferences when the target
    ///         store has no entry. A missing, unreadable, wrongly encoded, wrongly versioned, structurally invalid,
    ///         or over-budget file is refused with <see cref="SessionStoreMigrationException" /> (damaged) and the
    ///         file is never created or repaired.
    ///     </para>
    /// </summary>
    /// <param name="sidebarPreferencesPath">
    ///     <para>偏好文件路径；须为绝对路径。</para>
    ///     <para>The preferences file path; it must be absolute.</para>
    /// </param>
    /// <param name="storeDirectory">
    ///     <para>要匹配的受保护 session store 目录；须为绝对路径。</para>
    ///     <para>The protected session-store directory to match; it must be absolute.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消文件读取与解析。</para>
    ///     <para>Cancels reading and parsing.</para>
    /// </param>
    /// <exception cref="SessionStoreMigrationException">
    ///     <para>路径非法、文件缺失、损坏、编码非法、schema 不符或超出预算。</para>
    ///     <para>The path is invalid, or the file is missing, damaged, wrongly encoded, wrongly versioned, or over budget.</para>
    /// </exception>
    public static async Task<SessionStoreSidebarPreferences> ReadAsync(
        string sidebarPreferencesPath, string storeDirectory, CancellationToken cancellationToken)
    {
        var preferencesPath = NormalizeFilePath(sidebarPreferencesPath);
        var storePath       = NormalizeStoreDirectory(storeDirectory);
        byte[] bytes;
        try
        {
            SessionStorePaths.EnsureStoreLeaf(Path.GetDirectoryName(preferencesPath)!, preferencesPath);
            await using var stream = new FileStream(preferencesPath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096,
                                                    FileOptions.Asynchronous);
            if (stream.Length > MaxFileBytes)
                throw Damaged(preferencesPath,
                              $"The file exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");
            bytes = await ReadBoundedAsync(stream, preferencesPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            throw new SessionStoreMigrationException(
                                                    $"Sidebar preferences '{preferencesPath}' was configured for the import but is missing; " +
                                                    "it cannot be treated as empty without dropping the desktop organization state.",
                                                    SessionStoreFailureKind.Damaged, ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new SessionStoreMigrationException(
                                                    $"Sidebar preferences '{preferencesPath}' was configured for the import but its directory is missing; " +
                                                    "it cannot be treated as empty without dropping the desktop organization state.",
                                                    SessionStoreFailureKind.Damaged, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException or TinyHarness.Core.Exceptions.SessionStoreException)
        {
            throw new SessionStoreMigrationException(
                                                    $"Sidebar preferences '{preferencesPath}' cannot be read for the import: {ex.Message}",
                                                    SessionStoreErrorClassifier.Classify(ex), ex);
        }

        var document = Deserialize(bytes, preferencesPath);
        ValidateDocument(document, preferencesPath);
        if (document.Stores is null) return SessionStoreSidebarPreferences.Empty();

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var store in document.Stores)
        {
            if (store is null || store.StoreDirectory is null) continue;
            if (!comparer.Equals(NormalizeStoreDirectory(store.StoreDirectory), storePath)) continue;

            return new SessionStoreSidebarPreferences(
                store.ManualProjectOrder ?? [],
                store.ManualSessionOrder ?? [],
                store.CollapsedGroupKeys ?? [],
                store.LegacyPinnedProjectOrder ?? [],
                store.LegacyPinnedSessionIds ?? []);
        }

        return SessionStoreSidebarPreferences.Empty();
    }

    /// <summary>
    ///     <para>用严格 UTF-8 与源生成元数据反序列化偏好文档；未知成员被忽略。</para>
    ///     <para>Deserializes the preferences document with strict UTF-8 and source-generated metadata, ignoring unknown members.</para>
    /// </summary>
    private static SidebarPreferencesDocument Deserialize(byte[] bytes, string preferencesPath)
    {
        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw Damaged(preferencesPath, $"The file is not valid UTF-8: {ex.Message}", ex);
        }

        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        SidebarPreferencesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json,
                                                  SessionStoreSidebarPreferencesJsonContext.Default
                                                     .SidebarPreferencesDocument);
        }
        catch (JsonException ex)
        {
            throw Damaged(preferencesPath, $"The file contains invalid JSON: {ex.Message}", ex);
        }

        return document ?? throw Damaged(preferencesPath, "The file must contain a JSON object.");
    }

    /// <summary>
    ///     <para>校验 schema、store 条目、存储目录与各列表的预算、空白值和重复项。</para>
    ///     <para>Validates the schema, store entries, store directories, and each list's budget, blank values, and duplicates.</para>
    /// </summary>
    private static void ValidateDocument(SidebarPreferencesDocument document, string preferencesPath)
    {
        if (document.Schema is null)
            throw Damaged(preferencesPath, "Field 'schema' must not be null.");
        if (document.Schema != CurrentSchema)
            throw Damaged(preferencesPath,
                          $"Unsupported schema '{document.Schema}'; expected '{CurrentSchema}'.");
        if (document.Stores is null) return;
        if (document.Stores.Count > MaxStores)
            throw Damaged(preferencesPath, $"The file exceeds its budget of {MaxStores} stores.");

        var comparer         = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var storeDirectories = new HashSet<string>(comparer);
        var totalEntries     = 0;
        for (var index = 0; index < document.Stores.Count; index++)
        {
            var store = document.Stores[index];
            if (store is null)
                throw Damaged(preferencesPath, $"Field 'stores[{index}]' must not be null.");

            var normalizedDirectory = NormalizeStoreDocumentDirectory(store.StoreDirectory, preferencesPath, index);
            if (!storeDirectories.Add(normalizedDirectory))
                throw Damaged(preferencesPath,
                              $"The file contains duplicate store directory '{store.StoreDirectory}'.");

            totalEntries += ValidateIds(store.ManualProjectOrder, MaxProjectsPerOrder,
                                        $"stores[{index}].manualProjectOrder", preferencesPath);
            totalEntries += ValidateIds(store.ManualSessionOrder, MaxSessionOrderEntries,
                                        $"stores[{index}].manualSessionOrder", preferencesPath);
            totalEntries += ValidateIds(store.CollapsedGroupKeys, MaxCollapsedGroupKeys,
                                        $"stores[{index}].collapsedGroupKeys", preferencesPath);
            totalEntries += ValidateIds(store.LegacyPinnedProjectOrder, MaxProjectsPerOrder,
                                        $"stores[{index}].pinnedProjectOrder", preferencesPath);
            totalEntries += ValidateIds(store.LegacyPinnedSessionIds, MaxPinnedSessions,
                                        $"stores[{index}].pinnedSessionIds", preferencesPath);
            if (totalEntries > MaxTotalEntries)
                throw Damaged(preferencesPath,
                              $"The file exceeds its budget of {MaxTotalEntries} total entries.");
        }
    }

    /// <summary>
    ///     <para>校验一个 ID 列表的条目数、空白值、长度和唯一性，并返回条目数；null 按空列表处理。</para>
    ///     <para>Validates one id list's count, blank values, lengths, and uniqueness, returning its entry count; null is treated as empty.</para>
    /// </summary>
    private static int ValidateIds(
        List<string>? values, int maxCount, string field, string preferencesPath)
    {
        if (values is null) return 0;
        if (values.Count > maxCount)
            throw Damaged(preferencesPath, $"Field '{field}' exceeds its budget of {maxCount} entries.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var id = values[index];
            if (string.IsNullOrWhiteSpace(id))
                throw Damaged(preferencesPath, $"Field '{field}[{index}]' must not be blank.");
            if (id.Length > MaxIdentifierLength)
                throw Damaged(preferencesPath,
                              $"Field '{field}[{index}]' exceeds its {MaxIdentifierLength}-character budget.");
            if (!ids.Add(id))
                throw Damaged(preferencesPath, $"Field '{field}' contains duplicate ID '{id}'.");
        }

        return values.Count;
    }

    /// <summary>
    ///     <para>规范化 store 条目中的目录，用于与目标 store 做平台路径比较。</para>
    ///     <para>Normalizes the directory of a store entry for platform path comparison with the target store.</para>
    /// </summary>
    private static string NormalizeStoreDocumentDirectory(string? storeDirectory, string preferencesPath, int index)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw Damaged(preferencesPath, $"Field 'stores[{index}].storeDirectory' must not be blank.");
        if (storeDirectory.Length > MaxStoreDirectoryLength)
            throw Damaged(preferencesPath,
                          $"Field 'stores[{index}].storeDirectory' exceeds its {MaxStoreDirectoryLength}-character budget.");
        try
        {
            if (!Path.IsPathFullyQualified(storeDirectory))
                throw Damaged(preferencesPath,
                              $"Field 'stores[{index}].storeDirectory' must be an absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Damaged(preferencesPath,
                          $"Field 'stores[{index}].storeDirectory' is not a valid path: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>读取至多 1 MiB 加一个字节，以发现长度检查后继续增长的文件。</para>
    ///     <para>Reads at most 1 MiB plus one byte to detect a file that kept growing after the length check.</para>
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(
        FileStream stream, string preferencesPath, CancellationToken cancellationToken)
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
            throw Damaged(preferencesPath,
                          $"The file exceeds its budget of {MaxFileBytes} bytes; keep it and inspect it manually.");
        return buffer.AsSpan(0, length).ToArray();
    }

    /// <summary>
    ///     <para>校验偏好文件路径并返回绝对路径；路径必须指向文件。</para>
    ///     <para>Validates the preferences file path and returns its absolute form; it must identify a file.</para>
    /// </summary>
    private static string NormalizeFilePath(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (string.IsNullOrEmpty(Path.GetFileName(fullPath)))
                throw Damaged(filePath, "The path must identify a file.");
            return fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw Damaged(filePath, $"The path is invalid: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     <para>校验目标 store 目录并返回规范化绝对路径。</para>
    ///     <para>Validates the target store directory and returns its normalized absolute path.</para>
    /// </summary>
    private static string NormalizeStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new SessionStoreMigrationException(
                                                    $"Session-store directory '{storeDirectory}' must be absolute.",
                                                    SessionStoreFailureKind.Damaged);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
    }

    /// <summary>
    ///     <para>创建带偏好文件路径的损坏异常。</para>
    ///     <para>Creates a damaged exception that includes the preferences path.</para>
    /// </summary>
    private static SessionStoreMigrationException Damaged(
        string preferencesPath, string reason, Exception? innerException = null) =>
        new($"Sidebar preferences '{preferencesPath}' cannot be imported: {reason}",
            SessionStoreFailureKind.Damaged, innerException);
}

/// <summary>
///     <para>侧栏偏好文件的迁移子集文档；未知成员被忽略。</para>
///     <para>The migration subset document of the sidebar preferences; unknown members are ignored.</para>
/// </summary>
internal sealed class SidebarPreferencesDocument
{
    public string? Schema { get; set; }

    public List<SidebarStoreDocument?>? Stores { get; set; }
}

/// <summary>
///     <para>侧栏偏好中单个 store 的迁移子集条目。</para>
///     <para>The migration subset entry of one store inside the sidebar preferences.</para>
/// </summary>
internal sealed class SidebarStoreDocument
{
    public string? StoreDirectory { get; set; }

    public List<string>? ManualProjectOrder { get; set; }

    public List<string>? ManualSessionOrder { get; set; }

    public List<string>? CollapsedGroupKeys { get; set; }

    // 桌面偏好把旧置顶字段写作 pinnedProjectOrder／pinnedSessionIds，迁移读取器沿用同一协议名。
    [JsonPropertyName("pinnedProjectOrder")]
    public List<string>? LegacyPinnedProjectOrder { get; set; }

    [JsonPropertyName("pinnedSessionIds")]
    public List<string>? LegacyPinnedSessionIds { get; set; }
}

/// <summary>
///     <para>为侧栏偏好迁移子集生成 NativeAOT 可用的 JSON 元数据。</para>
///     <para>Generates NativeAOT-compatible JSON metadata for the sidebar-preferences migration subset.</para>
/// </summary>
[JsonSerializable(typeof(SidebarPreferencesDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SessionStoreSidebarPreferencesJsonContext : JsonSerializerContext;
