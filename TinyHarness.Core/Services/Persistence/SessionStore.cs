using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     受保护会话存储的路径与目录规则（M13.1，PLAN §7.1）：默认目录是用户级受保护目录；
///     显式配置的目录必须是绝对路径，且经词法与链接解析后都不得落入组合工作区——
///     沙箱目标命令可写的地方不能存放可续聊会话（PLAN §4.3）。
///     Path and directory rules of the protected session store (M13.1, PLAN §7.1):
///     the default directory is a user-level protected location; an explicitly
///     configured directory must be absolute and must resolve — lexically and
///     through links — outside the composition workspace, because a place
///     writable by sandboxed commands must never hold resumable sessions
///     (PLAN §4.3).
/// </summary>
public static class SessionStorePaths
{
    /// <summary>
    ///     覆盖会话存储目录的环境变量；用于便携部署与测试，普通用户无需设置。
    ///     Environment variable that overrides the session store directory; used for portable setups and tests.
    /// </summary>
    public const string DirectoryOverrideVariable = "TINYHARNESS_SESSION_STORE_DIR";

    /// <summary>
    ///     返回默认的用户级会话存储目录：优先环境变量覆盖；Windows 使用 %LOCALAPPDATA%\tinyharness\sessions，
    ///     其他平台使用 XDG_DATA_HOME 或 ~/.local/share；目录 API 不可用时退回当前目录下的 tinyharness\sessions。
    ///     Returns the default user-level session store directory: the environment override first; on Windows
    ///     %LOCALAPPDATA%\tinyharness\sessions; elsewhere XDG_DATA_HOME or ~/.local/share; the current directory's
    ///     tinyharness\sessions when the folder APIs are unavailable.
    /// </summary>
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
    ///     解析生效的受保护存储目录：未配置时使用默认目录；显式目录必须是绝对路径，并且经
    ///     <see cref="EnsureOutsideWorkspace" /> 检查后才被接受。返回规范化绝对路径（不创建目录）。
    ///     Resolves the effective protected store directory: the default when unset; an explicit
    ///     directory must be absolute and pass <see cref="EnsureOutsideWorkspace" />. Returns the
    ///     normalized absolute path (no directory is created).
    /// </summary>
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

        // The remediation hint must name where the offending directory came from:
        // an explicit setting, or the environment override behind the default.
        EnsureOutsideWorkspace(directory, workspaceRoot,
                               string.IsNullOrWhiteSpace(configuredDirectory)
                                   ? $"the '{DirectoryOverrideVariable}' environment variable or settings.sessionStoreDirectory"
                                   : "settings.sessionStoreDirectory");
        return directory;
    }

    /// <summary>
    ///     拒绝位于组合工作区内（词法判断或经符号链接/junction 解析后落入）的存储目录。
    ///     逐级解析已存在的路径段，因此链接指向工作区内部也算逃逸。供存储解析与沙箱组合守卫共用。
    ///     Rejects a store directory located inside the composition workspace, either lexically or after
    ///     resolving symlinks/junctions into it. Existing path segments are resolved level by level, so a link
    ///     landing inside the workspace counts as an escape. Shared by the store resolution and the sandbox
    ///     composition guard.
    /// </summary>
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
    ///     会话 ID 是标识符而不是路径片段：仅接受 32 位十六进制（Guid 的 "N" 形态），输入大小写
    ///     不敏感，但盘上统一存储为小写；读取端与写入端共用同一校验，杜绝路径逃逸与通配。
    ///     A session id is an identifier, never a path fragment: exactly 32 hex
    ///     characters (the Guid "N" form). Input is case-insensitive while the
    ///     on-disk form is canonically lower-case. Readers and writers share this
    ///     check, which rules out path escapes and wildcards.
    /// </summary>
    public static bool IsValidSessionId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigit);

    /// <summary>
    ///     从盘符根向下解析已存在路径段的链接目标；不存在的尾部保持词法路径。链接解析失败时
    ///     保持词法结果，由外层的词法检查兜底。
    ///     Resolves link targets of existing path segments from the drive root down; a nonexistent tail stays
    ///     lexical. A link that cannot be resolved keeps the lexical result, leaving the outer lexical check
    ///     as the backstop.
    /// </summary>
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
                // An unresolvable link keeps the lexical path; the caller's lexical
                // check still applies to it.
            }
        }

        return current;
    }
}

