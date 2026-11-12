using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         一次摘要构建的产物：摘要、捕获的日志字节上界与已扫描字节，供索引条件提交与对账使用。
///     </para>
///     <para>
///         One summary-build outcome: the summary, the captured log byte ceiling, and the scanned bytes, consumed by
///         conditional index commits and reconciliation.
///     </para>
/// </summary>
/// <param name="Summary">
///     <para>按最后一个完整有效批次构建的摘要。</para>
///     <para>The summary built through the last complete valid batch.</para>
/// </param>
/// <param name="SourceLength">
///     <para>构建开始时捕获的日志字节长度。</para>
///     <para>The log byte length captured at build start.</para>
/// </param>
/// <param name="ScannedBytes">
///     <para>本次构建实际扫描的字节数。</para>
///     <para>The bytes actually scanned by this build.</para>
/// </param>
/// <param name="SourceModifiedUtc">
///     <para>与字节长度同一次捕获的日志修改时间；作为失效线索与长度一并提交索引。</para>
///     <para>
///         The log modification time captured together with the byte length; committed to the index as a
///         staleness hint alongside the length.
///     </para>
/// </param>
public sealed record SessionSummaryBuild(
    SessionSummary Summary,
    long           SourceLength,
    long           ScannedBytes,
    DateTimeOffset SourceModifiedUtc);

/// <summary>
///     <para>
///         从会话 JSONL 构建列表摘要：流式扫描日志只提取标题、归属、时间与权限模式等列表元数据，
///         不物化完整 <see cref="PersistedSession" />。
///     </para>
///     <para>
///         Builds list summaries from session JSONL: a streaming scan extracts only list metadata such as title,
///         ownership, times, and permission mode, never materializing a full
///         <see cref="PersistedSession" />.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         结构与批次校验复用 <see cref="SessionLogScanner" />，因此摘要扫描的拒绝范围与严格读取一致；
///         但跨记录的协议边界校验（模型选择链、消息边界）仍只在完整恢复路径执行，
///         索引只验证其契约范围，不作为全日志已通过恢复校验的证明。半行结尾按严格扫描失败报告，
///         不产生半提交摘要。写入链路的增量更新通过 <see cref="Projection.SeedFromSummary" /> 加
///         <see cref="Projection.Apply" /> 完成，不重新扫描日志正文。
///     </para>
///     <para>
///         Structural and batch validation reuse <see cref="SessionLogScanner" />, so summary scans reject exactly
///         what strict reads reject; cross-record protocol-boundary checks (model-selection chains, message
///         boundaries) still run only on the full-recovery path, and the index validates its own contract without
///         standing in as proof that the whole log passed recovery validation. A partial-line tail is reported as a
///         strict-scan failure and never yields a half-committed summary. The write path's incremental updates go
///         through <see cref="Projection.SeedFromSummary" /> plus <see cref="Projection.Apply" /> without rescanning
///         log bodies.
///     </para>
/// </remarks>
public static class SessionSummaryBuilder
{
    /// <summary>
    ///     <para>侧栏标题的最大字符数；与 Desktop 会话行构造保持一致。</para>
    ///     <para>The sidebar title's maximum character count; kept identical to the Desktop row construction.</para>
    /// </summary>
    public const int MaxTitleCharacters = 48;

