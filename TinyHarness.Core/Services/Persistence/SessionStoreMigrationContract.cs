using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         迁移批次；切换按 <see cref="OrganizationState" /> → <see cref="ProjectCatalog" /> →
///         <see cref="SessionPlacement" /> 的顺序推进，每一批在标记中独立记录权威来源。
///     </para>
///     <para>
///         The migration batches; switching advances in the order
///         <see cref="OrganizationState" /> → <see cref="ProjectCatalog" /> → <see cref="SessionPlacement" />,
///         and each batch records its authoritative source independently in the marker.
///     </para>
/// </summary>
public enum SessionStoreMigrationBatch
{
    /// <summary>
    ///     <para>归档、置顶、普通顺序、置顶顺序与每 store 折叠状态。</para>
    ///     <para>Archive, pins, regular order, pinned order, and per-store collapsed state.</para>
    /// </summary>
    OrganizationState,

    /// <summary>
    ///     <para>项目定义目录册。</para>
    ///     <para>The project definition catalog.</para>
    /// </summary>
    ProjectCatalog,

    /// <summary>
    ///     <para>会话当前项目归属。</para>
    ///     <para>Session current project placement.</para>
    /// </summary>
    SessionPlacement
}

/// <summary>
///     <para>单个批次的迁移状态：尚未切换或已切换。</para>
///     <para>The migration state of one batch: not switched yet, or switched.</para>
/// </summary>
public enum SessionStoreBatchState
{
    /// <summary>
    ///     <para>尚未切换；该批数据仍以旧来源为准，导入可重做。</para>
    ///     <para>Not switched yet; the legacy source remains authoritative and the import can be redone.</para>
    /// </summary>
    Pending,

    /// <summary>
    ///     <para>已切换；该批数据以业务库为准，旧来源只作导入输入与冲突诊断。</para>
    ///     <para>Switched; the business database is authoritative and the legacy source is import input and conflict diagnosis only.</para>
    /// </summary>
    Switched
}

/// <summary>
///     <para>
///         会话存储的迁移状态。区分首次迁移、迁移中断、迁移后业务库丢失、同名库被替换、标记损坏与
///         旧来源在切换后被改写；生产读写只在 <see cref="Completed" /> 时使用业务库。
///     </para>
///     <para>
///         The session-store migration state. It distinguishes a first migration, an interrupted migration, a
///         business database lost after migration, a replaced file of the same name, a damaged marker, and legacy
///         sources rewritten after switching; production reads and writes use the business database only while
///         <see cref="Completed" />.
///     </para>
/// </summary>
public enum SessionStoreMigrationStatus
{
    /// <summary>
    ///     <para>没有迁移标记：尚未开始首次导入；旧来源仍是生产权威。</para>
    ///     <para>No migration marker: the first import has not started and the legacy sources remain production authorities.</para>
    /// </summary>
    NotStarted,

    /// <summary>
    ///     <para>有迁移标记但仍有批次未切换：迁移进行中或上次中断，继续迁移即可；生产读写拒绝。</para>
    ///     <para>
    ///         A migration marker exists but some batch is not switched: the migration is in progress or was
    ///         interrupted and must be resumed; production reads and writes are refused.
    ///     </para>
    /// </summary>
    InProgress,

    /// <summary>
    ///     <para>全部批次已切换且业务库校验通过：业务库是生产权威。</para>
    ///     <para>Every batch is switched and the business database verifies: it is the production authority.</para>
    /// </summary>
    Completed,

    /// <summary>
    ///     <para>标记显示迁移完成，但业务库文件缺失：只能由业务备份恢复，不得重新导入旧来源。</para>
    ///     <para>
    ///         The marker says migration completed but the business database is absent: only a business backup can
    ///         restore it and the legacy sources must not be imported again.
    ///     </para>
    /// </summary>
    DatabaseMissing,

    /// <summary>
    ///     <para>业务库身份与标记记录不一致：同名文件已被替换，必须显式恢复或确认，不得当作同一库继续。</para>
    ///     <para>
    ///         The business-database identity contradicts the marker: a same-named file was replaced, and it needs an
    ///         explicit restore or confirmation instead of being served as the same database.
    ///     </para>
    /// </summary>
    DatabaseReplaced,

