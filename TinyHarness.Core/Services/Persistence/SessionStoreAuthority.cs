using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Core.Services.Persistence;

/// <summary>
///     <para>
///         会话存储权威模式：旧文件仍是生产权威（尚未迁移）、业务库是生产权威（迁移完成），或迁移进行中／
///         状态异常而拒绝生产读写。
///     </para>
///     <para>
///         The session-store authority mode: legacy files remain production authorities (not migrated), the business
///         database is the production authority (migration completed), or production access is refused because a
///         migration is running or the state is abnormal.
///     </para>
/// </summary>
public enum SessionStoreMode
{
    /// <summary>
    ///     <para>没有迁移标记：旧文件是生产权威，迁移只能由显式入口触发。</para>
    ///     <para>No migration marker: legacy files are the production authority and only the explicit entry point migrates.</para>
    /// </summary>
    Legacy,

    /// <summary>
    ///     <para>迁移完成且标记有效：业务库是生产权威，旧文件不再读写。</para>
    ///     <para>The migration completed with a valid marker: the business database is authoritative and legacy files are no longer read or written.</para>
    /// </summary>
    Migrated,

    /// <summary>
    ///     <para>迁移进行中、标记损坏或库状态异常：拒绝生产读写并报告原因，不静默回退旧文件。</para>
    ///     <para>
    ///         A migration is running, the marker is damaged, or the database is abnormal: production access is
    ///         refused with a reason and never silently falls back to legacy files.
    ///     </para>
    /// </summary>
    Blocked
}

/// <summary>
///     <para>权威模式解析结果：模式、迁移状态与不含凭据的说明。</para>
///     <para>The authority-mode resolution: mode, migration status, and a credential-free detail.</para>
/// </summary>
/// <param name="Mode">
///     <para>权威模式。</para>
///     <para>The authority mode.</para>
/// </param>
/// <param name="Status">
///     <para>底层迁移状态。</para>
///     <para>The underlying migration status.</para>
/// </param>
/// <param name="Detail">
///     <para>阻止生产读写时的原因；正常模式下为 null。</para>
///     <para>The reason production access is blocked, or null in a normal mode.</para>
/// </param>
public sealed record SessionStoreModeResolution(SessionStoreMode Mode, SessionStoreMigrationStatus Status, string? Detail);

