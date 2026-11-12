using System.Runtime.CompilerServices;
using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopLazySandboxTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChatAndFileTools_DoNotInitializeSandbox(bool readFile)
    {
        using var fixture = await Fixture.CreateAsync();
        var       scans   = 0;
        var factory = fixture.Factory(new ScriptModel(readFile ? "read_file" : null, """{"path":"ordinary.txt"}"""),
                                      _ =>
                                      {
                                          scans++;
                                          throw new InvalidOperationException("A chat must never scan the profile.");
                                      });
        await using var session = await fixture.OpenAsync(factory);
        var             run     = await session.SubmitAsync("hello").WaitAsync(Limit);

        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(readFile ? 1 : 0));
        Assert.That(scans, Is.Zero);
        Assert.That(Directory.Exists(fixture.SandboxHome), Is.False, "聊天不能创建沙箱运行目录。");
        var persisted = await SessionStoreReader.ReadAsync(fixture.Store, session.Id, default);
        Assert.That(persisted!.Conversation!.Messages.Any(message => message.Role    == "User" &&
                                                                     message.Content == "hello"), Is.True);
    }

    [Test]
    public async Task FirstSandboxTool_ScanFailureLeavesToolUnexecutedAndChatUsable()
    {
        using var fixture = await Fixture.CreateAsync();
        var       scans   = 0;
        var factory = fixture.Factory(new ScriptModel("shell", ShellArguments), _ =>
        {
            scans++;
            throw new InvalidOperationException("enumeration budget exhausted");
        });
        await using var session = await fixture.OpenAsync(factory);
        Assert.That(scans, Is.Zero);
        var run = await session.SubmitAsync("try shell").WaitAsync(Limit);
        Assert.That(scans, Is.EqualTo(1));
        Assert.That(run.Result!.ToolExecutions, Is.Zero);
        Assert.That(run.Result.ToolCalls!.Single().PreparationError, Does.Contain("enumeration budget exhausted"));
        Assert.That(Directory.Exists(fixture.SandboxHome), Is.False);

        var chat = await session.SubmitAsync("plain chat afterwards").WaitAsync(Limit);
        Assert.That(chat.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(scans, Is.EqualTo(1));
    }

    [Test]
    public async Task StopDuringScan_CancelsPreparationAndAllowsFollowingChat()
    {
        using var fixture = await Fixture.CreateAsync();
        var       entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var       exited  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = fixture.Factory(new ScriptModel("shell", ShellArguments), token =>
        {
            entered.TrySetResult();
            try
            {
                token.WaitHandle.WaitOne();
                token.ThrowIfCancellationRequested();
                return null;
            }
            finally
            {
                exited.TrySetResult();
            }
        });
        await using var session = await fixture.OpenAsync(factory);
        var             running = session.SubmitAsync("start scan");
        await entered.Task.WaitAsync(Limit);
        Assert.That(session.Cancel(), Is.True);
        var run = await running.WaitAsync(Limit);
        await exited.Task.WaitAsync(Limit);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Cancelled));
        Assert.That(run.Result.ToolExecutions, Is.Zero);
        Assert.That(Directory.Exists(fixture.SandboxHome), Is.False);

        var chat = await session.SubmitAsync("continue chatting").WaitAsync(Limit);
        Assert.That(chat.Result!.Status, Is.EqualTo(AgentStatus.Completed));
    }

    [Test]
    public async Task MissingSandboxComponents_AreRejectedOnlyWhenToolIsRequested()
    {
        using var       fixture = await Fixture.CreateAsync();
        var             factory = fixture.Factory(new ScriptModel("shell", ShellArguments), _ => null);
        await using var session = await fixture.OpenAsync(factory);
        var             run     = await session.SubmitAsync("try missing sandbox").WaitAsync(Limit);
        Assert.That(run.Result!.ToolExecutions, Is.Zero);
        Assert.That(run.Result.ToolCalls!.Single().PreparationError, Does.Contain("setup executable was not found"));
    }

    [Test]
    public async Task SuccessfulComposition_IsReusedBeforeNetworkApproval_AndNeverExecutesDeniedCalls()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.SeedSandboxComponentsAsync();
        var scans     = 0;
        var approvals = 0;
        var arguments = """{"mode":"direct","executable":"unused-command","arguments":[],"networkAccess":"enabled"}""";
        var factory = fixture.Factory(new ScriptModel("shell", arguments, 2), _ =>
        {
            scans++;
            return null;
        });
        DesktopSession? session = null;
        session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root), _ => ValueTask.CompletedTask,
                                          approval =>
                                          {
                                              if (approval is not null)
                                              {
                                                  approvals++;
                                                  Assert.That(scans, Is.EqualTo(1));
                                                  Assert.That(approval.ExecutionPolicy,
                                                              Does.Contain("windows-sandbox"));
                                                  session!.Approvals.Respond(approval.Id,
                                                                             ApprovalResolution
                                                                                .Denied("fixture denial"));
                                              }

                                              return ValueTask.CompletedTask;
                                          }, default);
        await using (session)
        {
            var run = await session.SubmitAsync("two sandbox requests").WaitAsync(Limit);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(run.Result.ToolCalls!.Count, Is.EqualTo(2));
            Assert.That(run.Result.ToolCalls.All(call => call.PreparationError is null), Is.True);
            Assert.That(approvals, Is.GreaterThanOrEqualTo(1));
            Assert.That(scans, Is.EqualTo(1));
            Assert.That(run.Result.ToolExecutions, Is.Zero);
        }
    }

    private const string ShellArguments = """{"mode":"direct","executable":"unused-command","arguments":[]}""";

    private sealed class ScriptModel(string? tool, string arguments, int toolCalls = 1) : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _requests) == 1 && tool is not null)
            {
                for (var index = 0; index < toolCalls; index++)
                    yield return new ChatStreamEvent
                    {
                        Kind                 = ChatStreamEventKind.ToolCallDelta, ToolCallIndex = index,
                        ToolCallId           = $"lazy-call-{index}",
                        ToolCallFunctionName = tool, ToolCallArgumentsDelta = arguments
                    };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
            }
            else
            {
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _parent =
            Path.Combine(Path.GetTempPath(), "tinyharness-lazy-sandbox-" + Guid.NewGuid().ToString("N"));

        public string Root        => Path.Combine(_parent, "workspace");
        public string Store       => Path.Combine(_parent, "sessions");
        public string Config      => Path.Combine(_parent, "user.json");
        public string SandboxHome => Path.Combine(_parent, "sandbox-home");

        public void Dispose() => Directory.Delete(_parent, true);

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "ordinary.txt"), "fixture content");
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
                Settings = new UserConfigSettings
                {
                    SessionStoreDirectory = fixture.Store,
                    WindowsSandbox = new WindowsSandboxSettings
                    {
                        Enabled              = true, SandboxHome = fixture.SandboxHome,
                        SetupExecutablePath  = Path.Combine(fixture._parent, "missing-setup.exe"),
                        RunnerExecutablePath = Path.Combine(fixture._parent, "missing-runner.exe")
                    }
                }
            }, default);
            return fixture;
        }

        public DesktopSessionFactory Factory(IChatCompletionClient                                   model,
                                             Func<CancellationToken, SandboxBaselineReadProtection?> scan) =>
            new((_, _) => model, new Credentials(), Config) { BaselineReadProtectionOverride = scan };

        public async Task SeedSandboxComponentsAsync()
        {
            Directory.CreateDirectory(SandboxHome);
            await File.WriteAllBytesAsync(Path.Combine(_parent, "missing-setup.exe"), []);
            await File.WriteAllBytesAsync(Path.Combine(_parent, "missing-runner.exe"), []);
        }

        public Task<DesktopSession> OpenAsync(DesktopSessionFactory factory) => factory.OpenAsync(
         new DesktopSessionRequest(Root) { ProjectAssignmentState = SessionProjectAssignmentState.NoProject },
         _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, default);
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                              => true;
        public string? Read(string   targetName)                => "offline-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }
}
