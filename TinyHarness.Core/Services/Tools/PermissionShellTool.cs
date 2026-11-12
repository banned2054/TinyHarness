using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Tools;

/// <summary>
///     <para>
///         在准备时按 executionScope 冻结沙箱或宿主 Shell：缺省与 "workspace" 走沙箱，
///         "host" 是显式的 Level 3 宿主执行申请。审批后不能通过参数切换执行后端；
///         权限模式不参与路由，审批由权限引擎与授权凭据决定。
///     </para>
///     <para>
///         Freezes a sandbox or host shell during preparation by executionScope alone: the
///         default and "workspace" route to the sandbox, and "host" is an explicit Level 3
///         host-execution request. Arguments cannot switch backends after approval; the
///         permission mode never participates in routing, and approval belongs to the
///         permission engine and its authorization credentials.
///     </para>
/// </summary>
public sealed class PermissionShellTool(ITool restricted, ITool host) : ITool
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
                    "Defaults to 'workspace', which runs inside the configured Windows sandbox; "                   +
                    "when the sandbox is unavailable the whole command fails instead of falling back to the host. " +
                    "'host' is an explicit Level 3 request to run the command directly as the TinyHarness process " +
                    "with no OS sandbox isolation; it is not an administrator elevation and grants nothing beyond " +
                    "the host process's own permissions, and it always requires a single-operation approval "       +
                    "bound to this exact call. "                                                                    +
                    "Use 'host' only to rerun a specific command that failed at a sandbox boundary "                +
                    "(for example writing to a protected path such as .git), "                                      +
                    "after confirming earlier steps left no uncertain side effects; never use it to bypass a refusal."
            };
            // 沙箱网络申请：全网访问而非窄授权；host 作用域无沙箱隔离，参数被忽略。
            schema["properties"]!["networkAccess"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("default", "enabled"),
                ["description"] =
                    "Requests network access for this invocation inside the workspace sandbox "                 +
                    "(full network access, including host-local proxies such as a loopback proxy "              +
                    "the user configured). Defaults to 'default', which keeps the sandbox network restricted. " +
                    "Only meaningful for the 'workspace' scope; 'host' ignores it because host execution "      +
                    "has no sandbox isolation. Each enabled request requires its own approval "                 +
                    "unless the user granted standing network trust."
            };
            // 沙箱写根申请：单次调用的工作区外写范围；host 作用域无沙箱边界，参数同样被忽略。
            schema["properties"]!["writeRoots"] = new JsonObject
            {
                ["type"]  = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] =
                    "Optional absolute directory roots outside the workspace this single workspace-sandbox "     +
                    "invocation needs to write (at most 8). Each request requires per-call approval at level 2 " +
                    "and the approved roots are frozen into the grant for this exact command. "                  +
                    "Only meaningful for the 'workspace' scope; 'host' ignores it because host execution "       +
                    "has no sandbox write-root boundary. Paths containing spaces or quotes are refused."
            };
            return restricted.Definition with { Parameters = schema };
        }
    }

    /// <summary>
    ///     <para>
    ///         把后端选择与完整命令一起绑定到不可变计划：workspace 路由必须携带冻结策略身份，
    ///         缺失即明确失败（沙箱不可用不回退宿主）；host 路由标记显式后端申请。host 路径不携带网络申请。
    ///     </para>
    ///     <para>
    ///         Binds backend selection and the complete command to an immutable plan: the workspace
    ///         route must carry a frozen policy identity and fails explicitly without one (no host
    ///         fallback when the sandbox is unavailable); the host route marks the explicit backend
    ///         request. The host path never carries a network request.
    ///     </para>
    /// </summary>
    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args  = ToolArgs.ParseObject(call);
        var scope = args["executionScope"]?.GetValue<string>() ?? "workspace";
        if (scope is not ("workspace" or "host")) throw new InvalidDataException("Unknown shell execution scope.");
        // 路由只由 executionScope 决定：权限模式不参与，宿主执行是显式 Level 3 申请。
        var isHost = scope == "host";
        var tool   = isHost ? host : restricted;
        return Bind(tool, tool.Prepare(call), isHost);
    }

    /// <summary>
    ///     <para>只初始化请求指定的后端；取消在冻结计划与审批之前传播。</para>
    ///     <para>Initializes only the requested backend, propagating cancellation before plan freezing and approval.</para>
    /// </summary>
    public async ValueTask<ToolPreparation> PrepareAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        var args  = ToolArgs.ParseObject(call);
        var scope = args["executionScope"]?.GetValue<string>() ?? "workspace";
        if (scope is not ("workspace" or "host")) throw new InvalidDataException("Unknown shell execution scope.");
        var isHost = scope == "host";
        var tool   = isHost ? host : restricted;
        var plan   = await tool.PrepareAsync(call, cancellationToken).ConfigureAwait(false);
        return Bind(tool, plan, isHost);
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

    private ToolPreparation Bind(ITool tool, ToolPreparation plan, bool isHost)
    {
        // workspace 路由的身份存在性结构校验：沙箱未组合时组合点提供无身份形态，这里明确失败；
        // 沙箱可用性判定属于权限引擎的组合期布尔，本检查不重复实现可用性判断。
        if (!isHost && string.IsNullOrWhiteSpace(plan.ExecutionPolicy))
            throw new
                InvalidDataException("The workspace shell has no frozen execution policy; the Windows sandbox " +
                                     "backend is not available and host fallback is refused.");
        var summary = isHost && plan.RequiresNetworkAccess
            ? plan.Summary.Replace(ShellTool.NetworkSummaryPrefix, string.Empty, StringComparison.Ordinal)
            : plan.Summary;
        return new ToolPreparation
        {
            ToolName               = plan.ToolName,
            CallId                 = plan.CallId,
            Arguments              = plan.Arguments,
            Capability             = plan.Capability,
            Summary                = summary,
            RiskLevel              = plan.RiskLevel,
            TargetPaths            = plan.TargetPaths,
            InstructionScopes      = plan.InstructionScopes,
            SessionConstraint      = plan.SessionConstraint,
            SessionGrantConstraint = plan.SessionGrantConstraint,
            ExecutionPolicy        = plan.ExecutionPolicy,
            // workspace 路由透传内层网络申请与写根申请；host 路由两者皆不带（无沙箱边界，参数无意义）。
            RequiresNetworkAccess  = isHost ? false : plan.RequiresNetworkAccess,
            RequestedWriteRoots    = isHost ? [] : plan.RequestedWriteRoots,
            // host 是显式 Level 3 后端申请；缺省 null 表示默认沙箱路由。
            RequestedBackend       = isHost ? ProcessExecutionBackendKind.Host : null,
            ExecutionPlan          = new Plan(this, tool, plan)
        };
    }

    // internal：派发前复核（FrozenCapabilityScopeReader）需穿透包装读取冻结的进程计划。
    internal sealed record Plan(PermissionShellTool Owner, ITool Tool, ToolPreparation Preparation);
}
