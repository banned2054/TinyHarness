using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>解析受保护会话存储路径，校验工作区边界与会话标识。</para>
///     <para>
///         Resolves protected session-store paths
///         and validates workspace boundaries and session identifiers.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         显式目录必须为绝对路径；词法路径和链接解析结果均不得落入工作区。
///         沙箱目标命令可写的位置不能存放可续聊会话。
///     </para>
///     <para>
///         Explicit directories must be absolute; both lexical and link-resolved paths
///         must stay outside the workspace. Resumable sessions must not reside where sandboxed commands can write.
///     </para>
/// </remarks>
public static class SessionStorePaths
{
    /// <summary>
    ///     <para>覆盖默认会话存储目录的环境变量，供便携部署与测试使用。</para>
    ///     <para>
    ///         Environment variable overriding the default session-store directory
    ///         for portable setups and tests.
    ///     </para>
    /// </summary>
    public const string DirectoryOverrideVariable = "TINYHARNESS_SESSION_STORE_DIR";

    /// <summary>
    ///     <para>按环境覆盖与平台约定选择默认会话存储目录。</para>
    ///     <para>
    ///         Selects the default session-store directory
    ///         using the environment override and platform conventions.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         优先环境覆盖；Windows 使用 %LOCALAPPDATA%\tinyharness\sessions，
    ///         其他平台使用 XDG_DATA_HOME 或 ~/.local/share。
    ///         用户目录不可用时退回当前目录下的 tinyharness\sessions；受保护边界由调用方另行校验。
    ///     </para>
    ///     <para>
    ///         The environment override takes precedence; Windows uses %LOCALAPPDATA%\tinyharness\sessions,
    ///         while other platforms use XDG_DATA_HOME or ~/.local/share.
    ///         Unavailable user directories fall back to tinyharness\sessions under the current directory;
    ///         callers validate protected boundaries separately.
    ///     </para>
    /// </remarks>
    public static string DefaultDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(DirectoryOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
            return Path.GetFullPath(overrideDirectory.Trim());

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData)) return Path.Combine(localAppData, "tinyharness", "sessions");
        }
        else
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(dataHome))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home)) dataHome = Path.Combine(home, ".local", "share");
            }

            if (!string.IsNullOrWhiteSpace(dataHome)) return Path.Combine(dataHome, "tinyharness", "sessions");
        }

        return Path.GetFullPath(Path.Combine("tinyharness", "sessions"));
    }

    /// <summary>
    ///     <para>解析并校验工作区之外的会话存储目录，不创建目录。</para>
    ///     <para>
    ///         Resolves and validates a session-store directory outside the workspace
    ///         without creating it.
    ///     </para>
    /// </summary>
    /// <param name="configuredDirectory">
    ///     <para>显式绝对路径；未配置时使用默认目录。</para>
    ///     <para>An explicit absolute path; the default directory is used when unset.</para>
    /// </param>
    /// <returns>
    ///     <para>通过 <see cref="EnsureOutsideWorkspace" /> 校验的规范化绝对路径。</para>
    ///     <para>The normalized absolute path validated by <see cref="EnsureOutsideWorkspace" />.</para>
    /// </returns>
    public static string ResolveProtected(string? configuredDirectory, string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        string directory;
        if (string.IsNullOrWhiteSpace(configuredDirectory))
        {
            directory = DefaultDirectory();
        }
        else
        {
            var trimmed = configuredDirectory.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
                throw new
                    SessionStoreException($"The session store directory '{configuredDirectory}' must be an absolute path " +
                                          "(settings.sessionStoreDirectory); a relative location depends on the launch " +
                                          "directory and is not accepted for resumable session storage.");

            directory = Path.GetFullPath(trimmed);
        }

        // 错误提示须指出目录来源，避免环境覆盖导致用户修改错误的配置。
        EnsureOutsideWorkspace(directory, workspaceRoot,
                               string.IsNullOrWhiteSpace(configuredDirectory)
                                   ? $"the '{DirectoryOverrideVariable}' environment variable or settings.sessionStoreDirectory"
                                   : "settings.sessionStoreDirectory");
        return directory;
    }

    /// <summary>
    ///     <para>拒绝词法路径或符号链接/junction 解析结果位于工作区内的存储目录。</para>
    ///     <para>
    ///         Rejects store directories whose lexical or symlink/junction-resolved paths
    ///         lie inside the workspace.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>逐级解析已存在路径段，供存储解析与沙箱组合守卫共用。</para>
    ///     <para>
    ///         Resolves existing path segments individually;
    ///         shared by store resolution and the sandbox composition guard.
    ///     </para>
    /// </remarks>
    public static void EnsureOutsideWorkspace(string directory, string workspaceRoot, string settingName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        var full = Path.GetFullPath(directory);
        if (Workspace.IsInside(workspaceRoot, full))
            throw new
                SessionStoreException($"The session store directory '{full}' lies inside the workspace '{workspaceRoot}'; " +
                                      "sandboxed commands could tamper with session snapshots and audit records. " +
                                      $"Move it outside the workspace ({settingName}) before storing resumable sessions.");

        var resolved = ResolveExistingAncestors(full);
        if (!ReferenceEquals(resolved, full) && Workspace.IsInside(workspaceRoot, resolved))
            throw new
                SessionStoreException($"The session store directory '{full}' resolves through links into the workspace " +
                                      $"'{workspaceRoot}' ('{resolved}'); sandboxed commands could tamper with session " +
                                      $"snapshots and audit records. Move it outside the workspace ({settingName}).");
    }

    /// <summary>
    ///     <para>校验会话 ID 是否为 32 位十六进制标识符，而非路径片段。</para>
    ///     <para>
    ///         Validates a session ID as a 32-character hexadecimal identifier,
    ///         never a path fragment.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>读写端共用此校验以排除路径逃逸与通配；输入大小写不敏感，盘上统一为小写。</para>
    ///     <para>
    ///         Readers and writers share this check to exclude path escapes and wildcards;
    ///         input is case-insensitive and the on-disk form is lowercase.
    ///     </para>
    /// </remarks>
    public static bool IsValidSessionId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);

    /// <summary>
    ///     <para>从文件系统根向下解析已存在路径段的链接目标。</para>
    ///     <para>Resolves link targets of existing path segments from the filesystem root down.</para>
    /// </summary>
    /// <remarks>
    ///     <para>遇到不存在的段即返回当前路径；链接解析失败时保留当前词法路径，外层词法检查仍适用。</para>
    ///     <para>
    ///         Returns the current path at the first nonexistent segment;
    ///         failed link resolution retains the current lexical path, still subject to the outer lexical check.
    ///     </para>
    /// </remarks>
    private static string ResolveExistingAncestors(string directory)
    {
        var root      = Path.GetPathRoot(directory);
        var remainder = root is null ? directory : Path.GetRelativePath(root, directory);
        if (root is null || string.IsNullOrEmpty(remainder)) return directory;

        var current  = root;
        var segments = remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..") return directory;

            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current)) return current;

            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                var            target = info.ResolveLinkTarget(true);
                if (target is not null) current = target.FullName;
            }
            catch (IOException)
            {
                // 无法解析的链接保留词法路径，调用方的词法检查仍适用。
            }
        }

        return current;
    }
}

