using System.Threading.Channels;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证交互入口的会话选择、上下文隔离、锁交接和运行中切换。</para>
///     <para>Verifies session selection, context isolation, lock handoff, and switching during a run.</para>
/// </summary>
public class SessionNavigationTests
{
    private static readonly TimeSpan Timeout  = TimeSpan.FromSeconds(15);
    private static readonly string   FirstId  = new('a', 32);
    private static readonly string   SecondId = new('b', 32);

    [Test]
    public void NoArguments_StartsTheInteractiveEntry() =>
        Assert.That(CommandLine.Parse([]).Kind, Is.EqualTo(CliCommandKind.Chat));

    [TestCase("")]
    [TestCase("/sessions\n/new extra\n/sessions extra\n/switch\n/switch ../escape\n/switch a b\n")]
    public async Task StartupAndInvalidCommands_DoNotCreateSessionsOrCallTheModel(string input)
    {
        using var fixture      = new NavigationFixture();
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        using var console      = fixture.Console(new StringReader(input), output, cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(console.Host, Is.Null);
        Assert.That(fixture.Created, Is.Empty);
        Assert.That(fixture.Client.Requests, Is.Zero);
        Assert.That(output.ToString(), Does.Contain("Sessions (* current)"));
    }

    [Test]
    public async Task New_EntersBlankWithoutCreatingAndFirstMessageCreatesExactlyOneSession()
    {
        using var fixture      = new NavigationFixture();
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        using (var console = fixture.Console(new StringReader("/new\n/new\n/sessions\n\n   \n"), output, cancellation))
        {
            Assert.That(await console.RunAsync(), Is.Zero);
            Assert.That(console.Host, Is.Null);
            Assert.That(fixture.Created, Is.Empty);
            Assert.That(fixture.Client.Requests, Is.Zero);
            Assert.That(output.ToString(), Does.Contain("new session: blank"));
            Assert.That(output.ToString(), Does.Contain("(no stored sessions; type a request to create one)"));
        }

        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix), Is.Empty);

