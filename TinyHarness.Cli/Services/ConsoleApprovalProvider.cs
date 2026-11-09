using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>
///         展示副作用范围、执行分类与原始补丁预览，并读取本次运行的审批结论；命令摘要仍隐藏已知密钥。
///         非交互输入不是拒绝：返回 Unavailable，明确表示操作未执行。
///     </para>
///     <para>
///         Shows the side-effect scope, execution level and original patch preview, then reads one run-scoped
///         resolution; command summaries still hide known keys. Non-interactive input is not a denial — it
///         returns Unavailable, stating plainly that nothing executed.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         只有调用方显式声明交互输入时才读取审批；重定向输入即使包含有效批准词也不授予权限。
///         注入读取器的测试须显式传入 <c>interactive: true</c>，不依赖测试宿主的控制台状态。
///     </para>
///     <para>
///         An approval is read only when the caller explicitly declares interactive input; redirected input
///         never grants permission even when it contains a valid approval word. Tests that inject a reader
///         must pass <c>interactive: true</c> instead of relying on the test host's console state.
///     </para>
/// </remarks>
internal sealed class ConsoleApprovalProvider(
    TextReader                           input,
    TextWriter                           output,
    IReadOnlyDictionary<string, string>? knownSecrets = null,
    bool                                 interactive  = false) : IApprovalProvider
{
    private readonly DisplaySecretRedactor _redactor = new(knownSecrets);

    /// <summary>
    ///     <para>创建使用标准输入输出的交互式审批器；输入被重定向时按非交互处理。</para>
    ///     <para>
    ///         Creates an interactive approval provider backed by standard input and output; a redirected
    ///         input is treated as non-interactive.
    ///     </para>
    /// </summary>
    public ConsoleApprovalProvider() : this(Console.In, Console.Out, null, !Console.IsInputRedirected)
    {
    }

    /// <summary>
    ///     <para>
    ///         显示执行分类、副作用摘要、规范化目标、补丁预览与可授权范围，再把输入映射为审批结论。
    ///         调用方未声明交互输入时返回 Unavailable；无法识别的输入按拒绝处理。
    ///     </para>
    ///     <para>
    ///         Shows the execution level, side-effect summary, normalized targets, patch preview and grantable
    ///         scopes, then maps the input to a resolution. Without a declared interactive input the provider
    ///         returns Unavailable; unrecognized input is treated as a denial.
    ///     </para>
    /// </summary>
    public async Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!interactive)
        {
            await output.WriteLineAsync("! A side effect requires approval, but non-interactive input cannot approve it.")
                        .ConfigureAwait(false);
            return ApprovalResolution.Unavailable("No interactive approval input is available; the operation was not executed.");
        }

        var preparation = request.Preparation;
        await output.WriteLineAsync().ConfigureAwait(false);
        await output.WriteLineAsync("! Agent requests a side effect:").ConfigureAwait(false);
        await output.WriteLineAsync($"  level: {DescribeLevel(request.Level)}").ConfigureAwait(false);
        if (preparation.RiskLevel == ToolRiskLevel.Elevated)
            await output
                 .WriteLineAsync("  RISK: elevated — shell syntax can chain commands, redirect output, and expand variables.")
                 .ConfigureAwait(false);

        var commandRedactor = preparation.Capability == "process.execute" ? _redactor : null;
        await output.WriteLineAsync($"  {TerminalDisplay.Format(preparation.Summary, commandRedactor)}")
                    .ConfigureAwait(false);
        foreach (var target in preparation.TargetPaths)
            await output.WriteLineAsync($"  target: {TerminalDisplay.Format(target)}").ConfigureAwait(false);

        if (preparation.ToolName == "apply_patch")
        {
            var patch = preparation.Arguments["patch"]?.GetValue<string>()
                     ?? throw new InvalidDataException("Cannot approve apply_patch without a patch preview.");
            await output.WriteLineAsync("  Proposed diff (control characters are escaped):").ConfigureAwait(false);
            await output.WriteLineAsync(TerminalDisplay.Format(patch)).ConfigureAwait(false);
        }

        if (preparation.RequiresNetworkAccess)
            await output
                 .WriteLineAsync("  NETWORK: full network access inside the sandbox (possibly through your local proxy); not a domain-level narrow grant.")
                 .ConfigureAwait(false);
        // 展示事实行与 CLI/桌面两端一致：后端与隔离声明、文件与网络权限、有效期、活动授权摘要。
        foreach (var displayNote in request.DisplayNotes ?? [])
            await output.WriteLineAsync($"  fact: {TerminalDisplay.Format(_redactor.Redact(displayNote))}")
                        .ConfigureAwait(false);
        if (request.ReviewerNote is { } note)
            await output.WriteLineAsync($"  reviewer: {TerminalDisplay.Format(note)}").ConfigureAwait(false);

        var scopes = request.AllowedScopes;
        await output.WriteLineAsync(ScopeHelp(scopes)).ConfigureAwait(false);
        await output.WriteLineAsync("  Turn/session approvals last for the remainder of this run/session only;")
                    .ConfigureAwait(false);
        await output.WriteLineAsync("  they never survive a restart and are not saved as reusable authority.")
                    .ConfigureAwait(false);
        await output.WriteAsync("  choice: ").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
            return ApprovalResolution.Unavailable("No interactive input available; the operation was not executed.");

        var choice = line.Trim().ToLowerInvariant();
        if (choice is "t" or "turn" && scopes.Contains(GrantScope.Turn))
            return ApprovalResolution.Granted(GrantScope.Turn);
        if (choice is "s" or "session" && scopes.Contains(GrantScope.Session))
            return ApprovalResolution.Granted(GrantScope.Session);
        if (choice is "a" or "allow" or "once" or "allow once" or "y" or "yes")
            return ApprovalResolution.Granted(GrantScope.SingleOperation);
        if (choice is "d" or "deny" or "n" or "no")
            return ApprovalResolution.Denied("Denied by the user.");

        // 无法识别的输入按拒绝处理：宁可不执行。
        return ApprovalResolution.Denied($"Unrecognized approval input '{choice}'; treated as a denial.");
    }

    /// <summary>把执行分类转成面向终端的说明。</summary>
    private static string DescribeLevel(PermissionLevel level) => level switch
    {
        PermissionLevel.Level1 => "1 (sandbox baseline)",
        PermissionLevel.Level2 => "2 (sandbox add-on: outside paths, metadata writes or network)",
        PermissionLevel.Level3 => "3 (host execution, no OS isolation, single operation only)",
        _                      => "unclassified (fail-closed approval)"
    };

    /// <summary>按可授权范围生成输入说明。</summary>
    private static string ScopeHelp(IReadOnlyList<GrantScope> scopes)
    {
        var parts = new List<string> { "[a]llow once" };
        if (scopes.Contains(GrantScope.Turn)) parts.Add("[t]urn (this run)");
        if (scopes.Contains(GrantScope.Session)) parts.Add("[s]ession (this running instance, explicit opt-in)");
        return "  " + string.Join(" / ", parts) + " / [d]eny.";
    }
}