    /// <summary>
    ///     <para>业务库存在但损坏或版本不受支持：只能由备份恢复。</para>
    ///     <para>The business database exists but is damaged or unsupported: only a backup can restore it.</para>
    /// </summary>
    DatabaseDamaged,

    /// <summary>
    ///     <para>迁移标记无法严格解析：保留文件并拒绝任何自动处理，交由人工恢复。</para>
    ///     <para>The migration marker cannot be parsed strictly: keep it and refuse automatic handling for manual recovery.</para>
    /// </summary>
    MarkerDamaged
}

/// <summary>
///     <para>迁移标记文件的读取结果：缺失、有效或损坏（附原因）。</para>
///     <para>The read result of the migration-marker file: missing, valid, or damaged with a reason.</para>
/// </summary>
public enum SessionStoreMarkerReadStatus
{
    Missing,
    Present,
    Damaged
}

/// <summary>
///     <para>
///         记录迁移状态、库身份与各批来源指纹的持久化证明；位于业务库之外，用于区分首次迁移、迁移中断、
///         迁移后丢库与同名库被替换。
///     </para>
///     <para>
///         The persisted proof of the migration state, database identity, and per-batch source fingerprints. It
///         lives outside the business database and distinguishes a first migration, an interrupted migration, a
///         database lost after migration, and a replaced file of the same name.
///     </para>
/// </summary>
/// <param name="Schema">
///     <para>标记协议版本。</para>
///     <para>The marker protocol version.</para>
/// </param>
/// <param name="MigrationId">
///     <para>本次迁移的随机标识；与业务库元数据中的同名键共同绑定标记与库。</para>
///     <para>The random identity of this migration; together with the same key in the database metadata it binds the marker to the database.</para>
/// </param>
/// <param name="StoreIdentity">
///     <para>业务库身份；在建库后写入，尚未建库时为 null。</para>
///     <para>The business-database identity, written after creation and null before it.</para>
/// </param>
/// <param name="StartedUtc">
///     <para>迁移开始时间。</para>
///     <para>When the migration started.</para>
/// </param>
/// <param name="UpdatedUtc">
///     <para>最近一次状态写入时间。</para>
///     <para>When the state was last written.</para>
/// </param>
/// <param name="Batches">
///     <para>各批次的切换状态。</para>
///     <para>The switching state of every batch.</para>
/// </param>
/// <param name="SourceFingerprints">
///     <para>各批切换时记录的旧来源指纹；未切换的批次没有条目。</para>
///     <para>The legacy-source fingerprint recorded when each batch switched; unswitched batches have no entry.</para>
/// </param>
/// <param name="LastError">
///     <para>最近一次可重试失败的不含凭据说明；成功时为空。</para>
///     <para>A credential-free note about the last retryable failure, empty on success.</para>
/// </param>
public sealed record SessionStoreMigrationMarker(
    string                                            Schema,
    string                                            MigrationId,
    string?                                           StoreIdentity,
    DateTimeOffset                                    StartedUtc,
    DateTimeOffset                                    UpdatedUtc,
    IReadOnlyDictionary<SessionStoreMigrationBatch, SessionStoreBatchState> Batches,
    IReadOnlyDictionary<SessionStoreMigrationBatch, string>                SourceFingerprints,
    string?                                           LastError)
{
    /// <summary>
    ///     <para>当前迁移标记协议版本。</para>
    ///     <para>The current migration-marker protocol version.</para>
    /// </summary>
    public const string CurrentSchema = "tinyharness.session-store-migration/1";

    /// <summary>
    ///     <para>全部批次是否都已切换。</para>
    ///     <para>Whether every batch is switched.</para>
    /// </summary>
    public bool IsComplete =>
        Batches.Count == 3 && Batches.Values.All(state => state == SessionStoreBatchState.Switched);

    /// <summary>
    ///     <para>按标记顺序返回尚未切换的批次。</para>
    ///     <para>Returns the batches still unswitched, in migration order.</para>
    /// </summary>
    public IReadOnlyList<SessionStoreMigrationBatch> PendingBatches =>
        BatchOrder.Where(batch => Batches.GetValueOrDefault(batch) != SessionStoreBatchState.Switched).ToArray();

    /// <summary>
    ///     <para>固定的批次推进顺序。</para>
    ///     <para>The fixed batch order of the migration.</para>
    /// </summary>
    public static readonly SessionStoreMigrationBatch[] BatchOrder =
    [
        SessionStoreMigrationBatch.OrganizationState,
        SessionStoreMigrationBatch.ProjectCatalog,
        SessionStoreMigrationBatch.SessionPlacement
    ];

    /// <summary>
    ///     <para>创建首次迁移标记：尚无库身份、全部批次待切换。</para>
    ///     <para>Creates the first-migration marker: no database identity yet and every batch pending.</para>
    /// </summary>
    public static SessionStoreMigrationMarker Start(string migrationId, DateTimeOffset utcNow) => new(
        CurrentSchema,
        migrationId,
        null,
        utcNow,
        utcNow,
        BatchOrder.ToDictionary(batch => batch, _ => SessionStoreBatchState.Pending),
        new Dictionary<SessionStoreMigrationBatch, string>(),
        null);

    /// <summary>
    ///     <para>返回记录业务库身份后的标记。</para>
    ///     <para>Returns the marker after recording the business-database identity.</para>
    /// </summary>
    public SessionStoreMigrationMarker WithStoreIdentity(string storeIdentity, DateTimeOffset utcNow) =>
        this with
        {
            StoreIdentity = storeIdentity,
            UpdatedUtc    = utcNow
        };

    /// <summary>
    ///     <para>返回把指定批次标记为已切换并记录来源指纹后的标记。</para>
    ///     <para>Returns the marker after switching one batch and recording its source fingerprint.</para>
    /// </summary>
    public SessionStoreMigrationMarker WithBatchSwitched(
        SessionStoreMigrationBatch batch, string sourceFingerprint, DateTimeOffset utcNow)
    {
        var batches      = Batches.ToDictionary(pair => pair.Key, pair => pair.Value);
        var fingerprints = SourceFingerprints.ToDictionary(pair => pair.Key, pair => pair.Value);
        batches[batch]      = SessionStoreBatchState.Switched;
        fingerprints[batch] = sourceFingerprint;
        return this with
        {
            Batches            = batches,
            SourceFingerprints = fingerprints,
            UpdatedUtc         = utcNow,
            LastError          = null
        };
    }

    /// <summary>
    ///     <para>返回记录一次可重试失败说明后的标记；批次状态不变，重试从原进度继续。</para>
    ///     <para>Returns the marker with a retryable failure note; batch states are unchanged and a retry continues from the recorded progress.</para>
    /// </summary>
    public SessionStoreMigrationMarker WithFailure(string detail, DateTimeOffset utcNow) =>
        this with
        {
            LastError  = detail,
            UpdatedUtc = utcNow
        };
}

