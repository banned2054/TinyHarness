using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Models.Tools;

/// <summary>
///     <para>工具执行后作为 tool 消息返回模型的结果。</para>
///     <para>Outcome of a tool invocation returned to the model as a tool message.</para>
/// </summary>
public sealed record ToolResult
{
    /// <summary>
    ///     <para>执行是否在没有抛出异常的情况下完成。</para>
    ///     <para>Whether execution completed without throwing.</para>
    /// </summary>
    public bool Succeeded { get; init; }

    /// <summary>
    ///     <para>工具整体执行未完成；已确认的文件事实仍有效，但不能据此确认整个调用结果。</para>
    ///     <para>
    ///         The overall execution is incomplete; confirmed file facts remain valid without confirming the whole
    ///         invocation.
    ///     </para>
    /// </summary>
    public bool ExecutionIncomplete { get; init; }

    /// <summary>
    ///     <para>返回模型的文本，必要时按结果预算截断。</para>
    ///     <para>Text returned to the model, truncated to the result budget when necessary.</para>
    /// </summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>
    ///     <para>apply_patch 的已执行文件事实；null 表示没有进入写入阶段的变更事实。</para>
    ///     <para>Executed apply_patch file facts; null means no change reached the write phase.</para>
    /// </summary>
    public IReadOnlyList<PatchChange>? PatchChanges { get; init; }

    /// <summary>
    ///     <para>进程工具的退出码；未启动或超时时可以为空。</para>
    ///     <para>The process tool's exit code; may be null when the process never started or timed out.</para>
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    ///     <para>进程工具是否因其执行时限而终止。</para>
    ///     <para>Whether a process tool was terminated by its execution timeout.</para>
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    ///     <para>标准输出与标准错误的模型视图是否至少有一个被裁剪。</para>
    ///     <para>Whether at least one model-facing process stream was truncated.</para>
    /// </summary>
    public bool OutputTruncated { get; init; }

    /// <summary>
    ///     <para>返回模型的、有界标准输出视图。</para>
    ///     <para>Bounded stdout view returned to the model.</para>
    /// </summary>
    public string Stdout { get; init; } = string.Empty;

    /// <summary>
    ///     <para>返回模型的、有界标准错误视图。</para>
    ///     <para>Bounded stderr view returned to the model.</para>
    /// </summary>
    public string Stderr { get; init; } = string.Empty;

    /// <summary>
    ///     <para>标准输出被裁剪时保存完整（已脱敏）内容的本地临时 artifact。</para>
    ///     <para>
    ///         Local temporary artifact containing full redacted stdout when its model view was truncated.
    ///     </para>
    /// </summary>
    public string? StdoutArtifactPath { get; init; }

    /// <summary>
    ///     <para>标准错误被裁剪时保存完整（已脱敏）内容的本地临时 artifact。</para>
    ///     <para>
    ///         Local temporary artifact containing full redacted stderr when its model view was truncated.
    ///     </para>
    /// </summary>
    public string? StderrArtifactPath { get; init; }
}

/// <summary>
///     <para>
///         <see cref="ITool.Prepare" /> 生成的不可变、已完全校验计划。权限与执行都只消费此计划，
///         不再读取原始模型参数。
///     </para>
///     <para>
///         The immutable, fully validated plan produced by <see cref="ITool.Prepare" />.
///         Permissions and execution both operate on this, never on raw model arguments.
///     </para>
/// </summary>
public sealed class ToolPreparation
{
    private readonly JsonObject                      _arguments         = new();
    private readonly IReadOnlyList<InstructionScope> _instructionScopes = [];
    private readonly IReadOnlyList<string>           _targetPaths       = [];

    /// <summary>
    ///     <para>工具私有的不可变执行状态；审批后不可信 JSON 无法选择内部执行路径。</para>
    ///     <para>
    ///         Tool-private immutable execution state. Untrusted JSON cannot select an
    ///         internal execution path after the prepared invocation is approved.
    ///     </para>
    /// </summary>
    internal object? ExecutionPlan { get; init; }

    /// <summary>
    ///     <para>
    ///         一次工具轮预检生成的指令覆盖凭据；仅由 Agent Loop 通过
    ///         <see cref="WithInstructionCoverage" /> 绑定，工具执行期消费，不序列化。
    ///     </para>
    ///     <para>
    ///         Instruction coverage credential created by one tool-round preflight; bound only by
    ///         the Agent Loop through <see cref="WithInstructionCoverage" />, consumed at execution
    ///         time, and never serialized.
    ///     </para>
    /// </summary>
    internal InstructionCoverage? Coverage { get; init; }

    public required string ToolName { get; init; }

    public required string CallId { get; init; }

