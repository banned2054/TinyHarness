using System.Text.Json;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>单次 JSONL 追加提交，格式、会话、序号和批次身份均显式保存。</para>
///     <para>A single JSONL append commit with explicit format, session, sequence, and batch identity.</para>
/// </summary>
public sealed record SessionLogBatch
{
    public string                          Format     { get; init; } = string.Empty;
    public string                          SessionId  { get; init; } = string.Empty;
    public long                            Sequence   { get; init; }
    public string                          BatchId    { get; init; } = string.Empty;
    public DateTimeOffset                  WrittenUtc { get; init; }
    public IReadOnlyList<SessionLogRecord> Records    { get; init; } = [];
}

/// <summary>
///     <para>批次内的显式类型记录；ID 由 writer 按会话和序号分配。</para>
///     <para>An explicitly typed record within a batch; the writer assigns its session-scoped sequence ID.</para>
/// </summary>
public sealed record SessionLogRecord(string Id, string Kind, JsonElement Payload);

public sealed record SessionLogIdentity(
    string               Id,
    string               WorkspaceRoot,
    SessionModelIdentity Model,
    DateTimeOffset       CreatedUtc);

/// <summary>
///     <para>
///         会话的初始项目归属事实；随身份批次或后续单条追加提交一次。删除项目时可追加匹配的解除归属，之后不能再绑定项目。
///     </para>
///     <para>
///         The session's initial project-assignment fact, committed once inside the identity batch or as a later
///         single append. Project deletion may append a matching unassignment, after which the session cannot be rebound.
///     </para>
/// </summary>
public sealed record SessionLogProjectBinding(string Schema, string ProjectId)
{
    /// <summary>
    ///     <para>当前归属记录的 payload schema。</para>
    ///     <para>The payload schema of the current ownership record.</para>
    /// </summary>
    public const string SchemaName = "tinyharness.project-binding/1";

    /// <summary>
    ///     <para>校验项目 ID 形态：32 位小写十六进制字符；与目录、名称和修订号无关。</para>
    ///     <para>
    ///         Validates the project-id shape: 32 lowercase hexadecimal characters, independent of roots, names, and
    ///         revisions.
    ///     </para>
    /// </summary>
    internal static bool IsValidProjectId(string id) =>
        id.Length == 32 && id.All(char.IsAsciiHexDigit) && !id.Any(char.IsUpper);

    /// <summary>
    ///     <para>
    ///         严格解析并校验 project-binding 载荷，返回项目 ID；载荷必须恰含 schema 与 projectId
    ///         两个字符串属性（不得缺失、多余或重复），schema 精确匹配且 ID 为 32 位小写十六进制。
    ///     </para>
    ///     <para>
    ///         Strictly parses and validates a project-binding payload and returns the project id; the payload must
    ///         contain exactly the string properties schema and projectId (none missing, extra, or duplicated),
    ///         with the exact schema and a 32-character lowercase hexadecimal id.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>校验通过的项目 ID。</para>
    ///     <para>The validated project id.</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    ///     <para>载荷不是对象，属性缺失、多余、重复或类型不符，schema 不匹配，或 ID 形态非法。</para>
    ///     <para>
    ///         The payload is not an object, a property is missing, extra, duplicated, or mistyped, the schema does
    ///         not match, or the id is malformed.
    ///     </para>
    /// </exception>
    internal static string Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A project-binding payload must be an object.");
        string? schema    = null;
        string? projectId = null;
        foreach (var property in payload.EnumerateObject())
            switch (property.Name)
            {
                case "schema" :
                    if (schema is not null)
                        throw new InvalidDataException("A project-binding payload repeats its 'schema' property.");
                    schema = ReadStringProperty(property);
                    break;
                case "projectId" :
                    if (projectId is not null)
                        throw new InvalidDataException("A project-binding payload repeats its 'projectId' property.");
                    projectId = ReadStringProperty(property);
                    break;
                default :
                    throw new
                        InvalidDataException($"A project-binding payload must not contain the unknown property '{property.Name}'.");
            }

        if (schema is null || projectId is null)
            throw new InvalidDataException("A project-binding payload must contain 'schema' and 'projectId'.");
        if (schema != SchemaName)
            throw new InvalidDataException($"A project-binding payload must use schema '{SchemaName}'.");
        if (!IsValidProjectId(projectId))
            throw new
                InvalidDataException("A project-binding payload must carry a 32-character lowercase hexadecimal project id.");
        return projectId;

        static string ReadStringProperty(JsonProperty property)
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"A project-binding property '{property.Name}' must be a string.");
            return property.Value.GetString()!;
        }
    }
}

