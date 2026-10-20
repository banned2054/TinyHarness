using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         每命令 ACL 刷新：以 refresh_only=true 调用 setup.exe（普通权限，绝不提权，失败不自动
///         转 UAC 或 provisioning）。
///     </para>
///     <para>
///         Per-command ACL refresh: invokes setup.exe with refresh_only=true at normal privilege
///         (never elevating, never falling back to UAC or provisioning on failure).
///     </para>
/// </summary>
/// <remarks>
///     <para>只表达 refresh 语义；provisioning 属于独立管理入口。</para>
///     <para>It models refresh semantics only; provisioning belongs to the separate management entry point.</para>
/// </remarks>
public interface ISandboxSetupInvoker
{
    Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         setup.exe 的进程实现：payload base64 后作为唯一位置参数；超过 argv 安全阈值时切换
///         --launch-payload-env 分块环境通道。
///     </para>
///     <para>
///         Process-backed implementation for setup.exe: the base64 payload is passed as the single
///         positional argument, switching to the --launch-payload-env chunked environment channel
///         past the argv safety threshold.
///     </para>
/// </summary>
/// <remarks>
///     <para>失败读取 setup_error.json 报告具体错误。</para>
///     <para>Failures read setup_error.json for the concrete error report.</para>
/// </remarks>
public sealed class ProcessSandboxSetupInvoker(WindowsSandboxComponents components, TimeSpan? refreshTimeout = null)
    : ISandboxSetupInvoker
{
    /// <summary>
    ///     <para>base64 超过约 24000 个 UTF-16 单元必须走分块通道；此阈值留安全余量。</para>
    ///     <para>
    ///         Past roughly 24000 UTF-16 units of base64 the chunked channel is mandatory;
    ///         this threshold keeps a safety margin.
    ///     </para>
    /// </summary>
    internal const int PayloadArgumentCharacterLimit = 23_000;

    private static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromSeconds(60);

    public async Task RefreshAsync(SandboxSetupPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.RefreshOnly)
            throw new
                ArgumentException("The process invoker runs refreshes only; provisioning must go through the management entry point.",
                                  nameof(payload));

        var payloadJson =
            JsonSerializer.SerializeToUtf8Bytes(payload, WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        var base64Payload = Convert.ToBase64String(payloadJson);

        using var timeoutSource = new CancellationTokenSource(refreshTimeout ?? DefaultRefreshTimeout);
        using var linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        using var process = new Process();
        process.StartInfo = BuildStartInfo(base64Payload);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new
                InvalidOperationException($"Failed to launch the sandbox setup helper '{components.SetupExecutablePath}': {ex.Message}",
                                          ex);
        }

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw new TimeoutException($"The sandbox setup helper did not finish refreshing ACLs within " +
                                       $"{(int)(refreshTimeout ?? DefaultRefreshTimeout).TotalSeconds}s.");
        }
        catch (OperationCanceledException)
        {
            await KillAsync(process).ConfigureAwait(false);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Sandbox ACL refresh failed with exit code {process.ExitCode}: " +
                                                $"{await SandboxSetupErrorReader.DescribeAsync(components.SetupErrorPath).ConfigureAwait(false)}; " +
                                                $"command cwd: '{payload.CommandWorkingDirectory}'; helper: '{components.SetupExecutablePath}'; " +
                                                $"home: '{components.SandboxHome}'; see '{components.SandboxDirectory}\\sandbox.<UTC-date>.log'. " +
                                                "Refresh does not request UAC or retry provisioning; use /trust to inspect directory authorization.");
    }

    internal ProcessStartInfo BuildStartInfo(string base64Payload)
    {
        // helper 协议不使用 stdin/stdout；不重定向流可避免缓冲死锁与机器级信息被意外捕获。
        if (base64Payload.Length <= PayloadArgumentCharacterLimit)
            return new ProcessStartInfo(components.SetupExecutablePath)
            {
                Arguments        = base64Payload,
                UseShellExecute  = false,
                CreateNoWindow   = true,
                WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
            };

        // 大型 payload 走分块环境通道（CODEX_SANDBOX_LAUNCH_*），helper 改从环境变量读取而非 argv。
        var startInfo = new ProcessStartInfo(components.SetupExecutablePath)
        {
            Arguments        = "--launch-payload-env",
            UseShellExecute  = false,
            CreateNoWindow   = true,
            WorkingDirectory = Path.GetDirectoryName(components.SetupExecutablePath)!
        };
        var chunkSize  = 16                                     * 1024;
        var chunkCount = (base64Payload.Length + chunkSize - 1) / chunkSize;
        for (var index = 0; index < chunkCount; index++)
            startInfo.Environment[$"CODEX_SANDBOX_LAUNCH_{index}"] =
                base64Payload.Substring(index * chunkSize,
                                        Math.Min(chunkSize, base64Payload.Length - index * chunkSize));

        startInfo.Environment["CODEX_SANDBOX_LAUNCH_COUNT"] = chunkCount.ToString();
        startInfo.Environment["CODEX_SANDBOX_LAUNCH_BYTES"] = base64Payload.Length.ToString();
        return startInfo;
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // 取消与 kill 之间进程已自行退出。
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // 句柄已关闭，无可继续等待。
        }
    }
}
