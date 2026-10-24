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
            Name                      = "work",
            Endpoint                  = "https://one.example/v1",
            ChatApi                   = ChatApiKind.ChatCompletions,
            ApiKeyEnvironmentVariable = "WORK_KEY",
            Models                    = dropFlashFromWork
                ?
                [
                    new UserProfileModel
                    {
                        Id                  = "big",
                        ContextWindowTokens = 200_000,
                        ReasoningEfforts    = [ModelReasoningEffort.High]
                    }
                ]
                :
                [
                    new UserProfileModel
                    {
                        Id                  = "big",
                        ContextWindowTokens = 200_000,
                        ReasoningEfforts    = [ModelReasoningEffort.High]
                    },
                    new UserProfileModel
                    {
                        Id                  = "flash",
                        ContextWindowTokens = 128_000,
                        ReasoningEfforts    = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
                    }
                ]
        };

        var profiles = new List<UserProfile> { work };
        if (withSharedTripleProfile)
            profiles.Add(new UserProfile
            {
                Name                   = "alt",
                Endpoint               = "https://one.example/v1",
                ChatApi                = ChatApiKind.ChatCompletions,
                ApiKeyCredentialTarget = "alt-account",
                Models                 = [new UserProfileModel
                {
                    Id                  = sharedTripleModelId,
                    ContextWindowTokens = 128_000,
                    ReasoningEfforts    = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
                }]
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
    public async Task Resolve_EffortOnlySelectionReusesStartupClientAfterCatalogVerification()
    {
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            Context         = StartupOptions().Context,
            ReasoningEffort = ModelReasoningEffort.High,
            MessageBoundary = 3
        };
        var          configuredClient = new FakeChatClient();
        var          catalogLoaded    = false;
        List<string> created          = [];

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
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

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
        Assert.That(binding.Identity, Is.EqualTo(Identity));
        Assert.That(binding.Client, Is.SameAs(configuredClient));
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(binding.Options.Model, Is.EqualTo("big"));
        Assert.That(binding.Options.ReasoningEffort, Is.EqualTo(ModelReasoningEffort.High));
        Assert.That(binding.SavedSelection, Is.SameAs(saved));
        // 复用启动客户端不跳过当前配置核对：目录必须加载验证能力与窗口；客户端仍不重建。
        Assert.That(catalogLoaded, Is.True, "the reuse path must verify current capabilities and window");
        Assert.That(created, Is.Empty, "same-identity selections must reuse the startup client");
        Assert.That(binding.RefreshedContext, Is.Null, "an unknown (null) saved context is never back-filled");
        Assert.That(binding.CurrentCapabilities, Does.Contain(ModelReasoningEffort.High));
    }

    [Test]
    public async Task Resolve_SwitchedSelectionResolvesClientFromCatalogAndKeepsSavedParameters()
    {
        var switched = new SessionModelIdentity("flash", "https://one.example/v1", ChatApiKind.ChatCompletions);
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = switched,
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            MessageBoundary = 3
        };
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
        Assert.That(binding.Identity, Is.EqualTo(switched));
        Assert.That(created, Is.EqualTo(["work/flash"]));
        Assert.That(binding.Client, Is.Not.Null);
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(binding.Options.Model, Is.EqualTo("flash"));
        Assert.That(binding.Options.Context!.ContextWindowTokens, Is.EqualTo(128_000));
        Assert.That(binding.Options.ReasoningEffort, Is.Null);
    }

    [Test]
    public async Task Resolve_DuplicateTriplesPreferTheRecordedCredentialRoute()
    {
        // 同一模型三元组由两个 profile 提供：路由记录指向 alt 时必须选中 alt，而不是靠顺序碰运气。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            CredentialRoute = "alt|credential:alt-account",
            MessageBoundary = 3
        };
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(Catalog(withSharedTripleProfile : true)),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
        Assert.That(created, Is.EqualTo(["alt/flash"]));
        Assert.That(binding.CredentialRoute, Is.EqualTo("alt|credential:alt-account"));
    }

    [Test]
    public async Task Resolve_SameTripleDifferentRecordedRouteMustNotReuseStartupClient()
    {
        // 会话在相同三元组下切到另一 profile／凭据后保存：恢复不得因模型一致短路回启动账户。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "alt|credential:alt-account",
            Context         = StartupOptions().Context,
            MessageBoundary = 3
        };
        var          startupClient = new FakeChatClient();
        var          catalogLoaded = false;
        List<string> created       = [];

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
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

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
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
    public async Task Resolve_UnresolvableSelectionRejectsResumeWithoutDefaultFallback(
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
                    Revision        = 2,
                    Model           = Identity,
                    CredentialRoute = "alt|credential:alt-account",
                    MessageBoundary = 3
                };
                catalog = Catalog();
                break;
            case "model-deleted" :
                saved = new SessionModelSelection
                {
                    Revision        = 2,
                    Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                                   ChatApiKind.ChatCompletions),
                    CredentialRoute = "former|env:FORMER_KEY",
                    MessageBoundary = 3
                };
                catalog = Catalog(dropFlashFromWork : true);
                break;
            default :
                saved = new SessionModelSelection
                {
                    Revision        = 2,
                    Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                                   ChatApiKind.ChatCompletions),
                    MessageBoundary = 3
                };
                catalog = Catalog(withSharedTripleProfile : true);
                break;
        }

        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(catalog),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        // 目标不可解析：返回需要重选（历史仍可恢复，另选目标后继续），不构造任何候选客户端、不解析失效目标凭据。
        Assert.That(outcome.NeedsReselection, Is.True);
        Assert.That(outcome.ReselectionReason, Does.Contain(expectedFragment));
        Assert.That(created, Is.Empty, "a re-selection outcome must not construct any candidate client");
    }

    [Test]
    public async Task Resolve_LegacySelectionWithoutRouteFallsBackToUniqueIdentityMatch()
    {
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            MessageBoundary = 3
        };
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        Assert.That(created, Is.EqualTo(["work/flash"]));
        Assert.That(outcome.Target!.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
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

            var outcome = await ChatResumeBinding
                                .ResolveAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), new FakeChatClient(),
                                              "work|env:WORK_KEY", _ => LoadCatalog(Catalog()),
                                              (_, _, _) => Task.FromResult<IChatCompletionClient>(new FakeChatClient()),
                                              CancellationToken.None)
                                .ConfigureAwait(false);
            Assert.That(outcome.NeedsReselection, Is.False);
            var binding = outcome.Target!;
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
            await host.ChangeModelAsync(
                new SessionModelBinding(switchedIdentity, switchedClient,
                                        new ContextOptions { ContextWindowTokens = 128_000 },
                                        ModelReasoningEffort.Low, "work|env:WORK_KEY", "work"),
                CancellationToken.None);
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            var created = new List<string>();
            var outcome = await ChatResumeBinding
                                .ResolveAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), new FakeChatClient(),
                                              "work|env:WORK_KEY", _ => LoadCatalog(Catalog()),
                                              (profile, modelId, _) =>
                                              {
                                                  created.Add($"{profile.Name}/{modelId}");
                                                  return Task.FromResult<IChatCompletionClient>(new FakeChatClient());
                                              },
                                              CancellationToken.None)
                                .ConfigureAwait(false);
            Assert.That(outcome.NeedsReselection, Is.False);
            var binding = outcome.Target!;
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
            await host.ChangeModelAsync(
                new SessionModelBinding(Identity, new FakeChatClient(), StartupOptions().Context, null,
                                        "alt|credential:alt-account", "alt"),
                CancellationToken.None);
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            var startupClient = new FakeChatClient();
            var created       = new List<string>();
            var outcome = await ChatResumeBinding
                                .ResolveAsync(writer.LoadedSnapshot!, Identity, StartupOptions(), startupClient,
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
            Assert.That(outcome.NeedsReselection, Is.False);
            var binding = outcome.Target!;
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

    [Test]
    public async Task Resolve_MissingCredentialsForSavedTargetReturnsReselectInsteadOfFailing()
    {
        // profile 仍在、密钥被删除：目标不可用属于需要重选，不得把恢复整个判失败。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            MessageBoundary = 3
        };
        var factoryCalls = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                factoryCalls.Add($"{profile.Name}/{modelId}");
                                                                throw new ConfigException(
                                                                    $"No API key found for provider '{profile.Name}'.");
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.True);
        Assert.That(outcome.ReselectionReason, Does.Contain("credentials could not be resolved"));
        Assert.That(outcome.ReselectionReason, Does.Contain("select another model"));
        Assert.That(factoryCalls, Is.EqualTo(["work/flash"]));
    }

    [Test]
    public async Task Resolve_ReusePathRejectsSavedEffortNoLongerDeclared()
    {
        // 身份与路由都与启动一致：客户端可复用，但当前目录已把 big 改为仅声明 Low，
        // 保存的 High 不再受支持——不得沿用保存参数发送。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            ReasoningEffort = ModelReasoningEffort.High,
            MessageBoundary = 3
        };
        var lowOnly = new UserConfig
        {
            Profiles = [
                new UserProfile
                {
                    Name                      = "work",
                    Endpoint                  = "https://one.example/v1",
                    ChatApi                   = ChatApiKind.ChatCompletions,
                    ApiKeyEnvironmentVariable = "WORK_KEY",
                    DefaultModel              = "big",
                    Models                    = [
                        new UserProfileModel
                        {
                            Id                  = "big",
                            ContextWindowTokens = 200_000,
                            ReasoningEfforts    = [ModelReasoningEffort.Low]
                        }
                    ]
                }
            ]
        };
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            new FakeChatClient(), "work|env:WORK_KEY",
                                                            _ => LoadCatalog(lowOnly),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.True);
        Assert.That(outcome.ReselectionReason, Does.Contain("no longer declares"));
        Assert.That(created, Is.Empty, "an unsupported saved effort must re-select without building a client");
    }

    [Test]
    public async Task Resolve_ReusePathReturnsRefreshedContextWhenWindowChanged()
    {
        // 身份、路由与强度都仍有效，但目录窗口从 128k 改为 32k：
        // 客户端复用，保存参数用于接管校验，窗口漂移以刷新上下文返回，由调用方追加新选择快照。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            ReasoningEffort = ModelReasoningEffort.High,
            MessageBoundary = 3
        };
        var shrunk = new UserConfig
        {
            Profiles = [
                new UserProfile
                {
                    Name                      = "work",
                    Endpoint                  = "https://one.example/v1",
                    ChatApi                   = ChatApiKind.ChatCompletions,
                    ApiKeyEnvironmentVariable = "WORK_KEY",
                    DefaultModel              = "big",
                    Models                    = [
                        new UserProfileModel
                        {
                            Id                  = "big",
                            ContextWindowTokens = 32_000,
                            ReasoningEfforts    = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
                        }
                    ]
                }
            ]
        };
        var startupClient = new FakeChatClient();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            startupClient, "work|env:WORK_KEY",
                                                            _ => LoadCatalog(shrunk),
                                                            (_, _, _) => Task.FromResult<IChatCompletionClient>(
                                                                new FakeChatClient()),
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
        Assert.That(binding.Client, Is.SameAs(startupClient), "the startup client is reused");
        Assert.That(binding.Options.Context!.ContextWindowTokens, Is.EqualTo(128_000),
                    "take-over options keep the saved selection for the host consistency check");
        Assert.That(binding.RefreshedContext, Is.Not.Null);
        Assert.That(binding.RefreshedContext!.ContextWindowTokens, Is.EqualTo(32_000),
                    "the current window must be materialized as a new selection before use");
        Assert.That(binding.CurrentCapabilities, Does.Contain(ModelReasoningEffort.High));
    }

    [Test]
    public async Task Resolve_DegradedCompositionResolvesSavedTargetFromCatalogWithoutStartupClient()
    {
        // 启动默认凭据缺失（无启动客户端与路由）：目标按日志记录从可信目录解析并构造客户端。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = new SessionModelIdentity("flash", "https://one.example/v1",
                                                           ChatApiKind.ChatCompletions),
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            MessageBoundary = 3
        };
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            configuredClient      : null,
                                                            configuredCredentialRoute : null,
                                                            catalogLoader         : _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        var binding = outcome.Target!;
        Assert.That(created, Is.EqualTo(["work/flash"]));
        Assert.That(binding.Client, Is.Not.Null);
        Assert.That(binding.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
        Assert.That(binding.Options.Model, Is.EqualTo("flash"));
        Assert.That(binding.Options.ReasoningEffort, Is.EqualTo(saved.ReasoningEffort));
    }

    [Test]
    public async Task Resolve_DegradedCompositionReturnsReselectWhenSavedTargetCredentialsAreMissing()
    {
        // 失效的启动默认同时是日志保存目标（work/big）：目录解析命中但凭据不可用——需要重选，不拒绝恢复。
        var saved = new SessionModelSelection
        {
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            Context         = new ContextOptions { ContextWindowTokens = 128_000 },
            MessageBoundary = 3
        };
        var factoryCalls = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(saved), Identity, StartupOptions(),
                                                            configuredClient      : null,
                                                            configuredCredentialRoute : null,
                                                            catalogLoader         : _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                factoryCalls.Add($"{profile.Name}/{modelId}");
                                                                throw new
                                                                    ConfigException($"No API key found for provider '{profile.Name}'.");
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.True);
        Assert.That(outcome.ReselectionReason, Does.Contain("credentials could not be resolved"));
        Assert.That(factoryCalls, Is.EqualTo(["work/big"]));
    }

    [Test]
    public async Task Resolve_DegradedCompositionLegacyLogFallsBackToUniqueIdentityMatch()
    {
        // 无选择记录的旧日志在降级组合下同样可恢复：按初始身份三元组（Snapshot 的初始身份是 big）走唯一候选兜底。
        var created = new List<string>();

        var outcome = await ChatResumeBinding.ResolveAsync(Snapshot(), Identity, StartupOptions(),
                                                            configuredClient      : null,
                                                            configuredCredentialRoute : null,
                                                            catalogLoader         : _ => LoadCatalog(Catalog()),
                                                            (profile, modelId, _) =>
                                                            {
                                                                created.Add($"{profile.Name}/{modelId}");
                                                                return Task.FromResult<IChatCompletionClient>(
                                                                    new FakeChatClient());
                                                            },
                                                            CancellationToken.None);

        Assert.That(outcome.NeedsReselection, Is.False);
        Assert.That(created, Is.EqualTo(["work/big"]));
        Assert.That(outcome.Target!.CredentialRoute, Is.EqualTo("work|env:WORK_KEY"));
    }

    [Test]
    public async Task Reopen_DeletedTargetResumesUnboundThenSelectsNewTargetWithoutTouchingOldCredentials()
    {
        using var store = new TestTempDir();
        var       first = new FakeChatClient();
        first.Enqueue(FakeChatClient.ReasonedText("reason one", "item_1", "opaque-one", "first answer"));
        string sessionId;
        using (var host = new ChatSessionHost(WorkspaceRoot, Identity, StartupOptions(), first,
                                              new ToolRegistry([]), SystemPrompt,
                                              sessionWriterFactory : id => new SessionStoreWriter(store.Root, id),
                                              credentialRoute : "work|env:WORK_KEY"))
        {
            sessionId = host.Session.Id;
            var run = await host.SubmitTurnAsync("first question", CancellationToken.None);
            Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed), run.Result.Error);
        }

        var writer = await SessionStoreWriter.OpenExistingAsync(store.Root, sessionId, null,
                                                                CancellationToken.None);
        try
        {
            // A（big）连同其 profile 被彻底删除，目录只剩另一 profile 下的 B（flash）。
            var bOnly = new UserConfig
            {
                Profiles = [
                    new UserProfile
                    {
                        Name                      = "spare",
                        Endpoint                  = "https://spare.example/v1",
                        ChatApi                   = ChatApiKind.ChatCompletions,
                        ApiKeyEnvironmentVariable = "SPARE_KEY",
                        Models                    = [
                            new UserProfileModel
                            {
                                Id                  = "flash",
                                ContextWindowTokens = 128_000,
                                ReasoningEfforts    = [ModelReasoningEffort.Low, ModelReasoningEffort.High]
                            }
                        ]
                    }
                ]
            };
            var factoryCalls = new List<string>();

            // 全过程不解析 A 的凭据、不调用 A 的客户端工厂：目标解析失败在工厂之前返回。
            // 启动组合已落到仅存的 spare profile；保存选择仍指向被删除的 work/big。
            var startupSpare = new SessionModelIdentity("flash", "https://spare.example/v1",
                                                        ChatApiKind.ChatCompletions);
            var outcome = await ChatResumeBinding
                                .ResolveAsync(writer.LoadedSnapshot!, startupSpare, StartupOptions(),
                                              new FakeChatClient(), "spare|env:SPARE_KEY", _ => LoadCatalog(bOnly),
                                              (profile, modelId, _) =>
                                              {
                                                  factoryCalls.Add($"{profile.Name}/{modelId}");
                                                  return Task.FromResult<IChatCompletionClient>(new FakeChatClient());
                                              },
                                              CancellationToken.None)
                                .ConfigureAwait(false);
            Assert.That(outcome.NeedsReselection, Is.True);
            Assert.That(outcome.ReselectionReason, Does.Contain("no longer present"));
            Assert.That(factoryCalls, Is.Empty, "the deleted target's credentials and client factory must never be touched");

            // 历史事实仍可完整恢复；未绑定宿主接管历史与写入端，不伪造旧客户端，发送被拒绝。
            var restored = SessionResume.Restore(writer.LoadedSnapshot!, WorkspaceRoot, SystemPrompt);
            Assert.That(restored.Session.Turns, Has.Count.EqualTo(1));
            using var resumed = new ChatSessionHost(restored.Session, restored.Context, StartupOptions(),
                                                    modelClient : null, new ToolRegistry([]), SystemPrompt,
                                                    sessionWriter : writer);
            Assert.That(resumed.HasModelBinding, Is.False);
            await TestAssert.ThrowsAsync<InvalidOperationException>(
                () => resumed.SubmitTurnAsync("too early", CancellationToken.None));

            // 用户显式选择 B：只构造 B 客户端，按正常切换事务追加选择并可靠保存新 epoch。
            var target = new SessionModelIdentity("flash", "https://spare.example/v1", ChatApiKind.ChatCompletions);
            var before = resumed.Session.ModelSelection!;
            var second = new FakeChatClient();
            second.Enqueue(FakeChatClient.Text("second answer"));
            var selection = await resumed.ChangeModelAsync(
                                               new SessionModelBinding(target, second,
                                                   new ContextOptions { ContextWindowTokens = 128_000 }, null,
                                                   "spare|env:SPARE_KEY", "spare",
                                                   bOnly.Profiles[0].Models[0].ReasoningEfforts),
                                               CancellationToken.None,
                                               expectedRevision : before.Revision)
                                           .ConfigureAwait(false);
            Assert.That(selection.Revision, Is.EqualTo(before.Revision + 1));
            Assert.That(selection.PreviousRevision, Is.EqualTo(before.Revision));
            Assert.That(selection.ContinuationEpoch, Is.EqualTo(before.ContinuationEpoch + 1),
                        "switching to a different target opens a new epoch");

            var run2 = await resumed.SubmitTurnAsync("second question", CancellationToken.None);
            Assert.That(run2.Result!.Status, Is.EqualTo(AgentStatus.Completed), run2.Result.Error);
            Assert.That(factoryCalls, Is.Empty, "resuming and switching still never touch the deleted target");

            // 正文与完整历史保留；旧区间 opaque 推理被隔离；A 的历史身份未被 B 覆盖。
            var sent = second.RequestLog.Single();
            Assert.That(sent.Model, Is.EqualTo("flash"));
            Assert.That(sent.Messages.Where(message => message.Role == ChatRole.User)
                              .Select(message => message.Content),
                        Is.EqualTo(["first question", "second question"]));
            Assert.That(sent.Messages.Single(message => message.Role == ChatRole.Assistant).Reasoning, Is.Null);
            Assert.That(resumed.Session.InitialModel, Is.EqualTo(Identity));
        }
        finally
        {
            writer.Dispose();
        }
    }
}