/// <summary>
///     <para>排他写入版本化会话快照并追加提交审计。</para>
///     <para>Exclusively writes versioned session snapshots and appends commit audit records.</para>
/// </summary>
/// <remarks>
///     <para>
///         调用方在完整回合或已验证的安全边界提交。
///         v3 原样保存完整协议历史与压缩状态，可能包含秘密；回合/运行检查投影保持脱敏。
///         保存失败必须报告“本次未可靠保存”，运行终态不证明落盘成功。
///     </para>
///     <para>
///         Callers commit at complete-turn or verified safe boundaries.
///         Format v3 preserves full protocol history and compaction state verbatim and may contain secrets;
///         turn/run inspection projections remain redacted.
///         Save failures must be reported as "not reliably saved"; a terminal run does not prove persistence.
///     </para>
/// </remarks>
public interface ISessionStoreWriter : IDisposable
{
    /// <summary>
    ///     <para>快照文件的绝对路径。</para>
    ///     <para>The absolute path of the snapshot file.</para>
    /// </summary>
    string SnapshotPath { get; }

    /// <summary>
    ///     <para>最近一次成功提交的序号；首次提交前为 0。</para>
    ///     <para>The last successfully committed sequence; zero before the first commit.</para>
    /// </summary>
    long CommitSequence { get; }

