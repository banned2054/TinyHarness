using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Tools;

/// <summary>
/// search_text: substring search over workspace text files. Matches are reported
/// as "path:line: text". Binary files, oversized files and file links whose
/// final target escapes the workspace are skipped and counted. The walk is
/// bounded by the DirectoryWalker exclusion list and by
/// <see cref="MaxFilesScanned"/> (a truncated walk is reported), and the result
/// list stops early once <see cref="MaxResults"/> is reached.
/// </summary>
public sealed class SearchTextTool(Workspace workspace) : ITool
{
    private const int DefaultMaxResults = 100;

    private const int AbsoluteMaxResults = 500;

    private const int MaxFilesScanned = 3_000;

    private const long MaxFileBytes = 4 * 1024 * 1024;

    private const int BinaryProbeBytes = 512;

    private const int MaxLineDisplayChars = 240;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "Text to search for.",
            },
            ["path"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "File or directory to search, relative to the workspace root (default \".\").",
            },
            ["caseSensitive"] = new JsonObject
            {
                ["type"]        = "boolean",
                ["description"] = "Case-sensitive match (default false).",
            },
            ["maxResults"] = new JsonObject
            {
                ["type"]        = "integer",
                ["description"] = "Maximum matches to report (default 100, max 500).",
            },
        },
        ["required"] = new JsonArray("pattern"),
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name        = "search_text",
        Description = "Searches for a substring in workspace text files. Reports 'path:line: text' matches.",
        Parameters  = Schema,
    };

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args       = ToolArgs.ParseObject(call);
        var pattern    = JsonArgs.Required(args, "pattern");
        var rawPath    = JsonArgs.Optional(args, "path", ".");
        var maxResults = JsonArgs.OptionalInt(args, "maxResults") ?? DefaultMaxResults;
        if (maxResults is < 1 or > AbsoluteMaxResults)
        {
            throw new InvalidDataException($"Tool argument 'maxResults' must be between 1 and {AbsoluteMaxResults}.");
        }

        var absolute = workspace.ResolveInside(rawPath, "path");
        args["path"] = absolute; // Normalized plan value; Execute never re-resolves raw input.
        return new ToolPreparation
        {
            ToolName    = Definition.Name,
            CallId      = call.Id,
            Arguments   = args,
            Capability  = "filesystem.search",
            Summary     = $"search_text \"{pattern}\" in {workspace.ToDisplay(absolute)}",
            TargetPaths = [absolute],
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var absolute      = ToolArgs.ReadAbsolute(preparation, "path");
        var pattern       = JsonArgs.Required(preparation.Arguments, "pattern");
        var caseSensitive = JsonArgs.OptionalBool(preparation.Arguments, "caseSensitive", false);
        var maxResults    = JsonArgs.OptionalInt(preparation.Arguments, "maxResults") ?? DefaultMaxResults;
        var comparison    = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (!File.Exists(absolute) && !Directory.Exists(absolute))
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"Path not found: {workspace.ToDisplay(absolute)}",
            };
        }

        workspace.EnsureFinalTargetInside(absolute, isDirectory : Directory.Exists(absolute), "Path");

        var files       = new List<string>();
        var walkLimited = false;
        if (File.Exists(absolute))
        {
            files.Add(absolute);
        }
        else
        {
            var walk = DirectoryWalker.CollectFiles(absolute, MaxFilesScanned, cancellationToken);
            files.AddRange(walk.Entries);
            walkLimited = walk.Truncated;
        }

        var output          = new List<string>(maxResults);
        var filesScanned    = 0;
        var skippedLarge    = 0;
        var skippedBinary   = 0;
        var skippedEscaping = 0;
        var stoppedAtCap    = false;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Count >= maxResults)
            {
                stoppedAtCap = true;
                break;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes)
            {
                skippedLarge++;
                continue;
            }

            var scan = await ScanFileAsync(file, pattern, comparison, maxResults - output.Count, workspace,
                                           cancellationToken).ConfigureAwait(false);
            if (scan.SkipReason is ScanSkipReason.Binary)
            {
                skippedBinary++;
                continue;
            }

            if (scan.SkipReason is ScanSkipReason.OutsideWorkspace)
            {
                skippedEscaping++;
                continue;
            }

            filesScanned++;
            output.AddRange(scan.Matches);
            if (scan.StoppedMidFile)
            {
                stoppedAtCap = true;
                break;
            }
        }

        var details = new List<string>(4);
        if (skippedEscaping > 0)
        {
            details.Add($"skipped {skippedEscaping} file link(s) to outside the workspace");
        }

        if (skippedBinary > 0)
        {
            details.Add($"skipped {skippedBinary} binary file(s)");
        }

        if (skippedLarge > 0)
        {
            details.Add($"skipped {skippedLarge} large file(s)");
        }

        if (walkLimited)
        {
            details.Add($"only the first {MaxFilesScanned} files scanned");
        }

        var note = details.Count == 0 ? string.Empty : "; " + string.Join("; ", details);

        var content = new StringBuilder();
        if (output.Count == 0)
        {
            content.Append($"(no matches for '{pattern}'{note})");
        }
        else
        {
            foreach (var line in output)
            {
                content.Append(line).Append('\n');
            }

            var matchWord = output.Count == 1 ? "match" : "matches";
            if (stoppedAtCap)
            {
                content.Append($"... (stopped after {output.Count} {matchWord}");
            }
            else
            {
                content.Append($"({output.Count} {matchWord} in {filesScanned} " +
                               (filesScanned == 1 ? "file" : "files"));
            }

            content.Append(note).Append(')');
        }

        return new ToolResult { Succeeded = true, Content = content.ToString().TrimEnd('\n') };
    }

    /// <summary>
    /// Scans one file line by line. Returns a skip outcome when the file looks
    /// binary or when it is a link whose final target escapes the workspace;
    /// otherwise the matched "path:line: text" lines and whether the scan had
    /// to stop mid-file because the match cap was hit.
    /// </summary>
    private static async Task<ScanOutcome> ScanFileAsync(string file, string pattern, StringComparison comparison,
                                                         int maxMatches, Workspace workspace,
                                                         CancellationToken cancellationToken)
    {
        // A file found by the walk can itself be a symlink/junction whose final
        // target escapes the workspace even though the searched root does not
        // (PLAN §11: re-check the resolved boundary before touching each file).
        // Explicit single-file paths were already checked in ExecuteAsync, so
        // this only fires for walk-discovered links: skip them, never read.
        try
        {
            workspace.EnsureFinalTargetInside(file, isDirectory : false, "File");
        }
        catch (InvalidDataException)
        {
            return ScanOutcome.OutsideWorkspace;
        }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                                FileOptions.Asynchronous);
        var probe = new byte[BinaryProbeBytes];
        var read = await stream.ReadAsync(probe.AsMemory(0, BinaryProbeBytes), cancellationToken)
                               .ConfigureAwait(false);
        for (var i = 0; i < read; i++)
        {
            if (probe[i] == 0)
            {
                return ScanOutcome.Binary;
            }
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks : true,
                                            bufferSize : 64 * 1024, leaveOpen : false);

        var     display    = workspace.ToDisplay(file);
        var     matches    = new List<string>(maxMatches);
        var     lineNumber = 0;
        string? line;
        while (matches.Count < maxMatches &&
               (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            if (line.IndexOf(pattern, comparison) < 0)
            {
                continue;
            }

            var shown = line.Length <= MaxLineDisplayChars
                ? line
                : line[..MaxLineDisplayChars] + "...";
            matches.Add($"{display}:{lineNumber}: {shown}");
        }

        // Distinguish "the file had exactly maxMatches and ended" from "we cut it
        // off mid-file" with one extra probe read.
        var stoppedMidFile = false;
        if (matches.Count >= maxMatches)
        {
            var more = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            stoppedMidFile = more is not null;
        }

        return new ScanOutcome(matches, stoppedMidFile, null);
    }

    private sealed record ScanOutcome(List<string> Matches, bool StoppedMidFile, ScanSkipReason? SkipReason)
    {
        public static ScanOutcome Binary { get; } = new([], false, ScanSkipReason.Binary);

        public static ScanOutcome OutsideWorkspace { get; } = new([], false, ScanSkipReason.OutsideWorkspace);
    }

    private enum ScanSkipReason
    {
        /// <summary>File content looks binary; not scanned further.</summary>
        Binary,

        /// <summary>File link whose final target escapes the workspace.</summary>
        OutsideWorkspace,
    }
}
