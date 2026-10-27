using System.Text.Json;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>单次会话归属初始化的结果状态：本次追加绑定、已保持既有绑定、writer 被占用待重试，或读取/追加失败。</para>
///     <para>Per-session ownership-initialization outcomes: newly bound this run, existing binding kept, writer held for a later retry, or a read or append failure.</para>
/// </summary>
public enum SessionProjectBindingStatus
{
    Bound,
    AlreadyBound,
    Pending,
    Failed
}

/// <summary>
///     <para>一个会话的初始化事实：会话 ID、保存的执行根、结果状态、项目 ID 与不含凭据的失败/待处理原因。</para>
///     <para>One session's initialization fact: session id, saved workspace root, outcome, project id, and a credential-free pending or failure reason.</para>
/// </summary>
public sealed record SessionProjectBindingOutcome(
    string                      SessionId,
    string                      WorkspaceRoot,
    SessionProjectBindingStatus Status,
    string?                     ProjectId,
    string?                     Detail);

/// <summary>
///     <para>本次运行创建或复用的项目事实。</para>
///     <para>A project created or reused by this run.</para>
/// </summary>
public sealed record SessionProjectReference(string ProjectId, string PrimaryRoot);

/// <summary>
///     <para>
///         一次初始化运行的整体结果：是否因取消提前结束、续扫游标与是否还有未检查日志、逐会话事实及本次涉及的项目。
///     </para>
///     <para>
///         The aggregate result of one initialization run: whether it ended early on cancellation, the continuation
///         cursor with whether unexamined logs remain, the per-session facts, and the projects involved this run.
///     </para>
/// </summary>
public sealed record SessionProjectInitializationResult(
    bool                                        Cancelled,
    string?                                     NextCursor,
    bool                                        HasMore,
    IReadOnlyList<SessionProjectBindingOutcome> Outcomes,
    IReadOnlyList<SessionProjectReference>      Projects);

