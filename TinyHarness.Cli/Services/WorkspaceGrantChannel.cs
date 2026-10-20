using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>通过限制访问的管道传递已确认请求，双向校验进程 ID，拒绝伪造连接。</para>
///     <para>Transfers consented requests over an access-restricted pipe with bidirectional process-ID verification.</para>
/// </summary>
internal sealed partial class WorkspaceGrantChannel : IDisposable
{
    private const int MaximumBytes = 24000;

    private readonly NamedPipeServerStream _pipe;

    /// <summary>
    ///     <para>为原调用账户和本地管理员建立一次性管道，不允许子进程继承句柄。</para>
    ///     <para>Creates a single-use pipe for the original account and local administrators without handle inheritance.</para>
    /// </summary>
    public WorkspaceGrantChannel(string callerSid)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows pipes are required.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(callerSid), PipeAccessRights.FullControl,
                                                  AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,
                                                                         null),
                                                  PipeAccessRights.ReadWrite, AccessControlType.Allow));
        _pipe = NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                PipeOptions.Asynchronous, MaximumBytes, MaximumBytes, security);
    }

    public string Name { get; } = "tinyharness-workspace-acl-" + Guid.NewGuid().ToString("N");

    public void Dispose() => _pipe.Dispose();

    /// <summary>
    ///     <para>确认连接者就是已启动的 helper 后发送请求，并有界读取确认结果。</para>
    ///     <para>Sends a request only to the launched helper and reads its bounded confirmation.</para>
    /// </summary>
    public async Task<WorkspaceAclResponse> SendAsync(int               helperPid, WorkspaceAclRequest request,
                                                      CancellationToken token)
    {
        await _pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var actual) || actual != (uint)helperPid)
            throw new InvalidDataException("Workspace authorization pipe client is not the launched helper.");
        await WriteAsync(_pipe, request, WorkspaceAclJsonContext.Default.WorkspaceAclRequest, token)
           .ConfigureAwait(false);
        return await ReadAsync(_pipe, WorkspaceAclJsonContext.Default.WorkspaceAclResponse, token)
           .ConfigureAwait(false);
    }

    internal static bool IsValidName(string name) =>
        name.StartsWith("tinyharness-workspace-acl-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name["tinyharness-workspace-acl-".Length..], "N", out _);

    /// <summary>
    ///     <para>读取请求并核对服务端 PID，尚不授予任何文件权限。</para>
    ///     <para>Reads a request and verifies its server PID without granting filesystem permissions.</para>
    /// </summary>
    internal static async Task<WorkspaceAclRequest> ReceiveAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        var request = await ReadAsync(pipe, WorkspaceAclJsonContext.Default.WorkspaceAclRequest, token)
           .ConfigureAwait(false);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) ||
            actual != (uint)request.CallerProcessId)
            throw new InvalidDataException("Workspace authorization pipe server is not the original caller.");
        return request;
    }

    internal static async Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Workspace authorization frame is too large.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumBytes)
            throw new InvalidDataException("Invalid workspace authorization frame size.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, type) ??
               throw new InvalidDataException("Missing workspace authorization frame.");
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return : MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
