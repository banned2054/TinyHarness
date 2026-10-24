using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>一次已选模型的不可变请求配置、能力与策略版本快照及历史连续性边界，不包含客户端或凭据。</para>
///     <para>Immutable request configuration, capability and policy-version snapshots with continuity boundaries for a model selection, without clients or credentials.</para>
/// </summary>
/// <remarks>
///     <para>
///         选择修订链显式记录前一修订与选择时间；强度档位按记录时的能力快照验证，当前配置是否仍支持是发送前的独立检查，
///         当前能力变化不能把有效历史判成损坏。profile 提示只用于重新定位可信配置和展示，不是协议身份。
///         投影与强度映射版本固定按哪套规则解释记录；未知 schema、投影或映射版本按契约拒绝续聊。
///     </para>
///     <para>
///         Selection revisions explicitly record their predecessor and selection time; effort levels validate against
///         the capability snapshot recorded with them, while current-config support is a separate pre-send check — a
///         later capability change never invalidates valid history. The profile hint only relocates trusted
///         configuration and drives display; it is not protocol identity. Projection and effort-mapping versions pin
///         the rules used to interpret a record; unknown schema, projection, or mapping versions refuse continuation
///         by contract.
///     </para>
/// </remarks>
public sealed record SessionModelSelection
{
    /// <summary>当前选择记录契约。The current selection-record contract.</summary>
    public const string CurrentSchema = "tinyharness.model-selection/2";

    /// <summary>
    ///     <para>
    ///         旧版 /1 记录：缺 profile 提示、能力快照、前一修订、选择时间与策略版本；可读取并续聊，
    ///         缺失信息保持未知，首次重复应用同策略选择时按 /2 追加固化记录。
    ///     </para>
    ///     <para>
    ///         Legacy /1 records: no profile hint, capability snapshot, predecessor revision, selection time, or
    ///         policy versions; they remain readable and resumable with their missing information kept unknown, and
    ///         the first re-application of the same policy appends a /2 record to materialize it.
    ///     </para>
    /// </summary>
    public const string LegacySchemaV1 = "tinyharness.model-selection/1";

    /// <summary>当前历史投影策略版本：跨模型请求视图、连续性区间与 opaque 隔离规则。The current history-projection policy version.</summary>
    public const string CurrentProjectionVersion = "tinyharness.history-projection/1";

    public          string               Schema   { get; init; } = CurrentSchema;
    public long   Revision { get; init; }

    /// <summary>
    ///     <para>前一选择修订；首份记录引用隐式修订 0。/2 必填并由写入端、读取端共同验证前驱一致。</para>
    ///     <para>The previous selection revision; the first record references implicit revision zero. Required on /2 and validated for predecessor consistency by both writer and reader.</para>
    /// </summary>
    public long? PreviousRevision { get; init; }

    /// <summary>
    ///     <para>选择发生的时间；/2 必填。不与其他时间字段互换语义。</para>
    ///     <para>When the selection was made; required on /2 and not interchangeable with other timestamps.</para>
    /// </summary>
    public DateTimeOffset? SelectedUtc { get; init; }

    public required SessionModelIdentity Model { get; init; }

    /// <summary>
    ///     <para>
    ///         产生本选择的 profile 名，仅供重新定位可信配置与展示；null 表示未知（如旧记录或宿主种子）。
    ///         不得据 profile 名推断 Endpoint、协议或凭据路由。
    ///     </para>
    ///     <para>
    ///         The profile name that produced this selection, used only to relocate trusted configuration and for
    ///         display; null means unknown (legacy records or host seeds). Endpoint, protocol, or credential routes
    ///         are never inferred from the profile name.
    ///     </para>
    /// </summary>
    public string? ProfileHint { get; init; }

    /// <summary>
    ///     <para>
    ///         产生本选择的凭据路由标识（profile 与密钥引用来源）；null 表示未跟踪，如历史日志或草稿。
    ///         只含引用，不含密钥值或指纹；同一路由重选保留连续性，路由变化开启新 epoch。
    ///     </para>
    ///     <para>
    ///         The credential route that produced this selection (profile and key-reference source); null means
    ///         untracked, e.g. legacy logs or drafts. References only, never secret values or fingerprints;
    ///         re-selecting the same route keeps continuity while a route change opens a new epoch.
    ///     </para>
    /// </summary>
    public string? CredentialRoute { get; init; }