/// <summary>
///     <para>
///         幂等的旧日志项目归属初始化服务：有界扫描当前 store 的统一日志，为未绑定会话按保存的执行根创建或复用项目，
///         并在排他 writer 的重读边界内仅追加一条归属记录。
///     </para>
///     <para>
///         An idempotent initializer that binds legacy unified logs to projects: it scans the current store within a
///         bounded batch, creates or reuses one project per saved execution root for unbound sessions, and appends
///         exactly one ownership record inside an exclusive writer's re-read boundary.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         本服务是唯一显式入口，不被 reader、历史分页或列举路径隐式调用；日志 JSONL 是归属的唯一权威事实，
///         目录册只保存项目定义。扫描按会话 ID 顺序逐日志读取并在处理完立即释放投影，只在内存保留会话 ID 与
///         路径元数据；<paramref name="maxSessions" /> 约束本次实际检查的日志数量，配合
///         <paramref name="afterSessionId" /> 与 <see cref="SessionProjectInitializationResult.NextCursor" /> 续扫，
///         使靠前的 Pending 条目不会饿死后续批次，末批结束后可从头上重试。损坏、截断、超预算或非法身份的日志
///         单独报告，不修复、不截断也不跳过关键事实后追加；非法或相对执行根按失败报告，不改用进程目录推测。
///     </para>
///     <para>
///         This service is the single explicit entry point and is never invoked implicitly by readers, history paging,
///         or listing; the JSONL log stays the only authoritative ownership fact while the catalog stores project
///         definitions only. Scanning reads one log at a time in session-id order and releases each projection right
///         after processing, keeping only session-id and path metadata in memory;
///         <paramref name="maxSessions" /> bounds how many logs this run examines, and together with
///         <paramref name="afterSessionId" /> and <see cref="SessionProjectInitializationResult.NextCursor" /> the caller
///         resumes so an early Pending entry never starves later batches, and after the final batch the head can be
///         retried. Damaged, truncated, over-budget, or invalid-identity logs are reported individually without repair,
///         truncation, or appending past skipped facts; an invalid or relative execution root fails its own item instead
///         of being guessed against the process directory.
///     </para>
///     <para>
///         先保存项目并释放目录册锁，再逐会话获取 writer 重读；重读已绑定的会话保持原值，即使目录册缺失也不重建换绑。
///         目录册的存储、IO 或权限错误按候选逐项记为失败，不中断其他条目；目录枚举自身的故障作为整体异常抛出，
///         不被吞并。被占用的 writer 只标记待处理，不抢锁，本进程宿主持有时同样待处理，由原宿主在其安全空闲边界处理。
///     </para>
///     <para>
///         Projects are saved and the catalog lock released before any writer is taken; a re-read session found already
///         bound keeps its value even when the catalog is missing, without rebuilding or rebinding. Catalog storage, IO,
///         or permission failures mark their candidates failed item by item without interrupting the rest, while a failure
///         of the directory enumeration itself propagates as one explicit exception instead of being swallowed. A held
///         writer is only marked pending — never lock-stolen, including holds by a host in this process, which the
///         original host handles at its own safe boundary.
///     </para>
///     <para>
///         取消令牌传入归属追加：追加前与末条后都反映取消，取消按既有 writer 契约传播（写盘中途取消会成为 poison 的
///         不确定写入）。追加抛出的失败在令牌已取消时保留为该会话的 Failed 事实并置
///         <see cref="SessionProjectInitializationResult.Cancelled" /> 后停止；直接传播的取消同样置 Cancelled 并返回
///         已完成事实，不吞为 Failed，本轮均不重试。失败明细只来自结构化的存储异常消息，不解析凭据、不创建模型
///         客户端、不发模型请求、不执行旧工具，也不恢复或清除 NeedsInspection。
///     </para>
///     <para>
///         The cancellation token is passed into the ownership append: cancellation is reflected both before each append
///         and after the final item, propagating under the existing writer contract (a mid-write cancellation becomes a
///         poisoned uncertain write). When an append fails while the token is already cancelled, the failure is kept as
///         that session's Failed fact, <see cref="SessionProjectInitializationResult.Cancelled" /> is set, and the run
///         stops; a directly propagating cancellation likewise sets Cancelled with the completed facts instead of
///         swallowing it as Failed, and nothing is retried this run. Failure details come only from structured
///         store-exception messages; credentials are never parsed, model clients created, models requested, legacy tools
///         executed, and NeedsInspection is never restored or cleared.
///     </para>
/// </remarks>
public static class SessionProjectInitializer
{
    /// <summary>
    ///     <para>
    ///         初始化当前 store 的未绑定会话：按会话 ID 顺序有界检查统一日志，逐条报告读取失败、既有绑定与非法根，
    ///         未绑定候选按根分组创建或复用项目，再逐会话排他 writer 重读后仅追加归属。
    ///     </para>
    ///     <para>
    ///         Initializes the store's unbound sessions: examines unified logs in bounded session-id order, reporting
    ///         per-entry read failures, existing bindings, and invalid roots; unbound candidates are grouped by root to
    ///         create or reuse projects, then ownership is appended per session behind an exclusive writer after
    ///         re-reading.
    ///     </para>
    /// </summary>
    /// <param name="storeDirectory">
    ///     <para>受保护会话存储目录。</para>
    ///     <para>The protected session-store directory.</para>
    /// </param>
    /// <param name="afterSessionId">
    ///     <para>续扫游标：仅检查按序大于该 ID 的日志；null 从头开始。</para>
    ///     <para>Continuation cursor: only logs ordered after this id are examined; null starts from the head.</para>
    /// </param>
    /// <param name="maxSessions">
    ///     <para>本次实际检查的最大日志数；null 不限制，剩余部分由 HasMore/NextCursor 指示。</para>
    ///     <para>The maximum number of logs examined this run; null means no limit, with the remainder indicated by HasMore/NextCursor.</para>
    /// </param>
    /// <param name="utcNow">
    ///     <para>创建项目时使用的时间；null 使用当前时间。</para>
    ///     <para>The timestamp used when creating projects; null uses the current time.</para>
    /// </param>
    /// <returns>
    ///     <para>逐会话事实、项目事实、取消标记、续扫游标与剩余指示；未检查的日志不在 Outcomes 中。</para>
    ///     <para>Per-session facts, project facts, cancellation, the continuation cursor, and the remaining flag; unexamined logs are absent from Outcomes.</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    ///     <para>存储目录为空。</para>
    ///     <para>The store directory is null or empty.</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <para>检查上限小于 1。</para>
    ///     <para>The examination limit is below one.</para>
    /// </exception>
    /// <exception cref="SessionStoreException">
    ///     <para>目录枚举整体失败。</para>
    ///     <para>The directory enumeration itself failed.</para>
    /// </exception>
    public static Task<SessionProjectInitializationResult> InitializeAsync(
        string storeDirectory, string? afterSessionId = null, int? maxSessions = null, DateTimeOffset? utcNow = null,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(storeDirectory, afterSessionId, maxSessions, utcNow, ownershipAppend: null,
                        cancellationToken);

    /// <summary>
    ///     <para>
    ///         带每次调用独立归属追加实现的初始化入口；<paramref name="ownershipAppend" /> 为 null 时使用默认
    ///         project-binding 追加。仅供同程序集测试注入断点，不构成共享或公开的注入框架。
    ///     </para>
    ///     <para>
    ///         Initialization entry with a per-call ownership-append implementation; a null
    ///         <paramref name="ownershipAppend" /> uses the default project-binding append. It exists for same-assembly
    ///         test breakpoints only and is neither shared state nor a public injection framework.
    ///     </para>
    /// </summary>
    internal static async Task<SessionProjectInitializationResult> InitializeAsync(
        string storeDirectory, string? afterSessionId, int? maxSessions, DateTimeOffset? utcNow,
        Func<string, SessionStoreWriter, string, CancellationToken, Task>? ownershipAppend,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        if (maxSessions is < 1)
            throw new ArgumentOutOfRangeException(nameof(maxSessions),
                                                  "The maximum number of sessions to examine must be at least one.");

        var outcomes  = new List<SessionProjectBindingOutcome>();
        var projects  = new List<SessionProjectReference>();
        var cancelled = false;

        if (cancellationToken.IsCancellationRequested)
            return new SessionProjectInitializationResult(true, afterSessionId, true, [], []);

        // 文件名枚举与排序是全量轻量操作（只取 ID 元数据）；有界的是逐日志正文读取。
        List<string> orderedIds;
        try
        {
            var ids = new List<string>();
            foreach (var path in Directory.EnumerateFiles(storeDirectory, "*" + SessionStoreWriter.LogFileSuffix))
            {
                // 枚举阶段每个条目都响应取消。
                cancellationToken.ThrowIfCancellationRequested();
                ids.Add(Path.GetFileName(path)[..^SessionStoreWriter.LogFileSuffix.Length]);
            }

            ids.Sort(StringComparer.Ordinal);
            orderedIds = ids;
        }
        catch (OperationCanceledException)
        {
            return new SessionProjectInitializationResult(true, afterSessionId, true, [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SessionStoreException(
                $"Cannot enumerate the session store directory '{Path.GetFullPath(storeDirectory)}': {ex.Message}", ex);
        }

        var eligible = orderedIds.Where(id => afterSessionId is null ||
                                             string.CompareOrdinal(id, afterSessionId) > 0)
                                 .ToList();
        var examined        = maxSessions is { } limit ? eligible.Take(limit).ToList() : eligible;
        var moreFilesRemain = examined.Count < eligible.Count;

        try
        {
            var candidates = new List<(string SessionId, string WorkspaceRoot)>();
            foreach (var sessionId in examined)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                // 单日志严格读取：处理完立即释放投影，只保留结果元数据。
                var snapshot = await ReadSessionSnapshotAsync(storeDirectory, sessionId, cancellationToken);
                if (snapshot.Error is not null)
                {
                    outcomes.Add(new SessionProjectBindingOutcome(sessionId, string.Empty,
                                                                  SessionProjectBindingStatus.Failed, null,
                                                                  snapshot.Error));
                }
                else if (snapshot.Session!.ProjectId is { } boundProjectId)
                {
                    // 已绑定条目保持原值；即使目录册缺失也不重建换绑，本服务不为其执行任何写入。
                    outcomes.Add(new SessionProjectBindingOutcome(sessionId, snapshot.Session.WorkspaceRoot,
                                                                  SessionProjectBindingStatus.AlreadyBound,
                                                                  boundProjectId, null));
                }
                else if (RootValidationError(snapshot.Session.WorkspaceRoot) is { } rootError)
                {
                    outcomes.Add(new SessionProjectBindingOutcome(sessionId, snapshot.Session.WorkspaceRoot,
                                                                  SessionProjectBindingStatus.Failed, null,
                                                                  rootError));
                }
                else
                {
                    candidates.Add((sessionId, snapshot.Session.WorkspaceRoot));
                }
            }

            // 先创建或复用项目并释放目录册锁，再进入会话 writer 边界；目录册错误按候选逐项失败，不覆盖目录册。
            var rootComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var projectsByRoot = new Dictionary<string, Project>(rootComparer);
            var boundCandidates = new List<(string SessionId, string WorkspaceRoot, string NormalizedRoot)>();
            foreach (var group in candidates.GroupBy(candidate => ProjectRoots.Normalize(candidate.WorkspaceRoot),
                                                     rootComparer))
            {
                Project? project = null;
                try
                {
                    project = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(storeDirectory), group.Key,
                                                                  utcNow, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ProjectStoreException or IOException or UnauthorizedAccessException)
                {
                    foreach (var candidate in group)
                        outcomes.Add(new SessionProjectBindingOutcome(candidate.SessionId, candidate.WorkspaceRoot,
                                                                      SessionProjectBindingStatus.Failed, null,
                                                                      ex.Message));
                    continue;
                }

                projectsByRoot[group.Key] = project;
                projects.Add(new SessionProjectReference(project.Id, project.PrimaryRoot));
                boundCandidates.AddRange(group.Select(candidate =>
                                                          (candidate.SessionId, candidate.WorkspaceRoot, group.Key)));
            }

            foreach (var (sessionId, scannedRoot, normalizedRoot) in boundCandidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var open = await SessionStoreWriter.TryOpenExistingAsync(storeDirectory, sessionId, null,
                                                                          cancellationToken).ConfigureAwait(false);
                if (open.Status != SessionWriterOpenStatus.Opened)
                {
                    // 锁占用（含本进程宿主）仅待处理；其余打开或严格读取失败单独报告，不修复。
                    var status = open.Status == SessionWriterOpenStatus.LockHeld
                        ? SessionProjectBindingStatus.Pending
                        : SessionProjectBindingStatus.Failed;
                    outcomes.Add(new SessionProjectBindingOutcome(sessionId, scannedRoot, status, null,
                                                                  open.Error!.Message));
                    continue;
                }

                using (var writer = open.Writer!)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    var reloaded = writer.LoadedSnapshot!;
                    if (reloaded.ProjectId is { } existingProjectId)
                    {
                        // 重读已绑定即保留现值，不按过期扫描结果换绑。
                        outcomes.Add(new SessionProjectBindingOutcome(sessionId, reloaded.WorkspaceRoot,
                                                                      SessionProjectBindingStatus.AlreadyBound,
                                                                      existingProjectId, null));
                        continue;
                    }

                    if (RootValidationError(reloaded.WorkspaceRoot) is { } reloadedRootError)
                    {
                        outcomes.Add(new SessionProjectBindingOutcome(sessionId, reloaded.WorkspaceRoot,
                                                                      SessionProjectBindingStatus.Failed, null,
                                                                      reloadedRootError));
                        continue;
                    }

                    if (!ProjectRoots.AreEqual(ProjectRoots.Normalize(reloaded.WorkspaceRoot), normalizedRoot))
                    {
                        outcomes.Add(new SessionProjectBindingOutcome(
                                         sessionId, reloaded.WorkspaceRoot, SessionProjectBindingStatus.Failed, null,
                                         $"The session's saved workspace root changed since the scan ('{reloaded.WorkspaceRoot}'); it was not bound to the scanned project."));
                        continue;
                    }

                    var projectId = projectsByRoot[normalizedRoot].Id;
                    try
                    {
                        await AppendOwnershipAsync(ownershipAppend, sessionId, writer, projectId,
                                                   cancellationToken).ConfigureAwait(false);
                        outcomes.Add(new SessionProjectBindingOutcome(sessionId, reloaded.WorkspaceRoot,
                                                                      SessionProjectBindingStatus.Bound, projectId,
                                                                      null));
                    }
                    catch (OperationCanceledException)
                    {
                        // 写盘前取消：本次未写入任何字节，无失败事实，仅停止本轮。
                        cancelled = true;
                        break;
                    }
                    catch (SessionStoreException ex)
                    {
                        // 追加失败（含 poison 的不确定写入）当轮不重试；令牌已取消时保留失败事实并停止。
                        outcomes.Add(new SessionProjectBindingOutcome(sessionId, reloaded.WorkspaceRoot,
                                                                      SessionProjectBindingStatus.Failed, projectId,
                                                                      ex.Message));
                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }
                    }
                }
            }

            // 末条之后同样反映取消：即使全部处理完，也已观察到取消请求。
            if (cancellationToken.IsCancellationRequested) cancelled = true;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // 游标只推进到“本轮已有事实的已处理连续 ID 前缀”：分组乱序处理不得越过无事实的空洞，
        // 直接取消且未完成（无结果）的会话不能被跨过；仍有未处理项时 HasMore 为真。
        var factIds    = outcomes.Select(outcome => outcome.SessionId).ToHashSet(StringComparer.Ordinal);
        var nextCursor = afterSessionId;
        foreach (var sessionId in examined)
        {
            if (!factIds.Contains(sessionId)) break;
            nextCursor = sessionId;
        }

        var hasUnprocessed = examined.Any(sessionId => !factIds.Contains(sessionId));
        return new SessionProjectInitializationResult(cancelled,
                                                      nextCursor,
                                                      moreFilesRemain || hasUnprocessed,
                                                      outcomes.OrderBy(outcome => outcome.SessionId,
                                                                       StringComparer.Ordinal).ToArray(),
                                                      projects);
    }

    /// <summary>
    ///     <para>
    ///         按会话执行归属追加；生产实现只提交一条 project-binding 记录并传入调用方取消令牌，
    ///         测试可通过每次调用独立的委托复现初始化层断点。
    ///     </para>
    ///     <para>
    ///         Appends the session's project ownership; the production path commits exactly one project-binding record
    ///         with the caller's cancellation token; tests may supply a per-call delegate to reproduce breakpoints.
    ///     </para>
    /// </summary>
    private static Task AppendOwnershipAsync(
        Func<string, SessionStoreWriter, string, CancellationToken, Task>? ownershipAppend,
        string sessionId, SessionStoreWriter writer, string projectId, CancellationToken cancellationToken)
    {
        if (ownershipAppend is { } injected)
            return injected(sessionId, writer, projectId, cancellationToken);
        return writer.AppendRecordsAsync(
        [
            new SessionLogRecord(string.Empty, "project-binding",
                                 JsonSerializer.SerializeToElement(
                                     new SessionLogProjectBinding(SessionLogProjectBinding.SchemaName, projectId),
                                     SessionLogJsonContext.Default.SessionLogProjectBinding))
        ], cancellationToken);
    }

    /// <summary>
    ///     <para>读取单个日志快照；预期格式与存储异常转为失败消息，取消原样传播。</para>
    ///     <para>Reads one log snapshot; expected format and storage exceptions become failure messages while cancellation propagates.</para>
    /// </summary>
    private static async Task<(PersistedSession? Session, string? Error)> ReadSessionSnapshotAsync(
        string storeDirectory, string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await SessionStoreReader.ReadAsync(storeDirectory, sessionId, cancellationToken)
                                                  .ConfigureAwait(false);
            return session is null
                ? (null, $"Session log '{sessionId}' disappeared before it could be read.")
                : (session, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // 覆盖严格读取实际可能逃逸的异常族：结构化 SessionStoreException、
        // JsonElement 解码/反序列化的 JsonException 与 InvalidOperationException、存储 IO 与权限错误。
        catch (Exception ex) when (SessionStoreWriter.IsRecoverableOpenFailure(ex))
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    ///     <para>校验保存的执行根：空白、相对或无法规范化的根返回错误，不改用进程目录推测。</para>
    ///     <para>Validates the saved workspace root: blank, relative, or unnormalizable roots return an error instead of being guessed against the process directory.</para>
    /// </summary>
    private static string? RootValidationError(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
            return "The session's saved workspace root is empty.";
        if (!Path.IsPathFullyQualified(workspaceRoot))
            return $"The session's saved workspace root '{workspaceRoot}' is not an absolute path; it was not resolved against the process directory.";
        try
        {
            ProjectRoots.Normalize(workspaceRoot);
        }
        catch (Exception ex) when (ex is ProjectStoreException or ArgumentException or InvalidOperationException or
                                       PathTooLongException or NotSupportedException)
        {
            return $"The session's saved workspace root '{workspaceRoot}' cannot be normalized: {ex.Message}";
        }

        return null;
    }
}
