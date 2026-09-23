namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     worker 请求各字段的硬上限。限制值是保守的任务包大小，由 Core 校验逻辑强制执行；
///     MCP schema、specialist 指令或调用方自律都不是边界。
///     Hard per-field limits for worker requests. The values are conservative task-package sizes enforced
///     by Core validation logic; the MCP schema, specialist instructions or caller discipline are not the
///     boundary.
/// </summary>
public static class WorkerRequestLimits
{
    /// <summary>
    ///     <c>task</c> 最大字符数，对应一次具体小任务的描述量级。
    ///     Maximum length of "task" in characters, sized for one concrete small task.
    /// </summary>
    public const int MaxTaskPromptLength = 4_000;

    /// <summary>
    ///     <c>knownFacts</c> 最大条数。 Maximum number of knownFacts entries.
    /// </summary>
    public const int MaxKnownFactCount = 16;

    /// <summary>
    ///     每条 <c>knownFacts</c> 的最大字符数。 Maximum characters per knownFacts entry.
    /// </summary>
    public const int MaxKnownFactLength = 500;

    /// <summary>
    ///     <c>focusPaths</c> 最大条数。 Maximum number of focusPaths entries.
    /// </summary>
    public const int MaxFocusPathCount = 16;

    /// <summary>
    ///     每条 <c>focusPaths</c> 的最大字符数。 Maximum characters per focusPaths entry.
    /// </summary>
    public const int MaxFocusPathLength = 256;

    /// <summary>
    ///     <c>expectedOutput</c> 最大字符数。 Maximum length of expectedOutput in characters.
    /// </summary>
    public const int MaxExpectedOutputLength = 1_000;
}
