using System.Globalization;
using Microsoft.Data.Sqlite;
using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         业务库备份、校验与恢复的显式入口：备份通过 SQLite 在线备份 API 生成一致快照，校验只读地确认结构、
///         完整性与库身份，恢复把备份提交到业务库文件名并清理可重建的索引派生数据。
///     </para>
///     <para>
///         The explicit entry point for business-database backup, verification, and restoration: a backup is a
///         consistent snapshot produced through the SQLite online backup API, verification read-only confirms
///         structure, integrity, and identity, and restoration commits the backup to the business database file name
///         and clears rebuildable index-derived data.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         备份目标是显式目录中的 <c>session-store.db</c>，已存在时一律拒绝，绝不覆盖、移动或删除用户备份。
///         恢复要求迁移标记已完成（标记身份与备份身份一致），先把现有业务库复制为
///         <c>session-store.db.pre-restore-&lt;unixms&gt;</c>，再经同目录临时文件提交；旧来源文件与备份源目录
///         永不删除。备份、恢复与迁移通过租约互斥，恢复期间的其他业务库读写由调用方负责停止。
///     </para>
///     <para>
///         The backup target is <c>session-store.db</c> inside an explicit directory; an existing target is always
///         refused and a user backup is never overwritten, moved, or deleted. Restoration requires a completed
///         migration marker whose identity matches the backup, first copies the current business database to
///         <c>session-store.db.pre-restore-&lt;unixms&gt;</c>, and then commits through a same-directory temporary file;
///         legacy source files and the backup source directory are never deleted. Backup, restore, and migration are
///         mutually exclusive through the lease, and stopping other business-database readers and writers during a
///         restore stays the caller's responsibility.
///     </para>
///     <para>
///         恢复只删除可重建的 <c>session-index.db</c> 及其 <c>-wal</c>／<c>-shm</c>，因为恢复后的归档投影必须由
///         既有对账从会话日志重新派生；业务表、迁移标记、旧 JSON 文件与备份本身都不在删除范围内。恢复替换
///         业务库文件之前必须先把旧代际的 <c>-wal</c>／<c>-shm</c> 隔离到非日志文件名，否则旧 WAL 会被应用到
///         新文件上；这些文件属于数据库持久状态，隔离失败必须上报并中止，不能像派生索引那样尽力清理。隔离
///         之前先把待提交副本与计划隔离的日志写入 <see cref="SessionStoreRestoreRecovery" /> 的持久化记录，
///         因此进程在隔离之后、替换之前被终止时，下一次打开业务库会按记录把日志放回或丢弃，而不是在日志缺席
///         的情况下继续读取旧主库。替换成功后才删除隔离副本并清除记录；替换或回滚本身失败时把隔离的日志放回
///         原日志文件名，放回也失败时保留记录并把两个失败一起报告，业务库保持不可用。
///         替换后的复核失败时只把本次恢复保存的 <c>pre-restore</c> 副本放回，目录中更早的同名前缀副本不会被
///         自动选用，业务库原本缺失时也不会拿历史副本伪造回滚；回滚同样先隔离失败一代的 WAL／SHM，隔离
///         失败时不声称旧库已经放回。
///     </para>
///     <para>
///         Restoration deletes only the rebuildable <c>session-index.db</c> and its <c>-wal</c>／<c>-shm</c>
///         because the archive projection must be re-derived from session logs by the existing reconciliation;
///         business tables, the migration marker, legacy JSON files, and the backup itself are never deleted. Before
///         replacing the business-database file the restore must quarantine the previous generation's <c>-wal</c>／
///         <c>-shm</c> to non-journal names, because a stale WAL would otherwise be applied to the new file; those files
///         are persistent database state, so a failed quarantine is reported and aborts instead of being best-effort
///         cleanup like the index files. The pending copy and the journals planned for quarantine are written into the
///         durable <see cref="SessionStoreRestoreRecovery" /> record before any journal is moved, so a process killed
///         between the quarantine and the replacement makes the next open put the journals back or discard them instead
///         of reading the old main file while its journal is absent. The quarantined copies are deleted and the record
///         cleared only after a successful replacement; a failed replacement or rollback puts them back under their
///         journal names, and a failed put-back keeps the record, reports both failures, and leaves the business
///         database unavailable. When the post-replace validation fails only the <c>pre-restore</c> copy this restore
///         saved is put back; an older copy with the same prefix in that directory is never chosen automatically, a store
///         whose database was originally absent never fakes a rollback from a historical copy, and the rollback likewise
///         quarantines the failed generation's WAL／SHM first so it never claims the old database is back while they remain.
///     </para>
/// </remarks>
public static class SessionStoreBackup
{
    private const int CopyBufferBytes = 81920;

    /// <summary>
    ///     <para>
    ///         隔离完成、替换提交之前的测试接缝：进程真正在此处被终止时，磁盘上只剩恢复记录与改名为隔离名的日志，
    ///         因此这条路径无法在测试进程内直接到达。接缝只被程序集内的定向回归设置，生产路径始终为 null；
    ///         参数依次为 store 目录、当时的恢复记录是否已在磁盘上、被隔离的日志文件名。
    ///     </para>
    ///     <para>
    ///         A test seam between a finished quarantine and the replacement commit: a process really terminated here
    ///         leaves only the recovery record and the journals renamed to quarantined names on disk, so the window is
    ///         unreachable inside the test process. Only focused regressions inside the assembly set it; production
    ///         always leaves it null. Arguments are the store directory, whether the recovery record was on disk at
    ///         that moment, and the quarantined journal file names.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         定向回归在这个接缝里抛出异常并恢复同一个测试进程，模拟的是"磁盘停在该窗口"的文件状态，而不是真实的
    ///         进程终止；它不覆盖终止时的句柄释放时机、文件系统缓存落盘顺序，也不覆盖替换后 SQLite 对 WAL 的实际
    ///         重放行为。
    ///     </para>
    ///     <para>
    ///         Focused regressions throw from this seam and keep the same test process alive, so they reproduce the file
    ///         state left by that window rather than a real process termination; they do not cover handle-release timing
    ///         at termination, cache-flush ordering, or SQLite's actual WAL replay after a replacement.
    ///     </para>
    /// </remarks>
    internal static Func<string, bool, string[], CancellationToken, Task>? QuarantineCommittedHookForTests
    {
        get;
        set;
    }

    /// <summary>
    ///     <para>
    ///         业务库主文件旁的 SQLite 日志后缀；替换与回滚先隔离这些文件，避免上一代 WAL 被应用到新主文件。
    ///         恢复记录的读取端按同一份后缀校验原日志名，两侧不会各自维护一份清单。
    ///     </para>
    ///     <para>
    ///         The SQLite journal suffixes next to the business-database file; replacement and rollback quarantine them
    ///         first so the previous generation's WAL is never applied to a new main file. The recovery-record reader
    ///         validates original journal names from the same list, so the two sides never drift apart.
    ///     </para>
    /// </summary>
    internal static readonly string[] JournalSuffixes = ["-wal", "-shm"];

