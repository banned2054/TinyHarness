namespace TinyHarness.Cli.Exceptions;

/// <summary>
/// 参数解析失败；携带针对该命令的用法说明，由入口统一打印并返回使用错误退出码。
///
/// Thrown for argument parsing failures; carries command-specific usage that the entry point prints with a usage exit code.
/// </summary>
internal sealed class CliUsageException(string message, string? usage = null) : Exception(message)
{
    public string? Usage { get; } = usage;
}
