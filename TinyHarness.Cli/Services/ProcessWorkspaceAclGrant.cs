using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Runtime;
using CoreGrant = TinyHarness.Core.Services.Runtime.ProcessWorkspaceAclGrant;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>目录授权的已确认结果或仍未知的终态。</para>
///     <para>A confirmed directory-grant outcome or an indeterminate end state.</para>
/// </summary>
internal enum WorkspaceGrantOutcome
{
    Completed,
    Declined,
    Failed,
    DidNotFinish
}

/// <summary>
///     <para>可替换的目录权限检查与显式授权边界。</para>
///     <para>Replaceable directory permission checks and explicit authorization boundary.</para>
/// </summary>
internal interface IWorkspaceAclGrant
{
    /// <summary>
    ///     <para>只读检查既有沙箱权限或普通 helper 的 ACL 准备能力，包含子项继承状态。</para>
    ///     <para>Checks existing sandbox permissions or the normal helper's preparation access, including child inheritance.</para>
    /// </summary>
    Task<bool> CanChangePermissionsAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>宿主确认精确范围后，以普通权限准备当前工作区；不启动组件或提权。</para>
    ///     <para>Prepares the current workspace at normal privilege after host scope consent, without components or elevation.</para>
    /// </summary>
    Task PrepareWithoutElevationAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>严格只读复核沙箱根授权及子项继承；管理能力不代表已就绪。</para>
    ///     <para>Strictly reads sandbox root grants and child inheritance; management access does not establish readiness.</para>
    /// </summary>
    Task<bool> IsReadyAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>只读校验授权范围及宿主程序位置；不启动子进程、不修改 ACL。</para>
    ///     <para>Checks grant scope and executable placement without starting a child or changing ACLs.</para>
    /// </summary>
    void ValidatePreparationScope(WorkspaceDirectoryIdentity identity);

    /// <summary>
    ///     <para>仅在宿主确认之后申请授权；取消后结果可能仍未知。</para>
    ///     <para>Requests a grant only after host consent; cancellation may leave its result unknown.</para>
    /// </summary>
    Task<WorkspaceGrantOutcome> GrantAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);
}

/// <summary>
///     <para>只为已确认目录启动专用 UAC 授权子进程；不运行 provisioning 或项目命令。</para>
///     <para>Starts a dedicated UAC grant child for a confirmed directory without provisioning or project commands.</para>
/// </summary>
internal sealed class ProcessWorkspaceAclGrant(
    IReadOnlyList<string>   protectedPaths,
    IReadOnlyList<string>   writeRoots,
    WindowsSandboxSettings? sandboxSettings  = null,
    string?                 appBaseDirectory = null) : IWorkspaceAclGrant
{
    internal const   string    HelperArgument = CoreGrant.HelperArgument;
    private readonly CoreGrant _grant         = new(protectedPaths, writeRoots, sandboxSettings, appBaseDirectory);

    /// <inheritdoc />
    public Task<bool> CanChangePermissionsAsync(WorkspaceDirectoryIdentity identity,
                                                CancellationToken          cancellationToken) =>
        _grant.CanChangePermissionsAsync(identity, cancellationToken);

    /// <inheritdoc />
    public Task PrepareWithoutElevationAsync(WorkspaceDirectoryIdentity identity,
                                             CancellationToken          cancellationToken) =>
        _grant.PrepareWithoutElevationAsync(identity, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsReadyAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        _grant.IsReadyAsync(identity, cancellationToken);

    /// <inheritdoc />
    public void ValidatePreparationScope(WorkspaceDirectoryIdentity identity) =>
        _grant.ValidatePreparationScope(identity);

    /// <inheritdoc />
    public async Task<WorkspaceGrantOutcome> GrantAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) =>
        (WorkspaceGrantOutcome)await _grant.GrantAsync(identity, cancellationToken).ConfigureAwait(false);

    /// <summary>
    ///     <para>调度共享受限 helper，不读取 CLI 配置或执行命令。</para>
    ///     <para>Dispatches the shared restricted helper without CLI configuration or commands.</para>
    /// </summary>
    internal static Task<int> ExecuteHelperAsync(string pipeName) => CoreGrant.ExecuteHelperAsync(pipeName);

    internal static void ValidateProtectedPaths(WorkspaceDirectoryIdentity identity, IReadOnlyList<string> paths) =>
        CoreGrant.ValidateProtectedPaths(identity, paths);
}
