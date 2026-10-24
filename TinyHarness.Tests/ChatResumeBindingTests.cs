using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证恢复绑定按日志最后保存的模型选择准备请求配置：仅改强度或换模型后关闭，也能用一致的身份、
///         参数与客户端重新打开并续聊；目标已删除或匹配歧义时按恢复失败拒绝，不悄悄改用全局默认。
///     </para>
///     <para>
///         Verifies that the resume binding prepares the request configuration from the last saved model
///         selection: closing after an effort-only change or a model switch still reopens with consistent
///         identity, options, and client, while deleted or ambiguous targets reject the resume instead of
///         silently adopting the startup defaults.
///     </para>
/// </summary>
public class ChatResumeBindingTests
{
    private const string SystemPrompt = "You are a test harness inspecting a workspace.";

    private static readonly SessionModelIdentity Identity =
        new("big", "https://one.example/v1", ChatApiKind.ChatCompletions);

    private static readonly string WorkspaceRoot =
        Path.Combine(Path.GetTempPath(), "tinyharness-resume-binding-tests");

    private static AgentOptions StartupOptions() => new()
    {
        Model                     = "big",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };

    private static UserConfig Catalog(bool   withSharedTripleProfile = false, bool dropFlashFromWork = false,
                                      string sharedTripleModelId     = "flash")
    {
        var work = new UserProfile
        {
            Name = "work", Endpoint = "https://one.example/v1", ChatApi = ChatApiKind.ChatCompletions,
            ApiKeyEnvironmentVariable = "WORK_KEY",
            Models = dropFlashFromWork
                ?
                [
                    new UserProfileModel { Id = "big", ContextWindowTokens = 200_000 }
                ]
                :
                [
                    new UserProfileModel { Id = "big", ContextWindowTokens   = 200_000 },
                    new UserProfileModel { Id = "flash", ContextWindowTokens = 128_000 }
                ]
        };

        var profiles = new List<UserProfile> { work };
        if (withSharedTripleProfile)
            profiles.Add(new UserProfile
            {
                Name = "alt", Endpoint = "https://one.example/v1", ChatApi = ChatApiKind.ChatCompletions,
                ApiKeyCredentialTarget = "alt-account",
                Models = [new UserProfileModel { Id = sharedTripleModelId, ContextWindowTokens = 128_000 }]
            });

        return new UserConfig { Profiles = profiles };
    }

    private static PersistedSession Snapshot(params SessionModelSelection[] selections) => new()
    {
        Id              = new string('a', 32),
        WorkspaceRoot   = WorkspaceRoot,
        Model           = Identity,
        ModelSelections = selections
    };

    private static Task<UserConfig> LoadCatalog(UserConfig catalog) => Task.FromResult(catalog);

    [Test]
    public async Task Prepare_EffortOnlySelectionDerivesOptionsWithoutCatalogOrClient()
    {
        var saved = new SessionModelSelection
        {
            Revision        = 2, Model = Identity, CredentialRoute = "work|env:WORK_KEY",
            Context         = StartupOptions().Context, ReasoningEffort = ModelReasoningEffort.High,
            MessageBoundary = 3
        };
        var          configuredClient = new FakeChatClient();
        var          catalogLoaded    = false;
        List<string> created          = [];

        var binding = await ChatResumeBinding.PrepareAsync(Snapshot(saved), Identity, StartupOptions(),
                                                           configuredClient, "work|env:WORK_KEY",
                                                           _ =>
                                                           {
                                                               catalogLoaded = true;
                                                               return LoadCatalog(Catalog());
                                                           },
                                                           (profile, modelId, _) =>
                                                           {
                                                               created.Add($"{profile.Name}/{modelId}");
                                                               return Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient());
                                                           },
                                                           CancellationToken.None);

        Assert.That(binding.Identity, Is.EqualTo(Identity));
        Assert.That(binding.Client, Is.SameAs(configuredClient));
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(binding.Options.Model, Is.EqualTo("big"));
        Assert.That(binding.Options.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(binding.SavedSelection, Is.SameAs(saved));
        Assert.That(catalogLoaded, Is.False, "same-identity selections must not load the catalog");
        Assert.That(created, Is.Empty, "same-identity selections must reuse the startup client");
    }

