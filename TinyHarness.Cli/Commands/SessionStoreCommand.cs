using TinyHarness.Cli.Models;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>
///         会话业务库的离线管理入口：检查迁移状态、显式执行或续作迁移、备份、校验备份与恢复备份。
///         所有子命令都不解析 API key、不调用模型、不运行工具，也不改写旧会话日志。
///     </para>
///     <para>
///         The offline management entry for the session business database: inspect the migration state, run or resume
///         the explicit migration, back up, verify a backup, and restore a backup. No subcommand resolves API keys,
///         calls a model, runs tools, or rewrites legacy session logs.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         迁移与恢复要求显式 <c>--yes</c>；缺少确认时只打印计划并返回 1。恢复会替换业务库文件并把旧库复制为
///         <c>session-store.db.pre-restore-&lt;unixms&gt;</c>，因此命令会如实打印结果路径供用户留存。
///     </para>
///     <para>
///         Migration and restore require an explicit <c>--yes</c>; without confirmation the command only prints the plan
///         and returns 1. A restore replaces the business-database file and copies the old one to
///         <c>session-store.db.pre-restore-&lt;unixms&gt;</c>, so the command prints the resulting paths for the user.
///     </para>
/// </remarks>
internal static class SessionStoreCommand
{
    /// <summary>
    ///     <para>调度 session store 子命令并返回进程退出码。</para>
    ///     <para>Dispatches the session store subcommand and returns its process exit code.</para>
    /// </summary>
    public static async Task<int> ExecuteAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken) => options.StoreOperation switch
    {
        "status"  => await StatusAsync(context, options, cancellationToken).ConfigureAwait(false),
        "migrate" => await MigrateAsync(context, options, cancellationToken).ConfigureAwait(false),
        "backup"  => await BackupAsync(context, options, cancellationToken).ConfigureAwait(false),
        "verify"  => await VerifyAsync(context, options, cancellationToken).ConfigureAwait(false),
        "restore" => await RestoreAsync(context, options, cancellationToken).ConfigureAwait(false),
        _ => throw new InvalidOperationException($"Unknown session store subcommand '{options.StoreOperation}'.")
    };

    /// <summary>
    ///     <para>
    ///         打印迁移状态、批次状态与诊断警告；迁移完成返回 0，未开始、进行中或异常状态返回 2。
    ///     </para>
    ///     <para>
    ///         Prints the migration status, batch states, and diagnostic warnings; a completed migration returns 0 while
    ///         not-started, in-progress, or abnormal states return 2.
    ///     </para>
    /// </summary>
    private static async Task<int> StatusAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io          = context.Io;
        var storePath   = options.StoreDirectoryPath!;
        var inspection  = await SessionStoreMigration.InspectAsync(storePath, cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Session store : {Path.GetFullPath(storePath)}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"Migration     : {inspection.Status}", cancellationToken).ConfigureAwait(false);
        if (inspection.MigrationId is { Length: > 0 } migrationId)
            await io.WriteLineAsync($"Migration id  : {migrationId}", cancellationToken).ConfigureAwait(false);
        if (inspection.StoreIdentity is { Length: > 0 } storeIdentity)
            await io.WriteLineAsync($"Store identity: {storeIdentity}", cancellationToken).ConfigureAwait(false);
        foreach (var batch in SessionStoreMigrationMarker.BatchOrder)
        {
            var state = inspection.Batches.GetValueOrDefault(batch);
            await io.WriteLineAsync($"  {batch,-17} {state}", cancellationToken).ConfigureAwait(false);
        }

        if (inspection.Detail is { Length: > 0 } detail)
            await io.WriteLineAsync($"Detail        : {detail}", cancellationToken).ConfigureAwait(false);
        foreach (var warning in inspection.Warnings)
            await io.WriteLineAsync($"Warning       : {warning}", cancellationToken).ConfigureAwait(false);
        return inspection.Status == SessionStoreMigrationStatus.Completed ? 0 : 2;
    }

    /// <summary>
    ///     <para>需要 <c>--yes</c> 才执行迁移；缺失时打印计划并返回 1。成功后打印每批的行数与来源指纹。</para>
    ///     <para>Runs the migration only with <c>--yes</c>; otherwise prints the plan and returns 1. On success it prints each batch's rows and source fingerprint.</para>
    /// </summary>
    private static async Task<int> MigrateAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var storePath = options.StoreDirectoryPath!;
        if (!options.AssumeYes)
        {
            await io.WriteLineAsync("This migrates the legacy session files of the store into session-store.db:", cancellationToken)
                    .ConfigureAwait(false);
            await io.WriteLineAsync($"  store            : {Path.GetFullPath(storePath)}", cancellationToken)
                    .ConfigureAwait(false);
            await io.WriteLineAsync("  batches          : organizationState, projectCatalog, sessionPlacement",
                                    cancellationToken).ConfigureAwait(false);
            if (options.StoreSidebarPreferencesPath is { Length: > 0 } sidebarPath)
                await io.WriteLineAsync($"  sidebar          : {Path.GetFullPath(sidebarPath)}", cancellationToken)
                        .ConfigureAwait(false);
            await io.WriteLineAsync("Legacy files stay untouched and are never deleted. Re-run with --yes to migrate.",
                                    cancellationToken).ConfigureAwait(false);
            return 1;
        }

        try
        {
            var migrationOptions = new SessionStoreMigrationOptions(storePath)
            {
                SidebarPreferencesPath = options.StoreSidebarPreferencesPath
            };
            var result = await SessionStoreMigration.MigrateAsync(migrationOptions, cancellationToken)
                                                   .ConfigureAwait(false);
            await io.WriteLineAsync(result.AlreadyCompleted
                                        ? "Migration     : already completed; nothing was imported"
                                        : "Migration     : completed",
                                    cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Migration id  : {result.MigrationId}", cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Store identity: {result.StoreIdentity}", cancellationToken).ConfigureAwait(false);
            foreach (var batch in result.Batches)
                await io.WriteLineAsync($"  {batch.Batch,-17} rows {batch.ImportedRows}  " +
                                        $"{(batch.SwitchedThisRun ? "switched this run" : "already switched")}  " +
                                        $"source {batch.SourceFingerprint}",
                                        cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (SessionStoreMigrationException ex)
        {
            await io.WriteErrorLineAsync($"Migration failed ({ex.Kind}): {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    ///     <para>把业务库备份到 <c>--output</c> 目录并打印备份元数据；不覆盖已存在的备份文件。</para>
    ///     <para>Backs the business database up into the <c>--output</c> directory and prints its metadata without overwriting an existing backup file.</para>
    /// </summary>
    private static async Task<int> BackupAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io = context.Io;
        try
        {
            var info = await SessionStoreBackup.CreateAsync(options.StoreDirectoryPath!, options.StoreOutputPath!,
                                                            cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Backup        : {info.Path}", cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Store identity: {info.StoreIdentity}", cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Schema version: {info.SchemaVersion}", cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Length        : {info.LengthBytes} bytes", cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (SessionStoreBackupException ex)
        {
            await io.WriteErrorLineAsync($"Backup failed ({ex.Kind}): {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    ///     <para>只读校验备份；有效返回 0，缺失、损坏或版本不支持返回 1。</para>
    ///     <para>Read-only verifies a backup; valid returns 0 while missing, damaged, or unsupported returns 1.</para>
    /// </summary>
    private static async Task<int> VerifyAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io = context.Io;
        try
        {
            var verification = await SessionStoreBackup.VerifyAsync(options.StoreBackupPath!, cancellationToken)
                                                      .ConfigureAwait(false);
            await io.WriteLineAsync($"Backup        : {Path.Combine(Path.GetFullPath(options.StoreBackupPath!),
                                                                          SessionStoreDatabase.DatabaseFileName)}",
                                    cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Verification  : {verification.Status}", cancellationToken).ConfigureAwait(false);
            if (verification.Detail is { Length: > 0 } detail)
                await io.WriteLineAsync($"Detail        : {detail}", cancellationToken).ConfigureAwait(false);
            if (verification.StoreIdentity is { Length: > 0 } identity)
                await io.WriteLineAsync($"Store identity: {identity}", cancellationToken).ConfigureAwait(false);
            if (verification.SchemaVersion is { } version)
                await io.WriteLineAsync($"Schema version: {version}", cancellationToken).ConfigureAwait(false);
            return verification.Status == SessionStoreBackupVerificationStatus.Valid ? 0 : 1;
        }
        catch (SessionStoreBackupException ex)
        {
            await io.WriteErrorLineAsync($"Backup verification failed ({ex.Kind}): {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    ///     <para>需要 <c>--yes</c> 才把备份恢复到目标 store；成功后打印恢复与恢复前快照路径。</para>
    ///     <para>Restores a backup into the target store only with <c>--yes</c>; on success it prints the restored database and pre-restore snapshot paths.</para>
    /// </summary>
    private static async Task<int> RestoreAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var io        = context.Io;
        var storePath = Path.GetFullPath(options.StoreDirectoryPath!);
        try
        {
            await SessionStoreBackup.RestoreAsync(options.StoreDirectoryPath!, options.StoreBackupPath!,
                                                  cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync($"Restored      : {SessionStoreDatabase.PathFor(storePath)}", cancellationToken)
                    .ConfigureAwait(false);
            await io.WriteLineAsync("Derived index files were removed for re-derivation; legacy JSON files and the backup " +
                                    "source were left untouched.",
                                    cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (SessionStoreBackupException ex)
        {
            await io.WriteErrorLineAsync($"Restore failed ({ex.Kind}): {ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }
    }
}