    /// <summary>
    ///     <para>严格扫描整份日志并构建摘要；日志不存在时返回 null，损坏或半行按既有扫描异常抛出。</para>
    ///     <para>
    ///         Strictly scans one log into a summary; null when the log is absent, with damage or a partial tail
    ///         surfacing as the existing scan exceptions.
    ///     </para>
    /// </summary>
    public static async Task<SessionSummaryBuild?> BuildAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                        nameof(sessionId));
        sessionId = sessionId.ToLowerInvariant();
        var path = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
        SessionStorePaths.EnsureStoreLeaf(directory, path);
        if (!File.Exists(path)) return null;
        var sourceInfo     = new FileInfo(path);
        var sourceLength   = sourceInfo.Length;
        var sourceModified = (DateTimeOffset)sourceInfo.LastWriteTimeUtc;
        var projection     = new Projection(sessionId);
        var scan = await SessionLogScanner.ScanAsync(path, sessionId, sourceLength, null, false,
                                                     (batch, _) =>
                                                     {
                                                         projection.Apply(batch);
                                                         return ValueTask.CompletedTask;
                                                     }, cancellationToken).ConfigureAwait(false);
        return new SessionSummaryBuild(projection.ToSummary(scan.LastSequence, scan.BytesRead), sourceLength,
                                       scan.BytesRead, sourceModified);
    }

    /// <summary>
    ///     <para>
    ///         从已提交批次推进摘要的流式投影：维护标题、归属、活动时间、权限模式与回合数等列表字段，
    ///         不保留对话或运行事实。
    ///     </para>
    ///     <para>
    ///         The streaming projection advancing a summary from committed batches: it maintains list fields such as
    ///         title, ownership, activity time, permission mode, and turn count without retaining conversation or run
    ///         facts.
    /// </summary>
    public sealed class Projection
    {
        private readonly string _sessionId;
        private SessionProjectAssignmentState _assignmentState = SessionProjectAssignmentState.PendingInitialization;
        private DateTimeOffset _createdUtc;
        private DateTimeOffset _lastActivityUtc;
        private string? _permissionModeName;
        private string? _projectId;
        private string? _title;
        private int _turnCount;
        private DateTimeOffset _updatedUtc;
        private string _workspaceRoot = string.Empty;

        /// <summary>
        ///     <para>为全新扫描创建投影。</para>
        ///     <para>Creates a projection for a fresh scan.</para>
        /// </summary>
        public Projection(string sessionId)
        {
            if (!SessionStorePaths.IsValidSessionId(sessionId))
                throw new ArgumentException("A session id must contain exactly 32 hexadecimal characters.",
                                            nameof(sessionId));
            sessionId        = sessionId.ToLowerInvariant();
            _sessionId       = sessionId;
            _title           = sessionId[..8];
            _createdUtc      = DateTimeOffset.MinValue;
            _updatedUtc      = DateTimeOffset.MinValue;
            _lastActivityUtc = DateTimeOffset.MinValue;
        }

        /// <summary>
        ///     <para>从既有摘要续种，供写入链路按已提交批次增量推进；不改写归档投影。</para>
        ///     <para>
        ///         Reseeds from an existing summary so the write path advances incrementally from committed batches;
        ///         the archived projection is never touched.
        ///     </para>
        /// </summary>
        /// <remarks>
        ///     <para>执行根随摘要一并恢复：增量批次不再携带 identity 记录，漏掉它会让后续摘要丢失执行根。</para>
        ///     <para>
        ///         The workspace root is restored with the summary: incremental batches no longer carry the identity
        ///         record, and missing it would drop the root from every later summary.
        ///     </para>
        /// </remarks>
        /// <exception cref="ArgumentException">
        ///     <para>摘要属于其他会话。</para>
        ///     <para>The summary belongs to a different session.</para>
        /// </exception>
        public void SeedFromSummary(SessionSummary summary)
        {
            ArgumentNullException.ThrowIfNull(summary);
            if (summary.Id != _sessionId)
                throw new ArgumentException("The seeded summary must belong to the projection's session.",
                                            nameof(summary));
            _workspaceRoot      = summary.WorkspaceRoot;
            _projectId          = summary.ProjectId;
            _assignmentState    = summary.ProjectAssignmentState;
            _permissionModeName = summary.PermissionModeName;
            _title              = summary.TurnCount > 0 ? summary.Title : summary.Id[..8];
            _turnCount          = summary.TurnCount;
            _createdUtc         = summary.CreatedUtc;
            _updatedUtc         = summary.UpdatedUtc;
            _lastActivityUtc    = summary.LastActivityUtc;
        }

        /// <summary>
        ///     <para>应用一个已校验批次；批次与 payload 只在调用完成前有效。</para>
        ///     <para>
        ///         Applies one validated batch; the batch and payloads are valid only until the call completes.
        ///     </para>
        /// </summary>
        public void Apply(SessionLogBatch batch)
        {
            _updatedUtc = batch.WrittenUtc;
            // 归属与命名维护不构成对话活动，避免后台任务改变侧栏排序；与完整恢复投影一致。
            if (batch.Records.Any(record => record.Kind is not ("project-binding" or "project-assignment" or
                                                                "project-unassignment" or "session-title")))
                _lastActivityUtc = batch.WrittenUtc;
            foreach (var record in batch.Records)
                switch (record.Kind)
                {
                    case "session-title" :
                        var name = record.Payload.Deserialize(SessionLogJsonContext.Default.SessionTitle)
                                ?? throw new SessionStoreException("Missing session title state.");
                        name.Validate();
                        if (name.Name is not null) _title = name.Name;
                        break;
                    case "identity" :
                        var identity = record.Payload.Deserialize(SessionLogJsonContext.Default.SessionLogIdentity)
                                    ?? throw new SessionStoreException(
                                                                       $"Session '{_sessionId}' has an empty identity payload.");
                        if (identity.Id != _sessionId)
                            throw new SessionStoreException(
                                                            $"Session log identity '{identity.Id}' does not match file session '{_sessionId}'.");
                        _workspaceRoot = identity.WorkspaceRoot;
                        _createdUtc    = identity.CreatedUtc;
                        break;
                    case "turn" :
                        var turn = record.Payload.Deserialize(SessionLogJsonContext.Default.SessionLogTurn)
                                ?? throw new SessionStoreException(
                                                                   $"Session '{_sessionId}' has an empty turn payload.");
                        _turnCount++;
                        if (_turnCount == 1) _title = SummarizeTitle(turn.UserInput);
                        break;
                    case "permission-selection" :
                        var permissionSchema = SessionStoreWriter.ReadSchemaName(record.Payload);
                        if (permissionSchema == SessionPermissionSelectionV2.CurrentSchema)
                        {
                            var selection =
                                record.Payload.Deserialize(PermissionPersistenceJsonContext.Default
                                                              .SessionPermissionSelectionV2)
                             ?? throw new SessionStoreException(
                                                                $"Session '{_sessionId}' has an empty permission-selection payload.");
                            selection.Validate();
                            _permissionModeName = selection.ApprovalMode.ToString();
                        }
                        else
                        {
                            var selection =
                                record.Payload.Deserialize(PermissionPersistenceJsonContext.Default
                                                              .LegacyPermissionSelection)
                             ?? throw new SessionStoreException(
                                                                $"Session '{_sessionId}' has an empty permission-selection payload.");
                            selection.Validate();
                            _permissionModeName = selection.Mode;
                        }
                        break;
                    case "project-binding" :
                        _projectId       = SessionLogProjectBinding.Parse(record.Payload);
                        _assignmentState = SessionProjectAssignmentState.Project;
                        break;
                    case "project-assignment" :
                        SessionLogProjectAssignment.ValidateNoProject(record.Payload);
                        _assignmentState = SessionProjectAssignmentState.NoProject;
                        break;
                    case "project-unassignment" :
                        var unassigned = SessionLogProjectUnassignment.Parse(record.Payload);
                        if (_projectId != unassigned)
                            throw new SessionStoreException(
                                                            $"Session '{_sessionId}' contains a project-unassignment record that does not match its current project.");
                        _projectId       = null;
                        _assignmentState = SessionProjectAssignmentState.NoProject;
                        break;
                }
        }

        /// <summary>
        ///     <para>生成到指定批次序号与字节边界为止的摘要。</para>
        ///     <para>Produces the summary through the given batch sequence and byte boundary.</para>
        /// </summary>
        public SessionSummary ToSummary(long commitSequence, long indexedBytes)
        {
            if (_updatedUtc == DateTimeOffset.MinValue)
                throw new SessionStoreException($"Session '{_sessionId}' has no valid identity record.");
            if (_createdUtc == DateTimeOffset.MinValue) _createdUtc = _updatedUtc;
            return new SessionSummary
            {
                Id                     = _sessionId,
                WorkspaceRoot          = _workspaceRoot,
                ProjectId              = _projectId,
                ProjectAssignmentState = _assignmentState,
                Title                  = _title ?? _sessionId[..8],
                CreatedUtc             = _createdUtc,
                UpdatedUtc             = _updatedUtc,
                LastActivityUtc        = _lastActivityUtc,
                TurnCount              = _turnCount,
                PermissionModeName     = _permissionModeName,
                CommitSequence         = commitSequence,
                IndexedBytes           = indexedBytes
            };
        }

        /// <summary>
        ///     <para>按侧栏规则截取标题：输入首行，最多 48 字符；空白输入回退为会话 ID 前 8 位。</para>
        ///     <para>
        ///         Summarizes the title by the sidebar rule: the input's first line, 48 characters at most; blank
        ///         input falls back to the first 8 id characters.
        ///     </para>
        /// </summary>
        private string SummarizeTitle(string userInput)
        {
            var title = string.IsNullOrWhiteSpace(userInput)
                ? _sessionId[..8]
                : userInput.Split('\n')[0];
            return title[..Math.Min(title.Length, MaxTitleCharacters)];
        }
    }
}
