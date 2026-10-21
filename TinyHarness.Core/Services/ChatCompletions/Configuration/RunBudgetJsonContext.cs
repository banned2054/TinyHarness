using System.Text.Json.Serialization;
using TinyHarness.Core.Models.Agent;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

[JsonSerializable(typeof(RunBudgetLimits))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class RunBudgetJsonContext : JsonSerializerContext;
