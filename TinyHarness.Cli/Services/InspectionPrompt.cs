using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>以脱敏检查说明请求用户明确确认 NeedsInspection，不依据模型输出解除阻断。</para>
///     <para>
///         Requests explicit user confirmation of NeedsInspection using a redacted inspection note;
///         model output never clears the block.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         EOF、取消或非确认输入均保持副作用阻断；只读分析仍可继续，用户可重新打开会话再次确认。
///         本类型仅采集回答，实际确认与保存由调用方执行。
///     </para>
///     <para>
///         EOF, cancellation, and nonconfirming input keep side effects blocked;
///         read-only analysis remains available, and reopening the session allows another confirmation.
///         This type only collects the answer; the caller applies and persists acknowledgement.
///     </para>
/// </remarks>
internal static class InspectionPrompt
{
    /// <summary>
    ///     <para>显示检查提示并读取一次确认回答，不修改宿主的阻断状态。</para>
    ///     <para>Displays the inspection prompt and reads one answer without changing the host's block state.</para>
    /// </summary>
    /// <param name="input">
    ///     <para>与会话回合及审批共享的输入读取器；由调用方持有，不在此释放。</para>
    ///     <para>The input reader shared with turns and approvals; owned by the caller and not disposed here.</para>
    /// </param>
    /// <returns>
    ///     <para>仅明确确认返回 true；EOF、读取取消或其他回答返回 false。</para>
    ///     <para>True only for explicit confirmation; false for EOF, read cancellation, or any other answer.</para>
    /// </returns>
    public static async Task<bool> ConfirmAsync(ChatSessionHost       host,     TextReader input, TextWriter output,
                                                DisplaySecretRedactor redactor, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(TerminalDisplay.Format($"! Needs inspection: {host.InspectionPendingNote}",
                                                           redactor));
        await
            output.WriteLineAsync("  Side-effect tools (patches, processes) are blocked until you inspect the workspace and confirm.");
        await output.WriteAsync("  inspect : [c]onfirmed, allow side effects / [k]eep blocked: ");
        await output.FlushAsync(cancellationToken);

        string? answer;
        try
        {
            answer = await input.ReadLineAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 读取取消不视为确认，保持副作用阻断；后续可重新打开会话确认。
            await output.WriteLineAsync();
            await output.WriteLineAsync("  inspection not confirmed; side effects stay blocked.");
            return false;
        }

        var confirmed = answer?.Trim().ToLowerInvariant() is "c" or "confirm" or "y" or "yes";
        await output.WriteLineAsync(confirmed
                                        ? "  inspection confirmed; side effects are allowed again."
                                        : "  side effects stay blocked; re-open the session later to confirm after inspecting.");
        return confirmed;
    }
}
