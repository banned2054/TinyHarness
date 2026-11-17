using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         迁移标记文件的严格读写与迁移租约：标记位于业务库之外，记录迁移状态、库身份与各批来源指纹；
///         写入使用同目录临时文件加原子替换，读取区分缺失与损坏。
///     </para>
///     <para>
///         Strict read/write access to the migration marker plus the migration lease. The marker lives outside the
///         business database and records the migration state, database identity, and per-batch source fingerprints;
///         writes use a same-directory temporary file with an atomic replace, and reads distinguish absence from damage.
///     </para>
/// </summary>
internal static class SessionStoreMigrationStateStore
{
    private const int MaxMarkerBytes = 64 * 1024;

    private const int LeaseTimeoutMs    = 5000;
    private const int LeaseRetryDelayMs = 25;

    private const int CommitRetryTimeoutMs = 2000;
    private const int CommitRetryDelayMs   = 25;

    private const int SharingViolationHResult = unchecked((int)0x80070020);
    private const int LockViolationHResult    = unchecked((int)0x80070021);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>返回迁移标记文件在 store 目录内的绝对路径。</para>
    ///     <para>Returns the absolute migration-marker path inside a store directory.</para>
    /// </summary>
    public static string MarkerPathFor(string storeDirectory, string markerFileName) =>
        Path.Combine(NormalizeStoreDirectory(storeDirectory), markerFileName);

    /// <summary>
    ///     <para>返回迁移租约文件的绝对路径。</para>
    ///     <para>Returns the absolute migration-lease path.</para>
    /// </summary>
    public static string LeasePathFor(string storeDirectory, string leaseFileName) =>
        Path.Combine(NormalizeStoreDirectory(storeDirectory), leaseFileName);

    /// <summary>
    ///     <para>
    ///         读取并严格校验迁移标记：缺失返回 Missing；无法读取或结构非法返回 Damaged 并保留文件原样；
    ///         标记路径是链接、目录占位或超预算时同样按损坏报告。
    ///     </para>
    ///     <para>
    ///         Reads and strictly validates the migration marker: absence yields Missing; unreadable or invalid
    ///         content yields Damaged while keeping the file untouched; a linked, directory-placeholder, or oversized
    ///         marker path is also reported as damaged.
    ///     </para>
    /// </summary>
    public static async Task<SessionStoreMarkerRead> ReadAsync(
        string storeDirectory, string markerFileName, CancellationToken cancellationToken)
    {
        var markerPath = MarkerPathFor(storeDirectory, markerFileName);
        try
        {
            SessionStorePaths.EnsureStoreLeaf(storeDirectory, markerPath);
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException)
        {
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                              null,
                                              $"Migration marker '{markerPath}' cannot be validated: {ex.Message}");
        }

