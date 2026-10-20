using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         验收报告类型的 source-generated JSON metadata（NativeAOT 约束：不启用反射）。独立于 wire
///         协议 context，并以缩进输出便于人工审阅验收证据。
///     </para>
///     <para>
///         Source-generated JSON metadata for the acceptance report types
///         (NativeAOT constraint: reflection-based serialization stays disabled).
///         It is separate from the wire-protocol context and writes indented output
///         for human review of the acceptance evidence.
///     </para>
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowsSandboxAcceptanceReport))]
[JsonSerializable(typeof(SandboxAcceptanceCaseResult))]
[JsonSerializable(typeof(SandboxAcceptanceComponentSnapshot))]
[JsonSerializable(typeof(SandboxAcceptanceFileInfo))]
[JsonSerializable(typeof(SandboxNetworkObservation))]
internal sealed partial class WindowsSandboxAcceptanceJsonContext : JsonSerializerContext;
