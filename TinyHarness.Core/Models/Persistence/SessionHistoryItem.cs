using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>会话历史与实时输出共用的本地展示条目；正文与工具内容保留原文，不包含任意 object 载荷。</para>
///     <para>A local display item shared by stored history and live output, preserving text and tool content without arbitrary object payloads.</para>
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

    /// <summary>
    ///     <para>助手的可读推理原文；与正文分开展示，不含 opaque 载荷，旧日志缺失时为空。</para>
    ///     <para>Readable assistant reasoning as received, shown apart from the body without opaque payloads; null when absent in older logs.</para>
    /// </summary>
    public string? Reasoning { get; init; }

    public string? CallId { get; init; }

    /// <summary>
    ///     <para>未脱敏的本次运行调用序号；旧历史缺失时按脱敏 CallId 关联。</para>
    ///     <para>The unredacted per-run invocation ordinal; older history without it correlates by redacted CallId.</para>
    /// </summary>
    public string? InvocationId { get; init; }

    public string? ToolName      { get; init; }
    public string? ArgumentsJson { get; init; }
    public string? Capability    { get; init; }
    public string? Summary       { get; init; }

    public IReadOnlyList<string>? TargetPaths { get; init; }

    public string? ExecutionPolicy  { get; init; }
    public string? Approval         { get; init; }
    public string? PreparationError { get; init; }

    /// <summary>
    ///     <para>工具未开始的独立原因，不与准备错误或授权拒绝混淆。</para>
    ///     <para>The separate reason a tool did not start, distinct from preparation errors and permission denials.</para>
    /// </summary>
    public string? NotStartedReason { get; init; }

    public string? State           { get; init; }
    public bool?   Succeeded       { get; init; }
    public int?    ExitCode        { get; init; }
    public bool    TimedOut        { get; init; }
    public bool    OutputTruncated { get; init; }
    public string? Stdout          { get; init; }
    public string? Stderr          { get; init; }

    public IReadOnlyList<PatchChange>? PatchChanges { get; init; }

    public bool    OutputIncomplete { get; init; }
    public string? OutputWarning    { get; init; }
}

/// <summary>
///     <para>历史展示条目的稳定种类。</para>
///     <para>Stable kinds of session history display items.</para>
/// </summary>
public static class SessionHistoryKind
{
    public const string User                = "user";
    public const string Process             = "process";
    public const string Final               = "final";
    public const string Tool                = "tool";
    public const string ProjectInstructions = "project-instructions";
}

[JsonSerializable(typeof(SessionHistoryItem))]
[JsonSerializable(typeof(PatchChange))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class SessionHistoryJsonContext : JsonSerializerContext;
