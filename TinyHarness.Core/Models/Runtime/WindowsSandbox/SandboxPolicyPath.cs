using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// permission_profile 条目的路径：special 形式（root/project_roots/tmpdir 等符号路径）或
/// 显式 Windows 路径。序列化形状与 Codex 协议 serde 一致：
/// {"type":"special","value":{"kind":"root"}} 或 {"type":"path","path":"C:\\work"}。
///
/// Path of a permission_profile entry: either a special symbolic path
/// (root/project_roots/tmpdir, …) or an explicit Windows path. The wire shape
/// matches the Codex protocol serde exactly.
/// </summary>
public sealed record SandboxPolicyPath
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxSpecialPathValue? Value { get; init; }

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExplicitPath { get; init; }

    public static SandboxPolicyPath Root() => new() { Type = "special", Value = new SandboxSpecialPathValue { Kind = "root" } };

    public static SandboxPolicyPath ProjectRoots()
        => new() { Type = "special", Value = new SandboxSpecialPathValue { Kind = "project_roots" } };

    public static SandboxPolicyPath ProjectRootsSubpath(string subpath)
        => new() { Type = "special", Value = new SandboxSpecialPathValue { Kind = "project_roots", Subpath = subpath } };

    public static SandboxPolicyPath TmpDir()
        => new() { Type = "special", Value = new SandboxSpecialPathValue { Kind = "tmpdir" } };

    public static SandboxPolicyPath SlashTmp()
        => new() { Type = "special", Value = new SandboxSpecialPathValue { Kind = "slash_tmp" } };

    public static SandboxPolicyPath Directory(string absolutePath)
        => new() { Type = "path", ExplicitPath = absolutePath };
}

/// <summary>
/// special 路径的值对象：kind 加可选 subpath；序列化时 null 字段省略。
///
/// Value object of a special path: a kind plus an optional subpath; null
/// members are omitted on the wire.
/// </summary>
public sealed record SandboxSpecialPathValue
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("subpath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subpath { get; init; }
}
