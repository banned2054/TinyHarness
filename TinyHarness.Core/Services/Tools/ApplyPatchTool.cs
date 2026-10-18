using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         项目唯一的写入工具。在 Prepare 阶段解析 unified diff、把所有目标路径解析进工作区并生成不可变计划；
///         权限引擎审批该计划，Execute 只应用同一计划，执行期不再重新解析原始模型输入。全部目标先在内存中匹配，
///         任一 hunk 失败都不会写文件。
///     </para>
///     <para>
///         The apply_patch write tool accepts a unified diff, parses it in Prepare, resolves every
///         target path into the workspace and records the resulting immutable plan in the prepared
///         arguments. The Permission Engine approves the plan's target paths; Execute applies exactly
///         that plan and never re-parses or re-resolves raw model input. All targets are matched in
///         memory before the first write, so a failed hunk leaves every file untouched.
///     </para>
/// </summary>
public sealed class ApplyPatchTool(Workspace workspace) : ITool
{
    private const int MaxPatchChars = 256 * 1024;
    private const int MaxFiles      = 64;
    private const int MaxFileBytes  = 4  * 1024 * 1024;
    private const int MaxTotalBytes = 16 * 1024 * 1024;

    // 路径同一性沿用 Workspace.IsInside 的 OS 规则，Windows 上仍能检出仅大小写不同的重复目标。
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["patch"] = new JsonObject
            {
                ["type"]      = "string",
                ["maxLength"] = MaxPatchChars,
                ["description"] =
                    "A unified diff to apply. Use one or more file sections of the form "                     +
                    "'--- a/path', '+++ b/path', then '@@ -s[,c] +s[,c] @@' hunks whose lines are "           +
                    "prefixed with ' ' (context), '-' (remove) or '+' (add). New files use "                  +
                    "'--- /dev/null'. File deletion is not supported. "                                       +
                    "Limits: 262144 UTF-16 code units of patch text, 64 files, 4 MiB per input/output file, " +
                    "and 16 MiB of combined input/output per call."
            }
        },
        ["required"] = new JsonArray("patch")
    };

    // 测试专用注入点；只在写入尝试登记 unknown 后、打开文件前触发。
    internal Action<string>? WriteAttemptStarting { get; init; }

    /// <summary>
    ///     <para>测试注入点，在单文件事实确认为已写入后调用。</para>
    ///     <para>A test hook invoked after a file fact has been confirmed written.</para>
    /// </summary>
    internal Action<string>? WriteCompleted { get; init; }

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "apply_patch",
        Description = "Applies a unified diff to one or more workspace files. Returns the files modified.",
        Parameters  = Schema
    };

    /// <summary>
    ///     <para>校验补丁大小和文件数、解析所有 hunk、规范化目标路径，并序列化权威执行计划供审批。</para>
    ///     <para>
    ///         Validates patch/file limits, parses hunks, normalizes targets, and serializes the authoritative plan
    ///         for approval.
    ///     </para>
    /// </summary>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args      = ToolArgs.ParseObject(call);
        var patchText = JsonArgs.Required(args, "patch");
        if (patchText.Length > MaxPatchChars)
            throw new InvalidDataException($"apply_patch: patch exceeds the {MaxPatchChars} character limit.");

        var parsed = PatchParser.Parse(patchText);
        if (parsed.Count > MaxFiles)
            throw new InvalidDataException($"apply_patch: patch exceeds the {MaxFiles} file limit.");

        var plan    = new JsonArray();
        var targets = new List<string>();
        var seen    = new HashSet<string>(PathComparer);
        foreach (var file in parsed)
        {
            var absolute = workspace.ResolveInside(file.Path, "patch path");
            if (!seen.Add(absolute))
                throw new
                    InvalidDataException($"apply_patch: '{workspace.ToDisplay(absolute)}' is targeted more than once by this patch; " +
                                         "merge the hunks for one file into a single '--- a/... +++ b/...' section.");

            targets.Add(absolute);

            var hunks = new JsonArray();
            foreach (var hunk in file.Hunks)
                hunks.Add((JsonNode)new JsonObject
                {
                    ["oldStart"] = hunk.OldStart,
                    ["oldCount"] = hunk.OldCount,
                    ["newStart"] = hunk.NewStart,
                    ["newCount"] = hunk.NewCount,
                    ["oldLines"] = ToArray(hunk.OldLines),
                    ["newLines"] = ToArray(hunk.NewLines)
                });

            plan.Add((JsonNode)new JsonObject
            {
                ["path"]      = absolute,
                ["isNewFile"] = file.IsNewFile,
                ["hunks"]     = hunks
            });
        }

        args["patch"] = patchText; // 原样保留原始输入，供审计与展示。
        args["_plan"] = plan;      // 已解析的权威计划；Execute 只读此字段。

        var display = targets.Select(workspace.ToDisplay).ToList();
        return new ToolPreparation
        {
            ToolName          = Definition.Name,
            CallId            = call.Id,
            Arguments         = args,
            Capability        = "filesystem.write",
            Summary           = $"apply_patch: modify {display.Count} file(s): {string.Join(", ", display)}",
            TargetPaths       = targets,
            InstructionScopes = targets.Select(path => new InstructionScope(path, InstructionScopeKind.File)).ToArray()
        };
    }

    /// <summary>
    ///     <para>分两阶段执行准备计划：先读取并在内存中应用全部文件，再在全部成功后顺序提交写入。</para>
    ///     <para>
    ///         Executes the prepared plan in two phases: apply every file in memory, then commit writes only after
    ///         all succeed.
    ///     </para>
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var plan    = ReadPlan(preparation);
        var changes = new List<PatchChange>();

        try
        {
            foreach (var file in plan)
                preparation.Coverage?.EnsureCoveredDirectory(Path.GetDirectoryName(file.Path)!);
            // 第一阶段：逐目标读取并在内存中应用 hunk；任何不匹配都在写入前抛出。
            var applied    = new List<(string Path, string Text, bool IsNew)>(plan.Count);
            var totalBytes = 0;
            foreach (var file in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var absolute = file.Path;
                if (Directory.Exists(absolute))
                    return new ToolResult
                    {
                        Succeeded = false,
                        Content   = $"{workspace.ToDisplay(absolute)} is a directory; apply_patch cannot modify it."
                    };

                var exists = File.Exists(absolute);
                switch (exists)
                {
                    case true when file.IsNewFile :
                        return new ToolResult
                        {
                            Succeeded = false,
                            Content =
                                $"File already exists: {workspace.ToDisplay(absolute)}; a new-file patch cannot modify it."
                        };
                    case false when !file.IsNewFile :
                        return new ToolResult
                        {
                            Succeeded = false,
                            Content   = $"File not found: {workspace.ToDisplay(absolute)}"
                        };
                    // 执行期复检最终目标：修改场景叶子已存在可直接检查；新文件路径尚不存在
                    // （链接解析会抛出），改经最近存在的祖先目录校验。
                    case true :
                        workspace.EnsureFinalTargetInside(absolute, false, "File");
                        break;
                    default :
                        EnsureNewFileInside(workspace, absolute);
                        break;
                }

                var original = string.Empty;
                if (exists)
                {
                    var input = await ReadOriginalAsync(absolute, Math.Min(MaxFileBytes, MaxTotalBytes - totalBytes),
                                                        cancellationToken).ConfigureAwait(false);
                    original   =  input.Text;
                    totalBytes += input.Bytes;
                }

                var text = PatchApplier.Apply(original, file.Hunks);
                if (file.IsNewFile && text.Length > 0 && !text.EndsWith('\n')) text += '\n';

                var outputBytes = Encoding.UTF8.GetByteCount(text);
                if (outputBytes > MaxFileBytes)
                    throw new
                        InvalidDataException($"Output for '{workspace.ToDisplay(absolute)}' exceeds the 4 MiB file limit.");

                totalBytes += outputBytes;
                if (totalBytes > MaxTotalBytes)
                    throw new InvalidDataException("Patch exceeds the 16 MiB combined input/output budget.");

                applied.Add((absolute, text, file.IsNewFile));
            }

            // 第二阶段：全部目标匹配成功，开始提交写入。
            for (var index = 0; index < applied.Count; index++)
            {
                var (path, text, isNew) = applied[index];
                cancellationToken.ThrowIfCancellationRequested();
                if (isNew)
                {
                    EnsureNewFileInside(workspace, path);
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                }
                else
                {
                    workspace.EnsureFinalTargetInside(path, false, "File");
                }

                var file   = plan[index];
                var change = CreateChange(file, PatchChange.UnknownState);
                changes.Add(change);
                WriteAttemptStarting?.Invoke(path);

                // CreateNew 同时防止覆盖校验阶段之后才出现的文件（含并发进程创建的文件）。
                await using (var stream = new FileStream(path, isNew ? FileMode.CreateNew : FileMode.Create,
                                                         FileAccess.Write, FileShare.None, 64 * 1024,
                                                         FileOptions.Asynchronous))
                {
                    await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                    await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                // writer 和 stream 都成功释放后，才能升级为 confirmed。
                changes[^1] = change with { State = PatchChange.ConfirmedState };
                WriteCompleted?.Invoke(path);
            }

            var changed = plan.Select(f => workspace.ToDisplay(f.Path)).ToList();
            return new ToolResult
            {
                Succeeded    = true,
                Content      = $"Applied patch to {changed.Count} file(s): {string.Join(", ", changed)}",
                PatchChanges = changes.ToArray()
            };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            if (changes.Count == 0) throw;

            // 只有写入已开始才携带结果；无副作用取消保持普通取消语义。
            throw new ToolExecutionCanceledException(new ToolResult
            {
                Succeeded           = false,
                ExecutionIncomplete = true,
                Content =
                    "apply_patch was cancelled; recorded file states may be partial.",
                PatchChanges = changes.ToArray()
            }, cancellationToken, ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ToolResult
            {
                Succeeded           = false,
                ExecutionIncomplete = changes.Count > 0,
                Content             = $"apply_patch failed: {ex.Message}",
                PatchChanges        = changes.Count == 0 ? null : changes.ToArray()
            };
        }
    }

    /// <summary>
    ///     <para>从已解析计划生成一个带当时 unified diff 的文件变更事实。</para>
    ///     <para>Creates one file-change fact with its original unified diff from the resolved plan.</para>
    /// </summary>
    private PatchChange CreateChange(ResolvedPatchFile file, string state)
    {
        var display = workspace.ToDisplay(file.Path);
        return new PatchChange(
                               display,
                               file.IsNewFile ? PatchChange.CreateOperation : PatchChange.ModifyOperation,
                               BuildDiff(display, file.IsNewFile, file.Hunks),
                               state);
    }

    /// <summary>
    ///     <para>从 hunk 计划重建每个文件的统一差异，使结果不依赖后来的工作区内容。</para>
    ///     <para>Rebuilds each file’s unified diff from planned hunks so facts do not depend on later workspace contents.</para>
    /// </summary>
    private static string BuildDiff(string displayPath, bool isNewFile, IReadOnlyList<PatchHunk> hunks)
    {
        var builder = new StringBuilder();
        builder.Append("--- ").Append(isNewFile ? "/dev/null" : $"a/{displayPath}").Append('\n');
        builder.Append("+++ b/").Append(displayPath).Append('\n');

        foreach (var hunk in hunks)
        {
            builder.Append("@@ -").Append(hunk.OldStart).Append(',').Append(hunk.OldCount)
                   .Append(" +").Append(hunk.NewStart).Append(',').Append(hunk.NewCount)
                   .Append(" @@\n");

            // 先匹配公共前后缀；中间按删除加新增重建，首尾显式输出上下文。
            var prefix = 0;
            while (prefix < hunk.OldLines.Count && prefix < hunk.NewLines.Count &&
                   string.Equals(hunk.OldLines[prefix], hunk.NewLines[prefix], StringComparison.Ordinal))
                prefix++;

            var suffix = 0;
            while (suffix < hunk.OldLines.Count - prefix && suffix < hunk.NewLines.Count - prefix &&
                   string.Equals(hunk.OldLines[^(suffix + 1)], hunk.NewLines[^(suffix + 1)], StringComparison.Ordinal))
                suffix++;

            for (var index = 0; index < prefix; index++)
                builder.Append(' ').Append(hunk.OldLines[index]).Append('\n');

            for (var index = prefix; index < hunk.OldLines.Count - suffix; index++)
                builder.Append('-').Append(hunk.OldLines[index]).Append('\n');

            for (var index = prefix; index < hunk.NewLines.Count - suffix; index++)
                builder.Append('+').Append(hunk.NewLines[index]).Append('\n');

            for (var index = hunk.OldLines.Count - suffix; index < hunk.OldLines.Count; index++)
                builder.Append(' ').Append(hunk.OldLines[index]).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    ///     <para>在单文件及调用总预算内异步读取原文件，并额外探测一个字节以发现读取期间增长。</para>
    ///     <para>
    ///         Reads the original file within per-file/aggregate budgets and probes one extra byte to detect
    ///         concurrent growth.
    ///     </para>
    /// </summary>
    private static async Task<(string Text, int Bytes)> ReadOriginalAsync(
        string path, int byteLimit, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > byteLimit)
            throw new
                InvalidDataException($"Input '{path}' exceeds the {byteLimit} byte read limit (4 MiB per file, 16 MiB combined input/output budget).");

        using var bytes  = new MemoryStream();
        var       buffer = new byte[64 * 1024];
        while (true)
        {
            // 比剩余预算多探测一个字节，文件增长不会把长度检查变成无限读取或静默截断。
            var count = Math.Min(buffer.Length, byteLimit - (int)bytes.Length + 1);
            var read  = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            if (bytes.Length + read > byteLimit)
                throw new InvalidDataException($"Input '{path}' grew beyond the {byteLimit} byte read limit.");

            bytes.Write(buffer, 0, read);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, true);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), (int)bytes.Length);
    }

    /// <summary>
    ///     <para>从准备参数反序列化内部权威计划；字段缺失或结构损坏会拒绝执行。</para>
    ///     <para>Deserializes the authoritative internal plan and rejects missing or malformed fields.</para>
    /// </summary>
    private static IReadOnlyList<ResolvedPatchFile> ReadPlan(ToolPreparation preparation)
    {
        var node = preparation.Arguments["_plan"] as JsonArray
                ?? throw new InvalidDataException("apply_patch: missing prepared plan.");

        var files = new List<ResolvedPatchFile>(node.Count);
        foreach (var item in node)
        {
            var obj = item as JsonObject
                   ?? throw new InvalidDataException("apply_patch: malformed prepared plan.");

            var path = obj["path"]?.GetValue<string>()
                    ?? throw new InvalidDataException("apply_patch: prepared plan is missing a path.");
            var isNew = obj["isNewFile"]?.GetValue<bool>() ?? false;

            var hunks = new List<PatchHunk>();
            foreach (var hunkNode in obj["hunks"] as JsonArray ?? [])
            {
                var hunk = hunkNode as JsonObject
                        ?? throw new InvalidDataException("apply_patch: malformed prepared hunk.");
                hunks.Add(new PatchHunk(hunk["oldStart"]?.GetValue<int>() ??
                                        throw new InvalidDataException("apply_patch: missing oldStart."),
                                        hunk["oldCount"]?.GetValue<int>() ??
                                        throw new InvalidDataException("apply_patch: missing oldCount."),
                                        hunk["newStart"]?.GetValue<int>() ??
                                        throw new InvalidDataException("apply_patch: missing newStart."),
                                        hunk["newCount"]?.GetValue<int>() ??
                                        throw new InvalidDataException("apply_patch: missing newCount."),
                                        ReadStringArray(hunk, "oldLines"),
                                        ReadStringArray(hunk, "newLines")));
            }

            files.Add(new ResolvedPatchFile(path, isNew, hunks));
        }

        return files;
    }

    /// <summary>
    ///     <para>从准备好的 hunk 对象复制字符串行数组。</para>
    ///     <para>Copies a string-line array from a prepared hunk object.</para>
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(JsonObject hunk, string key)
    {
        var array = hunk[key] as JsonArray ?? [];
        var lines = new List<string>(array.Count);
        foreach (var item in array) lines.Add(item?.GetValue<string>() ?? string.Empty);

        return lines;
    }

    /// <summary>
    ///     <para>
    ///         对尚不存在的新文件检查最近的已有祖先目录，防止经符号链接或 junction 逃逸工作区；
    ///         词法范围已由 Prepare 约束，这里复检最近存在祖先的链接链（叶子创建前不可能是链接）。
    ///     </para>
    ///     <para>
    ///         Verifies that a not-yet-existing file cannot escape the workspace through a symlinked
    ///         ancestor directory. The lexical path was already confined by Prepare; this re-checks
    ///         the nearest existing ancestor's link chain, since the leaf itself cannot be a link
    ///         before it is created.
    ///     </para>
    /// </summary>
    private static void EnsureNewFileInside(Workspace workspace, string absolute)
    {
        var ancestor = Path.GetDirectoryName(absolute);

        while (ancestor is not null && !Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor);

        if (ancestor is not null) workspace.EnsureFinalTargetInside(ancestor, true, "Directory");
    }

    /// <summary>
    ///     <para>把文本行复制为独立 JSON 数组，供不可变准备计划保存。</para>
    ///     <para>Copies text lines into an independent JSON array for the immutable prepared plan.</para>
    /// </summary>
    private static JsonArray ToArray(IEnumerable<string> lines)
    {
        var array = new JsonArray();
        foreach (var line in lines) array.Add((JsonNode?)JsonValue.Create(line));

        return array;
    }

    private sealed record ResolvedPatchFile(string Path, bool IsNewFile, IReadOnlyList<PatchHunk> Hunks);
}
