using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Core.Persistence;

public interface IRunRecorder
{
    Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken);
    Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken);
    Task RecordPermissionAsync(ToolPreparation preparation, PermissionDecision decision, string outcome,
                                CancellationToken cancellationToken);
    Task RecordResultAsync(ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken);
    Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken);
    Task CompleteAsync(AgentResult result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                       CancellationToken cancellationToken);
}

public sealed class FileRunRecorder : IRunRecorder
{
    private readonly SecretRedactor _redactor;
    private readonly string _auditPath;
    private readonly string _sessionPath;
    private readonly string _runId;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileRunRecorder(string directory, string? runId = null,
                           IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
        _redactor = new SecretRedactor(knownSecrets);
        if (_runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Run id contains invalid filename characters.", nameof(runId));
        }

        Directory.CreateDirectory(directory);
        _auditPath = Path.Combine(directory, $"{_runId}.audit.jsonl");
        _sessionPath = Path.Combine(directory, $"{_runId}.session.json");
    }

    public string RunId => _runId;
    public string AuditPath => _auditPath;
    public string SessionPath => _sessionPath;

    public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
        AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "run.started", SystemPrompt = _redactor.Redact(systemPrompt),
            UserInput = _redactor.Redact(userInput),
        }, cancellationToken);

    public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
        AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "tool.prepared", ToolName = _redactor.Redact(preparation.ToolName),
            CallId = _redactor.Redact(preparation.CallId), Capability = _redactor.Redact(preparation.Capability),
            Summary = _redactor.Redact(preparation.Summary),
            TargetPaths = RedactPaths(preparation.TargetPaths),
        }, cancellationToken);

    public Task RecordPermissionAsync(ToolPreparation preparation, PermissionDecision decision, string outcome,
                                      CancellationToken cancellationToken) =>
        AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "tool.permission", ToolName = _redactor.Redact(preparation.ToolName),
            CallId = _redactor.Redact(preparation.CallId), Capability = _redactor.Redact(preparation.Capability),
            Summary = _redactor.Redact(preparation.Summary), TargetPaths = RedactPaths(preparation.TargetPaths),
            Decision = _redactor.Redact(decision.ToString()),
            Outcome = _redactor.Redact(outcome),
        }, cancellationToken);

    public Task RecordResultAsync(ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken) =>
        AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "tool.result", ToolName = _redactor.Redact(preparation.ToolName),
            CallId = _redactor.Redact(preparation.CallId),
            Succeeded = result.Succeeded, ExitCode = result.ExitCode, TimedOut = result.TimedOut,
            OutputTruncated = result.OutputTruncated,
            Outcome = result.Succeeded ? "tool completed" : "tool failed",
        }, cancellationToken);

    public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
        AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "context.compacted", BeforeTokens = change.BeforeTokens,
            AfterTokens = change.AfterTokens,
        }, cancellationToken);

    public async Task CompleteAsync(AgentResult result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                                    CancellationToken cancellationToken)
    {
        var safeResult = result with
        {
            FinalMessage = _redactor.Redact(result.FinalMessage),
            Error        = _redactor.RedactNullable(result.Error),
        };

        // run.completed means that Agent execution reached a terminal result. It is
        // intentionally written before the snapshot and is not proof that every
        // persistence file was saved successfully.
        await AppendAsync(new AuditRecord
        {
            RunId = _runId, Kind = "run.completed", Status = _redactor.Redact(safeResult.Status.ToString()),
            Outcome = safeResult.Error,
        }, cancellationToken).ConfigureAwait(false);

        var snapshot = new SessionSnapshot
        {
            RunId = _runId, StartedUtc = _startedUtc, CompletedUtc = DateTimeOffset.UtcNow,
            Messages = messages.Select(_redactor.Redact).ToArray(), State = _redactor.Redact(state),
            Result = safeResult,
        };
        var json = JsonSerializer.Serialize(snapshot, PersistenceJsonContext.Default.SessionSnapshot);
        var temporaryPath = _sessionPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, _sessionPath, overwrite: true);
    }

    private IReadOnlyList<string> RedactPaths(IReadOnlyList<string> paths) =>
        paths.Select(_redactor.Redact).ToArray();

    private async Task AppendAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(record, PersistenceJsonContext.Default.AuditRecord) + Environment.NewLine;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(_auditPath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class SecretRedactor
{
    private static readonly Regex SecretAssignment = new(
        "(?<name>api[-_ ]?key|access[-_ ]?token|token|password|secret)(?<separator>\\s*[:=]\\s*)(?<value>\\\"(?:\\\\.|[^\\\"\\\\])*\\\"|'[^']*'|[^\\s,;}]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> SensitiveJsonNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "accesstoken", "token", "password", "secret",
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
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (TryRedactJson(value, out var redactedJson))
        {
            return redactedJson;
        }

        return SecretAssignment.Replace(RedactKnownSecrets(value), static match =>
        {
            var original = match.Groups["value"].Value;
            var replacement = original.StartsWith('"') ? "\"[REDACTED]\""
                : original.StartsWith('\'')             ? "'[REDACTED]'"
                : "[REDACTED]";
            return match.Groups["name"].Value + match.Groups["separator"].Value + replacement;
        });
    }

    public string? RedactNullable(string? value) => value is null ? null : Redact(value);

    private string RedactKnownSecrets(string value)
    {
        foreach (var secret in _knownSecrets)
        {
            value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    public StructuredState Redact(StructuredState state) => new()
    {
        Goal = Redact(state.Goal), Constraints = state.Constraints.Select(Redact).ToArray(),
        Decisions = state.Decisions.Select(Redact).ToArray(), FilesInspected = state.FilesInspected.Select(Redact).ToArray(),
        FilesModified = state.FilesModified.Select(Redact).ToArray(),
        CommandsAndResults = state.CommandsAndResults.Select(Redact).ToArray(), PendingWork = state.PendingWork.Select(Redact).ToArray(),
    };

    public ChatMessage Redact(ChatMessage message) => new()
    {
        Role = message.Role, Content = Redact(message.Content), Name = RedactNullable(message.Name),
        ToolCallId = RedactNullable(message.ToolCallId),
        ToolCalls = message.ToolCalls?.Select(call => new ChatToolCall(
            Redact(call.Id), Redact(call.FunctionName), Redact(call.ArgumentsJson))).ToArray(),
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

    // Decode JSON before matching known secrets, then let the writer escape the
    // replacement. Never replace raw JSON syntax or reparse decoded string values.
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
                    {
                        writer.WriteStringValue("[REDACTED]");
                    }
                    else
                    {
                        WriteRedactedJson(property.Value, writer);
                    }
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array :
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(item, writer);
                }

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

public sealed record AuditRecord
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Kind { get; init; } = string.Empty;
    public string? SystemPrompt { get; init; }
    public string? UserInput { get; init; }
    public string? ToolName { get; init; }
    public string? CallId { get; init; }
    public string? Capability { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string>? TargetPaths { get; init; }
    public string? Decision { get; init; }
    public string? Outcome { get; init; }
    public string? Status { get; init; }
    public bool? Succeeded { get; init; }
    public int? ExitCode { get; init; }
    public bool? TimedOut { get; init; }
    public bool? OutputTruncated { get; init; }
    public int? BeforeTokens { get; init; }
    public int? AfterTokens { get; init; }
}

public sealed record SessionSnapshot
{
    public string RunId { get; init; } = string.Empty;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    public StructuredState State { get; init; } = StructuredState.Empty;
    public AgentResult Result { get; init; } = new() { Status = AgentStatus.Failed };
}

[JsonSerializable(typeof(AuditRecord))]
[JsonSerializable(typeof(SessionSnapshot))]
[JsonSerializable(typeof(AgentResult))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ChatToolCall))]
[JsonSerializable(typeof(StructuredState))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization)]
public sealed partial class PersistenceJsonContext : JsonSerializerContext;
