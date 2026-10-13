using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     `tinyharness session`（M13.1，PLAN §7.1）：受保护会话存储的严格离线查看入口——
///     不解析 API key、不调用模型、不启动工具、不打开工作区文件写入。list 容错枚举
///     （损坏条目报告不隐藏），show 严格校验（损坏、未知版本、审计与快照不一致均拒绝）；
///     旧 <c>.session.json</c> run 快照只读兼容展示，不能恢复为续聊会话（PLAN §7.2）。
///     `tinyharness session` (M13.1, PLAN §7.1): the strictly offline inspection
///     entry of the protected session store — no API key is resolved, no model
///     is called, no tool runs and no workspace file is opened for writing.
///     list tolerates damaged entries (reported, never hidden); show is strict
///     (damage, unknown versions and snapshot/audit divergence are refused);
///     legacy <c>.session.json</c> run snapshots are displayed read-only and are
///     never restored into resumable sessions (PLAN §7.2).
/// </summary>
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
    ///     列出受保护存储中的持久会话与 run 快照目录中的旧格式条目；单个条目损坏不终止列举。
    ///     Lists the persisted sessions in the protected store and the legacy entries in
    ///     the run-snapshot directory; one damaged entry does not stop the listing.
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
                    // Damaged-looking legacy names are reported, never hidden —
                    // the same rule the protected store's listing follows.
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
    ///     显示一个存储会话的完整检查视图；新存储中不存在时回退到旧 run 快照的只读展示。
    ///     两处都不存在时返回 1。
    ///     Shows the full inspection view of one stored session; falls back to the
    ///     read-only view of a legacy run snapshot when absent from the store.
    ///     Returns 1 when neither location holds the id.
    /// </summary>
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
    ///     解析两个查看位置：受保护存储目录（用户配置或默认）与 run 快照目录（生效配置）。
    ///     显式存储目录不满足保护要求时在这里拒绝。
    ///     Resolves both inspection locations: the protected store directory (user
    ///     config or default) and the run-snapshot directory (effective config). An
    ///     explicit store directory that fails the protection checks is refused here.
    /// </summary>
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
    }

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
    }
}
