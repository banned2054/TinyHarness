using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.ProjectInstructions;

/// <summary>
///     <para>在固定工作区内有界发现并严格读取项目规则；不执行模型、授权或持久化操作。</para>
///     <para>
///         Discovers and strictly reads bounded project rules inside a fixed workspace, without model, permission, or
///         persistence operations.
///     </para>
/// </summary>
public sealed class ProjectInstructionLoader(Workspace workspace)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>
    ///     <para>按目标祖先链替换规则集合；无工具范围时刷新当前集合，保留读取预算导致的省略事实。</para>
    ///     <para>
    ///         Replaces rules with target ancestor chains, or refreshes the active set when scopes are empty,
    ///         retaining omissions caused by read budgets.
    ///     </para>
    /// </summary>
    public async Task<ProjectInstructionSnapshot> LoadAsync(ProjectInstructionSnapshot? previous,
                                                            IReadOnlyList<InstructionScope> scopes,
                                                            CancellationToken cancellationToken)
    {
        try
        {
            return await LoadCandidateAsync(previous, scopes, cancellationToken).ConfigureAwait(false);
        }
        catch (InstructionScopeLimitException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    /// <summary>
    ///     <para>供工具派发边界区分可缩小范围的超限与不可恢复的规则读取失败。</para>
    ///     <para>Lets the tool dispatch boundary distinguish limits that permit a narrower scope from rule-read failures.</para>
    /// </summary>
    internal Task<ProjectInstructionSnapshot> LoadForToolScopeAsync(
        ProjectInstructionSnapshot? previous, IReadOnlyList<InstructionScope> scopes,
        CancellationToken cancellationToken) =>
        LoadCandidateAsync(previous, scopes, cancellationToken);

    private async Task<ProjectInstructionSnapshot> LoadCandidateAsync(
        ProjectInstructionSnapshot? previous, IReadOnlyList<InstructionScope> scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scopes);
        cancellationToken.ThrowIfCancellationRequested();
        if (previous is not null) ProjectInstructionRenderer.Validate(previous);
        var rootAttributes = Attributes(workspace.Root);
        if (rootAttributes is null || (rootAttributes.Value & FileAttributes.Directory) == 0)
            throw new
                InvalidDataException($"Project instruction workspace '{workspace.Root}' does not exist or is not a directory.");
        var directories = new HashSet<string>(PathComparer);
        AddChain(workspace.Root, directories);
        if (scopes.Count == 0)
            foreach (var directory in previous?.ProbedDirectories ?? [])
                AddChain(workspace.ResolveInside(directory, "tracked instruction directory"), directories);
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(scope.Kind) || scope.MaxDepth < 0 ||
                scope.MaxEntries is < 1 or > ProjectInstructionLimits.MaxDiscoveryEntries)
                throw new InvalidDataException("Invalid project instruction access scope or traversal budget.");
            if (Path.IsPathRooted(scope.Path) &&
                !Workspace.IsInside(workspace.Root, Path.GetFullPath(scope.Path)))
                continue;
            var target = workspace.ResolveInside(scope.Path, "instruction scope");
            var directory = scope.Kind == InstructionScopeKind.File ? Path.GetDirectoryName(target)! : target;
            var attributes = scope.Kind != InstructionScopeKind.File ? Attributes(target) : null;
            if (attributes is { } targetAttributes && (targetAttributes & FileAttributes.Directory) == 0)
                directory = Path.GetDirectoryName(target)!;
            AddChain(directory, directories);
            EnsureNoLinks(directory, true);
        }

        var documents = new List<ProjectInstructionDocument>();
        var omissions = new List<ProjectInstructionOmission>();
        var totalBytes = 0;
        // 读取与渲染都优先保留更具体的规则；同深度按路径稳定排序。
        foreach (var relativeDirectory in directories
                                         .OrderByDescending(path => path == "." ? 0 : path.Count(c => c == '/') + 1)
                                         .ThenBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteDirectory = workspace.ResolveInside(relativeDirectory, "instruction directory");
            EnsureNoLinks(absoluteDirectory, true);
            var source = Path.Combine(absoluteDirectory, "AGENTS.md");
            var document = await ReadDocumentAsync(source, relativeDirectory,
                                                   ProjectInstructionLimits.MaxTotalBytes - totalBytes,
                                                   omissions, cancellationToken).ConfigureAwait(false);
            if (document is not null)
            {
                documents.Add(document);
                totalBytes += document.ByteCount;
            }

            if (documents.Count + omissions.Count > ProjectInstructionLimits.MaxDocuments)
                throw new
                    InstructionScopeLimitException($"Project instructions exceed the 64-document budget at '{workspace.ToDisplay(source)}'. Narrow the operation.");
        }

        return ProjectInstructionRenderer.CreateWithOmissions(documents, directories.ToArray(), omissions);
    }

    /// <summary>
    ///     <para>加入根到目标的全部祖先，包含尚不存在的目录。</para>
    ///     <para>Adds every ancestor from the root to the target, including missing directories.</para>
    /// </summary>
    private void AddChain(string absoluteDirectory, HashSet<string> directories)
    {
        var current = workspace.ResolveInside(absoluteDirectory, "instruction directory");
        while (true)
        {
            directories.Add(workspace.ToDisplay(current));
            if (directories.Count > ProjectInstructionLimits.MaxProbedDirectories)
                throw new
                    InstructionScopeLimitException("Project instruction discovery exceeds 2,048 tracked directories; narrow the operation.");
            if (PathComparer.Equals(current.TrimEnd(Path.DirectorySeparatorChar),
                                    workspace.Root.TrimEnd(Path.DirectorySeparatorChar))) break;
            current = Path.GetDirectoryName(current)
                   ?? throw new InvalidDataException("Instruction discovery could not reach the workspace root.");
        }
    }

    /// <summary>
    ///     <para>严格限长读取规则，保留 BOM 身份；已观测不稳定时只重试一次。</para>
    ///     <para>Reads rules under a strict length bound, retaining BOM identity and retrying observed instability only once.</para>
    /// </summary>
    private async Task<ProjectInstructionDocument?> ReadDocumentAsync(
        string source, string scope, int remainingBytes, List<ProjectInstructionOmission> omissions,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            EnsureNoLinks(source, false);
            var attributes = Attributes(source);
            if (attributes is null) return null;
            if ((attributes.Value & FileAttributes.Directory) != 0)
                throw new InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' is a directory.");
            var before = new FileInfo(source);
            var length = before.Length;
            var timestamp = before.LastWriteTimeUtc;
            if (length > ProjectInstructionLimits.MaxDocumentBytes || length > remainingBytes)
            {
                omissions.Add(new ProjectInstructionOmission
                {
                    SourcePath = workspace.ToDisplay(source),
                    ScopeDirectory = scope,
                    ByteCount = length,
                    LastWriteUtc = timestamp,
                    Reason = length > ProjectInstructionLimits.MaxDocumentBytes
                        ? "source-byte-limit"
                        : "total-byte-limit"
                });
                return null;
            }

            try
            {
                await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                                                        FileShare.ReadWrite | FileShare.Delete, 4_096,
                                                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[Math.Min(ProjectInstructionLimits.MaxDocumentBytes, remainingBytes) + 1];
                var count = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }

                EnsureNoLinks(source, false);
                var after = new FileInfo(source);
                if (!after.Exists || after.Length != length || count != length || after.LastWriteTimeUtc != timestamp)
                {
                    if (attempt == 0) continue;
                    throw new
                        InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' changed during both reads.");
                }

                var bom = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;
                var offset = bom ? 3 : 0;
                string content;
                try
                {
                    content = StrictUtf8.GetString(buffer, offset, count - offset);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new
                        InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' is not valid UTF-8.",
                                             ex);
                }

                if (content.Length == 0) return null;
                return new ProjectInstructionDocument
                {
                    SourcePath = workspace.ToDisplay(source),
                    ScopeDirectory = scope,
                    Content = content,
                    ContentHash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))),
                    ByteCount = count,
                    HasUtf8Bom = bom,
                    LoadedUtc = DateTimeOffset.UtcNow
                };
            }
            catch (FileNotFoundException) when (attempt == 0)
            {
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
            }
            catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) and not InvalidDataException)
            {
                throw new IOException($"Cannot read instruction source '{workspace.ToDisplay(source)}': {ex.Message}",
                                      ex);
            }
        }

        throw new
            InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' could not be read consistently.");
    }

    /// <summary>
    ///     <para>拒绝自动规则链中的重解析点，包括指令文件自身与工作区根。</para>
    ///     <para>Rejects reparse points in automatic instruction chains, including the source file and workspace root.</para>
    /// </summary>
    private void EnsureNoLinks(string absolutePath, bool isDirectory)
    {
        workspace.ResolveInside(absolutePath, "instruction path");
        var current = absolutePath;
        while (Workspace.IsInside(workspace.Root, current))
        {
            var attributes = Attributes(current);
            if (attributes is { } value && (value & FileAttributes.ReparsePoint) != 0)
                throw new
                    InvalidDataException($"Instruction discovery refuses linked path '{workspace.ToDisplay(current)}'.");
            if (PathComparer.Equals(current.TrimEnd(Path.DirectorySeparatorChar),
                                    workspace.Root.TrimEnd(Path.DirectorySeparatorChar))) break;
            current = Path.GetDirectoryName(current)
                   ?? throw new InvalidDataException("Instruction path could not reach the workspace root.");
        }
    }

    /// <summary>
    ///     <para>只有明确缺失返回 null，权限或 I/O 故障保持失败。</para>
    ///     <para>Returns null only for explicit absence, preserving access and I/O failures.</para>
    /// </summary>
    private static FileAttributes? Attributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