/// <summary>
///     <para>迁移标记文件的读取结果；缺失时标记为 null，损坏时带原因。</para>
///     <para>The read result of the marker file; the marker is null when missing and the detail explains damage.</para>
/// </summary>
/// <param name="Status">
///     <para>读取状态。</para>
///     <para>The read status.</para>
/// </param>
/// <param name="Marker">
///     <para>有效标记；缺失或损坏时为 null。</para>
///     <para>The valid marker, or null when missing or damaged.</para>
/// </param>
/// <param name="Detail">
///     <para>损坏原因；其他状态为 null。</para>
///     <para>The damage reason, or null for other statuses.</para>
/// </param>
public sealed record SessionStoreMarkerRead(
    SessionStoreMarkerReadStatus Status,
    SessionStoreMigrationMarker? Marker,
    string?                        Detail);

/// <summary>
///     <para>
///         一次显式迁移的输入：受保护的 store 目录，可选的桌面侧栏偏好文件（每 store 顺序与折叠状态），
///         以及可选的发起方说明。
///     </para>
///     <para>
///         The input of one explicit migration: the protected store directory, an optional desktop sidebar
///         preferences file (per-store order and collapsed state), and an optional actor note.
///     </para>
/// </summary>
/// <param name="StoreDirectory">
///     <para>要迁移的受保护会话存储目录。</para>
///     <para>The protected session-store directory to migrate.</para>
/// </param>
public sealed record SessionStoreMigrationOptions(string StoreDirectory)
{
    /// <summary>
    ///     <para>
    ///         桌面侧栏偏好文件路径；提供时导入该 store 的手动顺序与折叠分组，缺失或无法读取时让迁移
    ///         明确失败，而不是静默丢失组织状态。
    ///     </para>
    ///     <para>
    ///         The desktop sidebar preferences path; when set, the store's manual order and collapsed groups are
    ///         imported, and a missing or unreadable file fails the migration explicitly instead of silently
    ///         dropping organization state.
    ///     </para>
    /// </summary>
    public string? SidebarPreferencesPath { get; init; }

