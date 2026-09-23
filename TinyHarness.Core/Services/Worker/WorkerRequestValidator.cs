using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     在 Core 内强制实施 worker 请求的硬边界：必填字段、长度与数量上限、focusPaths 必须是
///     工作区相对的提示路径。这里的校验是权威边界，不依赖 MCP schema 或 specialist 指令；
///     文件系统层面的访问判断仍由现有 Workspace 与只读工具边界执行，本层不做路径解析或 I/O。
///     Enforces the hard boundaries for worker requests inside Core: required fields, length and count
///     limits, and workspace-relative focusPaths. This validation is the authoritative boundary — not the
///     MCP schema or specialist instructions. Filesystem access decisions stay with the existing Workspace
///     and read-only tool boundaries; this layer does no path resolution or I/O.
/// </summary>
public static class WorkerRequestValidator
{
    public static WorkerRequestValidationResult Validate(WorkerRequest? request)
    {
        var errors = new List<string>();

        if (request is null)
        {
            errors.Add("Field 'request' is required; the payload must describe a worker task.");
            return new WorkerRequestValidationResult { Errors = errors };
        }

        ValidateTaskPrompt(request.TaskPrompt, errors);
        ValidateKnownFacts(request.KnownFacts, errors);
        ValidateFocusPaths(request.FocusPaths, errors);
        ValidateExpectedOutput(request.ExpectedOutput, errors);

        return new WorkerRequestValidationResult { Errors = errors };
    }

    private static void ValidateTaskPrompt(string? taskPrompt, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(taskPrompt))
        {
            errors.Add("Field 'task' is required and must contain non-whitespace characters.");
            return;
        }

        if (taskPrompt.Length > WorkerRequestLimits.MaxTaskPromptLength)
            errors.Add($"Field 'task' exceeds the maximum length of {WorkerRequestLimits.MaxTaskPromptLength} characters (actual: {taskPrompt.Length}).");
    }

    private static void ValidateKnownFacts(IReadOnlyList<string>? knownFacts, List<string> errors)
    {
        if (knownFacts is not { Count: > 0 }) return;

        if (knownFacts.Count > WorkerRequestLimits.MaxKnownFactCount)
            errors.Add($"Field 'knownFacts' contains {knownFacts.Count} entries; the maximum is {WorkerRequestLimits.MaxKnownFactCount}.");

        for (var i = 0; i < knownFacts.Count; i++)
        {
            var fact = knownFacts[i];

            if (string.IsNullOrWhiteSpace(fact))
            {
                errors.Add($"Field 'knownFacts[{i}]' is required and must contain non-whitespace characters.");
                continue;
            }

            if (fact.Length > WorkerRequestLimits.MaxKnownFactLength)
                errors.Add($"Field 'knownFacts[{i}]' exceeds the maximum length of {WorkerRequestLimits.MaxKnownFactLength} characters (actual: {fact.Length}).");
        }
    }

    private static void ValidateFocusPaths(IReadOnlyList<string>? focusPaths, List<string> errors)
    {
        if (focusPaths is not { Count: > 0 }) return;

        if (focusPaths.Count > WorkerRequestLimits.MaxFocusPathCount)
            errors.Add(
                       $"Field 'focusPaths' contains {focusPaths.Count} entries; the maximum is {WorkerRequestLimits.MaxFocusPathCount}.");

        for (var i = 0; i < focusPaths.Count; i++) ValidateFocusPath(focusPaths[i], i, errors);
    }

    private static void ValidateFocusPath(string? focusPath, int index, List<string> errors)
    {
        var field = $"Field 'focusPaths[{index}]'";

        if (string.IsNullOrWhiteSpace(focusPath))
        {
            errors.Add($"{field} is required and must contain non-whitespace characters.");
            return;
        }

        if (focusPath.Length > WorkerRequestLimits.MaxFocusPathLength)
        {
            errors.Add($"{field} exceeds the maximum length of {WorkerRequestLimits.MaxFocusPathLength} characters (actual: {focusPath.Length}).");
            return;
        }

        if (focusPath.Any(char.IsControl))
        {
            errors.Add($"{field} must not contain control characters.");
            return;
        }

        // focusPaths 只允许工作区相对路径提示：跨平台拒绝盘符、盘符相对、UNC 与任一风格的
        // 根化路径，绝不允许 ".." 段——这里的检查只是请求卫生，真正的访问边界在 Workspace/只读工具。
        // Only workspace-relative hints are accepted: drive-qualified, drive-relative, UNC and rooted
        // paths are rejected on every platform, and never a ".." segment. This is request hygiene only;
        // the real access boundary is Workspace/read-only tools.
        if (!WorkspaceRelativePath.IsAcceptableShape(focusPath))
        {
            errors.Add(
                       $"{field} ('{focusPath}') must be a workspace-relative hint; rooted, absolute, drive-qualified or UNC paths are not accepted.");
            return;
        }

        if (ContainsParentSegment(focusPath))
            errors.Add($"{field} ('{focusPath}') must not contain '..' path segments.");
    }

    private static bool ContainsParentSegment(string focusPath)
    {
        foreach (var segment in focusPath.Split('/', '\\'))
            if (segment == "..")
                return true;

        return false;
    }

    private static void ValidateExpectedOutput(string? expectedOutput, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(expectedOutput)) return;

        if (expectedOutput.Length > WorkerRequestLimits.MaxExpectedOutputLength)
            errors.Add($"Field 'expectedOutput' exceeds the maximum length of {WorkerRequestLimits.MaxExpectedOutputLength} characters (actual: {expectedOutput.Length}).");
    }
}
