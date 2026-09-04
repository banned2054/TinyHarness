using System.Text;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Tools;

/// <summary>
/// read_file: reads a text file inside the workspace, with optional 1-based line
/// offset/limit paging. Line-oriented so partial reads are always line-aligned.
/// Output is bounded: files larger than 4 MiB are refused with their size, a
/// call returns at most 4000 lines and 64 KiB of text, and single lines longer
/// than 8 KiB are cut in place with an explicit "[line N truncated]" suffix.
/// Every early stop reports the exact offset to continue from, and all markers
/// count against the output budget. Binary files are reported rather than
/// dumped.
/// </summary>
public sealed class ReadFileTool(Workspace workspace) : ITool
{
    /// <summary>
    /// Hard per-call line ceiling, also the default when the caller omits limit.
    /// Prepare rejects larger values; Execute never clamps silently.
    /// </summary>
    private const int AbsoluteMaxLines = 4_000;

    /// <summary>Files strictly larger than this are refused with their size.</summary>
    private const long MaxFileBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Total output budget in UTF-16 code units, truncation markers included.
    /// One code unit is one char, so CJK-heavy content can still serialize to
    /// ~3x this many bytes; the JSON byte bound is a transport concern.
    /// </summary>
    private const int MaxOutputChars = 64 * 1024;

    /// <summary>
    /// Content kept from a line longer than this; the rest is discarded and the
    /// line is marked "... [line N truncated]" in place. The line still counts
    /// as displayed, so the next page starts after it, never by re-reading it.
    /// </summary>
    private const int MaxLineChars = 8 * 1024;

    /// <summary>
    /// Headroom kept from the line budget so every marker (and the concurrency
    /// note) still fits inside <see cref="MaxOutputChars"/>.
    /// </summary>
    private const int TrailerReserveChars = 80;

