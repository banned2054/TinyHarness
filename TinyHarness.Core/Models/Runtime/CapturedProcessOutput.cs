namespace TinyHarness.Core.Models.Runtime;

/// <summary>
/// 单个进程输出流的有界视图及完整 artifact 元数据。
/// Bounded model-facing view of one process output stream plus metadata of the full artifact.
/// </summary>
public sealed record CapturedProcessOutput(
    string  Content,
    bool    Truncated,
    long    OriginalCharacterCount,
    string? ArtifactPath);