    /// <summary>
    ///     <para>原子替换当前会话快照，追加审计后递增提交序号。</para>
    ///     <para>
    ///         Atomically replaces the current session snapshot
    ///         and advances the commit sequence after appending the audit.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         快照包含检查视图与 canonical 对话；调用方须先保证对话位于完整协议边界。
    ///         快照替换后不再响应取消，继续尝试写入审计以完成一致提交。
    ///     </para>
    ///     <para>
    ///         The snapshot includes inspection views and the canonical conversation;
    ///         callers must first ensure the conversation ends at a complete protocol boundary.
    ///         After replacement, audit writing proceeds independently of cancellation to complete a consistent commit.
    ///     </para>
    /// </remarks>
    /// <exception cref="SessionStoreException">
    ///     <para>快照超限、写入失败或审计追加失败；本次保存不能视为可靠成功。</para>
    ///     <para>
    ///         The snapshot exceeds its limit, writing fails, or audit appending fails;
    ///         the save cannot be considered reliably successful.
    ///     </para>
    /// </exception>
    Task CommitAsync(ChatSession session, ConversationContext context, CancellationToken cancellationToken);
}

/// <summary>
///     <para>通过文件锁、原子快照替换与提交审计实现排他会话存储。</para>
///     <para>
///         Implements exclusive session storage
///         using a file lock, atomic snapshot replacement, and commit auditing.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         独占持有 <c>&lt;id&gt;.lock</c> 至 <see cref="Dispose" />；同进程或跨进程的第二个写入者立即失败。
///         提交顺序为同目录临时文件、原子替换、追加审计。
///         替换前失败保留上一快照；替换后审计失败留下的不一致由读取端对账检出。
///     </para>
///     <para>
///         Holds <c>&lt;id&gt;.lock</c> exclusively until <see cref="Dispose" />;
///         a second writer in the same or another process fails immediately.
///         Commits use a same-directory temporary file, atomic replacement, then audit appending.
///         Pre-replacement failures retain the previous snapshot;
///         post-replacement audit failures leave divergence detected by reader reconciliation.
///     </para>
/// </remarks>
public sealed class SessionStoreWriter : ISessionStoreWriter
{
    /// <summary>
    ///     <para>会话快照的读写大小上限，防止失控会话耗尽存储与查看资源。</para>
    ///     <para>
    ///         Snapshot size limit for reading and writing,
    ///         preventing runaway sessions from exhausting storage and inspection resources.
    ///     </para>
    /// </summary>
    public const int MaxSnapshotBytes = 16 * 1024 * 1024;

    private readonly FileStream _lockStream;

    private readonly SecretRedactor _redactor;

    /// <summary>
    ///     <para>创建新会话的排他写入端，不打开已有快照。</para>
    ///     <para>Creates an exclusive writer for a new session without opening an existing snapshot.</para>
    /// </summary>
    /// <remarks>
    ///     <para>校验会话 ID，创建存储目录并独占持有锁文件；已有快照被拒绝。</para>
    ///     <para>
    ///         Validates the session ID, creates the store directory,
    ///         and holds the lock file exclusively; existing snapshots are rejected.
    ///     </para>
    /// </remarks>
    public SessionStoreWriter(
        string directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets = null)
        : this(directory, sessionId, true, knownSecrets)
    {
    }

    /// <summary>
    ///     <para>为新建或打开会话建立文件路径并取得排他锁。</para>
    ///     <para>Establishes file paths and acquires an exclusive lock for creating or opening a session.</para>
    /// </summary>
    /// <param name="requireNewSnapshot">
    ///     <para>为 true 时拒绝已有快照；为 false 时由调用方在取锁后严格读取校验。</para>
    ///     <para>
    ///         When true, rejects an existing snapshot;
    ///         when false, the caller strictly reads and validates it after locking.
    ///     </para>
    /// </param>
    private SessionStoreWriter(
        string directory, string sessionId, bool requireNewSnapshot, IReadOnlyDictionary<string, string>? knownSecrets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                ArgumentException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.",
                                  nameof(sessionId));

        Directory.CreateDirectory(directory);
        // 文件名与快照 ID 统一为小写，避免大小写敏感文件系统产生不同会话或锁。
        SessionId    = sessionId.ToLowerInvariant();
        SnapshotPath = Path.Combine(directory, $"{SessionId}.session.json");
        AuditPath    = Path.Combine(directory, $"{SessionId}.audit.jsonl");
        _redactor    = new SecretRedactor(knownSecrets);

