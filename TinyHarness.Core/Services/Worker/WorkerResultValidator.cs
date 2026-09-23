using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     在 Core 内对 worker 结果做交回调用方前的最终校验。模型输出按非可信资料处理：
///     各状态的合法形状（Completed 有结论且无状态说明；Incomplete/Failed/Cancelled 只带
///     状态说明、不携带成功结论）、统计非负、证据路径形状与行号、各输出部分的长度与
///     条数、以及总字符数都在这里强制执行。校验失败返回字段级错误，不抛异常穿透到
///     MCP 调用方；文件系统访问判断仍由现有 Workspace 与只读工具边界执行，本层只做
///     字符串卫生检查。
///     Performs the final validation of a worker result before it is handed back to the caller. Model
///     output is treated as untrusted: the legal shape per status (Completed carries a conclusion and no
///     status detail; Incomplete/Failed/Cancelled carry a status detail and never a conclusion), non-negative
///     statistics, evidence path shape and line numbers, per-section length and count limits, and the total
///     character budget are all enforced here. Failures come back as field-level errors instead of escaping
///     as exceptions. Filesystem access decisions stay with the existing Workspace and read-only tool
///     boundaries; this layer performs string hygiene checks only.
/// </summary>
public static class WorkerResultValidator
{
    public static WorkerResultValidationResult Validate(WorkerResult? result)
    {
        var errors = new List<string>();

        if (result is null)
        {
            errors.Add("Field 'result' is required; the payload must describe a worker run outcome.");
            return new WorkerResultValidationResult { Errors = errors };
        }

        ValidateStatusShape(result, errors);
        ValidateEvidence(result.Evidence, errors);
        ValidateStringList(result.SuggestedChanges, "suggestedChanges", errors);
        ValidateStringList(result.TestSuggestions, "testSuggestions", errors);
        ValidateStringList(result.Uncertainties, "uncertainties", errors);
        ValidateStatistics(result.Statistics, errors);
        ValidateTotalLength(result, errors);

        return new WorkerResultValidationResult { Errors = errors };
    }

    /// <summary>
    ///     状态与结论/状态说明的合法组合：
    ///     Completed 必须有非空结论且不得携带状态说明；Incomplete、Failed、Cancelled 不得携带
    ///     成功结论，必须有非空状态说明。证据、建议与不确定项在各状态下都可选。
    ///     Legal combinations of status, conclusion and status detail: Completed requires a non-empty
    ///     conclusion and forbids a status detail; Incomplete, Failed and Cancelled forbid a conclusion and
    ///     require a non-empty status detail. Evidence, suggestions and uncertainties stay optional.
    /// </summary>
    private static void ValidateStatusShape(WorkerResult result, List<string> errors)
    {
        var status = result.Status;

        if (status is not (WorkerResultStatus.Completed
                        or WorkerResultStatus.Incomplete
                        or WorkerResultStatus.Failed
                        or WorkerResultStatus.Cancelled))
        {
            errors.Add($"Field 'status' has an unknown value '{status}'.");
            return;
        }

        var conclusion   = result.Conclusion   ?? string.Empty;
        var statusDetail = result.StatusDetail ?? string.Empty;

        // statusDetail 长度上限无条件生效：即使是 Completed 下的长空白输入也要拒绝，
        // 防止反序列化出的异常形状绕过状态形状检查。
        // The statusDetail length limit applies unconditionally: even long whitespace-only values under
        // Completed are rejected, so malformed shapes cannot slip past the status shape checks.
        if (statusDetail.Length > WorkerResultLimits.MaxStatusDetailLength)
            errors.Add(
                       $"Field 'statusDetail' exceeds the maximum length of {WorkerResultLimits.MaxStatusDetailLength} characters (actual: {statusDetail.Length}).");

        if (status == WorkerResultStatus.Completed)
        {
            if (string.IsNullOrWhiteSpace(conclusion))
                errors.Add("Field 'conclusion' is required and must contain non-whitespace characters when status is 'completed'.");
            else if (conclusion.Length > WorkerResultLimits.MaxConclusionLength)
                errors.Add(
                           $"Field 'conclusion' exceeds the maximum length of {WorkerResultLimits.MaxConclusionLength} characters (actual: {conclusion.Length}).");

            if (!string.IsNullOrWhiteSpace(statusDetail))
                errors.Add("Field 'statusDetail' must be empty when status is 'completed'; non-completion reasons belong on non-completed statuses.");

            return;
        }

        if (!string.IsNullOrWhiteSpace(conclusion))
            errors.Add(
                       $"Field 'conclusion' must be empty when status is '{status}'; only completed runs may carry a conclusion.");

        if (string.IsNullOrWhiteSpace(statusDetail))
            errors.Add(
                       $"Field 'statusDetail' is required and must contain non-whitespace characters when status is '{status}'.");
    }

    private static void ValidateEvidence(IReadOnlyList<WorkerEvidence>? evidence, List<string> errors)
    {
        if (evidence is not { Count: > 0 }) return;

        if (evidence.Count > WorkerResultLimits.MaxEvidenceCount)
            errors.Add(
                       $"Field 'evidence' contains {evidence.Count} citations; the maximum is {WorkerResultLimits.MaxEvidenceCount}.");

        for (var i = 0; i < evidence.Count; i++)
        {
            var item = evidence[i];

            if (item is null)
            {
                errors.Add($"Field 'evidence[{i}]' is required.");
                continue;
            }

            ValidateEvidencePath(item.Path, i, errors);
            ValidateEvidenceLines(item, i, errors);

            var noteLength = item.Note?.Length ?? 0;
            if (noteLength > WorkerResultLimits.MaxEvidenceNoteLength)
                errors.Add(
                           $"Field 'evidence[{i}].note' exceeds the maximum length of {WorkerResultLimits.MaxEvidenceNoteLength} characters (actual: {noteLength}).");
        }
    }

