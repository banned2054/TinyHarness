using System.Runtime.CompilerServices;
using TinyHarness.Core.ChatCompletions;

namespace TinyHarness.Cli;

/// <summary>
/// Disposable temp workspace holding the offline smoke fixtures. The tools under
/// test are read-only; the smoke verb creates and cleans up these files itself,
/// so a run never depends on the repo layout or leaves anything behind.
/// </summary>
internal sealed class SmokeWorkspace : IDisposable
{
    private SmokeWorkspace(string root) => Root = root;

    public string Root { get; }

    public static async Task<SmokeWorkspace> CreateAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new SmokeWorkspace(root);
        try
        {
            await workspace.WriteAsync("sample.txt", "alpha needle\nbeta\ngamma needle", cancellationToken)
                           .ConfigureAwait(false);
            await workspace.WriteAsync("long.txt", new string('x', 20_000) + "\ntail", cancellationToken)
                           .ConfigureAwait(false);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private Task WriteAsync(string name, string content, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(Path.Combine(Root, name), content, cancellationToken);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive : true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}

/// <summary>
/// One step of the offline smoke script: the tool call to issue (or the final
/// plain-text turn) plus the check its own result must satisfy. The check runs
/// on the next model request, once the Agent loop has handed the result back.
/// </summary>
internal sealed class SmokeStep
{
    /// <summary>Tool to call this turn; null for the final plain-text turn.</summary>
    public string? ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    /// <summary>Validates this step's tool result; throws on mismatch.</summary>
    public Action<string>? VerifyResult { get; init; }

    public bool IsTool => ToolName is not null;

    /// <summary>Builds the streamed completion for this step.</summary>
    public IReadOnlyList<ChatStreamEvent> BuildEvents(int turn)
    {
        if (ToolName is null)
        {
            return
            [
                Content("Smoke ok: the Agent loop ran "),
                Content("list_files, search_text and read_file."),
                End(),
            ];
        }

        // Arguments are split across deltas (when long enough) to exercise the
        // same fragment assembly the real transport relies on.
        const string callId = "smoke_call";
        var          events = new List<ChatStreamEvent>();
        if (ArgumentsJson!.Length > 2)
        {
            var mid = ArgumentsJson.Length / 2;
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = callId,
                ToolCallFunctionName   = ToolName,
                ToolCallArgumentsDelta = ArgumentsJson[..mid],
            });
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallArgumentsDelta = ArgumentsJson[mid..],
            });
        }
        else
        {
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = callId,
                ToolCallFunctionName   = ToolName,
                ToolCallArgumentsDelta = ArgumentsJson,
            });
        }

        events.Add(End());
        return events;
    }

    private static ChatStreamEvent Content(string chunk) => new()
    {
        Kind         = ChatStreamEventKind.ContentDelta,
        ContentDelta = chunk,
    };

    private static ChatStreamEvent End() => new() { Kind = ChatStreamEventKind.End };
}

/// <summary>
/// Scripted model client for the offline smoke. Each model request replays the
/// next script step; before every step after the first it verifies that the
/// Agent loop really handed the previous tool result back into the conversation
/// (the M3 model-tool-model closure) and that the content matches what the tool
/// must have produced. Any mismatch throws, which the Agent loop surfaces as
/// <see cref="Agent.AgentStatus.Failed"/> and makes the smoke exit non-zero.
/// </summary>
internal sealed class SmokeScriptClient(IReadOnlyList<SmokeStep> steps) : IChatCompletionClient
{
    private int _turn;

    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                 [EnumeratorCancellation]
                                                                 CancellationToken cancellationToken)
    {
        var turn = _turn++;
        if (turn >= steps.Count)
        {
            throw new InvalidOperationException($"Smoke script: unexpected model request #{turn + 1}.");
        }

        if (turn > 0)
        {
            var previousTool = steps[turn - 1];
            if (!previousTool.IsTool)
            {
                throw new InvalidOperationException("Smoke script: a plain-text turn preceded a tool turn.");
            }

            var lastResult = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
            if (lastResult is null)
            {
                throw new InvalidOperationException(
                                                    $"Smoke: model request #{turn + 1} has no tool result in history; the Agent loop " +
                                                    "did not hand the previous tool result back (loop closure broken).");
            }

            previousTool.VerifyResult?.Invoke(lastResult.Content);
        }

        foreach (var item in steps[turn].BuildEvents(turn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}

/// <summary>The scripted smoke plan: ordered steps plus the expected tool execution count.</summary>
internal sealed record SmokePlan(IReadOnlyList<SmokeStep> Steps, int ExpectedToolExecutions);

/// <summary>
/// Builds the offline smoke plan. Fixtures are fixed, so every expected result
/// below is deterministic; any deviation (including the read_file truncation
/// semantics changed when the line/output budgets landed) fails the smoke.
/// </summary>
internal static class SmokeScript
{
    public static SmokePlan Build()
    {
        var steps = new List<SmokeStep>
        {
            Tool("list_files", "{}", content =>
            {
                RequireContains(content, "sample.txt", "list_files");
                RequireContains(content, "long.txt", "list_files");
            }),
            Tool("search_text", """{"pattern":"needle","path":"sample.txt"}""", content =>
            {
                RequireContains(content, "sample.txt:1: alpha needle", "search_text");
                RequireContains(content, "sample.txt:3: gamma needle", "search_text");
                RequireContains(content, "(2 matches in 1 file)", "search_text");
            }),
            Tool("read_file", """{"path":"sample.txt"}""", content =>
                     RequireEqual(content, "alpha needle\nbeta\ngamma needle", "read_file sample.txt")),
            Tool("read_file", """{"path":"sample.txt","offset":2,"limit":1}""", content =>
                     RequireEqual(content, "beta\n... truncated before line 3; continue with offset=3",
                                  "read_file sample.txt offset/limit")),
            Tool("read_file", """{"path":"long.txt"}""", content =>
            {
                RequireContains(content, "... [line 1 truncated]", "read_file long.txt");
                if (!content.EndsWith("tail", StringComparison.Ordinal))
                {
                    throw Mismatch("read_file long.txt", content, "to end with 'tail' after the cut line");
                }
            }),
            new() // Final plain-text turn; terminates the loop.
        };

        return new SmokePlan(steps, steps.Count(s => s.IsTool));
    }

    private static SmokeStep Tool(string name, string argumentsJson, Action<string> verify)
        => new()
        {
            ToolName      = name,
            ArgumentsJson = argumentsJson,
            VerifyResult  = verify,
        };

    private static void RequireContains(string content, string expected, string what)
    {
        if (!content.Contains(expected, StringComparison.Ordinal))
        {
            throw Mismatch(what, content, $"to contain '{expected}'");
        }
    }

    private static void RequireEqual(string content, string expected, string what)
    {
        if (!string.Equals(content, expected, StringComparison.Ordinal))
        {
            throw Mismatch(what, content, $"to equal '{expected}'");
        }
    }

    private static InvalidOperationException Mismatch(string what, string content, string expectation)
    {
        var shown = content.Length <= 300 ? content : content[..300] + "...";
        return new InvalidOperationException($"Smoke: {what} result expected {expectation}, but got: {shown}");
    }
}
