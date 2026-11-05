using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>目录册文件的单次状态快照：存在性、字节长度与 UTC 修改时间，作为内容检查前的快速变化线索。</para>
///     <para>
///         A single stat snapshot of the catalog file: existence, byte length, and UTC write time, the
///         fast change cue before any content check.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         文件缺失时统一归一化为 <c>Exists = false</c>、<c>Length = 0</c>、
///         <c>LastWriteTimeUtc = DateTime.MinValue</c>：各平台对不存在文件返回的默认时间不同，
///         归一化保证“都不存在”的基线在任何平台上一致可比。
///     </para>
///     <para>
///         A missing file normalizes to <c>Exists = false</c>, <c>Length = 0</c>, and
///         <c>LastWriteTimeUtc = DateTime.MinValue</c>: platforms report different default times for
///         missing files, and the normalization keeps an all-missing baseline comparable everywhere.
///     </para>
/// </remarks>
public readonly record struct ProjectCatalogFileState(bool Exists, long Length, DateTime LastWriteTimeUtc);

/// <summary>
///     <para>目录册变更检查的状态。</para>
///     <para>The status of a catalog change check.</para>
/// </summary>
public enum ProjectCatalogCheckStatus
{
    /// <summary>
    ///     <para>文件元数据与已确认基线完全一致，未读取文件内容。</para>
    ///     <para>File metadata fully matches the confirmed baseline; file content was not read.</para>
    /// </summary>
    Unchanged,

    /// <summary>
    ///     <para>目录册内容已加载；结果携带目录册与本次 stat 值。</para>
    ///     <para>The catalog content was loaded; the result carries the catalog and this round's stat.</para>
    /// </summary>
    Loaded,

    /// <summary>
    ///     <para>加载失败；基线保持不变，下一轮检查自然重试。</para>
    ///     <para>The load failed; the baseline stays untouched and the next check retries naturally.</para>
    /// </summary>
    Failed
}

/// <summary>
///     <para>单次目录册变更检查的结果：状态、加载到的目录册与本次文件状态快照。</para>
///     <para>
///         The result of one catalog change check: status, the loaded catalog, and this round's
///         file-state snapshot.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         仅 <see cref="ProjectCatalogCheckStatus.Loaded" /> 携带非 null 目录册；
///         <see cref="ProjectCatalogFileState" /> 始终是检查开始时的 stat 值，调用方在成功处理一次
///         Loaded 结果后把它传给 <see cref="ProjectCatalogMonitor.Confirm" /> 记为已消费基线。
///     </para>
///     <para>
///         Only <see cref="ProjectCatalogCheckStatus.Loaded" /> carries a non-null catalog;
///         <see cref="ProjectCatalogFileState" /> is always the stat taken when the check started, and
///         the caller passes it to <see cref="ProjectCatalogMonitor.Confirm" /> after successfully
///         processing one Loaded result to record the consumed baseline.
///     </para>
/// </remarks>
public readonly record struct ProjectCatalogCheckResult(
    ProjectCatalogCheckStatus Status,
    ProjectCatalog?           Catalog,
    ProjectCatalogFileState   FileState);

