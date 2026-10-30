using Avalonia;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Desktop;

internal static class Program
{
    /// <summary>
    ///     <para>优先分派受限目录授权子进程，再启动桌面或无窗口的离线验证。</para>
    ///     <para>
    ///         Dispatches the restricted directory-grant helper before starting the desktop or windowless offline
    ///         verification.
    ///     </para>
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == ProcessWorkspaceAclGrant.HelperArgument)
        {
            if (args.Length != 2) return 2;
            try
            {
                return ProcessWorkspaceAclGrant.ExecuteHelperAsync(args[1]).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                // 子进程失败仅返回状态，不启动窗口或将授权请求写入诊断日志。
                return 1;
            }
        }

        // Debug 构建固定启用沙箱诊断文件日志；Release 下调用整体消失。
        SandboxDiagnosticLog.Initialize();
        if (args.Contains("--offline-smoke", StringComparer.Ordinal))
            return OfflineSmoke.RunAsync().GetAwaiter().GetResult();
        DesktopLifecycleDiagnostics.Initialize();
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            DesktopLifecycleDiagnostics.Record("desktop-loop-returned");
            return 0;
        }
        catch (Exception exception)
        {
            DesktopLifecycleDiagnostics.Record("desktop-loop-faulted", exception);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
