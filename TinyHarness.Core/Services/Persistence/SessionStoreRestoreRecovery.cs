using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         业务库替换窗口的持久化恢复记录：在隔离旧日志之前写入，记录待提交副本与每个被隔离的日志文件名；
///         任何打开业务库的路径都先按这份记录把文件代际恢复一致，恢复不了就拒绝打开。
///     </para>
///     <para>
///         The durable recovery record of the business-database replacement window: it is written before any previous
///         journal is quarantined and names the pending copy together with every quarantined journal file. Every path
///         that opens the business database first brings the file generations back into agreement, and refuses to open
///         while that is impossible.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         记录只保存文件名而不保存绝对路径，因此不能把恢复动作引向 store 目录之外；文件名仍逐个经过
///         <see cref="SessionStorePaths.EnsureStoreLeaf" /> 校验，并且必须落在它在记录中的角色上：待提交副本只能是
///         本次操作产生的临时副本，原日志只能是业务库的 <c>-wal</c>／<c>-shm</c>，隔离名必须由对应原日志派生，
///         同一个名字也不得出现两次。角色校验在恢复改动任何文件之前完成，因此一份结构合法但把业务库或会话日志
///         写成副本／隔离名的记录只会被拒绝，不会被用来删除真实文件。
///     </para>
///     <para>
///         The record stores file names instead of absolute paths, so recovery can never be aimed outside the store
///         directory; every name is still validated through <see cref="SessionStorePaths.EnsureStoreLeaf" /> and must
///         also match the role it claims in the record: the pending copy is only the temporary copy of the operation
///         that wrote the record, an original journal is only the business database's <c>-wal</c>／<c>-shm</c>, a
///         quarantined name is derived from its original journal, and no name may appear twice. Role validation runs
///         before recovery changes any file, so a structurally valid record that names the business database or a
///         session log as a copy or a quarantine is refused instead of deleting real data.
///     </para>
///     <para>
///         待提交副本是否还在是"替换是否已发生"的判据：
///         <c>File.Replace</c>／<c>File.Move</c> 成功时源文件已被消耗，所以副本仍在表示替换未发生，隔离的日志
///         必须放回原日志名；副本已不在表示替换已完成，隔离的日志属于上一代，必须丢弃而不能重放。
///     </para>
///     <para>
///         The presence of the pending copy is the evidence of whether the replacement happened: a successful
///         <c>File.Replace</c>／<c>File.Move</c> consumes its source, so a surviving copy means the replacement did not
///         happen and the quarantined journals must go back under their journal names, while a missing copy means the
///         replacement completed and the quarantined journals belong to the previous generation and must be discarded
///         rather than replayed.
///     </para>
///     <para>
///         恢复只在持有 store 级迁移租约时修改文件，因此不会与另一个进程正在执行的恢复交错；本进程正处于替换
///         窗口时跳过恢复，由窗口自身的编排负责收尾。恢复失败时记录保留，业务库保持不可用，直到文件代际一致。
///     </para>
///     <para>
///         Recovery only changes files while holding the store-level migration lease, so it never interleaves with a
///         restore running in another process; while this process is inside the replacement window recovery is skipped
///         and the window's own orchestration finishes the work. A failed recovery keeps the record and leaves the
///         business database unavailable until the file generations agree again.
///     </para>
/// </remarks>
internal static class SessionStoreRestoreRecovery
{
    /// <summary>
    ///     <para>恢复记录文件名；位于 store 目录，与业务库和迁移标记分开。</para>
    ///     <para>The recovery-record file name; it lives in the store directory, separate from the business database and the migration marker.</para>
    /// </summary>
    internal const string RecordFileName = "session-store-restore-recovery.json";

    private const string CurrentSchema = "tinyharness.session-store-restore-recovery/1";
    private const int    MaxRecordBytes = 16 * 1024;

    /// <summary>
    ///     <para>恢复操作名：恢复与回滚是仅有的两个替换窗口。</para>
    ///     <para>The restore operation name; restore and rollback are the only two replacement windows.</para>
    /// </summary>
    internal const string RestoreOperation = "restore";

    /// <summary>
    ///     <para>回滚操作名。</para>
    ///     <para>The rollback operation name.</para>
    /// </summary>
    internal const string RollbackOperation = "rollback";

