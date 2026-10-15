using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>提供会话存储与旧运行快照的严格离线检查入口。</para>
///     <para>Provides strictly offline inspection of stored sessions and legacy run snapshots.</para>
/// </summary>
/// <remarks>
///     <para>
///         不解析 API key、不调用模型、不启动工具，也不写入工作区文件。
///         list 报告损坏条目并继续；show 拒绝损坏、未知版本或审计与快照不一致的会话。
///         旧 <c>.session.json</c> 运行快照仅供只读查看，不能用于续聊。
///     </para>
///     <para>
///         Does not resolve API keys, call models, start tools, or write workspace files.
///         list reports damaged entries and continues; show rejects damage, unknown versions,
///         or snapshot/audit divergence. Legacy <c>.session.json</c> run snapshots are
///         read-only inspection data, not resumable sessions.
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
    ///     <para>列出持久会话与旧运行快照；报告损坏条目后继续列举。</para>
    ///     <para>
    ///         Lists persisted sessions and legacy run snapshots;
    ///         reports damaged entries and continues listing.
    ///     </para>
    /// </summary>
    private static async Task<int> ListAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (storeDir, legacyDir) = await ResolveDirectoriesAsync(context, cancellationToken).ConfigureAwait(false);

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

        await io.WriteLineAsync($"Run snapshots (legacy, read-only): {legacyDir}", cancellationToken)
                .ConfigureAwait(false);
        var legacyCount = 0;
        if (Directory.Exists(legacyDir))
            foreach (var path in Directory.EnumerateFiles(legacyDir, "*.session.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileName(path)[..^".session.json".Length];
                if (!SessionStorePaths.IsValidSessionId(id))
                {
                    // 与受保护存储一致，报告无效旧文件名，不静默隐藏。
                    legacyCount++;
                    await io.WriteLineAsync($"  {Path.GetFileName(path)}  skipped: file name is not a session id",
                                            cancellationToken).ConfigureAwait(false);
                    continue;
                }

                legacyCount++;
                string summary;
                try
                {
                    var snapshot = await SessionStoreReader.ReadLegacyAsync(legacyDir, id, cancellationToken)
                                                           .ConfigureAwait(false);
                    summary = snapshot is null
                        ? "unreadable"
                        : $"{snapshot.Result.Status} {snapshot.CompletedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}  messages {snapshot.Messages.Count}";
                }
                catch (SessionStoreException ex)
                {
                    summary = $"damaged: {ex.Message}";
                }

                await io.WriteLineAsync($"  {id}  {summary}", cancellationToken).ConfigureAwait(false);
            }

        if (legacyCount == 0)
            await io.WriteLineAsync("  (none)", cancellationToken).ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    ///     <para>显示会话检查视图；存储中不存在时尝试只读展示旧运行快照。</para>
    ///     <para>
    ///         Shows a session inspection view; tries a read-only legacy run snapshot
    ///         when the session is absent from the store.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>展示成功返回 0；找不到会话或存储读取被拒绝时返回 1。</para>
    ///     <para>0 on successful display; 1 if the session is missing or a store read is rejected.</para>
    /// </returns>
    private static async Task<int> ShowAsync(
        CommandContext context, string sessionId, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var (storeDir, legacyDir) = await ResolveDirectoriesAsync(context, cancellationToken).ConfigureAwait(false);
        var redactor = new DisplaySecretRedactor(null);

        try
        {
            var session = await SessionStoreReader.ReadAsync(storeDir, sessionId, cancellationToken)
                                                  .ConfigureAwait(false);
            if (session is not null)
            {
                await WriteStoredSessionAsync(io, session, redactor, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            var legacy = await SessionStoreReader.ReadLegacyAsync(legacyDir, sessionId, cancellationToken)
                                                 .ConfigureAwait(false);
            if (legacy is not null)
            {
                await WriteLegacySnapshotAsync(io, legacy, legacyDir, redactor, cancellationToken)
                   .ConfigureAwait(false);
                return 0;
            }
        }
        catch (SessionStoreException ex)
        {
            await io.WriteErrorLineAsync(ex.Message, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await io.WriteErrorLineAsync($"No session '{sessionId}' found in '{storeDir}' or '{legacyDir}'.",
                                     cancellationToken)
                .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     <para>解析受保护会话存储目录与旧运行快照目录。</para>
    ///     <para>Resolves the protected session-store directory and legacy run-snapshot directory.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         会话存储目录仅取用户配置或默认值，旧快照目录取生效配置；
    ///         显式存储目录必须通过保护检查。
    ///     </para>
    ///     <para>
    ///         The session-store directory comes only from user settings or the default;
    ///         the legacy directory comes from effective configuration.
    ///         An explicit store directory must pass protection checks.
    ///     </para>
    /// </remarks>
    private static async Task<(string StoreDirectory, string LegacyDirectory)> ResolveDirectoriesAsync(
        CommandContext context, CancellationToken cancellationToken)
    {
        var resolution = await ConfigResolver
                              .ResolveAsync(null, cancellationToken,
                                            context.ResolveWorkingDirectory(), context.ResolveUserConfigPath())
                              .ConfigureAwait(false);
        var userConfig = await UserConfigStore.LoadAsync(context.ResolveUserConfigPath(), cancellationToken)
                                              .ConfigureAwait(false);
        var storeDirectory = SessionStorePaths.ResolveProtected(userConfig.Settings?.SessionStoreDirectory,
                                                                resolution.Config.WorkspaceRoot);
        return (storeDirectory, resolution.Config.SessionDirectory);
    }

    private static async Task WriteStoredSessionAsync(
        ICliConsole io, PersistedSession session, DisplaySecretRedactor redactor, CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"session         : {session.Id}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"format version  : {session.FormatVersion}", cancellationToken).ConfigureAwait(false);
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
                                   (session.FormatVersion < PersistedSession.CurrentFormatVersion ||
                                    conversation.OpaqueRedacted
                                       ? "(inspection only; legacy redaction)"
                                       : "(verbatim protocol stored; resume checks required)")
                                 : "conversation    : not stored by this format version (inspection only)",
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
            await io.WriteLineAsync($"  input : {TerminalDisplay.Format(turn.UserInput, redactor)}", cancellationToken)
                    .ConfigureAwait(false);

            foreach (var run in turn.Runs)
                await WriteRunSummaryAsync(io, run, redactor, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRunSummaryAsync(
        ICliConsole io, PersistedRun run, DisplaySecretRedactor redactor, CancellationToken cancellationToken)
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

        if (!string.IsNullOrEmpty(run.FinalMessage))
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
        if (run.ToolCalls is not { Count: > 0 }) return;

        foreach (var call in run.ToolCalls)
            await WriteToolCallAsync(io, call.ToolName, call.Approval, call.State, call.SideEffects,
                                     redactor, cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteLegacySnapshotAsync(
        ICliConsole       io, SessionSnapshot snapshot, string legacyDirectory, DisplaySecretRedactor redactor,
        CancellationToken cancellationToken)
    {
        await io.WriteLineAsync("legacy run snapshot (read-only; cannot be resumed)", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"run id          : {snapshot.RunId}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"directory       : {legacyDirectory}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"started         : {snapshot.StartedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"completed       : {snapshot.CompletedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"status          : {snapshot.Result.Status}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"messages        : {snapshot.Messages.Count}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"steps / tools   : {snapshot.Result.Steps} / {snapshot.Result.ToolExecutions}",
                                cancellationToken).ConfigureAwait(false);
        if (snapshot.Result.InputTokens is not null || snapshot.Result.OutputTokens is not null)
            await io.WriteLineAsync($"tokens          : {snapshot.Result.InputTokens ?? 0:N0} in / " +
                                    $"{snapshot.Result.OutputTokens                  ?? 0:N0} out",
                                    cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(snapshot.Result.FinalMessage))
            await io
                 .WriteLineAsync($"final           : {TerminalDisplay.Format(snapshot.Result.FinalMessage, redactor)}",
                                 cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(snapshot.Result.Error))
            await io.WriteLineAsync($"error           : {TerminalDisplay.Format(snapshot.Result.Error, redactor)}",
                                    cancellationToken).ConfigureAwait(false);
        if (snapshot.Result.ToolCalls is { Count: > 0 } calls)
            foreach (var call in calls)
                // 旧快照中的数字枚举经源生成读取器还原后，共用此展示路径。
                await WriteToolCallAsync(io, call.ToolName, call.Approval, call.State.ToString(), call.SideEffects,
                                         redactor, cancellationToken).ConfigureAwait(false);
    }
}
