using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>提供会话日志的严格离线检查入口。</para>
///     <para>Provides strictly offline inspection of session logs.</para>
/// </summary>
/// <remarks>
///     <para>不解析 API key、不调用模型、不启动工具或写入工作区；list 报告损坏条目，show 拒绝损坏与未知格式。</para>
///     <para>
///         Resolves no API keys, calls no models, starts no tools, and writes no workspace files;
///         list reports damaged entries, while show rejects damage and unknown formats.
///     </para>
/// </remarks>
internal static class SessionCommand
{
    public static async Task<int> ExecuteAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken) => options.Subcommand switch
    {
        "list" => await ListAsync(context, cancellationToken).ConfigureAwait(false),
        "show" => await ShowAsync(context, options.Primary!, cancellationToken).ConfigureAwait(false),
        _      => throw new InvalidOperationException($"Unknown session subcommand '{options.Subcommand}'.")
    };

    /// <summary>
    ///     <para>列出持久会话；报告损坏条目后继续列举。</para>
    ///     <para>Lists persisted sessions, reporting damaged entries without stopping the list.</para>
    /// </summary>
    private static async Task<int> ListAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io       = context.Io;
        var storeDir = await ResolveStoreDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Session store: {storeDir}", cancellationToken).ConfigureAwait(false);
        var entries = await SessionStoreReader.ListAsync(storeDir, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
            await io.WriteLineAsync("  (no stored sessions)", cancellationToken).ConfigureAwait(false);

        foreach (var entry in entries)
            if (entry.Session is { } session)
                await io
                     .WriteLineAsync($"  {entry.Id}  updated {session.UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}" +
                                     $"  turns {session.Turns.Count}  {session.WorkspaceRoot}",
                                     cancellationToken).ConfigureAwait(false);
            else
                await io.WriteLineAsync($"  {entry.Id}  damaged: {entry.Error}", cancellationToken)
                        .ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    ///     <para>显示会话日志的检查视图；不存在或读取被拒绝时返回 1。</para>
    ///     <para>Shows a session-log inspection view; returns 1 if absent or reading is rejected.</para>
    /// </summary>
    private static async Task<int> ShowAsync(
        CommandContext context, string sessionId, CancellationToken cancellationToken)
    {
        var io       = context.Io;
        var storeDir = await ResolveStoreDirectoryAsync(context, cancellationToken).ConfigureAwait(false);
        var redactor = new DisplaySecretRedactor(null);
        try
        {
            var session = await SessionStoreReader.ReadAsync(storeDir, sessionId, cancellationToken)
                                                  .ConfigureAwait(false);
            if (session is not null)
            {
                await WriteStoredSessionAsync(io, storeDir, session, redactor, cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }
        catch (SessionStoreException ex)
        {
            await io.WriteErrorLineAsync(ex.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await io.WriteErrorLineAsync($"No session '{sessionId}' found in '{storeDir}'.", cancellationToken)
                .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     <para>解析用户配置或默认的受保护存储目录，并校验工作区边界。</para>
    ///     <para>Resolves the protected store from user settings or defaults and validates workspace boundaries.</para>
    /// </summary>
    private static async Task<string> ResolveStoreDirectoryAsync(
        CommandContext context, CancellationToken cancellationToken)
    {
        var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken,
                                                           context.ResolveWorkingDirectory(),
                                                           context.ResolveUserConfigPath()).ConfigureAwait(false);
        var userConfig = await UserConfigStore.LoadAsync(context.ResolveUserConfigPath(), cancellationToken)
                                              .ConfigureAwait(false);
        return SessionStorePaths.ResolveProtected(userConfig.Settings?.SessionStoreDirectory,
                                                  resolution.Config.WorkspaceRoot);
    }

    private static async Task WriteStoredSessionAsync(
        ICliConsole       io, string storeDirectory, PersistedSession session, DisplaySecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        var historyReader = new SessionHistoryReader();
        var firstHistoryPage = await historyReader.ReadPageAsync(storeDirectory, session.Id,
                                                                 SessionHistoryReader.MaxPageItems, null,
                                                                 cancellationToken).ConfigureAwait(false);

        var hasHistory = firstHistoryPage is { Items.Count: > 0 };
        await io.WriteLineAsync($"session         : {session.Id}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"format          : {SessionLogFormat.FormatName}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"workspace       : {session.WorkspaceRoot}", cancellationToken).ConfigureAwait(false);
        var chatApi = session.Model is null
            ? string.Empty
            : $" ({ChatApiKindParser.ToValueString(session.Model.ChatApi)})";
        await io.WriteLineAsync(session.Model is null
                                    ? "model           : (unknown)"
                                    : $"model           : {session.Model.ModelId}{chatApi} @ {session.Model.Endpoint}",
                                cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"created         : {session.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                                cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"updated         : {session.UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss} (commit {session.CommitSequence})",
                             cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"turns           : {session.Turns.Count}", cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync(session.Conversation is { } conversation
                                 ? $"conversation    : {conversation.Messages.Count} messages " +
                                   "(verbatim protocol stored; resume checks required)"
                                 : "conversation    : not stored",
                             cancellationToken)
             .ConfigureAwait(false);

        // 区分历史未知副作用与当前确认状态；待检查的会话续聊时仍阻断副作用。
        if (session.PendingInspectionReason is not null || session.InspectionAcknowledgedUtc is not null ||
            session.Turns.SelectMany(turn => turn.Runs).Any(run => run.NeedsInspection == true))
        {
            await io.WriteLineAsync(session.InspectionAcknowledgedUtc is { } acknowledged
                                        ? $"inspection      : acknowledged {acknowledged.LocalDateTime:yyyy-MM-dd HH:mm:ss}; " +
                                          "side effects are not blocked by this inspection"
                                        : "inspection      : needs inspection (pending; side effects blocked until " +
                                          "confirmed on resume)",
                                    cancellationToken)
                    .ConfigureAwait(false);
            if (session.InspectionAcknowledgedUtc is null && session.PendingInspectionReason is { } reason)
                await io.WriteLineAsync($"  reason        : {TerminalDisplay.Format(reason, redactor)}",
                                        cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < session.Turns.Count; i++)
        {
            var turn = session.Turns[i];
            await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"turn {i + 1} <{turn.Id}> {turn.CreatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                                    cancellationToken).ConfigureAwait(false);
            if (!hasHistory)
                await io.WriteLineAsync($"  input : {TerminalDisplay.Format(turn.UserInput, redactor)}",
                                        cancellationToken)
                        .ConfigureAwait(false);

            foreach (var run in turn.Runs)
                await WriteRunSummaryAsync(io, run, redactor, !hasHistory, cancellationToken).ConfigureAwait(false);
        }

        if (!hasHistory) return;
        await io.WriteLineAsync(string.Empty, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("history (append-only JSONL):", cancellationToken).ConfigureAwait(false);
        var page = firstHistoryPage;
        while (true)
        {
            foreach (var item in page.Items)
                await WriteHistoryItemAsync(io, item, redactor, cancellationToken).ConfigureAwait(false);
            if (page.Warning is { Length: > 0 } pageWarning)
                await io.WriteLineAsync($"  history warning: {TerminalDisplay.Format(pageWarning, redactor)}",
                                        cancellationToken).ConfigureAwait(false);
            if (page.NextCursor is null) break;
            page = await historyReader.ReadPageAsync(storeDirectory, session.Id,
                                                     SessionHistoryReader.MaxPageItems, page.NextCursor,
                                                     cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRunSummaryAsync(ICliConsole io, PersistedRun run, DisplaySecretRedactor redactor,
                                                   bool        includeDetails, CancellationToken cancellationToken)
    {
        var status = run.Status ?? "running";
        var line = $"  run <{run.Id}>: {status.ToLowerInvariant()}" +
                   (run.FinishedUtc is { } finished
                       ? $" at {finished.LocalDateTime:yyyy-MM-dd HH:mm:ss}"
                       : string.Empty) +
                   $"  steps {run.Steps}  tools {run.ToolExecutions}";
        if (run.InputTokens is not null || run.OutputTokens is not null)
            line += $"  tokens {run.InputTokens ?? 0:N0} in / {run.OutputTokens ?? 0:N0} out";
        await io.WriteLineAsync(line, cancellationToken).ConfigureAwait(false);

        if (includeDetails && !string.IsNullOrEmpty(run.FinalMessage))
            await io.WriteLineAsync($"    final : {TerminalDisplay.Format(run.FinalMessage, redactor)}",
                                    cancellationToken)
                    .ConfigureAwait(false);
        if (!string.IsNullOrEmpty(run.Error))
            await io.WriteLineAsync($"    error : {TerminalDisplay.Format(run.Error, redactor)}", cancellationToken)
                    .ConfigureAwait(false);
        if (run.NeedsInspection == true)
            await io
                 .WriteLineAsync("    inspection : a side effect was possibly executed with an unknown result in this run " +
                                 "(historical fact; current inspection state is shown above)",
                                 cancellationToken).ConfigureAwait(false);

        // 从脱敏检查投影展示执行记录；未知结果不得补造成功、失败或退出码。
        if (!includeDetails || run.ToolCalls is not { Count: > 0 }) return;

        foreach (var call in run.ToolCalls)
            await WriteToolCallAsync(io, call.ToolName, call.Approval, call.State, call.SideEffects,
                                     redactor, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHistoryItemAsync(
        ICliConsole io, SessionHistoryItem item, DisplaySecretRedactor redactor, CancellationToken cancellationToken)
    {
        var identity                                      = $"  {item.Kind} <{item.Id}> seq {item.Sequence}";
        if (item.RunId is { Length: > 0 } runId) identity += $" run <{runId}>";
        await io.WriteLineAsync(identity, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(item.Text))
            await io.WriteLineAsync($"    text       : {TerminalDisplay.Format(item.Text, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Kind != SessionHistoryKind.Tool) return;

        if (item.ToolName is { Length: > 0 } toolName)
            await io.WriteLineAsync($"    tool       : {TerminalDisplay.Format(toolName, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.State is { Length: > 0 } state)
            await io.WriteLineAsync($"    state      : {ExecutionStateText(state, true, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Succeeded is { } succeeded)
            await io.WriteLineAsync($"    succeeded  : {succeeded}", cancellationToken).ConfigureAwait(false);
        if (item.ExitCode is { } exitCode)
            await io.WriteLineAsync($"    exit code  : {exitCode}", cancellationToken).ConfigureAwait(false);
        if (item.TimedOut)
            await io.WriteLineAsync("    timed out  : true", cancellationToken).ConfigureAwait(false);
        if (item.Approval is { Length: > 0 } approval)
            await io.WriteLineAsync($"    approval   : {TerminalDisplay.Format(approval, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Capability is { Length: > 0 } capability)
            await io.WriteLineAsync($"    capability : {TerminalDisplay.Format(capability, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.ExecutionPolicy is { Length: > 0 } policy)
            await io.WriteLineAsync($"    policy     : {TerminalDisplay.Format(policy, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Summary is { Length: > 0 } summary)
            await io.WriteLineAsync($"    summary    : {TerminalDisplay.Format(summary, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.ArgumentsJson is { Length: > 0 } arguments)
            await io.WriteLineAsync($"    arguments  : {TerminalDisplay.Format(arguments, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.TargetPaths is { Count: > 0 } paths)
            foreach (var path in paths)
                await io.WriteLineAsync($"    target     : {TerminalDisplay.Format(path, redactor)}",
                                        cancellationToken).ConfigureAwait(false);
        if (item.NotStartedReason is { Length: > 0 } notStartedReason)
            await io.WriteLineAsync($"    not started: {TerminalDisplay.Format(notStartedReason, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Stdout is { Length: > 0 } stdout)
            await io.WriteLineAsync($"    stdout     : {TerminalDisplay.Format(stdout, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.Stderr is { Length: > 0 } stderr)
            await io.WriteLineAsync($"    stderr     : {TerminalDisplay.Format(stderr, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (item.OutputIncomplete)
            await io
                 .WriteLineAsync($"    output     : incomplete{(item.OutputWarning is { Length: > 0 } warning ? "; " + TerminalDisplay.Format(warning, redactor) : string.Empty)}",
                                 cancellationToken).ConfigureAwait(false);
        if (item.PatchChanges is { Count: > 0 } changes)
            foreach (var change in changes)
            {
                await io.WriteLineAsync($"    diff {TerminalDisplay.Format(change.Path, redactor)} " +
                                        $"({TerminalDisplay.Format(change.Operation, redactor)}; {TerminalDisplay.Format(change.State, redactor)}):",
                                        cancellationToken).ConfigureAwait(false);
                await io.WriteLineAsync(TerminalDisplay.Format(change.Diff, redactor), cancellationToken)
                        .ConfigureAwait(false);
            }
    }

    private static Task WriteToolCallAsync(ICliConsole io, string toolName, string? approval, string state,
                                           bool sideEffects, DisplaySecretRedactor redactor,
                                           CancellationToken cancellationToken) =>
        io.WriteLineAsync($"    tool {TerminalDisplay.Format(toolName, redactor)}: " +
                          (approval is null ? "(not authorized)" : TerminalDisplay.Format(approval, redactor)) +
                          " — " + ExecutionStateText(state, sideEffects, redactor), cancellationToken);

    /// <summary>
    ///     <para>格式化执行状态；不识别的值经脱敏和转义后展示，不猜测其含义。</para>
    ///     <para>
    ///         Formats an execution state; redacts and escapes unrecognized values
    ///         without guessing their meaning.
    ///     </para>
    /// </summary>
    private static string ExecutionStateText(string state, bool sideEffects, DisplaySecretRedactor redactor) =>
        state switch
        {
            nameof(ToolCallExecutionState.KnownNotStarted) => "known not started",
            nameof(ToolCallExecutionState.ConfirmedResult) => "confirmed result",
            nameof(ToolCallExecutionState.UnknownResult) => sideEffects
                ? "result unknown; side effect possible"
                : "result unknown",
            _ => $"unrecognized state '{TerminalDisplay.Format(state, redactor)}'"
        };
}