    /// <summary>
    ///     <para>记录允许的操作名集合；未知操作说明记录不是本实现写的，按损坏拒绝。</para>
    ///     <para>The operation names a record may carry; an unknown operation means the record was not written by this implementation and reports as damaged.</para>
    /// </summary>
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.Ordinal)
    {
        RestoreOperation, RollbackOperation
    };

    /// <summary>
    ///     <para>待提交副本与隔离名共用的临时文件扩展名；业务库、会话日志与迁移标记都不以此结尾。</para>
    ///     <para>The temporary-file extension shared by pending copies and quarantined names; the business database, session logs, and the migration marker never end with it.</para>
    /// </summary>
    private const string TemporaryFileExtension = ".tmp";

    /// <summary>
    ///     <para>隔离名相对原日志的固定中缀，与隔离步骤的命名规则一致。</para>
    ///     <para>The fixed infix a quarantined name carries relative to its original journal, matching the quarantine step's naming.</para>
    /// </summary>
    private const string QuarantineInfix = ".stale-";

    /// <summary>
    ///     <para>临时名中随机片段的长度；与 <see cref="Guid" /> 的 "N" 格式一致。</para>
    ///     <para>The length of the random fragment in a temporary name, matching the "N" format of <see cref="Guid" />.</para>
    /// </summary>
    private const int TemporaryTokenLength = 32;

    /// <summary>
    ///     <para>恢复记录允许点名的原日志名；业务库的 WAL／SHM 是替换窗口唯一的持久状态附注文件。</para>
    ///     <para>The original journal names a record may mention; the business database's WAL／SHM are the only journal-shaped state next to it.</para>
    /// </summary>
    private static readonly HashSet<string> RecoveryJournalNames =
        new(SessionStoreBackup.JournalSuffixes.Select(suffix => SessionStoreDatabase.DatabaseFileName + suffix),
            PathComparer);

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>
    ///         本进程已收尾恢复记录的 store 目录及嵌套深度；已持有租约的编排在这里登记后，内部的打开动作不会再次
    ///         申请租约，也不会把编排中的日志放回或丢弃。
    ///     </para>
    ///     <para>
    ///         The store directories whose recovery record this process already settled, with nesting depth; an
    ///         orchestration that already holds the lease registers here so its internal opens neither acquire the lease
    ///         again nor put the orchestration's journals back or discard them.
    ///     </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> SettledDirectories = new(PathComparer);

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    ///     <para>返回恢复记录在 store 目录内的绝对路径。</para>
    ///     <para>Returns the absolute recovery-record path inside a store directory.</para>
    /// </summary>
    internal static string RecordPathFor(string storeDirectory) =>
        Path.Combine(Normalize(storeDirectory), RecordFileName);

    /// <summary>
    ///     <para>为一次替换窗口生成待提交副本路径；写入记录与恢复校验共用这一处命名规则。</para>
    ///     <para>Creates the pending-copy path of one replacement window; the record writer and the recovery validator share this single naming rule.</para>
    /// </summary>
    internal static string NewPendingCopyPath(string databasePath, string operation) =>
        databasePath + "." + operation + "-" + Guid.NewGuid().ToString("N") + TemporaryFileExtension;

    /// <summary>
    ///     <para>为一次隔离生成隔离副本路径；SQLite 不会把不带日志后缀的名字应用到主文件上。</para>
    ///     <para>Creates a quarantine path for one journal; a name without a journal suffix is never applied to a main file by SQLite.</para>
    /// </summary>
    internal static string NewQuarantinePath(string journalPath) =>
        journalPath + QuarantineInfix + Guid.NewGuid().ToString("N") + TemporaryFileExtension;

    /// <summary>
    ///     <para>
    ///         登记调用方已持有该 store 的迁移租约并把恢复记录处理完毕；释放返回值时注销。登记期间
    ///         <see cref="RecoverAsync" /> 不做任何文件改动，因此编排内部的打开不会重入租约，也不会与自己的
    ///         替换窗口冲突。
    ///     </para>
    ///     <para>
    ///         Registers that the caller holds the store's migration lease and has settled its recovery record;
    ///         disposing the result unregisters it. While registered <see cref="RecoverAsync" /> changes no files, so the
    ///         orchestration's internal opens neither re-enter the lease nor collide with its own replacement window.
    ///     </para>
    /// </summary>
    internal static IDisposable HoldSettled(string storeDirectory)
    {
        var directory = Normalize(storeDirectory);
        SettledDirectories.AddOrUpdate(directory, 1, (_, depth) => depth + 1);
        return new SettledScope(directory);
    }

    /// <summary>
    ///     <para>判断本进程是否已处理该 store 的恢复记录且仍持有租约。</para>
    ///     <para>Whether this process already settled the store's recovery record and still holds the lease.</para>
    /// </summary>
    internal static bool IsSettled(string storeDirectory) => SettledDirectories.ContainsKey(Normalize(storeDirectory));

    /// <summary>
    ///     <para>判断是否还有未收尾的恢复记录；用于在打开业务库前决定是否必须先取得租约。</para>
    ///     <para>Whether an unsettled recovery record exists; used to decide whether the lease must be taken before opening the business database.</para>
    /// </summary>
    internal static bool HasPendingRecord(string storeDirectory) => RecordExists(Normalize(storeDirectory));

    /// <summary>
    ///     <para>
    ///         在打开业务库之前完成可能中断的恢复：没有记录时立即返回；本进程已处理过该记录时跳过；否则先取得
    ///         迁移租约，确认另一个进程没有正在恢复，再按记录把文件代际恢复一致。
    ///     </para>
    ///     <para>
    ///         Completes an interrupted restore before the business database is opened: it returns immediately without a
    ///         record, skips when this process already settled the record, and otherwise takes the migration lease first
    ///         to confirm no other process is restoring, then brings the file generations back into agreement.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionStoreBackupException">
    ///     <para>记录损坏、租约被另一个进程占用、文件代际无法恢复或记录无法删除。</para>
    ///     <para>The record is damaged, another process holds the lease, the file generations cannot be recovered, or the record cannot be deleted.</para>
    /// </exception>
    internal static async Task RecoverAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var directory = Normalize(storeDirectory);
        // 快路径：绝大多数打开都没有恢复记录，不能为此创建租约文件或额外句柄。
        if (!RecordExists(directory)) return;
        if (IsSettled(directory)) return;

        SessionStoreMigrationLease lease;
        try
        {
            lease = await SessionStoreMigration.AcquireLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        catch (SessionStoreMigrationException ex)
        {
            throw new SessionStoreBackupException(
                                                  $"Session store '{directory}' holds an unfinished restore record " +
                                                  $"'{RecordPathFor(directory)}' and its migration lease could not be acquired " +
                                                  $"({ex.Message}); the business database stays unavailable until that restore " +
                                                  "finishes or the record is resolved.",
                                                  ex.Kind, ex);
        }

        await using (lease)
            await RecoverLockedAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         调用方已持有迁移租约时完成恢复；没有记录时不做任何事，恢复成功后删除记录，失败时保留记录。
    ///     </para>
    ///     <para>
    ///         Completes recovery while the caller already holds the migration lease; a missing record is a no-op, a
    ///         successful recovery deletes the record, and a failure keeps it.
    ///     </para>
    /// </summary>
    internal static async Task RecoverLockedAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var directory = Normalize(storeDirectory);
        var record    = await ReadAsync(directory, cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        Apply(directory, record);
        await ClearAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         在隔离旧日志之前原子写入恢复记录：记录操作、待提交副本与全部计划隔离的日志文件名；已存在记录时
    ///         拒绝覆盖，因为那说明上一次替换窗口尚未收尾。
    ///     </para>
    ///     <para>
    ///         Atomically writes the recovery record before any previous journal is quarantined: it names the
    ///         operation, the pending copy, and every journal planned for quarantine; an existing record is refused
    ///         instead of overwritten because it means a previous replacement window has not been resolved.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         写入前先套用与读取端相同的角色校验，因此本实现不会产出自己会拒绝的记录；名字或操作不符合替换窗口
    ///         形状时在改动任何日志之前中止。
    ///     </para>
    ///     <para>
    ///         The same role validation as the read path runs before anything is written, so this implementation never
    ///         produces a record it would refuse; a name or operation that does not fit a replacement window aborts
    ///         before any journal is moved.
    ///     </para>
    /// </remarks>
    /// <exception cref="SessionStoreBackupException">
    ///     <para>已存在未收尾的记录、待提交副本不在 store 目录内或记录写入失败。</para>
    ///     <para>A record is already pending, the pending copy lies outside the store directory, or the record cannot be written.</para>
    /// </exception>
    internal static async Task WriteAsync(
        string storeDirectory, string operation, string pendingPath,
        IReadOnlyList<SessionStoreJournalQuarantine> journals, CancellationToken cancellationToken)
    {
        var directory = Normalize(storeDirectory);
        var recordPath = RecordPathFor(directory);
        if (RecordExists(directory))
            throw new SessionStoreBackupException(
                                                  $"Session store '{directory}' already holds an unfinished restore record " +
                                                  $"'{recordPath}'; the {operation} refused to overwrite it and left the store " +
                                                  "unchanged.",
                                                  SessionStoreFailureKind.Conflict);

        var document = new SessionStoreRestoreRecoveryDocument
        {
            Schema          = CurrentSchema,
            Operation       = operation,
            PendingFileName = FileNameFor(directory, pendingPath),
            Journals = journals.Select(journal => new SessionStoreRestoreRecoveryJournal
            {
                OriginalFileName    = FileNameFor(directory, journal.OriginalPath),
                QuarantinedFileName = FileNameFor(directory, journal.QuarantinedPath)
            }).ToList()
        };
        ValidateRecordRoles(directory, document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document,
                                                       SessionStoreRestoreRecoveryJsonContext.Default
                                                          .SessionStoreRestoreRecoveryDocument);
        if (bytes.Length > MaxRecordBytes)
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record for '{directory}' exceeds its {MaxRecordBytes}-byte " +
                                                  "budget; the operation was aborted.",
                                                  SessionStoreFailureKind.IoFailure);

        var tempPath  = recordPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created   = false;
        var committed = false;
        try
        {
            SessionStorePaths.EnsureStoreLeaf(directory, tempPath);
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                                                   FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            SessionStorePaths.EnsureStoreLeaf(directory, recordPath);
            // 首次写入用移动提交：记录只在本次替换窗口内存在，替换旧记录会掩盖未收尾的窗口。
            File.Move(tempPath, recordPath);
            committed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionStoreBackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' could not be written, so the {operation} " +
                                                  $"stopped without changing '{SessionStoreDatabase.PathFor(directory)}': {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
        finally
        {
            if (created && !committed) TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    ///     <para>读取恢复记录；文件缺失时返回 null，内容或路径形态非法时按损坏报告并保留文件。</para>
    ///     <para>Reads the recovery record; null when the file is missing, and invalid content or paths report as damage while keeping the file.</para>
    /// </summary>
    internal static async Task<SessionStoreRestoreRecoveryDocument?> ReadAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        var directory  = Normalize(storeDirectory);
        var recordPath = RecordPathFor(directory);
        if (!RecordExists(directory)) return null;

        try
        {
            SessionStorePaths.EnsureStoreLeaf(directory, recordPath);
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' cannot be validated: {ex.Message}; the " +
                                                  "business database stays unavailable until it is resolved.",
                                                  SessionStoreFailureKind.Damaged, ex);
        }

        var attributes = TryGetAttributes(recordPath);
        if (attributes is null) return null;
        if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' is a directory or reparse point; the " +
                                                  "business database stays unavailable until it is resolved.",
                                                  SessionStoreFailureKind.Damaged);

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read,
                                                    FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaxRecordBytes)
                throw new SessionStoreBackupException(
                                                      $"The restore recovery record '{recordPath}' exceeds its {MaxRecordBytes}-byte " +
                                                      "budget; the business database stays unavailable until it is resolved.",
                                                      SessionStoreFailureKind.Damaged);
            using var buffer = new MemoryStream((int)stream.Length);
            var       chunk  = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxRecordBytes)
                    throw new SessionStoreBackupException(
                                                          $"The restore recovery record '{recordPath}' exceeds its {MaxRecordBytes}-byte " +
                                                          "budget; the business database stays unavailable until it is resolved.",
                                                          SessionStoreFailureKind.Damaged);
                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionStoreBackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' cannot be read: {ex.Message}; the " +
                                                  "business database stays unavailable until it is resolved.",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }

        SessionStoreRestoreRecoveryDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(StrictUtf8.GetString(bytes),
                                                  SessionStoreRestoreRecoveryJsonContext.Default
                                                     .SessionStoreRestoreRecoveryDocument);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' is damaged: {ex.Message}; the business " +
                                                  "database stays unavailable until it is resolved.",
                                                  SessionStoreFailureKind.Damaged, ex);
        }

        if (document is null || !string.Equals(document.Schema, CurrentSchema, StringComparison.Ordinal))
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' is not a " +
                                                  $"'{CurrentSchema}' document; the business database stays unavailable until it is " +
                                                  "resolved.",
                                                  SessionStoreFailureKind.Damaged);
        // 角色校验必须在恢复改动任何文件之前完成：结构合法但语义非法的记录会让 Apply 删除真实业务库或会话日志。
        // Role validation must finish before recovery changes any file: a structurally valid but semantically invalid
        // record would otherwise let Apply delete the real business database or a session log.
        ValidateRecordRoles(directory, document);
        return document;
    }

    /// <summary>
    ///     <para>
    ///         校验记录中每个文件名的角色：待提交副本必须是本次操作产生的替换临时副本，原日志只能是业务库的
    ///         <c>-wal</c>／<c>-shm</c>，隔离名必须由对应原日志派生，且同一个名字只能出现一次。
    ///     </para>
    ///     <para>
    ///         Validates the role of every name in the record: the pending copy must be the replacement temporary copy
    ///         of this operation, an original journal may only be the business database's <c>-wal</c>／<c>-shm</c>, a
    ///         quarantined name must be derived from its original journal, and no name may appear twice.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         校验只读取记录内容，不判断文件是否存在，也不改动任何文件；被拒绝的记录保留在磁盘上，业务库保持
    ///         不可用，交由人工处理。目录边界与叶子校验在这里一并完成，因此校验通过后的名字不会再被
    ///         <see cref="Apply" /> 重新解释。
    ///     </para>
    ///     <para>
    ///         Validation reads only the record and neither probes file existence nor changes anything; a refused record
    ///         stays on disk and leaves the business database unavailable for manual resolution. Directory-boundary and
    ///         leaf checks run here too, so <see cref="Apply" /> no longer reinterprets a name that passed validation.
    ///     </para>
    /// </remarks>
    private static void ValidateRecordRoles(string storeDirectory, SessionStoreRestoreRecoveryDocument document)
    {
        var recordPath = RecordPathFor(storeDirectory);
        if (document.Operation is not { } operation || !AllowedOperations.Contains(operation))
            throw Damaged(recordPath,
                          $"it names the operation '{document.Operation}', which is not a replacement window this build writes");
        var journals = document.Journals ??
                       throw Damaged(recordPath, "it carries no journals list");

        // 同一个名字出现两次时，第二次处理会把第一次刚放回或刚丢弃的结果再解释一遍，因此一律按损坏拒绝。
        // A name that appears twice would be reinterpreted after the first pass already put it back or discarded it,
        // so duplicates are refused as damage.
        var seen = new HashSet<string>(PathComparer);
        var pendingName = document.PendingFileName;
        _ = ResolveRecordFile(storeDirectory, pendingName);
        if (!IsPendingCopyName(pendingName, operation))
            throw Damaged(recordPath,
                          $"its pending copy '{pendingName}' is not the '{operation}' replacement temporary copy of " +
                          $"'{SessionStoreDatabase.DatabaseFileName}'");
        _ = seen.Add(pendingName);

        foreach (var journal in journals)
        {
            if (journal is null)
                throw Damaged(recordPath, "it names a journal entry that is empty");

            var originalName    = journal.OriginalFileName;
            var quarantinedName = journal.QuarantinedFileName;
            _ = ResolveRecordFile(storeDirectory, originalName);
            _ = ResolveRecordFile(storeDirectory, quarantinedName);

            if (!RecoveryJournalNames.Contains(originalName))
                throw Damaged(recordPath,
                              $"it names '{originalName}' as an original journal, which is not the business database's " +
                              string.Join(" or ", SessionStoreBackup.JournalSuffixes));
            if (!IsQuarantineNameFor(quarantinedName, originalName))
                throw Damaged(recordPath,
                              $"its quarantined name '{quarantinedName}' is not a quarantine copy of '{originalName}'");
            if (!seen.Add(originalName))
                throw Damaged(recordPath, $"it names '{originalName}' more than once");
            if (!seen.Add(quarantinedName))
                throw Damaged(recordPath, $"it names '{quarantinedName}' more than once");
        }
    }

    /// <summary>
    ///     <para>判断名字是否为指定操作的待提交副本：<c>session-store.db.&lt;operation&gt;-&lt;32 位小写十六进制&gt;.tmp</c>。</para>
    ///     <para>Whether a name is the pending copy of the given operation: <c>session-store.db.&lt;operation&gt;-&lt;32 lowercase hex&gt;.tmp</c>.</para>
    /// </summary>
    private static bool IsPendingCopyName(string fileName, string operation) =>
        IsTemporaryName(fileName, SessionStoreDatabase.DatabaseFileName + "." + operation + "-");

    /// <summary>
    ///     <para>判断隔离名是否由原日志派生：<c>&lt;original&gt;.stale-&lt;32 位小写十六进制&gt;.tmp</c>。</para>
    ///     <para>Whether a quarantined name derives from its original journal: <c>&lt;original&gt;.stale-&lt;32 lowercase hex&gt;.tmp</c>.</para>
    /// </summary>
    private static bool IsQuarantineNameFor(string quarantinedName, string originalName) =>
        IsTemporaryName(quarantinedName, originalName + QuarantineInfix);

    /// <summary>
    ///     <para>判断名字是否为 <c>&lt;前缀&gt;&lt;32 位小写十六进制&gt;.tmp</c> 的临时文件形状。</para>
    ///     <para>Whether a name has the temporary shape <c>&lt;prefix&gt;&lt;32 lowercase hex&gt;.tmp</c>.</para>
    /// </summary>
    private static bool IsTemporaryName(string fileName, string prefix) =>
        fileName.Length == prefix.Length + TemporaryTokenLength + TemporaryFileExtension.Length &&
        fileName.StartsWith(prefix, PathComparison) &&
        IsLowerHexToken(fileName.AsSpan(prefix.Length, TemporaryTokenLength)) &&
        fileName.EndsWith(TemporaryFileExtension, PathComparison);

    /// <summary>
    ///     <para>判断片段是否为 <see cref="Guid" /> 的 "N" 格式小写十六进制串。</para>
    ///     <para>Whether a fragment is a lowercase hexadecimal string in the "N" format of <see cref="Guid" />.</para>
    /// </summary>
    private static bool IsLowerHexToken(ReadOnlySpan<char> token)
    {
        foreach (var character in token)
        {
            if (character is >= '0' and <= '9' or >= 'a' and <= 'f') continue;
            return false;
        }

        return token.Length != 0;
    }

    /// <summary>
    ///     <para>构造一条"记录内容非法"的损坏异常；消息包含记录路径与具体原因。</para>
    ///     <para>Builds a damaged-record exception carrying the record path and the specific reason.</para>
    /// </summary>
    private static SessionStoreBackupException Damaged(string recordPath, string reason) =>
        new($"The restore recovery record '{recordPath}' is damaged: {reason}; the business database stays unavailable " +
            "until it is resolved.",
            SessionStoreFailureKind.Damaged);

    /// <summary>
    ///     <para>删除恢复记录；失败时抛出，使调用方不能声称恢复已经收尾。</para>
    ///     <para>Deletes the recovery record; a failure throws so the caller cannot claim recovery finished.</para>
    /// </summary>
    internal static async Task ClearAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var recordPath = RecordPathFor(storeDirectory);
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(recordPath)) File.Delete(recordPath);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{recordPath}' could not be deleted: {ex.Message}; the " +
                                                  "business database stays unavailable until it is resolved.",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         尽力删除恢复记录：调用方已把文件代际恢复一致，记录残留只会让下一次打开重做一次幂等恢复。
    ///     </para>
    ///     <para>
    ///         Best-effort deletes the recovery record: the caller already made the file generations agree, so a
    ///         leftover record only makes the next open repeat an idempotent recovery.
    ///     </para>
    /// </summary>
    internal static void TryClear(string storeDirectory)
    {
        try
        {
            var recordPath = RecordPathFor(storeDirectory);
            if (File.Exists(recordPath)) File.Delete(recordPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException)
        {
            // 记录残留由下一次打开恢复；此处不掩盖调用方正在报告的结果。
        }
    }

    /// <summary>
    ///     <para>
    ///         按记录把文件代际恢复一致：待提交副本仍在时把隔离日志放回原日志名，副本已不在时丢弃隔离日志；
    ///         任一必需步骤失败则抛出并保留记录，业务库保持不可用。
    ///     </para>
    ///     <para>
    ///         Brings the file generations back into agreement from the record: a surviving pending copy puts the
    ///         quarantined journals back under their journal names, a missing one discards them; any required step that
    ///         fails throws and keeps the record, leaving the business database unavailable.
    ///     </para>
    /// </summary>
    private static void Apply(string storeDirectory, SessionStoreRestoreRecoveryDocument record)
    {
        var pendingPath = ResolveRecordFile(storeDirectory, record.PendingFileName);
        var replaced    = TryGetAttributes(pendingPath) is null;
        var failures    = new List<string>();
        foreach (var journal in record.Journals)
        {
            var original    = ResolveRecordFile(storeDirectory, journal.OriginalFileName);
            var quarantined = ResolveRecordFile(storeDirectory, journal.QuarantinedFileName);
            if (replaced)
            {
                // 替换已提交：隔离的日志属于上一代，重放会破坏新主文件，只能丢弃。
                TryDeleteFile(quarantined);
                continue;
            }

            if (TryGetAttributes(quarantined) is null) continue;
            if (TryGetAttributes(original) is not null)
            {
                failures.Add($"'{original}' exists while its quarantined copy '{quarantined}' is still aside");
                continue;
            }

            try
            {
                File.Move(quarantined, original);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add($"'{quarantined}' could not be put back as '{original}' ({ex.Message})");
            }
        }

        if (failures.Count != 0)
            throw new SessionStoreBackupException(
                                                  $"The interrupted {record.Operation} of session store '{storeDirectory}' could not be " +
                                                  $"recovered: {string.Join("; ", failures)}. Restore those files manually; the business " +
                                                  $"database stays unavailable until '{RecordPathFor(storeDirectory)}' is resolved.",
                                                  SessionStoreFailureKind.IoFailure);
        if (!replaced) TryDeleteFile(pendingPath);
    }

    /// <summary>
    ///     <para>
    ///         把记录中的文件名解析为 store 目录内的绝对路径，拒绝目录分隔符、根路径、相对段与链接；解析结果
    ///         一律经过 store 目录边界与叶子校验。
    ///     </para>
    ///     <para>
    ///         Resolves a record file name into an absolute path inside the store directory, refusing directory
    ///         separators, rooted paths, relative segments, and links; the result always passes the store-directory
    ///         boundary and leaf checks.
    ///     </para>
    /// </summary>
    private static string ResolveRecordFile(string storeDirectory, string fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName is "." or ".." ||
            fileName != Path.GetFileName(fileName) ||
            Path.IsPathRooted(fileName))
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{RecordPathFor(storeDirectory)}' names " +
                                                  $"'{fileName}', which is not a plain file name inside the store directory.",
                                                  SessionStoreFailureKind.Damaged);

        var fullPath = Path.Combine(storeDirectory, fileName);
        try
        {
            SessionStorePaths.EnsureStoreLeaf(storeDirectory, fullPath);
        }
        catch (Exception ex) when (ex is SessionStoreException or IOException or UnauthorizedAccessException)
        {
            throw new SessionStoreBackupException(
                                                  $"The restore recovery record '{RecordPathFor(storeDirectory)}' names " +
                                                  $"'{fileName}', which cannot be used inside the store directory: {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }

        return fullPath;
    }

    /// <summary>
    ///     <para>把绝对路径转换为 store 目录内的文件名；不在该目录内时拒绝写入记录。</para>
    ///     <para>Converts an absolute path into a file name inside the store directory, refusing to record anything outside it.</para>
    /// </summary>
    private static string FileNameFor(string storeDirectory, string path)
    {
        var fullPath  = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory is null ||
            !string.Equals(Path.TrimEndingDirectorySeparator(directory), storeDirectory, PathComparison))
            throw new SessionStoreBackupException(
                                                  $"Session store file '{fullPath}' is outside its storage directory " +
                                                  $"'{storeDirectory}'; no restore recovery record was written.",
                                                  SessionStoreFailureKind.IoFailure);

        return Path.GetFileName(fullPath);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    ///     <para>判断恢复记录文件是否存在；目录占位同样视为存在。</para>
    ///     <para>Whether the recovery-record file exists; a directory placeholder also counts.</para>
    /// </summary>
    private static bool RecordExists(string storeDirectory) => TryGetAttributes(RecordPathFor(storeDirectory)) is not null;

    /// <summary>
    ///     <para>返回文件属性；文件或父目录缺失时返回 null。</para>
    ///     <para>Returns the file attributes, or null when the file or its directory is missing.</para>
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
    ///     <para>尽力删除恢复记录或待提交副本；失败只留下可再次处理的文件，不掩盖调用方结果。</para>
    ///     <para>Best-effort deletes a recovery record or pending copy; a failure only leaves a file that can be handled again and never masks the caller's result.</para>
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 残留文件在下次打开时按记录重新处理。
        }
    }

    private static string Normalize(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new ArgumentException($"Session-store directory '{storeDirectory}' must be absolute.",
                                        nameof(storeDirectory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
    }

    /// <summary>
    ///     <para>一次“记录已收尾”的登记；释放时递减同一目录的嵌套深度。</para>
    ///     <para>One "record settled" registration; disposing decrements the nesting depth of the same directory.</para>
    /// </summary>
    private sealed class SettledScope : IDisposable
    {
        private readonly string _directory;
        private          bool   _disposed;

        public SettledScope(string directory) => _directory = directory;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (SettledDirectories.TryGetValue(_directory, out var depth))
            {
                if (depth <= 1) SettledDirectories.TryRemove(_directory, out _);
                else SettledDirectories[_directory] = depth - 1;
            }
        }
    }
}

