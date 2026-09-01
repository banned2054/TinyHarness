using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Core.Tools;

/// <summary>
/// Contract implemented by each tool. The flow is Prepare (pure validation)
/// then Execute (the only place side effects are allowed). Authorization is
/// handled by the Agent Loop via a permission engine between the two calls.
/// </summary>
public interface ITool
{
    string Name { get; }

    string Description { get; }

    /// <summary>Pure, side-effect-free preparation of a single invocation.</summary>
    ToolPreparation Prepare(ChatToolCall call);

    /// <summary>Executes a prepared plan. Called only after authorization.</summary>
    Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken);
}
