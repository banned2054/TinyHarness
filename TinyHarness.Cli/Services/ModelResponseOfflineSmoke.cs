using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>离线验证空回答失败、请求展示和同日志续聊；只读取现有 smoke 夹具，不调用模型服务。</para>
///     <para>Checks empty-response failure, progress display and journal continuation offline using the existing smoke fixture.</para>
/// </summary>
internal static class ModelResponseOfflineSmoke
{
    /// <summary>
    ///     <para>执行一次读取后模拟无效回答，恢复日志并验证缺失终止信息和后续成功回合。</para>
    ///     <para>Reads once, simulates invalid output, restores the journal, and checks missing termination followed by success.</para>
    /// </summary>
    internal static async Task RunAsync(
        string workspace, string directory, SessionModelIdentity identity, CancellationToken cancellationToken)
    {
        var options = new AgentOptions { Model = identity.ModelId, MaxAgentSteps = 4, DefaultToolTimeoutSeconds = 30 };
        using var display = new StringWriter();
        var sink = new ConsoleHostEventSink(output : display);
        var model = new ResponseClient(false);
        var tools = new ToolRegistry([new ReadFileTool(new Workspace(workspace))]);
        const string systemPrompt = "Offline response completion smoke";
        string sessionId;
        using (var host = new ChatSessionHost(workspace, identity, options, model, tools, systemPrompt, events : sink,
                                              sessionWriterFactory : id => new SessionStoreWriter(directory, id)))
        {
            var result = (await host.SubmitTurnAsync("read sample", cancellationToken).ConfigureAwait(false)).Result!;
            Require(result is { Status: AgentStatus.Failed, ToolExecutions: 1 } && model.Requests == 2,
                    "reasoning-only response did not fail after the single confirmed read");
            Require(result.ToolCalls?.Single().State == ToolCallExecutionState.ConfirmedResult &&
                    !host.InspectionPending && host.PersistenceWarning is null,
                    "confirmed tool or failure persistence changed");
            sessionId = host.Session.Id;
        }

        var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Response smoke log missing.");
        var restored = SessionResume.Restore(snapshot, workspace, identity, systemPrompt);
        var resumedModel = new ResponseClient(true);
        using (var writer = await SessionStoreWriter.OpenExistingAsync(directory, sessionId, null, cancellationToken)
                                                    .ConfigureAwait(false))
        using (var host = new ChatSessionHost(restored.Session, restored.Context, options, resumedModel, tools,
                                              systemPrompt, events : sink, sessionWriter : writer))
        {
            var missing = (await host.SubmitTurnAsync("missing terminal", cancellationToken).ConfigureAwait(false)).Result!;
            Require(missing is { Status: AgentStatus.Failed, ToolExecutions: 0 } && resumedModel.Requests == 1,
                    "missing termination was accepted or retried");
            var completed = (await host.SubmitTurnAsync("continue", cancellationToken).ConfigureAwait(false)).Result!;
            Require(completed is { Status: AgentStatus.Completed, ToolExecutions: 0, FinalMessage: "continued" } &&
                    resumedModel.Requests == 2 && host.PersistenceWarning is null && !host.InspectionPending,
                    "new turn failed or replayed a tool");
        }

        var final = (await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken).ConfigureAwait(false))!;
        Require(final.Turns.SelectMany(turn => turn.Runs).Select(run => run.Status)
                     .SequenceEqual(["Failed", "Failed", "Completed"]), "terminal facts did not round-trip");
        var rendered = display.ToString();
        Require(rendered.Contains("waiting for model", StringComparison.Ordinal) &&
                rendered.Contains("model request 2: failed", StringComparison.Ordinal) &&
                !rendered.Contains("private reasoning", StringComparison.Ordinal),
                "request progress or reasoning privacy failed");
    }

    /// <summary>
    ///     <para>将断言失败转换为明确的 smoke 错误。</para>
    ///     <para>Converts a failed assertion into a contextual smoke error.</para>
    /// </summary>
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException($"Response completion smoke: {detail}.");
    }

    private sealed class ResponseClient(bool resumed) : IChatCompletionClient
    {
        internal int Requests { get; private set; }

        /// <summary>
        ///     <para>播放有界响应并核查恢复视图，禁止隐式重试和历史工具重放。</para>
        ///     <para>Plays bounded responses and checks the restored view, rejecting implicit retries and tool replay.</para>
        /// </summary>
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var index = Requests++;
            Require(index < 2, "unexpected model retry");
            if (resumed)
            {
                Require(request.Messages.Count(message => message.Role == ChatRole.Tool) == 1 &&
                        !request.Messages.Any(message => message.Reasoning is { Count: > 0 }),
                        "confirmed tool was lost or invalid reasoning entered the restored view");
                if (index == 1)
                    yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "continued" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = index == 0 ? null : "stop" };
            }
            else if (index == 0)
            {
                yield return new ChatStreamEvent
                {
                    Kind = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "read-once",
                    ToolCallFunctionName = "read_file", ToolCallArgumentsDelta = """{"path":"sample.txt"}"""
                };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
            }
            else
            {
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ReasoningDelta, ReasoningDelta = "private reasoning" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
            }
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}
