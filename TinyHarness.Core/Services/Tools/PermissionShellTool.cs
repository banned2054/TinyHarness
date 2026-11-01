using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>在准备时冻结受限或宿主 Shell；审批后不能通过参数切换执行后端。</para>
///     <para>Freezes a restricted or host shell during preparation so arguments cannot switch backends after approval.</para>
/// </summary>
public sealed class PermissionShellTool(ITool restricted, ITool host, Func<SessionPermissionMode?> mode) : ITool
{
    public ToolDefinition Definition
    {
        get
        {
            var schema = (JsonObject)restricted.Definition.Parameters.DeepClone();
            schema["properties"]!["executionScope"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("workspace", "host"),
                ["description"] =
                    "Defaults to workspace. Use host to request execution outside the configured sandbox; requires approval except in Full Access."
            };
            return restricted.Definition with { Parameters = schema };
        }
    }

    /// <summary>
    ///     <para>把后端选择与完整命令一起绑定到不可变计划。</para>
    ///     <para>Binds backend selection and the complete command to an immutable plan.</para>
    /// </summary>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args  = ToolArgs.ParseObject(call);
        var scope = args["executionScope"]?.GetValue<string>() ?? "workspace";
        if (scope is not ("workspace" or "host")) throw new InvalidDataException("Unknown shell execution scope.");
        var tool = mode() == SessionPermissionMode.FullAccess || scope == "host" ? host : restricted;
        var plan = tool.Prepare(call);
        return new ToolPreparation
        {
            ToolName          = plan.ToolName, CallId = plan.CallId, Arguments = plan.Arguments,
            Capability        = plan.Capability, Summary = plan.Summary, RiskLevel = plan.RiskLevel,
            TargetPaths       = plan.TargetPaths, InstructionScopes = plan.InstructionScopes,
            SessionConstraint = plan.SessionConstraint, SessionGrantConstraint = plan.SessionGrantConstraint,
            ExecutionPolicy   = plan.ExecutionPolicy, ExecutionPlan = new Plan(this, tool, plan)
        };
    }

    /// <summary>
    ///     <para>只执行本工具准备的后端及计划，并传递规则覆盖凭据。</para>
    ///     <para>Executes only this tool's prepared backend and plan, forwarding instruction coverage.</para>
    /// </summary>
    public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        if (preparation.ExecutionPlan is not Plan plan || plan.Owner != this)
            throw new InvalidDataException("Missing prepared shell backend.");
        return plan.Tool.ExecuteAsync(preparation.Coverage is { } coverage
                                          ? plan.Preparation.WithInstructionCoverage(coverage)
                                          : plan.Preparation, cancellationToken);
    }

    private sealed record Plan(PermissionShellTool Owner, ITool Tool, ToolPreparation Preparation);
}