    /// <summary>
    ///     <para>
    ///         Prepare 校验并规范化后的参数副本（如路径解析为工作区内绝对路径）；读取与写入时均深拷贝，
    ///         避免审批后被修改。Execute 只消费该对象，执行期不再重新解释原始模型参数。
    ///     </para>
    ///     <para>
    ///         Validated arguments after Prepare-side normalization (e.g. paths resolved
    ///         to absolute, inside-workspace form); deep-copied on read and write so they
    ///         cannot change after approval. Execute consumes this object only; raw model
    ///         arguments are never re-interpreted at execution time.
    ///     </para>
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
    ///     <para>该调用所需的规范化能力名，例如 filesystem.list。</para>
    ///     <para>Normalized capability required by the invocation, for example filesystem.list.</para>
    /// </summary>
    public required string Capability { get; init; }

    /// <summary>
    ///     <para>审批和审计界面显示的一行摘要。</para>
    ///     <para>Human-readable one-line summary shown during approval and audit.</para>
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    ///     <para>向审批者展示的风险提示；权限结论仍由 Permission Engine 独立计算。</para>
    ///     <para>
    ///         Risk hint shown to the approver; the Permission Engine still computes the decision independently.
    ///     </para>
    /// </summary>
    public ToolRiskLevel RiskLevel { get; init; }

    /// <summary>
    ///     <para>
    ///         可选的会话约束。会话拒绝与它精确匹配（命令身份），使拒绝不因执行后端变化意外失效；
    ///         进程工具用它绑定 executable 与 arguments，避免一次规则覆盖任意命令。
    ///     </para>
    ///     <para>
    ///         Optional session constraint. Session denies match it exactly (command
    ///         identity) so a denial survives execution-backend changes; process tools
    ///         use it to bind executable and arguments so one rule never covers arbitrary commands.
    ///     </para>
    /// </summary>
    public string? SessionConstraint { get; init; }

    /// <summary>
    ///     <para>
    ///         可选的会话授权约束（allow session / allow once 使用）：在命令身份之上叠加执行策略身份，
    ///         使同一命令的授权不能跨后端、跨隔离策略或跨环境复用。缺省时回落到
    ///         <see cref="SessionConstraint" />，非进程工具行为不变。
    ///     </para>
    ///     <para>
    ///         Optional session grant constraint (used by allow session / allow once):
    ///         the command identity plus the execution policy identity, so an approval
    ///         for one command can never be reused across backends, isolation policies,
    ///         or environments. Absent means fall back to <see cref="SessionConstraint" />;
    ///         non-process tools keep their existing behavior.
    ///     </para>
    /// </summary>
    public string? SessionGrantConstraint { get; init; }

    /// <summary>
    ///     <para>
    ///         本次执行的策略身份（如 backend=host 或完整沙箱身份串），进入审计记录；
    ///         空表示工具未提供执行策略描述。它只是事实记录，不是授权。
    ///     </para>
    ///     <para>
    ///         The execution policy identity for this invocation (e.g. backend=host or
    ///         the full sandbox identity), recorded in the audit trail; empty when the
    ///         tool provides no execution policy. It is a factual record, not an authorization.
    ///     </para>
    /// </summary>
    public string? ExecutionPolicy { get; init; }

    /// <summary>
    ///     <para>本次调用将访问的规范化绝对路径；权限规则以此匹配范围，Agent Loop 与 CLI 用于展示。</para>
    ///     <para>
    ///         Normalized absolute resource paths this invocation will access. The
    ///         Permission Engine matches its rules against these scopes; the Agent Loop and
    ///         CLI use them for display.
    ///     </para>
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

    /// <summary>
    ///     <para>
    ///         Prepare 阶段声明的指令作用域：仅描述本次调用将访问的文件／目录／递归范围，
    ///         不读取规则，也不包含授权。
    ///     </para>
    ///     <para>
    ///         Instruction scopes declared during Prepare. They describe only the file, directory,
    ///         or recursive range this invocation will access; they read no rules and carry no authority.
    ///     </para>
    /// </summary>
    public IReadOnlyList<InstructionScope> InstructionScopes
    {
        get => _instructionScopes;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _instructionScopes = Array.AsReadOnly(value.ToArray());
        }
    }

    /// <summary>
    ///     <para>
    ///         生成绑定指令覆盖凭据的不可变派生计划；保留原参数、能力、摘要、约束、目标路径、
    ///         私有执行计划与已声明作用域，原对象保持不变。
    ///     </para>
    ///     <para>
    ///         Returns an immutable derivation bound to an instruction coverage credential while
    ///         preserving the original arguments, capability, summary, constraints, target paths,
    ///         private execution plan, and declared scopes; the source instance is left unchanged.
    ///     </para>
    /// </summary>
    public ToolPreparation WithInstructionCoverage(InstructionCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        return new ToolPreparation
        {
            ToolName               = ToolName,
            CallId                 = CallId,
            Arguments              = _arguments,
            Capability             = Capability,
            Summary                = Summary,
            RiskLevel              = RiskLevel,
            SessionConstraint      = SessionConstraint,
            SessionGrantConstraint = SessionGrantConstraint,
            ExecutionPolicy        = ExecutionPolicy,
            TargetPaths            = _targetPaths,
            InstructionScopes      = _instructionScopes,
            ExecutionPlan          = ExecutionPlan,
            Coverage               = coverage
        };
    }
}
