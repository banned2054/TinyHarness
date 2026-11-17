using System.Runtime.CompilerServices;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop;

/// <summary>
///     <para>验证原生产物的独立会话、并发、同一日志和恢复；所有模型与凭据均为离线夹具。</para>
///     <para>Verifies native independent sessions, concurrency, shared journaling and resume using only offline fixtures.</para>
/// </summary>
internal static class OfflineSmoke
{
    public static async Task<int> RunAsync()
    {
        var root       = Path.Combine(Path.GetTempPath(), "tinyharness-desktop-smoke-" + Guid.NewGuid().ToString("N"));
        var workspace  = Path.Combine(root, "workspace");
        var store      = Path.Combine(root, "sessions");
        var configPath = Path.Combine(root, "user-config.json");
        Directory.CreateDirectory(workspace);
        var user = new UserConfig
        {
            DefaultProfile = "offline",
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "offline-only",
                    DefaultModel = "offline-model", Models =
                    [
                        new UserProfileModel { Id = "offline-model", ContextWindowTokens = 128_000 },
                        new UserProfileModel
                        {
                            Id               = "flash", ContextWindowTokens = 128_000,
                            ReasoningEfforts = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
                        }
                    ]
                }
            ],
            Settings = new UserConfigSettings { SessionStoreDirectory = store, Permissions = new PermissionSettings() }
        };
        await UserConfigStore.SaveAsync(configPath, user, CancellationToken.None);
        var bothStarted         = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered             = 0;
        var createdModels       = 0;
        var titleEvents         = 0;
        var disposedTitleModels = 0;
        var titlesGenerated     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory =
            new DesktopSessionFactory((_, _) => new OfflineModel(async token =>
                                      {
                                          if (Interlocked.Increment(ref entered) >= 2) bothStarted.TrySetResult();
                                          await bothStarted.Task.WaitAsync(token);
                                      }, () => Interlocked.Increment(ref createdModels)),
                                      new OfflineCredentials(), configPath,
                                      (_, _) => new OfflineModel(_ => Task.CompletedTask,
                                                                 () => Interlocked.Increment(ref disposedTitleModels)));
        try
        {
            string firstId;
            await using (var manager = new DesktopSessionManager())
            {
                Task<DesktopSession> Open() =>
                    manager.OpenAsync(null, token => factory.OpenAsync(new DesktopSessionRequest(workspace),
                        item =>
                        {
                            if (item.Kind == "title" && Interlocked.Increment(ref titleEvents) == 2)
                                titlesGenerated.TrySetResult();
                            return ValueTask.CompletedTask;
                        }, _ => ValueTask.CompletedTask, token));

                var first    = await Open();
                var second   = await Open();
                var projects = await ProjectStore.LoadAsync(ProjectStore.FilePath(store), CancellationToken.None);
                if (projects.Projects.Count      != 1 || first.Host.Session.ProjectId != projects.Projects[0].Id ||
                    first.Host.Session.ProjectId != second.Host.Session.ProjectId)
                    throw new InvalidOperationException("Desktop sessions did not share the reliably saved project.");
                firstId = first.Id;
                foreach (var mode in SessionApprovalModes.All)
                    await first.ChangeApprovalModeAsync(mode);
                var firstRun  = first.SubmitAsync("offline first");
                var secondRun = second.SubmitAsync("offline second");
                var runs      = await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(20));
                await titlesGenerated.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if (first.Id == second.Id || runs.Any(run => run.Result?.Status      != AgentStatus.Completed ||
                                                             run.Result.FinalMessage != "offline desktop response"))
                    throw new InvalidOperationException("Parallel desktop sessions did not complete independently.");
                if (first.Host.PersistenceWarning is not null || second.Host.PersistenceWarning is not null)
                    throw new InvalidOperationException("Desktop sessions were not reliably saved.");
                await first.ChangeModelAsync("offline", "flash", ModelReasoningEffort.High,
                                             first.Host.Session.ModelSelection!.Revision);
                await first.SubmitAsync("offline switched");
                await first.ChangeModelAsync("offline", "offline-model", null,
                                             first.Host.Session.ModelSelection!.Revision);
                await first.SubmitAsync("offline switched back");
                await first.ChangeModelAsync("offline", "flash", ModelReasoningEffort.High,
                                             first.Host.Session.ModelSelection!.Revision);
            }

            await UserConfigStore.SaveAsync(configPath, user with
            {
                Profiles =
                [
                    user.Profiles[0] with
                    {
                        Endpoint = "https://refreshed.invalid/v1",
                        ApiKeyCredentialTarget = "offline-updated"
                    }
                ]
            }, CancellationToken.None);
            await using (var manager = new DesktopSessionManager())
            {
                var restored = await manager.OpenAsync(firstId,
                                                       token =>
                                                           factory.OpenAsync(new DesktopSessionRequest(workspace,
                                                                                 SessionId : firstId),
                                                                             _ => ValueTask.CompletedTask,
                                                                             _ => ValueTask.CompletedTask, token));
                if (restored.Host.Session.PermissionSelectionV2?.ApprovalMode != SessionApprovalMode.AIAutoReview)
                    throw new InvalidOperationException("The saved session permission mode did not survive resume.");
                if (restored.Host.Session.Turns.Count                    != 3                              ||
                    restored.Host.Session.ModelSelection?.Model.ModelId  != "flash"                        ||
                    restored.Host.Session.ModelSelection.ReasoningEffort != ModelReasoningEffort.High      ||
                    restored.Host.Session.ModelSelection.Model.Endpoint  != "https://refreshed.invalid/v1" ||
                    restored.Host.Session.ModelSelection.CredentialRoute != "offline|credential:offline-updated")
                    throw
                        new InvalidOperationException("Resume lost history or failed to refresh the existing target.");
                var run = await restored.SubmitAsync("offline resumed");
                if (run.Result?.Status != AgentStatus.Completed)
                    throw new InvalidOperationException("Resumed turn failed.");
            }

            var saved = await SessionStoreReader.ReadAsync(store, firstId, CancellationToken.None);
            if (saved?.Turns.Count != 4 || saved.ProjectId is null || createdModels != 6 ||
                saved.Title.Pending     || saved.Title.Name != "offline desktop response" || disposedTitleModels != 2)
                throw new InvalidOperationException("Persistence or client ownership failed.");
            if (saved.Conversation?.Messages.LastOrDefault(message => message.Role == "Assistant")?.TextItems is not
                [
                    { Phase: "commentary", Text  : "offline progress" },
                    { Phase: "final_answer", Text: "offline desktop response" }
                ])
                throw new
                    InvalidOperationException("Assistant message boundaries or phases did not survive native persistence.");
            var business = await RunBusinessStoreSmokeAsync(root, workspace);
            Console.WriteLine($"PASS desktop: parallel=2 switched=A/B/A/B refreshed=true resumedTurns=4 disposedClients={createdModels} titles=2 session-store={business}; fixture={root}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL desktop: {exception.Message}; fixture={root}");
            return 1;
        }
    }

    /// <summary>
    ///     <para>
    ///         在隔离目录内执行业务库建库、旧来源导入、读写、重开与备份恢复；用于证明 Desktop 原生产物在
    ///         NativeAOT 下真的执行新增业务 schema，而不只是编译未退化。失败抛出，资源在 finally 中清理。
    ///     </para>
    ///     <para>
    ///         Runs business-database creation, legacy import, read/write, reopen, and backup/restore in an isolated
    ///         directory, proving the native Desktop artifact executes the new business schema under NativeAOT rather
    ///         than merely compiling. Failures throw and resources are cleaned up in finally.
    ///     </para>
    /// </summary>
    private static async Task<string> RunBusinessStoreSmokeAsync(string root, string workspace)
    {
        var businessRoot = Path.Combine(root, "business");
        var store        = Path.Combine(businessRoot, "sessions");
        var backup       = Path.Combine(businessRoot, "backup");
        Directory.CreateDirectory(store);
        try
        {
            var project = await ProjectStore.GetOrCreateAsync(ProjectStore.FilePath(store), workspace, null,
                                                              CancellationToken.None);
            var archived = Guid.NewGuid().ToString("N");
            await SessionStoreArchiveStore.SetArchivedAsync(store, archived, true, CancellationToken.None);
            await SessionStorePinStore.SetProjectPinnedAsync(store, project.Id, true, CancellationToken.None);

            var migration = await SessionStoreMigration.MigrateAsync(new SessionStoreMigrationOptions(store),
                                                                     CancellationToken.None);
            if (migration.AlreadyCompleted || string.IsNullOrEmpty(migration.StoreIdentity))
                throw new InvalidOperationException("Business-store migration did not run or returned no identity.");
            var inspection = await SessionStoreMigration.InspectAsync(store, CancellationToken.None);
            if (inspection.Status != SessionStoreMigrationStatus.Completed)
                throw new InvalidOperationException($"Business-store status after migration was {inspection.Status}.");

            var organization = await SessionStoreOrganizationStore.LoadAsync(store, CancellationToken.None);
            if (!organization.ArchivedSessionIds.Contains(archived, StringComparer.Ordinal) ||
                !organization.PinnedProjectIds.Contains(project.Id, StringComparer.Ordinal))
                throw new InvalidOperationException("Business organization state did not import the legacy sources.");

            var pinnedAdditionally = Guid.NewGuid().ToString("N");
            await SessionStoreOrganizationStore.SetSessionPinnedAsync(store, pinnedAdditionally, true,
                                                                      CancellationToken.None);
            var reopened = await SessionStoreOrganizationStore.LoadAsync(store, CancellationToken.None);
            if (!reopened.PinnedSessionIds.Contains(pinnedAdditionally, StringComparer.Ordinal))
                throw new InvalidOperationException("Business organization write did not survive a reopen.");

            await SessionStoreBackup.CreateAsync(store, backup, CancellationToken.None);
            var verification = await SessionStoreBackup.VerifyAsync(backup, CancellationToken.None);
            if (verification.Status != SessionStoreBackupVerificationStatus.Valid)
                throw new InvalidOperationException($"Business backup verification was {verification.Status}.");
            File.Delete(SessionStoreDatabase.PathFor(store));
            await SessionStoreBackup.RestoreAsync(store, backup, CancellationToken.None);
            var restored = await SessionStoreOrganizationStore.LoadAsync(store, CancellationToken.None);
            if (!restored.PinnedSessionIds.Contains(pinnedAdditionally, StringComparer.Ordinal))
                throw new InvalidOperationException("Business backup restore lost the organization state.");
            return $"migrated+readwrite+restore identity={migration.StoreIdentity[..8]}";
        }
        finally
        {
            try
            {
                if (Directory.Exists(businessRoot)) Directory.Delete(businessRoot, true);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                // 尽力清理隔离 smoke 目录；失败不影响 smoke 结果。
            }
        }
    }

    private sealed class OfflineCredentials : ICredentialStore
    {
        public bool IsSupported => true;

        public string? Read(string targetName) =>
            targetName is "offline-only" or "offline-updated" ? "offline-fixture-key" : null;

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class OfflineModel(Func<CancellationToken, Task> started, Action disposed)
        : IChatCompletionClient, IDisposable
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            await started(cancellationToken);
            foreach (var item in new AssistantTextItem[]
                     {
                         new("offline progress", "msg-progress", "commentary"),
                         new("offline desktop response", "msg-answer", "final_answer")
                     })
            {
                yield return new ChatStreamEvent
                {
                    Kind          = ChatStreamEventKind.ContentDelta,
                    ContentDelta  = item.Text,
                    TextMessageId = item.MessageId,
                    TextPhase     = item.Phase
                };
                yield return new ChatStreamEvent
                {
                    Kind          = ChatStreamEventKind.TextMessageCompleted,
                    ContentDelta  = item.Text,
                    TextMessageId = item.MessageId,
                    TextPhase     = item.Phase
                };
            }

            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public void Dispose() => disposed();
    }
}
