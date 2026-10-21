using TinyHarness.Cli.Prompts;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>检查内置提示词在工具轮、多轮、压缩和续聊中的接入，不评估模型的语义遵循能力。</para>
///     <para>
///         Checks built-in prompt delivery across tools, turns, compaction and resume, without evaluating model
///         adherence.
///     </para>
/// </summary>
public class WorkspacePromptTests
{
    private static readonly SessionModelIdentity Identity =
        new("prompt-test-model", "https://example.invalid/v1", ChatApiKind.ChatCompletions);

    [Test]
    public async Task OneShotRun_SendsCurrentPromptAndDeclaredToolSchema()
    {
        using var workspace = new TestTempDir();
        workspace.WriteFile("sample.txt", "file evidence");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"sample.txt"}"""));
        client.Enqueue(FakeChatClient.Text("scripted assessment"));
        var tools = new ToolRegistry([new ReadFileTool(workspace.Workspace)]);
        var loop  = new AgentLoop(client, tools, Options(), new PermissionEngine(workspace.Root), null);

        var result = await loop.RunAsync(WorkspacePrompts.Current, "只分析这个文件，不修改。", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(client.RequestLog.Count, Is.EqualTo(2));
        foreach (var request in client.RequestLog)
        {
            AssertPrompt(request, WorkspacePrompts.Current);
            Assert.That(request.Tools!.Select(tool => tool.Name), Is.EqualTo(new[] { "read_file" }));
        }

        Assert.That(client.RequestLog[1].Messages.Any(message =>
            message.Role == ChatRole.Tool && message.Content.Contains("file evidence")), Is.True);
    }

    [Test]
    public async Task Chat_RoundTripKeepsCurrentPromptAndSeparatelySourcedProjectRules()
    {
        using var workspace = new TestTempDir();
        using var store     = new TestTempDir();
        workspace.WriteFile("AGENTS.md", "PROJECT_RULE_FOR_PROMPT_TEST");
        workspace.WriteFile("sample.txt", "file evidence");
        const string prompt = WorkspacePrompts.Current;
        var          loader = new ProjectInstructionLoader(workspace.Workspace);
        var          client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"sample.txt"}"""));
        client.Enqueue(FakeChatClient.Text("scripted assessment"));
        client.Enqueue(FakeChatClient.Text("scripted follow-up"));
        var    tools = new ToolRegistry([new ReadFileTool(workspace.Workspace)]);
        string sessionId;
        using (var cancellation = new ChatCancellation(false))
        using (var console = new ChatSessionConsole(async token =>
               {
                   var initial = await loader.LoadAsync(null, [], token);
                   return new ChatSessionHost(workspace.Root, Identity, Options(), client, tools, prompt,
                                              new PermissionEngine(workspace.Root),
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              instructionLoader : loader, initialInstructions : initial);
               }, new StringReader("只分析，不修改。\n继续讨论。\n"), new StringWriter(), cancellation))
        {
            Assert.That(await console.RunAsync(), Is.EqualTo(0));
            sessionId = console.Session.Id;
            Assert.That(console.Session.Turns.Count, Is.EqualTo(2));
        }

        foreach (var request in client.RequestLog)
        {
            AssertPrompt(request, prompt);
            Assert.That(request.Messages.Count(message => message.Content.Contains("PROJECT_RULE_FOR_PROMPT_TEST")),
                        Is.EqualTo(1));
            Assert.That(request.Messages[0].Content, Does.Not.Contain("PROJECT_RULE_FOR_PROMPT_TEST"));
        }

        using var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                      CancellationToken.None);
        var snapshot = writer.LoadedSnapshot!;
        Assert.That(snapshot.Conversation!.Messages[0].Content, Is.EqualTo(prompt));
        var canonical     = snapshot.Conversation.Messages.ToArray();
        var restored      = SessionResume.Restore(snapshot, workspace.Root, Identity, prompt, new ContextOptions());
        var resumedClient = new FakeChatClient();
        resumedClient.Enqueue(FakeChatClient.Text("scripted resumed answer"));
        using var host = new ChatSessionHost(restored.Session, restored.Context, Options(), resumedClient, tools,
                                             prompt, new PermissionEngine(workspace.Root), sessionWriter : writer,
                                             instructionLoader : loader);

        var run = await host.SubmitTurnAsync("上次读到了什么？", CancellationToken.None);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        AssertPrompt(resumedClient.RequestLog.Single(), prompt);
        Assert.That(restored.Context.Messages.Take(canonical.Length).Select(message => message.Content),
                    Is.EqualTo(canonical.Select(message => message.Content)));
        Assert.That(resumedClient.RequestLog.Single().Messages
                                 .Count(message => message.Content.Contains("PROJECT_RULE_FOR_PROMPT_TEST")),
                    Is.EqualTo(1));
    }

    [Test]
    public void CompactionAndContextRestore_PreserveCurrentPromptWithoutRewritingCanonicalHistory()
    {
        var options = new ContextOptions { ContextWindowTokens = 8_000, ReservedOutputTokens = 256 };
        var context = new ConversationContext(options);
        context.Append(ChatMessage.System(WorkspacePrompts.Current));
        context.Append(ChatMessage.User("只分析，不修改。"));
        for (var index = 0; index < 8; index++)
        {
            var id = "read-" + index;
            context.Append(ChatMessage.Assistant("", [new ChatToolCall(id, "read_file", "{}")]));
            context.Append(ChatMessage.Tool("read_file", id, new string('x', 5_000)));
        }

        var canonical = context.Messages.ToArray();
        Assert.That(context.RequiresCompaction([]), Is.True);
        Assert.That(context.BuildCompactionMessages([]), Is.Not.Empty);
        Assert.That(context.TryApplyCompaction("只分析，不修改；不要修改文件。"),
                    Is.True);
        Assert.That(context.Messages, Is.EqualTo(canonical));
        var view = context.BuildModelView();
        Assert.That(view.Count, Is.LessThan(canonical.Length));
        Assert.That(view[0], Is.EqualTo(ChatMessage.System(WorkspacePrompts.Current)));

        var restored = new ConversationContext(options);
        restored.Restore(context.Messages.ToArray(), context.State, context.Position.HasState,
                         context.Position.FoldStartMessageIndex, context.Position.FoldEndMessageIndex);
        Assert.That(restored.BuildModelView(), Is.EqualTo(view));
        Assert.That(restored.BuildModelView().Count(message => message.Role == ChatRole.System), Is.EqualTo(1));
    }

    [TestCase("unknown")]
    [TestCase("changed-current")]
    [TestCase("forged-role")]
    [TestCase("empty")]
    public void Resume_RejectsUnknownOrMalformedPrompts(string kind)
    {
        using var workspace = new TestTempDir();
        var snapshot = new PersistedSession
        {
            WorkspaceRoot = workspace.Root,
            Model         = Identity,
            Conversation = new PersistedConversation
            {
                Messages = kind == "empty"
                    ? []
                    :
                    [
                        new PersistedMessage
                        {
                            Role = kind == "forged-role" ? nameof(ChatRole.User) : nameof(ChatRole.System),
                            Content = kind == "unknown"
                                ? "unrecognized prompt"
                                : WorkspacePrompts.Current + (kind == "changed-current" ? " changed" : "")
                        }
                    ]
            }
        };

        Assert.Throws<SessionStoreException>(() =>
            SessionResume.Restore(snapshot, workspace.Root, Identity, WorkspacePrompts.Current));
    }

    /// <summary>
    ///     <para>建立不调用真实模型的默认回合配置。</para>
    ///     <para>Creates default turn options without calling a real model.</para>
    /// </summary>
    private static AgentOptions Options() => new()
    {
        Model                     = Identity.ModelId,
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    /// <summary>
    ///     <para>检查普通模型请求中唯一且完整的系统提示词。</para>
    ///     <para>Checks the sole, complete system prompt in an ordinary model request.</para>
    /// </summary>
    private static void AssertPrompt(ChatCompletionRequest request, string expected)
    {
        Assert.That(request.Messages[0].Content, Is.EqualTo(expected));
        Assert.That(request.Messages[0].Role, Is.EqualTo(ChatRole.System));
        Assert.That(request.Messages.Count(message => message.Role == ChatRole.System), Is.EqualTo(1));
    }
}
