using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Context;

namespace TinyHarness.Core.Services.Context;

/// <summary>
/// StructuredState 的 source-generated JSON 契约。序列化与反序列化都走静态 metadata，
/// 保持 NativeAOT 裁剪安全（PLAN §6/§21）。
///
/// Source-generated JSON contract for <see cref="TinyHarness.Core.Models.Context.StructuredState"/>. Both
/// serialization directions use static metadata so the compaction path stays
/// trimming-safe under NativeAOT (PLAN §6/§21).
/// </summary>
[JsonSerializable(typeof(StructuredState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class StructuredStateJsonContext : JsonSerializerContext;
