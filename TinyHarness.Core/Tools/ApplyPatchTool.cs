using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Tools;

/// <summary>
/// 项目唯一的写入工具。在 Prepare 阶段解析 unified diff、解析所有目标路径并生成不可变计划；
/// 权限引擎审批该计划，Execute 只应用同一计划。全部目标先在内存中匹配，任一 hunk 失败都不会写文件。
///
/// The apply_patch write tool accepts a unified diff, parses
/// it in Prepare, resolves every target path into the workspace and records the
/// resulting immutable plan in the prepared arguments. The Permission Engine
/// approves the plan's target paths; Execute applies exactly that plan and never
/// re-parses or re-resolves raw model input. All targets are matched in memory
/// before the first write, so a failed hunk leaves every file untouched.
/// </summary>
public sealed class ApplyPatchTool(Workspace workspace) : ITool
{
    private const int MaxPatchChars = 256 * 1024;
    private const int MaxFiles      = 64;
    private const int MaxFileBytes  = 4  * 1024 * 1024;
    private const int MaxTotalBytes = 16 * 1024 * 1024;

    // Path identity follows the OS rules used by Workspace.IsInside, so a
    // duplicate declared with different casing is still detected on Windows.
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
                    "and 16 MiB of combined input/output per call.",
            },
        },
        ["required"] = new JsonArray("patch"),
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "apply_patch",
        Description = "Applies a unified diff to one or more workspace files. Returns the files modified.",
        Parameters  = Schema,
    };

    /// <summary>
    /// 校验补丁大小和文件数、解析所有 hunk、规范化目标路径，并序列化权威执行计划供审批。
    /// Validates patch/file limits, parses hunks, normalizes targets, and serializes the authoritative plan for approval.
    /// </summary>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args      = ToolArgs.ParseObject(call);
        var patchText = JsonArgs.Required(args, "patch");
        if (patchText.Length > MaxPatchChars)
        {
            throw new InvalidDataException($"apply_patch: patch exceeds the {MaxPatchChars} character limit.");
        }

        var parsed = PatchParser.Parse(patchText);
        if (parsed.Count > MaxFiles)
        {
            throw new InvalidDataException($"apply_patch: patch exceeds the {MaxFiles} file limit.");
        }

        var plan    = new JsonArray();
        var targets = new List<string>();
        var seen    = new HashSet<string>(PathComparer);
        foreach (var file in parsed)
        {
            var absolute = workspace.ResolveInside(file.Path, "patch path");
            if (!seen.Add(absolute))
            {
                throw new
                    InvalidDataException($"apply_patch: '{workspace.ToDisplay(absolute)}' is targeted more than once by this patch; " +
                                         "merge the hunks for one file into a single '--- a/... +++ b/...' section.");
            }

            targets.Add(absolute);

            var hunks = new JsonArray();
            foreach (var hunk in file.Hunks)
            {
                hunks.Add((JsonNode)new JsonObject
                {
                    ["oldStart"] = hunk.OldStart,
                    ["oldCount"] = hunk.OldCount,
                    ["newStart"] = hunk.NewStart,
                    ["newCount"] = hunk.NewCount,
                    ["oldLines"] = ToArray(hunk.OldLines),
                    ["newLines"] = ToArray(hunk.NewLines),
                });
            }

            plan.Add((JsonNode)new JsonObject
            {
                ["path"]      = absolute,
                ["isNewFile"] = file.IsNewFile,
                ["hunks"]     = hunks,
            });
        }

        args["patch"] = patchText; // Original input kept verbatim for audit/display.
        args["_plan"] = plan;      // Authoritative resolved plan; Execute reads only this.

        var display = targets.Select(workspace.ToDisplay).ToList();
        return new ToolPreparation
        {
            ToolName    = Definition.Name,
            CallId      = call.Id,
            Arguments   = args,
            Capability  = "filesystem.write",
            Summary     = $"apply_patch: modify {display.Count} file(s): {string.Join(", ", display)}",
            TargetPaths = targets,
        };
    }

    /// <summary>
    /// 分两阶段执行准备计划：先读取并在内存中应用全部文件，再在全部成功后顺序提交写入。
    /// Executes the prepared plan in two phases: apply every file in memory, then commit writes only after all succeed.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var plan = ReadPlan(preparation);

        try
        {
            // Phase 1: read every target and apply its hunks in memory. Any
            // mismatch throws before a single byte is written.
            var applied    = new List<(string Path, string Text, bool IsNew)>(plan.Count);
            var totalBytes = 0;
            foreach (var file in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var absolute = file.Path;
                if (Directory.Exists(absolute))
                {
                    return new ToolResult
                    {
                        Succeeded = false,
                        Content   = $"{workspace.ToDisplay(absolute)} is a directory; apply_patch cannot modify it.",
                    };
                }

                var exists = File.Exists(absolute);
                if (exists && file.IsNewFile)
                {
                    return new ToolResult
                    {
                        Succeeded = false,
                        Content =
                            $"File already exists: {workspace.ToDisplay(absolute)}; a new-file patch cannot modify it.",
                    };
                }

                if (!exists && !file.IsNewFile)
                {
                    return new ToolResult
                    {
                        Succeeded = false,
                        Content   = $"File not found: {workspace.ToDisplay(absolute)}",
                    };
                }

                // Re-check the final target at execution time. The leaf itself
                // only exists for a modification; a new file is verified
                // through its nearest existing ancestor directory, because link
                // resolution throws for a path that does not exist yet.
                if (exists)
                {
                    workspace.EnsureFinalTargetInside(absolute, isDirectory : false, "File");
                }
                else
                {
                    EnsureNewFileInside(workspace, absolute);
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
                if (file.IsNewFile && text.Length > 0 && !text.EndsWith('\n'))
                {
                    text += '\n';
                }

                var outputBytes = Encoding.UTF8.GetByteCount(text);
                if (outputBytes > MaxFileBytes)
                {
                    throw new
                        InvalidDataException($"Output for '{workspace.ToDisplay(absolute)}' exceeds the 4 MiB file limit.");
                }

                totalBytes += outputBytes;
                if (totalBytes > MaxTotalBytes)
                {
                    throw new InvalidDataException("Patch exceeds the 16 MiB combined input/output budget.");
                }

                applied.Add((absolute, text, file.IsNewFile));
            }

            // Phase 2: every target matched, so commit the writes.
            foreach (var (path, text, isNew) in applied)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isNew)
                {
                    EnsureNewFileInside(workspace, path);
                    var parent = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(parent))
                    {
                        Directory.CreateDirectory(parent);
                    }
                }
                else
                {
                    workspace.EnsureFinalTargetInside(path, isDirectory : false, "File");
                }

                // CreateNew also prevents overwriting a file created after the
                // validation phase (including a competing process's file).
                await using var stream = new FileStream(path, isNew ? FileMode.CreateNew : FileMode.Create,
                                                        FileAccess.Write, FileShare.None, 64 * 1024,
                                                        FileOptions.Asynchronous);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var changed = plan.Select(f => workspace.ToDisplay(f.Path)).ToList();
            return new ToolResult
            {
                Succeeded = true,
                Content   = $"Applied patch to {changed.Count} file(s): {string.Join(", ", changed)}",
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new ToolResult { Succeeded = false, Content = $"apply_patch failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// 在单文件及调用总预算内异步读取原文件，并额外探测一个字节以发现读取期间增长。
    /// Reads the original file within per-file/aggregate budgets and probes one extra byte to detect concurrent growth.
    /// </summary>
    private static async Task<(string Text, int Bytes)> ReadOriginalAsync(string            path, int byteLimit,
                                                                          CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > byteLimit)
        {
            throw new
                InvalidDataException($"Input '{path}' exceeds the {byteLimit} byte read limit (4 MiB per file, 16 MiB combined input/output budget).");
        }

        using var bytes  = new MemoryStream();
        var       buffer = new byte[64 * 1024];
        while (true)
        {
            // Probe one byte beyond the remaining budget, so growth cannot
            // turn a length check into an unbounded read or silent truncation.
            var count = Math.Min(buffer.Length, byteLimit - (int)bytes.Length + 1);
            var read  = await stream.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (bytes.Length + read > byteLimit)
            {
                throw new InvalidDataException($"Input '{path}' grew beyond the {byteLimit} byte read limit.");
            }

            bytes.Write(buffer, 0, read);
        }

        bytes.Position = 0;
        using var reader = new StreamReader(bytes, Encoding.UTF8, detectEncodingFromByteOrderMarks : true);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false), (int)bytes.Length);
    }

    /// <summary>
    /// 从准备参数反序列化内部权威计划；字段缺失或结构损坏会拒绝执行。
    /// Deserializes the authoritative internal plan and rejects missing or malformed fields.
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
    /// 从准备好的 hunk 对象复制字符串行数组。
    /// Copies a string-line array from a prepared hunk object.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(JsonObject hunk, string key)
    {
        var array = hunk[key] as JsonArray ?? [];
        var lines = new List<string>(array.Count);
        foreach (var item in array)
        {
            lines.Add(item?.GetValue<string>() ?? string.Empty);
        }

        return lines;
    }

    /// <summary>
    /// 对尚不存在的新文件检查最近的已有祖先目录，防止经符号链接或 junction 逃逸工作区。
    ///
    /// Verifies that a not-yet-existing file cannot escape the workspace through a
    /// symlinked ancestor directory. The lexical path was already confined by
    /// Prepare; this re-checks the nearest existing ancestor's link chain, since
    /// the leaf itself cannot be a link before it is created.
    /// </summary>
    private static void EnsureNewFileInside(Workspace workspace, string absolute)
    {
        var ancestor = Path.GetDirectoryName(absolute);
        while (ancestor is not null && !Directory.Exists(ancestor))
        {
            ancestor = Path.GetDirectoryName(ancestor);
        }

        if (ancestor is not null)
        {
            workspace.EnsureFinalTargetInside(ancestor, isDirectory : true, "Directory");
        }
    }

    /// <summary>
    /// 把文本行复制为独立 JSON 数组，供不可变准备计划保存。
    /// Copies text lines into an independent JSON array for the immutable prepared plan.
    /// </summary>
    private static JsonArray ToArray(IEnumerable<string> lines)
    {
        var array = new JsonArray();
        foreach (var line in lines)
        {
            array.Add((JsonNode?)JsonValue.Create(line));
        }

        return array;
    }

    private sealed record ResolvedPatchFile(string Path, bool IsNewFile, IReadOnlyList<PatchHunk> Hunks);
}