/// <summary>
///     <para>
///         按持久化迁移标记决定一个 store 的生产权威来源；标记缺失只表示尚未迁移，读不到或损坏时拒绝
///         生产读写，绝不据此回退旧文件。
///     </para>
///     <para>
///         Decides a store's production authority from the persisted migration marker. A missing marker only means
///         the store is not migrated; an unreadable or damaged marker refuses production access and never falls back
///         to legacy files.
///     </para>
/// </summary>
public static class SessionStoreAuthority
{
    /// <summary>
    ///     <para>解析指定 store 的权威模式；每次调用都读取当前标记，不缓存跨进程可见的迁移状态。</para>
    ///     <para>Resolves a store's authority mode; every call reads the current marker and never caches cross-process migration state.</para>
    /// </summary>
    public static async Task<SessionStoreModeResolution> ResolveAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeDirectory);
        var read = await SessionStoreMigration.ReadMarkerAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        switch (read.Status)
        {
            case SessionStoreMarkerReadStatus.Missing :
                return new SessionStoreModeResolution(SessionStoreMode.Legacy,
                                                      SessionStoreMigrationStatus.NotStarted, null);
            case SessionStoreMarkerReadStatus.Damaged :
                return new SessionStoreModeResolution(SessionStoreMode.Blocked,
                                                      SessionStoreMigrationStatus.MarkerDamaged,
                                                      read.Detail);
        }

        var marker = read.Marker!;
        return marker.IsComplete
            ? new SessionStoreModeResolution(SessionStoreMode.Migrated, SessionStoreMigrationStatus.Completed,
                                             null)
            : new SessionStoreModeResolution(
                SessionStoreMode.Blocked, SessionStoreMigrationStatus.InProgress,
                $"Session-store migration '{marker.MigrationId}' has not finished switching every batch.");
    }

    /// <summary>
    ///     <para>业务库当前是否为生产权威；标记缺失时为 false。</para>
    ///     <para>Whether the business database is the current production authority; false when the marker is missing.</para>
    /// </summary>
    public static async Task<bool> IsMigratedAsync(string storeDirectory, CancellationToken cancellationToken) =>
        (await ResolveAsync(storeDirectory, cancellationToken).ConfigureAwait(false)).Mode ==
        SessionStoreMode.Migrated;

    /// <summary>
    ///     <para>要求业务库是生产权威；尚未迁移或状态异常时以类型化异常拒绝，调用方不得回退旧文件。</para>
    ///     <para>Requires the business database to be the production authority; an unmigrated or abnormal store is refused with a typed exception and callers must not fall back.</para>
    /// </summary>
    public static async Task RequireMigratedAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        if (resolution.Mode == SessionStoreMode.Migrated) return;

        throw new SessionStoreMigrationException(
                                                $"Session store '{Path.GetFullPath(storeDirectory)}' is not ready for business-database access " +
                                                $"({Describe(resolution)}); run the explicit session-store migration or restore first.",
                                                resolution.Mode == SessionStoreMode.Legacy
                                                    ? SessionStoreFailureKind.MigrationRequired
                                                    : SessionStoreFailureKind.Conflict);
    }

    /// <summary>
    ///     <para>把拒绝原因渲染为简短说明；仅用于错误消息。</para>
    ///     <para>Renders a refusal reason for error messages only.</para>
    /// </summary>
    internal static string Describe(SessionStoreModeResolution resolution) =>
        resolution.Detail is { Length: > 0 } detail
            ? $"{resolution.Status}: {detail}"
            : resolution.Status.ToString();

    /// <summary>
    ///     <para>在迁移进行中、标记损坏或库状态异常时抛出类型化拒绝；正常模式（旧文件或业务库）直接返回。</para>
    ///     <para>Throws a typed refusal while a migration is running, the marker is damaged, or the database is abnormal; normal modes return.</para>
    /// </summary>
    public static void ThrowIfBlocked(string storeDirectory, SessionStoreModeResolution resolution)
    {
        if (resolution.Mode != SessionStoreMode.Blocked) return;
        throw new SessionStoreMigrationException(
                                                $"Session store '{Path.GetFullPath(storeDirectory)}' cannot be read or written while " +
                                                $"{Describe(resolution)}; finish the migration or restore the business backup first.",
                                                SessionStoreFailureKind.Conflict);
    }
}

