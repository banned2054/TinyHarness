using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         会话存储迁移的检查与执行入口：缺失标记时首次导入，存在标记时从第一个未切换批次续作；每批先读取并
///         校验完整来源快照，再在单个事务内整体替换该批数据，最后原子写入批次切换证明。
///     </para>
///     <para>
///         The inspection and execution entry of the session-store migration: a missing marker starts the first
///         import, while an existing marker resumes at the first unswitched batch. Each batch reads and validates one
///         complete source snapshot, replaces that batch inside one transaction, and only then atomically records the
///         switch.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         批次顺序固定为组织状态 → 项目目录册 → 会话归属，与 <see cref="SessionStoreMigrationMarker.BatchOrder" />
///         一致。业务库事务与标记文件写入不是跨文件原子的：中断只会停留在"某批仍待切换"，重试按快照幂等重做
///         该批。已切换批次不再重新导入，其旧来源只用于诊断。
///     </para>
///     <para>
///         The order is fixed to organization state → project catalog → session placement, matching
///         <see cref="SessionStoreMigrationMarker.BatchOrder" />. The database transaction and the marker write are
///         not atomic across files: an interruption always leaves "this batch still pending", and a retry redoes that
///         batch idempotently from a fresh snapshot. Switched batches are never imported again; their legacy sources
///         are diagnosis input only.
///     </para>
///     <para>
///         组织状态与项目目录册在批次提交后、标记写入前会重新计算来源指纹；若旧来源在该窗口内被旧程序改写，
///         本次运行以 <see cref="SessionStoreFailureKind.Conflict" /> 失败且不把批次标记为已切换，重试会导入新
///         来源。会话归属的日志数量可能很多，因此该批指纹是文件名、长度与写入时间的摘要，检查时同样只做该
///         廉价比较；日志内容变化必然改变长度或写入时间。
///     </para>
///     <para>
///         Organization state and the project catalog recompute their source fingerprint after the batch commits and
///         before the marker write. If an older program rewrites the legacy source inside that window, the run fails
///         with <see cref="SessionStoreFailureKind.Conflict" /> and leaves the batch pending, so a retry imports the
///         new source. Session placement may cover thousands of logs, so its fingerprint is a digest of file names,
///         lengths, and write times and inspection compares only that cheap digest; a content change always changes
///         a length or a write time.
///     </para>
///     <para>
///         旧文件在全部批次切换并通过结构校验前保持原样，完成后也不会自动删除；迁移本身不调用模型、不改写
///         工作区，也不修改应用偏好中的全局设置。
///     </para>
///     <para>
///         Legacy files stay untouched until every batch is switched and validated and are never deleted automatically
///         afterwards. The migration never calls a model, never writes the workspace, and never changes global
///         application preferences.
///     </para>
/// </remarks>
public static partial class SessionStoreMigration
{
    /// <summary>
    ///     <para>业务库元数据中保存本次迁移标识的键；用于确认库与标记属于同一次迁移。</para>
    ///     <para>
    ///         The metadata key holding this migration's identity; it confirms that the database and the marker belong to
    ///         one migration.
    ///     </para>
    /// </summary>
    internal const string MigrationIdMetaKey = "migration_id";

    private const int MaxDiagnostics = 100;

    private static readonly string[] OrganizationTables =
    [
        "archived_session",
        "pinned_session",
        "pinned_project",
        "project_order",
        "session_order",
        "collapsed_group"
    ];

    /// <summary>
    ///     <para>
    ///         检查一个 store 的迁移状态：不创建、不修复也不改写任何文件。标记缺失返回
    ///         <see cref="SessionStoreMigrationStatus.NotStarted" />，标记损坏返回
    ///         <see cref="SessionStoreMigrationStatus.MarkerDamaged" />；异常状态只报告，不抛出。
    ///     </para>
    ///     <para>
    ///         Inspects one store's migration state without creating, repairing, or rewriting any file. A missing
    ///         marker reports <see cref="SessionStoreMigrationStatus.NotStarted" /> and a damaged marker reports
    ///         <see cref="SessionStoreMigrationStatus.MarkerDamaged" />; abnormal states are reported rather than
    ///         thrown.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         迁移完成时核对业务库可用性与库身份；已切换批次对应的旧来源若再次被写入，会在
    ///         <see cref="SessionStoreMigrationInspection.Warnings" /> 中提示，但不改变状态。会话归属只比较
    ///         文件名、长度与写入时间摘要。
    ///     </para>
    ///     <para>
    ///         On completion the business-database availability and identity are checked; legacy sources of switched
    ///         batches that were written again surface in <see cref="SessionStoreMigrationInspection.Warnings" />
    ///         without changing the status. Session placement compares only the file-name, length, and write-time
    ///         digest.
    ///     </para>
    /// </remarks>
    /// <param name="storeDirectory">
    ///     <para>受保护的 session store 目录；须为绝对路径。</para>
    ///     <para>The protected session-store directory; it must be absolute.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消标记读取、可用性探测与指纹比较。</para>
    ///     <para>Cancels marker reading, availability probing, and fingerprint comparison.</para>
    /// </param>
    /// <returns>
    ///     <para>状态分类、说明、标记摘要与不改变状态的诊断警告。</para>
    ///     <para>The status category, a detail, the marker summary, and diagnostics that do not change the status.</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     <para>store 目录为空或不是绝对路径。</para>
    ///     <para>The store directory is blank or not absolute.</para>
    /// </exception>
    public static Task<SessionStoreMigrationInspection> InspectAsync(
        string storeDirectory, CancellationToken cancellationToken) =>
        InspectAsync(new SessionStoreMigrationOptions(storeDirectory), cancellationToken);

