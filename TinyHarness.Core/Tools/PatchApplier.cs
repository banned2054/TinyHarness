namespace TinyHarness.Core.Tools;

/// <summary>
/// 在内存中把已解析 hunk 应用到文件文本。所有上下文先验证，任何不匹配都会在写入前失败；
/// hunk 必须按旧行号排序且不能重叠，并保留原文件的 LF/CRLF 风格。
///
/// Applies parsed hunks to a file's text. All hunks are matched before any line
/// is emitted as "new" content, so a mismatch throws and the caller performs no
/// write. Hunks must be non-overlapping and ordered by their old line number.
/// The file's line-ending style (LF vs CRLF) is preserved; a new file defaults
/// to LF.
/// </summary>
internal static class PatchApplier
{
    /// <summary>
    /// 验证并应用有序 hunk，返回完整新文本；不执行任何文件系统写入。
    /// Validates and applies ordered hunks, returning complete new text without touching the file system.
    /// </summary>
    public static string Apply(string originalText, IReadOnlyList<PatchHunk> hunks)
    {
        var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (lines, endsWithNewline) = SplitLines(originalText);
        var result = new List<string>();
        var cursor = 0; // Index of the next original line not yet consumed.

        foreach (var hunk in hunks)
        {
            // An empty old range names the line AFTER which insertion occurs.
            var before = hunk.OldCount == 0 ? hunk.OldStart : hunk.OldStart - 1;
            if (before < cursor)
            {
                throw new
                    InvalidDataException($"apply_patch: hunks overlap or are out of order near line {before + 1}.");
            }

            if (before > lines.Count)
            {
                throw new InvalidDataException(
                    $"apply_patch: hunk at line {hunk.OldStart} does not match (file has only {lines.Count} line(s)).");
            }

            while (cursor < before)
            {
                result.Add(lines[cursor]);
                cursor++;
            }

            if (cursor + hunk.OldLines.Count > lines.Count)
            {
                throw new
                    InvalidDataException($"apply_patch: hunk at line {hunk.OldStart} does not match (file has only {lines.Count} line(s)).");
            }

            for (var k = 0; k < hunk.OldLines.Count; k++)
            {
                if (!string.Equals(lines[cursor + k], hunk.OldLines[k], StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"apply_patch: context mismatch at line {hunk.OldStart + k}.");
                }
            }

            cursor += hunk.OldLines.Count;
            result.AddRange(hunk.NewLines);
        }

        while (cursor < lines.Count)
        {
            result.Add(lines[cursor]);
            cursor++;
        }

        return string.Join(newline, result) + (result.Count > 0 && endsWithNewline ? newline : string.Empty);
    }

    /// <summary>
    /// 拆分原始文本并单独保留“是否以换行结尾”，供重建时精确保留格式。
    /// Splits source text while preserving the trailing-newline fact for exact reconstruction.
    /// </summary>
    private static (List<string> Lines, bool EndsWithNewline) SplitLines(string text)
    {
        var lines = new List<string>();
        if (text.Length == 0)
        {
            return (lines, false);
        }

        var endsWithNewline = text.EndsWith('\n');
        var start           = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
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

        if (start < text.Length)
        {
            var last = text[start..];
            if (last.EndsWith('\r'))
            {
                last = last[..^1];
            }

            lines.Add(last);
        }

        return (lines, endsWithNewline);
    }
}
