using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     <para>一次已选模型的不可变请求配置和历史连续性边界，不包含客户端或凭据。</para>
///     <para>Immutable request configuration and continuity boundaries for a model selection, without clients or credentials.</para>
/// </summary>
public sealed record SessionModelSelection
{
    public const string CurrentSchema = "tinyharness.model-selection/1";

    public          string               Schema   { get; init; } = CurrentSchema;
    public          long                 Revision { get; init; }
    public required SessionModelIdentity Model    { get; init; }

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

    public ContextOptions?       Context                    { get; init; }
    public ModelReasoningEffort? ReasoningEffort            { get; init; }
    public int                   MessageBoundary            { get; init; }
    public long                  ContinuationEpoch          { get; init; }
    public int                   ReasoningStartMessageIndex { get; init; }

    /// <summary>
    ///     <para>验证可序列化的选择参数；不探测服务或解析凭据。</para>
    ///     <para>Validates serializable selection parameters without probing services or resolving credentials.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != CurrentSchema ||
            Revision <= 0 ||
            Model is null ||
            string.IsNullOrWhiteSpace(Model.ModelId) ||
            string.IsNullOrWhiteSpace(Model.Endpoint) ||
            !Enum.IsDefined(Model.ChatApi) ||
            MessageBoundary < 0 ||
            ContinuationEpoch < 0 ||
            ReasoningStartMessageIndex < 0 ||
            ReasoningStartMessageIndex > MessageBoundary ||
            (CredentialRoute is not null && string.IsNullOrWhiteSpace(CredentialRoute)) ||
            (ReasoningEffort is { } effort && !Enum.IsDefined(effort)))
            throw new
                InvalidDataException("Invalid model selection schema, identity, route, effort or continuity boundary.");
        Context?.Validate();
    }
}
