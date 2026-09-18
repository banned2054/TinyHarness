namespace TinyHarness.Core.Configuration;

/// <summary>
/// 配置解析失败的异常。<see cref="IsUsageError"/> 为 true 时表示用户输入（参数、显式路径）有问题，
/// CLI 应返回使用错误退出码；否则表示文件内容或环境状态问题。
///
/// Thrown when configuration resolution fails. <see cref="IsUsageError"/> marks user-input problems
/// (arguments, explicit paths) that the CLI reports with a usage exit code; other values indicate file
/// content or environment problems.
/// </summary>
public sealed class ConfigException : Exception
{
    /// <summary>
    /// 创建配置异常并标记是否属于使用错误。
    /// Creates a config exception and marks whether it is a usage error.
    /// </summary>
    public ConfigException(string message, bool isUsageError = false) : base(message)
    {
        IsUsageError = isUsageError;
    }

    /// <summary>
    /// 是否属于用户输入错误（对应 CLI 的使用错误退出码）。
    /// Whether this is a user-input error (mapped to the CLI usage exit code).
    /// </summary>
    public bool IsUsageError { get; }
}
