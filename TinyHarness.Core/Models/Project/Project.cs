using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Models.Project;

/// <summary>
///     <para>具有稳定身份的项目：随机 ID、可修改显示名称、关联目录、显式主目录与配置修订号。</para>
///     <para>
///         A project with a stable identity: random ID, user-editable display name, associated roots,
///         an explicit primary root, and a configuration revision.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         ID 不由路径或名称计算，可靠保存后保持稳定；名称与路径不是身份，改名不改变执行环境。
///         目录配置只作为新会话默认值，不构成写入授权；第一版只接受一个 root，
///         列表结构为多目录扩展保留。
///     </para>
///     <para>
///         The ID is never derived from the path or name and stays stable once reliably saved; name and
///         path are not identity, and a rename does not change the execution environment. Root
///         configuration only defaults new sessions and grants no write authorization. This version
///         accepts exactly one root; the list shape is reserved for multi-root expansion.
///     </para>
/// </remarks>
public sealed record Project
{
    /// <summary>
    ///     <para>显示名称的字符数上限。</para>
    ///     <para>The display-name character budget.</para>
    /// </summary>
    public const int MaxNameLength = 200;

    /// <summary>
    ///     <para>单个目录路径的字符数上限。</para>
    ///     <para>The per-root path character budget.</para>
    /// </summary>
    public const int MaxRootPathLength = 2048;

    /// <summary>
    ///     <para>当前版本允许的关联目录数量；多目录执行另行实施。</para>
    ///     <para>Root count allowed by the current version; multi-root execution is a separate effort.</para>
    /// </summary>
    public const int MaxRootCount = 1;

    /// <summary>
    ///     <para>稳定的随机项目 ID；32 位小写十六进制，与目录和名称无关。</para>
    ///     <para>The stable random project ID; 32 lowercase hex characters, independent of path and name.</para>
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     <para>用户可修改的显示名称；不是项目身份。</para>
    ///     <para>The user-editable display name; not the project identity.</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     <para>有序的关联目录列表；按路径比较规则去重，作为新会话的默认目录。</para>
    ///     <para>The ordered associated roots, deduplicated by path comparison, used to default new sessions.</para>
    /// </summary>
    public required IReadOnlyList<ProjectRoot> Roots { get; init; }

    /// <summary>
    ///     <para>显式保存的主目录；必须属于 <see cref="Roots" />，调整顺序不改变主目录。</para>
    ///     <para>
    ///         The explicitly saved primary root; it must be one of <see cref="Roots" />, and reordering
    ///         never moves it.
    ///     </para>
    /// </summary>
    public required string PrimaryRoot { get; init; }

    /// <summary>
    ///     <para>项目配置修订号；从 1 开始，每次改名递增，用于过期编辑冲突判断。</para>
    ///     <para>
    ///         The project configuration revision; starts at one and increments on each rename, used to
    ///         reject stale edits.
    ///     </para>
    /// </summary>
    public required long Revision { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>
    ///     <para>从单个目录创建新项目：生成随机 ID、按目录取默认名称、主目录即该目录，修订号为 1。</para>
    ///     <para>
    ///         Creates a new project from one root: random ID, default name from the directory, the root
    ///         itself as primary root, and revision one.
    ///     </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">
    ///     <para>目录非法、超长，或默认名称超出预算。</para>
    ///     <para>The root is invalid or over budget, or the default name exceeds its budget.</para>
    /// </exception>
    public static Project CreateNew(string workspaceRoot, DateTimeOffset utcNow)
    {
        var normalized = ProjectRoots.Normalize(workspaceRoot);
        if (normalized.Length > MaxRootPathLength)
            throw new
                ProjectStoreException($"Project root '{normalized}' exceeds its budget of {MaxRootPathLength} characters.");

        return new Project
        {
            Id          = Guid.NewGuid().ToString("N"),
            Name        = ValidateName(ProjectRoots.DefaultDisplayName(normalized)),
            Roots       = [new ProjectRoot(normalized)],
            PrimaryRoot = normalized,
            Revision    = 1,
            CreatedUtc  = utcNow,
            UpdatedUtc  = utcNow
        };
    }

    /// <summary>
    ///     <para>校验并返回规范化显示名称：去除首尾空白、非空、不超预算且不含控制字符。</para>
    ///     <para>
    ///         Validates and returns the normalized display name: trimmed, non-empty, within budget, and
    ///         free of control characters.
    ///     </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">
    ///     <para>名称为空白、超预算或包含控制字符。</para>
    ///     <para>The name is blank, over budget, or contains control characters.</para>
    /// </exception>
    public static string ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
            throw new
                ProjectStoreException($"Project name exceeds its budget of {MaxNameLength} characters.");

        if (trimmed.Any(char.IsControl))
            throw new ProjectStoreException("Project name must not contain control characters.");

        return trimmed;
    }

