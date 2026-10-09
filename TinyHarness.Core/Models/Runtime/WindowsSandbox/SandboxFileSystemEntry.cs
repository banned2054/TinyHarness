using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     restricted 文件系统策略的一条 entry：路径、read/write 访问与可选的缺失跳过行为。
///     One entry of the restricted filesystem policy: a path, read/write access,
///     and an optional skip-if-missing behavior.
/// </summary>
public sealed record SandboxFileSystemEntry
{
    [JsonPropertyName("path")]
    public required SandboxPolicyPath Path { get; init; }

    /// <summary>
    ///     "read" 或 "write"。
    ///     Either "read" or "write".
    /// </summary>
    [JsonPropertyName("access")]
    public required string Access { get; init; }

    [JsonPropertyName("missing_path_behavior")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingPathBehavior { get; init; }

    public static SandboxFileSystemEntry Read(SandboxPolicyPath path, bool skipIfMissing = false)
    {
        return new SandboxFileSystemEntry
        {
            Path                = path,
            Access              = "read",
            MissingPathBehavior = skipIfMissing ? "skip" : null
        };
    }

    public static SandboxFileSystemEntry Write(SandboxPolicyPath path)
    {
        return new SandboxFileSystemEntry { Path = path, Access = "write" };
    }
}
