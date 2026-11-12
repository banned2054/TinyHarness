using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         所有工具的统一契约。Prepare 只做纯校验，PrepareAsync 可延迟准备运行基础设施；
///         模型请求的操作仅在 Execute 阶段执行，准备与执行之间由 Agent Loop 完成权限检查。
///     </para>
///     <para>
///         Common tool contract: Prepare is pure validation and PrepareAsync may initialize runtime infrastructure.
///         The requested operation runs only in Execute, after the Agent Loop authorizes its prepared plan.
///     </para>
/// </summary>
public interface ITool
{
    /// <summary>
    ///     发送给模型的工具名称、说明和 JSON Schema。
    ///     The tool name, description, and JSON Schema declared to the model.
    /// </summary>
    ToolDefinition Definition { get; }

    /// <summary>
    ///     对一次调用做纯粹、无副作用的准备和规范化。
    ///     Performs pure, side-effect-free preparation and normalization for one invocation.
    /// </summary>
    ToolPreparation Prepare(ChatToolCall call);

    /// <summary>
    ///     <para>可取消地准备工具；需要延迟初始化的实现应在冻结执行计划前完成初始化。</para>
    ///     <para>Prepares a tool with cancellation; deferred initialization must finish before its execution plan is frozen.</para>
    /// </summary>
    ValueTask<ToolPreparation> PrepareAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Prepare(call));
    }

    /// <summary>
    ///     执行已经获批的准备计划，不再解释原始模型参数。
    ///     Executes an authorized prepared plan without reinterpreting raw model arguments.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken);
}