/// <summary>
///     会话快照的写入端（M13.1）：排他持有会话锁文件，在完整回合边界原子替换版本化快照并追加
///     提交审计。写入失败抛 <see cref="SessionStoreException" />，调用方必须按“本次未可靠保存”报告，
///     不得把运行终态当作落盘成功的证明（PLAN §7.1）。
///     The write side of session snapshots (M13.1): holds the session's lock
///     file exclusively, atomically replaces the versioned snapshot at complete
///     turn boundaries and appends the commit audit. A failed write throws
///     <see cref="SessionStoreException" />; callers must report "not reliably
///     saved" and never present a run's terminal state as proof of persistence
///     (PLAN §7.1).
/// </summary>
public interface ISessionStoreWriter : IDisposable
{
    /// <summary>快照文件绝对路径。The absolute path of the snapshot file.</summary>
    string SnapshotPath { get; }

    /// <summary>最近一次成功提交的序号；尚未提交过时为 0。The last successfully committed sequence; 0 before the first commit.</summary>
    long CommitSequence { get; }

    /// <summary>
    ///     序列化会话的当前完整状态并以原子替换落盘（序号加一），随后追加提交审计。
    ///     Serializes the session's current complete state, persists it by atomic replacement (sequence + 1),
    ///     then appends the commit audit.
    /// </summary>
    Task CommitAsync(ChatSession session, CancellationToken cancellationToken);
}

/// <summary>
///     <see cref="ISessionStoreWriter" /> 的文件实现：同一持久会话同一时刻只允许一个写入者——
///     构造时以独占共享模式打开 <c>&lt;id&gt;.lock</c> 并持有到 <see cref="Dispose" />，第二个进程
///     （或同进程第二个实例）打开同一会话立即失败（PLAN §5 的跨进程排他写入）。提交顺序为
///     “同目录临时文件 → 原子替换快照 → 追加审计提交行”：替换前的任何失败都保留上一有效快照；
///     替换后、审计前失败会留下领先于审计的快照，由读取端的提交序号对账检出。
///     The file implementation of <see cref="ISessionStoreWriter" />: one writer
///     per stored session at a time — the constructor opens <c>&lt;id&gt;.lock</c>
///     with exclusive sharing and holds it until <see cref="Dispose" />, so a
///     second process (or a second instance in the same process) fails
///     immediately (cross-process exclusive writing, PLAN §5). The commit order
///     is "same-directory temp file → atomic snapshot replacement → appended
///     commit audit": any failure before the replacement keeps the previous
///     valid snapshot; a failure between replacement and audit leaves a snapshot
///     ahead of the audit, which the reader's sequence reconciliation detects.
/// </summary>
public sealed class SessionStoreWriter : ISessionStoreWriter
{
    /// <summary>
    ///     快照文件大小上限；超过即拒绝写入或读取，防止失控会话拖垮存储与查看。Upper bound for a snapshot file; larger files are refused on write and read
    ///     so a runaway session cannot overwhelm storage or inspection.
    /// </summary>
    public const int MaxSnapshotBytes = 16 * 1024 * 1024;

    private readonly FileStream _lockStream;

    private readonly SecretRedactor _redactor;

    /// <summary>
    ///     创建（且只创建）新会话的写入端：校验会话 ID、拒绝已存在的快照、独占打开锁文件并创建存储目录。
    ///     Creates the writer for a brand-new session: validates the session id, refuses an existing snapshot,
    ///     opens the lock file exclusively and creates the store directory.
    /// </summary>
    public SessionStoreWriter(
        string directory, string sessionId, IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                ArgumentException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.",
                                  nameof(sessionId));

        Directory.CreateDirectory(directory);
        // The on-disk canonical form is lower-case hex (the Guid "N" form): the
        // snapshot, audit and lock names — and the snapshot's id field — all use
        // it, so case-sensitive file systems resolve every id spelling.
        SessionId    = sessionId.ToLowerInvariant();
        SnapshotPath = Path.Combine(directory, $"{SessionId}.session.json");
        AuditPath    = Path.Combine(directory, $"{SessionId}.audit.jsonl");
        _redactor    = new SecretRedactor(knownSecrets);

