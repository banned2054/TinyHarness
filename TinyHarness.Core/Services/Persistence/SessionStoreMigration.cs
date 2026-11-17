namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         会话存储从旧文件迁入业务库的显式入口：读取迁移标记、执行分批导入并在每批切换后写入持久化证明。
///         迁移不在后台自动开始，也不在多进程间共享租约；同一 store 同一时间只允许一个迁移、备份或恢复。
///     </para>
///     <para>
///         The explicit entry point that migrates a session store from legacy files into the business database:
///         reading the marker, performing the batched import, and recording the durable proof after each batch
///         switches. Migration never starts implicitly in the background and shares no cross-process lease; one
///         store allows only one migration, backup, or restore at a time.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         迁移顺序固定为组织状态 → 项目目录册 → 会话归属，每一批在单个事务内替换该批数据，随后把批次
///         标记为已切换；事务与标记写入不是跨文件原子的，因此任何中断都按“批次未切换 → 重做该批”
///         恢复。旧文件在全部批次切换并通过结构校验前保持原样，完成后也不自动删除。
///     </para>
///     <para>
///         The order is fixed to organization state → project catalog → session placement. Each batch replaces
///         its data inside a single transaction and only then records the switch. The transaction and the marker
///         write are not atomic across files, so any interruption recovers as "batch not switched → redo that
///         batch". Legacy files stay untouched until every batch is switched and validated, and are never deleted
///         automatically afterwards.
///     </para>
/// </remarks>
public static partial class SessionStoreMigration
{
    /// <summary>
    ///     <para>迁移标记文件名；位于 store 目录，与业务库分开。</para>
    ///     <para>The migration-marker file name; it lives in the store directory, separate from the business database.</para>
    /// </summary>
    public const string MarkerFileName = "session-store-migration.json";

    /// <summary>
    ///     <para>迁移租约文件名；独占句柄由操作系统在进程退出时释放。</para>
    ///     <para>The migration-lease file name; the operating system releases an exclusive handle on process exit.</para>
    /// </summary>
    public const string LeaseFileName = "session-store-migration.lock";

    /// <summary>
    ///     <para>返回迁移标记文件在 store 目录内的绝对路径。</para>
    ///     <para>Returns the absolute migration-marker path inside a store directory.</para>
    /// </summary>
    public static string MarkerPathFor(string storeDirectory) =>
        SessionStoreMigrationStateStore.MarkerPathFor(storeDirectory, MarkerFileName);

    /// <summary>
    ///     <para>读取迁移标记；缺失、有效与损坏分别报告，损坏时保留文件供人工恢复。</para>
    ///     <para>Reads the migration marker, reporting absence, validity, or damage separately and keeping a damaged file for manual recovery.</para>
    /// </summary>
    public static Task<SessionStoreMarkerRead> ReadMarkerAsync(
        string storeDirectory, CancellationToken cancellationToken) =>
        SessionStoreMigrationStateStore.ReadAsync(storeDirectory, MarkerFileName, cancellationToken);

    /// <summary>
    ///     <para>获取 store 级迁移租约；迁移、备份与恢复在持有期间互斥。</para>
    ///     <para>Acquires the store-level migration lease; migrations, backups, and restores are mutually exclusive while it is held.</para>
    /// </summary>
    internal static Task<SessionStoreMigrationLease> AcquireLeaseAsync(
        string storeDirectory, CancellationToken cancellationToken) =>
        SessionStoreMigrationStateStore.AcquireLeaseAsync(storeDirectory, LeaseFileName, cancellationToken);

    /// <summary>
    ///     <para>在租约内原子写入迁移标记。</para>
    ///     <para>Atomically writes the migration marker while the lease is held.</para>
    /// </summary>
    internal static Task WriteMarkerAsync(
        string storeDirectory, SessionStoreMigrationMarker marker, CancellationToken cancellationToken) =>
        SessionStoreMigrationStateStore.WriteAsync(storeDirectory, MarkerFileName, marker, cancellationToken);
}
