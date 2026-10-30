using Avalonia;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Desktop;

internal static class Program
{
    /// <summary>
    /// <para>启动原生桌面；离线 smoke 入口不初始化窗口或访问真实模型。</para>
    /// <para>Starts the native desktop; the offline smoke path neither creates windows nor accesses real models.</para>
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
                return 1;
            }
        }

        SandboxDiagnosticLog.Initialize();
        if (args.Contains("--offline-smoke", StringComparer.Ordinal))
            return OfflineSmoke.RunAsync().GetAwaiter().GetResult();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