/// <summary>
///     <para>标记会话明确选择无项目；与旧日志缺少归属记录的待整理状态区分。</para>
///     <para>Marks an explicit no-project choice, distinct from legacy logs that are pending ownership initialization.</para>
/// </summary>
public sealed record SessionLogProjectAssignment(string Schema, string State)
{
    public const string SchemaName = "tinyharness.project-assignment/1";
    public const string NoProject  = "no-project";

    /// <summary>
    ///     <para>严格校验无项目归属记录。</para>
    ///     <para>Strictly validates a no-project assignment record.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     <para>载荷字段、schema 或状态不符合当前协议。</para>
    ///     <para>The payload fields, schema, or state do not match the current protocol.</para>
    /// </exception>
    internal static void ValidateNoProject(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A project-assignment payload must be an object.");
        string? schema = null;
        string? state  = null;
        foreach (var property in payload.EnumerateObject())
            switch (property.Name)
            {
                case "schema" :
                    if (schema is not null)
                        throw new InvalidDataException("A project-assignment payload repeats its 'schema' property.");
                    schema = ReadStringProperty(property);
                    break;
                case "state" :
                    if (state is not null)
                        throw new InvalidDataException("A project-assignment payload repeats its 'state' property.");
                    state = ReadStringProperty(property);
                    break;
                default :
                    throw new InvalidDataException(
                                                   $"A project-assignment payload must not contain the unknown property '{property.Name}'.");
            }

        if (schema != SchemaName || state != NoProject)
            throw new
                InvalidDataException("A project-assignment payload must identify the 'no-project' state and supported schema.");

        static string ReadStringProperty(JsonProperty property)
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"A project-assignment property '{property.Name}' must be a string.");
            return property.Value.GetString()!;
        }
    }
}

/// <summary>
///     <para>解除会话项目归属的日志事实；仅将项目归属转为明确无项目，不改变历史或执行目录。</para>
///     <para>
///         A journal fact that removes project ownership by transitioning the assignment to explicit no-project,
///         preserving history and the workspace root.
///     </para>
/// </summary>
/// <param name="Schema">
///     <para>解除归属载荷 schema。</para>
///     <para>The unassignment payload schema.</para>
/// </param>
/// <param name="ProjectId">
///     <para>必须与日志当前归属匹配的项目 ID。</para>
///     <para>The project id that must match the journal's current assignment.</para>
/// </param>
public sealed record SessionLogProjectUnassignment(string Schema, string ProjectId)
{
    /// <summary>
    ///     <para>当前解除归属载荷 schema。</para>
    ///     <para>The current unassignment payload schema.</para>
    /// </summary>
    public const string SchemaName = "tinyharness.project-unassignment/1";

    /// <summary>
    ///     <para>严格解析解除归属载荷并验证项目 ID。</para>
    ///     <para>Strictly parses an unassignment payload and validates its project id.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     <para>载荷字段、schema 或项目 ID 不符合当前协议。</para>
    ///     <para>The payload fields, schema, or project id do not match the current protocol.</para>
    /// </exception>
    internal static string Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A project-unassignment payload must be an object.");

        string? schema    = null;
        string? projectId = null;
        foreach (var property in payload.EnumerateObject())
            switch (property.Name)
            {
                case "schema" :
                    if (schema is not null)
                        throw new InvalidDataException("A project-unassignment payload repeats its 'schema' property.");
                    schema = ReadStringProperty(property);
                    break;
                case "projectId" :
                    if (projectId is not null)
                        throw new
                            InvalidDataException("A project-unassignment payload repeats its 'projectId' property.");
                    projectId = ReadStringProperty(property);
                    break;
                default :
                    throw new InvalidDataException(
                                                   $"A project-unassignment payload must not contain the unknown property '{property.Name}'.");
            }

        if (schema != SchemaName || projectId is null || !SessionLogProjectBinding.IsValidProjectId(projectId))
            throw new InvalidDataException(
                                           "A project-unassignment payload must contain the supported schema and a 32-character lowercase project id.");

        return projectId;

        static string ReadStringProperty(JsonProperty property)
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                                               $"A project-unassignment property '{property.Name}' must be a string.");
            return property.Value.GetString()!;
        }
    }
}

public sealed record SessionLogTurn(string Id, string UserInput, DateTimeOffset CreatedUtc);

public sealed record SessionLogRun(string TurnId, PersistedRun Run);

public sealed record SessionLogMessage(PersistedMessage Message);

public sealed record SessionLogCheckpoint(
    CompactionState? State,
    bool             HasState,
    int              FoldStartMessageIndex,
    int              FoldEndMessageIndex,
    long?            ModelSelectionRevision = null);

public sealed record SessionLogSessionState(
    DateTimeOffset? InspectionAcknowledgedUtc,
    string?         PendingInspectionReason,
    bool            ExecutionIntent = false);
