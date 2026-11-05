using TinyHarness.Core.Exceptions;

namespace TinyHarness.Core.Models.Project;

/// <summary>
///     <para>项目目录册文档：显式 schema、目录册修订号和项目列表，是项目名称与目录配置的唯一权威来源。</para>
///     <para>
///         The project-catalog document: explicit schema, catalog revision, and project list; the single
///         authoritative source for project names and root configuration.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         目录册不维护权威的会话成员列表；会话归属由各自 JSONL 日志的绑定记录承载，
///         成员投影从会话记录生成，避免双向归属冲突。
///     </para>
///     <para>
///         The catalog keeps no authoritative session membership; session ownership lives in each JSONL
///         log's binding record and membership is projected from session records, avoiding two-way
///         ownership conflicts.
///     </para>
/// </remarks>
public sealed record ProjectCatalog
{
    /// <summary>
    ///     <para>当前目录册 schema 版本。</para>
    ///     <para>The current catalog schema version.</para>
    /// </summary>
    public const string CurrentSchema = "tinyharness.projects/1";

    /// <summary>
    ///     <para>目录册可容纳的项目数量上限。</para>
    ///     <para>The catalog's project-count budget.</para>
    /// </summary>
    public const int MaxProjects = 1000;

    public required string Schema { get; init; }

    /// <summary>
    ///     <para>目录册修订号；每次成功保存递增。</para>
    ///     <para>The catalog revision; incremented on every successful save.</para>
    /// </summary>
    public required long Revision { get; init; }

    public required IReadOnlyList<Project> Projects { get; init; }

    /// <summary>
    ///     <para>返回无项目的空目录册（schema 为当前版本、修订号 0），供缺失文件的新存储使用。</para>
    ///     <para>
    ///         Returns an empty catalog (current schema, revision zero) used for a fresh store with no
    ///         catalog file yet.
    ///     </para>
    /// </summary>
    public static ProjectCatalog Empty() => new()
    {
        Schema   = CurrentSchema,
        Revision = 0,
        Projects = []
    };

    /// <summary>
    ///     <para>严格校验目录册：schema、修订号、项目数量、显式 null、ID 唯一性与跨项目目录唯一性。</para>
    ///     <para>
    ///         Strictly validates the catalog: schema, revision, project count, explicit nulls, ID
    ///         uniqueness, and cross-project root uniqueness.
    ///     </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">
    ///     <para>版本不支持、超预算、显式 null 或存在重复 ID／目录等歧义；消息包含目录册路径。</para>
    ///     <para>
    ///         An unsupported version, a budget violation, explicit nulls, or ambiguous duplicate IDs or
    ///         roots; messages include the catalog path.
    ///     </para>
    /// </exception>
    public void Validate(string filePath)
    {
        if (Schema is null)
            throw new ProjectStoreException($"Project catalog field 'schema' in '{filePath}' must not be null.");

        if (Schema != CurrentSchema)
            throw new
                ProjectStoreException($"Project catalog '{filePath}' has unsupported schema '{Schema}'; expected '{CurrentSchema}'. Keep the file and upgrade TinyHarness.");

        if (Revision < 0)
            throw new ProjectStoreException($"Project catalog field 'revision' in '{filePath}' must not be negative.");

        if (Projects is null)
            throw new ProjectStoreException($"Project catalog field 'projects' in '{filePath}' must not be null.");

        if (Projects.Count > MaxProjects)
            throw new
                ProjectStoreException($"Project catalog '{filePath}' exceeds its budget of {MaxProjects} projects.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Projects.Count; i++)
        {
            var project = Projects[i];
            if (project is null)
                throw new
                    ProjectStoreException($"Project catalog field 'projects[{i}]' in '{filePath}' must not be null.");

            project.Validate(filePath);
            if (!seenIds.Add(project.Id))
                throw new
                    ProjectStoreException($"Project catalog '{filePath}' contains duplicate project id '{project.Id}'.");

            // 同目录出现多个候选项目即归属歧义，读取端必须报告而不是任选其一。
            for (var j = 0; j < i; j++)
                if (Projects[j].Roots.Any(existing => ProjectRoots.AreEqual(existing.Path, project.Roots[0].Path)))
                    throw new
                        ProjectStoreException($"Project catalog '{filePath}' maps root '{project.Roots[0].Path}' to both project '{Projects[j].Id}' and '{project.Id}'; resolve the ambiguity manually.");
        }
    }

    /// <summary>
    ///     <para>
    ///         按持久化语义比较两个目录册的内容是否一致：修订号相等且项目列表逐位置相等才视为相同，
    ///         schema 不参与比较。
    ///     </para>
    ///     <para>
    ///         Compares two catalogs for content equality under persistence semantics: equal revision and a
    ///         positionally equal project list; the schema does not participate.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         这是面向变更检测（“磁盘目录册 vs 内存已应用列表”）的内容语义比较，不替代 record
    ///         引用相等；schema 不参与是因为 <see cref="ProjectStore.LoadAsync" /> 的严格校验已保证
    ///         读到的 schema 合法，比较只关心内容。项目列表按位置逐项比较，顺序是语义的一部分。
    ///     </para>
    ///     <para>
    ///         This is a content-semantic comparison for change detection (on-disk catalog versus the
    ///         in-memory applied list) and never replaces record reference equality. The schema is excluded
    ///         because the strict validation of <see cref="ProjectStore.LoadAsync" /> already guarantees a
    ///         legal schema on load, so the comparison cares about content only. The project list compares
    ///         positionally, order being part of the meaning.
    ///     </para>
    /// </remarks>
    /// <param name="other">
    ///     <para>比较目标；null 不与任何目录册内容相等。</para>
    ///     <para>The comparison target; null is never content-equal to any catalog.</para>
    /// </param>
    /// <returns>
    ///     <para>修订号与全部项目内容一致时为 true。</para>
    ///     <para>True when the revision and every project's content match.</para>
    /// </returns>
    public bool ContentEquals(ProjectCatalog? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Revision != other.Revision ||
            Projects is null           ||
            other.Projects is null     ||
            Projects.Count != other.Projects.Count)
            return false;

        for (var i = 0; i < Projects.Count; i++)
            if (Projects[i] is not { } project || !project.ContentEquals(other.Projects[i]))
                return false;

        return true;
    }
}