    private static void ValidateEvidencePath(string? path, int index, List<string> errors)
    {
        var field = $"Field 'evidence[{index}].path'";

        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"{field} is required and must contain non-whitespace characters.");
            return;
        }

        if (path.Length > WorkerResultLimits.MaxEvidencePathLength)
        {
            errors.Add(
                       $"{field} exceeds the maximum length of {WorkerResultLimits.MaxEvidencePathLength} characters (actual: {path.Length}).");
            return;
        }

        if (path.Any(char.IsControl))
        {
            errors.Add($"{field} must not contain control characters.");
            return;
        }

        // 只做字符串卫生检查：跨平台拒绝盘符、盘符相对、UNC 与任一风格的根化路径，
        // 绝不允许 ".." 段。不解析路径、不访问文件；真实访问边界仍是 Workspace 与只读工具。
        // String hygiene only: drive-qualified, drive-relative, UNC and rooted paths are rejected on
        // every platform, and never a ".." segment. No path resolution, no file access; the real access
        // boundary remains the Workspace and read-only tools.
        if (!WorkspaceRelativePath.IsAcceptableShape(path))
        {
            errors.Add(
                       $"{field} ('{path}') must be a workspace-relative path; rooted, absolute, drive-qualified or UNC paths are not accepted.");
            return;
        }

        if (ContainsParentSegment(path)) errors.Add($"{field} ('{path}') must not contain '..' path segments.");
    }

    private static void ValidateEvidenceLines(WorkerEvidence item, int index, List<string> errors)
    {
        if (item.LineStart is { } lineStart)
        {
            if (lineStart < 1)
                errors.Add(
                           $"Field 'evidence[{index}].lineStart' must be 1 or greater when provided (actual: {lineStart}).");

            if (item.LineEnd is { } lineEnd && lineEnd < lineStart)
                errors.Add(
                           $"Field 'evidence[{index}].lineEnd' must not be smaller than lineStart (actual: {lineEnd} < {lineStart}).");

            return;
        }

        if (item.LineEnd is not null)
            errors.Add($"Field 'evidence[{index}].lineEnd' requires lineStart to be provided as well.");
    }

    private static void ValidateStringList(IReadOnlyList<string>? entries, string fieldName, List<string> errors)
    {
        if (entries is not { Count: > 0 }) return;

        if (entries.Count > WorkerResultLimits.MaxListCount)
            errors.Add(
                       $"Field '{fieldName}' contains {entries.Count} entries; the maximum is {WorkerResultLimits.MaxListCount}.");

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];

            if (string.IsNullOrWhiteSpace(entry))
            {
                errors.Add($"Field '{fieldName}[{i}]' is required and must contain non-whitespace characters.");
                continue;
            }

            if (entry.Length > WorkerResultLimits.MaxListEntryLength)
                errors.Add(
                           $"Field '{fieldName}[{i}]' exceeds the maximum length of {WorkerResultLimits.MaxListEntryLength} characters (actual: {entry.Length}).");
        }
    }

    private static void ValidateStatistics(WorkerExecutionStats? statistics, List<string> errors)
    {
        // Statistics 声明为非空，但反序列化可能得到 null；在信任边界上返回字段级错误而非抛异常。
        // Statistics is declared non-nullable, but deserialization can still yield null; report a
        // field-level error instead of throwing across the trust boundary.
        if (statistics is null)
        {
            errors.Add("Field 'statistics' is required and must not be null.");
            return;
        }

        if (statistics.ModelRequests < 0) errors.Add("Field 'statistics.modelRequests' must not be negative.");

        if (statistics.ToolCalls < 0) errors.Add("Field 'statistics.toolCalls' must not be negative.");

        if (statistics.ToolOutputCharacters < 0)
            errors.Add("Field 'statistics.toolOutputCharacters' must not be negative.");

        if (statistics.Elapsed < TimeSpan.Zero) errors.Add("Field 'statistics.elapsed' must not be negative.");
    }

    private static void ValidateTotalLength(WorkerResult result, List<string> errors)
    {
        var conclusion   = result.Conclusion   ?? string.Empty;
        var statusDetail = result.StatusDetail ?? string.Empty;

        // 总字符数用宽整数累计：反序列化出的超大字段集合可能超过 int 范围，不能因溢出绕过总预算。
        // Accumulate in a wide integer: oversized field collections from deserialization could exceed
        // the int range, and an overflow must not bypass the total budget.
        long total = conclusion.Length + statusDetail.Length;

        if (result.Evidence is { Count: > 0 })
            foreach (var item in result.Evidence)
                if (item is not null)
                    total += (item.Path?.Length ?? 0) + (item.Note?.Length ?? 0);

        total += SumLengths(result.SuggestedChanges);
        total += SumLengths(result.TestSuggestions);
        total += SumLengths(result.Uncertainties);

        if (total > WorkerResultLimits.MaxTotalTextLength)
            errors.Add(
                       $"Field 'result' exceeds the maximum total of {WorkerResultLimits.MaxTotalTextLength} characters across conclusion, statusDetail, evidence and suggestion lists (actual: {total}).");
    }

    private static long SumLengths(IReadOnlyList<string>? entries)
    {
        if (entries is not { Count: > 0 }) return 0;

        long total                           = 0;
        foreach (var entry in entries) total += entry?.Length ?? 0;

        return total;
    }

    private static bool ContainsParentSegment(string path)
    {
        foreach (var segment in path.Split('/', '\\'))
            if (segment == "..")
                return true;

        return false;
    }
}