    [Test]
    public async Task Prepare_SwitchedSelectionResolvesClientFromCatalogAndKeepsSavedParameters()
    {
        var switched = new SessionModelIdentity("flash", "https://one.example/v1", ChatApiKind.ChatCompletions);
        var saved = new SessionModelSelection
        {
            Revision = 2, Model = switched, CredentialRoute = "work|env:WORK_KEY",
            Context  = new ContextOptions { ContextWindowTokens = 128_000 }, MessageBoundary = 3
        };
        var created = new List<string>();

        var binding = await ChatResumeBinding.PrepareAsync(Snapshot(saved), Identity, StartupOptions(),
                                                           new FakeChatClient(), "work|env:WORK_KEY",
                                                           _ => LoadCatalog(Catalog()),
                                                           (profile, modelId, _) =>
                                                           {
                                                               created.Add($"{profile.Name}/{modelId}");
                                                               return Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient());
                                                           },
                                                           CancellationToken.None);

        Assert.That(binding.Identity, Is.EqualTo(switched));
        Assert.That(created, Is.EqualTo(["work/flash"]));
        Assert.That(binding.Client, Is.Not.Null);
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(binding.Options.Model, Is.EqualTo("flash"));
        Assert.That(binding.Options.Context!.ContextWindowTokens, Is.EqualTo(128_000));
        Assert.That(binding.Options.ReasoningEffort, Is.Null);
    }

    [Test]
    public async Task Prepare_DuplicateTriplesPreferTheRecordedCredentialRoute()
    {
        // 同一模型三元组由两个 profile 提供：路由记录指向 alt 时必须选中 alt，而不是靠顺序碰运气。
        var saved = new SessionModelSelection
        {
            Revision = 2, Model = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            CredentialRoute = "alt|credential:alt-account", MessageBoundary = 3
        };
        var created = new List<string>();

        var binding = await ChatResumeBinding.PrepareAsync(Snapshot(saved), Identity, StartupOptions(),
                                                           new FakeChatClient(), "work|env:WORK_KEY",
                                                           _ => LoadCatalog(Catalog(withSharedTripleProfile : true)),
                                                           (profile, modelId, _) =>
                                                           {
                                                               created.Add($"{profile.Name}/{modelId}");
                                                               return Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient());
                                                           },
                                                           CancellationToken.None);

        Assert.That(created, Is.EqualTo(["alt/flash"]));
        Assert.That(binding.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
    }

    [Test]
    public async Task Prepare_SameTripleDifferentRecordedRouteMustNotReuseStartupClient()
    {
        // 会话在相同三元组下切到另一 profile／凭据后保存：恢复不得因模型一致短路回启动账户。
        var saved = new SessionModelSelection
        {
            Revision = 2, Model = Identity, CredentialRoute = "alt|credential:alt-account",
            Context  = StartupOptions().Context, MessageBoundary = 3
        };
        var          startupClient = new FakeChatClient();
        var          catalogLoaded = false;
        List<string> created       = [];

        var binding = await ChatResumeBinding.PrepareAsync(Snapshot(saved), Identity, StartupOptions(),
                                                           startupClient, "work|env:WORK_KEY",
                                                           _ =>
                                                           {
                                                               catalogLoaded = true;
                                                               return LoadCatalog(Catalog(
                                                                withSharedTripleProfile : true,
                                                                sharedTripleModelId : "big"));
                                                           },
                                                           (profile, modelId, _) =>
                                                           {
                                                               created.Add($"{profile.Name}/{modelId}");
                                                               return Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient());
                                                           },
                                                           CancellationToken.None);

        Assert.That(binding.Identity, Is.EqualTo(Identity));
        Assert.That(catalogLoaded, Is.True, "a differing recorded route must be resolved against the catalog");
        Assert.That(created, Is.EqualTo(["alt/big"]));
        Assert.That(binding.Client, Is.Not.SameAs(startupClient));
        Assert.That(binding.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
        Assert.That(binding.Options.Model, Is.EqualTo("big"));
    }

    [TestCase("route-deleted", "no longer configured")]
    [TestCase("model-deleted", "no longer present")]
    [TestCase("route-null-ambiguous", "multiple providers")]
    public async Task Prepare_UnresolvableSelectionRejectsResumeWithoutDefaultFallback(
        string reason, string expectedFragment)
    {
        // route-deleted：保存 alt/B 后删除 alt profile，只剩同三元组 work——唯一候选不得替代已记录账户。
        SessionModelSelection saved;
        UserConfig            catalog;
        switch (reason)
        {
            case "route-deleted" :
                saved = new SessionModelSelection
                {
                    Revision        = 2, Model = Identity, CredentialRoute = "alt|credential:alt-account",
                    MessageBoundary = 3
                };
                catalog = Catalog();
                break;
            case "model-deleted" :
                saved = new SessionModelSelection
                {
                    Revision = 2, Model = new SessionModelIdentity("flash", "https://one.example/v1",
                                                                   ChatApiKind.ChatCompletions),
                    CredentialRoute = "former|env:FORMER_KEY", MessageBoundary = 3
                };
                catalog = Catalog(dropFlashFromWork : true);
                break;
            default :
                saved = new SessionModelSelection
                {
                    Revision = 2, Model = new SessionModelIdentity("flash", "https://one.example/v1",
                                                                   ChatApiKind.ChatCompletions),
                    MessageBoundary = 3
                };
                catalog = Catalog(withSharedTripleProfile : true);
                break;
        }

        var created = new List<string>();

        var ex = await TestAssert.ThrowsAsync<SessionStoreException>(() =>
                                                                         ChatResumeBinding.PrepareAsync(Snapshot(saved),
                                                                             Identity, StartupOptions(),
                                                                             new FakeChatClient(),
                                                                             "work|env:WORK_KEY",
                                                                             _ => LoadCatalog(catalog),
                                                                             (profile, modelId, _) =>
                                                                             {
                                                                                 created
                                                                                    .Add($"{profile.Name}/{modelId}");
                                                                                 return Task
                                                                                    .FromResult<
                                                                                         IChatCompletionClient>(new
                                                                                         FakeChatClient());
                                                                             },
                                                                             CancellationToken.None));

        Assert.That(ex.Message, Does.Contain(expectedFragment));
        Assert.That(created, Is.Empty, "a rejected resume must not construct any candidate client");
    }

    [Test]
    public async Task Prepare_LegacySelectionWithoutRouteFallsBackToUniqueIdentityMatch()
    {
        var saved = new SessionModelSelection
        {
            Revision = 2, Model = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            MessageBoundary = 3
        };
        var created = new List<string>();

        var binding = await ChatResumeBinding.PrepareAsync(Snapshot(saved), Identity, StartupOptions(),
                                                           new FakeChatClient(), "work|env:WORK_KEY",
                                                           _ => LoadCatalog(Catalog()),
                                                           (profile, modelId, _) =>
                                                           {
                                                               created.Add($"{profile.Name}/{modelId}");
                                                               return Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient());
                                                           },
                                                           CancellationToken.None);

        Assert.That(created, Is.EqualTo(["work/flash"]));
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
    }

    [Test]
    public async Task Reopen_AfterEffortOnlyChangeUsesSavedSelectionForTheNextRequest()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.Text("first answer"));
        string sessionId;
        using (var host = new ChatSessionHost(WorkspaceRoot, Identity, StartupOptions(), first,
                                              new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("first question", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
            await host.ChangeReasoningEffortAsync(ModelReasoningEffort.High, CancellationToken.None);
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            // 启动形状的 Options 与保存的高强度选择不一致：直接接管被拒，正是恢复绑定要弥合的差距。
            var stale = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, Identity, SystemPrompt,
                                              StartupOptions().Context);
            TestAssert.Throws<ArgumentException>(() =>
                                                     new ChatSessionHost(stale.Session, stale.Context, StartupOptions(),
                                                                         new FakeChatClient(),
                                                                         new ToolRegistry([]), SystemPrompt,
                                                                         sessionWriter : writer));

            var binding = await ChatResumeBinding
                               .PrepareAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), new FakeChatClient(),
                                             "work|env:WORK_KEY", _ => LoadCatalog(Catalog()),
                                             (_, _, _) => Task.FromResult<IChatCompletionClient>(new FakeChatClient()),
                                             CancellationToken.None)
                               .ConfigureAwait(false);
            var restored = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, binding.Identity, SystemPrompt,
                                                 binding.Options.Context);
            var second = new FakeChatClient();
            second.Enqueue(FakeChatClient.Text("second answer"));
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, binding.Options, second,
                                                    new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
            var before  = resumed.Session.ModelSelection!;
            var aligned = await resumed.EnsureCredentialRouteAsync(binding.CredentialRoute, CancellationToken.None);
            var run2    = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

            Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed), run2.Result.Error);
            Assert.That(aligned.Revision, Is.EqualTo(before.Revision));
            Assert.That(resumed.Session.ModelSelection!.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
            Assert.That(second.RequestLog.Single().ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public async Task Reopen_AfterModelSwitchResolvesTargetClientAndContinues()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.Text("first answer"));
        var switchedIdentity = new SessionModelIdentity("flash", "https://one.example/v1",
                                                        ChatApiKind.ChatCompletions);
        string sessionId;
        using (var host = new ChatSessionHost(WorkspaceRoot, Identity, StartupOptions(), first,
                                              new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("first question", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
            var switchedClient = new FakeChatClient();
            await host.ChangeModelAsync(switchedIdentity, switchedClient,
                                        new ContextOptions { ContextWindowTokens = 128_000 },
                                        ModelReasoningEffort.Low, CancellationToken.None,
                                        credentialRoute : "work|env:WORK_KEY");
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            var created = new List<string>();
            var binding = await ChatResumeBinding
                               .PrepareAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), new FakeChatClient(),
                                             "work|env:WORK_KEY", _ => LoadCatalog(Catalog()),
                                             (profile, modelId, _) =>
                                             {
                                                 created.Add($"{profile.Name}/{modelId}");
                                                 return Task.FromResult<IChatCompletionClient>(new FakeChatClient());
                                             },
                                             CancellationToken.None)
                               .ConfigureAwait(false);
            var restored = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, binding.Identity, SystemPrompt,
                                                 binding.Options.Context);
            var second = new FakeChatClient();
            second.Enqueue(FakeChatClient.Text("second answer"));
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, binding.Options, second,
                                                    new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
            var before  = resumed.Session.ModelSelection!;
            var aligned = await resumed.EnsureCredentialRouteAsync(binding.CredentialRoute, CancellationToken.None);
            var run2    = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

            Assert.That(created, Is.EqualTo(["work/flash"]));
            Assert.That(binding.Options.Context!.ContextWindowTokens, Is.EqualTo(128_000));
            Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed), run2.Result.Error);
            Assert.That(aligned.Revision, Is.EqualTo(before.Revision));
            Assert.That(second.RequestLog.Single().ReasoningEffort, Is.EqualTo(ModelReasoningEffort.Low));
            Assert.That(second.RequestLog.Single().Messages.Where(message => message.Role == ChatRole.User)
                              .Select(message => message.Content),
                        Is.EqualTo(["first question", "second question"]));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public async Task Reopen_SameTripleDifferentRouteKeepsRecordedAccountWithoutNewRevision()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.Text("first answer"));
        string sessionId;
        using (var host = new ChatSessionHost(WorkspaceRoot, Identity, StartupOptions(), first,
                                              new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("first question", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);

            // 同三元组切到 alt 账户：按切换契约开启新 epoch 并保存新路由。
            await host.ChangeModelAsync(Identity, new FakeChatClient(), StartupOptions().Context, null,
                                        CancellationToken.None,
                                        credentialRoute : "alt|credential:alt-account");
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            var startupClient = new FakeChatClient();
            var created       = new List<string>();
            var binding = await ChatResumeBinding
                               .PrepareAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), startupClient,
                                             "work|env:WORK_KEY",
                                             _ => LoadCatalog(Catalog(withSharedTripleProfile : true,
                                                                      sharedTripleModelId : "big")),
                                             (profile, modelId, _) =>
                                             {
                                                 created.Add($"{profile.Name}/{modelId}");
                                                 return Task.FromResult<IChatCompletionClient>(new FakeChatClient());
                                             },
                                             CancellationToken.None)
                               .ConfigureAwait(false);
            var restored = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, binding.Identity, SystemPrompt,
                                                 binding.Options.Context);
            var second = new FakeChatClient();
            second.Enqueue(FakeChatClient.Text("second answer"));
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, binding.Options, second,
                                                    new ToolRegistry([]), SystemPrompt, sessionWriter : writer);
            var before  = resumed.Session.ModelSelection!;
            var aligned = await resumed.EnsureCredentialRouteAsync(binding.CredentialRoute, CancellationToken.None);
            var run2    = await resumed.SubmitTurnAsync("second question", CancellationToken.None);

            // 恢复沿用记录的 alt 账户：路由一致不新增修订、不再改写 epoch，启动账户未被使用。
            Assert.That(created, Is.EqualTo(["alt/big"]));
            Assert.That(startupClient.Requests, Is.Zero);
            Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed), run2.Result.Error);
            Assert.That(aligned.Revision, Is.EqualTo(before.Revision));
            Assert.That(aligned.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
            Assert.That(aligned.ContinuationEpoch, Is.EqualTo(before.ContinuationEpoch));
            Assert.That(second.RequestLog.Single().Messages.Where(message => message.Role == ChatRole.User)
                              .Select(message => message.Content),
                        Is.EqualTo(["first question", "second question"]));
        }
        finally
        {
            writer.Dispose();
        }
    }
}
