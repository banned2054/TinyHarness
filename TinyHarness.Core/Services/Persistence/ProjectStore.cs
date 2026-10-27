using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Project;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>受保护的项目目录册：projects.json 的读取、跨进程锁保存、按最终执行目录的创建复用与改名。</para>
///     <para>
///         The protected project catalog: reading projects.json, saving under a cross-process lock,
///         create-or-reuse by final execution root, and rename.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         目录册跟随当前会话存储目录；调用方必须先用 <c>SessionStorePaths.ResolveProtected</c> 解析出
///         工作区之外的存储目录，本类只校验目录册、锁与临时文件本身不是链接、reparse point 或目录占位。
///         目录册损坏、版本不支持或超预算时保留原文件并明确报告，绝不按空目录册覆盖。
///     </para>
///     <para>
///         The catalog follows the current session-store directory; callers must resolve that directory
///         outside the workspace via <c>SessionStorePaths.ResolveProtected</c> first. This class only
///         validates that the catalog, lock, and temp files themselves are not links, reparse points, or
///         directory placeholders. A damaged, unsupported, or oversized catalog is kept and reported
///         explicitly, never overwritten as if empty.
///     </para>
/// </remarks>
public static class ProjectStore
{
    /// <summary>
    ///     <para>当前会话存储目录内的目录册文件名。</para>
    ///     <para>The catalog file name inside the current session-store directory.</para>
    /// </summary>
    public const string FileName = "projects.json";

    /// <summary>
    ///     <para>保存锁文件名后缀；锁以独占句柄为准，进程崩溃即由操作系统释放，残留文件不阻塞后续保存。</para>
    ///     <para>
    ///         Save-lock file name suffix; the lock is the exclusive handle, released by the OS on process
    ///         death, and a leftover file never blocks later saves.
    ///     </para>
    /// </summary>
    public const string SaveLockSuffix = ".lock";

    /// <summary>
    ///     <para>目录册文件的字节预算；读写两侧都按实际字节检查，超限保留文件并报告。</para>
    ///     <para>
    ///         The catalog file byte budget; both sides check actual bytes and keep over-limit files
    ///         while reporting them.
    ///     </para>
    /// </summary>
    public const int MaxCatalogBytes = 1 << 20;

    private const int SaveLockTimeoutMs    = 5000;
    private const int SaveLockRetryDelayMs = 25;

    /// <summary>
    ///     <para>瞬时共享冲突与打开竞态的有界重试时间窗口。</para>
    ///     <para>The bounded retry window for transient sharing conflicts and open races.</para>
    /// </summary>
    private const int TransientRetryTimeoutMs = 2000;

    /// <summary>
    ///     <para>上述重试之间的等待间隔。</para>
    ///     <para>The wait interval between such retries.</para>
    /// </summary>
    private const int TransientRetryDelayMs = 25;

    /// <summary>
    ///     <para>Win32 ERROR_SHARING_VIOLATION（32）对应的 HRESULT，仅 Windows 的共享检查会返回。</para>
    ///     <para>The HRESULT for Win32 ERROR_SHARING_VIOLATION (32), returned only by Windows sharing checks.</para>
    /// </summary>
    private const int SharingViolationHResult = unchecked((int)0x80070020);

    /// <summary>
    ///     <para>Win32 ERROR_LOCK_VIOLATION（33）对应的 HRESULT，仅 Windows 的共享检查会返回。</para>
    ///     <para>The HRESULT for Win32 ERROR_LOCK_VIOLATION (33), returned only by Windows sharing checks.</para>
    /// </summary>
    private const int LockViolationHResult = unchecked((int)0x80070021);

