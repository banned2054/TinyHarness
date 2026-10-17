using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>会话历史与实时输出共用的脱敏展示条目，不包含任意 object 载荷。</para>
///     <para>A redacted display item shared by stored history and live output, with no arbitrary object payloads.</para>
/// </summary>
public sealed record SessionHistoryItem
{
    public required string  Id        { get; init; }
    public          long    Sequence  { get; init; }
    public required string  SessionId { get; init; }
    public required string  TurnId    { get; init; }
    public          string? RunId     { get; init; }
    public required string  Kind      { get; init; }
    public          string? Text      { get; init; }
    public          string? CallId    { get; init; }

    /// <summary>
    ///     <para>未脱敏的本次运行调用序号；旧历史缺失时按脱敏 CallId 关联。</para>
    ///     <para>The unredacted per-run invocation ordinal; older history without it correlates by redacted CallId.</para>
    /// </summary>
    public string? InvocationId { get; init; }

    public string?                     ToolName         { get; init; }
    public string?                     ArgumentsJson    { get; init; }
    public string?                     Capability       { get; init; }
    public string?                     Summary          { get; init; }
    public IReadOnlyList<string>?      TargetPaths      { get; init; }
    public string?                     ExecutionPolicy  { get; init; }
    public string?                     Approval         { get; init; }
    public string?                     PreparationError { get; init; }
    public string?                     State            { get; init; }
    public bool?                       Succeeded        { get; init; }
    public int?                        ExitCode         { get; init; }
    public bool                        TimedOut         { get; init; }
    public bool                        OutputTruncated  { get; init; }
    public string?                     Stdout           { get; init; }
    public string?                     Stderr           { get; init; }
    public IReadOnlyList<PatchChange>? PatchChanges     { get; init; }
    public bool                        OutputIncomplete { get; init; }
    public string?                     OutputWarning    { get; init; }
}

/// <summary>
///     <para>历史展示条目的稳定种类。</para>
///     <para>Stable kinds of session history display items.</para>
/// </summary>
public static class SessionHistoryKind
{
    public const string User    = "user";
    public const string Process = "process";
    public const string Final   = "final";
    public const string Tool    = "tool";
}

[JsonSerializable(typeof(SessionHistoryItem))]
[JsonSerializable(typeof(PatchChange))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionHistoryJsonContext : JsonSerializerContext;
