using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopSessionTitleTests
{
    [Test]
    public async Task FailedNamingKeepsFirstInputAndRetriesAfterRestartThenStopsOnceNamed()
    {
        var fixture  = await Fixture.CreateAsync();
        var attempts = 0;
        var requests = new ConcurrentQueue<ChatCompletionRequest>();
        var clients  = new ConcurrentQueue<Model>();
        var events   = new ConcurrentQueue<DesktopEvent>();
        var factory = fixture.Factory((_, _) =>
        {
            var client = new Model(async (request, token) =>
            {
                if (!IsTitle(request)) return "ordinary answer";
                requests.Enqueue(request);
                await Task.Yield();
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("offline title failure");
                return "生成会话名称";
            });
            clients.Enqueue(client);
            return client;
        });
        string id;
        await using (var first = await fixture.OpenAsync(factory, events))
        {
            id = first.Id;
            await first.SubmitAsync("第一条输入\n继续说明");
            await first.WaitForTitleGenerationAsync();
            Assert.That(first.Host.Session.Title.Pending, Is.True);
            Assert.That((await SessionSummaryBuilder.BuildAsync(fixture.Store, id, default))!.Summary.Title,
                        Is.EqualTo("第一条输入"));
            Assert.That(events.Any(item => item.Kind == "title"), Is.False);
        }

        var pending = (await SessionStoreReader.ReadAsync(fixture.Store, id, default))!;
        Assert.That(pending.Title.Pending, Is.True);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(fixture.Store, id + ".session.jsonl")),
                    Does.Contain("session-title"));

        await using (var resumed = await fixture.OpenAsync(factory, events, id))
        {
            Assert.That(attempts, Is.EqualTo(1), "Opening must not call a model.");
            await resumed.SubmitAsync("第二条输入：新任务");
            await resumed.WaitForTitleGenerationAsync();
            Assert.That(resumed.Host.Session.Title.Name, Is.EqualTo("生成会话名称"));
            Assert.That(resumed.Host.Session.Title.Pending, Is.False);
            Assert.That(requests.All(item => item.Messages[1].Content == "第一条输入\n继续说明"), Is.True);
            Assert.That(requests.Select(item => item.Model), Is.All.EqualTo("main"));
            await resumed.SubmitAsync("第三条输入");
            await resumed.WaitForTitleGenerationAsync();
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(events.Where(item => item.Kind == "title").Select(item => item.Text),
                        Is.EqualTo(new[] { "生成会话名称" }));
        }

        await using (var named = await fixture.OpenAsync(factory, events, id))
        {
            await named.SubmitAsync("重启后继续");
            await named.WaitForTitleGenerationAsync();
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(named.Host.Session.Title.Name, Is.EqualTo("生成会话名称"));
        }

        Assert.That(clients.All(item => item.DisposeCount == 1), Is.True);
    }

    [Test]
    public async Task FixedTitleModelDoesNotUseTheFixedReviewerModel()
    {
        var fixture = await Fixture.CreateAsync(new SessionTitleSettings
        {
            UseCurrentModel = false, Profile = "offline", Model = "title"
        });
        var requests = new ConcurrentQueue<ChatCompletionRequest>();
        var factory = fixture.Factory((_, _) => new Model((request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult(IsTitle(request) ? "命名结果" : "ordinary answer");
        }));
        await using var session = await fixture.OpenAsync(factory, new ConcurrentQueue<DesktopEvent>());
        await session.SubmitAsync("question");
        await session.WaitForTitleGenerationAsync();
        Assert.That(requests.Where(IsTitle).Single().Model, Is.EqualTo("title"));
        Assert.That(requests.Where(item => !IsTitle(item)).Single().Model, Is.EqualTo("main"));
        Assert.That(requests.Any(item => item.Model == "reviewer"), Is.False);
        Assert.That(session.Host.Session.ModelSelection!.Model.ModelId, Is.EqualTo("main"));
        var saved = await UserConfigStore.LoadAsync(fixture.Path, default);
        Assert.That(saved.Settings!.Permissions!.ReviewerModel, Is.EqualTo("reviewer"));
    }

    [Test]
    public async Task PendingRequestDoesNotBlockTurnsOrDuplicateAndShutdownCancelsIt()
    {
        var    fixture     = await Fixture.CreateAsync();
        var    started     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var    attempts    = 0;
        Model? titleClient = null;
        var factory = fixture.Factory((_, _) =>
        {
            Model? client = null;
            client = new Model(async (request, token) =>
            {
                if (!IsTitle(request)) return "ordinary answer";
                titleClient = client;
                Interlocked.Increment(ref attempts);
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return "unreachable";
            });
            return client;
        });
        var session = await fixture.OpenAsync(factory, new ConcurrentQueue<DesktopEvent>());
        try
        {
            await session.SubmitAsync("first").WaitAsync(TimeSpan.FromSeconds(10));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.SubmitAsync("second").WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(session.Host.Session.Turns, Has.Count.EqualTo(2));
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.That(titleClient!.DisposeCount, Is.EqualTo(1));
        Assert.That((await SessionStoreReader.ReadAsync(fixture.Store, session.Id, default))!.Title.Pending, Is.True);
    }

    private static bool IsTitle(ChatCompletionRequest request) =>
        request.Messages[0].Content == SessionTitleGenerator.Instructions;

    private sealed record Fixture(string Root, string Store, string Path)
    {
        public DesktopSessionFactory Factory(Func<TinyHarnessConfig, string, IChatCompletionClient> create) =>
            new(create, new Credentials(), Path, create);

        public static async Task<Fixture> CreateAsync(SessionTitleSettings? settings = null)
        {
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                                   "tinyharness-title-runtime-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(System.IO.Path.Combine(directory, "workspace"),
                                      System.IO.Path.Combine(directory, "store"),
                                      System.IO.Path.Combine(directory, "config.json"));
            Directory.CreateDirectory(fixture.Root);
            await UserConfigStore.SaveAsync(fixture.Path, new UserConfig
            {
                DefaultProfile = "offline",
                Profiles =
                [
                    new UserProfile
                    {
                        Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "main",
                        Models =
                        [
                            new UserProfileModel { Id = "main", ContextWindowTokens     = 64000 },
                            new UserProfileModel { Id = "title", ContextWindowTokens    = 64000 },
                            new UserProfileModel { Id = "reviewer", ContextWindowTokens = 64000 }
                        ]
                    }
                ],
                Settings = new UserConfigSettings
                {
                    SessionStoreDirectory = fixture.Store, SessionTitles = settings,
                    Permissions = new PermissionSettings
                    {
                        UseCurrentModel = false, ReviewerProfile = "offline", ReviewerModel = "reviewer"
                    }
                }
            }, default);
            return fixture;
        }

        public Task<DesktopSession> OpenAsync(DesktopSessionFactory factory, ConcurrentQueue<DesktopEvent> events,
                                              string?               id = null) => factory.OpenAsync(
         new DesktopSessionRequest(Root, SessionApprovalMode.HumanApproval, id, "offline", "main")
         {
             ProjectAssignmentState = SessionProjectAssignmentState.NoProject
         }, item =>
         {
             events.Enqueue(item);
             return ValueTask.CompletedTask;
         },
         _ => ValueTask.CompletedTask, default);
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                              => true;
        public string? Read(string   targetName)                => "fixture-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model(Func<ChatCompletionRequest, CancellationToken, Task<string>> respond)
        : IChatCompletionClient, IDisposable
    {
        public int DisposeCount { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            var text = await respond(request, cancellationToken);
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = text };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
        }

        public void Dispose() => DisposeCount++;
    }
}