    /// <summary>Bytes probed for binary detection.</summary>
    private const int BinaryProbeBytes = 512;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "File to read, relative to the workspace root.",
            },
            ["offset"] = new JsonObject
            {
                ["type"]        = "integer",
                ["minimum"]     = 1,
                ["description"] = "1-based first line to read (default 1).",
            },
            ["limit"] = new JsonObject
            {
                ["type"]    = "integer",
                ["minimum"] = 1,
                ["maximum"] = AbsoluteMaxLines,
                ["description"] =
                    "Maximum lines to return (1-4000; default 4000). Output is also capped at 64 KiB total " +
                    "and 8 KiB per line; files over 4 MiB are refused.",
            },
        },
        ["required"] = new JsonArray("path"),
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "read_file",
        Description =
            "Reads a text file from the workspace. Page large files with offset/limit. " +
            "Files over 4 MiB are refused; output is capped at 4000 lines / 64 KiB.",
        Parameters = Schema,
    };

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args    = ToolArgs.ParseObject(call);
        var rawPath = JsonArgs.Required(args, "path");
        var offset  = JsonArgs.OptionalInt(args, "offset") ?? 1;
        var limit   = JsonArgs.OptionalInt(args, "limit");
        if (offset < 1)
        {
            throw new InvalidDataException("Tool argument 'offset' must be a positive integer.");
        }

        if (limit is < 1 or > AbsoluteMaxLines)
        {
            throw new InvalidDataException($"Tool argument 'limit' must be between 1 and {AbsoluteMaxLines}.");
        }

        var absolute = workspace.ResolveInside(rawPath, "path");
        args["path"] = absolute; // Normalized plan value; Execute never re-resolves raw input.
        return new ToolPreparation
        {
            ToolName   = Definition.Name,
            CallId     = call.Id,
            Arguments  = args,
            Capability = "filesystem.read",
            Summary = $"read_file {workspace.ToDisplay(absolute)}"           +
                      (offset > 1 ? $" (from line {offset})" : string.Empty) +
                      (limit is not null ? $" (up to {limit} lines)" : string.Empty),
            TargetPaths = [absolute],
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var absolute = ToolArgs.ReadAbsolute(preparation, "path");
        var offset   = JsonArgs.OptionalInt(preparation.Arguments, "offset") ?? 1;
        var limit    = JsonArgs.OptionalInt(preparation.Arguments, "limit")  ?? AbsoluteMaxLines;

        if (Directory.Exists(absolute))
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"{workspace.ToDisplay(absolute)} is a directory; use list_files instead.",
            };
        }

        if (!File.Exists(absolute))
        {
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"File not found: {workspace.ToDisplay(absolute)}",
            };
        }

        workspace.EnsureFinalTargetInside(absolute, isDirectory : false, "File");

        var display    = workspace.ToDisplay(absolute);
        var fileLength = new FileInfo(absolute).Length;
        if (fileLength > MaxFileBytes)
        {
            // Refuse rather than partially read: offset paging is line-based, so
            // a truncated byte window could never be paged past, and a file this
            // large is better served by search_text anyway.
            return new ToolResult
            {
                Succeeded = true,
                Content =
                    $"{display} is {fileLength} bytes, larger than the {MaxFileBytes / (1024 * 1024)} MiB read limit; " +
                    "it was not read. Use search_text to locate specific content instead.",
            };
        }

        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                                FileOptions.Asynchronous);
        var binary = await LooksBinaryAsync(stream, cancellationToken).ConfigureAwait(false);
        if (binary)
        {
            return new ToolResult
            {
                Succeeded = true,
                Content   = $"{display} appears to be a binary file ({fileLength} bytes); not shown.",
            };
        }

        stream.Position = 0;
        // The size check above already refused files over MaxFileBytes; this
        // bounded read only trips if the file grew in between (FileShare allows
        // concurrent writes). It keeps a single ReadLineAsync from ever loading
        // more than MaxFileBytes worth of one line into memory.
        using var limited = new LimitedReadStream(stream, MaxFileBytes, leaveOpen : true);
        using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks : true,
                                            bufferSize : 64 * 1024, leaveOpen : false);

        var     output     = new StringBuilder();
        var     lineNumber = 0;
        var     rendered   = 0;
        var     lineBudget = MaxOutputChars - TrailerReserveChars;
        var     stopped    = false;
        string? line;

        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            if (lineNumber < offset)
            {
                continue;
            }

            if (rendered >= limit)
            {
                // A real next line was just read, so the file continues past the
                // line cap; report it with the exact continuation offset. Without
                // this probe read, a file ending exactly one line past the cap
                // would lose that last line silently.
                AppendContinueMarker(output, lineNumber);
                stopped = true;
                break;
            }

            var shown = line.Length <= MaxLineChars ? line : TruncateLine(line, lineNumber, MaxLineChars);
            if (output.Length + shown.Length + 1 > lineBudget)
            {
                // The next whole line (already read to learn its length) does not
                // fit: it is not shown at all, so the next page must start at it.
                AppendContinueMarker(output, lineNumber);
                stopped = true;
                break;
            }

            output.Append(shown).Append('\n');
            rendered++;
        }

        if (!stopped && limited.HitLimit)
        {
            // Only reachable through concurrent growth after the size check:
            // reads ended at the 4 MiB ceiling instead of at a natural EOF.
            output.Append("\n(... read stopped at the 4 MiB safety limit: the file grew while being read)");
        }

        if (output.Length == 0)
        {
            return new ToolResult
            {
                Succeeded = true,
                Content   = $"(no lines in {display} from line {offset} on)",
            };
        }

        return new ToolResult { Succeeded = true, Content = output.ToString().TrimEnd('\n') };
    }

    private static void AppendContinueMarker(StringBuilder output, int nextLine)
    {
        output.Append("... truncated before line ").Append(nextLine)
              .Append("; continue with offset=").Append(nextLine);
    }

    private static string TruncateLine(string line, int lineNumber, int maxChars)
    {
        // Keep the first maxChars code units without splitting a UTF-16 surrogate
        // pair; the "... [line N truncated]" suffix is an annotation, not file
        // content, so no line-number prefix is injected into the content itself.
        var cut = Math.Min(line.Length, maxChars);
        if (cut < line.Length && char.IsHighSurrogate(line[cut - 1]))
        {
            cut++; // The pair's low surrogate sits at index cut; keep both.
        }

        if (cut == line.Length)
        {
            return line; // The pair straddled the boundary: the whole line fits after all.
        }

        return $"{line[..cut]}... [line {lineNumber} truncated]";
    }

    private static async Task<bool> LooksBinaryAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var probe = new byte[BinaryProbeBytes];
        var read = await stream.ReadAsync(probe.AsMemory(0, BinaryProbeBytes), cancellationToken)
                               .ConfigureAwait(false);
        for (var i = 0; i < read; i++)
        {
            if (probe[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Read-only ceiling over an inner stream. Reads stop after
    /// <paramref name="maxBytes"/> bytes have been consumed, and
    /// <see cref="HitLimit"/> is set only when that ceiling (not a natural end
    /// of the inner stream) ended the reads.
    /// </summary>
    private sealed class LimitedReadStream(Stream inner, long maxBytes, bool leaveOpen) : Stream
    {
        private long _consumed;

        /// <summary>True when reads ended at the byte ceiling instead of a natural EOF.</summary>
        public bool HitLimit { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = maxBytes - _consumed;
            if (remaining <= 0)
            {
                return ProbePastEnd();
            }

            var read = inner.Read(buffer, offset, (int)Math.Min(remaining, count));
            _consumed += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var remaining = maxBytes - _consumed;
            if (remaining <= 0)
            {
                return ProbePastEnd();
            }

            var read = inner.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            _consumed += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte>      buffer,
                                                       CancellationToken cancellationToken = default)
        {
            var remaining = maxBytes - _consumed;
            if (remaining <= 0)
            {
                return await ProbePastEndAsync(cancellationToken).ConfigureAwait(false);
            }

            var read = await inner.ReadAsync(buffer[..(int)Math.Min(remaining, buffer.Length)], cancellationToken)
                                  .ConfigureAwait(false);
            _consumed += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[]            buffer, int offset, int count,
                                            CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>Returns 0. Sets <see cref="HitLimit"/> only when data exists past the ceiling.</summary>
        private int ProbePastEnd()
        {
            if (HitLimit)
            {
                return 0;
            }

            var probe = new byte[1];
            if (inner.Read(probe, 0, 1) == 0)
            {
                return 0; // Natural EOF: the file ended exactly at the ceiling.
            }

            HitLimit = true; // Data exists past the ceiling: the file grew mid-read.
            return 0;
        }

        private async ValueTask<int> ProbePastEndAsync(CancellationToken cancellationToken)
        {
            if (HitLimit)
            {
                return 0;
            }

            var probe = new byte[1];
            var read  = await inner.ReadAsync(probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return 0; // Natural EOF.
            }

            HitLimit = true;
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
