using System.Text;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Cli;

/// <summary>
/// 在终端显示已经准备好的副作用、权限范围与必要预览，然后读取用户的审批选择。
/// Displays the prepared side effect, permission scope, and any required preview before reading the user's choice.
/// </summary>
internal sealed class ConsoleApprovalProvider(TextReader input, TextWriter output) : IApprovalProvider
{
    /// <summary>
    /// 创建使用标准输入输出的交互式审批器。
    /// Creates an interactive approval provider backed by standard input and output.
    /// </summary>
    public ConsoleApprovalProvider() : this(Console.In, Console.Out)
    {
    }

    /// <summary>
    /// 显示副作用摘要、规范化目标和补丁预览，再将输入映射为单次允许、会话允许或拒绝。
    /// Shows the side-effect summary, normalized targets, and patch preview, then maps input to an approval action.
    /// </summary>
    public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync();
        await output.WriteLineAsync("! Agent requests a side effect:");
        if (preparation.RiskLevel == ToolRiskLevel.Elevated)
        {
            await
                output.WriteLineAsync("  RISK: elevated — shell syntax can chain commands, redirect output, and expand variables.");
        }

        await output.WriteLineAsync($"  {DisplayText(preparation.Summary)}");
        foreach (var target in preparation.TargetPaths)
        {
            await output.WriteLineAsync($"  target: {DisplayText(target)}");
        }

        if (preparation.ToolName == "apply_patch")
        {
            var patch = preparation.Arguments["patch"]?.GetValue<string>()
                     ?? throw new InvalidDataException("Cannot approve apply_patch without a patch preview.");
            await output.WriteLineAsync("  Proposed diff (control characters are escaped):");
            await output.WriteLineAsync(DisplayText(patch));
        }

        var sessionConstraint = preparation.SessionConstraint is null
            ? "."
            : preparation.Arguments["mode"]?.GetValue<string>() == "shell"
                ? " and this exact shell type, command text, and working directory."
                : " and this exact executable/argument shape.";
        await
            output.WriteLineAsync($"  Session approval grants '{DisplayText(preparation.Capability)}' for the target paths above" +
                                  sessionConstraint);
        await
            output.WriteLineAsync("  Directory scopes include descendants; later changes in this scope may run without asking.");
        await output.WriteAsync("  [a]llow once / [s]ession / [d]eny: ");
        await output.FlushAsync(cancellationToken);
        var line = await input.ReadLineAsync(cancellationToken);

        return (line?.Trim().ToLowerInvariant()) switch
        {
            "a" or "allow" or "once" or "allow once" or "y" or "yes" => ApprovalAction.AllowOnce,
            "s" or "session" or "allow session"                      => ApprovalAction.AllowSession,
            _                                                        => ApprovalAction.Deny,
        };
    }

    /// <summary>
    /// 保留换行与制表符，同时转义其他控制字符，避免审批界面被不可见内容误导。
    /// Preserves newlines/tabs while escaping other control characters so invisible content cannot mislead approval.
    /// </summary>
    private static string DisplayText(string text)
    {
        var display = new StringBuilder();
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            if (char.IsControl(character) && character is not '\n' and not '\t')
            {
                display.Append("\\u").Append(((int)character).ToString("X4"));
            }
            else
            {
                display.Append(character);
            }
        }

        return display.ToString();
    }
}
