namespace TinyHarness.Core.Exceptions;

/// <summary>
///     <para>
///         会话存储迁移、备份恢复与权威切换的失败类别；用于把锁等待、权限、只读、损坏、缺失与身份冲突
///         分开报告，避免一律混成“缺失”或“损坏”。
///     </para>
///     <para>
///         The failure categories of session-store migration, backup/restore, and authority switching; they keep
///         lock waits, permissions, read-only media, damage, absence, and identity conflicts apart instead of
///         collapsing everything into "missing" or "damaged".
///     </para>
/// </summary>
public enum SessionStoreFailureKind
{
    /// <summary>
    ///     <para>确定不存在；首次迁移与迁移后丢失需由迁移标记区分。</para>
    ///     <para>Definitively absent; the migration marker distinguishes a first migration from a post-migration loss.</para>
    /// </summary>
    Missing,

    /// <summary>
    ///     <para>SQLite 明确报告文件损坏或不是数据库。</para>
    ///     <para>SQLite definitively reports corruption or a non-database file.</para>
    /// </summary>
    Damaged,

    /// <summary>
    ///     <para>结构或版本不属于本程序，或高于本程序支持的版本。</para>
    ///     <para>The structure or version does not belong to this program, or is newer than this build supports.</para>
    /// </summary>
    Unsupported,

    /// <summary>
    ///     <para>锁等待耗尽或被其他连接／进程占用。</para>
    ///     <para>Lock waiting was exhausted or another connection or process holds the resource.</para>
    /// </summary>
    Locked,

    /// <summary>
    ///     <para>权限不足，无法读取或写入。</para>
    ///     <para>Insufficient permission to read or write.</para>
    /// </summary>
    PermissionDenied,

    /// <summary>
    ///     <para>目标为只读介质或只读连接。</para>
    ///     <para>The target is read-only media or a read-only connection.</para>
    /// </summary>
    ReadOnly,

    /// <summary>
    ///     <para>文件系统或其他 IO 失败，未达到可判定损坏的程度。</para>
    ///     <para>A file-system or other IO failure that does not establish damage.</para>
    /// </summary>
    IoFailure,

    /// <summary>
    ///     <para>库身份或迁移状态与持久化证明不一致（同名库被替换或标记被改写）。</para>
    ///     <para>The database identity or migration state contradicts the persisted proof (a replaced file or a rewritten marker).</para>
    /// </summary>
    Conflict,

    /// <summary>
    ///     <para>当前存储尚未完成迁移，生产读写必须先显式迁移或恢复。</para>
    ///     <para>The store is not migrated yet; production reads and writes require an explicit migration or restore first.</para>
    /// </summary>
    MigrationRequired
}