    /// <summary>
    ///     <para>严格校验从目录册读取的项目：ID 规范、名称规范化、root 数量与形态、主目录归属和修订号。</para>
    ///     <para>
    ///         Strictly validates a project read from the catalog: ID shape, normalized name, root count and
    ///         form, primary-root membership, and revision.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         显式 null 不受 required 检查保护；每个可空字段先行拒绝，消息包含目录册路径与字段位置。
    ///     </para>
    ///     <para>
    ///         Explicit nulls bypass required-member checks; every nullable field is rejected up front with
    ///         the catalog path and field location in the message.
    ///     </para>
    /// </remarks>
    /// <exception cref="ProjectStoreException">
    ///     <para>任一不变量被破坏或字段为显式 null；消息包含目录册路径与字段位置。</para>
    ///     <para>Any invariant is broken or a field is explicitly null; messages include the catalog path and field location.</para>
    /// </exception>
    public void Validate(string filePath)
    {
        if (Id is null)
            throw new ProjectStoreException($"Project field 'id' in '{filePath}' must not be null.");

        if (Id.Length != 32 || !Id.All(char.IsAsciiHexDigit) || Id.Any(char.IsUpper))
            throw new
                ProjectStoreException($"Project field 'id' in '{filePath}' must be a 32-character lowercase hexadecimal identifier.");

        if (Name is null)
            throw new ProjectStoreException($"Project field 'name' in '{filePath}' must not be null.");

        if (Name.Length == 0 ||
            Name != Name.Trim() ||
            Name.Length > MaxNameLength ||
            Name.Any(char.IsControl))
            throw new
                ProjectStoreException($"Project field 'name' in '{filePath}' must be a trimmed, non-empty name of at most {MaxNameLength} characters without control characters.");

        if (Roots is null)
            throw new ProjectStoreException($"Project field 'roots' in '{filePath}' must not be null.");

        // 当前版本读到多目录配置时明确报告，不能只取第一项启动。
        if (Roots.Count != 1)
            throw new
                ProjectStoreException($"Project field 'roots' in '{filePath}' must contain exactly one root; this version does not support multi-root projects.");

        for (var i = 0; i < Roots.Count; i++)
        {
            var root = Roots[i];
            if (root is null)
                throw new ProjectStoreException($"Project field 'roots[{i}]' in '{filePath}' must not be null.");

            if (root.Path is null)
                throw new ProjectStoreException($"Project field 'roots[{i}].path' in '{filePath}' must not be null.");

            if (string.IsNullOrWhiteSpace(root.Path))
                throw new
                    ProjectStoreException($"Project field 'roots[{i}].path' in '{filePath}' is required and must be a non-empty path.");

            if (root.Path.Length > MaxRootPathLength)
                throw new
                    ProjectStoreException($"Project field 'roots[{i}].path' in '{filePath}' exceeds its budget of {MaxRootPathLength} characters.");

            var normalized = ProjectRoots.Normalize(root.Path);
            if (!string.Equals(normalized, root.Path, StringComparison.Ordinal))
                throw new
                    ProjectStoreException($"Project field 'roots[{i}].path' in '{filePath}' must be stored in normalized absolute form ('{normalized}').");
        }

        // primaryRoot 为 null 时归属计数为 0，由下方校验拒绝，不触发空引用。
        if (Roots.Count(root => ProjectRoots.AreEqual(root.Path, PrimaryRoot)) != 1)
            throw new
                ProjectStoreException($"Project field 'primaryRoot' in '{filePath}' must match exactly one entry of 'roots'.");

        if (Revision < 1)
            throw new ProjectStoreException($"Project field 'revision' in '{filePath}' must be a positive revision.");
    }
}
