using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Core.Services.Worker;

/// <summary>
///     worker MCP 输出的 source-generated JSON metadata。状态以字符串形式编码，所有成员静态可见，
///     避免 NativeAOT 下的反射序列化。
///     Source-generated JSON metadata for MCP worker output. Status values are strings and all members
///     are statically visible, avoiding reflection-based serialization under NativeAOT.
/// </summary>
[JsonSerializable(typeof(WorkerResult))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UseStringEnumConverter = true,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class WorkerResultJsonContext : JsonSerializerContext;
