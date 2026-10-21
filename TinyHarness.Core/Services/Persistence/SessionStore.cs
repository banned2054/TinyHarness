using TinyHarness.Core.Exceptions;
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
                                      "sandboxed commands could tamper with session logs. " +
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

    internal static void EnsureStoreLeaf(string directory, string path)
    {
        var root     = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(path);
        if (!Workspace.IsInside(root, fullPath))
            throw new
                SessionStoreException($"Session store file '{fullPath}' is outside its storage directory '{root}'.");

        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        string?        target;
        try
        {
            target = info.LinkTarget;
        }
        catch (IOException ex)
        {
            throw new SessionStoreException($"Cannot validate session store file '{fullPath}': {ex.Message}", ex);
        }

        if (target is not null ||
            ((File.Exists(fullPath) || Directory.Exists(fullPath)) &&
             (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0))
            throw new
                SessionStoreException($"Session store file '{fullPath}' is a link or reparse point and is not allowed.");
    }

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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无法解析的链接保留词法路径，调用方的词法检查仍适用。
            }
        }

        return current;
    }
}

/// <summary>
///     <para>在排他锁下向单一 JSONL 会话日志追加原子批次。</para>
///     <para>Appends atomic batches to one JSONL session log under an exclusive lock.</para>
/// </summary>
/// <remarks>
///     <para>
///         普通提交只写入新增回合、运行、canonical 消息和状态变化；压缩状态作为独立检查点记录。
///         canonical 内容可能包含秘密；回合/运行检查投影保持脱敏。写入失败后 writer 停止追加。
///     </para>
///     <para>
///         Ordinary commits append only new turns, runs, canonical messages, and state changes;
///         compaction state is recorded as a separate checkpoint. Canonical content may contain secrets,
///         while turn/run inspection projections stay redacted. A failed writer stops appending.
///     </para>
/// </remarks>
public interface ISessionStoreWriter : IDisposable
{
    /// <summary>
    ///     <para>写入链路是否仍允许追加记录；发生一次不确定写入失败后永久为 false。</para>
    ///     <para>Whether the append chain still accepts records; false permanently after an uncertain write failure.</para>
    /// </summary>
    bool IsHealthy => true;

    /// <summary>
    ///     <para>最近一次写入失败；写入健康时为 null。</para>
    ///     <para>The most recent write failure, or null while writes remain healthy.</para>
    /// </summary>
    string? WriteFailure => null;

    /// <summary>
    ///     <para>权威 JSONL 会话日志路径。</para>
    ///     <para>The authoritative JSONL session log path.</para>
    /// </summary>
    string SnapshotPath { get; }

    /// <summary>
    ///     <para>最近一次成功提交的序号；首次提交前为 0。</para>
    ///     <para>The last successfully committed sequence; zero before the first commit.</para>
    /// </summary>
    long CommitSequence { get; }

    /// <summary>
    ///     <para>将本次新增会话事实写为一个物理行批次，完整 flush 后递增提交序号。</para>
    ///     <para>Writes new session facts as one physical-line batch and advances the sequence after a complete flush.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         调用方须保证上下文位于可恢复协议边界；失败的追加不会重试，writer 随即 poison。
    ///     </para>
    ///     <para>
    ///         Callers must provide a resumable protocol boundary; a failed append is never retried and poisons the writer.
    ///     </para>
    /// </remarks>
    /// <exception cref="SessionStoreException">
    ///     <para>批次或日志超限、数据损坏或写入失败。</para>
    ///     <para>The batch or log exceeds its limit, data is damaged, or writing fails.</para>
    /// </exception>
    Task CommitAsync(ChatSession session, ConversationContext context, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>在同一批次提交安全边界的 canonical 增量及控制事实；不支持的 writer 必须明确拒绝。</para>
    ///     <para>Commits canonical increments and control facts in one safe-boundary batch; unsupported writers must refuse.</para>
    /// </summary>
    Task CommitControlAsync(ChatSession session, ConversationContext context, IReadOnlyList<SessionLogRecord> records,
                            CancellationToken cancellationToken) =>
        throw new NotSupportedException("This session writer does not support control records.");

    Task CommitExecutionIntentAsync(
        ChatSession session, ConversationContext context, CancellationToken cancellationToken) =>
        CommitAsync(session, context, cancellationToken);

    /// <summary>
    ///     <para>向已建立 identity 的会话日志追加一批显式类型记录，由 writer 统一分配 ID 与序号。</para>
    ///     <para>Appends explicit typed records after identity exists; the writer assigns record IDs and sequence.</para>
    /// </summary>
    Task<IReadOnlyList<SessionLogRecord>> AppendRecordsAsync(
        IReadOnlyList<SessionLogRecord> records, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This session writer does not support journal records.");

    IRunRecorder? CreateRunRecorder(string sessionId, string turnId, string runId,
                                    Func<CancellationToken, Task> commitInitialContext,
                                    Func<SessionHistoryItem, CancellationToken, ValueTask>? historyItemCommitted) =>
        null;
}

/// <summary>
///     <para>会话列表条目，携带读取成功的会话或该条目的错误原因。</para>
///     <para>
///         A session-list entry containing either the successfully read session
///         or the entry's error reason.
///     </para>
/// </summary>
public sealed record SessionStoreEntry(string Id, PersistedSession? Session, string? Error);
