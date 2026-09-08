namespace TinyHarness.Core.Tools;

/// <summary>
/// 统一差异中的单个变更块。起始行从 1 计数，0 表示首行之前；OldLines 是必须匹配的
/// 上下文与删除行，NewLines 是替换后的上下文与新增行。
///
/// A single change hunk from a unified diff. OldStart/NewStart are 1-based line
/// numbers; a 0 start means "before the first line" (used for new files and
/// leading insertions). OldLines is the ordered list of context + removed lines
/// that must match the target; NewLines is the ordered list of context + added
/// lines that replace them.
/// </summary>
internal sealed record PatchHunk(
    int                   OldStart,
    int                   OldCount,
    int                   NewStart,
    int                   NewCount,
    IReadOnlyList<string> OldLines,
    IReadOnlyList<string> NewLines);

/// <summary>
/// 补丁中的单个文件区段。路径已经去除 Git 前缀和时间戳；IsNewFile 表示由
/// “--- /dev/null”声明的新文件。
///
/// One file section of a patch. Path is the workspace-relative path (the "b/"
/// prefix and any trailing timestamp already stripped). IsNewFile marks a
/// "--- /dev/null" section, which creates the file.
/// </summary>
internal sealed record PatchFile(string Path, bool IsNewFile, IReadOnlyList<PatchHunk> Hunks);

