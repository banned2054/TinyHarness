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
    ///     <para>重新探测根、全部已知位置和本次工具范围；全部读取与预算通过后返回完整候选。</para>
    ///     <para>
    ///         Reprobes the root, all tracked locations, and current tool scopes, returning a complete candidate only after
    ///         all reads and budgets pass.
    ///     </para>
    /// </summary>
    public async Task<ProjectInstructionSnapshot> LoadAsync(ProjectInstructionSnapshot?     previous,
                                                            IReadOnlyList<InstructionScope> scopes,
                                                            CancellationToken               cancellationToken)
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
        foreach (var directory in previous?.ProbedDirectories ?? [])
            AddChain(workspace.ResolveInside(directory, "tracked instruction directory"), directories);
        foreach (var scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(scope.Kind) || scope.MaxDepth < 0 ||
                scope.MaxEntries is < 1 or > ProjectInstructionLimits.MaxDiscoveryEntries)
                throw new InvalidDataException("Invalid project instruction access scope or traversal budget.");
            var target     = workspace.ResolveInside(scope.Path, "instruction scope");
            var directory  = scope.Kind == InstructionScopeKind.File ? Path.GetDirectoryName(target)! : target;
            var attributes = scope.Kind == InstructionScopeKind.RecursiveDirectory ? Attributes(target) : null;
            if (attributes is { } targetAttributes && (targetAttributes & FileAttributes.Directory) == 0)
                directory = Path.GetDirectoryName(target)!;
            AddChain(directory, directories);
            EnsureNoLinks(directory, true);
            if (scope.Kind != InstructionScopeKind.RecursiveDirectory || attributes is null ||
                (attributes.Value & FileAttributes.Directory) == 0) continue;
            var walk = scope.FilesOnly
                ? DirectoryWalker.CollectFiles(target, scope.MaxEntries, cancellationToken, strictErrors : true)
                : DirectoryWalker.CollectEntries(target, true, scope.MaxDepth == int.MaxValue ? null : scope.MaxDepth,
                                                 scope.MaxEntries, cancellationToken, strictErrors : true);
            if (walk.EnumerationLimited ||
                (walk.Truncated && scope.MaxEntries == ProjectInstructionLimits.MaxDiscoveryEntries))
                throw new
                    InvalidDataException($"Instruction discovery for '{workspace.ToDisplay(target)}' exceeds the 16,384-entry budget; narrow the operation.");
            foreach (var visitedDirectory in walk.VisitedDirectories)
                AddChain(visitedDirectory, directories);
            foreach (var entry in walk.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entryAttributes = Attributes(entry);
                if (entryAttributes is null || (entryAttributes.Value & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory    = (entryAttributes.Value & FileAttributes.Directory) != 0;
                var entryDirectory = isDirectory ? entry : Path.GetDirectoryName(entry)!;
                var relative       = Path.GetRelativePath(target, entryDirectory);
                var depth          = relative == "." ? 0 : relative.Split(Path.DirectorySeparatorChar).Length;
                if (depth <= scope.MaxDepth) AddChain(entryDirectory, directories);
            }
        }

        var documents  = new List<ProjectInstructionDocument>();
        var totalBytes = 0;
        foreach (var relativeDirectory in directories.OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var absoluteDirectory = workspace.ResolveInside(relativeDirectory, "instruction directory");
            EnsureNoLinks(absoluteDirectory, true);
            var source   = Path.Combine(absoluteDirectory, "AGENTS.md");
            var document = await ReadDocumentAsync(source, relativeDirectory, cancellationToken).ConfigureAwait(false);
            if (document is null) continue;
            documents.Add(document);
            totalBytes += document.ByteCount;
            if (documents.Count > ProjectInstructionLimits.MaxDocuments ||
                totalBytes      > ProjectInstructionLimits.MaxTotalBytes)
                throw new
                    InvalidDataException($"Project instructions exceed the 64-document or 128-KiB budget at '{document.SourcePath}'. Narrow the operation.");
        }

        return ProjectInstructionRenderer.Create(documents, directories.ToArray());
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
                    InvalidDataException("Project instruction discovery exceeds 2,048 tracked directories; narrow the operation.");
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
        string source, string scope, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            EnsureNoLinks(source, false);
            var attributes = Attributes(source);
            if (attributes is null) return null;
            if ((attributes.Value & FileAttributes.Directory) != 0)
                throw new InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' is a directory.");
            var before    = new FileInfo(source);
            var length    = before.Length;
            var timestamp = before.LastWriteTimeUtc;
            if (length > ProjectInstructionLimits.MaxDocumentBytes)
                throw new InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' exceeds 32 KiB.");
            try
            {
                await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read,
                                                        FileShare.ReadWrite      | FileShare.Delete, 4_096,
                                                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[ProjectInstructionLimits.MaxDocumentBytes + 1];
                var count  = 0;
                while (count < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }

                if (count > ProjectInstructionLimits.MaxDocumentBytes)
                    throw new
                        InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' exceeds 32 KiB.");
                EnsureNoLinks(source, false);
                var after = new FileInfo(source);
                if (!after.Exists || after.Length != length || count != length || after.LastWriteTimeUtc != timestamp)
                {
                    if (attempt == 0) continue;
                    throw new
                        InvalidDataException($"Instruction source '{workspace.ToDisplay(source)}' changed during both reads.");
                }

                var    bom    = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF;
                var    offset = bom ? 3 : 0;
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
                    SourcePath     = workspace.ToDisplay(source),
                    ScopeDirectory = scope,
                    Content        = content,
                    ContentHash    = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, count))),
                    ByteCount      = count,
                    HasUtf8Bom     = bom,
                    LoadedUtc      = DateTimeOffset.UtcNow
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