/// <summary>
///     <para>
///         以文件元数据为线索的项目目录册变化检测：元数据可疑时经
///         <see cref="ProjectStore.LoadAsync" /> 加载内容，基线由调用方显式确认。
///     </para>
///     <para>
///         Project-catalog change detection using file metadata as the cue: a suspicious stat loads the
///         content via <see cref="ProjectStore.LoadAsync" />, and baseline confirmation stays explicit
///         with the caller.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         两级判定：存在性、长度与修改时间等元数据是廉价的快速线索，与已确认基线完全一致
///         （含“都不存在”）时跳过读取；任何差异都加载完整内容，是否构成变化由调用方用内容比较
///         （如 <c>ProjectCatalog.ContentEquals</c>）对照内存已应用列表决定。
///     </para>
///     <para>
///         Two-level judgment: metadata (existence, length, write time) is a cheap fast cue, and a full
///         match against the confirmed baseline (including both-missing) skips reading; any difference
///         loads the complete content, and whether it constitutes a change is decided by the caller
///         comparing content (for example with <c>ProjectCatalog.ContentEquals</c>) against the
///         in-memory applied list.
///     </para>
///     <para>
///         Confirm 由调用方显式调用是刻意的：本组件不读取内容语义，调用方成功处理一次 Loaded
///         结果（已应用，或确认内容与内存一致而跳过应用）后必须 Confirm 该次 stat；被跳过的应用
///         不 Confirm 就不会吞掉变化，同一差异会持续加载。
///     </para>
///     <para>
///         Confirm being explicit with the caller is deliberate: this component never reads content
///         semantics, so after successfully processing one Loaded result (applied, or skipped because the
///         content already matches memory) the caller must Confirm that stat; a skipped application that
///         is not Confirmed never swallows the change, and the same difference keeps reloading.
///     </para>
///     <para>
///         Failed 不改变基线：下一轮 stat 仍与基线不同，检查自然重试；损坏、共享冲突与权限问题都
///         归入 Failed，重试节奏（退避）由调用方负责。
///     </para>
///     <para>
///         Failed never changes the baseline: the next round's stat still differs from it, so the check
///         retries naturally; damage, sharing conflicts, and permission problems all report Failed, with
///         the retry cadence (backoff) owned by the caller.
///     </para>
///     <para>
///         先 stat 后 load 是有意的收敛序：结果中的 stat 是加载前取值，若加载期间发生新写入，
///         下一轮 stat 与本轮不同，会再次触发检查并最终收敛到最新内容；每次检查只返回某一时刻的
///         完整快照或明确的失败，不返回新旧混合内容。
///     </para>
///     <para>
///         Stat before load is a deliberate convergence order: the result's stat is taken before the
///         load, so a write that lands during the load makes the next round's stat differ from this
///         round's and triggers another check, eventually converging on the latest content. Each check
///         returns one complete point-in-time snapshot or a clear failure, never mixed old and new
///         content.
///     </para>
///     <para>
///         实例方法允许并发调用：内部用简单锁保护基线读写，锁内只做 stat 与基线快照、确认与作废，
///         不跨 await 持锁。
///     </para>
///     <para>
///         Instance methods are safe to call concurrently: a simple lock guards baseline reads and
///         writes, keeping only the stat and baseline snapshot, confirmation, and invalidation inside the
///         lock, never held across an await.
///     </para>
/// </remarks>
public sealed class ProjectCatalogMonitor
{
    private readonly object                   _gate = new();
    private          ProjectCatalogFileState? _baseline;

    /// <summary>
    ///     <para>
    ///         检查目录册文件：stat 与已确认基线一致时返回 Unchanged 且不读文件；否则加载内容返回
    ///         Loaded；目录册损坏、共享冲突或权限不足等加载失败返回 Failed。结果中的
    ///         <see cref="ProjectCatalogCheckResult.FileState" /> 是加载前取的 stat 值。
    ///     </para>
    ///     <para>
    ///         Checks the catalog file: a stat matching the confirmed baseline returns Unchanged without
    ///         reading the file; otherwise the content loads and returns Loaded; a damaged catalog, a
    ///         sharing conflict, insufficient permissions, and similar load failures return Failed. The
    ///         result's <see cref="ProjectCatalogCheckResult.FileState" /> is the stat taken before the load.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         本方法不修改基线：Unchanged 与 Loaded 都需要调用方在成功处理结果后显式
    ///         <see cref="Confirm" />；Failed 无需确认，下一轮自然重试。stat 自身的元数据查询失败
    ///         同样按 Failed 呈现，此时 FileState 为未知的缺失形态默认值。
    ///     </para>
    ///     <para>
    ///         This method never changes the baseline: both Unchanged and Loaded require the caller to
    ///         <see cref="Confirm" /> explicitly after successfully processing the result; Failed needs no
    ///         confirmation and retries naturally next round. A failed metadata query of the stat itself
    ///         also reports Failed, with the FileState left as the unknown default missing shape.
    ///     </para>
    /// </remarks>
    /// <param name="storeDirectory">
    ///     <para>当前会话存储目录；目录册路径由 <see cref="ProjectStore.FilePath" /> 解析。</para>
    ///     <para>The current session-store directory; the catalog path is resolved by <see cref="ProjectStore.FilePath" />.</para>
    /// </param>
    /// <param name="cancellationToken">
    ///     <para>取消本次检查；取消以 <see cref="OperationCanceledException" /> 原样传播。</para>
    ///     <para>Cancels the check; cancellation propagates as <see cref="OperationCanceledException" />.</para>
    /// </param>
    /// <exception cref="ArgumentException">
    ///     <para>存储目录为空或空白。</para>
    ///     <para>The store directory is null or blank.</para>
    /// </exception>
    public async Task<ProjectCatalogCheckResult> CheckAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        var                      filePath = ProjectStore.FilePath(storeDirectory);
        ProjectCatalogFileState? stat;
        ProjectCatalogFileState? baseline;
        lock (_gate)
        {
            // stat 是纯元数据查询，与基线读取一起在锁内完成，不跨 await 持锁。
            stat     = StatCatalog(filePath);
            baseline = _baseline;
        }