/// <summary>
///     <para>
///         业务库的已验证连接入口：要求迁移标记完整、库可用且身份与标记一致；失败按缺失、损坏、不受支持、
///         锁定、权限、只读、IO 分类，供上层转换为各自的领域异常。
///     </para>
///     <para>
///         The verified connection entry of the business database: it requires a completed migration marker, an
///         available database, and an identity matching the marker; failures are classified as missing, damaged,
///         unsupported, locked, permission, read-only, or IO for upper layers to translate into their domain errors.
///     </para>
/// </summary>
internal static class SessionStoreBusinessConnection
{
    /// <summary>
    ///     <para>打开一条已校验连接；调用方负责释放。</para>
    ///     <para>Opens one verified connection; the caller owns its disposal.</para>
    /// </summary>
    public static async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenVerifiedAsync(
        string storeDirectory, CancellationToken cancellationToken)
    {
        var read = await SessionStoreMigration.ReadMarkerAsync(storeDirectory, cancellationToken).ConfigureAwait(false);
        if (read.Status != SessionStoreMarkerReadStatus.Present || read.Marker is not { IsComplete: true } marker)
        {
            var status = read.Status == SessionStoreMarkerReadStatus.Missing
                ? SessionStoreMigrationStatus.NotStarted
                : read.Status == SessionStoreMarkerReadStatus.Damaged
                    ? SessionStoreMigrationStatus.MarkerDamaged
                    : SessionStoreMigrationStatus.InProgress;
            throw new SessionStoreMigrationException(
                                                    $"Session store '{Path.GetFullPath(storeDirectory)}' cannot use the business database ({status}).",
                                                    status == SessionStoreMigrationStatus.NotStarted
                                                        ? SessionStoreFailureKind.MigrationRequired
                                                        : SessionStoreFailureKind.Conflict);
        }

        var database     = new SessionStoreDatabase(storeDirectory);
        var availability = await database.GetAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (availability.Status != SessionStoreDatabaseStatus.Available)
            throw MapUnavailable(storeDirectory, availability);

        var connection = await database.OpenExistingForInternalUseAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = await SessionStoreDatabase.ReadStoreIdentityAsync(connection, cancellationToken)
                                                     .ConfigureAwait(false);
            if (identity is null || marker.StoreIdentity is null ||
                !string.Equals(identity, marker.StoreIdentity, StringComparison.Ordinal))
                throw new SessionStoreMigrationException(
                                                        $"Business database '{database.DatabasePath}' does not carry the identity recorded by the migration " +
                                                        $"marker ('{marker.StoreIdentity ?? "(none)"}'); the file was replaced or the marker belongs to another store.",
                                                        SessionStoreFailureKind.Conflict);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     <para>把不可用结果转换为带失败类别的迁移异常。</para>
    ///     <para>Translates an unavailable result into a migration exception carrying a failure category.</para>
    /// </summary>
    public static SessionStoreMigrationException MapUnavailable(
        string storeDirectory, SessionStoreDatabaseAvailability availability)
    {
        var databasePath = SessionStoreDatabase.PathFor(storeDirectory);
        var kind = availability.Status switch
        {
            SessionStoreDatabaseStatus.Missing => SessionStoreFailureKind.Missing,
            SessionStoreDatabaseStatus.Damaged => SessionStoreFailureKind.Damaged,
            SessionStoreDatabaseStatus.Unsupported => SessionStoreFailureKind.Unsupported,
            SessionStoreDatabaseStatus.NeedsUpgrade => SessionStoreFailureKind.Unsupported,
            SessionStoreDatabaseStatus.NeedsSchema => SessionStoreFailureKind.Damaged,
            _ => ClassifyFailure(availability.Error)
        };

        return new SessionStoreMigrationException(
                                                 $"Business database '{databasePath}' is not usable ({availability.Status}): " +
                                                 $"{availability.Detail ?? "no detail"}.",
                                                 kind, availability.Error);
    }

    /// <summary>
    ///     <para>按底层异常分类失败类别；无异常时归为 IO 失败。</para>
    ///     <para>Classifies a failure from its underlying exception, defaulting to IO failure.</para>
    /// </summary>
    public static SessionStoreFailureKind ClassifyFailure(Exception? exception) =>
        exception is null ? SessionStoreFailureKind.IoFailure : SessionStoreErrorClassifier.Classify(exception);
}

/// <summary>
///     <para>把 SQLite 与文件系统异常归类为迁移、备份与权威切换使用的失败类别。</para>
///     <para>Maps SQLite and file-system exceptions onto the failure categories used by migration, backup, and authority switching.</para>
/// </summary>
internal static class SessionStoreErrorClassifier
{
    /// <summary>
    ///     <para>返回异常的失败类别；优先看最内层原因，未知运行期错误按 IO 失败报告。</para>
    ///     <para>Returns the failure category, preferring the innermost cause and reporting unknown runtime errors as IO failures.</para>
    /// </summary>
    public static SessionStoreFailureKind Classify(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            switch (current)
            {
                case Microsoft.Data.Sqlite.SqliteException sqlite :
                    return sqlite.SqliteErrorCode switch
                    {
                        3 or 23 => SessionStoreFailureKind.PermissionDenied, // SQLITE_PERM / SQLITE_AUTH
                        5 or 6 => SessionStoreFailureKind.Locked,            // SQLITE_BUSY / SQLITE_LOCKED
                        8 => SessionStoreFailureKind.ReadOnly,               // SQLITE_READONLY
                        11 or 26 => SessionStoreFailureKind.Damaged,         // SQLITE_CORRUPT / SQLITE_NOTADB
                        19 => SessionStoreFailureKind.Conflict,              // SQLITE_CONSTRAINT
                        10 or 13 or 14 or 18 => SessionStoreFailureKind.IoFailure,
                        _ => SessionStoreFailureKind.IoFailure
                    };
                case FileNotFoundException or DirectoryNotFoundException :
                    return SessionStoreFailureKind.Missing;
                case UnauthorizedAccessException :
                    return SessionStoreFailureKind.PermissionDenied;
                case PathTooLongException or NotSupportedException :
                    return SessionStoreFailureKind.IoFailure;
            }

        return exception is IOException ? SessionStoreFailureKind.IoFailure : SessionStoreFailureKind.IoFailure;
    }
}
