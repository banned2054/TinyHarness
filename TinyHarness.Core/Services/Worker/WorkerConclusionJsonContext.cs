using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     WorkerConclusionDraft 的 source-generated JSON 契约。反序列化只走静态 metadata，
///     不使用反射，保持 NativeAOT 裁剪安全（PLAN §6/§21）。
///     Source-generated JSON contract for <see cref="WorkerConclusionDraft" />. Deserialization goes
///     through static metadata only — no reflection — so the parse path stays trimming-safe under
///     NativeAOT (PLAN §6/§21).
/// </summary>
[JsonSerializable(typeof(WorkerConclusionDraft))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class WorkerConclusionJsonContext : JsonSerializerContext;
