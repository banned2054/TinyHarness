using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>一个会话存储目录的置顶项目与会话 ID；项目列表顺序也是置顶项目的显示顺序。</para>
///     <para>The pinned project and session IDs for one session store; project order is also their display order.</para>
/// </summary>
public sealed record SessionStorePins
{
    /// <summary>
    ///     <para>当前置顶状态文件的 schema。</para>
    ///     <para>The current schema for the pin-state file.</para>
    /// </summary>
    public const string CurrentSchema = "tinyharness.session-store-pins/1";

    public const int MaxProjectIds       = 1000;
    public const int MaxSessionIds       = 10_000;
    public const int MaxIdentifierLength = 256;

    /// <summary>
    ///     <para>有序置顶项目 ID；ID 不存在于目录册时保留，以免暂时缺失时丢失用户状态。</para>
    ///     <para>Ordered pinned project IDs; IDs absent from the catalog remain to preserve state through temporary gaps.</para>
    /// </summary>
    public required IReadOnlyList<string> PinnedProjectIds { get; init; }

    /// <summary>
    ///     <para>置顶会话 ID；ID 不存在于当前历史列表时保留。</para>
    ///     <para>Pinned session IDs; IDs absent from the current history list remain stored.</para>
    /// </summary>
    public required IReadOnlyList<string> PinnedSessionIds { get; init; }

    /// <summary>
    ///     <para>是否已导入旧侧栏偏好中的置顶列表。</para>
    ///     <para>Whether pin lists from legacy sidebar preferences have been imported.</para>
    /// </summary>
    public bool LegacySidebarPinsImported { get; init; }

    /// <summary>
    ///     <para>置顶状态文件的 schema 标识。</para>
    ///     <para>The schema identifier of the pin-state file.</para>
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    ///     <para>创建未置顶的空状态。</para>
    ///     <para>Creates an empty state with no pinned items.</para>
    /// </summary>
    public static SessionStorePins Empty() => new()
    {
        Schema                    = CurrentSchema,
        PinnedProjectIds          = [],
        PinnedSessionIds          = [],
        LegacySidebarPinsImported = false
    };

    /// <summary>
    ///     <para>严格校验置顶状态文件的 schema、列表、ID 与条目预算。</para>
    ///     <para>Strictly validates the pin-state schema, lists, IDs, and entry budgets.</para>
    /// </summary>
    /// <exception cref="SessionStorePinException">
    ///     <para>schema 不支持、字段缺失或无效、存在重复 ID 或超出条目预算。</para>
    ///     <para>
    ///         The schema is unsupported, a field is missing or invalid, an ID is duplicated, or an entry budget is
    ///         exceeded.
    ///     </para>
    /// </exception>
    public void Validate(string filePath)
    {
        if (Schema != CurrentSchema)
            throw new SessionStorePinException(
                                               $"Pin state '{filePath}' has unsupported schema '{Schema}'; expected '{CurrentSchema}'. Keep the file and upgrade TinyHarness.");

        ValidateIds(PinnedProjectIds, MaxProjectIds,
                    "pinnedProjectIds", filePath);
        ValidateIds(PinnedSessionIds, MaxSessionIds,
                    "pinnedSessionIds", filePath);
    }

    private static void ValidateIds(IReadOnlyList<string>? values, int maxCount, string field, string filePath)
    {
        if (values is null)
            throw new SessionStorePinException($"Pin state field '{field}' in '{filePath}' must not be null.");

        if (values.Count > maxCount)
            throw new SessionStorePinException(
                                               $"Pin state field '{field}' in '{filePath}' exceeds its budget of {maxCount} entries.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            var id = values[i];
            if (string.IsNullOrWhiteSpace(id))
                throw
                    new SessionStorePinException($"Pin state field '{field}[{i}]' in '{filePath}' must not be blank.");

            if (id.Length > MaxIdentifierLength)
                throw new SessionStorePinException(
                                                   $"Pin state field '{field}[{i}]' in '{filePath}' exceeds its {MaxIdentifierLength}-character budget.");

            if (!ids.Add(id))
                throw new
                    SessionStorePinException($"Pin state field '{field}' in '{filePath}' contains duplicate ID '{id}'.");
        }
    }
}
