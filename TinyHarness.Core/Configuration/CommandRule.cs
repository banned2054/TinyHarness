namespace TinyHarness.Core.Configuration;

/// <summary>
/// 一条显式配置的命令允许规则。direct 模式匹配可执行文件和参数 pattern；shell 模式
/// 精确匹配 shell 类型和完整命令文本。两种模式都精确匹配工作目录。
///
/// An explicitly configured command allow rule. Direct mode matches executable
/// plus argument patterns; shell mode exactly matches shell flavor and full
/// command text. Both modes exactly match the working directory.
/// </summary>
public sealed record CommandRule
{
    public string Mode { get; init; } = "direct";

    public string Executable { get; init; } = string.Empty;

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string Shell { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string WorkingDirectory { get; init; } = ".";
}