/// <summary>
///     <para>
///         一对被隔离的旧日志：原日志路径与隔离后的非日志路径，用于替换失败时放回、替换成功时删除。
///     </para>
///     <para>
///         One quarantined previous-generation journal: its original journal path and the non-journal path it was moved
///         to, used to put it back when the replacement fails and to delete it after the replacement succeeds.
///     </para>
/// </summary>
/// <param name="OriginalPath">
///     <para>原日志文件名，例如 <c>session-store.db-wal</c>。</para>
///     <para>The original journal name, such as <c>session-store.db-wal</c>.</para>
/// </param>
/// <param name="QuarantinedPath">
///     <para>隔离后的文件名；不再带日志后缀，SQLite 不会把它应用到主文件。</para>
///     <para>The quarantined name; it no longer carries a journal suffix so SQLite never applies it to a main file.</para>
/// </param>
internal sealed record SessionStoreJournalQuarantine(string OriginalPath, string QuarantinedPath);

/// <summary>
///     <para>恢复记录的持久化文档；字段与协议版本一一对应，只保存 store 目录内的文件名。</para>
///     <para>The persisted recovery-record document; its fields map one-to-one onto the protocol version and hold only file names inside the store directory.</para>
/// </summary>
internal sealed class SessionStoreRestoreRecoveryDocument
{
    public string Schema { get; set; } = string.Empty;

