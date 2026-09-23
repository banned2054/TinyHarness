namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一次性只读 worker 的任务请求。请求只描述要调查的内容，是任务资料而不是授权：
///     模型、endpoint、工作区根目录、权限模式、命令规则和 token/工具/超时预算等可信运行设置
///     全部由 TinyHarness 侧的可信配置决定，本契约没有任何字段可以携带或改写它们。
///     The task request for the one-shot read-only worker. It describes what to investigate and carries no
///     authority: trusted run settings such as model, endpoint, workspace root, permission mode, command
///     rules and token/tool/timeout budgets are owned by trusted TinyHarness-side configuration; this
///     contract has no field that can carry or override them.
/// </summary>
public sealed record WorkerRequest
{
    /// <summary>
    ///     必填的具体任务描述（契约字段名 <c>task</c>）。空白或超长值由 <c>WorkerRequestValidator</c> 拒绝。
    ///     The required concrete task (contract field name "task"). Blank or over-long values are rejected
    ///     by <c>WorkerRequestValidator</c>.
    /// </summary>
    public required string TaskPrompt { get; init; }

    /// <summary>
    ///     可选的调用方已知事实，只是任务资料；与工具实际查到的证据冲突时不构成更高优先级的事实。
    ///     Optional caller-supplied known facts, task material only; they never outrank contradicting
    ///     evidence found through the tools.
    /// </summary>
    public IReadOnlyList<string> KnownFacts { get; init; } = [];

    /// <summary>
    ///     可选的收窄与搜索提示（工作区相对路径形式）。它们只影响搜索优先级，不构成读取授权，
    ///     不能扩大工作区、敏感文件排除或权限；实际文件访问继续走现有 Workspace 与只读工具边界。
    ///     Optional narrowing and search hints as workspace-relative paths. They only steer search priority
    ///     and never widen the workspace, sensitive-file exclusions or permissions; actual file access keeps
    ///     using the existing Workspace and read-only tool boundaries.
    /// </summary>
    public IReadOnlyList<string> FocusPaths { get; init; } = [];

    /// <summary>
    ///     可选的期望输出说明，只影响最终答复的组织方式。
    ///     Optional description of the expected output; it only steers how the final answer is organized.
    /// </summary>
    public string ExpectedOutput { get; init; } = string.Empty;
}
