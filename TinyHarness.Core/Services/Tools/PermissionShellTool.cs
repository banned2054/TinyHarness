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
                    "Defaults to 'workspace', which runs inside the configured sandbox when one is enabled. " +
                    "'host' runs the command directly as the TinyHarness process with no OS sandbox isolation; " +
                    "it is not an administrator elevation and grants nothing beyond the host process's own permissions. " +
                    "In every mode except Full Access a host call requires approval, " +
                    "and in AI review modes it is approved only when it matches the user's explicit authorization. " +
                    "Use 'host' only to rerun a specific command that failed at a sandbox boundary " +
                    "(for example writing to a protected path such as .git), " +
                    "after confirming earlier steps left no uncertain side effects; never use it to bypass a refusal."
            };
            // 沙箱网络申请：全网访问而非窄授权；host 作用域无沙箱隔离，参数被忽略。
            schema["properties"]!["networkAccess"] = new JsonObject
            {
                ["type"]        = "string",
                ["enum"]        = new JsonArray("default", "enabled"),
                ["description"] =
                    "Requests network access for this invocation inside the workspace sandbox " +
                    "(full network access, including host-local proxies such as a loopback proxy " +
                    "the user configured). Defaults to 'default', which keeps the sandbox network restricted. " +
                    "Only meaningful for the 'workspace' scope; 'host' ignores it because host execution " +
                    "has no sandbox isolation. Each enabled request requires its own approval " +
                    "unless the user granted standing network trust."
            };
            return restricted.Definition with { Parameters = schema };
        }
    }

    /// <summary>
    ///     <para>把后端选择与完整命令一起绑定到不可变计划；host 路径不携带网络申请。</para>
    ///     <para>
    ///         Binds backend selection and the complete command to an immutable plan;
    ///         the host path never carries a network request.
    ///     </para>
    /// </summary>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args  = ToolArgs.ParseObject(call);
        var scope = args["executionScope"]?.GetValue<string>() ?? "workspace";
        if (scope is not ("workspace" or "host")) throw new InvalidDataException("Unknown shell execution scope.");
        // FullAccess 与 scope=host 都走宿主工具：宿主执行没有沙箱，网络参数不产生任何语义。
        var isHost   = mode() == SessionPermissionMode.FullAccess || scope == "host";
        var tool     = isHost ? host : restricted;
        var plan     = tool.Prepare(call);
        var summary  = isHost && plan.RequiresNetworkAccess
            ? plan.Summary.Replace(ShellTool.NetworkSummaryPrefix, string.Empty, StringComparison.Ordinal)
            : plan.Summary;
        return new ToolPreparation
        {
            ToolName          = plan.ToolName, CallId = plan.CallId, Arguments = plan.Arguments,
            Capability        = plan.Capability, Summary = summary, RiskLevel = plan.RiskLevel,
            TargetPaths       = plan.TargetPaths, InstructionScopes = plan.InstructionScopes,
            SessionConstraint = plan.SessionConstraint, SessionGrantConstraint = plan.SessionGrantConstraint,
            ExecutionPolicy   = plan.ExecutionPolicy,
            // workspace 路径透传内层网络申请；host 路径恒 false，不受网络参数影响。
            RequiresNetworkAccess = isHost ? false : plan.RequiresNetworkAccess,
            ExecutionPlan         = new Plan(this, tool, plan)
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
