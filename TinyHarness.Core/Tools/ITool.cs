using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Core.Tools;

/// <summary>
/// 所有工具的统一契约。Prepare 只做无副作用的校验和规范化，Execute 是唯一允许产生副作用的阶段；
/// 两者之间由 Agent Loop 完成权限检查。
///
/// Contract implemented by each tool. The flow is Prepare (pure validation)
/// then Execute (the only place side effects are allowed). Authorization is
/// handled by the Agent Loop via a permission engine between the two calls.
/// </summary>
public interface ITool
{
    /// <summary>
    /// 发送给模型的工具名称、说明和 JSON Schema。
    /// The tool name, description, and JSON Schema declared to the model.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    /// 对一次调用做纯粹、无副作用的准备和规范化。
    /// Performs pure, side-effect-free preparation and normalization for one invocation.
    /// </summary>
    ToolPreparation Prepare(ChatToolCall call);

    /// <summary>
    /// 执行已经获批的准备计划，不再解释原始模型参数。
    /// Executes an authorized prepared plan without reinterpreting raw model arguments.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken);
}
