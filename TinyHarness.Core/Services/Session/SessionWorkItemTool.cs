using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     <para>仅更新会话工作项陈述；不写入工作区，不执行进程，不改变授权。</para>
///     <para>Updates session work-item claims only; never writes the workspace, runs processes or changes authorization.</para>
/// </summary>
internal sealed class SessionWorkItemTool(
    Func<IReadOnlyList<SessionWorkItem>> list, Func<string> evidenceCandidates,
    Func<string?, string, string, IReadOnlyList<int>, CancellationToken, Task<SessionWorkItem>> update) : ITool
{
    public ToolDefinition Definition { get; } = new()
    {
        Name = "work_items",
        Description =
            "List or update a bounded session work list with stable IDs. Use it for multi-step work and follow-ups. " +
            "Statuses are model claims, not verification. References may point to complete earlier canonical tool messages only. " +
            "Creating an item uses the current user turn as its source; this never grants permission. " +
            "Use action=list to inspect item IDs and recent canonical evidence-index candidates; evidence is optional.",
        Parameters = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["action"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("list", "update") },
                ["id"] = new JsonObject
                {
                    ["type"] = "string", ["description"] = "Existing ID to update; omit to create."
                },
                ["title"] = new JsonObject
                {
                    ["type"] = "string", ["description"] = "Goal and constraints, at most 2048 characters."
                },
                ["status"] = new JsonObject
                {
                    ["type"] = "string", ["enum"] = new JsonArray("pending", "in_progress", "completed", "cancelled")
                },
                ["evidenceMessageIndexes"] = new JsonObject
                {
                    ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "integer" }
                }
            },
            ["required"]             = new JsonArray("action"),
            ["additionalProperties"] = false
        }
    };

    public ToolPreparation Prepare(ChatToolCall call)
    {
        var args = ToolArgs.ParseObject(call);
        if (args.Any(pair => pair.Key is not ("action" or "id" or "title" or "status" or "evidenceMessageIndexes")))
            throw new InvalidDataException("Unknown work_items argument.");
        var     action   = JsonArgs.Required(args, "action");
        string? id       = null;
        string? title    = null;
        string? status   = null;
        var     evidence = new List<int>();
        if (action == "update")
        {
            if (args.ContainsKey("id"))
            {
                id = JsonArgs.Required(args, "id");
                if (!Guid.TryParseExact(id, "N", out _))
                    throw new InvalidDataException("A work item ID must be 32 hexadecimal characters.");
            }

            title  = JsonArgs.Required(args, "title");
            status = JsonArgs.Required(args, "status");
            if (title.Length > 2048 || status is not ("pending" or "in_progress" or "completed" or "cancelled"))
                throw new InvalidDataException("Invalid work item title or status.");
            if (args["evidenceMessageIndexes"] is { } node)
            {
                if (node is not JsonArray array || array.Count > 32)
                    throw new InvalidDataException("Invalid work item evidence array.");
                foreach (var value in array)
                {
                    if (value is not JsonValue scalar ||
                        !scalar.TryGetValue<int>(out var index) ||
                        index < 0 ||
                        evidence.Contains(index))
                        throw new InvalidDataException("Evidence indexes must be distinct nonnegative integers.");
                    evidence.Add(index);
                }
            }
        }
        else if (action != "list" || args.Count != 1)
        {
            throw new InvalidDataException("Use action=list alone, or action=update with title and status.");
        }

        return new ToolPreparation
        {
            CallId        = call.Id, ToolName       = Definition.Name, Arguments = args,
            Capability    = "session.work", Summary = $"work_items {action}",
            ExecutionPlan = new WorkPlan(action, id, title, status, evidence.ToArray())
        };
    }

    public async Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (preparation.ExecutionPlan is not WorkPlan plan)
            throw new InvalidDataException("Missing prepared work-item plan.");
        var items = plan.Action == "list"
            ? list()
            : [
                await update(plan.Id, plan.Title!, plan.Status!, plan.Evidence, cancellationToken).ConfigureAwait(false)
            ];
        var candidates = plan.Action == "list"
            ? "\nEvidence candidates (references only, not verified outcomes):\n" + evidenceCandidates()
            : string.Empty;
        return new ToolResult
        {
            Succeeded = true,
            Content = "Work statuses are claims, not verified outcomes.\n" +
                      string.Join('\n', items.Select(item => $"{item.Id} [{item.Status}] {item.Title} " +
                                                             $"(source turn: {item.SourceTurnId}; author: {item.Source}; evidence indexes: {string.Join(',', item.EvidenceMessageIndexes)})")) +
                      candidates
        };
    }

    private sealed record WorkPlan(
        string Action, string? Id, string? Title, string? Status, IReadOnlyList<int> Evidence);
}

public sealed partial class ChatSessionHost
{
    private string WorkEvidenceCandidates()
    {
        var boundary = ChatHistoryInvariants.LastCompleteBoundary(_context.Messages);
        return string.Join('\n', _context.Messages.Take(Math.Max(0, boundary))
                                         .Select((message, index) => (Message : message, Index : index))
                                         .Where(entry => entry.Message.Role == ChatRole.Tool).TakeLast(32)
                                         .Select(entry =>
                                                     $"{entry.Index}: tool {entry.Message.Name}, call {entry.Message.ToolCallId}"));
    }

    private async Task<SessionWorkItem> UpdateWorkItemAsync(
        SessionRun run, string? id, string title, string status,
        IReadOnlyList<int> evidence, CancellationToken cancellationToken)
    {
        var prior = id is null
            ? null
            : Session.WorkItems.SingleOrDefault(item => item.Id == id)
           ?? throw new InvalidDataException("Unknown work-item ID.");
        if (prior is null && Session.WorkItems.Count >= 128)
            throw new InvalidDataException("The session work-item limit is 128.");
        var safeContext = CompleteContextSnapshot();
        if (evidence.Any(index => index >= safeContext.Messages.Count ||
                                  safeContext.Messages[index].Role != ChatRole.Tool))
            throw new
                InvalidDataException("Evidence must reference complete earlier canonical tool messages; no verification is inferred.");
        var item = new SessionWorkItem
        {
            Id           = prior?.Id ?? Guid.NewGuid().ToString("N"), SourceTurnId = prior?.SourceTurnId ?? run.TurnId,
            UpdatedRunId = run.Id, Title = title, DisplayTitle = RedactControlText(title), Status = status,
            Revision     = checked((prior?.Revision ?? 0) + 1), EvidenceMessageIndexes = evidence.ToArray()
        };
        item.Validate();
        if (_sessionWriter is not null)
            try
            {
                var record = new SessionLogRecord(string.Empty, "work-item",
                                                  JsonSerializer.SerializeToElement(item,
                                                      SessionLogJsonContext.Default.SessionWorkItem));
                await _sessionWriter.CommitControlAsync(Session, safeContext, [record], cancellationToken)
                                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PersistenceWarning = ex.Message;
                throw new InvalidOperationException("The work-item update was not reliably saved.", ex);
            }

        Session.RecordWorkItem(item);
        return item;
    }
}