        if (requireNewSnapshot && File.Exists(SnapshotPath))
            throw new
                SessionStoreException($"A stored session '{sessionId}' already exists at '{SnapshotPath}'; this writer " +
                                      "creates only new sessions. Resume it with 'tinyharness chat --session <id>' or " +
                                      "inspect it read-only with 'session show'.");

        var lockPath = Path.Combine(directory, $"{SessionId}.lock");
        try
        {
            _lockStream = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new
                SessionStoreException($"Session '{sessionId}' is already open in another process (lock '{lockPath}'). " +
                                      "Close the other session first; exclusive writing is required for stored sessions.",
                                      ex);
        }

        // 文件内容仅供诊断；排他性来自打开的句柄，而非锁文件是否存在。
        var lockNote = $"pid={Environment.ProcessId} openedUtc={DateTimeOffset.UtcNow:O}";
        _lockStream.Write(Encoding.UTF8.GetBytes(lockNote));
        _lockStream.Flush();
    }

    /// <summary>
    ///     <para>写入端持有的小写规范会话 ID。</para>
    ///     <para>The canonical lowercase session ID held by this writer.</para>
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    ///     <para>会话提交审计文件的绝对路径。</para>
    ///     <para>The absolute path of the session's commit audit file.</para>
    /// </summary>
    public string AuditPath { get; }

    /// <summary>
    ///     <para>由 <see cref="OpenExistingAsync" /> 读取的快照；新会话写入端为 null。</para>
    ///     <para>
    ///         The snapshot read by <see cref="OpenExistingAsync" />;
    ///         null for a new-session writer.
    ///     </para>
    /// </summary>
    public PersistedSession? LoadedSnapshot { get; private set; }

    public string SnapshotPath { get; }

    public long CommitSequence { get; private set; }

    public async Task CommitAsync(ChatSession session, ConversationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        // 输入 ID 大小写不影响身份，盘上使用同一 ID 的小写规范形态。
        if (!string.Equals(session.Id, SessionId, StringComparison.OrdinalIgnoreCase))
            throw new
                ArgumentException($"The snapshot writer belongs to session '{SessionId}' but was asked to commit session " +
                                  $"'{session.Id}'.", nameof(session));

        var sequence  = CommitSequence + 1;
        var snapshot  = BuildSnapshot(session, context, sequence);
        var json      = JsonSerializer.Serialize(snapshot, SessionStoreJsonContext.Default.PersistedSession);
        var temporary = SnapshotPath + ".tmp";
        if (Encoding.UTF8.GetByteCount(json) > MaxSnapshotBytes)
            throw new
                SessionStoreException($"Session '{session.Id}' exceeds the snapshot size limit of {MaxSnapshotBytes:N0} bytes " +
                                      "and was not reliably saved; start a new session to continue.");

        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, SnapshotPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDeleteTemporary(temporary);
            throw new
                SessionStoreException($"Session '{session.Id}' was not reliably saved (snapshot write failed: {ex.Message}); " +
                                      "the previous valid snapshot is kept.", ex);
        }

        // 替换后快照暂时领先审计；追加失败时读取端须通过序号对账拒绝不一致状态。
        var record = new SessionStoreAuditRecord
        {
            SessionId = SessionId,
            Kind      = SessionStoreAuditRecord.CommittedKind,
            Sequence  = sequence
        };
        var auditLine = JsonSerializer.Serialize(record, SessionStoreJsonContext.Default.SessionStoreAuditRecord) +
                        Environment.NewLine;
        try
        {
            await File.AppendAllTextAsync(AuditPath, auditLine, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new
                SessionStoreException($"Session '{session.Id}' snapshot {sequence} was written but its audit record was not " +
                                      $"('{AuditPath}'): the saved state cannot be verified as consistent. The sequence is " +
                                      "not advanced; the next successful commit re-writes this sequence.", ex);
        }

        CommitSequence = sequence;
    }

    public void Dispose()
    {
        // 保留锁文件，避免关闭时删除与另一进程重建锁之间的竞态；排他性由句柄保证。
        _lockStream.Dispose();
    }

    /// <summary>
    ///     <para>先取排他锁，再严格读取已有会话并续种提交序号。</para>
    ///     <para>
    ///         Acquires the exclusive lock before strictly reading an existing session
    ///         and seeding its commit sequence.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>持锁写入端；<see cref="LoadedSnapshot" /> 供恢复流程继续校验兼容性，后续提交序号单调递增。</para>
    ///     <para>
    ///         A locked writer whose <see cref="LoadedSnapshot" /> requires further resume compatibility validation;
    ///         subsequent commit sequences increase monotonically.
    ///     </para>
    /// </returns>
    /// <exception cref="SessionStoreException">
    ///     <para>快照缺失、损坏、版本未知、审计不一致或锁不可用；不猜测或降级放行。</para>
    ///     <para>
    ///         The snapshot is missing, damaged, unknown-version, audit-inconsistent, or the lock is unavailable;
    ///         no guessing or permissive fallback is performed.
    ///     </para>
    /// </exception>
    public static async Task<SessionStoreWriter> OpenExistingAsync(
        string            directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                SessionStoreException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.");

        sessionId = sessionId.ToLowerInvariant();
        if (!File.Exists(Path.Combine(directory, $"{sessionId}.session.json")))
            throw new
                SessionStoreException($"No stored session '{sessionId}' exists in '{directory}'; there is nothing to resume. " +
                                      "'tinyharness session list' shows the stored sessions.");

        var writer = new SessionStoreWriter(directory, sessionId, false, knownSecrets);
        try
        {
            var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken)
                                                   .ConfigureAwait(false)
                        ?? throw new
                               SessionStoreException($"No stored session '{sessionId}' exists in '{directory}'; there is nothing to resume.");
            writer.CommitSequence = snapshot.CommitSequence;
            writer.LoadedSnapshot = snapshot;
            return writer;
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private PersistedSession BuildSnapshot(ChatSession session, ConversationContext context, long sequence) => new()
    {
        FormatVersion             = PersistedSession.CurrentFormatVersion,
        Id                        = SessionId,
        WorkspaceRoot             = session.WorkspaceRoot,
        Model                     = session.Model,
        CreatedUtc                = session.CreatedUtc,
        UpdatedUtc                = DateTimeOffset.UtcNow,
        CommitSequence            = sequence,
        InspectionAcknowledgedUtc = session.InspectionAcknowledgedUtc,
        PendingInspectionReason   = _redactor.RedactNullable(session.PendingInspectionReason),
        Turns = session.Turns.Select(turn => new PersistedTurn
        {
            Id         = turn.Id,
            UserInput  = _redactor.Redact(turn.UserInput),
            CreatedUtc = turn.CreatedUtc,
            Runs = turn.Runs.Select(run => new PersistedRun
            {
                Id             = run.Id,
                StartedUtc     = run.StartedUtc,
                FinishedUtc    = run.FinishedUtc,
                Status         = run.Result?.Status.ToString(),
                FinalMessage   = run.Result is null ? null : _redactor.Redact(run.Result.FinalMessage),
                Error          = run.Result is null ? null : _redactor.RedactNullable(run.Result.Error),
                Steps          = run.Result?.Steps          ?? 0,
                ToolExecutions = run.Result?.ToolExecutions ?? 0,
                InputTokens    = run.Result?.InputTokens,
                OutputTokens   = run.Result?.OutputTokens,
                // 与恢复端使用同一执行记录推导检查标记；旧运行无记录时保留 null，不能推定安全。
                NeedsInspection = (run.Result?.ToolCalls ?? run.ExecutionRecords) is { Count: > 0 } calls
                    ? calls.Any(call => call is { State: ToolCallExecutionState.UnknownResult, SideEffects: true })
                    : null,
                ToolCalls = (run.Result?.ToolCalls ?? run.ExecutionRecords)?
                           .Select(call => new PersistedToolCallRecord
                            {
                                CallId           = _redactor.Redact(call.CallId),
                                ToolName         = _redactor.Redact(call.ToolName),
                                Capability       = _redactor.RedactNullable(call.Capability),
                                SideEffects      = call.SideEffects,
                                Summary          = _redactor.Redact(call.Summary),
                                PreparationError = _redactor.RedactNullable(call.PreparationError),
                                Approval         = _redactor.RedactNullable(call.Approval),
                                State            = call.State.ToString()
                            })
                           .ToArray()
            }).ToArray()
        }).ToArray(),
        Conversation = BuildConversation(context)
    };

    /// <summary>
    ///     <para>构建保真 canonical 对话，保存协议历史、压缩状态与折叠边界。</para>
    ///     <para>
    ///         Builds a faithful canonical conversation
    ///         preserving protocol history, compaction state, and fold boundaries.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         内容不经过脱敏器，可能包含秘密，须写入受保护存储；保护不等于加密。
    ///         ProtectedData/ItemId 逐条记录完整性哈希，<see cref="PersistedConversation.OpaqueRedacted" /> 为 false。
    ///         检查投影与审计继续独立脱敏，不能替代此恢复内容。
    ///     </para>
    ///     <para>
    ///         Content bypasses redaction, may contain secrets, and requires protected storage;
    ///         protection does not imply encryption. ProtectedData/ItemId receive per-entry integrity hashes,
    ///         and <see cref="PersistedConversation.OpaqueRedacted" /> is false.
    ///         Inspection projections and audits remain independently redacted and cannot replace this resume content.
    ///     </para>
    /// </remarks>
    private PersistedConversation BuildConversation(ConversationContext context)
    {
        var position = context.Position;
        var messages = new PersistedMessage[context.Messages.Count];
        for (var i = 0; i < context.Messages.Count; i++)
        {
            var original = context.Messages[i];
            messages[i] = new PersistedMessage
            {
                Role       = original.Role.ToString(),
                Content    = original.Content,
                ToolCallId = original.ToolCallId,
                Name       = original.Name,
                ToolCalls = original.ToolCalls?
                                    .Select(call => new PersistedToolCall(call.Id, call.FunctionName,
                                                                          call.ArgumentsJson))
                                    .ToArray(),
                Reasoning = original.Reasoning?
                                    .Select(entry => new PersistedReasoning
                                     {
                                         Text          = entry.Text,
                                         ProtectedData = entry.ProtectedData,
                                         ItemId        = entry.ItemId,
                                         OpaqueHash =
                                             PersistedReasoning.ComputeOpaqueHash(entry.ProtectedData, entry.ItemId)
                                     })
                                    .ToArray()
            };
        }

        return new PersistedConversation
        {
            Messages              = messages,
            StateJson             = position.HasState ? context.State.ToJson() : null,
            HasState              = position.HasState,
            FoldStartMessageIndex = position.FoldStartMessageIndex,
            FoldEndMessageIndex   = position.FoldEndMessageIndex,
            OpaqueRedacted        = false
        };
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理尽力而为；后续提交可覆盖残留临时文件，不改变原始保存错误。
        }
    }
}