    /// <summary>
    ///     <para>
    ///         从已迁移的 store 备份业务库到指定目录的 <c>session-store.db</c>；目标已存在时拒绝，写完后校验
    ///         结构、完整性、库身份与 schema 版本，任何失败只删除本次创建的文件。
    ///     </para>
    ///     <para>
    ///         Backs up a migrated store's business database to <c>session-store.db</c> inside the given directory,
    ///         refusing an existing target and verifying structure, integrity, identity, and schema version afterwards;
    ///         on failure only the file this call created is deleted.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>已迁移的受保护 session store 目录；须为绝对路径。</para>
    ///     <para>The migrated protected session-store directory; it must be absolute.</para>
    /// </param>
    /// <param name="destinationDirectory">
    ///     <para>备份目录；不存在时创建，已存在但缺少目录时按 IO 失败报告。</para>
    ///     <para>The backup directory; it is created when missing, and an existing file in its place reports as an IO failure.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消租约等待、备份与校验。</para>
    ///     <para>Cancels lease waiting, the backup, and verification.</para>
    /// </param>
    /// <returns>
    ///     <para>备份文件路径、库身份、schema 版本、字节数与创建时间。</para>
    ///     <para>The backup path, database identity, schema version, byte length, and creation time.</para>
    /// </returns>
    /// <exception cref="SessionStoreBackupException">
    ///     <para>
    ///         尚未迁移、目标已存在、备份或校验失败，或上一次替换窗口留有未收尾的恢复记录且无法自动收尾。
    ///     </para>
    ///     <para>
    ///         The store is not migrated, the target exists, the backup or its verification failed, or a previous
    ///         replacement window left a recovery record that could not be settled automatically.
    ///     </para>
    /// </exception>
    public static async Task<SessionStoreBackupInfo> CreateAsync(
        string storeDirectory, string destinationDirectory, CancellationToken cancellationToken)
    {
        var storePath       = NormalizeDirectory(storeDirectory, nameof(storeDirectory));
        var destinationPath = NormalizeDirectory(destinationDirectory, nameof(destinationDirectory));
        var marker          = await ReadCompletedMarkerAsync(storePath, "back up", cancellationToken)
           .ConfigureAwait(false);
        var database = new SessionStoreDatabase(storePath);
        await RequireAvailableAsync(database, cancellationToken).ConfigureAwait(false);

        await using var lease = await AcquireLeaseAsync(storePath, cancellationToken).ConfigureAwait(false);
        // 备份同样先收尾中断的恢复：否则在线备份可能复制到一份缺少隔离日志内容的旧主库。
        await SessionStoreRestoreRecovery.RecoverLockedAsync(storePath, cancellationToken).ConfigureAwait(false);
        using var settled = SessionStoreRestoreRecovery.HoldSettled(storePath);
        // 备份目标固定为备份目录内的业务库文件名；已存在即拒绝，用户备份绝不覆盖。
        var backupPath = Path.Combine(destinationPath, SessionStoreDatabase.DatabaseFileName);
        if (FileExists(backupPath))
            throw new SessionStoreBackupException(
                                                  $"Backup target '{backupPath}' already exists; choose another directory instead of " +
                                                  "overwriting or deleting an existing backup.",
                                                  SessionStoreFailureKind.Conflict);
        try
        {
            Directory.CreateDirectory(destinationPath);
            SessionStorePaths.EnsureStoreLeaf(destinationPath, backupPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException or SessionStoreException)
        {
            throw new SessionStoreBackupException(
                                                  $"Backup directory '{destinationPath}' cannot be used: {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }

        var created  = false;
        var verified = false;
        try
        {
            await using var source = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode       = SqliteOpenMode.ReadWriteCreate,
                Pooling    = false
            };
            await using (var destination = new SqliteConnection(builder.ToString()))
            {
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                created = true;
                // 在线备份 API 生成一致快照：WAL 中已提交但尚未 checkpoint 的内容也包含在内，因此不复制文件。
                source.BackupDatabase(destination);
                // 关闭连接才会把目标文件的改动同步落盘；backup API 已在事务内提交。
                await destination.CloseAsync().ConfigureAwait(false);
            }

            var info = await VerifyCreatedBackupAsync(destinationPath, marker, cancellationToken).ConfigureAwait(false);
            verified = true;
            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionStoreBackupException)
        {
            throw;
        }
        catch (SessionStoreMigrationException ex)
        {
            throw new SessionStoreBackupException(ex.Message, ex.Kind, ex);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException
                                       or SessionStoreDatabaseException)
        {
            throw new SessionStoreBackupException(
                                                  $"Business database '{database.DatabasePath}' could not be backed up to " +
                                                  $"'{backupPath}': {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
        finally
        {
            // 失败时只删除本次创建且未通过校验的部分备份；已存在的用户备份从不会被触碰。
            if (created && !verified) TryDeleteCreatedFile(backupPath);
        }
    }

    /// <summary>
    ///     <para>
    ///         只读校验备份目录中的 <c>session-store.db</c>：缺失、损坏、版本不受支持或有效（附库身份与版本）；
    ///         不比较迁移标记身份，身份一致性属于恢复的职责。
    ///     </para>
    ///     <para>
    ///         Read-only verifies <c>session-store.db</c> inside a backup directory: missing, damaged, unsupported, or
    ///         valid with its identity and version; the migration-marker identity is not compared because restoration
    ///         owns that check.
    ///     </para>
    /// </summary>
    /// <param name="backupDirectory">
    ///     <para>备份目录；须为绝对路径。</para>
    ///     <para>The backup directory; it must be absolute.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消可用性探测与完整性检查。</para>
    ///     <para>Cancels availability probing and the integrity check.</para>
    /// </param>
    /// <returns>
    ///     <para>校验状态、说明、库身份与 schema 版本。</para>
    ///     <para>The verification status, detail, database identity, and schema version.</para>
    /// </returns>
    /// <exception cref="SessionStoreBackupException">
    ///     <para>备份暂时不可读（锁等待、权限、只读介质或 IO 失败）。</para>
    ///     <para>The backup is temporarily unreadable (lock waits, permissions, read-only media, or IO failures).</para>
    /// </exception>
    public static async Task<SessionStoreBackupVerification> VerifyAsync(
        string backupDirectory, CancellationToken cancellationToken)
    {
        var backupPath = NormalizeDirectory(backupDirectory, nameof(backupDirectory));
        var filePath   = Path.Combine(backupPath, SessionStoreDatabase.DatabaseFileName);
        var attributes = TryGetAttributes(filePath);
        if (attributes is null)
            return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Missing,
                                                      $"Backup file '{filePath}' does not exist.", null, null);
        if ((attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Damaged,
                                                      $"Backup file '{filePath}' is a directory or reparse point.", null, null);

        var database     = new SessionStoreDatabase(backupPath, false);
        var availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionStoreDatabaseStatus.Missing :
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Missing,
                                                          $"Backup file '{filePath}' does not exist.", null, null);
            case SessionStoreDatabaseStatus.NeedsUpgrade :
            case SessionStoreDatabaseStatus.Unsupported :
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Unsupported,
                                                          $"Backup '{filePath}' has schema version {availability.Detail}; this build " +
                                                          $"supports version {SessionStoreDatabase.CurrentSchemaVersion}.",
                                                          null, null);
            case SessionStoreDatabaseStatus.Available :
                break;
            case SessionStoreDatabaseStatus.Failed :
                throw new SessionStoreBackupException(
                                                      $"Backup '{filePath}' cannot be read right now: {availability.Detail ?? "no detail"}.",
                                                      BackupFailureKind(availability.Error), availability.Error);
            default :
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Damaged,
                                                          $"Backup '{filePath}' is damaged: {availability.Detail ?? "no detail"}.",
                                                          null, null);
        }

        try
        {
            // 校验只读语义：打开连接只设置锁等待，不切换 journal 模式，避免验证动作改写备份文件本身。
            await using var connection = await database.OpenAsync(SqliteOpenMode.ReadWrite, false, cancellationToken)
                                                       .ConfigureAwait(false);
            var integrity = await SessionStoreDatabase.RunIntegrityCheckAsync(connection, cancellationToken)
                                                     .ConfigureAwait(false);
            if (integrity is not null)
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Damaged,
                                                          $"Backup '{filePath}' failed its integrity check: {integrity}.",
                                                          null, null);
            // integrity_check 不检查外键引用：悬挂的 session_placement 必须在替换目标库之前就被拒绝。
            // integrity_check does not check foreign-key references, so a dangling session_placement must be
            // rejected before the target database is replaced.
            var foreignKey = await SessionStoreDatabase.RunForeignKeyCheckAsync(connection, cancellationToken)
                                                      .ConfigureAwait(false);
            if (foreignKey is not null)
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Damaged,
                                                          $"Backup '{filePath}' failed its foreign-key check: {foreignKey}.",
                                                          null, null);
            var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                    .ConfigureAwait(false);
            if (!IsStoreIdentifier(identity))
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Damaged,
                                                          $"Backup '{filePath}' has no valid store identity.", null, null);
            var version = await SessionStoreDatabase.ReadSchemaVersionAsync(connection, cancellationToken)
                                                   .ConfigureAwait(false);
            if (version != SessionStoreDatabase.CurrentSchemaVersion)
                return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Unsupported,
                                                          $"Backup '{filePath}' reports schema version " +
                                                          $"{version?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}; this build " +
                                                          $"supports version {SessionStoreDatabase.CurrentSchemaVersion}.",
                                                          identity, version);
            return new SessionStoreBackupVerification(SessionStoreBackupVerificationStatus.Valid, null, identity,
                                                      version);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionStoreBackupException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException
                                       or SessionStoreDatabaseException)
        {
            throw new SessionStoreBackupException(
                                                  $"Backup '{filePath}' could not be verified: {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         把备份恢复到一个已完成迁移的 store：先收尾上一次中断的替换窗口，校验备份与标记身份一致，把现有业务库
    ///         复制为 <c>pre-restore</c> 快照，再经临时文件替换业务库，随后复核完整性与外键，最后删除可重建的索引文件。
    ///     </para>
    ///     <para>
    ///         Restores a backup into a store whose migration completed: it first settles an interrupted replacement
    ///         window, verifies that the backup identity matches the marker, copies the current business database to a
    ///         <c>pre-restore</c> snapshot, replaces the database through a temporary file, re-checks integrity and
    ///         foreign keys, and finally deletes the rebuildable index files.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>恢复目标 store 目录；须为绝对路径且带有已完成的迁移标记。</para>
    ///     <para>The restore-target store directory; it must be absolute and carry a completed migration marker.</para>
    /// </param>
    /// <param name="backupDirectory">
    ///     <para>备份目录，包含 <c>session-store.db</c>；该目录与文件恢复后保持原样。</para>
    ///     <para>The backup directory containing <c>session-store.db</c>; it stays untouched after the restore.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消租约等待、文件复制与最终校验。</para>
    ///     <para>Cancels lease waiting, file copying, and final validation.</para>
    /// </param>
    /// <exception cref="SessionStoreBackupException">
    ///     <para>目标未迁移、备份缺失／损坏／版本不支持、备份身份不符、目标不可写或恢复后校验失败。</para>
    ///     <para>The target is not migrated, the backup is missing, damaged, or unsupported, the backup identity mismatches, the target is unwritable, or post-restore validation fails.</para>
    /// </exception>
    public static async Task RestoreAsync(
        string storeDirectory, string backupDirectory, CancellationToken cancellationToken)
    {
        var storePath  = NormalizeDirectory(storeDirectory, nameof(storeDirectory));
        var backupPath = NormalizeDirectory(backupDirectory, nameof(backupDirectory));
        var marker     = await ReadCompletedMarkerAsync(storePath, "restore", cancellationToken)
           .ConfigureAwait(false);
        var verification = await VerifyAsync(backupPath, cancellationToken).ConfigureAwait(false);
        switch (verification.Status)
        {
            case SessionStoreBackupVerificationStatus.Valid :
                break;
            case SessionStoreBackupVerificationStatus.IdentityMismatch :
                throw new SessionStoreBackupException(
                                                      $"Backup in '{backupPath}' does not belong to store '{storePath}'.",
                                                      SessionStoreFailureKind.Conflict);
            case SessionStoreBackupVerificationStatus.Missing :
                throw new SessionStoreBackupException(
                                                      $"Backup in '{backupPath}' is missing and cannot be restored. " +
                                                      (verification.Detail ?? string.Empty).Trim(),
                                                      SessionStoreFailureKind.Missing);
            case SessionStoreBackupVerificationStatus.Unsupported :
                throw new SessionStoreBackupException(
                                                      $"Backup in '{backupPath}' uses an unsupported schema and cannot be restored. " +
                                                      (verification.Detail ?? string.Empty).Trim(),
                                                      SessionStoreFailureKind.Unsupported);
            default :
                throw new SessionStoreBackupException(
                                                      $"Backup in '{backupPath}' is damaged and cannot be restored. " +
                                                      (verification.Detail ?? string.Empty).Trim(),
                                                      SessionStoreFailureKind.Damaged);
        }

        if (!string.Equals(verification.StoreIdentity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreBackupException(
                                                  $"Backup in '{backupPath}' carries identity '{verification.StoreIdentity ?? "(none)"}' while " +
                                                  $"store '{storePath}' completed migration '{marker.MigrationId}' with identity " +
                                                  $"'{marker.StoreIdentity}'; restore only a backup of this store.",
                                                  SessionStoreFailureKind.Conflict);

        await using var lease = await AcquireLeaseAsync(storePath, cancellationToken).ConfigureAwait(false);
        // 租约内先收尾上一次中断的恢复：隔离中的旧日志必须放回或丢弃，之后才允许比较身份与替换文件。
        await SessionStoreRestoreRecovery.RecoverLockedAsync(storePath, cancellationToken).ConfigureAwait(false);
        using var settled = SessionStoreRestoreRecovery.HoldSettled(storePath);
        // 取得租约后复查标记：并发迁移或人工改写不能把恢复目标换成另一个迁移。
        var current = await ReadCompletedMarkerAsync(storePath, "restore", cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.MigrationId, marker.MigrationId, StringComparison.Ordinal) ||
            !string.Equals(current.StoreIdentity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreBackupException(
                                                  $"Store '{storePath}' changed while the restore was starting; retry the restore.",
                                                  SessionStoreFailureKind.Conflict);

        var databasePath = SessionStoreDatabase.PathFor(storePath);
        var backupFile   = Path.Combine(backupPath, SessionStoreDatabase.DatabaseFileName);
        var attributes   = TryGetAttributes(databasePath);
        if (attributes is { } existing &&
            (existing & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new SessionStoreBackupException(
                                                  $"Business database path '{databasePath}' is a directory or reparse point and cannot be " +
                                                  "replaced by a restore.",
                                                  SessionStoreFailureKind.Damaged);

        // 只有本次恢复确实保存了旧库时才记录路径：回滚必须针对这一份副本，不能按文件名猜目录里更早的副本。
        // Only the copy this restore actually saved is recorded: a rollback targets that copy and never guesses an
        // older copy in the directory by file name.
        string? preservedPath = null;
        try
        {
            if (attributes is not null)
                preservedPath = await PreserveExistingDatabaseAsync(storePath, databasePath, cancellationToken)
                   .ConfigureAwait(false);
            await CommitBackupAsync(storePath, backupFile, databasePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SessionStoreBackupException)
        {
            throw;
        }
        catch (SessionStoreMigrationException ex)
        {
            throw new SessionStoreBackupException(ex.Message, ex.Kind, ex);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException
                                       or SessionStoreDatabaseException)
        {
            throw new SessionStoreBackupException(
                                                  $"Store '{storePath}' could not be restored from '{backupPath}': {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }

        var database = new SessionStoreDatabase(storePath);
        try
        {
            await RequireAvailableAsync(database, cancellationToken).ConfigureAwait(false);
            await VerifyRestoredDatabaseAsync(database, marker, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SessionStoreBackupException or SessionStoreMigrationException
                                       or SqliteException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException
                                       or SessionStoreDatabaseException)
        {
            // 替换已发生且本身不可回滚：提交前校验已覆盖结构与外键，这里仍失败时把本次恢复保存的原库副本放回，
            // 避免"报错但当前库已被坏备份覆盖"。没有本次副本（目标原本缺失）时不回滚，副本保留，回滚失败一并报告。
            // The replacement already happened and cannot be undone by itself: the pre-commit check covers
            // structure and foreign keys, so if this still fails the copy this restore preserved is put back
            // instead of leaving the store overwritten by a bad backup. Without such a copy, because the target was
            // originally absent, no rollback is attempted; the copy stays and a failed rollback is reported too.
            var rollback = await TryRollBackRestoreAsync(storePath, databasePath, preservedPath).ConfigureAwait(false);
            string detail;
            if (rollback is null)
                detail = "the previous business database was put back";
            else if (preservedPath is null)
                detail = $"no previous business database was saved to put back because the store had no database file " +
                         $"before this restore ({rollback})";
            else
                detail = $"the previous business database could not be put back ({rollback}); restore it from the " +
                         "pre-restore copy before using this store";
            throw new SessionStoreBackupException(
                                                  $"Store '{storePath}' failed its post-restore validation: {ex.Message} " +
                                                  $"({detail}).",
                                                  ex is SessionStoreBackupException backup
                                                      ? backup.Kind
                                                      : SessionStoreErrorClassifier.Classify(ex), ex);
        }

        DeleteDerivedIndexFiles(storePath);
    }

    /// <summary>
    ///     <para>
    ///         把本次恢复保存的 pre-restore 副本放回业务库文件名，供替换后校验失败时撤销本次恢复；成功的
    ///         回滚依赖调用方传入的精确副本路径，未保存副本时拒绝回滚。返回失败原因，成功时返回 null。
    ///     </para>
    ///     <para>
    ///         Puts the pre-restore copy this restore saved back under the business-database name so a failed
    ///         post-replace validation undoes this restore; a successful rollback relies on the exact copy path the
    ///         caller passes and refuses to roll back when no copy was saved. It returns the failure reason, or null
    ///         on success.
    ///     </para>
    /// </summary>
    /// <param name="storePath">
    ///     <para>store 目录，用于校验回滚临时文件仍落在受保护目录内。</para>
    ///     <para>The store directory, used to confirm the rollback temporary file stays inside the protected directory.</para>
    /// </param>
    /// <param name="databasePath">
    ///     <para>业务库文件路径；回滚写回这个名字。</para>
    ///     <para>The business-database path the rollback writes back under.</para>
    /// </param>
    /// <param name="preservedPath">
    ///     <para>本次恢复保存的副本路径；为 null 表示恢复前业务库缺失，没有可回滚的旧库。</para>
    ///     <para>
    ///         The copy path this restore saved, or null when the business database was absent before the restore so
    ///         there is no previous database to roll back to.
    ///     </para>
    /// </param>
    /// <remarks>
    ///     <para>
    ///         回滚窗口只在替换之后、复核完成之前可达，因此本方法与恢复编排分开暴露给程序集内的定向回归，
    ///         用于断言副本选择与"没有本次副本时不回滚"的行为。
    ///     </para>
    ///     <para>
    ///         The rollback window is only reachable after the replacement and before re-validation, so this method is
    ///         exposed inside the assembly separately from the restore orchestration for focused regressions on copy
    ///         selection and on refusing to roll back without a copy from the current restore.
    ///     </para>
    /// </remarks>
    internal static async Task<string?> TryRollBackRestoreAsync(
        string storePath, string databasePath, string? preservedPath)
    {
        try
        {
            // 目录中可能留有更早的同名前缀副本；只有本次恢复记录的那一份可以放回，否则会用一个与本次无关
            // 的历史库覆盖刚恢复的数据库。
            if (preservedPath is null)
                return "this restore saved no previous database because the business database was absent";
            if (!FileExists(preservedPath))
                return $"the preserved copy '{preservedPath}' no longer exists";

            var tempPath = SessionStoreRestoreRecovery.NewPendingCopyPath(databasePath,
                                                                          SessionStoreRestoreRecovery.RollbackOperation);
            try
            {
                SessionStorePaths.EnsureStoreLeaf(storePath, tempPath);
                // 用 CreateNew 复制到新临时文件：副本被并发换成目录或链接时复制失败而不是跟随。
                await CopyFileAsync(preservedPath, tempPath, CancellationToken.None).ConfigureAwait(false);
                // 失败一代的 WAL／SHM 必须在放回副本之前隔离：隔离失败时不放回，也不报告回滚成功。隔离前先落盘
                // 恢复记录，使"日志已移开、副本未放回"的窗口在进程被终止后仍可恢复。
                // The failed generation's WAL／SHM are quarantined before the copy is put back; a failed quarantine
                // keeps the database untouched instead of reporting a successful rollback. The recovery record is
                // written first so the "journals moved aside, copy not yet put back" window survives process exit.
                var journals = JournalQuarantinesFor(databasePath, SessionStoreRestoreRecovery.RollbackOperation);
                await SessionStoreRestoreRecovery.WriteAsync(storePath, SessionStoreRestoreRecovery.RollbackOperation,
                                                              tempPath, journals, CancellationToken.None)
                                                 .ConfigureAwait(false);
                List<SessionStoreJournalQuarantine> quarantined;
                try
                {
                    quarantined = QuarantineJournalFiles(databasePath, SessionStoreRestoreRecovery.RollbackOperation,
                                                         journals);
                }
                catch (SessionStoreBackupException quarantineFailure)
                {
                    throw await SettleQuarantineFailureAsync(storePath, quarantineFailure).ConfigureAwait(false);
                }

                try
                {
                    if (FileExists(databasePath)) File.Replace(tempPath, databasePath, null);
                    else File.Move(tempPath, databasePath);
                }
                catch (Exception ex)
                {
                    var restoreFailure = RestoreQuarantinedJournals(quarantined);
                    if (restoreFailure is null) SessionStoreRestoreRecovery.TryClear(storePath);
                    throw restoreFailure is null
                        ? ex
                        : new SessionStoreBackupException(
                            $"The rollback could not put the preserved copy back ('{databasePath}': {ex.Message}) and the " +
                            $"quarantined journals could not all be put back ({restoreFailure}); the business database stays " +
                            $"unavailable until '{SessionStoreRestoreRecovery.RecordPathFor(storePath)}' is resolved.",
                            SessionStoreErrorClassifier.Classify(ex), ex);
                }

                DeleteQuarantinedJournals(quarantined);
                SessionStoreRestoreRecovery.TryClear(storePath);
                return null;
            }
            finally
            {
                // 记录仍在时保留回滚副本：它是"放回未发生"的唯一证据，下一次打开按记录继续处理。
                // While the record remains the rollback copy is kept: it is the only evidence that the put-back did
                // not happen, and the next open continues from the record.
                if (!SessionStoreRestoreRecovery.HasPendingRecord(storePath)) TryDeleteSiblingFiles(tempPath);
            }
        }
        catch (Exception ex) when (ex is SessionStoreBackupException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException)
        {
            // 严格日志清理抛出的备份异常同样只作为回滚失败原因返回，不替换调用方正在报告的原错误。
            // A backup exception from the strict journal removal likewise becomes a rollback reason instead of
            // replacing the error the caller is already reporting.
            return ex.Message;
        }
    }

    /// <summary>
    ///     <para>校验刚写入的备份：可用性、库身份与 schema 版本必须与标记和当前实现一致。</para>
    ///     <para>Verifies a freshly written backup: availability, identity, and schema version must match the marker and this build.</para>
    /// </summary>
    private static async Task<SessionStoreBackupInfo> VerifyCreatedBackupAsync(
        string destinationDirectory, SessionStoreMigrationMarker marker, CancellationToken cancellationToken)
    {
        var backupFile = Path.Combine(destinationDirectory, SessionStoreDatabase.DatabaseFileName);
        var verification = await VerifyAsync(destinationDirectory, cancellationToken).ConfigureAwait(false);
        if (verification.Status != SessionStoreBackupVerificationStatus.Valid)
            throw new SessionStoreBackupException(
                                                  $"Written backup '{backupFile}' did not verify ({verification.Status}): " +
                                                  $"{verification.Detail ?? "no detail"}.",
                                                  verification.Status == SessionStoreBackupVerificationStatus.Unsupported
                                                      ? SessionStoreFailureKind.Unsupported
                                                      : SessionStoreFailureKind.Damaged);
        if (!string.Equals(verification.StoreIdentity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreBackupException(
                                                  $"Backup '{backupFile}' carries identity '{verification.StoreIdentity}' while the marker " +
                                                  $"recorded '{marker.StoreIdentity}'.",
                                                  SessionStoreFailureKind.Conflict);
        if (verification.SchemaVersion != SessionStoreDatabase.CurrentSchemaVersion)
            throw new SessionStoreBackupException(
                                                  $"Backup '{backupFile}' reports schema version {verification.SchemaVersion}; this build " +
                                                  $"supports version {SessionStoreDatabase.CurrentSchemaVersion}.",
                                                  SessionStoreFailureKind.Unsupported);
        var length = new FileInfo(backupFile).Length;
        if (length == 0)
            throw new SessionStoreBackupException($"Backup '{backupFile}' is empty.", SessionStoreFailureKind.Damaged);
        return new SessionStoreBackupInfo(backupFile, verification.StoreIdentity!, verification.SchemaVersion!.Value,
                                          length, DateTimeOffset.UtcNow);
    }

    /// <summary>
    ///     <para>
    ///         把现有业务库复制为带 Unix 毫秒后缀的恢复前快照并返回该快照路径；只复制不移动、不删除，复制失败时
    ///         终止恢复并保留原库。
    ///     </para>
    ///     <para>
    ///         Copies the existing business database to a pre-restore snapshot suffixed with Unix milliseconds and
    ///         returns that snapshot path; it only copies, never moves or deletes, and a failed copy aborts the restore
    ///         while keeping the original.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>本次创建的快照文件路径；回滚只使用这个路径。</para>
    ///     <para>The snapshot file path this call created; rollback uses only this path.</para>
    /// </returns>
    private static async Task<string> PreserveExistingDatabaseAsync(
        string storePath, string databasePath, CancellationToken cancellationToken)
    {
        // 先 checkpoint 已知可打开的库：把 WAL 中已提交的内容并入主文件，使复制出的快照包含最新数据。
        try
        {
            var database = new SessionStoreDatabase(storePath);
            await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                       .ConfigureAwait(false);
            var checkpoint = connection.CreateCommand();
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException
                                       or NotSupportedException or PathTooLongException or SessionStoreException
                                       or SessionStoreDatabaseException)
        {
            // 损坏或暂时打不开的旧库仍原样复制；不能因为 checkpoint 失败就丢弃恢复前证明。
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var target    = databasePath + PreRestoreSuffix + timestamp.ToString(CultureInfo.InvariantCulture);
        while (FileExists(target))
        {
            timestamp++;
            target = databasePath + PreRestoreSuffix + timestamp.ToString(CultureInfo.InvariantCulture);
        }

        try
        {
            await CopyFileAsync(databasePath, target, cancellationToken).ConfigureAwait(false);
            return target;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or PathTooLongException)
        {
            throw new SessionStoreBackupException(
                                                  $"The existing business database '{databasePath}' could not be preserved before the restore " +
                                                  $"({ex.Message}); the restore was aborted without changing it.",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         经同目录临时文件把备份复制并替换到业务库；新库从未存在时用移动提交，已存在时用替换提交。提交前先
    ///         把待提交副本与计划隔离的日志写入持久化恢复记录，再隔离旧代际的 WAL／SHM：记录或隔离失败即中止且
    ///         不改动业务库主文件；替换失败时把隔离的日志放回，放回也失败则记录保留、业务库保持不可用。
    ///     </para>
    ///     <para>
    ///         Copies the backup and commits it to the business-database path through a same-directory temporary file:
    ///         a move commits the first creation and a replace commits over an existing file. Before committing it
    ///         writes the pending copy and the journals planned for quarantine into the durable recovery record, then
    ///         quarantines the old generation's WAL／SHM; a failed record or quarantine aborts without touching the
    ///         business-database file, a failed replacement puts the quarantined journals back, and a failed put-back
    ///         keeps the record so the database stays unavailable instead of silently disagreeing with its journal.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         替换前先对实际待提交的临时副本做完整性与外键校验：替换动作没有回滚，一旦提交就无法撤销，
    ///         因此结构问题必须在覆盖当前业务库之前暴露。
    ///     </para>
    ///     <para>
    ///         The temporary copy that is about to be committed is verified for integrity and foreign keys first:
    ///         the replacement has no rollback, so a structural problem must surface before the current business
    ///         database is overwritten.
    ///     </para>
    /// </remarks>
    private static async Task CommitBackupAsync(
        string storePath, string backupFile, string databasePath, CancellationToken cancellationToken)
    {
        var tempPath  = SessionStoreRestoreRecovery.NewPendingCopyPath(databasePath,
                                                                       SessionStoreRestoreRecovery.RestoreOperation);
        var created   = false;
        var committed = false;
        var journals  = JournalQuarantinesFor(databasePath, SessionStoreRestoreRecovery.RestoreOperation);
        try
        {
            SessionStorePaths.EnsureStoreLeaf(storePath, tempPath);
            await CopyFileAsync(backupFile, tempPath, cancellationToken).ConfigureAwait(false);
            created = true;
            await VerifyPendingCopyAsync(tempPath, backupFile, cancellationToken).ConfigureAwait(false);
            // 提交前再次确认叶子：恢复窗口内业务库名不得被换成目录或链接。
            var attributes = TryGetAttributes(databasePath);
            if (attributes is { } existing &&
                (existing & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new SessionStoreBackupException(
                                                      $"Business database path '{databasePath}' became a directory or reparse point and " +
                                                      "cannot be replaced.",
                                                      SessionStoreFailureKind.Damaged);
            // 隔离记录必须先落盘：被替换文件代际的 WAL／SHM 属于数据库持久状态，进程在隔离之后、替换之前被终止时，
            // 只能靠这份记录知道哪些日志被移开、旧主文件是否仍在原位。
            // The quarantine record must be durable first: the replaced generation's WAL／SHM are database state, so a
            // process killed between the quarantine and the replacement can only learn which journals were moved and
            // whether the old main file is still in place from this record.
            await SessionStoreRestoreRecovery.WriteAsync(storePath, SessionStoreRestoreRecovery.RestoreOperation,
                                                          tempPath, journals, cancellationToken)
                                              .ConfigureAwait(false);
            var recordDurableBeforeQuarantine = SessionStoreRestoreRecovery.HasPendingRecord(storePath);
            List<SessionStoreJournalQuarantine> quarantined;
            try
            {
                quarantined = QuarantineJournalFiles(databasePath, SessionStoreRestoreRecovery.RestoreOperation,
                                                     journals);
            }
            catch (SessionStoreBackupException quarantineFailure)
            {
                throw await SettleQuarantineFailureAsync(storePath, quarantineFailure).ConfigureAwait(false);
            }

            if (QuarantineCommittedHookForTests is { } hook)
                await hook(storePath, recordDurableBeforeQuarantine,
                           [.. quarantined.Select(journal => journal.QuarantinedPath)],
                           cancellationToken).ConfigureAwait(false);

            try
            {
                if (FileExists(databasePath)) File.Replace(tempPath, databasePath, null);
                else File.Move(tempPath, databasePath);
            }
            catch (Exception ex)
            {
                // 替换没有发生：把隔离的旧日志放回，旧主文件与它的日志仍然是自洽的一代；放回失败时保留记录，
                // 业务库保持不可用，并把两个失败一起报告。
                // The replacement did not happen, so the quarantined journals go back and the old main file stays
                // consistent with its own journal generation; a failed put-back keeps the record and reports both
                // failures so the database stays unavailable instead of being served with a journal aside.
                var restoreFailure = RestoreQuarantinedJournals(quarantined);
                if (restoreFailure is null) SessionStoreRestoreRecovery.TryClear(storePath);
                throw restoreFailure is null
                    ? ex
                    : new SessionStoreBackupException(
                        $"Backup '{backupFile}' could not be committed to '{databasePath}' ({ex.Message}) and the quarantined " +
                        $"journals could not all be put back ({restoreFailure}); the business database stays unavailable " +
                        $"until '{SessionStoreRestoreRecovery.RecordPathFor(storePath)}' is resolved.",
                        SessionStoreErrorClassifier.Classify(ex), ex);
            }

            committed = true;
            DeleteQuarantinedJournals(quarantined);
            SessionStoreRestoreRecovery.TryClear(storePath);
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
                                       or PathTooLongException or SessionStoreException)
        {
            throw new SessionStoreBackupException(
                                                  $"Backup '{backupFile}' could not be committed to '{databasePath}': {ex.Message}",
                                                  SessionStoreErrorClassifier.Classify(ex), ex);
        }
        finally
        {
            // 待提交副本只有在替换窗口已经收尾后才能删除：记录仍在时它是"替换未发生"的唯一证据。
            // The pending copy is deleted only once the window is settled: while the record exists it is the only
            // evidence that the replacement did not happen.
            if (created && !committed && !SessionStoreRestoreRecovery.HasPendingRecord(storePath))
                TryDeleteSiblingFiles(tempPath);
        }
    }

    /// <summary>
    ///     <para>
    ///         校验即将替换业务库的临时副本：完整性、外键与可读 schema 版本；失败在覆盖目标之前中止恢复。
    ///     </para>
    ///     <para>
    ///         Verifies the temporary copy that is about to replace the business database: integrity, foreign keys,
    ///         and a readable schema version; a failure aborts the restore before the target is overwritten.
    ///     </para>
    /// </summary>
    private static async Task VerifyPendingCopyAsync(
        string tempPath, string backupFile, CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource     = tempPath,
            Mode           = SqliteOpenMode.ReadWrite,
            Pooling        = false,
            DefaultTimeout = 5
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var integrity = await SessionStoreDatabase.RunIntegrityCheckAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (integrity is not null)
            throw new SessionStoreBackupException(
                                                  $"The copy of backup '{backupFile}' that was about to replace the business database " +
                                                  $"failed its integrity check: {integrity}. The restore was aborted before overwriting it.",
                                                  SessionStoreFailureKind.Damaged);
        var foreignKey = await SessionStoreDatabase.RunForeignKeyCheckAsync(connection, cancellationToken)
                                                  .ConfigureAwait(false);
        if (foreignKey is not null)
            throw new SessionStoreBackupException(
                                                  $"The copy of backup '{backupFile}' that was about to replace the business database " +
                                                  $"failed its foreign-key check: {foreignKey}. The restore was aborted before overwriting it.",
                                                  SessionStoreFailureKind.Damaged);
        var version = await SessionStoreDatabase.ReadSchemaVersionAsync(connection, cancellationToken)
                                               .ConfigureAwait(false);
        if (version != SessionStoreDatabase.CurrentSchemaVersion)
            throw new SessionStoreBackupException(
                                                  $"The copy of backup '{backupFile}' reports schema version " +
                                                  $"{version?.ToString(CultureInfo.InvariantCulture) ?? "(none)"} while this build " +
                                                  $"supports version {SessionStoreDatabase.CurrentSchemaVersion}; the restore was aborted " +
                                                  "before overwriting the business database.",
                                                  SessionStoreFailureKind.Unsupported);
    }

    /// <summary>
    ///     <para>恢复后复核业务库：可用、库身份与标记一致、完整性与外键检查通过。</para>
    ///     <para>Re-checks the restored business database: available, identity matching the marker, and passing integrity and foreign-key checks.</para>
    /// </summary>
    private static async Task VerifyRestoredDatabaseAsync(
        SessionStoreDatabase database, SessionStoreMigrationMarker marker, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                .ConfigureAwait(false);
        if (!string.Equals(identity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreBackupException(
                                                  $"Restored business database '{database.DatabasePath}' carries identity " +
                                                  $"'{identity ?? "(none)"}' instead of the marker's '{marker.StoreIdentity}'.",
                                                  SessionStoreFailureKind.Conflict);
        var integrity = await SessionStoreDatabase.RunIntegrityCheckAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (integrity is not null)
            throw new SessionStoreBackupException(
                                                  $"Restored business database '{database.DatabasePath}' failed its integrity check: " +
                                                  $"{integrity}.",
                                                  SessionStoreFailureKind.Damaged);
        var foreignKey = await SessionStoreDatabase.RunForeignKeyCheckAsync(connection, cancellationToken)
                                                  .ConfigureAwait(false);
        if (foreignKey is not null)
            throw new SessionStoreBackupException(
                                                  $"Restored business database '{database.DatabasePath}' failed its foreign-key check: " +
                                                  $"{foreignKey}.",
                                                  SessionStoreFailureKind.Damaged);
    }

    /// <summary>
    ///     <para>
    ///         读取已完成迁移的标记；缺失、损坏、进行中或没有库身份时分别按失败类别拒绝，恢复与备份都不得
    ///         在未迁移或状态异常时继续。
    ///     </para>
    ///     <para>
    ///         Reads a completed migration marker; a missing, damaged, in-progress, or identity-less marker is refused
    ///         with the matching failure category because neither backup nor restore may continue on an unmigrated or
    ///         abnormal store.
    ///     </para>
    /// </summary>
    private static async Task<SessionStoreMigrationMarker> ReadCompletedMarkerAsync(
        string storePath, string operation, CancellationToken cancellationToken)
    {
        var read = await SessionStoreMigration.ReadMarkerAsync(storePath, cancellationToken).ConfigureAwait(false);
        switch (read.Status)
        {
            case SessionStoreMarkerReadStatus.Missing :
                throw new SessionStoreBackupException(
                                                      $"Session store '{storePath}' has no migration marker; it cannot {operation} before the " +
                                                      "explicit migration completes.",
                                                      SessionStoreFailureKind.MigrationRequired);
            case SessionStoreMarkerReadStatus.Damaged :
                throw new SessionStoreBackupException(
                                                      $"Session store '{storePath}' has a damaged migration marker; keep it and recover it " +
                                                      $"manually before trying to {operation}.",
                                                      SessionStoreFailureKind.Damaged);
        }

        var marker = read.Marker!;
        if (!marker.IsComplete)
            throw new SessionStoreBackupException(
                                                  $"Session store '{storePath}' has not finished switching every migration batch; it cannot " +
                                                  $"{operation} while the migration is in progress.",
                                                  SessionStoreFailureKind.Conflict);
        if (!IsStoreIdentifier(marker.StoreIdentity))
            throw new SessionStoreBackupException(
                                                  $"Session store '{storePath}' completed its migration marker without a valid database " +
                                                  $"identity, so it cannot {operation}.",
                                                  SessionStoreFailureKind.Conflict);
        return marker;
    }

    /// <summary>
    ///     <para>要求业务库可用；缺失、损坏与版本问题分别映射为备份失败类别。</para>
    ///     <para>Requires an available business database and maps absence, damage, and version problems onto backup failure categories.</para>
    /// </summary>
    private static async Task RequireAvailableAsync(
        SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        var availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Status == SessionStoreDatabaseStatus.Available) return;
        var kind = availability.Status switch
        {
            SessionStoreDatabaseStatus.Missing => SessionStoreFailureKind.Missing,
            SessionStoreDatabaseStatus.Damaged => SessionStoreFailureKind.Damaged,
            SessionStoreDatabaseStatus.NeedsSchema => SessionStoreFailureKind.Damaged,
            SessionStoreDatabaseStatus.NeedsUpgrade => SessionStoreFailureKind.Unsupported,
            SessionStoreDatabaseStatus.Unsupported => SessionStoreFailureKind.Unsupported,
            _ => BackupFailureKind(availability.Error)
        };
        throw new SessionStoreBackupException(
                                              $"Business database '{database.DatabasePath}' is not usable ({availability.Status}): " +
                                              $"{availability.Detail ?? "no detail"}.",
                                              kind, availability.Error);
    }

    /// <summary>
    ///     <para>获取 store 级迁移租约；被其他迁移、备份或恢复占用时映射为锁定失败。</para>
    ///     <para>Acquires the store-level migration lease, mapping contention onto the locked failure category.</para>
    /// </summary>
    private static async Task<SessionStoreMigrationLease> AcquireLeaseAsync(
        string storePath, CancellationToken cancellationToken) =>
        await SessionStoreMigration.AcquireLeaseAsync(storePath, cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     <para>以异步流复制文件并强制 flush，确保提交前内容已经落盘。</para>
    ///     <para>Copies a file through asynchronous streams and forces a flush so the content is durable before the commit.</para>
    /// </summary>
    private static async Task CopyFileAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                                FileShare.Read | FileShare.Delete, CopyBufferBytes,
                                                FileOptions.Asynchronous);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write,
                                                     FileShare.None, CopyBufferBytes,
                                                     FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         隔离阶段的失败收尾：记录已经落盘但日志没有全部移开，此时按记录把文件代际恢复一致并清除记录，
    ///         让调用方看到原始的隔离失败而不是一条会自愈的"存储不可用"。收尾也失败时保留记录并合并报告。
    ///     </para>
    ///     <para>
    ///         Settles a quarantine-stage failure: the record is already durable but not every journal was moved, so the
    ///         file generations are brought back into agreement and the record cleared, letting the caller see the
    ///         original quarantine failure instead of a self-healing "store unavailable". A failed settle keeps the
    ///         record and reports both failures.
    ///     </para>
    /// </summary>
    private static async Task<Exception> SettleQuarantineFailureAsync(
        string storePath, SessionStoreBackupException failure)
    {
        try
        {
            await SessionStoreRestoreRecovery.RecoverLockedAsync(storePath, CancellationToken.None)
                                             .ConfigureAwait(false);
            return failure;
        }
        catch (Exception settleFailure) when (settleFailure is SessionStoreBackupException or IOException
                                                  or UnauthorizedAccessException or NotSupportedException
                                                  or PathTooLongException)
        {
            return new SessionStoreBackupException(
                                                   $"{failure.Message} The quarantined journals could not all be put back " +
                                                   $"({settleFailure.Message}); the business database stays unavailable until " +
                                                   $"'{SessionStoreRestoreRecovery.RecordPathFor(storePath)}' is resolved.",
                                                   failure.Kind, failure);
        }
    }

    /// <summary>
    ///     <para>
    ///         计算本次操作计划隔离的日志对：每个存在的日志文件分配一个固定的非日志文件名；先算出再落盘记录，
    ///         保证记录与实际移动使用同一批名字。
    ///     </para>
    ///     <para>
    ///         Computes the journal pairs the operation plans to quarantine: every existing journal gets a fixed
    ///         non-journal name. Computing them before writing the record keeps the record and the actual moves on the
    ///         same names.
    ///     </para>
    /// </summary>
    private static List<SessionStoreJournalQuarantine> JournalQuarantinesFor(string databasePath, string operation) =>
        JournalSuffixes.Select(suffix => databasePath + suffix)
                       .Where(FileExists)
                       .Select(original => new SessionStoreJournalQuarantine(
                                   original, SessionStoreRestoreRecovery.NewQuarantinePath(original)))
                       .ToList();

    /// <summary>
    ///     <para>
    ///         把旧代际的 <c>-wal</c>／<c>-shm</c> 按计划移到非日志文件名下并返回这些隔离记录；等价的文件在移走后
    ///         不会再被 SQLite 应用到任何主文件上。移动失败时先把已移走的日志放回再抛出，调用方据此中止替换。
    ///     </para>
    ///     <para>
    ///         Moves the previous generation's <c>-wal</c>／<c>-shm</c> aside to the planned non-journal names and
    ///         returns the quarantine records; a file moved aside can no longer be applied to any main file by SQLite.
    ///         When a move fails the already moved journals are put back before it throws, so the caller can abort the
    ///         replacement.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         先隔离再替换而不是先删除：日志可能仍持有尚未 checkpoint 的已提交内容，替换若随后失败，先删除就等于
    ///         在保持旧主文件的同时丢掉它的数据。隔离只改名，替换成功后才真正删除。
    ///     </para>
    ///     <para>
    ///         Quarantine before the replacement instead of deleting: a journal can still hold committed content that
    ///         has not been checkpointed, so deleting first would drop that data while the old main file stays when
    ///         the replacement later fails. Quarantine only renames; the files are deleted after the replacement
    ///         succeeds.
    ///     </para>
    /// </remarks>
    private static List<SessionStoreJournalQuarantine> QuarantineJournalFiles(
        string databasePath, string operation, IReadOnlyList<SessionStoreJournalQuarantine> planned)
    {
        var quarantined = new List<SessionStoreJournalQuarantine>(planned.Count);
        foreach (var journal in planned)
        {
            try
            {
                // 计划后文件可能已被移除（例如并发清理），此时无需隔离。
                if (FileExists(journal.OriginalPath)) File.Move(journal.OriginalPath, journal.QuarantinedPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                var restoreFailure = RestoreQuarantinedJournals(quarantined);
                throw new SessionStoreBackupException(
                                                      $"Stale journal file '{journal.OriginalPath}' could not be moved aside, so the " +
                                                      $"{operation} stopped without changing '{databasePath}': {ex.Message}" +
                                                      (restoreFailure is null
                                                           ? string.Empty
                                                           : $" Journal files already moved aside could not all be put back " +
                                                             $"({restoreFailure}); restore them manually."),
                                                      SessionStoreErrorClassifier.Classify(ex), ex);
            }

            quarantined.Add(journal);
        }

        return quarantined;
    }

    /// <summary>
    ///     <para>把隔离的旧日志放回原文件名；返回第一个失败原因，全部成功时返回 null。</para>
    ///     <para>Puts quarantined journals back under their original names; returns the first failure reason, or null when all succeeded.</para>
    /// </summary>
    private static string? RestoreQuarantinedJournals(IReadOnlyList<SessionStoreJournalQuarantine> quarantined)
    {
        string? failure = null;
        foreach (var journal in quarantined)
            try
            {
                // 只在确实存在隔离副本时动作；原日志名已被占用说明两代日志同时在场，不能按成功处理。
                if (!FileExists(journal.QuarantinedPath)) continue;
                if (FileExists(journal.OriginalPath))
                {
                    failure ??= $"'{journal.OriginalPath}' is occupied while '{journal.QuarantinedPath}' is still aside";
                    continue;
                }

                File.Move(journal.QuarantinedPath, journal.OriginalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failure ??= $"'{journal.OriginalPath}' is left as '{journal.QuarantinedPath}' ({ex.Message})";
            }

        return failure;
    }

    /// <summary>
    ///     <para>
    ///         替换成功后删除隔离的旧日志；此时它们已不在日志文件名下，删除失败不会让 SQLite 把它们应用到新主文件，
    ///         因此按临时文件尽力清理。
    ///     </para>
    ///     <para>
    ///         Deletes quarantined journals after a successful replacement; they no longer carry journal names, so a
    ///         failed deletion cannot make SQLite apply them to the new main file and is handled as temporary-file
    ///         cleanup.
    ///     </para>
    /// </summary>
    private static void DeleteQuarantinedJournals(IReadOnlyList<SessionStoreJournalQuarantine> quarantined)
    {
        if (quarantined.Count == 0) return;
        TryDeleteSiblingFiles([.. quarantined.Select(journal => journal.QuarantinedPath)]);
    }

    /// <summary>
    ///     <para>删除恢复后可重建的索引派生文件（索引库及其 WAL／SHM），不触碰业务表与旧 JSON 文件。</para>
    ///     <para>Deletes the rebuildable index-derived files (the index database and its WAL／SHM) after a restore without touching business tables or legacy JSON files.</para>
    /// </summary>
    private static void DeleteDerivedIndexFiles(string storePath)
    {
        var indexPath = Path.Combine(storePath, SessionIndexStore.DatabaseFileName);
        TryDeleteSiblingFiles(indexPath, indexPath + "-wal", indexPath + "-shm");
    }

    /// <summary>
    ///     <para>
    ///         尽力删除明确属于自己的临时文件与可重建的派生索引文件；失败只影响清理，不掩盖恢复结果。业务库
    ///         自身的 WAL／SHM 不属于此范围，必须经 <see cref="QuarantineJournalFiles" /> 隔离。
    ///     </para>
    ///     <para>
    ///         Best-effort deletes temporary files this operation owns and rebuildable derived index files; a failure
    ///         only affects cleanup and never masks the restore result. The business database's own WAL／SHM are not
    ///         in this scope and must be quarantined through <see cref="QuarantineJournalFiles" />.
    ///     </para>
    /// </summary>
    private static void TryDeleteSiblingFiles(params string[] paths)
    {
        foreach (var path in paths)
            try
            {
                if (FileExists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // 派生索引与临时文件清理失败不影响业务库恢复结果。
            }
    }

    /// <summary>
    ///     <para>删除本次创建但未通过校验的备份文件。</para>
    ///     <para>Deletes a backup file this call created that did not pass verification.</para>
    /// </summary>
    private static void TryDeleteCreatedFile(string path)
    {
        try
        {
            if (FileExists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清理失败保留原始备份错误，不掩盖诊断。
        }
    }

    /// <summary>
    ///     <para>把底层异常归类为备份失败类别；空异常按 IO 失败处理。</para>
    ///     <para>Classifies the underlying exception into a backup failure category, defaulting to IO failure.</para>
    /// </summary>
    private static SessionStoreFailureKind BackupFailureKind(Exception? exception) =>
        exception is null ? SessionStoreFailureKind.IoFailure : SessionStoreErrorClassifier.Classify(exception);

    /// <summary>
    ///     <para>判断值是否为 32 位小写十六进制库身份。</para>
    ///     <para>Whether a value is a 32-character lowercase hexadecimal store identity.</para>
    /// </summary>
    private static bool IsStoreIdentifier(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit) && value.All(character => !char.IsUpper(character));

    /// <summary>
    ///     <para>返回文件的属性；文件或父目录缺失时返回 null。</para>
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
    ///     <para>按名字判断文件是否存在；目录占位同样视为存在，避免把目录当成可创建的目标。</para>
    ///     <para>Whether something exists under a name; a directory placeholder also counts so it is never treated as creatable.</para>
    /// </summary>
    private static bool FileExists(string path) => TryGetAttributes(path) is not null;

    /// <summary>
    ///     <para>校验并规范化目录；备份与恢复不接受相对路径。</para>
    ///     <para>Validates and normalizes a directory; backup and restore refuse relative paths.</para>
    /// </summary>
    private static string NormalizeDirectory(string directory, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory, parameterName);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException($"Directory '{directory}' must be an absolute path.", parameterName);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <summary>
    ///     <para>恢复前快照的文件名前缀，用于诊断与人工识别。</para>
    ///     <para>The pre-restore snapshot file-name prefix, used for diagnostics and manual identification.</para>
    /// </summary>
    internal const string PreRestoreSuffix = ".pre-restore-";
}
