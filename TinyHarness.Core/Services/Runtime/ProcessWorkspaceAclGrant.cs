using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>目录授权的已确认结果或仍未知的终态。</para>
///     <para>A confirmed directory-grant outcome or an indeterminate end state.</para>
/// </summary>
public enum WorkspaceGrantOutcome
{
    Completed,
    Declined,
    Failed,
    DidNotFinish
}

/// <summary>
///     <para>授权已确认失败，且没有仍在运行的授权子进程；失败可能已产生部分 ACL 变更。</para>
///     <para>A confirmed grant failure with no running grant child; partial ACL changes may have occurred.</para>
/// </summary>
public sealed class WorkspaceGrantFailureException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
///     <para>可替换的目录权限检查与显式授权边界。</para>
///     <para>Replaceable directory permission checks and explicit authorization boundary.</para>
/// </summary>
public interface IWorkspaceAclGrant
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
    ///     <para>仅在宿主确认之后申请授权；取消后结果可能仍未知。</para>
    ///     <para>Requests a grant only after host consent; cancellation may leave its result unknown.</para>
    /// </summary>
    Task<WorkspaceGrantOutcome> GrantAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);
}

/// <summary>
///     <para>只为已确认目录启动专用 UAC 授权子进程；不运行 provisioning 或项目命令。</para>
///     <para>Starts a dedicated UAC grant child for a confirmed directory without provisioning or project commands.</para>
/// </summary>
public sealed partial class ProcessWorkspaceAclGrant(
    IReadOnlyList<string>   protectedPaths,
    IReadOnlyList<string>   writeRoots,
    WindowsSandboxSettings? sandboxSettings = null) : IWorkspaceAclGrant
{
    public const string HelperArgument = "--internal-workspace-acl";

    /// <inheritdoc />
    public async Task<bool> CanChangePermissionsAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return true;
        var consistent =
            await WorkspaceAclInheritance.FindMissingAsync(identity, cancellationToken).ConfigureAwait(false) is null;
        if (consistent && sandboxSettings is not null &&
            await WorkspaceSandboxAclContext.HasPreparedRootAsync(sandboxSettings, identity, cancellationToken)
                                            .ConfigureAwait(false))
            return true;
        return WorkspaceDirectoryAcl.CanChangePermissions(identity) &&
               await WorkspaceAclInheritance.CanManageAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> IsReadyAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        identity.EnsureUnchanged();
        if (!OperatingSystem.IsWindows() || sandboxSettings is null) return false;
        using var pinned = WorkspaceDirectoryAcl.Pin(identity);
        return await WorkspaceSandboxAclContext.HasPreparedRootAsync(sandboxSettings, identity, cancellationToken)
                                               .ConfigureAwait(false) &&
               await WorkspaceAclInheritance.FindMissingAsync(identity, cancellationToken)
                                            .ConfigureAwait(false) is null;
    }

    /// <inheritdoc />
    public async Task PrepareWithoutElevationAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows ACL preparation is required.");
        ValidatePreparationScope(identity);
        using var pinned = WorkspaceDirectoryAcl.Pin(identity);
        if (await IsReadyAsync(identity, cancellationToken).ConfigureAwait(false)) return;
        var settings = sandboxSettings ??
                       throw new InvalidOperationException("Workspace preparation requires trusted sandbox settings.");
        await WorkspaceAclInheritance.ValidateRepairTreeAsync(identity, cancellationToken).ConfigureAwait(false);
        if (!WorkspaceDirectoryAcl.CanChangePermissions(identity) ||
            !await WorkspaceAclInheritance.CanManageAsync(identity, cancellationToken).ConfigureAwait(false))
            throw new
                UnauthorizedAccessException("Normal-privilege workspace ACL preparation requires READ_CONTROL | WRITE_DAC.");
        var sandbox = await WorkspaceSandboxAclContext.CaptureAsync(settings, identity, cancellationToken)
                                                      .ConfigureAwait(false);
        await sandbox.ValidateAsync(identity, cancellationToken).ConfigureAwait(false);
        await WorkspaceSandboxAclRepair.RunAsync(
                                                 () => WorkspaceDirectoryAcl.GrantSandboxWritePermissions(identity,
                                                          [sandbox.SandboxGroupSid, sandbox.WorkspaceCapabilitySid]),
                                                 () => WorkspaceDirectoryAcl.ReapplyInheritance(identity),
                                                 token => WorkspaceAclInheritance.FindMissingAsync(identity, token),
                                                 cancellationToken).ConfigureAwait(false);
        if (!await IsReadyAsync(identity, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Workspace ACL preparation did not establish sandbox readiness.");
    }

    /// <inheritdoc />
    public async Task<WorkspaceGrantOutcome> GrantAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        var started = false;
        try
        {
            return await GrantCoreAsync(identity, () => started = true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!started)
        {
            return WorkspaceGrantOutcome.Declined;
        }
        catch (Exception ex) when (!started && ex is not WorkspaceGrantFailureException)
        {
            throw new WorkspaceGrantFailureException(ex.Message, ex);
        }
    }

    private async Task<WorkspaceGrantOutcome> GrantCoreAsync(
        WorkspaceDirectoryIdentity identity, Action onStarted, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("UAC grants require Windows.");
        ValidatePreparationScope(identity);
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("Cannot locate TinyHarness executable.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new
                InvalidOperationException("Directory authorization requires a published TinyHarness executable outside sandbox write roots.");
        ValidateProtectedPaths(identity, [executable, .. protectedPaths]);
        foreach (var root in writeRoots)
            if (Workspace.IsInside(root, executable))
                throw new
                    InvalidOperationException("The authorization executable lies inside a sandbox write root. Publish outside all write roots first.");
        // 可执行文件的祖先也不得通过链接重定向到项目控制的位置。
        WorkspaceDirectoryIdentity.Capture(Path.GetDirectoryName(executable)!);
        if (new FileInfo(executable).LinkTarget is not null)
            throw new InvalidDataException("The authorization executable may not be a link.");
        using var caller  = Process.GetCurrentProcess();
        using var current = WindowsIdentity.GetCurrent();
        if (sandboxSettings is not null)
            foreach (var root in writeRoots)
                if (Workspace.IsInside(root,
                                       WindowsSandboxComponents.ResolveSandboxHome(sandboxSettings.SandboxHome,
                                                sandboxSettings.AllowTcpLoopback)))
                    throw new InvalidDataException("The sandbox home must lie outside every write root.");
        var sandbox = sandboxSettings is null
            ? null
            : await WorkspaceSandboxAclContext.CaptureAsync(sandboxSettings, identity, cancellationToken)
                                              .ConfigureAwait(false);
        var request = new WorkspaceAclRequest(identity, current.User!.Value, caller.Id,
                                              caller.StartTime.ToUniversalTime().Ticks, protectedPaths.ToArray(),
                                              sandbox);
        using var channel = new WorkspaceGrantChannel(request.CallerSid);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute  = true,
                Verb             = "runas",
                WindowStyle      = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(executable),
                Arguments        = HelperArgument + " " + channel.Name
            }
        };
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动目录授权子进程。");
            onStarted();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return WorkspaceGrantOutcome.Declined;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        WorkspaceAclResponse response;
        var exchange = channel.SendAsync(process.Id, request, linked.Token);
        var exited = process.WaitForExitAsync(linked.Token);
        try
        {
            response = await ReceiveBeforeExitAsync(exchange, exited, linked.Token).ConfigureAwait(false);
            await exited.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await linked.CancelAsync().ConfigureAwait(false);
            // 管道断开与进程退出通知可能相差数毫秒，另给退出确认一个有界窗口。
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            // 启动后任何协议或等待失败都必须先确认退出，否则禁止自动重试。
            if (TryConfirmExit(process))
                throw new WorkspaceGrantFailureException($"目录授权子进程已退出，但未能确认授权成功：{ex.Message}", ex);
            return WorkspaceGrantOutcome.DidNotFinish;
        }

        return ConfirmResponse(response, process.ExitCode);
    }

    // helper 已退出时只等待短暂的缓冲响应排空，不继续耗尽整个授权期限。
    internal static async Task<WorkspaceAclResponse> ReceiveBeforeExitAsync(
        Task<WorkspaceAclResponse> exchange, Task exited, CancellationToken token)
    {
        if (await Task.WhenAny(exchange, exited).WaitAsync(token).ConfigureAwait(false) == exited)
            return await exchange.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        return await exchange.ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>只读检查授权范围及程序位置；不启动子进程、不修改 ACL。</para>
    ///     <para>Checks grant scope and executable placement without starting a child or changing ACLs.</para>
    /// </summary>
    public void ValidatePreparationScope(WorkspaceDirectoryIdentity identity)
    {
        identity.EnsureUnchanged();
        var executable = Environment.ProcessPath ??
                         throw new InvalidOperationException("Cannot locate TinyHarness executable.");
        foreach (var root in writeRoots.Prepend(identity.Path))
            if (Workspace.IsInside(root, executable))
                throw new InvalidDataException($"当前程序位于沙箱写根内：{executable}。请将完整程序发布到所有写根之外的独立目录，启动后再修复权限。");
        var paths = new List<string>(protectedPaths) { executable };
        if (sandboxSettings is { } settings)
        {
            paths.Add(settings.SetupExecutablePath);
            paths.Add(settings.RunnerExecutablePath);
            paths.Add(WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback));
        }

        ValidateProtectedPaths(identity, paths);
        foreach (var root in writeRoots)
        foreach (var path in paths)
            if (Workspace.IsInside(root, path))
                throw new InvalidDataException($"Protected path '{path}' lies inside a sandbox write root.");
        WorkspaceDirectoryIdentity.Capture(Path.GetDirectoryName(executable)!);
        if (new FileInfo(executable).LinkTarget is not null)
            throw new InvalidDataException("The authorization executable may not be a link.");
    }

    /// <summary>
    ///     <para>执行固定授权请求，校验原进程身份及目录身份；不解析其他命令。</para>
    ///     <para>Executes a fixed grant request after verifying caller and directory identities; parses no other commands.</para>
    /// </summary>
    public static async Task<int> ExecuteHelperAsync(string pipeName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("UAC grants require Windows.");
        if (!WorkspaceGrantChannel.IsValidName(pipeName))
            throw new InvalidDataException("Invalid authorization channel name.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var pipe     = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var request = await WorkspaceGrantChannel.ReceiveAsync(pipe, deadline.Token).ConfigureAwait(false);
        WorkspaceAclResponse response;
        var stage = "caller-identity";
        try
        {
            using var parent = Process.GetProcessById(request.CallerProcessId);
            if (parent.StartTime.ToUniversalTime().Ticks != request.CallerStartTicks ||
                !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath,
                               StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Workspace authorization caller identity changed.");
            if (!OpenProcessToken(parent.Handle, 8, out var token))
                throw new Win32Exception(Marshal.GetLastPInvokeError(),
                                         "Cannot verify workspace authorization caller token.");
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            {
                if (identity.User?.Value != request.CallerSid)
                    throw new InvalidDataException("Workspace authorization SID does not match the original caller.");
            }

            stage = "protected-paths";
            ValidateProtectedPaths(request.Directory, [Environment.ProcessPath!, .. request.ProtectedPaths]);
            using var pinned = WorkspaceDirectoryAcl.Pin(request.Directory);
            stage = "repair-tree";
            await WorkspaceAclInheritance.ValidateRepairTreeAsync(request.Directory, deadline.Token)
                                         .ConfigureAwait(false);
            if (request.Sandbox is { } sandbox)
            {
                stage = "sandbox-identity";
                ValidateProtectedPaths(request.Directory, [sandbox.SandboxHome]);
                await sandbox.ValidateAsync(request.Directory, deadline.Token).ConfigureAwait(false);
                await WorkspaceSandboxAclRepair.RunAsync(() =>
                                                         {
                                                             stage = "root-grant";
                                                             WorkspaceDirectoryAcl
                                                                .GrantSandboxWritePermissions(request.Directory,
                                                                 [
                                                                     sandbox.SandboxGroupSid,
                                                                     sandbox.WorkspaceCapabilitySid
                                                                 ]);
                                                             WorkspaceDirectoryAcl
                                                                .GrantChangePermissions(request.Directory,
                                                                          request.CallerSid);
                                                         },
                                                         () =>
                                                         {
                                                             stage = "inheritance-propagation";
                                                             WorkspaceDirectoryAcl
                                                                .ReapplyInheritance(request.Directory);
                                                         },
                                                         cancellation =>
                                                         {
                                                             stage = "inheritance-verification";
                                                             return WorkspaceAclInheritance
                                                                .FindMissingAsync(request.Directory, cancellation);
                                                         },
                                                         deadline.Token).ConfigureAwait(false);
            }
            else
            {
                stage = "caller-grant";
                WorkspaceDirectoryAcl.GrantChangePermissions(request.Directory, request.CallerSid);
            }

            response = new WorkspaceAclResponse(true, null);
        }
        catch (Exception ex)
        {
            response =
                new WorkspaceAclResponse(false,
                                         $"{stage}: {ex.GetType().Name}: {ex.Message[..Math.Min(ex.Message.Length, 1800)]}");
        }

        // 工作超时后仍须留出独立的响应期限，否则取消本身会掩盖失败原因。
        using var replyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await WorkspaceGrantChannel
             .WriteAsync(pipe, response, WorkspaceAclJsonContext.Default.WorkspaceAclResponse, replyDeadline.Token)
             .ConfigureAwait(false);
        return response.Succeeded ? 0 : 1;
    }

    internal static void ValidateProtectedPaths(WorkspaceDirectoryIdentity identity, IReadOnlyList<string> paths)
    {
        WorkspaceDirectoryAcl.ValidateGrantTarget(identity.Path);
        foreach (var path in paths)
            if (Workspace.IsInside(identity.Path, path))
                throw new InvalidDataException($"Directory authorization would expose protected path '{path}'.");
    }

    internal static WorkspaceGrantOutcome ClassifyFailedExchange(bool exitConfirmed) => exitConfirmed
        ? WorkspaceGrantOutcome.Failed
        : WorkspaceGrantOutcome.DidNotFinish;

    // 仅在管道响应和子进程退出均已确认后调用，保留有界 helper 错误。
    internal static WorkspaceGrantOutcome ConfirmResponse(WorkspaceAclResponse response, int exitCode)
    {
        if (response.Succeeded && exitCode == 0) return WorkspaceGrantOutcome.Completed;
        var detail = string.IsNullOrWhiteSpace(response.Error) ? "未提供错误详情" : response.Error;
        throw new WorkspaceGrantFailureException($"目录授权子进程已退出（{exitCode}）：{detail[..Math.Min(detail.Length, 2000)]}");
    }

    private static bool TryConfirmExit(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
}

internal sealed record WorkspaceAclRequest(
    WorkspaceDirectoryIdentity  Directory,
    string                      CallerSid,
    int                         CallerProcessId,
    long                        CallerStartTicks,
    string[]                    ProtectedPaths,
    WorkspaceSandboxAclContext? Sandbox = null);

internal sealed record WorkspaceAclResponse(bool Succeeded, string? Error);

[JsonSerializable(typeof(WorkspaceAclRequest))]
[JsonSerializable(typeof(WorkspaceAclResponse))]
[JsonSerializable(typeof(SandboxCapabilitySids))]
internal partial class WorkspaceAclJsonContext : JsonSerializerContext;
