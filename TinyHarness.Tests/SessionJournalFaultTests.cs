using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证真实会话日志写入故障下的副作用阻断、poison 与恢复边界。</para>
///     <para>Verifies side-effect blocking, writer poisoning, and recovery boundaries under real journal write faults.</para>
/// </summary>
public sealed class SessionJournalFaultTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static readonly SessionModelIdentity Model = new("test-model", "https://example.invalid/v1",
                                                             ChatApiKind.ChatCompletions);

    private static readonly AgentOptions Options = new()
    {
        Model = "test-model", MaxAgentSteps = 10, DefaultToolTimeoutSeconds = 30
    };

    [Test]
    public async Task IntentBeforeWriteFailure_PreventsToolStart()
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        var       client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("write", "{}"));
        var                 tool       = new FakeTool("write");
        SessionStoreWriter? writer     = null;
        var                 faultCount = 0;
        using var host = NewHost(workspace.Root, directory.Root, client, [tool], id =>
        {
            writer = new SessionStoreWriter(directory.Root, id)
            {
                InjectWriteFault = (batch, phase) =>
                {
                    if (phase == "before-write" && HasExecutionIntent(batch))
                    {
                        faultCount++;
                        throw new IOException("injected execution-intent fault");
                    }
                }
            };
            return writer;
        });

        var run = await host.SubmitTurnAsync("write", CancellationToken.None);

        Assert.That(faultCount, Is.EqualTo(1));
        Assert.That(tool.ExecuteCount, Is.Zero);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(host.PersistenceWarning, Does.Contain("injected execution-intent fault"));
        Assert.That(writer!.IsHealthy, Is.False);
    }

    [Test]
    public async Task ResultBeforeWriteFailure_PoisonsWriterAndBlocksSecondToolAfterRestart()
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        var       client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("write", 2, "call"));
        var                 tool       = new FakeTool("write");
        SessionStoreWriter? writer     = null;
        var                 faultCount = 0;
        var host = NewHost(workspace.Root, directory.Root, client, [tool], id =>
        {
            writer = new SessionStoreWriter(directory.Root, id)
            {
                InjectWriteFault = (batch, phase) =>
                {
                    if (phase == "before-write" && HasToolHistory(batch, "call_1"))
                    {
                        faultCount++;
                        throw new IOException("injected tool-result fault");
                    }
                }
            };
            return writer;
        });

        var run = await host.SubmitTurnAsync("write twice", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        Assert.That(faultCount, Is.EqualTo(1));
        Assert.That(writer!.IsHealthy, Is.False);
        Assert.That(host.InspectionPending, Is.True);
        var poisonError =
            await Assert.ThrowsAsync<SessionStoreException>(() => writer.AppendRecordsAsync([
                new SessionLogRecord(string.Empty, "audit", JsonSerializer.SerializeToElement(new { }))
            ], CancellationToken.None));
        Assert.That(poisonError!.Message, Does.Contain("poisoned"));

        var sessionId = host.Session.Id;
        host.Dispose();
        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(restored.NeedsInspection, Is.Not.Null);
        Assert.That(reopened.LoadedSnapshot!.Turns.Single().Runs.Single().ToolCalls!
                            .Single(call => call.CallId == "call_1").State,
                    Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
    }

    [TestCase("after-line")]
    [TestCase("after-flush")]
    public async Task ResultConfirmationFault_DoesNotRetryAndLeavesValidSequence(string phase)
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        var       client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCalls("write", 2, "call"));
        var                 tool             = new FakeTool("write");
        SessionStoreWriter? writer           = null;
        var                 targetFaultCount = 0;
        using (var host = NewHost(workspace.Root, directory.Root, client, [tool], id =>
               {
                   writer = new SessionStoreWriter(directory.Root, id)
                   {
                       InjectWriteFault = (batch, writePhase) =>
                       {
                           if (writePhase != phase || !HasToolHistory(batch, "call_1")) return;
                           targetFaultCount++;
                           throw new IOException($"injected {phase} fault");
                       }
                   };
                   return writer;
               }))
        {
            var run = await host.SubmitTurnAsync("write twice", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Failed));
            Assert.That(tool.ExecuteCount, Is.EqualTo(1));
            Assert.That(host.InspectionPending, Is.True);
        }

        Assert.That(targetFaultCount, Is.EqualTo(1));
        Assert.That(writer!.IsHealthy, Is.False);
        var sessionId            = writer.SessionId;
        var sequenceBeforeReopen = writer.CommitSequence;
        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        Assert.That(reopened.CommitSequence, Is.InRange(sequenceBeforeReopen, sequenceBeforeReopen + 1));
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(restored.Session.Turns.Single().Runs.Single().ExecutionRecords, Is.Not.Null);
        Assert.That(tool.ExecuteCount, Is.EqualTo(1));
        var resumedSequence = reopened.CommitSequence;
        await reopened.CommitAsync(restored.Session, restored.Context, CancellationToken.None);
        Assert.That(reopened.CommitSequence, Is.EqualTo(resumedSequence));
    }

    [Test]
    public async Task AcknowledgementSurvivesRestartAndNewUnknownResultInvalidatesIt()
    {
        using var workspace   = new TestTempDir();
        using var directory   = new TestTempDir();
        var       firstClient = new FakeChatClient();
        firstClient.Enqueue(FakeChatClient.ToolCall("slow-one", "{}", "call_one"));
        var    firstTool = BlockingTool("slow-one", out var firstStarted);
        string sessionId;
        using (var host = NewHost(workspace.Root, directory.Root, firstClient, [firstTool]))
        {
            sessionId = host.Session.Id;
            await CancelDuringToolAsync(host, firstStarted);
            Assert.That(host.InspectionPending, Is.True);
            await host.AcknowledgeInspectionAsync(CancellationToken.None);
        }

        var resumedWriter = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                       CancellationToken.None);
        var firstRestoration =
            SessionResume.Restore(resumedWriter.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(firstRestoration.NeedsInspection, Is.Null);

        var secondClient = new FakeChatClient();
        secondClient.Enqueue(FakeChatClient.ToolCall("slow-two", "{}", "call_two"));
        var secondTool = BlockingTool("slow-two", out var secondStarted);
        using (var resumedHost = new ChatSessionHost(firstRestoration.Session, firstRestoration.Context, Options,
                                                     secondClient, new ToolRegistry([secondTool]), SystemPrompt,
                                                     sessionWriter : resumedWriter))
        {
            await CancelDuringToolAsync(resumedHost, secondStarted);
            Assert.That(resumedHost.InspectionPending, Is.True);
        }

        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        Assert.That(reopened.LoadedSnapshot!.InspectionAcknowledgedUtc, Is.Null);
        var finalRestoration = SessionResume.Restore(reopened.LoadedSnapshot, workspace.Root, Model, SystemPrompt);
        Assert.That(finalRestoration.NeedsInspection, Is.Not.Null);
        Assert.That(firstTool.ExecuteCount, Is.EqualTo(1));
        Assert.That(secondTool.ExecuteCount, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DistinctInvocationsRestoreWithOriginalOrLegacyRedactedCallIds(bool legacyRedacted)
    {
        using var    workspace    = new TestTempDir();
        using var    directory    = new TestTempDir();
        const string firstCallId  = "known-secret-call-one";
        const string secondCallId = "known-secret-call-two";
        var          client       = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = firstCallId,
                ToolCallFunctionName = "work", ToolCallArgumentsDelta                   = "{}"
            },
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = secondCallId,
                ToolCallFunctionName = "work", ToolCallArgumentsDelta                   = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        client.Enqueue(FakeChatClient.Text("done"));
        var tool = new FakeTool("work");
        var secrets = new Dictionary<string, string>
        {
            ["FIRST"]  = firstCallId,
            ["SECOND"] = secondCallId
        };
        string sessionId;
        using (var host = NewHost(workspace.Root, directory.Root, client, [tool], id =>
                                      new SessionStoreWriter(directory.Root, id, secrets)))
        {
            var run = await host.SubmitTurnAsync("run two calls", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(tool.ExecuteCount, Is.EqualTo(2));
            sessionId = host.Session.Id;
        }

        if (legacyRedacted)
        {
            // 模拟旧 writer 已经脱敏的本地记录，协议消息保持原样；读取不能猜回旧 CallId。
            var path  = Path.Combine(directory.Root, sessionId + SessionStoreWriter.LogFileSuffix);
            var lines = await File.ReadAllLinesAsync(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var batch = System.Text.Json.Nodes.JsonNode.Parse(lines[i])!;
                foreach (var record in batch["records"]!.AsArray())
                {
                    var kind    = record!["kind"]!.GetValue<string>();
                    var payload = record["payload"]!;
                    if (kind == "history" && payload["callId"] is not null)
                        payload["callId"] = "[REDACTED]";
                    if (kind == "run" && payload["run"]!["toolCalls"] is System.Text.Json.Nodes.JsonArray calls)
                        foreach (var call in calls)
                            call!["callId"] = "[REDACTED]";
                }

                lines[i] = batch.ToJsonString();
            }

            await File.WriteAllLinesAsync(path, lines);
        }

        var page = await new SessionHistoryReader().ReadPageAsync(directory.Root, sessionId, 200, null,
                                                                  CancellationToken.None);
        var history = page.Items.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();
        Assert.That(history.Select(item => item.CallId),
                    Is.EqualTo(legacyRedacted ? new[] { "[REDACTED]", "[REDACTED]" } : [firstCallId, secondCallId]));
        Assert.That(history.Select(item => item.InvocationId), Is.EqualTo(["call-00000001", "call-00000002"]));
        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, secrets,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Select(call => call.InvocationId),
                    Is.EqualTo(["call-00000001", "call-00000002"]));
        Assert.That(restored.Context.Messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id),
                    Is.EqualTo([firstCallId, secondCallId]));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReusedRawCallIdAcrossRounds_KeepsAppearanceOrderIncludingPreparationFailure(
        bool firstPrepareFails)
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        var       client    = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall(firstPrepareFails ? "missing" : "work", "{}", "smoke_call"));
        client.Enqueue(FakeChatClient.ToolCall("work", "{}", "smoke_call"));
        client.Enqueue(FakeChatClient.Text("done"));
        client.Enqueue(FakeChatClient.ToolCall("work", "{}", "smoke_call"));
        client.Enqueue(FakeChatClient.Text("second run done"));
        var    tool = new FakeTool("work");
        string sessionId;
        using (var host = NewHost(workspace.Root, directory.Root, client, [tool]))
        {
            var first = await host.SubmitTurnAsync("two rounds", CancellationToken.None);
            Assert.That(first.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(first.Result.ToolCalls!.Select(call => call.InvocationId),
                        Is.EqualTo(["call-00000001", "call-00000002"]));
            var second = await host.SubmitTurnAsync("new run", CancellationToken.None);
            Assert.That(second.Result!.ToolCalls!.Single().InvocationId, Is.EqualTo("call-00000001"));
            sessionId = host.Session.Id;
        }

        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(restored.NeedsInspection, Is.Null);
        Assert.That(tool.ExecuteCount, Is.EqualTo(firstPrepareFails ? 2 : 3));
        Assert.That(restored.Context.Messages.SelectMany(message => message.ToolCalls ?? []).Select(call => call.Id),
                    Is.EqualTo(["smoke_call", "smoke_call", "smoke_call"]));
    }

    [Test]
    public async Task CancelBetweenPatchFiles_PreservesConfirmedFileAndUnknownInvocation()
    {
        using var workspace    = new TestTempDir();
        using var directory    = new TestTempDir();
        using var cancellation = new CancellationTokenSource();
        var       firstPath    = workspace.WriteFile("first.txt", "before\n");
        var       secondPath   = workspace.WriteFile("second.txt", "before\n");
        var patchTool = new ApplyPatchTool(workspace.Workspace)
        {
            WriteCompleted = _ => cancellation.Cancel()
        };
        var laterTool = new FakeTool("later");
        var patch = "--- a/first.txt\n+++ b/first.txt\n@@ -1 +1 @@\n-before\n+after\n" +
                    "--- a/second.txt\n+++ b/second.txt\n@@ -1 +1 @@\n-before\n+after\n";
        var arguments = new JsonObject { ["patch"] = patch }.ToJsonString();
        var client    = new FakeChatClient();
        client.Enqueue([
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0, ToolCallId = "patch",
                ToolCallFunctionName = "apply_patch", ToolCallArgumentsDelta            = arguments
            },
            new ChatStreamEvent
            {
                Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 1, ToolCallId = "later",
                ToolCallFunctionName = "later", ToolCallArgumentsDelta                  = "{}"
            },
            new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" }
        ]);
        string sessionId;
        using (var host = NewHost(workspace.Root, directory.Root, client, [patchTool, laterTool]))
        {
            var run = await host.SubmitTurnAsync("patch files", cancellation.Token);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
            Assert.That(run.Result.ToolCalls!.Single(call => call.CallId == "patch").State,
                        Is.EqualTo(ToolCallExecutionState.UnknownResult));
            Assert.That(host.InspectionPending, Is.True);
            Assert.That(host.PersistenceWarning, Is.Null);
            sessionId = host.Session.Id;
        }

        Assert.That(await File.ReadAllTextAsync(firstPath), Is.EqualTo("after\n"));
        Assert.That(await File.ReadAllTextAsync(secondPath), Is.EqualTo("before\n"));
        Assert.That(laterTool.ExecuteCount, Is.Zero);
        var page = await new SessionHistoryReader().ReadPageAsync(directory.Root, sessionId, 200, null,
                                                                  CancellationToken.None);
        var history = page.Items.Single(item => item is { Kind: SessionHistoryKind.Tool, ToolName: "apply_patch" });
        Assert.That(history.State, Is.EqualTo(nameof(ToolCallExecutionState.UnknownResult)));
        Assert.That(history.PatchChanges, Has.Count.EqualTo(1));
        Assert.That(history.PatchChanges![0].Path, Is.EqualTo("first.txt"));
        Assert.That(history.PatchChanges[0].State, Is.EqualTo(PatchChange.ConfirmedState));
        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        Assert.That(restored.NeedsInspection, Is.Not.Null);
    }

    [Test]
    public async Task ReadOnlyTerminalHistoryBeforeFinalRun_RestoresWithoutExecutionIntent()
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        workspace.WriteFile("input.txt", "read-only contents\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", "{\"path\":\"input.txt\"}"));
        client.Enqueue(FakeChatClient.Text("done"));
        var    intentWrites = 0;
        string sessionId;
        using (var host = NewHost(workspace.Root, directory.Root, client, [new ReadFileTool(workspace.Workspace)], id =>
                                      new SessionStoreWriter(directory.Root, id)
                                      {
                                          InjectWriteFault = (batch, phase) =>
                                          {
                                              if (phase == "before-write" && HasExecutionIntent(batch)) intentWrites++;
                                          }
                                      }))
        {
            var run = await host.SubmitTurnAsync("read input", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(run.Result.ToolExecutions, Is.EqualTo(1));
            sessionId = host.Session.Id;
        }

        Assert.That(intentWrites, Is.Zero);
        using var reopened = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                        CancellationToken.None);
        var restored = SessionResume.Restore(reopened.LoadedSnapshot!, workspace.Root, Model, SystemPrompt);
        var call     = restored.Session.Turns.Single().Runs.Single().Result!.ToolCalls!.Single();
        Assert.That(call.State, Is.EqualTo(ToolCallExecutionState.ConfirmedResult));
        Assert.That(call.SideEffects, Is.False);
        Assert.That(call.InvocationId, Is.EqualTo("call-00000001"));
        Assert.That(restored.NeedsInspection, Is.Null);
    }

    [TestCase("ConfirmedResult", false)]
    [TestCase("ConfirmedResult", true)]
    [TestCase("UnknownResult", false)]
    [TestCase("UnknownResult", true)]
    public async Task ExecutedSideEffectWithoutIntent_IsRejected(string state, bool missingCapability)
    {
        using var workspace      = new TestTempDir();
        using var directory      = new TestTempDir();
        var       sessionId      = await WriteSuccessfulToolSessionAsync(workspace.Root, directory.Root);
        var       sawToolHistory = false;
        await RewriteLogAsync(directory.Root, sessionId, record =>
        {
            var payload = record["payload"]!.AsObject();
            if (record["kind"]!.GetValue<string>() == "run" && !sawToolHistory)
                payload["run"]!["toolCalls"] = new JsonArray();
            if (record["kind"]!.GetValue<string>()  != "history" ||
                payload["kind"]!.GetValue<string>() != SessionHistoryKind.Tool) return;
            sawToolHistory   = true;
            payload["state"] = state;
            if (missingCapability) payload["capability"] = null;
        });

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(directory.Root,
                                                                     sessionId, CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("without an execution-intent record"));
    }

    [TestCase("state")]
    [TestCase("callId")]
    [TestCase("toolName")]
    [TestCase("omit")]
    public async Task RunAfterTerminalHistory_CannotDowngradeRenameOrOmitInvocation(string change)
    {
        using var workspace      = new TestTempDir();
        using var directory      = new TestTempDir();
        var       sessionId      = await WriteSuccessfulToolSessionAsync(workspace.Root, directory.Root);
        var       sawToolHistory = false;
        await RewriteLogAsync(directory.Root, sessionId, record =>
        {
            var payload = record["payload"]!.AsObject();
            if (record["kind"]!.GetValue<string>()  == "history" &&
                payload["kind"]!.GetValue<string>() == SessionHistoryKind.Tool)
                sawToolHistory = true;
            if (!sawToolHistory || record["kind"]!.GetValue<string>() != "run") return;
            var run = payload["run"]!.AsObject();
            if (change == "omit") run["toolCalls"] = new JsonArray();
            else run["toolCalls"]![0]![change]     = change == "state" ? "UnknownResult" : "renamed";
        });

        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(directory.Root,
                                                                     sessionId, CancellationToken.None));
        Assert.That(error!.Message,
                    Does.Contain(change == "omit" ? "omits completed invocation" : "contradicts its terminal history"));
    }

    [Test]
    public async Task DuplicateTerminalInvocation_IsRejectedDespiteNewJournalRecordId()
    {
        using var workspace = new TestTempDir();
        using var directory = new TestTempDir();
        var       sessionId = await WriteSuccessfulToolSessionAsync(workspace.Root, directory.Root);
        var page = await new SessionHistoryReader().ReadPageAsync(directory.Root, sessionId, 200, null,
                                                                  CancellationToken.None);
        var item = page.Items.Single(entry => entry.Kind == SessionHistoryKind.Tool);
        using var writer = await SessionStoreWriter.OpenExistingAsync(directory.Root, sessionId, null,
                                                                      CancellationToken.None);
        var appended = await writer.AppendRecordsAsync([
            new SessionLogRecord(string.Empty, "history",
                                 JsonSerializer.SerializeToElement(item,
                                                                   SessionHistoryJsonContext.Default
                                                                      .SessionHistoryItem))
        ], CancellationToken.None);
        Assert.That(appended.Single().Id, Is.Not.EqualTo(item.Id));
        var error =
            await Assert.ThrowsAsync<SessionStoreException>(() => SessionStoreReader.ReadAsync(directory.Root,
                                                                     sessionId, CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("duplicate terminal history"));
    }

    /// <summary>
    ///     <para>通过真实宿主和 writer 写入一次完整副作用调用。</para>
    ///     <para>Writes a completed side-effect invocation through the real host and writer.</para>
    /// </summary>
    private static async Task<string> WriteSuccessfulToolSessionAsync(string workspace, string directory)
    {
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("work", "{}"));
        client.Enqueue(FakeChatClient.Text("done"));
        using var host = NewHost(workspace, directory, client, [new FakeTool("work")]);
        var       run  = await host.SubmitTurnAsync("work", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        return host.Session.Id;
    }

    /// <summary>
    ///     <para>仅篡改小型测试日志中的 typed payload，保留物理批次身份和序号。</para>
    ///     <para>Mutates typed payloads in a small test log while retaining physical batch identity and sequence.</para>
    /// </summary>
    private static async Task RewriteLogAsync(string directory, string sessionId, Action<JsonObject> rewrite)
    {
        var path  = Path.Combine(directory, sessionId + SessionStoreWriter.LogFileSuffix);
        var lines = await File.ReadAllLinesAsync(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var batch = JsonNode.Parse(lines[index])!.AsObject();
            foreach (var record in batch["records"]!.AsArray()) rewrite(record!.AsObject());
            lines[index] = batch.ToJsonString();
        }

        await File.WriteAllLinesAsync(path, lines);
    }

    private static ChatSessionHost NewHost(string workspace, string directory, FakeChatClient client,
                                           IReadOnlyList<ITool> tools,
                                           Func<string, ISessionStoreWriter>? writerFactory = null) =>
        new(workspace, Model, Options, client, new ToolRegistry(tools), SystemPrompt,
            sessionWriterFactory : writerFactory ?? (id => new SessionStoreWriter(directory, id)));

    private static bool HasExecutionIntent(SessionLogBatch batch) => batch.Records.Any(record =>
                 record.Kind == "session-state" && record.Payload.TryGetProperty("executionIntent", out var value) &&
                 value.ValueKind == JsonValueKind.True);

    private static bool HasToolHistory(SessionLogBatch batch, string callId) => batch.Records.Any(record =>
                 record.Kind == "history"                            &&
                 record.Payload.TryGetProperty("kind", out var kind) &&
                 kind.GetString() == "tool"                          &&
                 record.Payload.TryGetProperty("callId", out var id) && id.GetString() == callId);

    private static FakeTool BlockingTool(string name, out TaskCompletionSource started)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        started = signal;
        return new FakeTool(name)
        {
            ExecuteOverride = async cancellationToken =>
            {
                signal.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ToolResult { Succeeded = true, Content = "unreachable" };
            }
        };
    }

    private static async Task CancelDuringToolAsync(ChatSessionHost host, TaskCompletionSource started)
    {
        using var cancellation = new CancellationTokenSource();
        var       active       = host.SubmitTurnAsync("run a side effect", cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        var run = await active;
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
    }
}
