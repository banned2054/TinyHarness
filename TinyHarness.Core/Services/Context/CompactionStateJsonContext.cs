using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Services.Context;

/// <summary>
///     <para>压缩检查点的静态 JSON 元数据，保持 NativeAOT 安全。</para>
///     <para>Static JSON metadata for compaction checkpoints, preserving NativeAOT safety.</para>
/// </summary>
[JsonSerializable(typeof(CompactionState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class CompactionStateJsonContext : JsonSerializerContext;