        fixture.Client.Enqueue(FakeChatClient.Text("first answer"));
        fixture.Client.Enqueue(FakeChatClient.Text("second answer"));
        using var established = fixture.Console(new StringReader("/new\nfirst question\nsecond question\n"),
                                                new StringWriter(), cancellation);
        Assert.That(await established.RunAsync(), Is.Zero);
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(established.Session.Id, Is.EqualTo(FirstId));
        Assert.That(established.Session.Turns.Select(turn => turn.UserInput),
                    Is.EqualTo(["first question", "second question"]));
        Assert.That(fixture.Client.Requests, Is.EqualTo(2));
        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix),
                    Is.EqualTo([Path.Combine(fixture.Store.Root, FirstId + SessionStoreWriter.LogFileSuffix)]));
    }

    [Test]
    public async Task New_AfterEstablishedSession_ReleasesHostAndAddsNoStoredSessionOnExit()
    {
        using var fixture = new NavigationFixture();
        fixture.Client.Enqueue(FakeChatClient.Text("answer"));
        using var cancellation = new ChatCancellation(false);
        using var console = fixture.Console(new StringReader("question\n/new\n"), new StringWriter(), cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(console.Host, Is.Null);
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix)
                             .Select(Path.GetFileName),
                    Is.EqualTo([FirstId + SessionStoreWriter.LogFileSuffix]));
    }

    [Test]
    public async Task Switch_RestoresHistoryAndReleasesPreviousWriterWithoutMixingContexts()
    {
        using var fixture = new NavigationFixture();
        fixture.Client.Enqueue(FakeChatClient.ReasonedText("thought", "item", "opaque", "first answer"));
        fixture.Client.Enqueue(FakeChatClient.Text("second answer"));
        fixture.Client.Enqueue(FakeChatClient.Text("followup answer"));
        using var cancellation = new ChatCancellation(false);
        using var console =
            fixture.Console(new
                                StringReader($"/new\nfirst question\n/new\nsecond question\n/switch {FirstId.ToUpperInvariant()}\nfollowup\n"),
                            new StringWriter(), cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(console.Session.Id, Is.EqualTo(FirstId));
        Assert.That(console.Session.Turns.Select(turn => turn.UserInput),
                    Is.EqualTo(["first question", "followup"]));
        Assert.That(fixture.Client.RequestLog[1].Messages.Select(message => message.Content),
                    Does.Not.Contain("first question"));
        var final = fixture.Client.RequestLog[2].Messages;
        Assert.That(final.Select(message => message.Content),
                    Does.Contain("first question").And.Not.Contain("second question"));
        Assert.That(final.Single(message => message.Role == ChatRole.Assistant).Reasoning!.Single().ProtectedData,
                    Is.EqualTo("opaque"));
        using var released = await fixture.ResumeAsync(SecondId, default);
        Assert.That(released.Session.Turns.Single().UserInput, Is.EqualTo("second question"));
    }

    [Test]
    public async Task IdleSwitch_RoundTripsBothConversationsWithoutConfirmationOrManualSave()
    {
        using var fixture = new NavigationFixture();
        foreach (var answer in new[] { "A answer 1", "A answer 2", "B answer 1", "A answer 3", "B answer 2" })
            fixture.Client.Enqueue(FakeChatClient.Text(answer));

        using var cancellation = new ChatCancellation(false);
        var input = string.Join('\n', "/new", "A question 1", "A question 2", "/new", "B question 1",
                                $"/switch {FirstId}", "A question 3", $"/switch {SecondId}", "B question 2",
                                $"/switch {FirstId}");
        using var console = fixture.Console(new StringReader(input), new StringWriter(), cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);

        // 所有输入仅包含业务请求与切换命令；额外确认读取会吞掉后续请求，使此断言失败。
        Assert.That(fixture.Client.Requests, Is.EqualTo(5));
        Assert.That(console.Session.Id, Is.EqualTo(FirstId));
        Assert.That(console.Session.Turns.Select(turn => turn.UserInput),
                    Is.EqualTo(["A question 1", "A question 2", "A question 3"]));
        var first  = (await SessionStoreReader.ReadAsync(fixture.Store.Root, FirstId, default))!;
        var second = (await SessionStoreReader.ReadAsync(fixture.Store.Root, SecondId, default))!;
        Assert.That(first.Turns.Select(turn => turn.UserInput),
                    Is.EqualTo(["A question 1", "A question 2", "A question 3"]));
        Assert.That(second.Turns.Select(turn => turn.UserInput),
                    Is.EqualTo(["B question 1", "B question 2"]));
        Assert.That(first.Turns.SelectMany(turn => turn.Runs).Select(run => run.FinalMessage),
                    Is.EqualTo(["A answer 1", "A answer 2", "A answer 3"]));
        Assert.That(second.Turns.SelectMany(turn => turn.Runs).Select(run => run.FinalMessage),
                    Is.EqualTo(["B answer 1", "B answer 2"]));
        Assert.That(fixture.Client.RequestLog[3].Messages.Where(message => message.Role == ChatRole.User)
                           .Select(message => message.Content),
                    Is.EqualTo(["A question 1", "A question 2", "A question 3"]));
        Assert.That(fixture.Client.RequestLog[4].Messages.Where(message => message.Role == ChatRole.User)
                           .Select(message => message.Content),
                    Is.EqualTo(["B question 1", "B question 2"]));

        // 已离开的会话可以像启动恢复一样重新打开，不依赖旧宿主仍驻留内存。
        using var reopened = await fixture.ResumeAsync(SecondId, default);
        Assert.That(reopened.Session.Turns.Count, Is.EqualTo(2));
    }

    [TestCase("missing")]
    [TestCase("damaged")]
    [TestCase("locked")]
    [TestCase("identity")]
    [TestCase("nokey")]
    public async Task FailedSwitch_KeepsCurrentSessionUsable(string reason)
    {
        using var        fixture  = new NavigationFixture();
        var              targetId = new string('c', 32);
        ChatSessionHost? locked   = null;
        try
        {
            switch (reason)
            {
                case "damaged" :
                    await File.WriteAllTextAsync(Path.Combine(fixture.Store.Root,
                                                              targetId + SessionStoreWriter.LogFileSuffix), "broken\n");
                    break;
                case "nokey" :
                    // 恢复按最后选择解析目标 profile 密钥：解析失败按 ConfigException 报告，不得中断输入循环。
                    fixture.FailResumeWithConfigError = true;
                    break;
                case "locked" or "identity" :
                {
                    locked = await fixture.CreateStoredAsync(targetId,
                                                             reason == "identity"
                                                                 ? fixture.Identity with { ModelId = "other" }
                                                                 : fixture.Identity, default);
                    if (reason == "identity")
                    {
                        locked.Dispose();
                        locked = null;
                    }

                    break;
                }
            }

            fixture.Client.Enqueue(FakeChatClient.Text("before switching"));
            fixture.Client.Enqueue(FakeChatClient.Text("still here"));
            using var cancellation = new ChatCancellation(false);
            var       output       = new StringWriter();
            using var console =
                fixture.Console(new StringReader($"/new\nfirst question\n/switch {targetId}\nkeep going\n"),
                                output, cancellation);
            Assert.That(await console.RunAsync(), Is.Zero);
            Assert.That(console.Session.Id, Is.EqualTo(FirstId));
            Assert.That(console.Session.Turns.Select(turn => turn.UserInput),
                        Is.EqualTo(["first question", "keep going"]));
            Assert.That(fixture.Created.Count, Is.EqualTo(1));
            Assert.That(fixture.Client.Requests, Is.EqualTo(2));
            Assert.That(fixture.Client.RequestLog[1].Messages.Where(message => message.Role == ChatRole.User)
                               .Select(message => message.Content), Is.EqualTo(["first question", "keep going"]));
            Assert.That(output.ToString(), Does.Contain("Session command failed:"));
        }
        finally
        {
            locked?.Dispose();
        }
    }

    [TestCase("/new")]
    [TestCase("/switch")]
    public async Task DuringRun_SessionChangeWaitsForTerminalPersistence(string command)
    {
        using var fixture = new NavigationFixture();
        var       gate    = new GateChatClient();
        fixture.Model = gate;
        if (command == "/switch")
        {
            using var target = await fixture.CreateStoredAsync(SecondId, fixture.Identity, default);
        }

        using var input        = new NavigationInput();
        using var router       = new ChatInputRouter(input);
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        using var console      = fixture.Console(router, output, cancellation);
        input.Lines.Writer.TryWrite("start");
        var       running = console.RunAsync();
        using var timeout = new CancellationTokenSource(Timeout);
        await gate.WaitStartedAsync(timeout.Token);
        input.Lines.Writer.TryWrite("/sessions");
        input.Lines.Writer.TryWrite(command == "/new" ? command : $"/switch {SecondId}");
        if (command == "/switch") await fixture.SecondSelected.Task.WaitAsync(Timeout);
        input.Lines.Writer.Complete();
        Assert.That(await running.WaitAsync(Timeout), Is.Zero);
        var saved = (await SessionStoreReader.ReadAsync(fixture.Store.Root, FirstId, default))!;
        Assert.That(saved.Turns.Single().Runs.Single().Status, Is.EqualTo(nameof(AgentStatus.Cancelled)));
        Assert.That(saved.Amendments, Is.Empty);
        Assert.That(gate.Requests, Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("waiting for the current turn to finish cleanup"));
        if (command == "/new")
        {
            // 排队的 /new 在回合终态保存后进入空白态：不创建第二个会话或日志。
            Assert.That(console.Host, Is.Null);
            Assert.That(fixture.Created.Count, Is.EqualTo(1));
            Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix)
                                 .Select(Path.GetFileName),
                        Is.EqualTo([FirstId + SessionStoreWriter.LogFileSuffix]));
        }
        else
        {
            Assert.That(console.Session.Id, Is.EqualTo(SecondId));
            Assert.That(console.Session.Turns, Is.Empty);
        }
    }

    [Test]
    public async Task New_DuringApprovalCancelsThePromptAndNeverExecutesTheTool()
    {
        using var fixture = new NavigationFixture();
        var       tool    = new FakeTool("change");
        fixture.Tools = new ToolRegistry([tool]);
        fixture.Client.Enqueue(FakeChatClient.ToolCall("change", "{}"));
        using var input        = new NavigationInput();
        using var router       = new ChatInputRouter(input);
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        var       approver     = new SignaledApprover(new ConsoleApprovalProvider(router, output));
        fixture.Approver = approver;
        using var console = fixture.Console(router, output, cancellation);
        input.Lines.Writer.TryWrite("change it");
        var running = console.RunAsync();
        await approver.Entered.Task.WaitAsync(Timeout);
        input.Lines.Writer.TryWrite("/new");
        input.Lines.Writer.Complete();
        Assert.That(await running.WaitAsync(Timeout), Is.Zero);
        Assert.That(tool.ExecuteCount, Is.Zero);
        // 审批被 /new 取消后进入空白态：原会话以取消终态可靠保存，不创建第二个会话。
        Assert.That(console.Host, Is.Null);
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("new session: blank"));
        var saved = (await SessionStoreReader.ReadAsync(fixture.Store.Root, FirstId, default))!;
        Assert.That(saved.Turns.Single().Runs.Single().Status, Is.EqualTo(nameof(AgentStatus.Cancelled)));
        Assert.That(saved.Amendments, Is.Empty);
    }

    [Test]
    public async Task New_RefusesToDiscardChangesAfterPersistenceFailure()
    {
        using var fixture = new NavigationFixture { FailAfterRequest = true };
        fixture.Client.Enqueue(FakeChatClient.Text("answer"));
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        using var console      = fixture.Console(new StringReader("question\n/new\n"), output, cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(console.Host!.PersistenceWarning, Is.Not.Null);
        Assert.That(console.Session.Id, Is.EqualTo(FirstId));
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(output.ToString(), Does.Contain("Cannot leave this session"));
    }

    [Test]
    public async Task New_FromBlankState_SwitchResumesStoredHistoryAndContinues()
    {
        using var fixture  = new NavigationFixture();
        var       targetId = new string('c', 32);
        var       stored   = await fixture.CreateStoredAsync(targetId, fixture.Identity, default);
        stored.Dispose();
        fixture.Client.Enqueue(FakeChatClient.Text("followup answer"));
        using var cancellation = new ChatCancellation(false);
        using var console = fixture.Console(new StringReader($"/new\n/switch {targetId}\nfollowup\n"),
                                            new StringWriter(), cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(fixture.Created, Is.Empty);
        Assert.That(console.Session.Id, Is.EqualTo(targetId));
        Assert.That(console.Session.Turns.Select(turn => turn.UserInput), Is.EqualTo(["followup"]));
    }

    [Test]
    public async Task New_ThenPreflightFailure_StaysBlankWithoutStoreUntilSuccessfulSend()
    {
        using var fixture = new NavigationFixture { FailNextCreate = true };
        fixture.Client.Enqueue(FakeChatClient.Text("accepted"));
        using var cancellation = new ChatCancellation(false);
        var       output       = new StringWriter();
        using var console      = fixture.Console(new StringReader("/new\nrejected\naccepted\n"), output, cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(output.ToString(), Does.Contain("cannot start a turn: injected create failure"));
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(console.Session.Id, Is.EqualTo(FirstId));
        Assert.That(console.Session.Turns.Select(turn => turn.UserInput), Is.EqualTo(["accepted"]));
        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix)
                             .Select(Path.GetFileName),
                    Is.EqualTo([FirstId + SessionStoreWriter.LogFileSuffix]));
    }

    [Test]
    public async Task New_ThenFirstSendConfigError_KeepsBlankStateForSessionsAndRetry()
    {
        using var fixture = new NavigationFixture { FailNextCreateWithConfigError = true };
        fixture.Client.Enqueue(FakeChatClient.Text("accepted"));
        using var cancellation = new ChatCancellation(false);
        var       output = new StringWriter();
        using var console = fixture.Console(new StringReader("/new\nfirst\n/sessions\nretry\n"), output, cancellation);
        Assert.That(await console.RunAsync(), Is.Zero);
        // 缺凭据草稿只在首发工厂解析凭据：失败按既有错误报告，不退出输入循环，空白态可重选或重试。
        Assert.That(output.ToString(),
                    Does.Contain("cannot start a turn: No API key found for provider 'work'"));
        Assert.That(output.ToString(), Does.Contain("Sessions (* current)"));
        Assert.That(fixture.Created.Count, Is.EqualTo(1));
        Assert.That(console.Session.Id, Is.EqualTo(FirstId));
        Assert.That(console.Session.Turns.Select(turn => turn.UserInput), Is.EqualTo(["retry"]));
        Assert.That(fixture.Client.Requests, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix)
                             .Select(Path.GetFileName),
                    Is.EqualTo([FirstId + SessionStoreWriter.LogFileSuffix]));
    }

    [Test]
    public async Task New_ThenIdleCancellation_CreatesNoSessionOrLog()
    {
        using var fixture      = new NavigationFixture();
        using var cancellation = new ChatCancellation(false);
        using var input        = new NavigationInput();
        using var router       = new ChatInputRouter(input);
        var       output       = new StringWriter();
        using var console      = fixture.Console(router, output, cancellation);
        input.Lines.Writer.TryWrite("/new");
        var running  = console.RunAsync();
        var deadline = Task.Delay(Timeout);
        while (!output.ToString().Contains("new session: blank"))
            Assert.That(await Task.WhenAny(deadline, Task.Delay(20)), Is.Not.EqualTo(deadline),
                        "The blank prompt was not shown.");

        cancellation.RaiseCancel();
        input.Lines.Writer.Complete();
        Assert.That(await running.WaitAsync(Timeout), Is.EqualTo(130));
        Assert.That(fixture.Created, Is.Empty);
        Assert.That(Directory.GetFiles(fixture.Store.Root, "*" + SessionStoreWriter.LogFileSuffix), Is.Empty);
    }

    [Test]
    public async Task Router_DrainsCapturedControlBeforeReturningToIdle()
    {
        using var input   = new NavigationInput();
        using var router  = new ChatInputRouter(input);
        var       entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        input.Lines.Writer.TryWrite("initial");
        Assert.That(await router.ReadLineAsync(default), Is.EqualTo("initial"));
        router.SetActiveHandler(async _ =>
        {
            entered.SetResult();
            await release.Task;
        });
        input.Lines.Writer.TryWrite("/new");
        await entered.Task.WaitAsync(Timeout);
        var drained = router.ClearActiveHandlerAsync();
        Assert.That(drained.IsCompleted, Is.False);
        release.SetResult();
        await drained.WaitAsync(Timeout);
    }

    private sealed class SignaledApprover(IApprovalProvider inner) : IApprovalProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            var pending = inner.PromptAsync(preparation, cancellationToken);
            Entered.TrySetResult();
            return await pending;
        }
    }

    private sealed class NavigationInput : TextReader
    {
        internal Channel<string> Lines { get; } = Channel.CreateUnbounded<string>();

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            await Lines.Reader.WaitToReadAsync(cancellationToken)
                ? await Lines.Reader.ReadAsync(cancellationToken)
                : null;
    }

    private sealed class NavigationFixture : IDisposable
    {
        internal TestTempDir            Store            { get; } = new();
        private  TestTempDir            Workspace        { get; } = new();
        internal FakeChatClient         Client           { get; } = new();
        internal IChatCompletionClient? Model            { get; set; }
        internal ToolRegistry           Tools            { get; set; } = new([]);
        internal IApprovalProvider?     Approver         { get; set; }
        internal bool                   FailAfterRequest { get; init; }

        internal bool FailResumeWithConfigError { get; set; }

        /// <summary>
        ///     <para>下一次创建模拟发送前预检失败，触发后自动复位。</para>
        ///     <para>Simulates a preflight failure on the next creation, then resets automatically.</para>
        /// </summary>
        internal bool FailNextCreate { get; set; }

        /// <summary>
        ///     <para>下一次创建抛出配置异常以模拟草稿缺凭据，触发后自动复位。</para>
        ///     <para>Throws a configuration exception on the next creation to simulate missing draft credentials, then resets.</para>
        /// </summary>
        internal bool FailNextCreateWithConfigError { get; set; }

        internal List<ChatSessionHost> Created { get; } = [];
        internal TaskCompletionSource SecondSelected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal SessionModelIdentity Identity { get; } =
            new("test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

        public void Dispose()
        {
            Store.Dispose();
            Workspace.Dispose();
        }

        internal ChatSessionConsole Console(TextReader input, TextWriter output, ChatCancellation cancellation) =>
            new(CreateAsync, input, output, cancellation, resumeFactory : ResumeAsync,
                sessionStoreDirectory : Store.Root);

        private async Task<ChatSessionHost> CreateAsync(CancellationToken token)
        {
            if (FailNextCreate)
            {
                FailNextCreate = false;
                throw new InvalidDataException("injected create failure");
            }

            if (FailNextCreateWithConfigError)
            {
                FailNextCreateWithConfigError = false;
                throw new
                    ConfigException("No API key found for provider 'work'. Run 'tinyharness auth set work' or set the environment variable 'WORK_KEY' before switching.");
            }

            var host = await CreateStoredAsync(Created.Count == 0 ? FirstId : SecondId, Identity, token);
            Created.Add(host);
            if (Created.Count == 2) SecondSelected.TrySetResult();
            return host;
        }

        internal async Task<ChatSessionHost> CreateStoredAsync(
            string id, SessionModelIdentity identity, CancellationToken token)
        {
            var session = new ChatSession(id, Workspace.Root, identity);
            var context = new ConversationContext();
            context.Append(ChatMessage.System("sys"));
            var writer = new SessionStoreWriter(Store.Root, id)
            {
                InjectWriteFault = (_, _) =>
                {
                    if (FailAfterRequest && Client.Requests > 0) throw new IOException("injected save failure");
                }
            };
            try
            {
                await writer.CommitAsync(session, context, token);
                return Host(session, context, writer);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        internal async Task<ChatSessionHost> ResumeAsync(string id, CancellationToken token)
        {
            if (FailResumeWithConfigError)
                throw new
                    ConfigException("No API key found for provider 'work'. Run 'tinyharness auth set work' or set the environment variable 'WORK_KEY' before switching.");

            var writer = await SessionStoreWriter.OpenExistingAsync(Store.Root, id, null, token);
            try
            {
                var restored = SessionResume.Restore(writer.LoadedSnapshot!, Workspace.Root, Identity, "sys");
                var host     = Host(restored.Session, restored.Context, writer);
                if (id == SecondId) SecondSelected.TrySetResult();
                return host;
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        private ChatSessionHost Host(ChatSession session, ConversationContext context, ISessionStoreWriter writer) =>
            new(session, context,
                new AgentOptions { Model = session.Model.ModelId, MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30 },
                Model ?? Client, Tools, "sys", new PermissionEngine(Workspace.Root), Approver, sessionWriter : writer);
    }
}