        var attributes = TryGetAttributes(markerPath);
        if (attributes is null) return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Missing, null, null);
        if ((attributes.Value & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                              null,
                                              $"Migration marker '{markerPath}' is a directory or reparse point.");

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096,
                                                    FileOptions.Asynchronous);
            if (stream.Length > MaxMarkerBytes)
                return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                                  null,
                                                  $"Migration marker '{markerPath}' exceeds its {MaxMarkerBytes}-byte budget.");
            using var buffer = new MemoryStream((int)stream.Length);
            var       chunk  = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxMarkerBytes)
                    return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                                      null,
                                                      $"Migration marker '{markerPath}' exceeds its {MaxMarkerBytes}-byte budget.");
                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException)
        {
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                              null,
                                              $"Migration marker '{markerPath}' cannot be read: {ex.Message}");
        }

        string json;
        try
        {
            json = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                              null,
                                              $"Migration marker '{markerPath}' is not valid UTF-8: {ex.Message}");
        }

        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        MigrationMarkerDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(json,
                                                  SessionStoreMigrationJsonContext.Default
                                                     .MigrationMarkerDocument);
        }
        catch (JsonException ex)
        {
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged,
                                              null,
                                              $"Migration marker '{markerPath}' is damaged: {ex.Message}");
        }

        if (document is null)
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged, null,
                                              $"Migration marker '{markerPath}' must contain a JSON object.");

        if (TryCreateMarker(document, out var marker, out var validationError))
            return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Present, marker, null);

        return new SessionStoreMarkerRead(SessionStoreMarkerReadStatus.Damaged, null,
                                          $"Migration marker '{markerPath}' is invalid: {validationError}");
    }

    /// <summary>
    ///     <para>原子写入迁移标记；已有标记时以替换提交，首次写入时移动提交，失败不留下半写文件。</para>
    ///     <para>
    ///         Atomically writes the migration marker: replace for an existing marker, move for the first write, and a
    ///         failure never leaves a half-written file.
    ///     </para>
    /// </summary>
    public static async Task WriteAsync(
        string storeDirectory, string markerFileName, SessionStoreMigrationMarker marker,
        CancellationToken cancellationToken)
    {
        ValidateMarker(marker);
        var markerPath = MarkerPathFor(storeDirectory, markerFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ToDocument(marker),
                                                        SessionStoreMigrationJsonContext.Default
                                                           .MigrationMarkerDocument);
        if (bytes.Length > MaxMarkerBytes)
            throw new SessionStoreMigrationException(
                                                    $"Migration marker '{markerPath}' exceeds its {MaxMarkerBytes}-byte budget.",
                                                    SessionStoreFailureKind.Damaged);

        var tempPath  = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created   = false;
        var committed = false;
        try
        {
            SessionStorePaths.EnsureStoreLeaf(storeDirectory, tempPath);
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            SessionStorePaths.EnsureStoreLeaf(storeDirectory, markerPath);
            var deadline = Environment.TickCount64 + CommitRetryTimeoutMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(markerPath)) File.Replace(tempPath, markerPath, null);
                    else File.Move(tempPath, markerPath);
                    committed = true;
                    return;
                }
                catch (IOException ex) when (ex.HResult is SharingViolationHResult or LockViolationHResult &&
                                             Environment.TickCount64 < deadline)
                {
                    await Task.Delay(CommitRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException)
        {
            throw new SessionStoreMigrationException(
                                                    $"Could not write migration marker '{markerPath}': {ex.Message}",
                                                    ClassifyFileFailure(ex), ex);
        }
        finally
        {
            if (created && !committed) TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    ///     <para>
    ///         获取 store 级迁移租约：以独占句柄打开租约文件，迁移、备份与恢复在持有租约期间互斥；
    ///         旧程序不识别该文件，因此租约只保证本程序的写入方互斥，旧来源是否被改写由指纹校验发现。
    ///     </para>
    ///     <para>
    ///         Acquires the store-level migration lease: an exclusive handle on the lease file makes migrations,
    ///         backups, and restores mutually exclusive. Older programs do not recognize the file, so the lease only
    ///         excludes this program's writers; legacy-source rewrites are detected through fingerprint comparison.
    ///     </para>
    /// </summary>
    public static async Task<SessionStoreMigrationLease> AcquireLeaseAsync(
        string storeDirectory, string leaseFileName, CancellationToken cancellationToken)
    {
        var leasePath = LeasePathFor(storeDirectory, leaseFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        var deadline = Environment.TickCount64 + LeaseTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SessionStorePaths.EnsureStoreLeaf(storeDirectory, leasePath);
                var stream = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                            FileShare.None, 1, FileOptions.Asynchronous);
                return new SessionStoreMigrationLease(leasePath, stream);
            }
            catch (Exception ex) when (ex is SessionStoreException)
            {
                throw new SessionStoreMigrationException(
                                                        $"Cannot use the session-store migration lease '{leasePath}': {ex.Message}",
                                                        ClassifyFileFailure(ex), ex);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(LeaseRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new SessionStoreMigrationException(
                                                        $"Could not acquire the session-store migration lease '{leasePath}' " +
                                                        $"within {LeaseTimeoutMs} ms; another migration, backup, or restore may be running.",
                                                        SessionStoreFailureKind.Locked, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new SessionStoreMigrationException(
                                                        $"Cannot acquire the session-store migration lease '{leasePath}': {ex.Message}",
                                                        SessionStoreFailureKind.PermissionDenied, ex);
            }
        }
    }

    /// <summary>
    ///     <para>返回文件或路径失败的失败类别：权限、只读与 IO 失败分开，不一律当作损坏。</para>
    ///     <para>Classifies a file or path failure: permission, read-only, and IO failures stay apart and are never all treated as damage.</para>
    /// </summary>
    public static SessionStoreFailureKind ClassifyFileFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException) return SessionStoreFailureKind.PermissionDenied;
            if (current is PathTooLongException or NotSupportedException) return SessionStoreFailureKind.IoFailure;
        }

        return exception is IOException ? SessionStoreFailureKind.IoFailure : SessionStoreFailureKind.IoFailure;
    }

    private static void ValidateMarker(SessionStoreMigrationMarker marker)
    {
        if (marker.Schema != SessionStoreMigrationMarker.CurrentSchema)
            throw new SessionStoreMigrationException(
                                                    $"Migration marker schema '{marker.Schema}' is not supported.",
                                                    SessionStoreFailureKind.Unsupported);
        if (!IsMigrationId(marker.MigrationId))
            throw new SessionStoreMigrationException("A migration marker requires a 32-character lowercase hexadecimal migration id.",
                                                     SessionStoreFailureKind.Damaged);
        if (marker.StoreIdentity is { } identity && !IsMigrationId(identity))
            throw new SessionStoreMigrationException("A migration marker's store identity must be a 32-character lowercase hexadecimal id.",
                                                     SessionStoreFailureKind.Damaged);
        if (marker.Batches.Count != SessionStoreMigrationMarker.BatchOrder.Length ||
            SessionStoreMigrationMarker.BatchOrder.Any(batch => !marker.Batches.ContainsKey(batch)))
            throw new SessionStoreMigrationException("A migration marker must record exactly the three known batches.",
                                                     SessionStoreFailureKind.Damaged);
        foreach (var batch in SessionStoreMigrationMarker.BatchOrder)
            if (marker.Batches[batch] == SessionStoreBatchState.Switched &&
                string.IsNullOrWhiteSpace(marker.SourceFingerprints.GetValueOrDefault(batch)))
                throw new SessionStoreMigrationException(
                                                        $"A switched migration batch '{batch}' requires a source fingerprint.",
                                                        SessionStoreFailureKind.Damaged);
    }

    private static bool TryCreateMarker(
        MigrationMarkerDocument document, out SessionStoreMigrationMarker marker, out string? error)
    {
        marker = null!;
        error  = null;
        if (document.Schema != SessionStoreMigrationMarker.CurrentSchema)
        {
            error = $"unsupported schema '{document.Schema}'";
            return false;
        }

        if (!IsMigrationId(document.MigrationId))
        {
            error = "missing or invalid migration id";
            return false;
        }

        if (document.StoreIdentity is { } identity && !IsMigrationId(identity))
        {
            error = "invalid store identity";
            return false;
        }

        if (document.StartedUtcMs <= 0 || document.UpdatedUtcMs <= 0)
        {
            error = "invalid timestamps";
            return false;
        }

        if (document.Batches is null || document.Batches.Count != SessionStoreMigrationMarker.BatchOrder.Length)
        {
            error = "batches must contain exactly the three known batches";
            return false;
        }

        var batches      = new Dictionary<SessionStoreMigrationBatch, SessionStoreBatchState>();
        var fingerprints = new Dictionary<SessionStoreMigrationBatch, string>();
        foreach (var batch in SessionStoreMigrationMarker.BatchOrder)
        {
            var name = BatchName(batch);
            if (!document.Batches.TryGetValue(name, out var batchDocument) || batchDocument is null)
            {
                error = $"missing batch '{name}'";
                return false;
            }

            if (batchDocument.State == BatchStateName(SessionStoreBatchState.Pending))
            {
                batches[batch] = SessionStoreBatchState.Pending;
                continue;
            }

            if (batchDocument.State != BatchStateName(SessionStoreBatchState.Switched))
            {
                error = $"invalid state for batch '{name}'";
                return false;
            }

            if (string.IsNullOrWhiteSpace(batchDocument.SourceFingerprint))
            {
                error = $"switched batch '{name}' has no source fingerprint";
                return false;
            }

            batches[batch]                  = SessionStoreBatchState.Switched;
            fingerprints[batch]             = batchDocument.SourceFingerprint;
        }

        marker = new SessionStoreMigrationMarker(
            document.Schema,
            document.MigrationId,
            document.StoreIdentity,
            DateTimeOffset.FromUnixTimeMilliseconds(document.StartedUtcMs),
            DateTimeOffset.FromUnixTimeMilliseconds(document.UpdatedUtcMs),
            batches,
            fingerprints,
            document.LastError);
        return true;
    }

    private static MigrationMarkerDocument ToDocument(SessionStoreMigrationMarker marker)
    {
        var batches = new Dictionary<string, MigrationBatchDocument>(StringComparer.Ordinal);
        foreach (var batch in SessionStoreMigrationMarker.BatchOrder)
        {
            var state = marker.Batches.GetValueOrDefault(batch);
            batches[BatchName(batch)] = new MigrationBatchDocument
            {
                State             = BatchStateName(state),
                SourceFingerprint = state == SessionStoreBatchState.Switched
                    ? marker.SourceFingerprints.GetValueOrDefault(batch)
                    : null
            };
        }

        return new MigrationMarkerDocument
        {
            Schema        = marker.Schema,
            MigrationId   = marker.MigrationId,
            StoreIdentity = marker.StoreIdentity,
            StartedUtcMs  = marker.StartedUtc.ToUnixTimeMilliseconds(),
            UpdatedUtcMs  = marker.UpdatedUtc.ToUnixTimeMilliseconds(),
            Batches       = batches,
            LastError     = marker.LastError
        };
    }

    private static string BatchName(SessionStoreMigrationBatch batch) => batch switch
    {
        SessionStoreMigrationBatch.OrganizationState => "organizationState",
        SessionStoreMigrationBatch.ProjectCatalog    => "projectCatalog",
        SessionStoreMigrationBatch.SessionPlacement  => "sessionPlacement",
        _ => throw new ArgumentOutOfRangeException(nameof(batch), batch, "Unknown migration batch.")
    };

    private static string BatchStateName(SessionStoreBatchState state) => state switch
    {
        SessionStoreBatchState.Pending  => "pending",
        SessionStoreBatchState.Switched => "switched",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown batch state.")
    };

    private static bool IsMigrationId(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit) && value.All(character => !char.IsUpper(character));

    private static string NormalizeStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new ArgumentException($"Session-store directory '{storeDirectory}' must be absolute.",
                                        nameof(storeDirectory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 临时标记清理失败不掩盖原始写入异常；文件名独立且不参与读取。
        }
    }
}

