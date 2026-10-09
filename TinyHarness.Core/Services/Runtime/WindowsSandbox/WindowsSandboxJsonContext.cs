using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
/// Windows 沙箱 wire 类型的 source-generated JSON metadata（NativeAOT 约束：不启用反射）。
///
/// Source-generated JSON metadata for the Windows sandbox wire types
/// (NativeAOT constraint: reflection-based serialization stays disabled).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SandboxSetupPayload))]
[JsonSerializable(typeof(SandboxSetupMarker))]
[JsonSerializable(typeof(SandboxSetupErrorReport))]
[JsonSerializable(typeof(SandboxUsersFile))]
[JsonSerializable(typeof(SandboxCapabilitySids))]
[JsonSerializable(typeof(RunnerSpawnRequest))]
[JsonSerializable(typeof(RunnerSpawnReady))]
[JsonSerializable(typeof(RunnerOutputPayload))]
[JsonSerializable(typeof(RunnerExitPayload))]
[JsonSerializable(typeof(RunnerErrorPayload))]
internal sealed partial class WindowsSandboxJsonContext : JsonSerializerContext;
