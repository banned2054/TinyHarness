using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     &lt;home&gt;\.sandbox\setup_error.json 的共享读取器：setup.exe 退出码不区分失败类型，
///     失败详情只能读该文件。refresh 调用器（ProcessSandboxSetupInvoker）与管理入口
///     （WindowsSandboxProvisioner）共用同一份读取与描述，不改变各自的失败处理语义。
///     Shared reader for &lt;home&gt;\.sandbox\setup_error.json: the setup.exe
///     exit code does not classify failures, the details live in this file
///     only. The refresh invoker (ProcessSandboxSetupInvoker) and the
///     management entry point (WindowsSandboxProvisioner) share one
///     implementation without changing either failure-handling semantics.
/// </summary>
internal static class SandboxSetupErrorReader
{
    /// <summary>
    ///     读取 setup_error.json；文件缺失、不可读或非法 JSON 时返回 null（退出码仍是完整的失败信号）。
    ///     Reads setup_error.json; a missing, unreadable, or invalid file yields
    ///     null (the exit code remains a complete failure signal).
    /// </summary>
    internal static async Task<SandboxSetupErrorReport?> TryReadAsync(string setupErrorPath,
                                                                      CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(setupErrorPath)) return null;

            var content = await File.ReadAllTextAsync(setupErrorPath, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxSetupErrorReport);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    ///     生成人类可读的失败描述："code: message"；无报告时给出占位说明并指向沙箱日志。
    ///     Builds the human-readable failure description "code: message"; with
    ///     no report it falls back to a placeholder pointing at the sandbox log.
    /// </summary>
    internal static async Task<string> DescribeAsync(string setupErrorPath,
                                                     CancellationToken cancellationToken = default)
    {
        var report = await TryReadAsync(setupErrorPath, cancellationToken).ConfigureAwait(false);
        return report is not null
            ? $"{report.Code}: {report.Message}"
            : $"no error report found at {setupErrorPath}; see the sandbox log for details.";
    }
}