        if (stat is not { } observed)
            return new ProjectCatalogCheckResult(ProjectCatalogCheckStatus.Failed, null, default);

        // 元数据与基线完全一致（含“都不存在”）时跳过读取：变更检测的快速路径。
        if (baseline is { } confirmed && observed == confirmed)
            return new ProjectCatalogCheckResult(ProjectCatalogCheckStatus.Unchanged, null, observed);

        try
        {
            var catalog = await ProjectStore.LoadAsync(filePath, cancellationToken).ConfigureAwait(false);
            return new ProjectCatalogCheckResult(ProjectCatalogCheckStatus.Loaded, catalog, observed);
        }
        catch (Exception ex) when (ex is ProjectStoreException or IOException or UnauthorizedAccessException)
        {
            // Failed 不改基线：下一轮 stat 仍与基线不同，自然重试；取消与其他异常原样传播。
            return new ProjectCatalogCheckResult(ProjectCatalogCheckStatus.Failed, null, observed);
        }
    }

    /// <summary>
    ///     <para>
    ///         记录已消费的文件状态基线：调用方成功处理一次 Loaded 结果（已应用，或确认内容未变而
    ///         跳过应用）后调用，之后 stat 与该状态完全一致的检查返回 Unchanged 而不再读取文件。
    ///     </para>
    ///     <para>
    ///         Records a consumed file-state baseline: call after successfully processing one Loaded result
    ///         (applied, or skipped because the content was confirmed unchanged); later checks whose stat
    ///         matches this state exactly return Unchanged without reading the file.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         通常传入最近一次 Loaded 结果的 <see cref="ProjectCatalogCheckResult.FileState" />；
    ///         应用被跳过时也必须 Confirm，否则同一变化每轮重复加载。
    ///     </para>
    ///     <para>
    ///         Pass the <see cref="ProjectCatalogCheckResult.FileState" /> of the most recent Loaded result;
    ///         Confirm even when the application was skipped, or the same change reloads every round.
    ///     </para>
    /// </remarks>
    /// <param name="fileState">
    ///     <para>要记为已消费基线的文件状态。</para>
    ///     <para>The file state to record as the consumed baseline.</para>
    /// </param>
    public void Confirm(ProjectCatalogFileState fileState)
    {
        lock (_gate)
        {
            _baseline = fileState;
        }
    }

    /// <summary>
    ///     <para>作废已确认基线；后续检查必然重新加载，用于生效 store 切换等场景。</para>
    ///     <para>
    ///         Invalidates the confirmed baseline; later checks always reload, for scenarios such as an
    ///         effective store switch.
    ///     </para>
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _baseline = null;
        }
    }

    /// <summary>
    ///     <para>读取目录册元数据快照；元数据查询失败时返回 null，由调用方按 Failed 呈现。</para>
    ///     <para>
    ///         Reads the catalog's metadata snapshot; a failed metadata query returns null for the caller to report as
    ///         Failed.
    ///     </para>
    /// </summary>
    private static ProjectCatalogFileState? StatCatalog(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            // 缺失文件的元数据不取自 FileInfo：各平台默认时间不同，统一归一化保证基线可比。
            return info.Exists
                ? new ProjectCatalogFileState(true, info.Length, info.LastWriteTimeUtc)
                : new ProjectCatalogFileState(false, 0, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
