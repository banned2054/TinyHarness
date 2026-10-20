using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>保存宿主决定的工作区访问限制；切换使之前准备的工具计划失效。</para>
///     <para>Holds host-controlled workspace restrictions; transitions invalidate previously prepared tools.</para>
/// </summary>
public sealed class WorkspaceAccessState(WorkspaceDirectoryIdentity identity)
{
    public WorkspaceDirectoryIdentity Identity { get; } = identity;

    public bool IsReadOnly { get; private set; } = true;
    public long Revision   { get; private set; }

    /// <summary>
    ///     <para>在宿主工具边界切换限制，每次切换均废弃旧准备计划。</para>
    ///     <para>Changes restrictions at a host tool boundary, invalidating previous preparations.</para>
    /// </summary>
    public void SetReadOnly(bool readOnly)
    {
        IsReadOnly = readOnly;
        Revision++;
    }

    /// <summary>
    ///     <para>包装工具，使权限状态、目录身份与实际执行保持一致。</para>
    ///     <para>Wraps a tool to bind permissions, directory identity and actual execution.</para>
    /// </summary>
    public ITool Guard(Func<ITool> source) => new GuardedTool(this, source);

    private sealed class GuardedTool(WorkspaceAccessState state, Func<ITool> source) : ITool
    {
        public ToolDefinition Definition => source().Definition;

        public ToolPreparation Prepare(ChatToolCall call)
        {
            var tool   = source();
            var plan   = tool.Prepare(call);
            var policy = $"{plan.ExecutionPolicy}\nworkspace-access={state.Revision};read-only={state.IsReadOnly}";
            return new ToolPreparation
            {
                ToolName               = plan.ToolName,
                CallId                 = plan.CallId,
                Arguments              = plan.Arguments,
                Capability             = plan.Capability,
                Summary                = plan.Summary,
                RiskLevel              = plan.RiskLevel,
                SessionConstraint      = plan.SessionConstraint,
                SessionGrantConstraint = $"{plan.SessionGrantConstraint ?? plan.SessionConstraint}\n{policy}",
                ExecutionPolicy        = policy,
                TargetPaths            = plan.TargetPaths,
                InstructionScopes      = plan.InstructionScopes,
                ExecutionPlan          = new GuardedPlan(this, tool, plan, state.Revision)
            };
        }

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preparation.ExecutionPlan is not GuardedPlan plan || plan.Owner != this ||
                plan.Revision                                                   != state.Revision)
                throw new InvalidOperationException("Workspace access changed; prepare and authorize the tool again.");
            if (state.IsReadOnly && !ToolCapabilities.IsReadOnly(plan.Preparation.Capability))
                throw new
                    InvalidOperationException("This workspace is read-only. Use the host /trust command to change access.");
            state.Identity.EnsureUnchanged();
            var inner = preparation.Coverage is { } coverage
                ? plan.Preparation.WithInstructionCoverage(coverage)
                : plan.Preparation;
            return plan.Tool.ExecuteAsync(inner, cancellationToken);
        }

        private sealed record GuardedPlan(GuardedTool Owner, ITool Tool, ToolPreparation Preparation, long Revision);
    }
}