    /// <summary>
    ///     <para>
    ///         按显式迁移输入检查状态：给出侧栏偏好路径时可复算组织状态的完整来源指纹；未给出时只能按"无侧栏
    ///         偏好"复算，组织状态来源的差异会以警告说明可能是该文件不可见。
    ///     </para>
    ///     <para>
    ///         Inspects the state for explicit migration input: with a sidebar preferences path the full
    ///         organization-state source fingerprint is recomputed; without one only the "no sidebar preferences"
    ///         variant can be recomputed, and an organization-source difference warns that this file may be the reason.
    ///     </para>
    /// </summary>
    /// <param name="options">
    ///     <para>检查输入：store 目录与可选的侧栏偏好路径。</para>
    ///     <para>The inspection input: the store directory and an optional sidebar preferences path.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消标记读取、可用性探测与指纹比较。</para>
    ///     <para>Cancels marker reading, availability probing, and fingerprint comparison.</para>
    /// </param>
    /// <returns>
    ///     <para>状态分类、说明、标记摘要与不改变状态的诊断警告。</para>
    ///     <para>The status category, a detail, the marker summary, and diagnostics that do not change the status.</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     <para>store 目录为空或不是绝对路径。</para>
    ///     <para>The store directory is blank or not absolute.</para>
    /// </exception>
    public static async Task<SessionStoreMigrationInspection> InspectAsync(
        SessionStoreMigrationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var directory = NormalizeStoreDirectory(options.StoreDirectory);
        var read      = await ReadMarkerAsync(directory, cancellationToken).ConfigureAwait(false);
        switch (read.Status)
        {
            case SessionStoreMarkerReadStatus.Missing :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.NotStarted, null, null, null,
                                                           new Dictionary<SessionStoreMigrationBatch,
                                                               SessionStoreBatchState>(), []);
            case SessionStoreMarkerReadStatus.Damaged :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.MarkerDamaged, read.Detail,
                                                           null, null,
                                                           new Dictionary<SessionStoreMigrationBatch,
                                                               SessionStoreBatchState>(), []);
        }

        var marker   = read.Marker!;
        var database = new SessionStoreDatabase(directory);
        var warnings = new List<string>();
        if (marker.LastError is { Length: > 0 } lastError)
            warnings.Add($"The last migration run reported: {lastError}");

        await CollectLegacySourceWarningsAsync(directory, options.SidebarPreferencesPath, marker, warnings,
                                               cancellationToken).ConfigureAwait(false);

        if (!marker.IsComplete)
            return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.InProgress,
                                                       $"Batches still pending: {string.Join(", ", marker.PendingBatches)}.",
                                                       marker.MigrationId,
                                                       marker.StoreIdentity, CopyBatches(marker), warnings);

        var availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionStoreDatabaseStatus.Missing :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.DatabaseMissing,
                                                           $"The marker says migration '{marker.MigrationId}' completed, " +
                                                           $"but '{database.DatabasePath}' is missing; restore the business " +
                                                           "database from a backup instead of importing legacy files again.",
                                                           marker.MigrationId, marker.StoreIdentity,
                                                           CopyBatches(marker), warnings);
            case SessionStoreDatabaseStatus.Available :
                break;
            case SessionStoreDatabaseStatus.NeedsUpgrade :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.DatabaseNeedsUpgrade,
                                                           "The business database uses schema version below " +
                                                           $"{SessionStoreDatabase.CurrentSchemaVersion} ({availability.Detail ?? "no detail"}); " +
                                                           "run the explicit session-store migration to upgrade it in place.",
                                                           marker.MigrationId, marker.StoreIdentity,
                                                           CopyBatches(marker), warnings);
            case SessionStoreDatabaseStatus.Failed :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.DatabaseDamaged,
                                                           "The business database could not be inspected: " +
                                                           $"{availability.Detail ?? "no detail"}.",
                                                           marker.MigrationId, marker.StoreIdentity,
                                                           CopyBatches(marker), warnings);
            default :
                return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.DatabaseDamaged,
                                                           $"The business database is {availability.Status}: " +
                                                           $"{availability.Detail ?? "no detail"}.",
                                                           marker.MigrationId, marker.StoreIdentity,
                                                           CopyBatches(marker), warnings);
        }

        var identity = await ReadDatabaseIdentityAsync(database, cancellationToken).ConfigureAwait(false);
        if (marker.StoreIdentity is null || identity is null ||
            !string.Equals(identity, marker.StoreIdentity, StringComparison.Ordinal))
            return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.DatabaseReplaced,
                                                       "The business database carries identity " +
                                                       $"'{identity ?? "(none)"}' while the marker recorded " +
                                                       $"'{marker.StoreIdentity ?? "(none)"}'; " +
                                                       "the file was replaced or the marker belongs to another store.",
                                                       marker.MigrationId, marker.StoreIdentity, CopyBatches(marker),
                                                       warnings);

        return new SessionStoreMigrationInspection(SessionStoreMigrationStatus.Completed, null, marker.MigrationId,
                                                   marker.StoreIdentity, CopyBatches(marker), warnings);
    }

    /// <summary>
    ///     <para>
    ///         执行或续作一个 store 的迁移，返回每批的切换与导入报告。缺失标记时先写开始标记再建库；存在标记时
    ///         从第一个未切换批次继续，全部切换后做完整性、外键与行数校验并写入最终标记。
    ///     </para>
    ///     <para>
    ///         Runs or resumes one store's migration and returns per-batch switching and import reports. A missing
    ///         marker writes the start marker before creating the database; an existing marker resumes at the first
    ///         unswitched batch, and after every batch is switched the run checks integrity, foreign keys, and row
    ///         counts before writing the final marker.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         同一 store 的迁移、备份与恢复由迁移租约互斥；并发尝试等待有界后以
    ///         <see cref="SessionStoreFailureKind.Locked" /> 失败。取消在批次提交前发生时不留下部分事务；提交后、
    ///         标记写入前取消时该批仍待切换，重试重新导入。旧来源在全部批次切换前保持原样，任何失败都不删除
    ///         迁移标记、业务库或旧文件。取得租约后先收尾上一次中断的替换窗口，否则导入会读到日志仍停在隔离名下的
    ///         旧主文件；收尾失败时按对应类别拒绝，不基于不完整的一代继续导入。
    ///     </para>
    ///     <para>
    ///         Migration, backup, and restore of one store are mutually exclusive through the migration lease; a
    ///         concurrent attempt waits within bounds and then fails with
    ///         <see cref="SessionStoreFailureKind.Locked" />. Cancellation before a batch commits leaves no partial
    ///         transaction; cancellation after the commit but before the marker write leaves that batch pending and a
    ///         retry imports it again. Legacy sources stay untouched until every batch is switched, and no failure
    ///         deletes the marker, the business database, or legacy files. Once the lease is held an interrupted
    ///         replacement window is settled first, because otherwise the import would read an old main file whose
    ///         journal still sits under a quarantined name; a failed settle is refused with the matching category
    ///         instead of importing on top of an incomplete generation.
    ///     </para>
    /// </remarks>
    /// <param name="options">
    ///     <para>迁移输入：store 目录与可选的桌面侧栏偏好路径。</para>
    ///     <para>The migration input: the store directory and an optional desktop sidebar preferences path.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消快照读取、事务、标记写入与最终校验。</para>
    ///     <para>Cancels snapshot reads, transactions, marker writes, and final validation.</para>
    /// </param>
    /// <returns>
    ///     <para>迁移标识、库身份与按迁移顺序的批次报告；已完成时为 <see cref="SessionStoreMigrationResult.AlreadyCompleted" />。</para>
    ///     <para>
    ///         The migration identity, database identity, and batch reports in migration order; already-completed runs set
    ///         <see cref="SessionStoreMigrationResult.AlreadyCompleted" />.
    ///     </para>
    /// </returns>
    /// <exception cref="SessionStoreMigrationException">
    ///     <para>标记损坏、旧来源无法表示、来源在导入期间变化、库身份不符、业务库丢失或损坏、租约被占用。</para>
    ///     <para>
    ///         The marker is damaged, a legacy source cannot be represented, sources changed during the import, the database
    ///         identity contradicts the marker, the database is lost or damaged, or the lease is held.
    ///     </para>
    /// </exception>
    public static async Task<SessionStoreMigrationResult> MigrateAsync(
        SessionStoreMigrationOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var             storeDirectory = NormalizeStoreDirectory(options.StoreDirectory);
        await using var lease = await AcquireLeaseAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        // 迁移前先收尾中断的恢复：隔离中的旧日志未归位时，后续导入会读到缺少最新数据的旧主库。
        await SessionStoreRestoreRecovery.RecoverLockedAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        using var settled = SessionStoreRestoreRecovery.HoldSettled(storeDirectory);

        var markerRead = await ReadMarkerAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        var database = new SessionStoreDatabase(storeDirectory);
        SessionStoreMigrationMarker marker;
        switch (markerRead.Status)
        {
            case SessionStoreMarkerReadStatus.Damaged :
                throw new SessionStoreMigrationException("Session-store migration marker "                 +
                                                         $"'{MarkerPathFor(storeDirectory)}' is damaged; " +
                                                         "keep it and recover it manually instead of overwriting it.",
                                                         SessionStoreFailureKind.Damaged);
            case SessionStoreMarkerReadStatus.Missing :
                marker = SessionStoreMigrationMarker.Start(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow);
                // 先写开始标记再建库：中断后由"标记存在、身份为空"恢复，绝不把已迁移 store 当成首次迁移。
                await WriteMarkerAsync(storeDirectory, marker, cancellationToken).ConfigureAwait(false);
                break;
            default :
                marker = markerRead.Marker!;
                break;
        }

        if (marker.IsComplete)
            return await BuildAlreadyCompletedResultAsync(storeDirectory, database, marker, cancellationToken)
               .ConfigureAwait(false);

        var binding = await EnsureDatabaseBindingAsync(database, marker, cancellationToken).ConfigureAwait(false);
        marker = binding.Marker;

        var switchedThisRun = new HashSet<SessionStoreMigrationBatch>();
        var expectedRows    = new Dictionary<SessionStoreMigrationBatch, int>();
        var diagnostics     = new List<string>();
        foreach (var batch in marker.PendingBatches.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ImportBatchAsync(storeDirectory, database, batch, options, cancellationToken)
               .ConfigureAwait(false);
            diagnostics.AddRange(outcome.Diagnostics);

            // 提交后复查旧来源：命中窗口说明旧程序在导入期间写入了新内容，保持该批待切换并要求重试。
            if (batch is SessionStoreMigrationBatch.OrganizationState or SessionStoreMigrationBatch.ProjectCatalog)
                await EnsureSourceUnchangedAsync(storeDirectory, batch, options, outcome.Fingerprint,
                                                 cancellationToken).ConfigureAwait(false);

            marker = marker.WithBatchSwitched(batch, outcome.Fingerprint, DateTimeOffset.UtcNow);
            await WriteMarkerAsync(storeDirectory, marker, cancellationToken).ConfigureAwait(false);
            switchedThisRun.Add(batch);
            expectedRows[batch] = outcome.ImportedRows;
        }

        await VerifyMigratedDatabaseAsync(database, marker, expectedRows, cancellationToken).ConfigureAwait(false);

        // 成功标记里保留非致命中止诊断（例如未导入的损坏日志），供检查入口提示；可重试失败说明在此清空。
        var completed = marker with
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastError = diagnostics.Count == 0 ? null : JoinDiagnostics(diagnostics)
        };
        await WriteMarkerAsync(storeDirectory, completed, cancellationToken).ConfigureAwait(false);
        var counts = await ReadBatchRowCountsAsync(database, cancellationToken).ConfigureAwait(false);
        return new SessionStoreMigrationResult(false, completed.MigrationId, completed.StoreIdentity!,
                                               BuildReports(completed, switchedThisRun, counts),
                                               binding.UpgradedFrom);
    }

    /// <summary>
    ///     <para>
    ///         确保业务库存在且与标记绑定：首次迁移建库并写入库身份与迁移标识；续作时核对库身份与迁移标识，
    ///         已失去业务库的 store 明确要求备份恢复。
    ///     </para>
    ///     <para>
    ///         Ensures the business database exists and is bound to the marker: the first migration creates it and
    ///         records the identity and migration id, while a resume verifies both, and a store whose database was
    ///         lost is explicitly sent to backup restoration.
    ///     </para>
    /// </summary>
    private static async Task<SessionStoreDatabaseBinding> EnsureDatabaseBindingAsync(
        SessionStoreDatabase database, SessionStoreMigrationMarker marker, CancellationToken cancellationToken)
    {
        var  availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        // 版本更低说明业务库来自升级前的程序：schema 升级只走本入口，先在租约内升到位，后续身份核对与导入
        // 才按当前结构进行。生产读写在此之前一律拒绝，不会由某次普通读写顺带完成半套迁移。
        // A lower version means the database predates this build: the schema is upgraded here, under the lease, so
        // the identity check and the import below run on the current structure. Production access is refused until
        // then, so no ordinary read or write ever performs a partial migration.
        int? upgradedFrom = null;
        if (availability.Status == SessionStoreDatabaseStatus.NeedsUpgrade)
        {
            // 升级会提交 schema 写入，因此身份与迁移归属必须在它之前按旧库已有的 store_meta 核对：同名文件被
            // 另一份结构合法的旧库替换时，本入口必须以冲突拒绝且保持该文件原版本原结构，供旧程序继续使用。
            // The upgrade commits schema writes, so identity and migration ownership are verified first from the
            // older database's existing store_meta: when a same-named file was replaced by another structurally valid
            // older database, this entry must refuse with a conflict and leave that file's version and structure as
            // they are, so the older program can keep using it.
            await VerifyUpgradeOwnershipAsync(database, marker, cancellationToken).ConfigureAwait(false);
            upgradedFrom = await ReadSchemaVersionOrNullAsync(database, cancellationToken).ConfigureAwait(false);
            await UpgradeExistingDatabaseAsync(database, availability, cancellationToken).ConfigureAwait(false);
            availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }

        if (marker.StoreIdentity is null)
            switch (availability.Status)
            {
                case SessionStoreDatabaseStatus.Missing :
                case SessionStoreDatabaseStatus.NeedsSchema :
                    // 开始标记已写入但建库未完成：允许补建，旧来源尚未导入任何批次。
                    await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
                    return new SessionStoreDatabaseBinding(await AdoptFreshDatabaseAsync(database, marker,
                                                                    cancellationToken).ConfigureAwait(false),
                                                           upgradedFrom);
                case SessionStoreDatabaseStatus.Available :
                    return new SessionStoreDatabaseBinding(await AdoptFreshDatabaseAsync(database, marker,
                                                                    cancellationToken).ConfigureAwait(false),
                                                           upgradedFrom);
                default :
                    throw SessionStoreBusinessConnection.MapUnavailable(database.StoreDirectory, availability);
            }

        switch (availability.Status)
        {
            case SessionStoreDatabaseStatus.Missing :
                throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' is missing " +
                                                         $"although the migration marker '{marker.MigrationId}' "   +
                                                         $"recorded its identity '{marker.StoreIdentity}'; "        +
                                                         "restore the business database from a backup instead of importing legacy files again.",
                                                         SessionStoreFailureKind.Missing);
            case SessionStoreDatabaseStatus.Available :
                break;
            default :
                throw SessionStoreBusinessConnection.MapUnavailable(database.StoreDirectory, availability);
        }

        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (!string.Equals(identity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' carries identity " +
                                                     $"'{identity ?? "(none)"}' while the migration marker recorded " +
                                                     $"'{marker.StoreIdentity}'; the file was replaced or belongs to another store.",
                                                     SessionStoreFailureKind.Conflict);
        var migrationId = await SessionStoreDatabase.ReadMetaValueAsync(connection, MigrationIdMetaKey,
                                                                        cancellationToken).ConfigureAwait(false);
        if (migrationId is null)
            // 建库与写入迁移标识之间中断：身份已核对，补齐标识即可继续。
            await SessionStoreDatabase.WriteMetaValueAsync(connection, null, MigrationIdMetaKey, marker.MigrationId,
                                                           cancellationToken).ConfigureAwait(false);
        else if (!string.Equals(migrationId, marker.MigrationId, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' belongs to " +
                                                     $"migration '{migrationId}', not to the marker's migration '{marker.MigrationId}'; " +
                                                     "keep both files and resolve the conflict manually.",
                                                     SessionStoreFailureKind.Conflict);

        return new SessionStoreDatabaseBinding(marker, upgradedFrom);
    }

    /// <summary>
    ///     <para>
    ///         接管刚建立或已存在的业务库：读取库身份、写入本次迁移标识，并把身份记录到标记。
    ///     </para>
    ///     <para>
    ///         Adopts a freshly created or already existing database: reads the identity, writes this migration's id,
    ///         and records the identity in the marker.
    ///     </para>
    /// </summary>
    private static async Task<SessionStoreMigrationMarker> AdoptFreshDatabaseAsync(
        SessionStoreDatabase database, SessionStoreMigrationMarker marker, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (!IsStoreIdentifier(identity))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' has no valid " +
                                                     "store identity; it must not be filled by the migration.",
                                                     SessionStoreFailureKind.Damaged);
        var existingMigrationId = await SessionStoreDatabase.ReadMetaValueAsync(connection, MigrationIdMetaKey,
                                                                                    cancellationToken)
                                                            .ConfigureAwait(false);
        if (existingMigrationId is not null &&
            !string.Equals(existingMigrationId, marker.MigrationId, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' belongs to " +
                                                     $"migration '{existingMigrationId}', not to the marker's migration '{marker.MigrationId}'.",
                                                     SessionStoreFailureKind.Conflict);
        if (existingMigrationId is null)
            await SessionStoreDatabase.WriteMetaValueAsync(connection, null, MigrationIdMetaKey, marker.MigrationId,
                                                           cancellationToken).ConfigureAwait(false);

        var bound = marker.WithStoreIdentity(identity!, DateTimeOffset.UtcNow);
        await WriteMarkerAsync(database.StoreDirectory, bound, cancellationToken).ConfigureAwait(false);
        return bound;
    }

    /// <summary>
    ///     <para>导入一个批次并返回导入行数、来源指纹与诊断；空结果表示该批没有可导入内容。</para>
    ///     <para>Imports one batch and returns its imported row count, source fingerprint, and diagnostics.</para>
    /// </summary>
    private static Task<BatchImportResult> ImportBatchAsync(string storeDirectory, SessionStoreDatabase database,
                                                            SessionStoreMigrationBatch batch,
                                                            SessionStoreMigrationOptions options,
                                                            CancellationToken cancellationToken) => batch switch
    {
        SessionStoreMigrationBatch.OrganizationState => ImportOrganizationStateAsync(storeDirectory, database, options,
                 cancellationToken),
        SessionStoreMigrationBatch.ProjectCatalog => ImportProjectCatalogAsync(storeDirectory, database,
                                                                               cancellationToken),
        SessionStoreMigrationBatch.SessionPlacement => ImportSessionPlacementAsync(storeDirectory, database,
                 cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(batch), batch, "Unknown migration batch.")
    };

    /// <summary>
    ///     <para>
    ///         导入组织状态：置顶文件、归档文件与可选的桌面侧栏顺序／折叠状态，在单个事务内整体替换六张
    ///         组织表并核对行数。
    ///     </para>
    ///     <para>
    ///         Imports the organization state: pin file, archive file, and the optional desktop sidebar order and
    ///         collapsed state, replacing the six organization tables inside one transaction and verifying row counts.
    ///     </para>
    /// </summary>
    private static async Task<BatchImportResult> ImportOrganizationStateAsync(
        string            storeDirectory, SessionStoreDatabase database, SessionStoreMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var snapshot = await ReadOrganizationSnapshotAsync(storeDirectory, options.SidebarPreferencesPath,
                                                           cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                                                                         .ConfigureAwait(false);
        await SessionStoreOrganizationStore.ReplaceAllAsync(connection, transaction, snapshot.State,
                                                            cancellationToken).ConfigureAwait(false);
        var actual = await CountOrganizationRowsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (actual != snapshot.TotalRows)
            throw new SessionStoreMigrationException($"Imported organization state holds {actual} rows "             +
                                                     $"while the validated snapshot expected {snapshot.TotalRows}; " +
                                                     "the business database did not accept the import.",
                                                     SessionStoreFailureKind.Damaged);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatchImportResult(snapshot.TotalRows, snapshot.Fingerprint, snapshot.Diagnostics);
    }

    /// <summary>
    ///     <para>
    ///         导入项目目录册：读取 <c>projects.json</c>（缺失即空目录册），在单个事务内整体替换项目表并核对行数。
    ///     </para>
    ///     <para>
    ///         Imports the project catalog: reads <c>projects.json</c> (missing means an empty catalog), replaces the
    ///         project table inside one transaction, and verifies the row count.
    ///     </para>
    /// </summary>
    private static async Task<BatchImportResult> ImportProjectCatalogAsync(
        string storeDirectory, SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        var snapshot = await ReadProjectSnapshotAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                                                                         .ConfigureAwait(false);
        await SessionStoreProjectCatalogStore.ReplaceAllAsync(connection, transaction, snapshot.Projects,
                                                              cancellationToken).ConfigureAwait(false);
        var actual = await CountRowsAsync(connection, transaction, "project", cancellationToken)
           .ConfigureAwait(false);
        if (actual != snapshot.Projects.Count)
            throw new SessionStoreMigrationException($"Imported project catalog holds {actual} rows " +
                                                     $"while the validated snapshot expected {snapshot.Projects.Count}; " +
                                                     "the business database did not accept the import.",
                                                     SessionStoreFailureKind.Damaged);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatchImportResult(snapshot.Projects.Count, snapshot.Fingerprint, []);
    }

    /// <summary>
    ///     <para>
    ///         导入会话归属：逐个读取会话日志，按三态派生归属行，跳过损坏日志与指向目录册外项目的悬空引用，
    ///         在单个事务内整体替换归属表并核对行数。
    ///     </para>
    ///     <para>
    ///         Imports session placement: reads every session log, derives a placement row from the three-state
    ///         assignment, skips damaged logs and dangling project references, then replaces the placement table inside
    ///         one transaction and verifies the row count.
    ///     </para>
    /// </summary>
    private static async Task<BatchImportResult> ImportSessionPlacementAsync(
        string storeDirectory, SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        var snapshot = await ReadPlacementSnapshotAsync(storeDirectory, database, cancellationToken)
           .ConfigureAwait(false);
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                                                                         .ConfigureAwait(false);
        await SessionStorePlacementStore.ReplaceAllAsync(connection, transaction, snapshot.Rows,
                                                         cancellationToken).ConfigureAwait(false);
        var actual = await CountRowsAsync(connection, transaction, "session_placement", cancellationToken)
           .ConfigureAwait(false);
        if (actual != snapshot.Rows.Count)
            throw new SessionStoreMigrationException($"Imported session placement holds {actual} rows "               +
                                                     $"while the validated snapshot expected {snapshot.Rows.Count}; " +
                                                     "the business database did not accept the import.",
                                                     SessionStoreFailureKind.Damaged);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new BatchImportResult(snapshot.Rows.Count, snapshot.Fingerprint, snapshot.Diagnostics);
    }

    /// <summary>
    ///     <para>
    ///         合并组织状态来源并计算指纹：存档与置顶来自 store 内文件，手动顺序、折叠状态与旧置顶来自可选的
    ///         桌面偏好文件；旧置顶追加到置顶文件顺序之后且不重复。
    ///     </para>
    ///     <para>
    ///         Merges the organization-state sources and computes the fingerprint: archive and pins come from files in
    ///         the store, manual order, collapsed state, and legacy pins come from the optional desktop preferences; the
    ///         legacy pins are appended after the pin-file order without duplicates.
    ///     </para>
    /// </summary>
    private static async Task<OrganizationSnapshot> ReadOrganizationSnapshotAsync(
        string storeDirectory, string? sidebarPreferencesPath, CancellationToken cancellationToken)
    {
        SessionStorePins                pins;
        SessionStoreArchives            archives;
        SessionStoreSidebarPreferences? sidebar = null;
        try
        {
            pins = await SessionStorePinStore.LoadLegacyFileAsync(storeDirectory, cancellationToken)
                                             .ConfigureAwait(false);
            archives = await SessionStoreArchiveStore.LoadLegacyFileAsync(storeDirectory, cancellationToken)
                                                     .ConfigureAwait(false);
            if (sidebarPreferencesPath is not null)
                sidebar = await SessionStoreSidebarPreferencesReader.ReadAsync(sidebarPreferencesPath, storeDirectory,
                                                                               cancellationToken).ConfigureAwait(false);
        }
        catch (SessionStorePinException ex)
        {
            throw new SessionStoreMigrationException("Legacy pin state " +
                                                     $"'{SessionStorePinStore.FilePath(storeDirectory)}' cannot be imported: {ex.Message}",
                                                     SessionStoreFailureKind.Damaged, ex);
        }
        catch (SessionStoreArchiveException ex)
        {
            throw new SessionStoreMigrationException("Legacy archive state " +
                                                     $"'{SessionStoreArchiveStore.FilePath(storeDirectory)}' cannot be imported: {ex.Message}",
                                                     SessionStoreFailureKind.Damaged, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException or SessionStoreException)
        {
            throw new SessionStoreMigrationException($"Organization-state sources in '{storeDirectory}' " +
                                                     $"cannot be read: {ex.Message}",
                                                     SessionStoreErrorClassifier.Classify(ex), ex);
        }

        var archived = NormalizeIdSet(ValidateIds(archives.ArchivedSessionIds, "archived session id",
                                                  SessionStoreArchiveStore.FileName),
                                      StringComparer.Ordinal);
        var pinnedProjects =
            MergeOrderedIds(ValidateIds(pins.PinnedProjectIds, "pinned project id", SessionStorePinStore.FileName),
                            sidebar is null
                                ? []
                                : ValidateIds(sidebar.LegacyPinnedProjectOrder,
                                              "legacy pinned project id",
                                              SessionStoreSidebarPreferencesReader.FileName));
        var pinnedSessions =
            MergeOrderedIds(ValidateIds(pins.PinnedSessionIds, "pinned session id", SessionStorePinStore.FileName),
                            sidebar is null
                                ? []
                                : ValidateIds(sidebar.LegacyPinnedSessionIds,
                                              "legacy pinned session id",
                                              SessionStoreSidebarPreferencesReader.FileName));
        var manualProjects = sidebar is null
            ? []
            : ValidateIds(sidebar.ManualProjectOrder, "manual project order id",
                          SessionStoreSidebarPreferencesReader.FileName);
        var manualSessions = sidebar is null
            ? []
            : ValidateIds(sidebar.ManualSessionOrder, "manual session order id",
                          SessionStoreSidebarPreferencesReader.FileName);
        var collapsedKeys = sidebar is null
            ? []
            : NormalizeGroupKeys(sidebar.CollapsedGroupKeys,
                                 SessionStoreSidebarPreferencesReader.FileName);

        var state = new SessionStoreOrganizationState(archived, pinnedProjects, pinnedSessions, manualProjects,
                                                      manualSessions, collapsedKeys);
        var totalRows = state.ArchivedSessionIds.Count + state.PinnedProjectIds.Count   +
                        state.PinnedSessionIds.Count   + state.ManualProjectOrder.Count +
                        state.ManualSessionOrder.Count + state.CollapsedGroupKeys.Count;
        var fingerprint = ComputeOrganizationFingerprint(state, sidebar is not null);
        return new OrganizationSnapshot(state, totalRows, fingerprint, []);
    }

    /// <summary>
    ///     <para>读取项目目录册并计算指纹；文件缺失按空目录册处理，损坏或超预算明确失败。</para>
    ///     <para>
    ///         Reads the project catalog and computes its fingerprint; a missing file is an empty catalog, while damage or
    ///         an over-budget file fails explicitly.
    ///     </para>
    /// </summary>
    private static async Task<ProjectSnapshot> ReadProjectSnapshotAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        var            catalogPath = ProjectStore.FilePath(storeDirectory);
        ProjectCatalog catalog;
        try
        {
            catalog = await ProjectStore.LoadLegacyFileAsync(catalogPath, cancellationToken).ConfigureAwait(false);
        }
        catch (ProjectStoreException ex)
        {
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' " +
                                                     $"cannot be imported: {ex.Message}",
                                                     SessionStoreFailureKind.Damaged, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException or SessionStoreException)
        {
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' " +
                                                     $"cannot be read: {ex.Message}",
                                                     SessionStoreErrorClassifier.Classify(ex), ex);
        }

        // 目录册读取已按旧文件契约校验 ID 形态、名称、路径与修订号；这里再核对数据库 CHECK 可表达的长度边界，
        // 避免把无法写入业务表的旧数据静默丢为空项目。
        foreach (var project in catalog.Projects)
            ValidateProjectForImport(project, catalogPath);

        return new ProjectSnapshot(catalog.Projects, ComputeProjectFingerprint(catalog));
    }

    /// <summary>
    ///     <para>
    ///         逐个读取会话日志并派生归属行：已归属产生带项目 ID 的行，明确无项目产生空项目行，尚未初始化不产生
    ///         行；损坏日志、非法 ID 与目录册外的悬空引用只记入诊断。
    ///     </para>
    ///     <para>
    ///         Reads every session log and derives placement rows: an assignment produces a row with the project id, an
    ///         explicit no-project produces a row with a null project, and a pending assignment produces no row;
    ///         damaged logs, invalid ids, and dangling project references are diagnostics only.
    ///     </para>
    /// </summary>
    private static async Task<PlacementSnapshot> ReadPlacementSnapshotAsync(
        string storeDirectory, SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        var projectIds  = await ReadProjectIdsAsync(database, cancellationToken).ConfigureAwait(false);
        var importedUtc = DateTimeOffset.UtcNow;
        var diagnostics = new List<string>();
        var files       = EnumerateSessionLogFiles(storeDirectory, diagnostics);
        var fingerprint = ComputePlacementFingerprint(files);
        var rows        = new List<SessionStorePlacementRow>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName  = Path.GetFileName(file);
            var sessionId = fileName[..^SessionStoreWriter.LogFileSuffix.Length];
            if (!IsStoreIdentifier(sessionId))
            {
                AddDiagnostic(diagnostics,
                              $"Session log '{fileName}' does not carry a 32-character " +
                              "lowercase hexadecimal session id and was not imported.");
                continue;
            }

            PersistedSession? session;
            try
            {
                // 导入器读取的是仍以旧文件为权威的来源：只取日志自身的严格投影，不解析迁移权威，
                // 否则"导入中"的阻塞状态会反过来拒绝导入器自己。
                session = await SessionStoreReader.ReadLogProjectionAsync(storeDirectory, sessionId, cancellationToken)
                                                  .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (SessionStoreException ex)
            {
                AddDiagnostic(diagnostics,
                              $"Session '{sessionId}' was not imported because its log is damaged: {ex.Message}");
                continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                          or PathTooLongException or JsonException or InvalidDataException
                                          or FormatException)
            {
                AddDiagnostic(diagnostics,
                              $"Session '{sessionId}' was not imported because its log could not be read: {ex.Message}");
                continue;
            }

            if (session is null)
            {
                AddDiagnostic(diagnostics,
                              $"Session '{sessionId}' disappeared while its log was being read and was not imported.");
                continue;
            }

            switch (session.ProjectAssignmentState)
            {
                case SessionProjectAssignmentState.PendingInitialization :
                    // 无行即尚未初始化；迁移不替旧日志猜测项目。
                    break;
                case SessionProjectAssignmentState.NoProject :
                    rows.Add(new SessionStorePlacementRow(sessionId, null, importedUtc));
                    break;
                case SessionProjectAssignmentState.Project when session.ProjectId is { } projectId :
                    if (!IsStoreIdentifier(projectId))
                    {
                        AddDiagnostic(diagnostics,
                                      $"Session '{sessionId}' carries project id '{projectId}' " +
                                      "that cannot be represented and was not imported.");
                        break;
                    }

                    if (!projectIds.Contains(projectId))
                    {
                        // 旧归属指向已不存在的项目：保留为待整理，绝不猜测或替用户选择新项目。
                        AddDiagnostic(diagnostics,
                                      $"Session '{sessionId}' references project '{projectId}' " +
                                      "that is not in the imported project catalog; the dangling reference was not imported.");
                        break;
                    }

                    rows.Add(new SessionStorePlacementRow(sessionId, projectId, importedUtc));
                    break;
                case SessionProjectAssignmentState.Project :
                    AddDiagnostic(diagnostics,
                                  $"Session '{sessionId}' claims a project assignment without a project id and was not imported.");
                    break;
                default :
                    AddDiagnostic(diagnostics,
                                  $"Session '{sessionId}' carries an unknown project-assignment state and was not imported.");
                    break;
            }
        }

        rows.Sort((left, right) => string.CompareOrdinal(left.SessionId, right.SessionId));
        return new PlacementSnapshot(rows, fingerprint, diagnostics);
    }

    /// <summary>
    ///     <para>读取业务库中的项目 ID 集合，用于校验归属引用；目录册批次总是先于归属批次。</para>
    ///     <para>
    ///         Reads the project ids in the business database to validate placement references; the catalog batch always
    ///         precedes placement.
    ///     </para>
    /// </summary>
    private static async Task<HashSet<string>> ReadProjectIdsAsync(
        SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM project;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var             ids    = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    ///     <para>
    ///         列出 store 根目录下的会话日志；枚举失败按 IO 失败报告。结果按文件名序数排序，保证指纹稳定。
    ///     </para>
    ///     <para>
    ///         Lists session logs in the store root; an enumeration failure reports as an IO failure. The result is
    ///         ordinal-sorted by file name so fingerprints stay stable.
    ///     </para>
    /// </summary>
    private static List<string> EnumerateSessionLogFiles(string storeDirectory, List<string> diagnostics)
    {
        try
        {
            return Directory.EnumerateFiles(storeDirectory, "*" + SessionStoreWriter.LogFileSuffix)
                            .Order(StringComparer.Ordinal)
                            .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or PathTooLongException)
        {
            throw new SessionStoreMigrationException($"Session logs in '{storeDirectory}' " +
                                                     $"cannot be enumerated: {ex.Message}",
                                                     SessionStoreErrorClassifier.Classify(ex), ex);
        }
    }

    /// <summary>
    ///     <para>
    ///         重读指定批次的来源并比较指纹；不一致说明旧来源在导入期间被写入，本次运行以冲突失败且不标记该批，
    ///         重试会重新导入新来源。读取失败同样按冲突报告。
    ///     </para>
    ///     <para>
    ///         Re-reads the batch's source and compares fingerprints; a mismatch means the legacy source was written
    ///         during the import, so the run fails with a conflict without switching the batch and a retry imports the
    ///         new source. A failed re-read also reports as a conflict.
    ///     </para>
    /// </summary>
    private static async Task EnsureSourceUnchangedAsync(string storeDirectory, SessionStoreMigrationBatch batch,
                                                         SessionStoreMigrationOptions options,
                                                         string expectedFingerprint,
                                                         CancellationToken cancellationToken)
    {
        string currentFingerprint;
        try
        {
            currentFingerprint = batch switch
            {
                SessionStoreMigrationBatch.OrganizationState => (await ReadOrganizationSnapshotAsync(
                         storeDirectory, options.SidebarPreferencesPath, cancellationToken).ConfigureAwait(false))
                   .Fingerprint,
                SessionStoreMigrationBatch.ProjectCatalog => (await ReadProjectSnapshotAsync(storeDirectory,
                         cancellationToken).ConfigureAwait(false)).Fingerprint,
                _ => throw new ArgumentOutOfRangeException(nameof(batch), batch, "Unknown migration batch.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SessionStoreMigrationException($"Legacy sources for batch '{batch}' "                    +
                                                     $"could not be re-read after the import ({ex.Message}); " +
                                                     "the batch stays pending, so retry the migration.",
                                                     SessionStoreFailureKind.Conflict, ex);
        }

        if (string.Equals(currentFingerprint, expectedFingerprint, StringComparison.Ordinal)) return;

        var conflict = $"Legacy sources for batch '{batch}' changed while they were being imported; " +
                       "the batch stays pending and the database import will be redone on the next run.";
        // 数据库已提交但批次未切换：记录可重试说明后退出，绝不把旧快照标记成已切换的来源。
        var read = await ReadMarkerAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        if (read is { Status: SessionStoreMarkerReadStatus.Present, Marker: { } marker })
            await WriteMarkerAsync(storeDirectory, marker.WithFailure(conflict, DateTimeOffset.UtcNow),
                                   cancellationToken).ConfigureAwait(false);
        throw new SessionStoreMigrationException(conflict, SessionStoreFailureKind.Conflict);
    }

    /// <summary>
    ///     <para>
    ///         全部批次切换后的最终校验：业务库可用、库身份与迁移标识一致、完整性检查与外键检查通过，并复核
    ///         本次切换批次的行数。
    ///     </para>
    ///     <para>
    ///         The final validation after every batch switched: the database is available, its identity and migration
    ///         id match, the integrity and foreign-key checks pass, and row counts of batches switched this run are
    ///         verified again.
    ///     </para>
    /// </summary>
    private static async Task VerifyMigratedDatabaseAsync(SessionStoreDatabase        database,
                                                          SessionStoreMigrationMarker marker,
                                                          IReadOnlyDictionary<SessionStoreMigrationBatch, int>
                                                              expectedRows, CancellationToken cancellationToken)
    {
        var availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Status != SessionStoreDatabaseStatus.Available)
            throw SessionStoreBusinessConnection.MapUnavailable(database.StoreDirectory, availability);

        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (!string.Equals(identity, marker.StoreIdentity, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' identity " +
                                                     $"'{identity ?? "(none)"}' contradicts the migration marker " +
                                                     $"'{marker.StoreIdentity ?? "(none)"}' after the import.",
                                                     SessionStoreFailureKind.Conflict);
        var migrationId = await SessionStoreDatabase.ReadMetaValueAsync(connection, MigrationIdMetaKey,
                                                                        cancellationToken).ConfigureAwait(false);
        if (!string.Equals(migrationId, marker.MigrationId, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' migration id " +
                                                     $"'{migrationId ?? "(none)"}' does not match the marker's migration '{marker.MigrationId}'.",
                                                     SessionStoreFailureKind.Conflict);
        var integrity = await SessionStoreDatabase.RunIntegrityCheckAsync(connection, cancellationToken)
                                                  .ConfigureAwait(false);
        if (integrity is not null)
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' failed its " +
                                                     $"integrity check after the import: {integrity}.",
                                                     SessionStoreFailureKind.Damaged);
        var foreignKey = await SessionStoreDatabase.RunForeignKeyCheckAsync(connection, cancellationToken)
                                                   .ConfigureAwait(false);
        if (foreignKey is not null)
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' failed its " +
                                                     $"foreign-key check after the import: {foreignKey}.",
                                                     SessionStoreFailureKind.Damaged);

        var counts = await ReadBatchRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var (batch, expected) in expectedRows)
            if (counts[batch] != expected)
                throw new SessionStoreMigrationException($"Imported batch '{batch}' holds {counts[batch]} rows " +
                                                         $"while its validated snapshot expected {expected}; "   +
                                                         "the database content changed during the migration.",
                                                         SessionStoreFailureKind.Conflict);
    }

    /// <summary>
    ///     <para>
    ///         在迁移租约内把版本更低的业务库升到当前 schema；升级失败按对应失败类别报告，库保持原版本与原结构。
    ///     </para>
    ///     <para>
    ///         Upgrades an older business database to the current schema while the migration lease is held; a failure
    ///         reports with the matching category and leaves the database at its original version and structure.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         只接受 <see cref="SessionStoreDatabaseStatus.NeedsUpgrade" />：缺失、无 schema、版本更高与损坏仍按各自
    ///         类别拒绝，绝不由升级路径重建或降级业务库。
    ///     </para>
    ///     <para>
    ///         Only <see cref="SessionStoreDatabaseStatus.NeedsUpgrade" /> is accepted; a missing, schema-less, newer,
    ///         or damaged database is still refused with its own category, and the upgrade path never recreates or
    ///         downgrades a business database.
    ///     </para>
    /// </remarks>
    private static async Task UpgradeExistingDatabaseAsync(
        SessionStoreDatabase database, SessionStoreDatabaseAvailability availability,
        CancellationToken    cancellationToken)
    {
        if (availability.Status is not SessionStoreDatabaseStatus.NeedsUpgrade)
            throw SessionStoreBusinessConnection.MapUnavailable(database.StoreDirectory, availability);
        try
        {
            await database.UpgradeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SessionStoreDatabaseException exception)
        {
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' " +
                                                     "could not be upgraded to schema version "      +
                                                     $"{SessionStoreDatabase.CurrentSchemaVersion}: {exception.Message}",
                                                     SessionStoreErrorClassifier.Classify(exception), exception);
        }
    }

    /// <summary>
    ///     <para>
    ///         升级前按旧库自带的元数据核对身份与迁移归属：核对通过才允许提交 schema 升级，核对不通过时该文件
    ///         保持原版本与原结构，异库仍可被创建它的旧程序继续使用。
    ///     </para>
    ///     <para>
    ///         Verifies identity and migration ownership from the older database's own metadata before any upgrade
    ///         commits; a failed check leaves the file at its original version and structure, so a foreign database
    ///         stays usable by the older program that created it.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         读取只用锁等待配置，不切换 journal 模式，因此该步骤本身不写业务文件。元数据不可读时按对应失败
    ///         类别拒绝：无法确认归属就不得升级。
    ///     </para>
    ///     <para>
    ///         The read only sets the lock wait and never switches the journal mode, so this step itself writes
    ///         nothing. Unreadable metadata is refused with the matching failure category: without a confirmed
    ///         ownership the database must not be upgraded.
    ///     </para>
    /// </remarks>
    private static async Task VerifyUpgradeOwnershipAsync(
        SessionStoreDatabase database, SessionStoreMigrationMarker marker, CancellationToken cancellationToken)
    {
        string? identity;
        string? migrationId;
        try
        {
            await using var connection = await database.OpenAsync(SqliteOpenMode.ReadWrite, false, cancellationToken)
                                                       .ConfigureAwait(false);
            identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
            migrationId = await SessionStoreDatabase.ReadMetaValueAsync(connection, MigrationIdMetaKey,
                                                                        cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' "      +
                                                     "could not be identified before the schema upgrade " +
                                                     $"({exception.Message}); it was left unchanged.",
                                                     SessionStoreErrorClassifier.Classify(exception), exception);
        }

        // 标记尚未记录身份（首次迁移在建库前中断）时没有可比较的身份，但库里若已写明属于另一次迁移，同样拒绝。
        if (marker.StoreIdentity is { } expectedIdentity &&
            !string.Equals(identity, expectedIdentity, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' carries identity " +
                                                     $"'{identity ?? "(none)"}' while the migration marker recorded '{expectedIdentity}'; " +
                                                     "the schema upgrade was refused and the file keeps its version and structure.",
                                                     SessionStoreFailureKind.Conflict);
        if (migrationId is { } recordedMigrationId &&
            !string.Equals(recordedMigrationId, marker.MigrationId, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' belongs to " +
                                                     $"migration '{recordedMigrationId}', not to the marker's migration " +
                                                     $"'{marker.MigrationId}'; the schema upgrade was refused and the file " +
                                                     "keeps its version and structure.",
                                                     SessionStoreFailureKind.Conflict);
    }

    /// <summary>
    ///     <para>
    ///         已完成的迁移再次运行时只核对业务库可用性与库身份并返回报告；任何不符都以对应失败类别拒绝，
    ///         绝不重新导入旧来源。
    ///     </para>
    ///     <para>
    ///         A completed migration run again only verifies the business-database availability and identity before
    ///         returning its report; any mismatch is refused with the matching failure category and the legacy sources
    ///         are never imported again.
    ///     </para>
    /// </summary>
    private static async Task<SessionStoreMigrationResult> BuildAlreadyCompletedResultAsync(
        string            storeDirectory, SessionStoreDatabase database, SessionStoreMigrationMarker marker,
        CancellationToken cancellationToken)
    {
        var storeIdentity = marker.StoreIdentity;
        if (storeIdentity is null)
            throw new SessionStoreMigrationException($"Migration marker '{MarkerPathFor(storeDirectory)}' marks " +
                                                     "every batch switched but records no business-database identity; " +
                                                     "restore the marker or the database manually.",
                                                     SessionStoreFailureKind.Conflict);
        var  availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        // 旧版本业务库在已完成迁移的 store 里同样必须由本入口升级：读取与对账都按当前结构工作，程序升级后
        // 第一次跑迁移就是既定的升级时机，而不是要求用户手工换 schema 版本号。
        // A completed store may still carry an older schema, and this entry is the only upgrade point: reads and
        // reconciliation work on the current structure, so the first migration run after a program upgrade is where
        // the schema moves, without asking anyone to edit the version row by hand.
        int? upgradedFrom = null;
        if (availability.Status == SessionStoreDatabaseStatus.NeedsUpgrade)
        {
            // 同上：身份与迁移归属必须先于升级提交核对，异库不得被本入口改写 schema。
            // As above: identity and migration ownership are checked before the upgrade commits, so a foreign
            // database is never rewritten into the current schema by this entry.
            await VerifyUpgradeOwnershipAsync(database, marker, cancellationToken).ConfigureAwait(false);
            upgradedFrom = await ReadSchemaVersionOrNullAsync(database, cancellationToken).ConfigureAwait(false);
            await UpgradeExistingDatabaseAsync(database, availability, cancellationToken).ConfigureAwait(false);
            availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        }

        if (availability.Status != SessionStoreDatabaseStatus.Available)
            throw availability.Status == SessionStoreDatabaseStatus.Missing
                ? new SessionStoreMigrationException($"Migration '{marker.MigrationId}' completed "                  +
                                                     $"but business database '{database.DatabasePath}' is missing; " +
                                                     "restore it from a business backup instead of re-importing legacy files.",
                                                     SessionStoreFailureKind.Missing)
                : SessionStoreBusinessConnection.MapUnavailable(storeDirectory, availability);

        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                 .ConfigureAwait(false);
        if (!string.Equals(identity, storeIdentity, StringComparison.Ordinal))
            throw new SessionStoreMigrationException($"Business database '{database.DatabasePath}' "               +
                                                     $"carries identity '{identity ?? "(none)"}' while migration " +
                                                     $"'{marker.MigrationId}' recorded '{storeIdentity}'; "        +
                                                     "the file was replaced and must be restored from a backup or confirmed explicitly.",
                                                     SessionStoreFailureKind.Conflict);
        var counts = await ReadBatchRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
        return new SessionStoreMigrationResult(true, marker.MigrationId, storeIdentity,
                                               BuildReports(marker, new HashSet<SessionStoreMigrationBatch>(), counts),
                                               upgradedFrom);
    }

    /// <summary>
    ///     <para>读取业务库当前 schema 版本；文件或版本行不可读时返回 null，仅供升级诊断使用。</para>
    ///     <para>Reads the current schema version for upgrade diagnostics; null when the file or version row is unreadable.</para>
    /// </summary>
    private static async Task<int?> ReadSchemaVersionOrNullAsync(
        SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                       .ConfigureAwait(false);
            return await SessionStoreDatabase.ReadSchemaVersionAsync(connection, cancellationToken)
                                             .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SessionStoreDatabaseException or SessionStoreException
                                           or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>按迁移顺序构造批次报告；行数取业务库当前计数，来源指纹取标记记录。</para>
    ///     <para>
    ///         Builds the batch reports in migration order; row counts come from the database and fingerprints from the
    ///         marker.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<SessionStoreMigrationBatchReport> BuildReports(
        SessionStoreMigrationMarker marker, IReadOnlySet<SessionStoreMigrationBatch> switchedThisRun,
        IReadOnlyDictionary<SessionStoreMigrationBatch, int> counts) =>
        SessionStoreMigrationMarker.BatchOrder.Select(batch => new SessionStoreMigrationBatchReport(
                                                           batch, switchedThisRun.Contains(batch), counts[batch],
                                                           marker.SourceFingerprints.GetValueOrDefault(batch) ??
                                                           string.Empty))
                                   .ToArray();

    /// <summary>
    ///     <para>读取各批次的当前行数：组织状态为六张表之和，项目目录册取项目表，会话归属取归属表。</para>
    ///     <para>
    ///         Reads the current row count of every batch: the six organization tables combined, the project table, and the
    ///         placement table.
    ///     </para>
    /// </summary>
    private static async Task<Dictionary<SessionStoreMigrationBatch, int>> ReadBatchRowCountsAsync(
        SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        return await ReadBatchRowCountsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在给定连接上读取各批次的当前行数。</para>
    ///     <para>Reads the current row count of every batch on the given connection.</para>
    /// </summary>
    private static async Task<Dictionary<SessionStoreMigrationBatch, int>> ReadBatchRowCountsAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        var organization = 0;
        foreach (var table in OrganizationTables)
            organization += await CountRowsAsync(connection, null, table, cancellationToken).ConfigureAwait(false);
        var projects = await CountRowsAsync(connection, null, "project", cancellationToken).ConfigureAwait(false);
        var placements = await CountRowsAsync(connection, null, "session_placement", cancellationToken)
           .ConfigureAwait(false);
        return new Dictionary<SessionStoreMigrationBatch, int>
        {
            [SessionStoreMigrationBatch.OrganizationState] = organization,
            [SessionStoreMigrationBatch.ProjectCatalog]    = projects,
            [SessionStoreMigrationBatch.SessionPlacement]  = placements
        };
    }

    /// <summary>
    ///     <para>在给定连接、可选事务上统计一张表的行数。</para>
    ///     <para>Counts the rows of one table on the given connection and optional transaction.</para>
    /// </summary>
    private static async Task<int> CountRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string table, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     <para>统计组织状态六张表的行数之和。</para>
    ///     <para>Counts the combined rows of the six organization-state tables.</para>
    /// </summary>
    private static async Task<int> CountOrganizationRowsAsync(
        SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var table in OrganizationTables)
            total += await CountRowsAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
        return total;
    }

    /// <summary>
    ///     <para>读取业务库身份；打不开库时按底层失败分类报告。</para>
    ///     <para>Reads the business-database identity; an unopenable database reports through the underlying failure category.</para>
    /// </summary>
    private static async Task<string?> ReadDatabaseIdentityAsync(
        SessionStoreDatabase database, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenExistingForInternalUseAsync(cancellationToken)
                                                   .ConfigureAwait(false);
        return await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         收集旧来源在切换后被改写的警告：组织状态与项目目录册重算来源指纹，会话归属只比较文件名、长度与
    ///         写入时间摘要。任何比较失败都降级为警告，不改变检查状态。
    ///     </para>
    ///     <para>
    ///         Collects warnings for legacy sources rewritten after switching: organization state and the project
    ///         catalog recompute their source fingerprints, while placement compares only the file-name, length, and
    ///         write-time digest. A failed comparison degrades to a warning and never changes the inspected status.
    ///     </para>
    /// </summary>
    private static async Task CollectLegacySourceWarningsAsync(string storeDirectory, string? sidebarPreferencesPath,
                                                               SessionStoreMigrationMarker marker,
                                                               List<string> warnings,
                                                               CancellationToken cancellationToken)
    {
        if (marker.Batches.GetValueOrDefault(SessionStoreMigrationBatch.OrganizationState) ==
            SessionStoreBatchState.Switched)
            try
            {
                var snapshot = await ReadOrganizationSnapshotAsync(storeDirectory, sidebarPreferencesPath,
                                                                   cancellationToken).ConfigureAwait(false);
                if (!string.Equals(snapshot.Fingerprint, marker.SourceFingerprints.GetValueOrDefault(
                                        SessionStoreMigrationBatch.OrganizationState), StringComparison.Ordinal))
                    warnings.Add("Legacy organization-state sources no longer reproduce the fingerprint recorded at switch time; " +
                                 "pinned-items.json or archived-sessions.json was rewritten, or the desktop sidebar preferences " +
                                 "imported then are not available to this inspection. The business database remains authoritative.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Legacy organization-state sources could not be re-checked: {ex.Message}");
            }

        if (marker.Batches.GetValueOrDefault(SessionStoreMigrationBatch.ProjectCatalog) ==
            SessionStoreBatchState.Switched)
            try
            {
                var snapshot = await ReadProjectSnapshotAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(snapshot.Fingerprint, marker.SourceFingerprints.GetValueOrDefault(
                                        SessionStoreMigrationBatch.ProjectCatalog), StringComparison.Ordinal))
                    warnings.Add("Legacy project catalog no longer reproduces the fingerprint recorded at switch time; " +
                                 "projects.json was rewritten after the batch switched. The business database remains authoritative.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Legacy project catalog could not be re-checked: {ex.Message}");
            }

        if (marker.Batches.GetValueOrDefault(SessionStoreMigrationBatch.SessionPlacement) ==
            SessionStoreBatchState.Switched)
            try
            {
                var diagnostics = new List<string>();
                var files       = EnumerateSessionLogFiles(storeDirectory, diagnostics);
                if (!string.Equals(ComputePlacementFingerprint(files), marker.SourceFingerprints.GetValueOrDefault(
                                        SessionStoreMigrationBatch.SessionPlacement), StringComparison.Ordinal))
                    warnings.Add("Legacy session logs no longer reproduce the fingerprint recorded at switch time; at least one log was " +
                                 "written after the placement batch switched. The business database remains authoritative.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Legacy session logs could not be re-checked: {ex.Message}");
            }
    }

    /// <summary>
    ///     <para>校验旧来源中的 ID 列表并保持顺序；任一 ID 不是 32 位小写十六进制时明确拒绝并指出来源文件。</para>
    ///     <para>
    ///         Validates an id list from a legacy source while preserving order; any id that is not 32 lowercase hexadecimal
    ///         characters is refused with the source file named.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ValidateIds(
        IReadOnlyList<string> values, string description, string sourceFileName)
    {
        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (!IsStoreIdentifier(value))
                throw new SessionStoreMigrationException($"Legacy source '{sourceFileName}' " +
                                                         $"contains {description} '{value}' that cannot be represented " +
                                                         "in the business database; keep the file and fix the entry before migrating.",
                                                         SessionStoreFailureKind.Damaged);
            result.Add(value);
        }

        return result;
    }

    /// <summary>
    ///     <para>校验折叠分组键：非空白、不超过 256 字符且不重复，按序数排序返回。</para>
    ///     <para>
    ///         Validates collapsed group keys as non-blank, at most 256 characters, and unique, returning them
    ///         ordinal-sorted.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeGroupKeys(
        IReadOnlyList<string> values, string sourceFileName)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > SessionStorePinStore.MaxIdentifierLength)
                throw new SessionStoreMigrationException($"Legacy source '{sourceFileName}' contains " +
                                                         $"collapsed group key '{key}' "               +
                                                         "that cannot be represented in the business database.",
                                                         SessionStoreFailureKind.Damaged);
            keys.Add(key);
        }

        return keys.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    ///     <para>把有序 ID 列表规范为去重后的集合，按序数排序；迁移的存档集合由数据库读取顺序决定。</para>
    ///     <para>
    ///         Normalizes an ordered id list into a duplicate-free set sorted ordinally; the archive set order is decided by
    ///         database reads.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeIdSet(IReadOnlyList<string> values, StringComparer comparer) =>
        values.Distinct(comparer).Order(comparer).ToArray();

    /// <summary>
    ///     <para>合并两段有序 ID：保留第一段的顺序，追加第二段中尚未出现的条目。</para>
    ///     <para>
    ///         Merges two ordered id sequences: the first order is preserved and entries not yet present are appended from
    ///         the second.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> MergeOrderedIds(
        IReadOnlyList<string> primary, IReadOnlyList<string> secondary)
    {
        var seen = new HashSet<string>(primary, StringComparer.Ordinal);
        List<string> result =
        [
            with(primary.Count + secondary.Count),
            .. primary
        ];
        foreach (var id in secondary)
            if (seen.Add(id))
                result.Add(id);
        return result;
    }

    /// <summary>
    ///     <para>校验目录册项目能否写入业务表：ID、名称、目录长度与修订号必须满足冻结的 CHECK 约束。</para>
    ///     <para>
    ///         Validates that a catalog project can be written to the business table: id, name, root length, and revision
    ///         must satisfy the frozen CHECK constraints.
    ///     </para>
    /// </summary>
    private static void ValidateProjectForImport(Project project, string catalogPath)
    {
        if (!IsStoreIdentifier(project.Id))
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' " +
                                                     $"contains project id '{project.Id}' "     +
                                                     "that cannot be represented in the business database.",
                                                     SessionStoreFailureKind.Damaged);
        if (project.Name.Length is 0 or > Project.MaxNameLength || project.Name != project.Name.Trim())
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' "                     +
                                                     $"contains project '{project.Id}' with name '{project.Name}' " +
                                                     "that cannot be represented in the business database.",
                                                     SessionStoreFailureKind.Damaged);
        if (project.PrimaryRoot.Length is 0 or > Project.MaxRootPathLength)
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' " +
                                                     $"contains project '{project.Id}' with root '{project.PrimaryRoot}' " +
                                                     "that cannot be represented in the business database.",
                                                     SessionStoreFailureKind.Damaged);
        if (project.Revision < 1)
            throw new SessionStoreMigrationException($"Legacy project catalog '{catalogPath}' " +
                                                     $"contains project '{project.Id}' with revision {project.Revision}; " +
                                                     "a positive revision is required.",
                                                     SessionStoreFailureKind.Damaged);
    }

    /// <summary>
    ///     <para>计算组织状态指纹：覆盖六张组织表的最终内容与侧栏偏好是否参与导入，长度前缀避免拼接歧义。</para>
    ///     <para>
    ///         Computes the organization-state fingerprint over the final content of the six tables plus whether sidebar
    ///         preferences took part, with length prefixes to avoid concatenation ambiguity.
    ///     </para>
    /// </summary>
    private static string ComputeOrganizationFingerprint(SessionStoreOrganizationState state, bool sidebarPresent)
    {
        var builder = new StringBuilder();
        builder.Append("organizationState/1\n");
        AppendIdList(builder, "archived", state.ArchivedSessionIds);
        AppendIdList(builder, "pinnedProject", state.PinnedProjectIds);
        AppendIdList(builder, "pinnedSession", state.PinnedSessionIds);
        AppendIdList(builder, "manualProject", state.ManualProjectOrder);
        AppendIdList(builder, "manualSession", state.ManualSessionOrder);
        AppendIdList(builder, "collapsed", state.CollapsedGroupKeys);
        builder.Append("sidebarInput:").Append(sidebarPresent ? "present" : "absent").Append('\n');
        return ComputeSha256(builder);
    }

    /// <summary>
    ///     <para>计算项目目录册指纹：覆盖目录册修订号与每个项目的持久化语义字段，规范化路径按原值参与比较。</para>
    ///     <para>
    ///         Computes the project-catalog fingerprint over the catalog revision and every project's persisted-semantic
    ///         fields; normalized roots participate verbatim.
    ///     </para>
    /// </summary>
    private static string ComputeProjectFingerprint(ProjectCatalog catalog)
    {
        var builder = new StringBuilder();
        builder.Append("projectCatalog/1\n");
        builder.Append("revision:").Append(catalog.Revision).Append('\n');
        builder.Append("count:").Append(catalog.Projects.Count).Append('\n');
        foreach (var project in catalog.Projects)
        {
            AppendValue(builder, "id", project.Id);
            AppendValue(builder, "name", project.Name);
            AppendValue(builder, "primaryRoot", project.PrimaryRoot);
            AppendValue(builder, "roots", string.Join('\u001F', project.Roots.Select(root => root.Path)));
            builder.Append("revision:").Append(project.Revision).Append('\n');
            builder.Append("isDeleting:").Append(project.IsDeleting ? '1' : '0').Append('\n');
            builder.Append("createdUtc:").Append(project.CreatedUtc.ToUnixTimeMilliseconds()).Append('\n');
            builder.Append("updatedUtc:").Append(project.UpdatedUtc.ToUnixTimeMilliseconds()).Append('\n');
        }

        return ComputeSha256(builder);
    }

    /// <summary>
    ///     <para>
    ///         计算会话归属来源指纹：按文件名排序的日志名、字节长度与最后写入时间摘要。内容变化必然改变长度或
    ///         写入时间，因此无需读取全部日志即可发现旧程序在切换后的写入。
    ///     </para>
    ///     <para>
    ///         Computes the placement source fingerprint as a digest of the sorted log names, byte lengths, and last
    ///         write times. A content change always changes a length or a write time, so a post-switch write by an
    ///         older program is detected without reading every log.
    ///     </para>
    /// </summary>
    private static string ComputePlacementFingerprint(IReadOnlyList<string> files)
    {
        var builder = new StringBuilder();
        builder.Append("sessionPlacementSource/1\n");
        builder.Append("count:").Append(files.Count).Append('\n');
        foreach (var file in files)
        {
            var info = new FileInfo(file);
            builder.Append("file:");
            AppendLengthPrefixed(builder, info.Name);
            builder.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return ComputeSha256(builder);
    }

    /// <summary>
    ///     <para>把 ID 列表写入规范化文本；每行带长度前缀，保证不同列表不会拼出相同文本。</para>
    ///     <para>
    ///         Appends an id list to canonical text; every line carries a length prefix so distinct lists never concatenate
    ///         identically.
    ///     </para>
    /// </summary>
    private static void AppendIdList(StringBuilder builder, string name, IReadOnlyList<string> values)
    {
        builder.Append(name).Append(':').Append(values.Count).Append('\n');
        foreach (var value in values)
        {
            builder.Append("  ");
            AppendLengthPrefixed(builder, value);
            builder.Append('\n');
        }
    }

    /// <summary>
    ///     <para>把单个命名值写入规范化文本。</para>
    ///     <para>Appends one named value to canonical text.</para>
    /// </summary>
    private static void AppendValue(StringBuilder builder, string name, string value)
    {
        builder.Append(name).Append(':');
        AppendLengthPrefixed(builder, value);
        builder.Append('\n');
    }

    /// <summary>
    ///     <para>写入长度前缀加原值，消除分隔符歧义。</para>
    ///     <para>Writes a length prefix followed by the raw value, eliminating separator ambiguity.</para>
    /// </summary>
    private static void AppendLengthPrefixed(StringBuilder builder, string value) =>
        builder.Append(value.Length).Append(':').Append(value);

    /// <summary>
    ///     <para>计算规范化文本的 SHA-256 小写十六进制指纹。</para>
    ///     <para>Computes the lowercase hexadecimal SHA-256 fingerprint of canonical text.</para>
    /// </summary>
    private static string ComputeSha256(StringBuilder builder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();

    /// <summary>
    ///     <para>判断值是否为业务库要求的 32 位小写十六进制标识符。</para>
    ///     <para>Whether a value is the 32-character lowercase hexadecimal identifier required by the business database.</para>
    /// </summary>
    private static bool IsStoreIdentifier(string? value) =>
        value is { Length: 32 } && value.All(char.IsAsciiHexDigit) && value.All(character => !char.IsUpper(character));

    /// <summary>
    ///     <para>追加一条诊断，最多保留 <see cref="MaxDiagnostics" /> 条以免标记文件超预算。</para>
    ///     <para>
    ///         Appends one diagnostic, keeping at most <see cref="MaxDiagnostics" /> entries so the marker file stays within
    ///         budget.
    ///     </para>
    /// </summary>
    private static void AddDiagnostic(List<string> diagnostics, string detail)
    {
        if (diagnostics.Count >= MaxDiagnostics)
        {
            if (diagnostics.Count == MaxDiagnostics)
                diagnostics.Add($"Further diagnostics were omitted after {MaxDiagnostics} entries.");
            return;
        }

        diagnostics.Add(detail);
    }

    /// <summary>
    ///     <para>把诊断列表合并为一行说明，控制长度以适配标记文件的预算。</para>
    ///     <para>Joins diagnostics into one bounded note that fits the marker file budget.</para>
    /// </summary>
    private static string JoinDiagnostics(IReadOnlyList<string> diagnostics)
    {
        var text = string.Join(" | ", diagnostics);
        return text.Length <= 16_000 ? text : text[..16_000] + " | ...";
    }

    /// <summary>
    ///     <para>复制批次状态字典，避免检查结果与标记共享可变引用。</para>
    ///     <para>Copies the batch-state dictionary so inspection results never share mutable state with the marker.</para>
    /// </summary>
    private static IReadOnlyDictionary<SessionStoreMigrationBatch, SessionStoreBatchState> CopyBatches(
        SessionStoreMigrationMarker marker) =>
        marker.Batches.ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>
    ///     <para>校验并规范化 store 目录；核心迁移不接受相对路径，避免依赖启动目录。</para>
    ///     <para>
    ///         Validates and normalizes the store directory; the migration refuses relative paths that depend on the launch
    ///         directory.
    ///     </para>
    /// </summary>
    private static string NormalizeStoreDirectory(string storeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (!Path.IsPathFullyQualified(storeDirectory))
            throw new ArgumentException($"Session-store directory '{storeDirectory}' must be an absolute path.",
                                        nameof(storeDirectory));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));
    }

    /// <summary>
    ///     <para>业务库绑定步骤的结果：绑定后的迁移标记，以及本次把 schema 升级掉的来源版本。</para>
    ///     <para>
    ///         The result of the database-binding step: the bound migration marker and the schema version upgraded
    ///         during this run, if any.
    ///     </para>
    /// </summary>
    /// <param name="Marker">
    ///     <para>已与业务库身份绑定的迁移标记。</para>
    ///     <para>The migration marker bound to the business-database identity.</para>
    /// </param>
    /// <param name="UpgradedFrom">
    ///     <para>本次升级前的 schema 版本；未升级时为 null。</para>
    ///     <para>The schema version before this run's upgrade, or null when nothing was upgraded.</para>
    /// </param>
    private sealed record SessionStoreDatabaseBinding(SessionStoreMigrationMarker Marker, int? UpgradedFrom);

    /// <summary>
    ///     <para>一个批次的导入结果：行数、来源指纹与不阻断迁移的诊断。</para>
    ///     <para>
    ///         The import result of one batch: row count, source fingerprint, and diagnostics that do not block the
    ///         migration.
    ///     </para>
    /// </summary>
    private sealed record BatchImportResult(int ImportedRows, string Fingerprint, IReadOnlyList<string> Diagnostics);

    /// <summary>
    ///     <para>组织状态快照：最终状态、行数、来源指纹与诊断。</para>
    ///     <para>The organization-state snapshot: final state, row count, source fingerprint, and diagnostics.</para>
    /// </summary>
    private sealed record OrganizationSnapshot(
        SessionStoreOrganizationState State,
        int                           TotalRows,
        string                        Fingerprint,
        IReadOnlyList<string>         Diagnostics);

    /// <summary>
    ///     <para>项目目录册快照：项目列表与来源指纹。</para>
    ///     <para>The project-catalog snapshot: the project list and its source fingerprint.</para>
    /// </summary>
    private sealed record ProjectSnapshot(IReadOnlyList<Project> Projects, string Fingerprint);

    /// <summary>
    ///     <para>会话归属快照：归属行、来源指纹与损坏日志等诊断。</para>
    ///     <para>The session-placement snapshot: placement rows, source fingerprint, and diagnostics such as damaged logs.</para>
    /// </summary>
    private sealed record PlacementSnapshot(
        IReadOnlyList<SessionStorePlacementRow> Rows,
        string                                  Fingerprint,
        IReadOnlyList<string>                   Diagnostics);
}
