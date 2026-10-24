using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop;

/// <summary>
/// <para>验证原生产物的独立会话、并发、同一日志和恢复；所有模型与凭据均为离线夹具。</para>
/// <para>Verifies native independent sessions, concurrency, shared journaling and resume using only offline fixtures.</para>
/// </summary>
internal static class OfflineSmoke
{
    public static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var store = Path.Combine(root, "sessions");
        var configPath = Path.Combine(root, "user-config.json");
        Directory.CreateDirectory(workspace);
        var user = new UserConfig
        {
            DefaultProfile = "offline",
            Profiles = [new UserProfile
            {
                Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "offline-only",
                DefaultModel = "offline-model", Models = [new UserProfileModel { Id = "offline-model", ContextWindowTokens = 128_000 }]
            }],
            Settings = new UserConfigSettings { SessionStoreDirectory = store }
        };
        await UserConfigStore.SaveAsync(configPath, user, CancellationToken.None);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var createdModels = 0;
        var factory = new DesktopSessionFactory((_, _) => new OfflineModel(async token =>
        {
            if (Interlocked.Increment(ref entered) >= 2) bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(token);
        }, () => Interlocked.Increment(ref createdModels)), new OfflineCredentials(), configPath);
        try
        {
            string firstId;
            await using (var manager = new DesktopSessionManager())
            {
                Task<DesktopSession> Open() => manager.OpenAsync(null,
                    token => factory.OpenAsync(new DesktopSessionRequest(workspace), _ => ValueTask.CompletedTask,
                                                _ => ValueTask.CompletedTask, token));
                var first = await Open();
                var second = await Open();
                firstId = first.Id;
                var firstRun = first.SubmitAsync("offline first");
                var secondRun = second.SubmitAsync("offline second");
                var runs = await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(20));
                if (first.Id == second.Id || runs.Any(run => run.Result?.Status != Core.Models.Agent.AgentStatus.Completed))
                    throw new InvalidOperationException("Parallel desktop sessions did not complete independently.");
                if (first.Host.PersistenceWarning is not null || second.Host.PersistenceWarning is not null)
                    throw new InvalidOperationException("Desktop sessions were not reliably saved.");
            }
            await using (var manager = new DesktopSessionManager())
            {
                var restored = await manager.OpenAsync(firstId,
                    token => factory.OpenAsync(new DesktopSessionRequest(workspace, SessionId: firstId),
                                                _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, token));
                if (restored.Host.Session.Turns.Count != 1) throw new InvalidOperationException("Resume lost the first turn.");
                var run = await restored.SubmitAsync("offline resumed");
                if (run.Result?.Status != Core.Models.Agent.AgentStatus.Completed) throw new InvalidOperationException("Resumed turn failed.");
            }
            var saved = await SessionStoreReader.ReadAsync(store, firstId, CancellationToken.None);
            if (saved?.Turns.Count != 2 || createdModels != 3) throw new InvalidOperationException("Persistence or client ownership failed.");
            Console.WriteLine($"PASS desktop: parallel=2 resumedTurns=2 disposedClients={createdModels}; fixture={root}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL desktop: {exception.Message}; fixture={root}");
            return 1;
        }
    }

    private sealed class OfflineCredentials : ICredentialStore
    {
        public bool IsSupported => true;
        public string? Read(string targetName) => targetName == "offline-only" ? "offline-fixture-key" : null;
        public void Save(string targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class OfflineModel(Func<CancellationToken, Task> started, Action disposed) : IChatCompletionClient, IDisposable
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await started(cancellationToken);
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline desktop response" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
        public void Dispose() => disposed();
    }
}
