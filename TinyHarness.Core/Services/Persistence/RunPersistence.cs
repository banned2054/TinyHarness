using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Core.Services.Persistence;

public interface IRunRecorder
{
    string? DisplayWarning => null;

    Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken);

    Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>在继续请求模型前记录已知未启动的调用；默认不执行操作。</para>
    ///     <para>Records a known-not-started invocation before the next model request; the default is a no-op.</para>
    /// </summary>
    Task RecordNotStartedAsync(ToolCallRecord record, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task RecordPermissionAsync(
        ToolPreparation preparation, PermissionDecision decision, string outcome, CancellationToken cancellationToken);

    Task RecordResultAsync(ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken);

    Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken);

    Task RecordModelUsageAsync(
        int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken);

    /// <summary>
    ///     <para>记录模型完成拼装的 assistant 正文与工具调用，供实时与历史展示共用。</para>
    ///     <para>Records the assembled assistant text and tool calls for shared live and historical display.</para>
    /// </summary>
    Task RecordAssistantAsync(ChatMessage assistantMessage, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                       CancellationToken cancellationToken);
}

internal sealed class SecretRedactor
{
    private static readonly Regex SecretAssignment =
        new("(?<name>api[-_ ]?key|access[-_ ]?token|token|password|secret)(?<separator>\\s*[:=]\\s*)(?<value>\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'[^']*'|[^\\s,;}]*)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SensitiveJsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "accesstoken", "token", "password", "secret"
    };

    private readonly IReadOnlyList<string> _knownSecrets;

    public SecretRedactor(IReadOnlyDictionary<string, string>? knownSecrets)
    {
        _knownSecrets = (knownSecrets ?? new Dictionary<string, string>(StringComparer.Ordinal)).Values
           .Where(secret => !string.IsNullOrEmpty(secret))
           .Distinct(StringComparer.Ordinal)
           .OrderByDescending(secret => secret.Length)
           .ToArray();
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        if (TryRedactJson(value, out var redactedJson)) return redactedJson;

        return SecretAssignment.Replace(RedactKnownSecrets(value), static match =>
        {
            var original = match.Groups["value"].Value;
            var replacement = original.StartsWith('"') ? "\"[REDACTED]\""
                : original.StartsWith('\'')            ? "'[REDACTED]'"
                                                         : "[REDACTED]";
            return match.Groups["name"].Value + match.Groups["separator"].Value + replacement;
        });
    }

    public string? RedactNullable(string? value) => value is null ? null : Redact(value);

    private string RedactKnownSecrets(string value)
    {
        foreach (var secret in _knownSecrets) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        return value;
    }

    public StructuredState Redact(StructuredState state) => new()
    {
        Goal               = Redact(state.Goal),
        Constraints        = state.Constraints.Select(Redact).ToArray(),
        Decisions          = state.Decisions.Select(Redact).ToArray(),
        FilesInspected     = state.FilesInspected.Select(Redact).ToArray(),
        FilesModified      = state.FilesModified.Select(Redact).ToArray(),
        CommandsAndResults = state.CommandsAndResults.Select(Redact).ToArray(),
        PendingWork        = state.PendingWork.Select(Redact).ToArray()
    };

    public ToolCallRecord Redact(ToolCallRecord record) => new()
    {
        CallId           = Redact(record.CallId),
        InvocationId     = record.InvocationId,
        ToolName         = Redact(record.ToolName),
        Capability       = RedactNullable(record.Capability),
        SideEffects      = record.SideEffects,
        Summary          = Redact(record.Summary),
        PreparationError = RedactNullable(record.PreparationError),
        Approval         = RedactNullable(record.Approval),
        State            = record.State
    };

    public ChatMessage Redact(ChatMessage message) => new()
    {
        Role       = message.Role,
        Content    = Redact(message.Content),
        Name       = RedactNullable(message.Name),
        ToolCallId = RedactNullable(message.ToolCallId),
        ToolCalls = message.ToolCalls
                          ?.Select(call => new ChatToolCall(Redact(call.Id), Redact(call.FunctionName),
                                                            Redact(call.ArgumentsJson))).ToArray(),
        // 检查投影中的 opaque 字段同样必须脱敏；此投影不能用于无损续聊。
        Reasoning = message.Reasoning?.Select(entry => new ReasoningContent(RedactNullable(entry.Text),
                                                                            RedactNullable(entry.ProtectedData),
                                                                            RedactNullable(entry.ItemId))).ToArray()
    };

    private bool TryRedactJson(string value, out string redacted)
    {
        redacted = string.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        using (var output = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(output))
            {
                WriteRedactedJson(document.RootElement, writer);
            }

            redacted = Encoding.UTF8.GetString(output.ToArray());
        }

        return true;
    }

    // 在解码后的 JSON 值上匹配秘密，由 writer 转义替换值，避免破坏语法或重解析字符串。
    private void WriteRedactedJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object :
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(RedactKnownSecrets(property.Name));
                    if (IsSensitiveJsonName(property.Name) && property.Value.ValueKind != JsonValueKind.Null)
                        writer.WriteStringValue("[REDACTED]");
                    else
                        WriteRedactedJson(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array :
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) WriteRedactedJson(item, writer);

                writer.WriteEndArray();
                break;

            case JsonValueKind.String :
                writer.WriteStringValue(RedactKnownSecrets(element.GetString()!));
                break;

            default :
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveJsonName(string name)
    {
        var normalized = new string(name.Where(char.IsLetterOrDigit).ToArray());
        return SensitiveJsonNames.Contains(normalized);
    }
}

[JsonSerializable(typeof(AuditRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata |
                                              JsonSourceGenerationMode.Serialization)]
public sealed partial class PersistenceJsonContext : JsonSerializerContext;