        if (File.Exists(SnapshotPath))
            throw new
                SessionStoreException($"A stored session '{sessionId}' already exists at '{SnapshotPath}'; M13.1 opens " +
                                      "only new sessions for writing. Open it read-only with 'session show' instead.");

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

        // Diagnostic content only; the lock is held by the open handle, not by this file's existence.
        var lockNote = $"pid={Environment.ProcessId} openedUtc={DateTimeOffset.UtcNow:O}";
        _lockStream.Write(Encoding.UTF8.GetBytes(lockNote));
        _lockStream.Flush();
    }

    /// <summary>写入端持有的会话 ID。The session id held by this writer.</summary>
    public string SessionId { get; }

    /// <summary>会话提交审计文件绝对路径。The absolute path of the session's commit audit file.</summary>
    public string AuditPath { get; }

    public string SnapshotPath { get; }

    public long CommitSequence { get; private set; }

    public async Task CommitAsync(ChatSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        // The id comparison is case-insensitive: the canonical storage form is
        // the lower-case of the same id.
        if (!string.Equals(session.Id, SessionId, StringComparison.OrdinalIgnoreCase))
            throw new
                ArgumentException($"The snapshot writer belongs to session '{SessionId}' but was asked to commit session " +
                                  $"'{session.Id}'.", nameof(session));

        var sequence  = CommitSequence + 1;
        var snapshot  = BuildSnapshot(session, sequence);
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

        // The snapshot now leads the audit until this record lands; readers detect
        // the divergence if it never does.
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
        // The lock file itself is deliberately left in place: exclusivity comes
        // from the open handle, and deleting on close would race another process
        // re-creating the lock in between.
        _lockStream.Dispose();
    }

    private PersistedSession BuildSnapshot(ChatSession session, long sequence) => new()
    {
        FormatVersion  = PersistedSession.CurrentFormatVersion,
        Id             = SessionId,
        WorkspaceRoot  = session.WorkspaceRoot,
        Model          = session.Model,
        CreatedUtc     = session.CreatedUtc,
        UpdatedUtc     = DateTimeOffset.UtcNow,
        CommitSequence = sequence,
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
                OutputTokens   = run.Result?.OutputTokens
            }).ToArray()
        }).ToArray()
    };

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort only; a leftover temp/lock file never blocks a later
            // commit (temp names are overwritten, locks are re-created).
        }
    }
}

/// <summary>
///     会话存储的一个列表条目：读取成功的会话或损坏原因（list 容错展示，show 严格报错）。
///     One list entry of the session store: the session when it read
///     successfully, or the damage reason (list reports and continues, show is
///     strict).
/// </summary>
public sealed record SessionStoreEntry(string Id, PersistedSession? Session, string? Error);

/// <summary>
///     会话存储的严格离线读取端（M13.1，PLAN §7.1）：校验会话 ID、文件大小、JSON 深度与格式版本，
///     并把快照提交序号与审计提交记录对账。任何损坏、未知版本、路径逃逸或不一致都以
///     <see cref="SessionStoreException" /> 报告，绝不猜测解释内容。本类型不解析凭据、不调用模型、
///     不启动工具。旧 <c>.session.json</c> run 快照经 <see cref="ReadLegacyAsync" /> 只读兼容展示。
///     The strictly offline read side of the session store (M13.1, PLAN §7.1):
///     it validates session ids, file sizes, JSON depth and the format version,
///     and reconciles the snapshot's commit sequence against the commit audit.
///     Damage, unknown versions, path escapes and divergence are reported as
///     <see cref="SessionStoreException" /> — content is never guessed. This
///     type resolves no credentials, calls no model and starts no tool. Legacy
///     <c>.session.json</c> run snapshots are shown read-only via
///     <see cref="ReadLegacyAsync" />.
/// </summary>
public static class SessionStoreReader
{
    /// <summary>读取时允许的最大 JSON 深度；更深的输入按损坏拒绝。The maximum JSON depth accepted when reading; deeper input is rejected as damaged.</summary>
    public const int MaxJsonDepth = 32;

