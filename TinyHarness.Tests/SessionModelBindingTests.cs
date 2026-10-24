using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证客户端绑定的所有权转交与释放：提交切换释放旧客户端一次，拒绝或失败由调用方释放候选一次，
///         关闭释放当前客户端一次，仅改强度复用同一客户端，释放失败不改变已落盘结果，竞争不释放在用客户端。
///     </para>
///     <para>
///         Verifies client-binding ownership transfer and release: committed switches release the old client once,
///         rejections and failures leave the candidate with the caller for exactly one release, closing releases the
///         current client once, effort-only changes reuse one client, release failures never rewrite committed
///         results, and racing operations never release a client still in use.
///     </para>
/// </summary>
public class SessionModelBindingTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static readonly SessionModelIdentity Identity =
        new("bind-model", "https://binding.example/v1", ChatApiKind.ChatCompletions);

    private static readonly SessionModelIdentity Other =
        new("other-model", "https://binding.example/v1", ChatApiKind.ChatCompletions);

    private static AgentOptions Options() => new()
    {
        Model                     = Identity.ModelId,
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static ChatSessionHost NewHost(TrackingClient client, string? credentialRoute = null,
                                           TestTempDir? store = null) =>
        new(Path.GetTempPath(), Identity, Options(), client, new ToolRegistry([]), SystemPrompt,
            sessionWriterFactory : store is null ? null : id => new SessionStoreWriter(store.Root, id),
            credentialRoute      : credentialRoute);

    [Test]
    public async Task ChangeModel_CommittedSwitchReleasesOldClientOnceAndCloseReleasesCurrentClientOnce()
    {
        using var store     = new TestTempDir();
        var       oldClient = new TrackingClient();
        var       newClient = new TrackingClient();
        using var host      = NewHost(oldClient, "work|env:WORK_KEY", store);

        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Other, newClient, Options().Context, null, "work|env:WORK_KEY", "work"),
            CancellationToken.None);

        Assert.That(selection.Model, Is.EqualTo(Other));
        Assert.That(oldClient.DisposeCount, Is.EqualTo(1), "a committed switch must release the old client once");
        Assert.That(newClient.DisposeCount, Is.EqualTo(0), "the adopted client must stay alive with the host");

        host.Dispose();
        Assert.That(newClient.DisposeCount, Is.EqualTo(1), "closing must release the current client once");
        Assert.That(oldClient.DisposeCount, Is.EqualTo(1), "the old client must never be released twice");
    }

    [Test]
    public async Task ChangeModel_RejectedCandidateStaysWithCallerAndIsReleasedOnceByTheCaller()
    {
        var       startup       = new TrackingClient();
        using var host          = NewHost(startup, "work|env:WORK_KEY");
        var       staleRevision = host.Session.ModelSelection!.Revision + 5;
        var       candidate      = new TrackingClient();

        var candidateBinding = new SessionModelBinding(Other, candidate, Options().Context, null,
                                                       "work|env:WORK_KEY", "work");
        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    host.ChangeModelAsync(candidateBinding,
                                                                        CancellationToken.None,
                                                                        expectedRevision : staleRevision));

        Assert.That(candidate.DisposeCount, Is.EqualTo(0), "a rejected candidate stays with the caller, host never releases it");
        Assert.That(host.HasModelBinding, Is.True);
        host.Dispose();
        Assert.That(candidate.DisposeCount, Is.EqualTo(0), "host disposal must not touch a caller-owned candidate");

        candidateBinding.Dispose();
        Assert.That(candidate.DisposeCount, Is.EqualTo(1), "the caller releases the rejected candidate exactly once");
    }

    [Test]
    public void ConstructorFailure_LeavesTheClientWithTheCallerForASingleRelease()
    {
        // 所有权转交在构造成功返回后才完成：写入端工厂抛出时宿主不得提前释放客户端。
        var client = new TrackingClient();
        Assert.Throws<InvalidOperationException>(() =>
                                                 {
                                                     var neverReturned = new ChatSessionHost(
                                                         Path.GetTempPath(), Identity, Options(), client,
                                                         new ToolRegistry([]), SystemPrompt,
                                                         sessionWriterFactory :
                                                         _ => throw new InvalidOperationException(
                                                             "writer factory failed"));
                                                     Assert.Fail("construction must not succeed");
                                                 });

        Assert.That(client.DisposeCount, Is.EqualTo(0),
                    "a failed construction must not release the caller-owned client");
        // 调用方按契约清理：全程恰好一次。
        client.Dispose();
        Assert.That(client.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ChangeReasoningEffort_ReusesSameClientWithoutPrematureRelease()
    {
        var       client = new TrackingClient();
        using var host   = NewHost(client, "work|env:WORK_KEY");

        var selection = await host.ChangeReasoningEffortAsync(ModelReasoningEffort.High, CancellationToken.None,
                                                              [ModelReasoningEffort.High]);

        Assert.That(selection.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(selection.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(client.DisposeCount, Is.EqualTo(0), "an effort-only change must not release the reused client");
        host.Dispose();
        Assert.That(client.DisposeCount, Is.EqualTo(1), "the reused client is released once at close");
    }

    [Test]
    public async Task ChangeModel_NoOpWithEquivalentFreshClientAddsNoRevisionAndReleasesTheRedundantCandidate()
    {
        var       startup         = new TrackingClient();
        using var host            = NewHost(startup, "work|env:WORK_KEY");
        var       freshEquivalent = new TrackingClient();
        var       before          = host.Session.ModelSelection!;

        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, freshEquivalent, before.Context, before.ReasoningEffort,
                                    before.CredentialRoute, "work"),
            CancellationToken.None, expectedRevision : before.Revision);

        Assert.That(selection.Revision, Is.EqualTo(before.Revision), "an equivalent rebuilt client must not create a revision");
        Assert.That(host.Session.ModelSelections.Count, Is.EqualTo(1));
        Assert.That(freshEquivalent.DisposeCount, Is.EqualTo(1), "the redundant candidate client is released once by the host");
        Assert.That(startup.DisposeCount, Is.EqualTo(0), "the in-use client survives the no-op");
        host.Dispose();
        Assert.That(startup.DisposeCount, Is.EqualTo(1));
        Assert.That(freshEquivalent.DisposeCount, Is.EqualTo(1), "the redundant candidate is never released twice");
    }

    [Test]
    public async Task ChangeModel_ReleaseFailureKeepsCommittedSelectionAndReportsSeparately()
    {
        var       oldClient = new TrackingClient { ThrowOnDispose = true };
        var       newClient = new TrackingClient();
        using var host      = NewHost(oldClient, "work|env:WORK_KEY");

        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Other, newClient, Options().Context, null, "work|env:WORK_KEY", "work"),
            CancellationToken.None);

        Assert.That(selection.Revision, Is.EqualTo(2), "the switch stays committed after a failed old-client release");
        Assert.That(host.Session.ModelSelection!.Model, Is.EqualTo(Other));
        Assert.That(host.PersistenceWarning, Is.Null, "a release failure is not a persistence failure");
        Assert.That(host.BindingReleaseWarning, Does.Contain("client teardown failed"));
        Assert.That(oldClient.DisposeCount, Is.EqualTo(1), "the failing release is not retried");
        host.Dispose();
        Assert.That(oldClient.DisposeCount, Is.EqualTo(1), "the old client is never released again");
        Assert.That(newClient.DisposeCount, Is.EqualTo(1), "the adopted client is released once at close");
    }

    [Test]
    public async Task RacingOperations_NeverReleaseTheClientInUse()
    {
        var       entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       client  = new TrackingClient(entered);
        using var host    = NewHost(client, "work|env:WORK_KEY");

        var run = host.SubmitTurnAsync("long question", CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // 运行中拒绝切换；切换不得因拒绝而释放任何客户端。
        await TestAssert.ThrowsAsync<InvalidOperationException>(() =>
                                                                    host.ChangeModelAsync(
                                                                        new SessionModelBinding(Other,
                                                                            new TrackingClient(), Options().Context,
                                                                            null, "work|env:WORK_KEY", "work"),
                                                                        CancellationToken.None));
        Assert.That(client.DisposeCount, Is.EqualTo(0));

        // 运行中拒绝关闭；关闭不得释放正在使用的客户端。
        Assert.Throws<InvalidOperationException>(host.Dispose);
        Assert.That(client.DisposeCount, Is.EqualTo(0), "a racing close must not release the client in use");

        host.CancelRun();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        host.Dispose();
        Assert.That(client.DisposeCount, Is.EqualTo(1), "after the run settles, close releases the client exactly once");
    }

    [Test]
    public void BorrowedStartupClient_IsNotReleasedByHostDisposal()
    {
        // CLI 的 /new 宿主共用组合根的启动客户端：借用方式绑定，宿主释放不得影响其他宿主。
        var       client = new TrackingClient();
        var       session = new ChatSession(new string('b', 32), Path.GetTempPath(), Identity);
        var       context = new ConversationContext(Options().Context);
        using var host = new ChatSessionHost(session, context, Options(), client, new ToolRegistry([]),
                                             SystemPrompt, ownsModelClient : false);
        host.Dispose();
        Assert.That(client.DisposeCount, Is.EqualTo(0), "a borrowed startup client outlives host disposal");
    }

    [Test]
    public async Task UnboundHost_RefusesSendingUntilACandidateSelectionIsApplied()
    {
        var       session = new ChatSession(new string('c', 32), Path.GetTempPath(), Identity);
        var       context = new ConversationContext(Options().Context);
        session.RecordModelSelection(new SessionModelSelection
        {
            Revision                = 1,
            PreviousRevision        = 0,
            SelectedUtc             = DateTimeOffset.UtcNow,
            Model                   = Identity,
            CredentialRoute         = "work|env:WORK_KEY",
            Context                 = Options().Context,
            ReasoningMappingVersion = ReasoningEffortMappings.CurrentVersion,
            ProjectionVersion       = SessionModelSelection.CurrentProjectionVersion,
            MessageBoundary         = 0
        });
        using var host = new ChatSessionHost(session, context, Options(), modelClient : null,
                                             new ToolRegistry([]), SystemPrompt);

        Assert.That(host.HasModelBinding, Is.False);
        Assert.That(host.InspectionPending, Is.False);
        await TestAssert.ThrowsAsync<InvalidOperationException>(
            () => host.SubmitTurnAsync("too early", CancellationToken.None));

        var client = new TrackingClient();
        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Identity, client, Options().Context, null, "work|env:WORK_KEY", "work"),
            CancellationToken.None, expectedRevision : 1);
        Assert.That(host.HasModelBinding, Is.True);
        Assert.That(selection.Revision, Is.EqualTo(2));

        client.EnqueueAnswer();
        var run = await host.SubmitTurnAsync("now it works", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        Assert.That(client.Requests, Is.EqualTo(1));
    }

    [Test]
    public async Task ChangeModel_DoesNotClearNeedsInspection()
    {
        var snapshot = new PersistedSession
        {
            Id                      = new string('d', 32),
            WorkspaceRoot           = Path.GetTempPath(),
            Model                   = Identity,
            PendingInspectionReason = "inspect the workspace first",
            ModelSelections         = [
                new SessionModelSelection
                {
                    Revision                = 1,
                    PreviousRevision        = 0,
                    SelectedUtc             = DateTimeOffset.UtcNow,
                    Model                   = Identity,
                    CredentialRoute         = "work|env:WORK_KEY",
                    Context                 = Options().Context,
                    ReasoningMappingVersion = ReasoningEffortMappings.CurrentVersion,
                    ProjectionVersion       = SessionModelSelection.CurrentProjectionVersion,
                    MessageBoundary         = 1
                }
            ],
            Conversation            = new PersistedConversation
            {
                Messages = [new PersistedMessage { Role = "System", Content = SystemPrompt }]
            }
        };
        var restored = SessionResume.Restore(snapshot, Path.GetTempPath(), SystemPrompt);
        using var host = new ChatSessionHost(restored.Session, restored.Context, Options(), new TrackingClient(),
                                             new ToolRegistry([]), SystemPrompt,
                                             needsInspection : restored.NeedsInspection);

        Assert.That(host.InspectionPending, Is.True);
        var selection = await host.ChangeModelAsync(
            new SessionModelBinding(Other, new TrackingClient(), Options().Context, null, "work|env:WORK_KEY",
                                    "work"),
            CancellationToken.None);
        Assert.That(selection.Model, Is.EqualTo(Other));
        Assert.That(host.InspectionPending, Is.True, "switching models never lifts NeedsInspection");
        Assert.That(host.Session.PendingInspectionReason, Is.EqualTo("inspect the workspace first"));
    }

    /// <summary>
    ///     <para>可计数的假客户端：默认挂起直至取消（模拟长运行），可脚本一个立即完成的回答。</para>
    ///     <para>Counting fake client: hangs until cancelled by default (a long run), with an optional scripted immediate answer.</para>
    /// </summary>
    private sealed class TrackingClient(TaskCompletionSource? entered = null) : IChatCompletionClient, IDisposable
    {
        private readonly TaskCompletionSource _answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int  Requests       { get; private set; }
        public int  DisposeCount   { get; private set; }
        public bool ThrowOnDispose { get; set; }

        public void EnqueueAnswer() => _answer.TrySetResult();

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            entered?.TrySetResult();
            await (_answer.Task.IsCompleted
                ? Task.CompletedTask
                : Task.Delay(Timeout.Infinite, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "tracked answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose) throw new InvalidOperationException("client teardown failed");
        }
    }
}