    /// <summary>
    ///     <para>
    ///         写入记录的操作，例如 <c>restore</c> 或 <c>rollback</c>；决定待提交副本必须匹配哪一种临时名，
    ///         也用于诊断。
    ///     </para>
    ///     <para>
    ///         The operation that wrote the record, such as <c>restore</c> or <c>rollback</c>; it decides which
    ///         temporary shape the pending copy must match and also serves diagnostics.
    ///     </para>
    /// </summary>
    public string Operation { get; set; } = string.Empty;

    public string PendingFileName { get; set; } = string.Empty;

    [JsonRequired]
    public List<SessionStoreRestoreRecoveryJournal> Journals { get; set; } = [];
}

/// <summary>
///     <para>恢复记录中的一条隔离日志：原日志文件名与隔离后的文件名。</para>
///     <para>One quarantined journal inside the recovery record: the original journal name and the quarantined name.</para>
/// </summary>
internal sealed class SessionStoreRestoreRecoveryJournal
{
    public string OriginalFileName { get; set; } = string.Empty;
    public string QuarantinedFileName { get; set; } = string.Empty;
}

/// <summary>
///     <para>为恢复记录生成 NativeAOT 可用的 JSON 元数据。</para>
///     <para>Generates NativeAOT-compatible JSON metadata for the recovery record.</para>
/// </summary>
[JsonSerializable(typeof(SessionStoreRestoreRecoveryDocument))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
internal sealed partial class SessionStoreRestoreRecoveryJsonContext : JsonSerializerContext;
