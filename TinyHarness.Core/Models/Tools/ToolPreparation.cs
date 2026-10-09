using System.Text.Json.Nodes;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Models.Tools;

/// <summary>
/// 工具执行后作为 tool 消息返回模型的结果。
///
/// Outcome of a tool invocation returned to the model as a tool message.
/// </summary>
public sealed record ToolResult
{
    /// <summary>
    /// 执行是否在没有抛出异常的情况下完成。
    /// Whether execution completed without throwing.
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    /// 返回模型的文本，必要时按结果预算截断。
    /// Text returned to the model, truncated to the result budget when necessary.
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    /// 进程工具的退出码；未启动或超时时可以为空。
    /// Process exit code, when the tool represents a process that exited normally or was terminated.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// 进程工具是否因其执行时限而终止。
    /// Whether a process tool was terminated by its execution timeout.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// 标准输出与标准错误的模型视图是否至少有一个被裁剪。
    /// Whether at least one model-facing process stream was truncated.
    /// </summary>
    public bool OutputTruncated { get; init; }

    /// <summary>
    /// 返回模型的、有界标准输出视图。
    /// Bounded stdout view returned to the model.
    /// </summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>
    /// 返回模型的、有界标准错误视图。
    /// Bounded stderr view returned to the model.
    /// </summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>
    /// 标准输出被裁剪时保存完整（已脱敏）内容的本地临时 artifact。
    /// Local temporary artifact containing full redacted stdout when its model view was truncated.
    /// </summary>
    public string? StdoutArtifactPath { get; init; }

    /// <summary>
    /// 标准错误被裁剪时保存完整（已脱敏）内容的本地临时 artifact。
    /// Local temporary artifact containing full redacted stderr when its model view was truncated.
    /// </summary>
    public string? StderrArtifactPath { get; init; }
}

/// <summary>
/// <see cref="ITool.Prepare"/> 生成的不可变、已完全校验计划。权限与执行都只消费此计划，
/// 不再读取原始模型参数。
///
/// The immutable, fully validated plan produced by <see cref="ITool.Prepare"/>.
/// Permissions and execution both operate on this, never on raw model arguments.
/// </summary>
public sealed class ToolPreparation
{
    private JsonObject _arguments = new();
    private IReadOnlyList<string> _targetPaths = [];

    /// <summary>
    /// Tool-private immutable execution state. Untrusted JSON cannot select an
    /// internal execution path after the prepared invocation is approved.
    /// </summary>
    internal object? ExecutionPlan { get; init; }

    public required string ToolName { get; init; }

    public required string CallId { get; init; }

    /// <summary>
    /// Prepare 校验并规范化后的参数副本；读取与写入时均深拷贝，避免审批后被修改。
    ///
    /// Validated arguments after Prepare-side normalization (e.g. paths resolved
    /// to absolute, inside-workspace form). Execute consumes this object only;
    /// raw model arguments are never re-interpreted at execution time.
    /// </summary>
    public required JsonObject Arguments
    {
        get => (JsonObject)_arguments.DeepClone();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _arguments = (JsonObject)value.DeepClone();
        }
    }

    /// <summary>
    /// 该调用所需的规范化能力名，例如 filesystem.list。
    /// Normalized capability required by the invocation, for example filesystem.list.
    /// </summary>
    public required string Capability { get; init; }

    /// <summary>
    /// 审批和审计界面显示的一行摘要。
    /// Human-readable one-line summary shown during approval and audit.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// 向审批者展示的风险提示；权限结论仍由 Permission Engine 独立计算。
    /// Risk hint shown to the approver; the Permission Engine still computes the decision independently.
    /// </summary>
    public ToolRiskLevel RiskLevel { get; init; }

    /// <summary>
    /// 可选的会话约束。会话拒绝与它精确匹配（命令身份），使拒绝不因执行后端变化意外失效；
    /// 进程工具用它绑定 executable 与 arguments，避免一次规则覆盖任意命令。
    ///
    /// Optional session constraint. Session denies match it exactly (command
    /// identity) so a denial survives execution-backend changes; process tools
    /// use it to bind executable and arguments so one rule never covers arbitrary commands.
    /// </summary>
    public string? SessionConstraint { get; init; }

    /// <summary>
    /// 可选的会话授权约束（allow session / allow once 使用）：在命令身份之上叠加执行策略身份，
    /// 使同一命令的授权不能跨后端、跨隔离策略或跨环境复用。缺省时回落到
    /// <see cref="SessionConstraint"/>，非进程工具行为不变。
    ///
    /// Optional session grant constraint (used by allow session / allow once):
    /// the command identity plus the execution policy identity, so an approval
    /// for one command can never be reused across backends, isolation policies,
    /// or environments. Absent means fall back to <see cref="SessionConstraint"/>;
    /// non-process tools keep their existing behavior.
    /// </summary>
    public string? SessionGrantConstraint { get; init; }

    /// <summary>
    /// 本次执行的策略身份（如 backend=host 或完整沙箱身份串），进入审计记录；
    /// 空表示工具未提供执行策略描述。它只是事实记录，不是授权。
    ///
    /// The execution policy identity for this invocation (e.g. backend=host or
    /// the full sandbox identity), recorded in the audit trail; empty when the
    /// tool provides no execution policy. It is a factual record, not an authorization.
    /// </summary>
    public string? ExecutionPolicy { get; init; }

    /// <summary>
    /// 本次调用将访问的规范化绝对路径；权限规则以此匹配范围，Agent Loop 与 CLI 用于展示。
    ///
    /// Normalized absolute resource paths this invocation will access. The
    /// The Permission Engine matches its rules against these scopes; the Agent
    /// Loop and CLI use them for display.
    /// </summary>
    public IReadOnlyList<string> TargetPaths
    {
        get => _targetPaths;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _targetPaths = Array.AsReadOnly(value.ToArray());
        }
    }
}
