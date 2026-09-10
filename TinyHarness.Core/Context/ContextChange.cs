namespace TinyHarness.Core.Context;

/// <summary>
/// 一次成功的压缩变更报告：压缩前与压缩后发送给模型的视图估算 token 数，用于 CLI 展示
/// 与审计（PLAN §16）。
///
/// Report of one successful compaction: the estimated tokens of the model view
/// before and after the change, surfaced by the CLI and available for audit (PLAN §16).
/// </summary>
public sealed record ContextChange(int BeforeTokens, int AfterTokens);