    public ContextOptions?       Context         { get; init; }
    public ModelReasoningEffort? ReasoningEffort { get; init; }

    /// <summary>
    ///     <para>
    ///         选择时该模型声明的能力快照；null 表示未声明或未知（/1 升级记录、宿主种子）。
    ///         显式档位且快照已知时必须属于该集合；历史校验不依赖后来变化的 profile。
    ///     </para>
    ///     <para>
    ///         The capability snapshot declared by the model at selection time; null means undeclared or unknown
    ///         (/1 upgrade records, host seeds). An explicit effort level must belong to a known snapshot; historical
    ///         validation never depends on later profile changes.
    ///     </para>
    /// </summary>
    public ModelReasoningEffort[]? ReasoningCapabilities { get; init; }

    /// <summary>
    ///     <para>本选择的强度请求映射版本；/2 必填且须为已知实现，未知版本拒绝续聊。</para>
    ///     <para>The effort request-mapping version for this selection; required and known on /2, unknown versions refuse continuation.</para>
    /// </summary>
    public string? ReasoningMappingVersion { get; init; }

    /// <summary>
    ///     <para>按哪套规则构造目标历史视图（请求投影、预算与 opaque 隔离）；/2 必填且须为已知版本。</para>
    ///     <para>Which rules build the target history view (request projection, budgeting, and opaque isolation); required and known on /2.</para>
    /// </summary>
    public string? ProjectionVersion { get; init; }

    public int  MessageBoundary            { get; init; }
    public long ContinuationEpoch          { get; init; }
    public int  ReasoningStartMessageIndex { get; init; }

    /// <summary>是否为旧版 /1 记录。Whether this is a legacy /1 record.</summary>
    public bool IsLegacySchema => Schema == LegacySchemaV1;

    /// <summary>
    ///     <para>验证可序列化的选择参数；不探测服务或解析凭据。</para>
    ///     <para>Validates serializable selection parameters without probing services or resolving credentials.</para>
    /// </summary>
    public void Validate()
    {
        if (Revision <= 0 ||
            Model is null ||
            string.IsNullOrWhiteSpace(Model.ModelId) ||
            string.IsNullOrWhiteSpace(Model.Endpoint) ||
            !Enum.IsDefined(Model.ChatApi) ||
            MessageBoundary < 0 ||
            ContinuationEpoch < 0 ||
            ReasoningStartMessageIndex < 0 ||
            ReasoningStartMessageIndex > MessageBoundary ||
            (CredentialRoute is not null && string.IsNullOrWhiteSpace(CredentialRoute)) ||
            (ProfileHint is not null && string.IsNullOrWhiteSpace(ProfileHint)) ||
            (ReasoningEffort is { } effort && !Enum.IsDefined(effort)))
            throw new
                InvalidDataException("Invalid model selection schema, identity, route, effort or continuity boundary.");

        switch (Schema)
        {
            case CurrentSchema :
                // /2 必须自带完整策略版本与前驱；能力快照允许未知，但已知快照须包含显式档位。
                if (PreviousRevision is null || PreviousRevision < 0 ||
                    SelectedUtc is null ||
                    !ReasoningEffortMappings.IsKnown(ReasoningMappingVersion) ||
                    !string.Equals(ProjectionVersion, CurrentProjectionVersion, StringComparison.Ordinal) ||
                    (ReasoningEffort is { } declared &&
                     ReasoningCapabilities is { } capabilities &&
                     !capabilities.Contains(declared)))
                    throw new
                        InvalidDataException($"Model selection revision {Revision} does not satisfy the " +
                                             $"'{CurrentSchema}' contract: the previous revision, selection time, " +
                                             "effort-mapping and projection versions must be present and known, and an " +
                                             "explicit effort must belong to the recorded capability snapshot.");
                break;
            case LegacySchemaV1 :
                // /1 未记录策略版本与能力快照：缺失信息保持未知，不做能力校验，也不得补造默认值。
                break;
            default :
                throw new
                    InvalidDataException($"Unknown model selection schema '{Schema}'; this version refuses to continue " +
                                         "the session instead of reinterpreting the record with the current implementation.");
        }

        Context?.Validate();
    }
}
