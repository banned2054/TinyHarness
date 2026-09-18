using System.Text;

namespace TinyHarness.Cli;

/// <summary>
/// 管理命令的终端 I/O 抽象；测试注入假实现，避免依赖真实控制台。
///
/// Console I/O abstraction for management commands; tests inject a fake instead of relying on a real console.
/// </summary>
internal interface ICliConsole
{
    /// <summary>标准输入是否为交互式终端（未重定向）。Whether stdin is an interactive terminal (not redirected).</summary>
    bool IsInteractive { get; }

    Task WriteAsync(string text, CancellationToken cancellationToken);

    Task WriteLineAsync(string text, CancellationToken cancellationToken);

    Task WriteErrorLineAsync(string text, CancellationToken cancellationToken);

    /// <summary>
    /// 读取一行；EOF 返回 <see langword="null"/>。
    /// Reads one line; <see langword="null"/> at end of input.
    /// </summary>
    Task<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 读取一行并尽量隐藏输入（交互终端逐键回读）；重定向输入退化为普通读取。
    /// Reads one line, hiding the input on interactive terminals when possible; redirected input falls back to plain reading.
    /// </summary>
    Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 基于标准输入输出的 <see cref="ICliConsole"/> 实现。隐藏输入在交互终端上通过逐键回读实现。
///
/// <see cref="ICliConsole"/> implementation over stdin/stdout. Hidden input uses per-key reading on interactive terminals.
/// </summary>
internal sealed class ConsoleCliIo : ICliConsole
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public async Task WriteAsync(string text, CancellationToken cancellationToken) =>
        await Console.Out.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);

    public async Task WriteLineAsync(string text, CancellationToken cancellationToken) =>
        await Console.Out.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);

    public async Task WriteErrorLineAsync(string text, CancellationToken cancellationToken) =>
        await Console.Error.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);

    public async Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken)
    {
        if (!IsInteractive)
        {
            // Redirected input (piped scripts) cannot hide characters; read plainly.
            return await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }

        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept : true);
            switch (key.Key)
            {
                case ConsoleKey.Enter :
                    await Console.Out.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken)
                                 .ConfigureAwait(false);
                    return buffer.ToString();

                case ConsoleKey.Backspace when buffer.Length > 0 :
                    buffer.Remove(buffer.Length - 1, 1);
                    break;

                case ConsoleKey.Backspace :
                    break;

                default :
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}

/// <summary>
/// 管理命令共享的交互提示辅助。
///
/// Shared interactive prompt helpers for management commands.
/// </summary>
internal static class CliPrompt
{
    /// <summary>
    /// 显示提示并读取一行；EOF 抛出带说明的异常，避免把空输入静默当作答案。
    ///
    /// Shows a prompt and reads one line; EOF throws with an explanation instead of silently treating empty input as an answer.
    /// </summary>
    public static async Task<string> RequiredLineAsync(ICliConsole       io, string label,
                                                       CancellationToken cancellationToken)
    {
        await io.WriteAsync($"{label}: ", cancellationToken).ConfigureAwait(false);
        var line = await io.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new InvalidOperationException(
                                                $"Input ended while waiting for '{label}'. Nothing was saved; rerun the command to answer all prompts.");
        }

        return line.Trim();
    }

    /// <summary>
    /// 显示提示并读取一行，空输入时使用默认值。
    /// Shows a prompt and reads one line, using the default when the input is empty.
    /// </summary>
    public static async Task<string> LineWithDefaultAsync(ICliConsole       io, string label, string defaultValue,
                                                          CancellationToken cancellationToken)
    {
        var line = await RequiredLineAsync(io, $"{label} [{defaultValue}]", cancellationToken)
           .ConfigureAwait(false);
        return line.Length == 0 ? defaultValue : line;
    }

    /// <summary>
    /// 读取一个正整数，非法输入时重复提示。
    /// Reads a positive integer, re-prompting on invalid input.
    /// </summary>
    public static async Task<int> PositiveIntAsync(ICliConsole       io, string label, int defaultValue,
                                                   CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await LineWithDefaultAsync(io, label, defaultValue.ToString(), cancellationToken)
               .ConfigureAwait(false);
            if (int.TryParse(line, out var value) && value > 0)
            {
                return value;
            }

            await io.WriteLineAsync($"  '{line}' is not a positive integer; try again.", cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 读取一个 yes/no 确认；空输入使用默认答案。
    /// Reads a yes/no confirmation; empty input uses the default answer.
    /// </summary>
    public static async Task<bool> ConfirmAsync(ICliConsole       io, string label, bool defaultYes,
                                                CancellationToken cancellationToken)
    {
        var suffix = defaultYes ? "[Y/n]" : "[y/N]";
        while (true)
        {
            var line = await RequiredLineAsync(io, $"{label} {suffix}", cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                return defaultYes;
            }

            switch (line.ToLowerInvariant())
            {
                case "y" or "yes" :
                    return true;
                case "n" or "no" :
                    return false;
                default :
                    await io.WriteLineAsync("  Answer with 'y' or 'n'.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }
}