/// <summary>
///     <para>会话列表条目，携带读取成功的会话或该条目的错误原因。</para>
///     <para>
///         A session-list entry containing either the successfully read session
///         or the entry's error reason.
///     </para>
/// </summary>
public sealed record SessionStoreEntry(string Id, PersistedSession? Session, string? Error);

/// <summary>
///     <para>严格离线读取会话快照，校验标识、大小、JSON 深度、格式版本与提交审计。</para>
///     <para>
///         Strictly reads session snapshots offline,
///         validating identifiers, size, JSON depth, format version, and commit auditing.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         不解析凭据、不调用模型、不启动工具。
///         损坏、未知版本或审计不一致不猜测解释；旧 run 快照仅经 <see cref="ReadLegacyAsync" /> 只读展示。
///         格式读取成功不代表已通过续聊兼容性校验。
///     </para>
///     <para>
///         Resolves no credentials, calls no model, and starts no tool.
///         Damaged, unknown-version, or audit-inconsistent content is never guessed;
///         legacy run snapshots are inspected read-only via <see cref="ReadLegacyAsync" />.
///         Successful format reading does not establish resume compatibility.
///     </para>
/// </remarks>
public static class SessionStoreReader
{
    /// <summary>
    ///     <para>读取时的最大 JSON 深度；更深输入按损坏拒绝。</para>
    ///     <para>Maximum JSON depth accepted when reading; deeper input is rejected as damaged.</para>
    /// </summary>
    public const int MaxJsonDepth = 32;

