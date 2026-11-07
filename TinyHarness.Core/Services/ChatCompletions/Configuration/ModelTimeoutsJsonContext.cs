using System.Text.Json.Serialization;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Core.Services.ChatCompletions.Configuration;

[JsonSerializable(typeof(ModelTimeouts))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class ModelTimeoutsJsonContext : JsonSerializerContext;