/// <summary>
///     <para>
///         迁移租约：独占持有期间迁移、备份与恢复互斥；释放句柄即可，进程退出由操作系统释放。
///     </para>
///     <para>
///         The migration lease: migrations, backups, and restores are mutually exclusive while it is held; releasing
///         the handle is enough and process exit releases it through the operating system.
///     </para>
/// </summary>
internal sealed class SessionStoreMigrationLease : IAsyncDisposable
{
    private readonly FileStream _stream;

    /// <summary>
    ///     <para>绑定已打开的独占租约句柄。</para>
    ///     <para>Binds an already opened exclusive lease handle.</para>
    /// </summary>
    public SessionStoreMigrationLease(string leasePath, FileStream stream)
    {
        LeasePath = leasePath;
        _stream   = stream;
    }

    /// <summary>
    ///     <para>租约文件路径。</para>
    ///     <para>The lease file path.</para>
    /// </summary>
    public string LeasePath { get; }

    /// <summary>
    ///     <para>释放租约句柄。</para>
    ///     <para>Releases the lease handle.</para>
    /// </summary>
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>
///     <para>迁移标记的持久化文档；字段与协议版本一一对应。</para>
///     <para>The persisted marker document; its fields map one-to-one onto the protocol version.</para>
/// </summary>
internal sealed class MigrationMarkerDocument
{
    public string Schema { get; set; } = SessionStoreMigrationMarker.CurrentSchema;
    public string MigrationId { get; set; } = string.Empty;
    public string? StoreIdentity { get; set; }
    public long StartedUtcMs { get; set; }
    public long UpdatedUtcMs { get; set; }
    public Dictionary<string, MigrationBatchDocument> Batches { get; set; } = new(StringComparer.Ordinal);
    public string? LastError { get; set; }
}

/// <summary>
///     <para>单个批次的持久化状态与来源指纹。</para>
///     <para>The persisted state and source fingerprint of one batch.</para>
/// </summary>
internal sealed class MigrationBatchDocument
{
    public string State { get; set; } = "pending";
    public string? SourceFingerprint { get; set; }
}

/// <summary>
///     <para>为迁移标记生成 NativeAOT 可用的 JSON 元数据。</para>
///     <para>Generates NativeAOT-compatible JSON metadata for the migration marker.</para>
/// </summary>
[JsonSerializable(typeof(MigrationMarkerDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SessionStoreMigrationJsonContext : JsonSerializerContext;
