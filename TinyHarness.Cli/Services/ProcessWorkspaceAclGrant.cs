using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;

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
    ///     <para>仅在宿主确认之后申请授权；取消后结果可能仍未知。</para>
    ///     <para>Requests a grant only after host consent; cancellation may leave its result unknown.</para>
    /// </summary>
    Task<WorkspaceGrantOutcome> GrantAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken);
}

/// <summary>
///     <para>只为已确认目录启动专用 UAC 授权子进程；不运行 provisioning 或项目命令。</para>
///     <para>Starts a dedicated UAC grant child for a confirmed directory without provisioning or project commands.</para>
/// </summary>
internal sealed partial class ProcessWorkspaceAclGrant(
    IReadOnlyList<string>   protectedPaths,
    IReadOnlyList<string>   writeRoots,
    WindowsSandboxSettings? sandboxSettings = null) : IWorkspaceAclGrant
{
    internal const string HelperArgument = "--internal-workspace-acl";

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
        return WorkspaceDirectoryAcl.CanChangePermissions(identity) && consistent &&
               await WorkspaceAclInheritance.CanManageAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WorkspaceGrantOutcome> GrantAsync(
        WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("UAC grants require Windows.");
        identity.EnsureUnchanged();
        WorkspaceDirectoryAcl.ValidateGrantTarget(identity.Path);
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
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return WorkspaceGrantOutcome.Declined;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        try
        {
            var response = await channel.SendAsync(process.Id, request, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            if (!response.Succeeded)
                throw new InvalidOperationException($"Directory authorization helper failed: {response.Error}");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // 提权子进程不能安全撤销，未知状态下不启动工具或自动重试。
            return WorkspaceGrantOutcome.DidNotFinish;
        }

        return process.ExitCode == 0 ? WorkspaceGrantOutcome.Completed : WorkspaceGrantOutcome.Failed;
    }

    /// <summary>
    ///     <para>执行固定授权请求，校验原进程身份及目录身份；不解析其他命令。</para>
    ///     <para>Executes a fixed grant request after verifying caller and directory identities; parses no other commands.</para>
    /// </summary>
    internal static async Task<int> ExecuteHelperAsync(string pipeName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("UAC grants require Windows.");
        if (!WorkspaceGrantChannel.IsValidName(pipeName))
            throw new InvalidDataException("Invalid authorization channel name.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var pipe     = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
        var       request = await WorkspaceGrantChannel.ReceiveAsync(pipe, deadline.Token).ConfigureAwait(false);
        using var parent  = Process.GetProcessById(request.CallerProcessId);
        if (parent.StartTime.ToUniversalTime().Ticks != request.CallerStartTicks ||
            !string.Equals(parent.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
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

        WorkspaceAclResponse response;
        try
        {
            ValidateProtectedPaths(request.Directory, [Environment.ProcessPath!, .. request.ProtectedPaths]);
            using var pinned = WorkspaceDirectoryAcl.Pin(request.Directory);
            await WorkspaceAclInheritance.ValidateRepairTreeAsync(request.Directory, deadline.Token)
                                         .ConfigureAwait(false);
            if (request.Sandbox is { } sandbox)
            {
                ValidateProtectedPaths(request.Directory, [sandbox.SandboxHome]);
                await sandbox.ValidateAsync(request.Directory, deadline.Token).ConfigureAwait(false);
                await WorkspaceSandboxAclRepair.RunAsync(() =>
                                                         {
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
                                                             WorkspaceDirectoryAcl
                                                                .ReapplyInheritance(request.Directory),
                                                         cancellation =>
                                                             WorkspaceAclInheritance.FindMissingAsync(request.Directory,
                                                                      cancellation),
                                                         deadline.Token).ConfigureAwait(false);
            }
            else
            {
                WorkspaceDirectoryAcl.GrantChangePermissions(request.Directory, request.CallerSid);
            }

            response = new WorkspaceAclResponse(true, null);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception
                                      or UnauthorizedAccessException)
        {
            response = new WorkspaceAclResponse(false, ex.Message[..Math.Min(ex.Message.Length, 2000)]);
        }

        await WorkspaceGrantChannel
             .WriteAsync(pipe, response, WorkspaceAclJsonContext.Default.WorkspaceAclResponse, deadline.Token)
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
