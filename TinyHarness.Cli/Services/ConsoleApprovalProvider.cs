using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>展示副作用范围与原始补丁预览，并读取本次运行范围的审批；命令摘要仍隐藏已知密钥。</para>
///     <para>
///         Shows side-effect scopes and original patch previews for run-scoped approval; command summaries still hide
///         known keys.
///     </para>
/// </summary>
internal sealed class ConsoleApprovalProvider(
    TextReader                           input,
    TextWriter                           output,
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

        var commandRedactor = preparation.Capability == "process.execute" ? _redactor : null;
        await output.WriteLineAsync($"  {TerminalDisplay.Format(preparation.Summary, commandRedactor)}");
        foreach (var target in preparation.TargetPaths)
            await output.WriteLineAsync($"  target: {TerminalDisplay.Format(target)}");

        if (preparation.ToolName == "apply_patch")
        {
            var patch = preparation.Arguments["patch"]?.GetValue<string>()
                     ?? throw new InvalidDataException("Cannot approve apply_patch without a patch preview.");
            await output.WriteLineAsync("  Proposed diff (control characters are escaped):");
            await output.WriteLineAsync(TerminalDisplay.Format(patch));
        }

        var runConstraint = preparation.SessionConstraint is null
            ? "."
            : preparation.Arguments["mode"]?.GetValue<string>() == "shell"
                ? " and this exact shell type, command text, and working directory."
                : " and this exact executable/argument shape.";
        await
            output.WriteLineAsync($"  Run approval ('s') grants '{TerminalDisplay.Format(preparation.Capability, _redactor)}' for the target paths above" +
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
}
