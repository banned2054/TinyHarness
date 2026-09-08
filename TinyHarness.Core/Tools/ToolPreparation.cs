using System.Text.Json.Nodes;

namespace TinyHarness.Core.Tools;

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
    private JsonObject            _arguments   = new();
    private IReadOnlyList<string> _targetPaths = [];

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