/// <summary>
/// 无反射的最小 unified diff 解析器。支持多文件、可选时间戳、新建文件和标准 hunk；
/// 明确拒绝删文件及“无末尾换行”标记，使应用结果保持确定。
///
/// Reflection-free parser for the minimal unified-diff subset apply_patch accepts.
/// Supports multiple file sections, "--- a/path" / "+++ b/path" headers (with an
/// optional tab-separated timestamp), new-file sections ("--- /dev/null"), and
/// "@@ -s[,c] +s[,c] @@" hunks whose counts are validated against their bodies.
/// File deletion ("+++ /dev/null") and the "\\ No newline at end of file" marker
/// are rejected as unsupported, so the applied result is always well-defined.
/// </summary>
internal static class PatchParser
{
    /// <summary>
    /// 解析并完整校验补丁的文件区段、hunk 头与行数，返回可供准备阶段使用的结构化表示。
    /// Parses and fully validates file sections, hunk headers, and line counts into a structured patch plan.
    /// </summary>
    public static IReadOnlyList<PatchFile> Parse(string patch)
    {
        var lines = SplitLines(patch);
        var files = new List<PatchFile>();
        var i     = 0;

        while (i < lines.Count)
        {
            var headerLine = lines[i];
            if (!headerLine.StartsWith("--- ", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"apply_patch: expected a '--- ' file header at line {i + 1}.");
            }

            var oldPath = ParseHeaderPath(headerLine, "--- ");
            i++;
            if (i >= lines.Count || !lines[i].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"apply_patch: expected a '+++ ' header after '{oldPath}'.");
            }

            var newPath = ParseHeaderPath(lines[i], "+++ ");
            i++;

            var target  = DetermineTargetPath(oldPath, newPath);
            var hunks   = new List<PatchHunk>();
            var sawHunk = false;

            while (i < lines.Count && !lines[i].StartsWith("--- ", StringComparison.Ordinal))
            {
                var line = lines[i];
                if (!line.StartsWith("@@", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                                                   $"apply_patch: expected a hunk header '@@' for '{target}' at line {i + 1}.");
                }

                var (oldStart, oldCount, newStart, newCount) = ParseHunkHeader(line);
                i++;
                sawHunk = true;

                var oldLines = new List<string>();
                var newLines = new List<string>();

                // Counts delimit the body: a removal such as "--- comment"
                // is still content until this hunk has consumed its ranges.
                while (i < lines.Count && (oldLines.Count < oldCount || newLines.Count < newCount))
                {
                    var body = lines[i];
                    if (body == "\\ No newline at end of file")
                    {
                        throw new InvalidDataException("apply_patch: '\\ No newline at end of file' is not supported.");
                    }

                    if (body.StartsWith('+'))
                    {
                        newLines.Add(body[1..]);
                    }
                    else if (body.StartsWith('-'))
                    {
                        oldLines.Add(body[1..]);
                    }
                    else if (body.StartsWith(' ') || body.Length == 0)
                    {
                        // Context line (a space prefix) or a bare empty line: both
                        // must match the target and pass through unchanged.
                        var content = body.StartsWith(' ') ? body[1..] : string.Empty;
                        oldLines.Add(content);
                        newLines.Add(content);
                    }
                    else
                    {
                        throw new
                            InvalidDataException($"apply_patch: unexpected hunk body line for '{target}' at line {i + 1}.");
                    }

                    i++;

                    if (oldLines.Count > oldCount || newLines.Count > newCount)
                    {
                        throw new
                            InvalidDataException($"apply_patch: hunk for '{target}' exceeds its declared line counts.");
                    }
                }

                if (i < lines.Count && lines[i] == "\\ No newline at end of file")
                {
                    throw new InvalidDataException("apply_patch: '\\ No newline at end of file' is not supported.");
                }

                if (oldLines.Count != oldCount)
                {
                    throw new
                        InvalidDataException($"apply_patch: hunk for '{target}' declares {oldCount} old line(s) but has {oldLines.Count}.");
                }

                if (newLines.Count != newCount)
                {
                    throw new
                        InvalidDataException($"apply_patch: hunk for '{target}' declares {newCount} new line(s) but has {newLines.Count}.");
                }

                hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, oldLines, newLines));
            }

            if (!sawHunk || hunks.Count == 0)
            {
                throw new InvalidDataException($"apply_patch: file section for '{target}' has no hunks.");
            }

            files.Add(new PatchFile(target, oldPath == "/dev/null", hunks));
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException("apply_patch: the patch contains no files.");
        }

        return files;
    }

    /// <summary>
    /// 从文件头取出路径，并移除制表符后的可选时间戳。
    /// Extracts a file-header path and removes an optional tab-separated timestamp.
    /// </summary>
    private static string ParseHeaderPath(string line, string prefix)
    {
        var rest = line[prefix.Length..];
        var tab  = rest.IndexOf('\t');
        return tab >= 0 ? rest[..tab] : rest;
    }

    /// <summary>
    /// 根据新旧文件头确定写入目标，同时拒绝删除和无效的新文件路径。
    /// Determines the write target from old/new headers while rejecting deletion and invalid creation paths.
    /// </summary>
    private static string DetermineTargetPath(string oldPath, string newPath)
    {
        if (newPath == "/dev/null")
        {
            throw new InvalidDataException("apply_patch: file deletion ('+++ /dev/null') is not supported.");
        }

        if (oldPath == "/dev/null")
        {
            var created = StripGitPrefix(newPath);
            if (string.IsNullOrWhiteSpace(created) || created == "/dev/null")
            {
                throw new InvalidDataException("apply_patch: invalid path for a new file.");
            }

            return created;
        }

        var target = StripGitPrefix(newPath);
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidDataException("apply_patch: invalid target path.");
        }

        return target;
    }

    /// <summary>
    /// 移除 unified diff 常见的“a/”或“b/”路径前缀。
    /// Removes the conventional "a/" or "b/" unified-diff path prefix.
    /// </summary>
    private static string StripGitPrefix(string path)
        => path.StartsWith("b/", StringComparison.Ordinal) || path.StartsWith("a/", StringComparison.Ordinal)
            ? path[2..]
            : path;

    /// <summary>
    /// 解析“@@ -old +new @@”头部，并验证起始行及计数均非负。
    /// Parses an "@@ -old +new @@" header and validates non-negative starts and counts.
    /// </summary>
    private static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkHeader(string line)
    {
        // Form: "@@ -oldStart[,oldCount] +newStart[,newCount] @@ optional heading".
        const string prefix = "@@ -";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"apply_patch: malformed hunk header '{line}'.");
        }

        var rest   = line[prefix.Length..];
        var oldEnd = rest.IndexOf(' ');
        if (oldEnd <= 0)
        {
            throw new InvalidDataException($"apply_patch: malformed hunk header '{line}'.");
        }

        var oldPart = rest[..oldEnd];
        rest = rest[(oldEnd + 1)..];
        if (!rest.StartsWith("+", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"apply_patch: malformed hunk header '{line}'.");
        }

        var newPartEnd = rest.IndexOf(' ');
        var newPart    = newPartEnd < 0 ? rest : rest[..newPartEnd];
        if (newPart.Length < 1)
        {
            throw new InvalidDataException($"apply_patch: malformed hunk header '{line}'.");
        }

        var (oldStart, oldCount) = ParseRange(oldPart, line);
        var (newStart, newCount) = ParseRange(newPart, line);
        if (oldStart < 0 || newStart < 0 || oldCount < 0 || newCount < 0)
        {
            throw new InvalidDataException($"apply_patch: negative range in hunk header '{line}'.");
        }

        return (oldStart, oldCount, newStart, newCount);
    }

    /// <summary>
    /// 解析 hunk 中的“start[,count]”范围，省略 count 时默认为 1。
    /// Parses a hunk "start[,count]" range, defaulting an omitted count to one.
    /// </summary>
    private static (int Start, int Count) ParseRange(string part, string line)
    {
        var comma = part.IndexOf(',');
        if (comma < 0)
        {
            return int.TryParse(part, out var start) ? (start, 1) : ThrowRange(line);
        }

        return int.TryParse(part[..comma], out var s) && int.TryParse(part[(comma + 1)..], out var c)
            ? (s, c)
            : ThrowRange(line);
    }

    /// <summary>
    /// 以统一消息抛出 hunk 范围格式错误。
    /// Throws the consistent diagnostic for a malformed hunk range.
    /// </summary>
    private static (int, int) ThrowRange(string line)
        => throw new InvalidDataException($"apply_patch: malformed range in hunk header '{line}'.");

    /// <summary>
    /// 按 LF/CRLF 拆分补丁文本，去除分隔符，并避免末尾换行产生幽灵空行。
    /// Splits LF/CRLF patch text without delimiters and avoids a phantom element from the trailing newline.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
            {
                continue;
            }

            var line = text[start..i];
            if (line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            lines.Add(line);
            start = i + 1;
        }

        // A trailing '\n' produced one empty final element; drop it so a patch
        // that ends with a newline does not gain a phantom empty line.
        if (text.EndsWith('\n') && lines.Count > 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