    /// <summary>
    ///     <para>可选的发起方说明，仅用于诊断。</para>
    ///     <para>An optional actor note used for diagnostics only.</para>
    /// </summary>
    public string? Actor { get; init; }
}

/// <summary>
///     <para>单个批次的迁移报告：本次是否切换、导入行数与来源指纹。</para>
///     <para>The report of one batch: whether it switched this run, the imported row count, and the source fingerprint.</para>
/// </summary>
/// <param name="Batch">
///     <para>批次。</para>
///     <para>The batch.</para>
/// </param>
/// <param name="SwitchedThisRun">
///     <para>本次运行是否完成了切换。</para>
///     <para>Whether this run performed the switch.</para>
/// </param>
/// <param name="ImportedRows">
///     <para>导入的业务行数。</para>
///     <para>The number of imported business rows.</para>
/// </param>
/// <param name="SourceFingerprint">
///     <para>切换时记录的来源指纹。</para>
///     <para>The source fingerprint recorded at switch time.</para>
/// </param>
public sealed record SessionStoreMigrationBatchReport(
    SessionStoreMigrationBatch Batch,
    bool                       SwitchedThisRun,
    int                        ImportedRows,
    string                     SourceFingerprint);

/// <summary>
///     <para>一次迁移运行的结果：迁移标识、库身份与全部批次报告。</para>
///     <para>The result of one migration run: the migration identity, the database identity, and every batch report.</para>
/// </summary>
/// <param name="AlreadyCompleted">
///     <para>迁移此前已完成且本次未执行任何导入。</para>
///     <para>The migration had already completed and this run imported nothing.</para>
/// </param>
/// <param name="MigrationId">
///     <para>迁移标识。</para>
///     <para>The migration identity.</para>
/// </param>
/// <param name="StoreIdentity">
///     <para>业务库身份。</para>
///     <para>The business-database identity.</para>
/// </param>
/// <param name="Batches">
///     <para>按迁移顺序的批次报告。</para>
///     <para>The batch reports in migration order.</para>
/// </param>
public sealed record SessionStoreMigrationResult(
    bool                                         AlreadyCompleted,
    string                                       MigrationId,
    string                                       StoreIdentity,
    IReadOnlyList<SessionStoreMigrationBatchReport> Batches);

/// <summary>
///     <para>迁移状态检查结果：状态分类、说明、标记内容摘要与旧来源在切换后被改写的诊断。</para>
///     <para>
///         The result of a migration-state inspection: the status category, a detail, a marker summary, and
///         diagnostics for legacy sources rewritten after switching.
///     </para>
/// </summary>
/// <param name="Status">
///     <para>迁移状态。</para>
///     <para>The migration status.</para>
/// </param>
/// <param name="Detail">
///     <para>非正常状态的原因说明；正常时为空。</para>
///     <para>The reason for any abnormal state, empty otherwise.</para>
/// </param>
/// <param name="MigrationId">
///     <para>标记中的迁移标识；标记缺失或损坏时为 null。</para>
///     <para>The migration identity from the marker, or null when absent or damaged.</para>
/// </param>
/// <param name="StoreIdentity">
///     <para>标记记录的库身份；尚未创建业务库时为 null。</para>
///     <para>The database identity recorded in the marker, or null before the database exists.</para>
/// </param>
/// <param name="Batches">
///     <para>各批次的切换状态；标记缺失或损坏时为空。</para>
///     <para>The batch states, or empty when the marker is absent or damaged.</para>
/// </param>
/// <param name="Warnings">
///     <para>不改变状态但需要提示的诊断（例如已切换批次对应的旧文件在切换后被改写）。</para>
///     <para>Diagnostics that do not change the status but must be surfaced (for example legacy files changed after switching).</para>
/// </param>
public sealed record SessionStoreMigrationInspection(
    SessionStoreMigrationStatus                                           Status,
    string?                                                               Detail,
    string?                                                               MigrationId,
    string?                                                               StoreIdentity,
    IReadOnlyDictionary<SessionStoreMigrationBatch, SessionStoreBatchState> Batches,
    IReadOnlyList<string>                                                 Warnings)
{
    /// <summary>
    ///     <para>生产读写是否可以使用业务库。</para>
    ///     <para>Whether production reads and writes may use the business database.</para>
    /// </summary>
    public bool IsMigrated => Status == SessionStoreMigrationStatus.Completed;
}

