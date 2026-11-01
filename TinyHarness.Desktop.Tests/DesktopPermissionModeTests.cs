using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopPermissionModeTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    [Test]
    public async Task ReadOnlyCanApproveOneWriteAndNextWriteStillAsks()
    {
        var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<DesktopApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var model   = new ScriptModel([Patch("one.txt"), null, Patch("two.txt"), null]);
        var factory = new DesktopSessionFactory((_, _) => model, new Credentials(), fixture.Config);
        await using var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root),
                                                          _ => ValueTask.CompletedTask,
                                                          approval =>
                                                          {
                                                              if (approval is not null) pending.TrySetResult(approval);
                                                              return ValueTask.CompletedTask;
                                                          }, default);
        var first   = session.SubmitAsync("创建 one.txt");
        var request = await pending.Task.WaitAsync(Limit);
        Assert.That(File.Exists(Path.Combine(fixture.Root, "one.txt")), Is.False);
        session.Approvals.Respond(request.Id, ApprovalAction.AllowOnce);
        await first.WaitAsync(Limit);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Root, "one.txt")), Does.Contain("ok"));
        pending = new TaskCompletionSource<DesktopApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = session.SubmitAsync("创建 two.txt");
        request = await pending.Task.WaitAsync(Limit);
        Assert.That(request.Id, Is.Not.Empty);
        session.Approvals.Respond(request.Id, ApprovalAction.Deny);
        await second.WaitAsync(Limit);
        Assert.That(File.Exists(Path.Combine(fixture.Root, "two.txt")), Is.False);
        Assert.That(session.Host.Session.PermissionSelection!.Mode, Is.EqualTo(SessionPermissionMode.ReadOnly));
    }

    [Test]
    public async Task ProjectExternalWriteAsksAndApprovedInvocationCanExecute()
    {
        var fixture = await Fixture.CreateAsync();
        var outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside.txt");
        var call    = Patch(outside.Replace('\\', '/'));
        var factory =
            new DesktopSessionFactory((_, _) => new ScriptModel([call, null]), new Credentials(), fixture.Config);
        DesktopSession? session = null;
        var             asked   = 0;
        session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root)
                                              { PermissionMode = SessionPermissionMode.ProjectWrite },
                                          _ => ValueTask.CompletedTask, approval =>
                                          {
                                              if (approval is not null)
                                              {
                                                  asked++;
                                                  Assert.That(approval.TargetPaths, Does.Contain(outside));
                                                  Assert.That(File.Exists(outside), Is.False);
                                                  session!.Approvals.Respond(approval.Id, ApprovalAction.AllowOnce);
                                              }

                                              return ValueTask.CompletedTask;
                                          }, default);
        await using (session)
        {
            var run = await session.SubmitAsync("创建指定项目外文件").WaitAsync(Limit);
            Assert.That(run.Result!.Error, Is.Null);
            Assert.That(asked, Is.EqualTo(1));
            Assert.That(File.Exists(outside), Is.True);
        }
    }

    [Test]
    public async Task ModeSwitchPersistsAndReopenDoesNotReplaceItWithDefaultOrRequest()
    {
        var fixture = await Fixture.CreateAsync();
        var factory = new DesktopSessionFactory((_, _) => new ScriptModel([null]), new Credentials(), fixture.Config);
        string id;
        await using (var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root),
                                                           _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask,
                                                           default))
        {
            id = session.Id;
            await session.ChangePermissionModeAsync(SessionPermissionMode.ProjectWriteAutoReview);
            await session.ChangePermissionModeAsync(SessionPermissionMode.FullAccess);
            Assert.That(session.Host.Session.Turns, Is.Empty);
        }

        await using var restored = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root, SessionId : id)
                                                           {
                                                               PermissionMode = SessionPermissionMode.ConfirmEveryAction
                                                           },
                                                           _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask,
                                                           default);
        Assert.That(restored.Host.Session.PermissionSelection!.Mode, Is.EqualTo(SessionPermissionMode.FullAccess));
        Assert.That(restored.Host.Session.PermissionSelection.Revision, Is.EqualTo(3));
        await restored.ChangePermissionModeAsync(SessionPermissionMode.ReadOnly);
        var snapshot = await SessionStoreReader.ReadAsync(fixture.Store, id, default);
        Assert.That(snapshot!.PermissionSelection!.Mode, Is.EqualTo(SessionPermissionMode.ReadOnly));
    }

    [Test]
    public async Task WaitingApprovalMustBeCancelledBeforeSwitchAndStaleResponseCannotExecute()
    {
        var fixture = await Fixture.CreateAsync();
        var pending = new TaskCompletionSource<DesktopApproval>(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new DesktopSessionFactory((_, _) => new ScriptModel([Patch("cancelled.txt"), null]),
                                                new Credentials(), fixture.Config);
        await using var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root),
                                                          _ => ValueTask.CompletedTask,
                                                          approval =>
                                                          {
                                                              if (approval is not null) pending.TrySetResult(approval);
                                                              return ValueTask.CompletedTask;
                                                          }, default);
        var run     = session.SubmitAsync("创建 cancelled.txt");
        var request = await pending.Task.WaitAsync(Limit);
        Assert.Throws<InvalidOperationException>(() => session.ChangePermissionModeAsync(SessionPermissionMode
                                                    .FullAccess));
        session.Cancel();
        await run.WaitAsync(Limit);
        await session.ChangePermissionModeAsync(SessionPermissionMode.ProjectWrite);
        Assert.That(session.Approvals.Respond(request.Id, ApprovalAction.AllowOnce), Is.False);
        Assert.That(File.Exists(Path.Combine(fixture.Root, "cancelled.txt")), Is.False);
    }

    [TestCase("{\"decision\":\"allow\",\"reason\":\"required edit\"}", false)]
    [TestCase("{\"decision\":\"deny\",\"reason\":\"outside task\"}", true)]
    [TestCase("not json", true)]
    [TestCase("{\"decision\":\"allow\",\"decision\":\"deny\",\"reason\":\"duplicate\"}", true)]
    public async Task AiReviewIsIsolatedAndInvalidOrDeniedResultsFallBackToHuman(string output, bool fallback)
    {
        var                      reviewer   = new ReviewModel(output);
        DesktopApprovalProvider? human      = null;
        var                      humanCalls = 0;
        human = new DesktopApprovalProvider(approval =>
        {
            if (approval is not null)
            {
                humanCalls++;
                Assert.That(approval.ReviewNote, Does.Contain("操作尚未执行"));
                human!.Respond(approval.Id, ApprovalAction.Deny);
            }

            return ValueTask.CompletedTask;
        }, text => text);
        var provider = new DesktopAutoApprovalProvider(human, () => SessionPermissionMode.ReadOnlyAutoReview,
                                                       _ =>
                                                           Task.FromResult<(IChatCompletionClient, string)>((reviewer,
                                                               "reviewer")),
                                                       () => ["实现指定修改"], _ => ValueTask.CompletedTask);
        var result = await provider.PromptAsync(new ToolPreparation
        {
            ToolName  = "test", CallId = "test", Capability = "filesystem.write", Summary = "specific edit",
            Arguments = new JsonObject(), TargetPaths = []
        }, default).WaitAsync(Limit);
        Assert.That(result, Is.EqualTo(fallback ? ApprovalAction.Deny : ApprovalAction.AllowOnce));
        Assert.That(humanCalls, Is.EqualTo(fallback ? 1 : 0));
        Assert.That(reviewer.Request!.Tools, Is.Null);
        Assert.That(reviewer.Request.Messages, Has.Count.EqualTo(2));
        Assert.That(JsonNode.Parse(reviewer.Request.Messages[1].Content)!["userInstructions"]![0]!.GetValue<string>(),
                    Is.EqualTo("实现指定修改"));
        Assert.That(reviewer.Disposed, Is.True);
    }

    [Test]
    public async Task SettingsSaveVisibilityDefaultAndReviewerWithoutChangingExistingSession()
    {
        var fixture = await Fixture.CreateAsync();
        var editor  = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();
        Assert.That(editor.Modes, Has.Count.EqualTo(7));
        foreach (var row in editor.Modes) row.ShowInComposer = false;
        editor.DefaultMode     = editor.Modes.Single(row => row.Mode == SessionPermissionMode.ProjectWrite);
        editor.UseCurrentModel = false;
        editor.ReviewerModel   = editor.Models.Single();
        await editor.SaveAsync();
        var saved = (await UserConfigStore.LoadAsync(fixture.Config, default)).Settings!.Permissions!;
        Assert.That(saved.DefaultMode, Is.EqualTo(SessionPermissionMode.ProjectWrite));
        Assert.That(saved.ComposerModes, Is.EqualTo(new[] { SessionPermissionMode.ProjectWrite }));
        Assert.That(saved.UseCurrentModel, Is.False);
        await using var manager = new DesktopSessionManager();
        var             draft   = new SessionViewModel(manager, new DesktopSessionFactory(), fixture.Root);
        draft.ApplyPermissionSettings(saved);
        Assert.That(draft.PermissionMode, Is.EqualTo(SessionPermissionMode.ProjectWrite));
        await draft.SelectPermissionModeAsync(SessionPermissionMode.ReadOnly);
        draft.ApplyPermissionSettings(saved);
        Assert.That(draft.PermissionMode, Is.EqualTo(SessionPermissionMode.ReadOnly));
        Assert.That(draft.PermissionOptions, Has.Count.EqualTo(1));
        var history = new SessionViewModel(manager, new DesktopSessionFactory(), fixture.Root, new string('a', 32));
        history.ApplyStoredPermissionMode(SessionPermissionMode.FullAccess);
        history.ApplyPermissionSettings(saved);
        Assert.That(history.PermissionMode, Is.EqualTo(SessionPermissionMode.FullAccess));
    }

    private static ChatToolCall Patch(string path) => new(Guid.NewGuid().ToString("N"), "apply_patch",
                                                          new JsonObject
                                                          {
                                                              ["patch"] =
                                                                  $"--- /dev/null\n+++ {path}\n@@ -0,0 +1 @@\n+ok\n"
                                                          }.ToJsonString());

    [TestCase(SessionPermissionMode.ConfirmEveryAction, 1)]
    [TestCase(SessionPermissionMode.ReadOnly, 1)]
    [TestCase(SessionPermissionMode.ProjectWrite, 1)]
    [TestCase(SessionPermissionMode.FullAccess, 0)]
    public async Task ShellActuallyExecutesOnlyAfterRequiredApproval(SessionPermissionMode mode, int expectedApprovals)
    {
        var fixture = await Fixture.CreateAsync();
        var call = new ChatToolCall("shell-call", "shell", new JsonObject
        {
            ["mode"]    = "shell", ["shell"]                          = OperatingSystem.IsWindows() ? "cmd" : "sh",
            ["command"] = "echo permission-shell", ["executionScope"] = "host"
        }.ToJsonString());
        var factory =
            new DesktopSessionFactory((_, _) => new ScriptModel([call, null]), new Credentials(), fixture.Config);
        var             approvals = 0;
        DesktopSession? session   = null;
        session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root) { PermissionMode = mode },
                                          _ => ValueTask.CompletedTask, approval =>
                                          {
                                              if (approval is not null)
                                              {
                                                  approvals++;
                                                  Assert.That(approval.ExecutionPolicy, Does.Contain("backend=host"));
                                                  session!.Approvals.Respond(approval.Id, ApprovalAction.AllowOnce);
                                              }

                                              return ValueTask.CompletedTask;
                                          }, default);
        await using (session)
        {
            await session.SubmitAsync("执行 echo permission-shell").WaitAsync(Limit);
            Assert.That(approvals, Is.EqualTo(expectedApprovals));
            var snapshot = await SessionStoreReader.ReadAsync(fixture.Store, session.Id, default);
            Assert.That(snapshot!.Conversation!.Messages.Where(message => message.Role == "Tool")
                                 .Any(message => message.Content.Contains("permission-shell") &&
                                                 message.Content.Contains("Exit code: 0")), Is.True);
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FactoryUsesIndependentReviewerWithCurrentOrFixedModel(bool useCurrent)
    {
        var fixture = await Fixture.CreateAsync();
        var user    = await UserConfigStore.LoadAsync(fixture.Config, default);
        await UserConfigStore.SaveAsync(fixture.Config, user with
        {
            Profiles =
            [
                user.Profiles[0] with
                {
                    Models =
                    [
                        .. user.Profiles[0].Models,
                        new UserProfileModel { Id = "second", ContextWindowTokens = 128_000 }
                    ]
                }
            ],
            Settings = user.Settings! with
            {
                Permissions = new PermissionSettings
                {
                    UseCurrentModel = useCurrent, ReviewerProfile = "offline", ReviewerModel = "second"
                }
            }
        }, default);
        var requests = new List<(RoutingModel Client, ChatCompletionRequest Request)>();
        var factory =
            new DesktopSessionFactory((_, _) => new RoutingModel(requests), new Credentials(), fixture.Config);
        var humanCalls = 0;
        await using var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root)
                                                          {
                                                              PermissionMode = SessionPermissionMode.ReadOnlyAutoReview
                                                          }, _ => ValueTask.CompletedTask,
                                                          approval =>
                                                          {
                                                              if (approval is not null) humanCalls++;
                                                              return ValueTask.CompletedTask;
                                                          }, default);
        if (useCurrent)
            await session.ChangeModelAsync("offline", "second", null, session.Host.Session.ModelSelection!.Revision);
        await session.SubmitAsync("创建 approved.txt").WaitAsync(Limit);
        var review = requests.Single(item => item.Request.Tools is null);
        var worker = requests.First(item => item.Request.Tools is not null);
        Assert.That(review.Request.Model, Is.EqualTo("second"));
        Assert.That(worker.Request.Model, Is.EqualTo(useCurrent ? "second" : "offline"));
        Assert.That(review.Client, Is.Not.SameAs(worker.Client));
        Assert.That(review.Client.Disposed, Is.True);
        Assert.That(worker.Client.Disposed, Is.False);
        Assert.That(humanCalls, Is.Zero);
        Assert.That(File.Exists(Path.Combine(fixture.Root, "approved.txt")), Is.True);
    }

    [Test]
    public async Task SettingsConflictRetainsNewerProviderConfiguration()
    {
        var fixture = await Fixture.CreateAsync();
        var editor  = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();
        var latest = await UserConfigStore.LoadAsync(fixture.Config, default);
        await UserConfigStore.SaveAsync(fixture.Config, latest with
        {
            Profiles = [latest.Profiles[0] with { Endpoint = "https://new.invalid/v1" }]
        }, default);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await editor.SaveAsync());
        Assert.That((await UserConfigStore.LoadAsync(fixture.Config, default)).Profiles[0].Endpoint,
                    Is.EqualTo("https://new.invalid/v1"));
    }

    private sealed class RoutingModel(List<(RoutingModel Client, ChatCompletionRequest Request)> requests)
        : IChatCompletionClient, IDisposable
    {
        private bool _sent;
        public  bool Disposed { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            requests.Add((this, request));
            await Task.CompletedTask;
            if (request.Tools is null)
            {
                yield return new ChatStreamEvent
                {
                    Kind         = ChatStreamEventKind.ContentDelta,
                    ContentDelta = """{"decision":"allow","reason":"required edit"}"""
                };
            }
            else if (!_sent)
            {
                _sent = true;
                var call = Patch("approved.txt");
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                    ToolCallId             = call.Id, ToolCallFunctionName                    = call.FunctionName,
                    ToolCallArgumentsDelta = call.ArgumentsJson
                };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
                yield break;
            }
            else
            {
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            }

            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public void Dispose() => Disposed = true;
    }

    private sealed record Fixture(string Root, string Store, string Config)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var parent = Path.Combine(Path.GetTempPath(), "tinyharness-permissions-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(Path.Combine(parent, "workspace"), Path.Combine(parent, "sessions"),
                                      Path.Combine(parent, "user.json"));
            Directory.CreateDirectory(fixture.Root);
            await UserConfigStore.SaveAsync(fixture.Config, new UserConfig
            {
                DefaultProfile = "offline",
                Profiles =
                [
                    new UserProfile
                    {
                        Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = fixture.Store }
            }, default);
            return fixture;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                              => true;
        public string? Read(string   targetName)                => "offline-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class ScriptModel(IReadOnlyList<ChatToolCall?> calls) : IChatCompletionClient
    {
        private int _next;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            var call = _next < calls.Count ? calls[_next++] : null;
            if (call is not null)
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = 0,
                    ToolCallId             = call.Id, ToolCallFunctionName                    = call.FunctionName,
                    ToolCallArgumentsDelta = call.ArgumentsJson
                };
            else yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.End, FinishReason = call is null ? "stop" : "tool_calls" };
        }
    }

    private sealed class ReviewModel(string output) : IChatCompletionClient, IDisposable
    {
        public ChatCompletionRequest? Request  { get; private set; }
        public bool                   Disposed { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            Request = request;
            await Task.CompletedTask;
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = output };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
        }

        public void Dispose() => Disposed = true;
    }
}