    /// <summary>
    ///     <para>
    ///         严格 UTF-8 解码器：非法字节抛出 <see cref="System.Text.DecoderFallbackException" />，
    ///         绝不用替换字符静默续读后把损坏目录册当作可覆盖。
    ///     </para>
    ///     <para>
    ///         The strict UTF-8 decoder: invalid bytes throw <see cref="System.Text.DecoderFallbackException" />
    ///         instead of being silently replaced, so a corrupted catalog is never treated as overwritable.
    ///     </para>
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    ///     <para>返回会话存储目录内的目录册绝对路径。</para>
    ///     <para>Returns the catalog path inside a session-store directory.</para>
    /// </summary>
    public static string FilePath(string storeDirectory) => Path.Combine(storeDirectory, FileName);

    /// <summary>
    ///     <para>
    ///         读取项目目录册：目录册确属缺失时返回空目录册。缺失判定与协议内保存者协调——打开既有
    ///         保存锁（绝不创建）等待写者完成后再复查；锁被占用时有界等待并响应取消，超时或取消明确
    ///         失败，绝不返回空目录册。目录或链接占位、损坏内容、重复属性、版本不支持、超预算或字段
    ///         非法时抛出 <see cref="ProjectStoreException" />，保留原文件，由调用方决定后续处理。
    ///     </para>
    ///     <para>
    ///         Loads the project catalog; a genuinely missing catalog yields an empty one. Absence is
    ///         coordinated with protocol savers: the existing save lock is opened (never created) to wait
    ///         out an in-flight writer before rechecking; while the lock is held, the load waits within
    ///         bounds and honors cancellation, and a timeout or cancellation fails explicitly instead of
    ///         returning an empty catalog. A directory or link placeholder, damaged content, duplicate
    ///         properties, an unsupported version, an over-budget file, or invalid fields throw
    ///         <see cref="ProjectStoreException" /> while keeping the file; the caller decides what follows.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         长度检查与内容读取使用同一打开句柄，读到的总是某一时刻的完整快照。读取句柄按
    ///         <c>FileShare.Read | FileShare.Delete</c> 打开：合作写者被限定为整体替换、不共享原地写入，
    ///         Windows 上由共享检查强制，Unix 上只是合作写者的约定。
    ///     </para>
    ///     <para>
    ///         The length check and content read use one open handle, so the result is always one complete
    ///         snapshot at a point in time. The read handle opens with <c>FileShare.Read | FileShare.Delete</c>:
    ///         cooperating writers are limited to whole-file replacement with no in-place write sharing;
    ///         Windows enforces this via share checks, while on Unix it is only a convention cooperating
    ///         writers follow.
    ///     </para>
    /// </remarks>
    public static async Task<ProjectCatalog> LoadAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        return await LoadCoreAsync(filePath, coordinateOnMissing: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         目录册读取核心流程：<paramref name="coordinateOnMissing" /> 为 true 时（公共读取入口），
    ///         目录册缺失先与协议内保存者的保存锁互斥再复查；为 false 时（已持有保存锁的写路径）跳过
    ///         该协调，避免与调用方自身锁冲突。其余校验与异常行为一致。
    ///     </para>
    ///     <para>
    ///         Core catalog load: when <paramref name="coordinateOnMissing" /> is true (the public read
    ///         entry), a missing catalog first mutually excludes protocol savers via the save lock before
    ///         rechecking; when false (writer paths already holding the save lock) that coordination is
    ///         skipped to avoid conflicting with the caller's own lock. Validation and error behavior are
    ///         otherwise identical.
    ///     </para>
    /// </summary>
    private static async Task<ProjectCatalog> LoadCoreAsync(
        string filePath, bool coordinateOnMissing, CancellationToken cancellationToken)
    {
        await using var stream =
            await OpenCatalogSnapshotAsync(filePath, coordinateOnMissing, cancellationToken).ConfigureAwait(false);
        if (stream is null) return ProjectCatalog.Empty();

        // 句柄内完成长度检查与内容读取，两者来自同一文件实例。
        if (stream.Length > MaxCatalogBytes)
            throw new
                ProjectStoreException($"Project catalog '{filePath}' exceeds its budget of {MaxCatalogBytes} bytes; keep the file and inspect it manually.");

        // 严格 UTF-8 解码并关闭 BOM 探测：非法字节按损坏报告，UTF-16/32 BOM 也不再切换解码器绕过
        // 严格校验（目录册契约只允许 UTF-8）；UTF-8 BOM 按既有行为在解码后剥离。
        using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
        string    json;
        try
        {
            json = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ProjectStoreException($"Project catalog '{filePath}' is not valid UTF-8: {ex.Message}", ex);
        }

        // UTF-8 BOM（EF BB BF）在严格解码后成为前导 U+FEFF 字符，解析前剥离以维持既有 BOM 行为。
        if (json.Length > 0 && json[0] == '\uFEFF') json = json[1..];
        ProjectCatalog? catalog;
        try
        {
            catalog = JsonSerializer.Deserialize(json, ProjectJsonContext.Default.ProjectCatalog);
        }
        catch (JsonException ex)
        {
            throw new ProjectStoreException($"Project catalog '{filePath}' is damaged: {ex.Message}", ex);
        }

        if (catalog is null)
            throw new ProjectStoreException($"Project catalog '{filePath}' must contain a JSON object.");

        catalog.Validate(filePath);
        return catalog;
    }

    /// <summary>
    ///     <para>
    ///         打开目录册快照句柄：先按名字分类缺失、目录与链接占位，占位明确拒绝。<paramref name="coordinateOnMissing" />
    ///         为 true 时，目录册缺失先打开既有保存锁与协议内写者互斥（绝不创建锁），锁内复查后仍缺失
    ///         才按空目录册返回；写者占用锁时有界等待并响应取消，超时明确失败。已观察到文件存在后打开
    ///         失败（瞬时消失或短暂占用）时有界重试，缺失重试耗尽按明确错误报告，绝不把打开失败伪装成
    ///         空目录册。
    ///     </para>
    ///     <para>
    ///         Opens a catalog snapshot handle: missing, directory, and link placeholders are classified by
    ///         name first, and placeholders are rejected. When <paramref name="coordinateOnMissing" /> is
    ///         true, a missing catalog first opens the existing save lock to mutually exclude protocol
    ///         writers (the lock is never created) and rechecks under it; only an absence persisting past
    ///         that recheck yields an empty catalog, while a held lock waits within bounds and honors
    ///         cancellation, failing explicitly on timeout. Failed opens after the file was observed retry
    ///         within bounds; exhausted absence surfaces a clear error and is never disguised as an empty
    ///         catalog.
    ///     </para>
    /// </summary>
    private static async Task<FileStream?> OpenCatalogSnapshotAsync(
        string filePath, bool coordinateOnMissing, CancellationToken cancellationToken)
    {
        var         attributes     = TryGetAttributes(filePath);
        FileStream? saveLockReader = null;
        try
        {
            if (attributes is null)
            {
                if (!coordinateOnMissing) return null;

                // 缺失时与协议内保存者协调：打开既有保存锁与写者互斥（绝不创建），锁内复查缺失。
                saveLockReader =
                    await OpenExistingSaveLockReaderAsync(filePath, cancellationToken).ConfigureAwait(false);
                attributes = TryGetAttributes(filePath);
                if (attributes is null) return null;
            }

            if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
                throw new
                    ProjectStoreException($"Project catalog '{filePath}' is a link or reparse point and is not allowed.");
            if ((attributes.Value & FileAttributes.Directory) != 0)
                throw new ProjectStoreException($"Project catalog '{filePath}' is a directory, not a catalog file.");

            var deadline = Environment.TickCount64 + TransientRetryTimeoutMs;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // FileShare.Read | FileShare.Delete 限定合作写者整体替换、不共享原地写入；
                    // Asynchronous 让读取走异步 I/O 路径。
                    return new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                          FileShare.Read | FileShare.Delete, 4096,
                                          FileOptions.Asynchronous);
                }
                catch (FileNotFoundException) when (Environment.TickCount64 < deadline)
                {
                    // 已观察到文件存在，打开时瞬时消失：短暂等待后有界重试。
                    await Task.Delay(TransientRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException ex) when ((ex.HResult is SharingViolationHResult or LockViolationHResult) &&
                                             Environment.TickCount64 < deadline)
                {
                    // Windows 共享冲突：外部程序短暂占用目标文件时有界重试；权限等其他错误立即传播。
                    await Task.Delay(TransientRetryDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (FileNotFoundException ex)
                {
                    // 观察到存在却持续打不开（重试耗尽）：明确报错，不假装目录册为空。
                    throw new
                        ProjectStoreException($"Project catalog '{filePath}' was observed but disappeared before it could be opened; inspect the store directory.",
                                              ex);
                }
            }
        }
        finally
        {
            // 读锁只保证缺失复查与快照打开不与写者交错，快照句柄打开后即可释放。
            if (saveLockReader is not null) await saveLockReader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>
    ///         以共享读取打开已存在的保存锁，与写者的独占保存锁互斥；只打开既有文件，绝不创建锁或目录。
    ///         写者占用时有界等待并响应取消，超时抛出带锁路径的 <see cref="IOException" />；锁文件本身
    ///         缺失时返回 null，表示尚无遵守协议的保存者。
    ///     </para>
    ///     <para>
    ///         Opens the existing save lock with shared read access, mutually excluding the writer's
    ///         exclusive lock; only existing files are opened, and neither the lock nor any directory is
    ///         ever created. A held lock waits within bounds and honors cancellation, throwing an
    ///         <see cref="IOException" /> naming the lock path on timeout; a missing lock file returns
    ///         null, meaning no protocol-obeying saver exists yet.
    ///     </para>
    /// </summary>
    private static async Task<FileStream?> OpenExistingSaveLockReaderAsync(
        string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath + SaveLockSuffix;
        EnsureRegularLeaf(lockPath, "Project catalog save lock");

        var deadline = Environment.TickCount64 + SaveLockTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 每次重试都重新校验锁文件叶子，保持拒绝链接与目录占位的保护边界。
            EnsureRegularLeaf(lockPath, "Project catalog save lock");
            try
            {
                return new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 锁文件本身缺失：尚无遵守协议的保存者，交回调用方按缺失复查。
                return null;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                // 写者正持有保存锁：短暂等待其释放，有界并响应取消。
                await Task.Delay(SaveLockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new
                    IOException($"Could not acquire the project-catalog save lock '{lockPath}' within " +
                                $"{SaveLockTimeoutMs} ms; another writer may be saving the same catalog. Retry the load.",
                                ex);
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         按最终生效的执行目录创建或复用项目：目录册锁内重读并精确匹配等价词法路径，
    ///         命中即返回既有项目；未命中才创建并可靠保存，保证并发调用共享同一项目。
    ///     </para>
    ///     <para>
    ///         Creates or reuses the project for the final effective execution root: re-reads and
    ///         exact-matches equivalent lexical paths inside the catalog lock, returning the existing
    ///         project on a hit, and only creating and reliably saving on a miss so concurrent callers
    ///         share one project.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         匹配不使用进程工作目录，也不向上寻找仓库根；目录当前不存在不阻止归属。
    ///         归属匹配不替代目录身份捕获、链接检查、工作区信任或运行授权。
    ///     </para>
    ///     <para>
    ///         Matching never uses the process working directory or walks up to a repository root, and a
    ///         currently missing directory does not block ownership. Ownership matching never replaces
    ///         directory-identity capture, link checks, workspace trust, or run authorization.
    ///     </para>
    /// </remarks>
    /// <exception cref="ProjectStoreException">
    ///     <para>目录非法、目录册损坏、超预算或保存锁无法获取。</para>
    ///     <para>An invalid root, a damaged catalog, a budget violation, or an unobtainable save lock.</para>
    /// </exception>
    public static async Task<Project> GetOrCreateAsync(
        string filePath, string workspaceRoot, DateTimeOffset? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var root = ProjectRoots.Normalize(workspaceRoot);
        EnsureParentDirectory(filePath);

        await using var saveLock = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        // 锁内重读目录册再匹配，避免并发生成两个项目。
        var catalog = await LoadCoreAsync(filePath, coordinateOnMissing: false, cancellationToken).ConfigureAwait(false);
        if (catalog.Projects.FirstOrDefault(project => ProjectRoots.AreEqual(project.Roots[0].Path, root)) is
            { } existing)
            return existing;

        var project = Project.CreateNew(root, utcNow ?? DateTimeOffset.UtcNow);
        await SaveAsync(filePath,
                        catalog with
                        {
                            Revision = catalog.Revision + 1,
                            Projects = [.. catalog.Projects, project]
                        },
                        cancellationToken).ConfigureAwait(false);
        return project;
    }

    /// <summary>
    ///     <para>
    ///         改名项目：目录册锁内重读，按调用方持有的修订号判断过期基线，仅更新名称、
    ///         递增修订号并刷新更新时间；ID、目录、归属与既有配置保持不变。
    ///     </para>
    ///     <para>
    ///         Renames a project: re-reads inside the catalog lock, judges a stale baseline by the
    ///         caller's held revision, and only updates the name, increments the revision, and refreshes
    ///         the updated time; ID, roots, ownership, and other configuration stay unchanged.
    ///     </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">
    ///     <para>项目不存在、修订号过期、名称非法、目录册损坏或保存锁无法获取；过期时重载后重试。</para>
    ///     <para>
    ///         A missing project, a stale revision, an invalid name, a damaged catalog, or an unobtainable
    ///         save lock; on staleness the caller reloads and retries.
    ///     </para>
    /// </exception>
    public static async Task<Project> RenameAsync(
        string filePath, string projectId, string newName, long expectedRevision,
        DateTimeOffset? utcNow = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var name = Project.ValidateName(newName);

        EnsureParentDirectory(filePath);
        await using var saveLock = await OpenSaveLockAsync(filePath, cancellationToken).ConfigureAwait(false);
        var catalog = await LoadCoreAsync(filePath, coordinateOnMissing: false, cancellationToken).ConfigureAwait(false);

        var index = -1;
        for (var i = 0; i < catalog.Projects.Count; i++)
            if (catalog.Projects[i].Id == projectId)
            {
                index = i;
                break;
            }

        if (index < 0)
            throw new ProjectStoreException($"Project '{projectId}' was not found in the catalog '{filePath}'.");

        var project = catalog.Projects[index];
        if (project.Revision != expectedRevision)
            throw new
                ProjectStoreException($"Project '{projectId}' in '{filePath}' changed while it was being edited (stored revision {project.Revision}, expected {expectedRevision}); reload it and retry.");

        var renamed = project with
        {
            Name       = name,
            Revision   = project.Revision + 1,
            UpdatedUtc = utcNow ?? DateTimeOffset.UtcNow
        };
        await SaveAsync(filePath,
                        catalog with
                        {
                            Revision = catalog.Revision + 1,
                            Projects = ReplaceAt(catalog.Projects, index, renamed)
                        },
                        cancellationToken).ConfigureAwait(false);
        return renamed;
    }

    /// <summary>
    ///     <para>
    ///         在锁内校验候选目录册、按实际写出字节检查预算，写独占创建的同目录临时文件并 flush，
    ///         校验目标叶子后以 <c>File.Replace</c>／<c>File.Move</c> 完成同目录提交；任一步失败直接抛出，
    ///         不把半写入内容提交到目标，也不返回已保存状态。
    ///     </para>
    ///     <para>
    ///         Inside the lock, validates the candidate catalog, checks the byte budget against the actual
    ///         serialized output, writes an exclusively created temp file in the same directory and flushes
    ///         it, then commits the target with <c>File.Replace</c> / <c>File.Move</c> after validating the
    ///         target leaf; any failure throws immediately, a half-written payload is never committed over
    ///         the target, and the project is never reported as saved.
    ///     </para>
    /// </summary>
    private static async Task SaveAsync(
        string filePath, ProjectCatalog catalog, CancellationToken cancellationToken)
    {
        // 先校验候选：非法输入不触碰文件系统。
        catalog.Validate(filePath);

        var content = JsonSerializer.Serialize(catalog, ProjectJsonContext.Default.ProjectCatalog) +
                      Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(content);

        // 按实际写出字节检查预算：中文名称与路径经 JSON 转义后字符数不代表字节数；
        // 超预算在替换前失败，原文件保持可读。
        if (bytes.Length > MaxCatalogBytes)
            throw new
                ProjectStoreException($"Serialized project catalog '{filePath}' exceeds its budget of {MaxCatalogBytes} bytes; keep the file and prune projects before saving again.");

        var tempPath  = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
        var created   = false;
        var committed = false;
        try
        {
            // 临时文件独占新创建：同名对象已存在（含预放置链接）时直接失败，绝不覆盖。
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                                   FileShare.None, 4096, FileOptions.Asynchronous))
            {
                // 只有本进程成功创建临时文件后才允许清理，绝不删除预放置的同名对象。
                created = true;
                await temp.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 替换前复查最终文件不是链接、reparse point 或目录占位。
            EnsureRegularLeaf(filePath, "Project catalog");

            // 已有目标 File.Replace、首次创建 File.Move 提交：不先删除或挪走目标，也没有 copy-delete 回退；
            // 与普通 flush 一样不单独证明断电持久性。
            await CommitTempIntoPlaceAsync(tempPath, filePath, cancellationToken).ConfigureAwait(false);
            committed = true;
        }
        catch
        {
            // 只清理本进程成功创建、且尚未完成替换的临时文件；清理失败不得掩盖原始错误。
            if (created && !committed) TryDeleteTempFile(tempPath);

            throw;
        }
    }

    /// <summary>
    ///     <para>
    ///         把已 flush 的临时文件提交为目录册：已有目标走 <c>File.Replace</c>，首次创建走 <c>File.Move</c>；
    ///         Windows 共享冲突在有限窗口内有界重试，其他错误立即抛出。
    ///     </para>
    ///     <para>
    ///         Commits the flushed temp file as the catalog: <c>File.Replace</c> over an existing target and
    ///         <c>File.Move</c> for first creation; Windows sharing violations retry within a bounded window,
    ///         everything else fails immediately.
    ///     </para>
    /// </summary>
    private static async Task CommitTempIntoPlaceAsync(
        string tempPath, string filePath, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + TransientRetryTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(filePath))
                    File.Replace(tempPath, filePath, null);
                else
                    File.Move(tempPath, filePath);

                return;
            }
            catch (IOException ex) when ((ex.HResult is SharingViolationHResult or LockViolationHResult) &&
                                         Environment.TickCount64 < deadline)
            {
                // Windows 共享冲突（外部程序短暂占用目标文件）时有界重试；其他错误不掩盖。
                await Task.Delay(TransientRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     <para>尽力删除尚未替换的临时文件；任何清理失败都静默忽略，避免掩盖保存过程中的原始错误。</para>
    ///     <para>
    ///         Best-effort deletes a temp file that was not moved into place; every cleanup failure is
    ///         silently ignored so the original save error is never masked.
    ///     </para>
    /// </summary>
    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 清理失败不掩盖原始错误；临时文件名为 *.tmp-*，残留不影响后续保存。
        }
    }

    /// <summary>
    ///     <para>确保目录册父目录存在，供保存与锁文件使用。</para>
    ///     <para>Ensures the catalog's parent directory exists for saving and the lock file.</para>
    /// </summary>
    private static void EnsureParentDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    /// <summary>
    ///     <para>获取目录册的跨进程保存锁；被占用时短暂等待，超时抛出带锁路径的 <see cref="IOException" />。</para>
    ///     <para>
    ///         Acquires the cross-process save lock for the catalog; waits briefly while held and throws
    ///         an <see cref="IOException" /> naming the lock path on timeout.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         获取前先拒绝目录占位与符号链接、悬空链接、reparse point 锁文件；打开前检查无法消除
    ///         检查与打开之间的固有窗口，彻底保证依赖受保护的存储目录（调用方经
    ///         <c>SessionStorePaths.ResolveProtected</c> 解析）。残留普通锁文件可重新获取。
    ///     </para>
    ///     <para>
    ///         Acquisition first rejects a directory placeholder and lock files that are symlinks, dangling
    ///         links, or reparse points; the pre-open check cannot close the inherent check-then-open gap,
    ///         and the hard guarantee is the protected store directory (resolved by the caller via
    ///         <c>SessionStorePaths.ResolveProtected</c>). A leftover regular lock file is always acquirable.
    ///     </para>
    /// </remarks>
    private static async Task<FileStream> OpenSaveLockAsync(string filePath, CancellationToken cancellationToken)
    {
        var lockPath = filePath + SaveLockSuffix;
        EnsureRegularLeaf(lockPath, "Project catalog save lock");

        var deadline = Environment.TickCount64 + SaveLockTimeoutMs;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(SaveLockRetryDelayMs, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new
                    IOException($"Could not acquire the project-catalog save lock '{lockPath}' within " +
                                $"{SaveLockTimeoutMs} ms; another writer may be saving the same catalog. Retry the save.",
                                ex);
            }
        }
    }

    /// <summary>
    ///     <para>
    ///         校验既有对象既不是符号链接、junction（含悬空链接）也不是目录；不存在时允许调用方创建。
    ///         使用按名字的属性查询，不打开句柄跟随链接；检查与后续打开之间的固有窗口由受保护的
    ///         存储目录兜底。
    ///     </para>
    ///     <para>
    ///         Validates that an existing object is neither a symlink, a junction (including dangling
    ///         links), nor a directory; missing objects are allowed so callers can create them. The
    ///         by-name attribute query never opens a handle through the link; the inherent
    ///         check-then-open gap is backstopped by the protected store directory.
    ///     </para>
    /// </summary>
    private static void EnsureRegularLeaf(string path, string description)
    {
        var attributes = TryGetAttributes(path);
        if (attributes is null) return;
        if ((attributes.Value & FileAttributes.ReparsePoint) != 0)
            throw new ProjectStoreException($"{description} '{path}' is a link or reparse point and is not allowed.");
        if ((attributes.Value & FileAttributes.Directory) != 0)
            throw new ProjectStoreException($"{description} '{path}' is a directory, not a file.");
    }

    /// <summary>
    ///     <para>按名字查询文件属性；对象不存在（含普通缺失）时返回 null，权限等真实错误继续传播。</para>
    ///     <para>
    ///         Queries file attributes by name; returns null for a missing object (including normal
    ///         absence), while real errors such as access denied keep propagating.
    ///     </para>
    /// </summary>
    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static IReadOnlyList<Project> ReplaceAt(IReadOnlyList<Project> projects, int index, Project updated)
    {
        var list = new List<Project>(projects.Count);
        for (var i = 0; i < projects.Count; i++) list.Add(i == index ? updated : projects[i]);
        return list;
    }
}

[JsonSerializable(typeof(ProjectCatalog))]
[JsonSerializable(typeof(Project))]
[JsonSerializable(typeof(ProjectRoot))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                             AllowDuplicateProperties = false,
                             WriteIndented = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class ProjectJsonContext : JsonSerializerContext;
