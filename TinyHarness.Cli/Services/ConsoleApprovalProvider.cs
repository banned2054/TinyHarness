using System.Text;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Cli.Services;

/// <summary>
///     在终端显示已经准备好的副作用、权限范围与必要预览，然后读取用户的审批选择。
///     M12.2 起"会话允许"的文案明确为本次运行范围（PLAN §4.2）：授权不延伸到未来运行，
///     也不被已保存会话继承；旧的 <c>[s]ession</c> 输入保持兼容，含义相同。所有展示文本
///     先做已知密钥脱敏再转义控制字符——审批界面与其他展示面遵循同一条不回显密钥的规则。
///     Displays the prepared side effect, permission scope, and any required preview before reading the user's choice.
///     Since M12.2 the "session approval" copy states the run-scoped reality (PLAN §4.2):
///     the grant does not extend to future runs nor is it inherited by a saved session,
///     while the legacy <c>[s]ession</c> input stays accepted with the same meaning. Every
///     displayed text is redacted for known secrets before control characters are escaped —
///     the approval UI follows the same no-echo rule as every other display surface.
/// </summary>
internal sealed class ConsoleApprovalProvider(TextReader input, TextWriter output,
                                              IReadOnlyDictionary<string, string>? knownSecrets = null) : IApprovalProvider
{
    private readonly DisplaySecretRedactor _redactor = new(knownSecrets);

    /// <summary>
    ///     创建使用标准输入输出的交互式审批器。
    ///     Creates an interactive approval provider backed by standard input and output.
    /// </summary>
    public ConsoleApprovalProvider() : this(Console.In, Console.Out)
    {
    }

    /// <summary>
    ///     显示副作用摘要、规范化目标和补丁预览，再将输入映射为单次允许、会话允许或拒绝。
    ///     Shows the side-effect summary, normalized targets, and patch preview, then maps input to an approval action.
    /// </summary>
    public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await output.WriteLineAsync();
        await output.WriteLineAsync("! Agent requests a side effect:");
        if (preparation.RiskLevel == ToolRiskLevel.Elevated)
            await
                output.WriteLineAsync("  RISK: elevated — shell syntax can chain commands, redirect output, and expand variables.");

        await output.WriteLineAsync($"  {DisplayText(preparation.Summary)}");
        foreach (var target in preparation.TargetPaths) await output.WriteLineAsync($"  target: {DisplayText(target)}");

        if (preparation.ToolName == "apply_patch")
        {
            var patch = preparation.Arguments["patch"]?.GetValue<string>()
                     ?? throw new InvalidDataException("Cannot approve apply_patch without a patch preview.");
            await output.WriteLineAsync("  Proposed diff (control characters are escaped):");
            await output.WriteLineAsync(DisplayText(patch));
        }

        var runConstraint = preparation.SessionConstraint is null
            ? "."
            : preparation.Arguments["mode"]?.GetValue<string>() == "shell"
                ? " and this exact shell type, command text, and working directory."
                : " and this exact executable/argument shape.";
        await
            output.WriteLineAsync($"  Run approval ('s') grants '{DisplayText(preparation.Capability)}' for the target paths above" +
                                  runConstraint);
        await
            output.WriteLineAsync("  It lasts for the remainder of this run only; future runs and a saved session do not inherit it.");
        await
            output.WriteLineAsync("  The legacy '[s]ession' input keeps the same run-scoped meaning.");
        await
            output.WriteLineAsync("  Directory scopes include descendants; later changes in this scope may run without asking.");
        await output.WriteAsync("  [a]llow once / [s]ession / [d]eny: ");
        await output.FlushAsync(cancellationToken);
        var line = await input.ReadLineAsync(cancellationToken);

        return line?.Trim().ToLowerInvariant() switch
        {
            "a" or "allow" or "once" or "allow once" or "y" or "yes" => ApprovalAction.AllowOnce,
            "s" or "session" or "allow session"                      => ApprovalAction.AllowSession,
            _                                                        => ApprovalAction.Deny
        };
    }

    /// <summary>
    ///     先做已知密钥脱敏，再保留换行与制表符、转义其他控制字符，避免审批界面回显密钥或
    ///     被不可见内容误导。
    ///     Redacts known secrets first, then preserves newlines/tabs while escaping other
    ///     control characters, so the approval UI neither echoes secrets nor is misled by
    ///     invisible content.
    /// </summary>
    private string DisplayText(string text)
    {
        text = _redactor.Redact(text);
        var display = new StringBuilder();
        foreach (var character in text.Replace("\r\n", "\n", StringComparison.Ordinal))
            if (char.IsControl(character) && character is not '\n' and not '\t')
                display.Append("\\u").Append(((int)character).ToString("X4"));
            else
                display.Append(character);

        return display.ToString();
    }
}