/// <summary>
///     <para>一个 store 的组织状态快照：归档集合、置顶顺序、普通顺序与折叠分组。</para>
///     <para>The organization-state snapshot of one store: archived set, pinned order, regular orders, and collapsed groups.</para>
/// </summary>
/// <param name="ArchivedSessionIds">
///     <para>已归档会话 ID（按序数排序）。</para>
///     <para>Archived session ids, ordinal-sorted.</para>
/// </param>
/// <param name="PinnedProjectIds">
///     <para>置顶项目 ID，按置顶位置升序。</para>
///     <para>Pinned project ids in ascending pin position.</para>
/// </param>
/// <param name="PinnedSessionIds">
///     <para>置顶会话 ID，按置顶位置升序。</para>
///     <para>Pinned session ids in ascending pin position.</para>
/// </param>
/// <param name="ManualProjectOrder">
///     <para>普通分类中的项目手动顺序，按位置升序。</para>
///     <para>The manual project order in the regular category, ascending by position.</para>
/// </param>
/// <param name="ManualSessionOrder">
///     <para>手工会话顺序，按位置升序。</para>
///     <para>The manual session order, ascending by position.</para>
/// </param>
/// <param name="CollapsedGroupKeys">
///     <para>已折叠的项目或展示组键（按序数排序）。</para>
///     <para>Collapsed project or display-group keys, ordinal-sorted.</para>
/// </param>
public sealed record SessionStoreOrganizationState(
    IReadOnlyList<string> ArchivedSessionIds,
    IReadOnlyList<string> PinnedProjectIds,
    IReadOnlyList<string> PinnedSessionIds,
    IReadOnlyList<string> ManualProjectOrder,
    IReadOnlyList<string> ManualSessionOrder,
    IReadOnlyList<string> CollapsedGroupKeys)
{
    /// <summary>
    ///     <para>空组织状态。</para>
    ///     <para>The empty organization state.</para>
    /// </summary>
    public static SessionStoreOrganizationState Empty() => new([], [], [], [], [], []);
}

/// <summary>
///     <para>导入用的会话归属行：会话 ID、项目 ID（null 表示明确无项目）与更新时间。</para>
///     <para>One session-placement row for the import: session id, project id (null means explicitly no project), and update time.</para>
/// </summary>
/// <param name="SessionId">
///     <para>32 位小写十六进制会话 ID。</para>
///     <para>The 32-character lowercase hexadecimal session id.</para>
/// </param>
/// <param name="ProjectId">
///     <para>项目 ID；null 表示明确无项目。</para>
///     <para>The project id, or null for explicitly no project.</para>
/// </param>
/// <param name="UpdatedUtc">
///     <para>该归属事实的导入时间。</para>
///     <para>The import time of this placement fact.</para>
/// </param>
public sealed record SessionStorePlacementRow(string SessionId, string? ProjectId, DateTimeOffset UpdatedUtc);

/// <summary>
///     <para>一个会话的当前归属：尚未初始化、明确无项目或属于某个项目。</para>
///     <para>A session's current placement: not initialized yet, explicitly no project, or belonging to a project.</para>
/// </summary>
/// <param name="State">
///     <para>归属状态。</para>
///     <para>The placement state.</para>
/// </param>
/// <param name="ProjectId">
///     <para>项目 ID；仅 <see cref="SessionProjectAssignmentState.Project" /> 时非空。</para>
///     <para>The project id, non-null only for <see cref="SessionProjectAssignmentState.Project" />.</para>
/// </param>
public sealed record SessionStorePlacement(SessionProjectAssignmentState State, string? ProjectId)
{
    /// <summary>
    ///     <para>尚未初始化归属。</para>
    ///     <para>Placement has not been initialized yet.</para>
    /// </summary>
    public static SessionStorePlacement PendingInitialization() =>
        new(SessionProjectAssignmentState.PendingInitialization, null);

