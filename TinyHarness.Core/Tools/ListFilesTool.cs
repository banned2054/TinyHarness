using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;

namespace TinyHarness.Core.Tools;

/// <summary>
/// list_files: lists the entries of a workspace directory. Directory entries are
/// suffixed with '/'; recursive walks omit and never descend into well-known
/// build/version-control directories (see <see cref="DirectoryWalker"/>).
/// </summary>
public sealed class ListFilesTool(Workspace workspace) : ITool
{
    private const int MaxEntries = 500;

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "Directory to inspect, relative to the workspace root (default \".\").",
            },
            ["recursive"] = new JsonObject
            {
                ["type"]        = "boolean",
                ["description"] = "Recurse into subdirectories (default false).",
            },
            ["maxDepth"] = new JsonObject
            {
                ["type"]        = "integer",
                ["description"] = "Maximum recursion depth when recursive (default: unlimited).",
            },
        },
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "list_files",
        Description = "Lists files and directories inside the workspace. Returns one entry per line; " +
                      "directories end with '/'. Paths are relative to the workspace root.",
        Parameters = Schema,
    };

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args      = ToolArgs.ParseObject(call);
        var rawPath   = JsonArgs.Optional(args, "path", ".");
        var recursive = JsonArgs.OptionalBool(args, "recursive", false);
        var maxDepth  = JsonArgs.OptionalInt(args, "maxDepth");
        if (maxDepth is < 1)
        {
            throw new InvalidDataException("Tool argument 'maxDepth' must be a positive integer when provided.");
        }

        if (!recursive && maxDepth is not null)
        {
            throw new InvalidDataException("Tool argument 'maxDepth' requires 'recursive' to be true.");
        }

        var absolute = workspace.ResolveInside(rawPath, "path");
        args["path"] = absolute; // Normalized plan value; Execute never re-resolves raw input.
        return new ToolPreparation
        {
            ToolName   = Definition.Name,
            CallId     = call.Id,
            Arguments  = args,
            Capability = "filesystem.list",
            Summary = $"list_files {workspace.ToDisplay(absolute)}" + (recursive
                ? maxDepth is null ? " (recursive)" : $" (recursive, depth {maxDepth})"
                : string.Empty),
            TargetPaths = [absolute],
        };
    }

    public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var absolute = ToolArgs.ReadAbsolute(preparation, "path");
        if (!Directory.Exists(absolute))
        {
            return Task.FromResult(new ToolResult
            {
                Succeeded = false,
                Content   = $"Directory not found: {workspace.ToDisplay(absolute)}",
            });
        }

        workspace.EnsureFinalTargetInside(absolute, isDirectory : true, "Directory");
        var recursive = JsonArgs.OptionalBool(preparation.Arguments, "recursive", false);
        var maxDepth  = JsonArgs.OptionalInt(preparation.Arguments, "maxDepth");

        var result = DirectoryWalker.CollectEntries(absolute, recursive, maxDepth, MaxEntries, cancellationToken);
        if (result.Entries.Count == 0)
        {
            return Task.FromResult(new ToolResult
            {
                Succeeded = true,
                Content   = $"(no entries in {workspace.ToDisplay(absolute)})",
            });
        }

        var lines = new List<string>(result.Entries.Count);
        foreach (var entry in result.Entries)
        {
            var isDir = Directory.Exists(entry);
            lines.Add(workspace.ToDisplay(entry) + (isDir ? "/" : string.Empty));
        }

        if (result.Truncated)
        {
            lines.Add($"... (only the first {MaxEntries} entries are shown)");
        }

        return Task.FromResult(new ToolResult { Succeeded = true, Content = string.Join('\n', lines) });
    }
}