    /// <summary>
    ///     会话快照文件的统一后缀（新旧格式同名，按所在目录区分）。The shared suffix of session snapshot files (both formats use it; the directory
    ///     tells them apart).
    /// </summary>
    private const string SessionSnapshotFileSuffix = ".session.json";

    /// <summary>
    ///     读取一个存储会话并完成全部校验与对账；文件缺失返回 null（调用方区分“不存在”与“损坏”）。
    ///     Reads one stored session with full validation and reconciliation; a missing file returns null
    ///     (callers distinguish "absent" from "damaged").
    /// </summary>
    public static async Task<PersistedSession?> ReadAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                SessionStoreException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.");

        // Id input is case-insensitive; lookups go through the lower-case
        // canonical form so case-sensitive file systems find every stored session.
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
            // Parse first with a bounded depth so hostile deep nesting fails here
            // instead of inside type binding.
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

        if (snapshot.FormatVersion != PersistedSession.CurrentFormatVersion)
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' uses format version " +
                                      $"{snapshot.FormatVersion}, which this build does not know (expected "   +
                                      $"{PersistedSession.CurrentFormatVersion}); it stays read-only until a build that understands it.");

        if (!string.Equals(snapshot.Id, sessionId, StringComparison.Ordinal))
            throw new
                SessionStoreException($"Stored session '{sessionId}' at '{snapshotPath}' declares id '{snapshot.Id}'; " +
                                      "the file name and the content disagree.");

        await ReconcileAuditTrailAsync(directory, sessionId, snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    /// <summary>
    ///     枚举存储目录中的会话；单个条目损坏不终止列举，而是以 <see cref="SessionStoreEntry.Error" /> 报告。
    ///     结果按更新时间从新到旧排列。
    ///     Enumerates the sessions in the store directory; one damaged entry does
    ///     not stop the listing — it is reported through <see cref="SessionStoreEntry.Error" />. Entries
    ///     are ordered newest first by updated time.
    /// </summary>
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
    ///     读取旧版 run 快照（<c>&lt;runId&gt;.session.json</c>，M13 之前的脱敏检查产物）用于只读展示；
    ///     文件缺失返回 null。旧格式不能恢复为续聊会话（PLAN §7.2）。
    ///     Reads a legacy run snapshot (<c>&lt;runId&gt;.session.json</c>, the redacted inspection artifact from
    ///     before M13) for read-only display; a missing file returns null. The legacy format is never restored
    ///     into a resumable session (PLAN §7.2).
    /// </summary>
    public static async Task<SessionSnapshot?> ReadLegacyAsync(
        string directory, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!SessionStorePaths.IsValidSessionId(sessionId))
            throw new
                SessionStoreException($"'{sessionId}' is not a valid session id: exactly 32 hex characters are required.");

        // Same canonical lower-case lookup as the protected store.
        sessionId = sessionId.ToLowerInvariant();
        var snapshotPath = Path.Combine(directory, $"{sessionId}{SessionSnapshotFileSuffix}");
        if (!File.Exists(snapshotPath)) return null;

        try
        {
            var json = await File.ReadAllTextAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            using (JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxJsonDepth }))
            {
            }

            // The non-null property defaults do not protect against an explicit
            // JSON null; a snapshot without its result or messages is damaged,
            // never a crash in the display layer.
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
    ///     对账：审计中最大已提交序号必须等于快照的提交序号。快照领先（替换后、审计前中断）或审计
    ///     缺失/损坏都说明本次保存未验证完成，按不一致拒绝展示。
    ///     Reconciliation: the audit's maximum committed sequence must equal the
    ///     snapshot's commit sequence. A snapshot ahead of the audit (interrupted
    ///     between replacement and audit), a missing or a corrupt audit all mean
    ///     the save was never verified — the entry is rejected as inconsistent.
    /// </summary>
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
[JsonSerializable(typeof(SessionStoreAuditRecord))]
[JsonSerializable(typeof(SessionModelIdentity))]
[JsonSerializable(typeof(ChatApiKind))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionStoreJsonContext : JsonSerializerContext;