    /// <summary>
    ///     <para>明确无项目。</para>
    ///     <para>Explicitly no project.</para>
    /// </summary>
    public static SessionStorePlacement NoProject() => new(SessionProjectAssignmentState.NoProject, null);

    /// <summary>
    ///     <para>属于指定项目。</para>
    ///     <para>Belongs to the specified project.</para>
    /// </summary>
    public static SessionStorePlacement ForProject(string projectId) =>
        new(SessionProjectAssignmentState.Project, projectId);
}

/// <summary>
///     <para>业务库备份的元数据：路径、库身份、schema 版本、字节数与创建时间。</para>
///     <para>The metadata of a business-database backup: path, identity, schema version, byte length, and creation time.</para>
/// </summary>
/// <param name="Path">
///     <para>备份文件绝对路径。</para>
///     <para>The absolute backup file path.</para>
/// </param>
/// <param name="StoreIdentity">
///     <para>备份中的业务库身份。</para>
///     <para>The business-database identity inside the backup.</para>
/// </param>
/// <param name="SchemaVersion">
///     <para>备份中的 schema 版本。</para>
///     <para>The schema version inside the backup.</para>
/// </param>
/// <param name="LengthBytes">
///     <para>备份文件字节数。</para>
///     <para>The backup file's byte length.</para>
/// </param>
/// <param name="CreatedUtc">
///     <para>备份创建时间。</para>
///     <para>When the backup was created.</para>
/// </param>
public sealed record SessionStoreBackupInfo(
    string         Path,
    string         StoreIdentity,
    int            SchemaVersion,
    long           LengthBytes,
    DateTimeOffset CreatedUtc);

/// <summary>
///     <para>备份校验结果状态。</para>
///     <para>The verification status of a backup.</para>
/// </summary>
public enum SessionStoreBackupVerificationStatus
{
    /// <summary>
    ///     <para>结构、完整性检查与库身份均通过。</para>
    ///     <para>The structure, integrity check, and database identity all pass.</para>
    /// </summary>
    Valid,

    /// <summary>
    ///     <para>备份文件缺失。</para>
    ///     <para>The backup file is missing.</para>
    /// </summary>
    Missing,

    /// <summary>
    ///     <para>备份文件损坏或不是本程序的业务库。</para>
    ///     <para>The backup is damaged or not this program's business database.</para>
    /// </summary>
    Damaged,

    /// <summary>
    ///     <para>备份版本高于或低于本程序支持的版本。</para>
    ///     <para>The backup version is newer or older than this build supports.</para>
    /// </summary>
    Unsupported,

    /// <summary>
    ///     <para>备份有效，但库身份与目标 store 的迁移标记不一致。</para>
    ///     <para>The backup is valid but its identity mismatches the target store's migration marker.</para>
    /// </summary>
    IdentityMismatch
}

/// <summary>
///     <para>备份校验结果：状态、说明、库身份与 schema 版本。</para>
///     <para>The result of a backup verification: status, detail, identity, and schema version.</para>
/// </summary>
/// <param name="Status">
///     <para>校验状态。</para>
///     <para>The verification status.</para>
/// </param>
/// <param name="Detail">
///     <para>失败原因；有效时为 null。</para>
///     <para>The failure reason, or null when valid.</para>
/// </param>
/// <param name="StoreIdentity">
///     <para>读到的库身份；文件缺失或损坏时为 null。</para>
///     <para>The read identity, or null when the file is missing or damaged.</para>
/// </param>
/// <param name="SchemaVersion">
///     <para>读到的 schema 版本；文件缺失或损坏时为 null。</para>
///     <para>The read schema version, or null when the file is missing or damaged.</para>
/// </param>
public sealed record SessionStoreBackupVerification(
    SessionStoreBackupVerificationStatus Status,
    string?                             Detail,
    string?                             StoreIdentity,
    int?                                SchemaVersion);