    /// <summary>
    ///     <para>会话与旧 run 快照共用的文件后缀；两种格式按目录区分。</para>
    ///     <para>
    ///         File suffix shared by session and legacy run snapshots;
    ///         their directories distinguish the formats.
    ///     </para>
    /// </summary>
    private const string SessionSnapshotFileSuffix = ".session.json";

    /// <summary>
    ///     <para>读取存储会话并完成存储格式校验与审计对账。</para>
    ///     <para>Reads a stored session with storage-format validation and audit reconciliation.</para>
    /// </summary>
    /// <returns>
    ///     <para>读取的快照；文件缺失时为 null，以区分不存在与损坏。</para>
    ///     <para>The read snapshot, or null for a missing file to distinguish absence from damage.</para>
    /// </returns>
    public static async Task<PersistedSession?> ReadAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                SessionStoreException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.");

        // 查找使用小写规范 ID，兼容大小写敏感文件系统。
        sessionId = sessionId.ToLowerInvariant();
        var snapshotPath = Path.Combine(directory, $"{sessionId}{SessionSnapshotFileSuffix}");
        if (!File.Exists(snapshotPath)) return null;

        var length = new FileInfo(snapshotPath).Length;
        if (length > SessionStoreWriter.MaxSnapshotBytes)
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' is {length:N0} bytes and exceeds " +
                                      $"the {SessionStoreWriter.MaxSnapshotBytes:N0}-byte limit; refusing to read it.");

        string json;
        try
        {
            json = await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new
                SessionStoreException($"Stored session '{sessionId}' cannot be read from '{snapshotPath}': {ex.Message}.",
                                      ex);
        }

        PersistedSession snapshot;
        try
        {
            // 绑定类型前先限制 JSON 深度，拒绝恶意嵌套。
            using (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }))
            {
            }

            snapshot = JsonSerializer.Deserialize(json, SessionStoreJsonContext.Default.PersistedSession)
                    ?? throw new SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' is damaged JSON: {ex.Message}.",
                                      ex);
        }

        if (snapshot.FormatVersion is < 1 or > PersistedSession.CurrentFormatVersion)
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' uses format version "    +
                                      $"{snapshot.FormatVersion}, which this build does not know (understood: 1–" +
                                      $"{PersistedSession.CurrentFormatVersion}); it stays read-only until a build that understands it.");

        if (!string.Equals(snapshot.Id, sessionId, StringComparison.Ordinal))
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' declares id '{snapshot.Id}'; " +
                                      "the file name and the content disagree.");

        await ReconcileAuditTrailAsync(directory, sessionId, snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    ///     <para>枚举存储会话，单个损坏条目不终止列举。</para>
    ///     <para>Lists stored sessions without aborting on an individual damaged entry.</para>
    /// </summary>
    /// <returns>
    ///     <para>按更新时间从新到旧排列的条目；读取错误通过 <see cref="SessionStoreEntry.Error" /> 报告。</para>
    ///     <para>
    ///         Entries ordered newest first by update time;
    ///         read errors are reported through <see cref="SessionStoreEntry.Error" />.
    ///     </para>
    /// </returns>
    public static async Task<IReadOnlyList<SessionStoreEntry>> ListAsync(
        string directory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory)) return [];

        var entries = new List<SessionStoreEntry>();
        foreach (var path in Directory.EnumerateFiles(directory, "*" + SessionSnapshotFileSuffix))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(path)[..^SessionSnapshotFileSuffix.Length];
            if (!SessionStorePaths.IsValidSessionId(id))
            {
                entries.Add(new SessionStoreEntry(id, null,
                                                  $"file name '{Path.GetFileName(path)}' is not a session id"));
                continue;
            }

            try
            {
                var session = await ReadAsync(directory, id, cancellationToken).ConfigureAwait(false);
                entries.Add(session is null
                                ? new SessionStoreEntry(id, null, "snapshot file disappeared while listing")
                                : new SessionStoreEntry(id, session, null));
            }
            catch (SessionStoreException ex)
            {
                entries.Add(new SessionStoreEntry(id, null, ex.Message));
            }
        }

        return entries.OrderByDescending(entry => entry.Session?.UpdatedUtc ?? DateTimeOffset.MinValue).ToArray();
    }

    /// <summary>
    ///     <para>读取旧版脱敏 run 快照，仅供只读检查，不能用于续聊恢复。</para>
    ///     <para>
    ///         Reads a legacy redacted run snapshot for read-only inspection,
    ///         never for session resumption.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>旧版 <c>&lt;runId&gt;.session.json</c> 快照；文件缺失时为 null。</para>
    ///     <para>The legacy <c>&lt;runId&gt;.session.json</c> snapshot, or null when the file is missing.</para>
    /// </returns>
    public static async Task<SessionSnapshot?> ReadLegacyAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                SessionStoreException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.");

        // 与会话存储共用小写规范 ID，兼容大小写敏感文件系统。
        sessionId = sessionId.ToLowerInvariant();
        var snapshotPath = Path.Combine(directory, $"{sessionId}{SessionSnapshotFileSuffix}");
        if (!File.Exists(snapshotPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            using (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }))
            {
            }

            // JSON 显式 null 会覆盖非空默认值；应在读取边界拒绝，避免展示层崩溃。
            var snapshot = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.SessionSnapshot);
            if (snapshot?.Result is null || snapshot.Messages is null)
                throw new
                    SessionStoreException($"Legacy run snapshot '{sessionId}' at '{snapshotPath}' is damaged: " +
                                          "it has no run result or message list.");
            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new
                SessionStoreException($"Legacy run snapshot '{sessionId}' at '{snapshotPath}' cannot be read: {ex.Message}.",
                                      ex);
        }
    }

    /// <summary>
    ///     <para>校验审计中最大已提交序号与快照提交序号一致。</para>
    ///     <para>Validates that the audit's maximum committed sequence matches the snapshot sequence.</para>
    /// </summary>
    /// <remarks>
    ///     <para>序号不一致或审计缺失、损坏均拒绝；原子替换成功本身不足以证明一致保存。</para>
    ///     <para>
    ///         Rejects sequence divergence or a missing or corrupt audit;
    ///         successful atomic replacement alone does not prove a consistent save.
    ///     </para>
    /// </remarks>
    private static async Task ReconcileAuditTrailAsync(
        string directory, string sessionId, PersistedSession snapshot, CancellationToken cancellationToken)
    {
        var auditPath = Path.Combine(directory, $"{sessionId}.audit.jsonl");
        if (!File.Exists(auditPath))
            throw new
                SessionStoreException($"Stored session '{sessionId}' has no commit audit at '{auditPath}'; its snapshot " +
                                      "cannot be verified as a consistent save.");

        long maximum = 0;
        try
        {
            await foreach (var line in File.ReadLinesAsync(auditPath, cancellationToken).ConfigureAwait(false))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                SessionStoreAuditRecord record;
                try
                {
                    record = JsonSerializer.Deserialize(line, SessionStoreJsonContext.Default.SessionStoreAuditRecord)
                          ?? throw new JsonException("empty record");
                }
                catch (JsonException ex)
                {
                    throw new
                        SessionStoreException($"The commit audit of session '{sessionId}' at '{auditPath}' contains a " +
                                              $"damaged line and cannot be reconciled: {ex.Message}.", ex);
                }

                if (string.Equals(record.Kind, SessionStoreAuditRecord.CommittedKind, StringComparison.Ordinal) &&
                    string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
                    maximum = Math.Max(maximum, record.Sequence);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new
                SessionStoreException($"The commit audit of session '{sessionId}' cannot be read from '{auditPath}': {ex.Message}.",
                                      ex);
        }

        if (maximum != snapshot.CommitSequence)
            throw new
                SessionStoreException($"Stored session '{sessionId}' is inconsistent: the snapshot is at commit sequence " +
                                      $"{snapshot.CommitSequence} while the audit at '{auditPath}' confirms {maximum}. " +
                                      "The last save was not verifiably completed; inspect the files before trusting either.");
    }
}

[JsonSerializable(typeof(PersistedSession))]
[JsonSerializable(typeof(PersistedTurn))]
[JsonSerializable(typeof(PersistedRun))]
[JsonSerializable(typeof(PersistedToolCallRecord))]
[JsonSerializable(typeof(PersistedConversation))]
[JsonSerializable(typeof(PersistedMessage))]
[JsonSerializable(typeof(PersistedToolCall))]
[JsonSerializable(typeof(PersistedReasoning))]
[JsonSerializable(typeof(SessionStoreAuditRecord))]
[JsonSerializable(typeof(SessionModelIdentity))]
[JsonSerializable(typeof(ChatApiKind))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionStoreJsonContext : JsonSerializerContext;
