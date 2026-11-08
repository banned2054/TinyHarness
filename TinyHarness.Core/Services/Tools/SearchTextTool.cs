using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         在工作区文本文件中按行执行字面子串或 NonBacktracking 正则搜索。结果格式为"路径:行号: 内容"，
///         会跳过二进制、超大文件以及最终目标逃逸工作区的文件链接，并对扫描量、匹配量与总输出量设置上限；
///         超时、截断与部分计数都在结果中显式标注，绝不显示为权威的"无匹配"。
///     </para>
///     <para>
///         search_text: line-based literal or NonBacktracking-regex search over workspace text files. Matches are
///         reported as "path:line: text". Binary files, oversized files and file links whose final target escapes
///         the workspace are skipped and counted. The walk is bounded by the DirectoryWalker exclusion list and by
///         <see cref="MaxFilesScanned" /> (a truncated walk is reported), the match list stops at
///         <see cref="DefaultMaxResults" />/<see cref="AbsoluteMaxResults" />, and total output stops at the
///         64k-character budget. Regex mode uses the NonBacktracking engine with a per-match timeout; unsupported
///         syntax fails preparation instead of switching engines. Include/exclude globs filter the already
///         authorized scan set only and never widen access; context blocks merge adjacent windows; the
///         <c>files</c>/<c>count</c> output modes remain subject to read-permission constraints, and
///         policy-excluded files never appear by name, count, or snippet. The optional
///         <see cref="WorkerReadPolicy" />, injected only by the WorkerRunner, rejects out-of-scope explicit
///         paths in Prepare, filters walked entries, re-checks every scanned file's resolved link chain, and
///         skips excluded paths by count without ever revealing their names or content.
///     </para>
/// </summary>
public sealed class SearchTextTool(
    Workspace             workspace,
    WorkerReadPolicy?     workerPolicy   = null,
    ResourceAccessPolicy? resourcePolicy = null) : ITool
{
    private const int DefaultMaxResults = 100;

    private const int AbsoluteMaxResults = 500;

    private const int MaxFilesScanned = 3_000;

    private const long MaxFileBytes = 4 * 1024 * 1024;

    private const int BinaryProbeBytes = 512;

    private const int MaxLineDisplayChars = 240;

    /// <summary>正则模式允许的最长 pattern；超限在准备期报错。</summary>
    private const int MaxPatternCharacters = 512;

    /// <summary>上下文行数上限（每侧）；超出在准备期报错。</summary>
    private const int MaxContextLines = 5;

    /// <summary>include/exclude 各自允许的 pattern 数上限。</summary>
    private const int MaxIncludeExcludePatterns = 16;

    /// <summary>总输出预算（字符）；超出即截断并显式标注。</summary>
    private const int TotalOutputBudgetCharacters = 64 * 1024;

    /// <summary>NonBacktracking 单次匹配的超时；触发后整次扫描提前停止并标注部分结果。</summary>
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromSeconds(1);

    private static readonly JsonObject Schema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "Text to search for, or a regex when mode is \"regex\" (max 512 characters)."
            },
            ["path"] = new JsonObject
            {
                ["type"]        = "string",
                ["description"] = "File or directory to search, relative to the workspace root (default \".\")."
            },
            ["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("literal", "regex"),
                ["description"] =
                    "Match mode. \"literal\" (default) does a case-insensitive substring match per line; "       +
                    "\"regex\" compiles the pattern with the .NET NonBacktracking engine and matches each line " +
                    "separately (no cross-line matches, no replacement). NonBacktracking does not support "      +
                    "lookaround, backreferences, or conditionals; unsupported syntax fails the call instead of " +
                    "falling back to another engine."
            },
            ["caseSensitive"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] =
                    "Case-sensitive match (default false). Also controls regex IgnoreCase and glob case sensitivity."
            },
            ["maxResults"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] =
                    "Maximum matches to scan before stopping early (default 100, max 500). It caps matches, " +
                    "not files; stopping early is reported as partial."
            },
            ["include"] = new JsonObject
            {
                ["type"]  = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] =
                    "Include glob(s) (*, **, ?, [...]) matched against workspace-relative paths. They only filter " +
                    "the already authorized scan set and never widen access; a file matches when any pattern "      +
                    "matches. An empty array means no filtering."
            },
            ["exclude"] = new JsonObject
            {
                ["type"]  = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] =
                    "Exclude glob(s) with the same semantics as include; a matching file is skipped. Exclude wins " +
                    "over include."
            },
            ["contextBefore"] = new JsonObject
            {
                ["type"]        = "integer",
                ["description"] = "Context lines before each match in content output (0-5, default 0)."
            },
            ["contextAfter"] = new JsonObject
            {
                ["type"]        = "integer",
                ["description"] = "Context lines after each match in content output (0-5, default 0)."
            },
            ["outputMode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("content", "files", "count"),
                ["description"] =
                    "\"content\" (default) prints match lines with optional context blocks; \"files\" prints the " +
                    "path of every file with at least one match; \"count\" prints \"path: matches\" per file. "    +
                    "All modes remain subject to read permissions; excluded files never appear."
            }
        },
        ["required"] = new JsonArray("pattern")
    };

    public ToolDefinition Definition { get; } = new()
    {
        Name = "search_text",
        Description = "Searches workspace text files per line for a literal substring or a NonBacktracking regex. " +
                      "Reports \"path:line: text\" matches, file lists, or per-file counts; include/exclude globs " +
                      "narrow the authorized scan set only; truncation, timeouts, and partial counts are always "   +
                      "marked explicitly.",
        Parameters = Schema
    };

    /// <summary>
    ///     校验搜索模式、正则语法、glob、上下文与输出上限，并把路径规范化到工作区内。
    ///     Validates the search mode, regex syntax, globs, context, and output caps, then normalizes the path
    ///     inside the workspace.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         正则在准备期编译：NonBacktracking 不支持的语法在此报错，该调用不进入执行。
    ///     </para>
    ///     <para>
    ///         The regex is compiled during preparation: unsupported NonBacktracking syntax fails here and the
    ///         invocation never executes.
    ///     </para>
    /// </remarks>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args          = ToolArgs.ParseObject(call);
        var pattern       = JsonArgs.Required(args, "pattern");
        var rawPath       = JsonArgs.Optional(args, "path", ".");
        var mode          = ParseChoice(args, "mode", "literal", "literal", "regex");
        var outputMode    = ParseChoice(args, "outputMode", "content", "content", "files", "count");
        var caseSensitive = JsonArgs.OptionalBool(args, "caseSensitive", false);
        var maxResults    = JsonArgs.OptionalInt(args, "maxResults") ?? DefaultMaxResults;
        if (maxResults is < 1 or > AbsoluteMaxResults)
            throw new InvalidDataException($"Tool argument 'maxResults' must be between 1 and {AbsoluteMaxResults}.");
        var contextBefore = ReadContextLines(args, "contextBefore");
        var contextAfter  = ReadContextLines(args, "contextAfter");
        if (pattern.Length > MaxPatternCharacters)
            throw new InvalidDataException($"Tool argument 'pattern' exceeds {MaxPatternCharacters} characters.");
        // 准备期即校验 glob 取值，无效项不进入执行。
        CompileGlobs(JsonArgs.OptionalStringArray(args, "include", MaxIncludeExcludePatterns), "include",
                     caseSensitive);
        CompileGlobs(JsonArgs.OptionalStringArray(args, "exclude", MaxIncludeExcludePatterns), "exclude",
                     caseSensitive);
        if (mode == "regex") CompileRegex(pattern, caseSensitive);

        var absolute = workspace.ResolveInside(rawPath, "path");
        workerPolicy?.EnsureLexicalAccessAllowed(absolute);
        // 统一资源分类：权限系统保护路径在此硬拒；其余类别只作为注记进入准备计划。
        var classification = resourcePolicy?.Evaluate(absolute, "path");
        args["path"] = absolute; // Normalized plan value; Execute never re-resolves raw input.
        return new ToolPreparation
        {
            ToolName               = Definition.Name,
            CallId                 = call.Id,
            Arguments              = args,
            Capability             = "filesystem.search",
            ExecutionPolicy        = WorkspaceFileToolPolicy.Identity(workspace.Root),
            ResourceClassification = classification,
            Summary = mode == "regex"
                ? $"search_text \"{pattern}\" (regex) in {workspace.ToDisplay(absolute)}"
                : $"search_text \"{pattern}\" in {workspace.ToDisplay(absolute)}",
            TargetPaths = [absolute],
            InstructionScopes =
            [
                new InstructionScope(absolute, InstructionScopeKind.RecursiveDirectory,
                                     MaxEntries : MaxFilesScanned, FilesOnly : true)
            ]
        };
    }

    /// <summary>
    ///     扫描单个文件或受限目录树，按输出模式汇总结果；扫描量、匹配量、超时与输出预算的提前停止都显式标注。
    ///     Scans one file or a bounded directory tree and reports results per output mode; early stops from scan,
    ///     match, timeout, or output-budget limits are always marked explicitly.
    /// </summary>
    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        var args          = preparation.Arguments;
        var absolute      = ToolArgs.ReadAbsolute(preparation, "path");
        var pattern       = JsonArgs.Required(args, "pattern");
        var mode          = ParseChoice(args, "mode", "literal", "literal", "regex");
        var outputMode    = ParseChoice(args, "outputMode", "content", "content", "files", "count");
        var caseSensitive = JsonArgs.OptionalBool(args, "caseSensitive", false);
        var maxResults    = JsonArgs.OptionalInt(args, "maxResults") ?? DefaultMaxResults;
        var contextBefore = ReadContextLines(args, "contextBefore");
        var contextAfter  = ReadContextLines(args, "contextAfter");
        var include = CompileGlobs(JsonArgs.OptionalStringArray(args, "include", MaxIncludeExcludePatterns),
                                   "include", caseSensitive);
        var exclude = CompileGlobs(JsonArgs.OptionalStringArray(args, "exclude", MaxIncludeExcludePatterns),
                                   "exclude", caseSensitive);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var regex      = mode       == "regex" ? CompileRegex(pattern, caseSensitive) : null;
        var useContext = outputMode == "content" && (contextBefore > 0 || contextAfter > 0);

        if (!File.Exists(absolute) && !Directory.Exists(absolute))
            return new ToolResult
            {
                Succeeded = false,
                Content   = $"Path not found: {workspace.ToDisplay(absolute)}"
            };

        workspace.EnsureFinalTargetInside(absolute, Directory.Exists(absolute), "Path");
        // 搜索只激活显式目标的祖先规则，不把候选文件的后代规则预加载到模型。
        preparation.Coverage?.EnsureCoveredDirectory(File.Exists(absolute)
                                                         ? Path.GetDirectoryName(absolute)!
                                                         : absolute);

        var files            = new List<string>();
        var walkLimited      = false;
        var sensitiveSkipped = 0;
        if (File.Exists(absolute))
        {
            files.Add(absolute);
        }
        else
        {
            // 枚举结果按策略逐项过滤；被排除目录不下降，因此不会进入扫描列表。
            // 敏感后代不因普通父目录的基线广泛读而进入扫描集：本次准备档不是敏感档时，
            // 遍历发现的敏感命中在入口即排除（不下降、不计数名字），读取须经 L2 审批。
            // Enumeration results are filtered by the policy; excluded directories are never
            // descended into, so they never reach the scan list. Sensitive descendants do not
            // enter the scan set through a normal parent's baseline broad read: while the
            // prepared strictest class is not the sensitive one, sensitive hits found by the
            // walk are excluded at the entry (never descended, never named); reading them
            // requires L2 approval.
            Func<string, bool>? excludeEntry = null;
            if (workerPolicy is not null || resourcePolicy is not null)
                excludeEntry = child =>
                {
                    if (workerPolicy is not null && !workerPolicy.IsEntryAllowed(child)) return true;

                    if (resourcePolicy is not null &&
                        resourcePolicy.RequiresApprovalForRecursiveRead(child,
                                                                        preparation.ResourceClassification))
                    {
                        sensitiveSkipped++;
                        return true;
                    }

                    return false;
                };

            var walk = DirectoryWalker.CollectFiles(absolute, MaxFilesScanned, cancellationToken, excludeEntry);
            files.AddRange(walk.Entries);
            walkLimited = walk.Truncated;
        }

        // include/exclude 只过滤已授权扫描集的相对路径，不产生任何新的访问范围。
        // Include/exclude filters operate on workspace-relative paths of the authorized scan
        // set only; they never create new access.
        var scanned         = files;
        var skippedFiltered = 0;
        if (include.Count > 0 || exclude.Count > 0)
        {
            scanned = [];
            foreach (var file in files)
                if (IsGlobMatched(file, include, exclude))
                    scanned.Add(file);
                else
                    skippedFiltered++;
        }

        var output           = new StringBuilder();
        var matchTotal       = 0;
        var filesWithMatches = 0;
        var filesScanned     = 0;
        var skippedLarge     = 0;
        var skippedBinary    = 0;
        var skippedEscaping  = 0;
        var skippedExcluded  = 0;
        var skippedProtected = 0;
        var stoppedAtCap     = false;
        var regexTimedOut    = false;
        var outputTruncated  = false;

        foreach (var file in scanned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matchTotal >= maxResults)
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

            var display = workspace.ToDisplay(file);
            var scan = await ScanFileAsync(file, pattern, regex, comparison, maxResults - matchTotal, workspace,
                                           workerPolicy, resourcePolicy, preparation.ResourceClassification,
                                           cancellationToken).ConfigureAwait(false);
            switch (scan.SkipReason)
            {
                case ScanSkipReason.Binary :
                    skippedBinary++;
                    continue;
                case ScanSkipReason.OutsideWorkspace :
                    skippedEscaping++;
                    continue;
                case ScanSkipReason.Excluded :
                    skippedExcluded++;
                    continue;
                case ScanSkipReason.PermissionProtected :
                    skippedProtected++;
                    continue;
                case ScanSkipReason.ApprovalRequired :
                    sensitiveSkipped++;
                    continue;
            }

            filesScanned++;
            if (scan.TimedOut) regexTimedOut = true;
            if (scan.MatchLines.Count > 0)
            {
                matchTotal += scan.MatchLines.Count;
                filesWithMatches++;
                var appended = outputMode switch
                {
                    "files" => AppendWithinBudget(output, $"{display}\n"),
                    "count" => AppendWithinBudget(output, $"{display}: {scan.MatchLines.Count}\n"),
                    _ when useContext => await AppendContextBlocksAsync(output, file, display, scan.MatchLines,
                                                                        contextBefore, contextAfter, cancellationToken)
                       .ConfigureAwait(false),
                    _ => AppendPlainMatches(output, display, scan.MatchLines, scan.MatchTexts)
                };
                if (!appended)
                {
                    outputTruncated = true;
                    break;
                }
            }

            if (scan.TimedOut) break;
            if (!scan.StoppedMidFile) continue;
            stoppedAtCap = true;
            break;
        }

        var details = new List<string>(8);
        if (skippedFiltered > 0) details.Add($"skipped {skippedFiltered} file(s) by include/exclude filters");

        if (skippedEscaping > 0) details.Add($"skipped {skippedEscaping} file link(s) to outside the workspace");

        if (skippedExcluded > 0) details.Add($"skipped {skippedExcluded} excluded path(s)");

        if (skippedBinary > 0) details.Add($"skipped {skippedBinary} binary file(s)");

        if (skippedLarge > 0) details.Add($"skipped {skippedLarge} large file(s)");

        if (skippedProtected > 0)
            details.Add($"skipped {skippedProtected} file(s) resolving into permission-system protected paths");

        if (sensitiveSkipped > 0)
            details.Add($"skipped {sensitiveSkipped} path(s) inside sensitive locations that were not approved " +
                        "for this search");

        if (walkLimited) details.Add($"only the first {MaxFilesScanned} files scanned");

        if (regexTimedOut) details.Add("regex match timeout; results are partial");

        if (outputTruncated)
            details.Add($"output truncated at {TotalOutputBudgetCharacters} characters; results are partial");

        if (stoppedAtCap && outputMode != "content")
            details.Add($"stopped after {matchTotal} matches; results are partial");

        var note         = details.Count == 0 ? string.Empty : "; " + string.Join("; ", details);
        var stoppedEarly = walkLimited || regexTimedOut || outputTruncated || stoppedAtCap;
        var hasRows      = output.Length > 0;

        var content = new StringBuilder();
        content.Append(output);
        if (!hasRows)
        {
            // 提前停止时绝不给出权威的"无匹配"；必须连同部分扫描事实一起说明。
            content.Append(stoppedEarly
                               ? $"(no matches within scanned limits; results are partial{note})"
                               : $"(no matches for '{pattern}'{note})");
        }
        else if (outputMode == "content")
        {
            var matchWord = matchTotal == 1 ? "match" : "matches";
            if (stoppedAtCap)
                content.Append($"... (stopped after {matchTotal} {matchWord}");
            else
                content.Append($"({matchTotal} {matchWord} in {filesScanned} " +
                               (filesScanned == 1 ? "file" : "files"));

            content.Append(note).Append(')');
        }
        else
        {
            var fileWord = filesWithMatches == 1 ? "file" : "files";
            content.Append($"({filesWithMatches} {fileWord} with matches{note})");
        }

        if (outputTruncated)
            content.Append($"... (output truncated at {TotalOutputBudgetCharacters} characters; results are partial)");

        return new ToolResult { Succeeded = true, Content = content.ToString().TrimEnd('\n') };
    }

    /// <summary>
    ///     逐行扫描一个文件；文件像二进制、链接目标越界、最终落点进入权限系统保护路径，
    ///     或命中未获批敏感位置时返回跳过状态，否则返回匹配行号与原文、截断状态与正则超时状态。
    ///     Scans one file line by line. Returns a skip outcome when the file looks
    ///     binary, when its link's final target escapes the workspace, when it
    ///     resolves into a permission-system protected path, or when it hits a
    ///     sensitive location this invocation never had approved; otherwise the matched
    ///     line numbers with their raw text, whether the scan stopped mid-file at the
    ///     match cap, and whether the regex timed out.
    /// </summary>
    private async Task<FileScan> ScanFileAsync(string file, string pattern, Regex? regex, StringComparison comparison,
                                               int maxMatches, Workspace workspace,
                                               WorkerReadPolicy? workerPolicy,
                                               ResourceAccessPolicy? resourcePolicy,
                                               ResourceClassification? preparationClassification,
                                               CancellationToken cancellationToken)
    {
        // A file found by the walk can itself be a symlink/junction whose final
        // target escapes the workspace even though the searched root does not;
        // re-check the resolved boundary before touching each file.
        // Explicit single-file paths were already checked in ExecuteAsync, so
        // this only fires for walk-discovered links: skip them, never read.
        try
        {
            workspace.EnsureFinalTargetInside(file, false, "File");
        }
        catch (InvalidDataException)
        {
            return FileScan.OutsideWorkspace;
        }

        // 权限系统保护路径（Prepare 与 Execute 之间新增的链接改向也在防护范围内）：
        // 命中按跳过计数，异常消息含保护路径名，绝不进入输出。
        // Permission-system protected paths (including link redirection created
        // between Prepare and Execute): a hit is skipped and counted; the exception
        // message carries the protected path, so it never reaches the output.
        if (resourcePolicy is not null)
            try
            {
                resourcePolicy.EnsureNotPermissionProtected(file, "File");
            }
            catch (InvalidDataException)
            {
                return FileScan.PermissionProtected;
            }

        // 执行期敏感复核（遍历排除之外的第二层）：遍历与扫描之间新增的敏感命中或链接改向
        // 不落入未获批扫描；内容绝不读取、路径名绝不出现。
        // The execute-time sensitive re-check (a second layer beyond the walk filter): sensitive
        // hits or link redirections created between the walk and the scan never enter an
        // unapproved scan; the content is never read and the path is never shown.
        if (resourcePolicy is not null &&
            resourcePolicy.RequiresApprovalForRecursiveRead(file, preparationClassification))
            return FileScan.ApprovalRequired;

        // 策略的最终复核：链接链解析后的目标仍须落在 worker 读取范围内（focus/敏感排除）；
        // 不合格按跳过计数，绝不读取内容、不暴露路径名。
        // The policy's final re-check: the resolved link-chain target must also be inside the worker
        // read scope (focus/sensitive). Disqualified files are skipped and counted; their content is
        // never read and their names never revealed.
        if (workerPolicy is not null)
            try
            {
                workerPolicy.EnsureFinalAccessAllowed(file);
            }
            catch (InvalidDataException)
            {
                return FileScan.Excluded;
            }

        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                                FileOptions.Asynchronous);
        var probe = new byte[BinaryProbeBytes];
        var read = await stream.ReadAsync(probe.AsMemory(0, BinaryProbeBytes), cancellationToken)
                               .ConfigureAwait(false);
        for (var i = 0; i < read; i++)
            if (probe[i] == 0)
                return FileScan.Binary;

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, true,
                                            64 * 1024, false);

        var     matchLines = new List<int>(Math.Min(maxMatches, 64));
        var     matchTexts = new List<string>(Math.Min(maxMatches, 64));
        var     lineNumber = 0;
        var     timedOut   = false;
        string? line;
        while (matchLines.Count < maxMatches &&
               (line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            var isMatch = regex is { } compiled
                ? TryMatch(compiled, line, ref timedOut)
                : line.Contains(pattern, comparison);
            if (!isMatch) continue;

            matchLines.Add(lineNumber);
            matchTexts.Add(line);
        }

        // Distinguish "the file had exactly maxMatches and ended" from "we cut it
        // off mid-file" with one extra probe read.
        var stoppedMidFile = false;
        if (matchLines.Count < maxMatches) return new FileScan(matchLines, matchTexts, false, timedOut, null);
        var more = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        stoppedMidFile = more is not null;

        return new FileScan(matchLines, matchTexts, stoppedMidFile, timedOut, null);
    }

    /// <summary>
    ///     正则单行匹配；超时记为扫描提前停止的原因之一，不吞掉异常语义。
    ///     Matches one line with the regex; a timeout becomes an explicit early-stop
    ///     reason instead of a silently swallowed failure.
    /// </summary>
    private static bool TryMatch(Regex regex, string line, ref bool timedOut)
    {
        try
        {
            return regex.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            timedOut = true;
            return false;
        }
    }

    /// <summary>
    ///     追加一个输出单元；总输出预算超限时返回 false，由调用方标记截断并停止扫描。
    ///     Appends one output unit; returns false once the total output budget would
    ///     be exceeded so the caller marks truncation and stops scanning.
    /// </summary>
    private static bool AppendWithinBudget(StringBuilder output, string text)
    {
        if (output.Length + text.Length > TotalOutputBudgetCharacters) return false;

        output.Append(text);
        return true;
    }

    /// <summary>
    ///     按现有格式追加匹配行；任一行超预算即整体返回 false。
    ///     Appends match lines in the established format; returns false as a whole
    ///     once any line would exceed the budget.
    /// </summary>
    private static bool AppendPlainMatches(StringBuilder         output, string display, IReadOnlyList<int> matchLines,
                                           IReadOnlyList<string> matchTexts)
    {
        for (var i = 0; i < matchLines.Count; i++)
        {
            var text  = matchTexts[i];
            var shown = text.Length <= MaxLineDisplayChars ? text : text[..MaxLineDisplayChars] + "...";
            if (!AppendWithinBudget(output, $"{display}:{matchLines[i]}: {shown}\n")) return false;
        }

        return true;
    }

    /// <summary>
    ///     重新读取文件并把匹配窗口合并为上下文块输出；相邻或重叠窗口合并为一块，块间以 -- 分隔。
    ///     Re-reads the file and emits merged context blocks; adjacent or overlapping
    ///     windows merge into one block and blocks are separated by --.
    /// </summary>
    private async Task<bool> AppendContextBlocksAsync(StringBuilder output, string file, string display,
                                                      IReadOnlyList<int> matchLines, int contextBefore,
                                                      int contextAfter, CancellationToken cancellationToken)
    {
        var lines = new List<string>(256);
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                                                 FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                                                 FileOptions.Asynchronous))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024, false);
            string?   line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
                lines.Add(line);
        }

        var lastIndex = lines.Count - 1;
        var blocks    = new List<(int Start, int End)>();
        foreach (var match in matchLines)
        {
            // 匹配行号是 1 基；窗口计算统一转 0 基下标。
            var matchIndex = match - 1;
            var start      = Math.Max(0, matchIndex         - contextBefore);
            var end        = Math.Min(lastIndex, matchIndex + contextAfter);
            if (blocks.Count > 0 && start <= blocks[^1].End + 1)
            {
                if (end > blocks[^1].End) blocks[^1] = (blocks[^1].Start, end);
            }
            else
            {
                blocks.Add((start, end));
            }
        }

        var matches = new HashSet<int>(matchLines.Select(line => line - 1));
        foreach (var (start, end) in blocks)
        {
            if (!AppendWithinBudget(output, $"{display}:{start + 1}-{end + 1}:\n")) return false;

            for (var n = start; n <= end; n++)
            {
                var marker = matches.Contains(n) ? ">" : " ";
                var text   = lines[n];
                var shown  = text.Length <= MaxLineDisplayChars ? text : text[..MaxLineDisplayChars] + "...";
                if (!AppendWithinBudget(output, $"{marker} {n + 1}: {shown}\n")) return false;
            }

            if (!AppendWithinBudget(output, "--\n")) return false;
        }

        return true;
    }

    /// <summary>
    ///     判断文件是否通过 include/exclude 过滤：include 任一命中才保留，exclude 任一命中即排除。
    ///     Decides whether a file passes the include/exclude filters: any include match
    ///     keeps it, and any exclude match drops it.
    /// </summary>
    private bool IsGlobMatched(string file, IReadOnlyList<GlobMatcher> include, IReadOnlyList<GlobMatcher> exclude)
    {
        // 相对路径统一使用 '/' 分隔；文件来自已授权扫描集，因此相对路径必然在工作区内。
        var relative = Path.GetRelativePath(workspace.Root, file).Replace('\\', '/');
        if (exclude.Any(pattern => pattern.Matches(relative))) return false;

        return include.Count == 0 || include.Any(pattern => pattern.Matches(relative));
    }

    /// <summary>
    ///     编译 glob 列表；空白 pattern 在准备期报错，未闭合 '[' 按字面量处理。
    ///     Compiles a glob list; blank patterns fail in preparation and an
    ///     unterminated '[' is treated as a literal.
    /// </summary>
    private static IReadOnlyList<GlobMatcher> CompileGlobs(IReadOnlyList<string> patterns, string key,
                                                           bool                  caseSensitive)
    {
        var result = new List<GlobMatcher>(patterns.Count);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                throw new InvalidDataException($"Tool argument '{key}' must not contain blank glob patterns.");

            result.Add(new GlobMatcher(pattern, caseSensitive));
        }

        return result;
    }

    /// <summary>
    ///     编译 NonBacktracking 正则；不支持的语法与其他无效 pattern 都转换为含上下文的准备错误，不静默换引擎。
    ///     Compiles a NonBacktracking regex; unsupported syntax and other invalid patterns become contextual
    ///     preparation errors instead of silently switching engines.
    /// </summary>
    private static Regex CompileRegex(string pattern, bool caseSensitive)
    {
        var options                 = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;

        try
        {
            return new Regex(pattern, options, RegexMatchTimeout);
        }
        catch (RegexParseException ex)
        {
            throw new InvalidDataException(
                                           $"Tool argument 'pattern' is not supported by the NonBacktracking regex engine: {ex.Message}");
        }
        catch (NotSupportedException ex)
        {
            // 环视、反向引用等语法由符号引擎在构造期以 NotSupportedException 拒绝；报错后不换引擎。
            throw new InvalidDataException(
                                           $"Tool argument 'pattern' is not supported by the NonBacktracking regex engine: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Tool argument 'pattern' is not a valid regex: {ex.Message}");
        }
    }

    /// <summary>
    ///     读取上下文行数参数；超出 0–5 范围在准备期报错。
    ///     Reads a context-line argument; values outside 0-5 fail in preparation.
    /// </summary>
    private static int ReadContextLines(JsonObject args, string key)
    {
        var value = JsonArgs.OptionalInt(args, key) ?? 0;
        if (value is < 0 or > MaxContextLines)
            throw new InvalidDataException($"Tool argument '{key}' must be between 0 and {MaxContextLines}.");

        return value;
    }

    /// <summary>
    ///     读取受限枚举字符串参数；缺失用回退值，未知取值在准备期报错。
    ///     Reads a restricted enum-string argument; a missing value falls back and an
    ///     unknown value fails in preparation.
    /// </summary>
    private static string ParseChoice(JsonObject args, string key, string fallback, params string[] allowed)
    {
        var node = args[key];
        if (node is null) return fallback;

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null)
            throw new InvalidDataException($"Tool argument '{key}' must be a string.");
        if (!allowed.Contains(text, StringComparer.Ordinal))
            throw new InvalidDataException($"Tool argument '{key}' must be one of: {string.Join(", ", allowed)}.");

        return text;
    }

    /// <summary>单文件扫描结果：匹配行号与原文、截断状态、超时状态与跳过原因。</summary>
    private sealed record FileScan(
        List<int>       MatchLines,
        List<string>    MatchTexts,
        bool            StoppedMidFile,
        bool            TimedOut,
        ScanSkipReason? SkipReason)
    {
        public static FileScan Binary { get; } = new([], [], false, false, ScanSkipReason.Binary);

        public static FileScan OutsideWorkspace { get; } = new([], [], false, false, ScanSkipReason.OutsideWorkspace);

        public static FileScan Excluded { get; } = new([], [], false, false, ScanSkipReason.Excluded);

        public static FileScan PermissionProtected { get; } =
            new([], [], false, false, ScanSkipReason.PermissionProtected);

        public static FileScan ApprovalRequired { get; } =
            new([], [], false, false, ScanSkipReason.ApprovalRequired);
    }

    private enum ScanSkipReason
    {
        /// <summary>
        ///     文件内容像二进制，不再扫描。
        ///     File content looks binary and is not scanned further.
        /// </summary>
        Binary,

        /// <summary>
        ///     文件链接的最终目标逃逸工作区。
        ///     File link whose final target escapes the workspace.
        /// </summary>
        OutsideWorkspace,

        /// <summary>
        ///     文件被 worker 读取策略排除（focus 范围外或敏感路径）。
        ///     The file is excluded by the worker read policy (outside focus or a sensitive path).
        /// </summary>
        Excluded,

        /// <summary>
        ///     文件最终落点进入权限系统保护路径；按跳过计数，绝不读取或显示。
        ///     The file resolves into a permission-system protected path; skipped and counted, never read or shown.
        /// </summary>
        PermissionProtected,

        /// <summary>
        ///     文件命中未获批的已知敏感位置；按跳过计数，绝不读取或显示。
        ///     The file hits a known sensitive location this invocation never had approved;
        ///     skipped and counted, never read or shown.
        /// </summary>
        ApprovalRequired
    }

    /// <summary>
    ///     <para>
    ///         支持 <c>*</c>、<c>**</c>、<c>?</c> 与 <c>[...]</c> 的路径 glob。仅独占一段的 <c>**</c> 跨目录
    ///         （可匹配零段），未闭合的 <c>[</c> 按字面量处理；匹配只针对已授权扫描集的相对路径。
    ///     </para>
    ///     <para>
    ///         A path glob supporting <c>*</c>, <c>**</c>, <c>?</c> and <c>[...]</c>. A standalone <c>**</c>
    ///         segment crosses directories and matches zero or more segments, an unterminated <c>[</c> stays
    ///         literal, and matching only ever applies to relative paths of the authorized scan set.
    ///     </para>
    /// </summary>
    private sealed class GlobMatcher(string pattern, bool caseSensitive)
    {
        private readonly string[] _segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>判断 '/' 分隔的工作区相对路径是否命中；空白 pattern 在编译期已被拒绝。</summary>
        public bool Matches(string relativePath)
        {
            var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return MatchFrom(0, 0, parts);
        }

        /// <summary>按段递归匹配；独占段的 <c>**</c> 可跳过零或多段，其余段逐段通配。</summary>
        private bool MatchFrom(int patternIndex, int valueIndex, string[] parts)
        {
            if (patternIndex == _segments.Length) return valueIndex == parts.Length;
            if (IsCrossSegment(patternIndex))
            {
                for (var skip = 0; valueIndex + skip <= parts.Length; skip++)
                    if (MatchFrom(patternIndex + 1, valueIndex + skip, parts))
                        return true;

                return false;
            }

            if (valueIndex == parts.Length) return false;
            if (!MatchSegment(_segments[patternIndex], parts[valueIndex])) return false;

            return MatchFrom(patternIndex + 1, valueIndex + 1, parts);
        }

        /// <summary>判断该段是否为独占的 <c>**</c> 跨目录段；段内 <c>**</c> 与 <c>*</c> 等价。</summary>
        private bool IsCrossSegment(int patternIndex) =>
            string.Equals(_segments[patternIndex], "**", StringComparison.Ordinal);

        /// <summary>用动态规划匹配单段通配，避免回退式回溯。</summary>
        private bool MatchSegment(string segment, string value)
        {
            var vlen = value.Length;
            var dp   = new bool[vlen + 1];
            dp[0] = true;
            var i = 0;
            while (i < segment.Length)
            {
                var c    = segment[i];
                var next = new bool[vlen + 1];
                if (c == '*')
                {
                    var any = false;
                    for (var j = 0; j <= vlen; j++)
                    {
                        any     |= dp[j];
                        next[j] =  any;
                    }
                }
                else if (c == '?')
                {
                    for (var j = 0; j < vlen; j++) next[j + 1] = dp[j];
                }
                else if (c == '[')
                {
                    var parsed = TryParseClass(segment, i, out var negated, out var singles, out var ranges,
                                               out var end);
                    if (!parsed)
                    {
                        // 未闭合 '[' 按字面量处理，与常见 glob 语义一致。
                        for (var j = 0; j < vlen; j++) next[j + 1] = dp[j] && CharsEqual(value[j], '[');

                        i++;
                        dp = next;
                        continue;
                    }

                    for (var j = 0; j < vlen; j++)
                        next[j + 1] = dp[j] && ClassMatches(value[j], negated, singles, ranges);

                    i  = end + 1;
                    dp = next;
                    continue;
                }
                else
                {
                    for (var j = 0; j < vlen; j++) next[j + 1] = dp[j] && CharsEqual(value[j], c);
                }

                i++;
                dp = next;
            }

            return dp[vlen];
        }

        /// <summary>
        ///     解析字符类并给出结束下标。支持 <c>[abc]</c>、<c>[a-z]</c>、<c>[!a-z]</c>/<c>[^a-z]</c>；
        ///     首位 <c>]</c> 视为字面量成员；未闭合时返回 false。
        ///     Parses a character class and yields the closing index. Supports
        ///     <c>[abc]</c>, <c>[a-z]</c>, and <c>[!a-z]</c>/<c>[^a-z]</c>; a leading <c>]</c> is a literal
        ///     member; returns false when unterminated.
        /// </summary>
        private static bool TryParseClass(string segment, int start, out bool negated, out char[] singles,
                                          out (char Lo, char Hi)[] ranges, out int end)
        {
            negated = false;
            singles = [];
            ranges  = [];
            end     = start;
            var i = start + 1;
            if (i < segment.Length && segment[i] is '!' or '^')
            {
                negated = true;
                i++;
            }

            var members = new List<char>();
            var spans   = new List<(char Lo, char Hi)>();
            var closed  = false;
            if (i < segment.Length && segment[i] == ']')
            {
                members.Add(']');
                i++;
            }

            while (i < segment.Length)
            {
                if (segment[i] == ']')
                {
                    closed = true;
                    end    = i;
                    break;
                }

                if (i + 2 < segment.Length && segment[i + 1] == '-' && segment[i + 2] != ']')
                {
                    spans.Add((segment[i], segment[i + 2]));
                    i += 3;
                }
                else
                {
                    members.Add(segment[i]);
                    i++;
                }
            }

            if (!closed) return false;

            singles = [.. members];
            ranges  = [.. spans];
            return true;
        }

        /// <summary>字符类成员判断；大小写不敏感时两侧折叠后比较，再套用否定。</summary>
        private bool ClassMatches(char value, bool negated, char[] singles, (char Lo, char Hi)[] ranges)
        {
            var hit = false;
            foreach (var single in singles)
                if (CharsEqual(value, single))
                {
                    hit = true;
                    break;
                }

            if (!hit)
                foreach (var (lo, hi) in ranges)
                {
                    var low  = caseSensitive ? lo : char.ToLowerInvariant(lo);
                    var high = caseSensitive ? hi : char.ToLowerInvariant(hi);
                    if ((caseSensitive ? value : char.ToLowerInvariant(value)) >= low &&
                        (caseSensitive ? value : char.ToLowerInvariant(value)) <= high)
                    {
                        hit = true;
                        break;
                    }
                }

            return negated ? !hit : hit;
        }

        /// <summary>单字符比较；大小写不敏感时折叠后比较。</summary>
        private bool CharsEqual(char left, char right) =>
            caseSensitive ? left == right : char.ToLowerInvariant(left) == char.ToLowerInvariant(right);
    }
}
