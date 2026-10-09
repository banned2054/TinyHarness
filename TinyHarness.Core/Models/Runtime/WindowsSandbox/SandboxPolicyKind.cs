namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     首期固定的两种隔离策略：ReadOnly（工作区不可写）与 WorkspaceWrite（工作区与临时目录
///     可写，元数据目录保持只读）。两者都断网。
///     The two fixed first-phase isolation policies: ReadOnly (the workspace is
///     not writable) and WorkspaceWrite (workspace and temp directories are
///     writable, metadata directories stay read-only). Both are offline.
/// </summary>
public enum SandboxPolicyKind
{
    ReadOnly,
    WorkspaceWrite
}
