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
///         会话的项目归属事实；随初始身份批次或后续单条追加恰好提交一次，不支持转移或改绑。
///     </para>
///     <para>
///         The session's project ownership fact; committed exactly once inside the initial identity batch or as a
///         later single append, never transferred or rebound.
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
    ///     <para>Validates the project-id shape: 32 lowercase hexadecimal characters, independent of roots, names, and revisions.</para>
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
