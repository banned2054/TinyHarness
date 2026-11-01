using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>一个 session store 中已归档的会话 ID；会话历史本身仍保留在各自日志中。</para>
///     <para>Archived session IDs for one session store; conversation history remains in each session log.</para>
/// </summary>
public sealed record SessionStoreArchives
{
    public const string CurrentSchema       = "tinyharness.session-store-archives/1";
    public const int    MaxSessionIds       = 10_000;
    public const int    MaxIdentifierLength = 256;

    public required string                Schema             { get; init; }
    public required IReadOnlyList<string> ArchivedSessionIds { get; init; }

    /// <summary>
    ///     <para>创建没有归档会话的空状态。</para>
    ///     <para>Creates an empty state with no archived sessions.</para>
    /// </summary>
    public static SessionStoreArchives Empty() => new()
    {
        Schema             = CurrentSchema,
        ArchivedSessionIds = []
    };

    /// <summary>
    ///     <para>
    ///         校验 schema、归档列表、32 位十六进制会话 ID 与条目预算。
    ///     </para>
    ///     <para>
    ///         Validates the schema, archived-session list, 32-character hexadecimal session IDs, and entry budget.
    ///     </para>
    /// </summary>
    /// <exception cref="SessionStoreArchiveException">
    ///     <para>归档状态损坏、版本不支持或超出预算。</para>
    ///     <para>The archive state is damaged, uses an unsupported version, or exceeds its budget.</para>
    /// </exception>
    public void Validate(string filePath)
    {
        if (Schema != CurrentSchema)
            throw new SessionStoreArchiveException(
                                                   $"Archive state '{filePath}' has unsupported schema '{Schema}'; expected '{CurrentSchema}'. Keep the file and upgrade TinyHarness.");

        if (ArchivedSessionIds is null)
            throw new SessionStoreArchiveException(
                                                   $"Archive state field 'archivedSessionIds' in '{filePath}' must not be null.");
        if (ArchivedSessionIds.Count > MaxSessionIds)
            throw new SessionStoreArchiveException(
                                                   $"Archive state field 'archivedSessionIds' in '{filePath}' exceeds its budget of {MaxSessionIds} entries.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < ArchivedSessionIds.Count; i++)
        {
            var id = ArchivedSessionIds[i];
            if (string.IsNullOrWhiteSpace(id) || id.Length != 32 || !id.All(char.IsAsciiHexDigit))
                throw new SessionStoreArchiveException(
                                                       $"Archive state field 'archivedSessionIds[{i}]' in '{filePath}' " +
                                                       "must be a 32-character hexadecimal session ID.");
            if (!ids.Add(id))
                throw new SessionStoreArchiveException(
                                                       $"Archive state field 'archivedSessionIds' in '{filePath}' contains duplicate ID '{id}'.");
        }
    }
}
