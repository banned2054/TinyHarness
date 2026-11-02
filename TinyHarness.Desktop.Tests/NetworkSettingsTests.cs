using System.Runtime.CompilerServices;
using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class NetworkSettingsTests
{
    [TearDown]
    public void DeleteTemporaryDirectories()
    {
        foreach (var directory in _temporaryDirectories)
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        _temporaryDirectories.Clear();
    }

    private readonly List<string> _temporaryDirectories = [];

    [Test]
    public async Task NewProviderFollowsGlobalNetworkAndReflectsChangesToTheDefault()
    {
        var path = NewConfigPath();
        var proxy = new ProxyDefinition
        {
            Id = "local", Name = "本地 Mihomo", Host = "127.0.0.1", Port = 7890
        };
        var baseline = new UserConfig
        {
            Settings = new UserConfigSettings
            {
                Network = new NetworkSettings
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxy.Id },
                    Proxies      = [proxy]
                }
            }
        };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var credentials = new MemoryCredentials();
        var editor      = CatalogProviderEditorViewModel.ForCreate(path, credentials, baseline, _ => { }, _ => { });
        editor.Name     = "work";
        editor.Endpoint = "https://api.example.invalid/v1";

        Assert.That(editor.SelectedProviderNetwork?.Value, Is.EqualTo("follow"));
        Assert.That(editor.ProviderNetworkDescription, Is.EqualTo("跟随软件默认（当前：代理 本地 Mihomo）"));
        var direct = baseline with
        {
            Settings = baseline.Settings! with
            {
                Network = baseline.Settings.Network! with
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Direct }
                }
            }
        };
        await UserConfigStore.SaveAsync(path, direct, baseline, CancellationToken.None);
        editor.AcceptBaseline(direct);
        Assert.That(editor.ProviderNetworkDescription, Is.EqualTo("跟随软件默认（当前：直连）"));

        await editor.SaveAsync();
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(loaded.Profiles.Single().Network, Is.Null);
    }

    [Test]
    public async Task EditingProviderNetworkPreservesModelsAndCredentialReference()
    {
        var path = NewConfigPath();
        var profile = new UserProfile
        {
            Name = "work", Endpoint = "https://api.example.invalid/v1", ChatApi = ChatApiKind.Responses,
            ApiKeyCredentialTarget = "existing-api-key", DefaultModel = "model-b",
            Models =
            [
                new UserProfileModel { Id = "model-a", ContextWindowTokens = 128_000 },
                new UserProfileModel { Id = "model-b", ContextWindowTokens = 256_000 }
            ]
        };
        var baseline = new UserConfig { DefaultProfile = profile.Name, Profiles = [profile] };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var editor = CatalogProviderEditorViewModel.ForEdit(path, new MemoryCredentials(), baseline, profile,
                                                            _ => { }, _ => { });
        editor.SelectedProviderNetwork = editor.ProviderNetworkOptions.Single(option => option.Value == "direct");

        await editor.SaveAsync();
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        var saved  = loaded.Profiles.Single();

        Assert.That(saved.Network?.Mode, Is.EqualTo(NetworkMode.Direct));
        Assert.That(saved.Models, Is.EqualTo(profile.Models));
        Assert.That(saved.DefaultModel, Is.EqualTo(profile.DefaultModel));
        Assert.That(saved.ApiKeyCredentialTarget, Is.EqualTo(profile.ApiKeyCredentialTarget));
        Assert.That(saved.ChatApi, Is.EqualTo(profile.ChatApi));
    }

    [Test]
    public async Task NetworkPageBlocksDeletingReferencedProxyAndReportsEachReference()
    {
        var path = NewConfigPath();
        var proxy = new ProxyDefinition
        {
            Id = "shared", Name = "Shared", Host = "127.0.0.1", Port = 7890
        };
        var profile = new UserProfile
        {
            Name    = "work", Endpoint = "https://api.example.invalid/v1",
            Network = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxy.Id }
        };
        var config = new UserConfig
        {
            Profiles = [profile],
            Settings = new UserConfigSettings
            {
                Network = new NetworkSettings
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = proxy.Id },
                    Proxies      = [proxy]
                }
            }
        };
        await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
        var viewModel = new NetworkSettingsViewModel(path, credentials : new MemoryCredentials());
        await viewModel.LoadAsync();
        var row = viewModel.Proxies.Single();

        Assert.That(row.IsReferenced, Is.True);
        Assert.That(row.ReferenceText, Does.Contain("全局默认网络").And.Contain("Provider work"));
        await row.DeleteCommand.ExecuteAsync();
        Assert.That(viewModel.Notice, Does.Contain("全局默认网络").And.Contain("Provider work"));

        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(loaded.Settings?.Network?.Proxies.Single().Id, Is.EqualTo(proxy.Id));

        row.EditCommand.Execute(null);
        viewModel.ProxyHost = "127.0.0.2";
        await viewModel.SaveProxyCommand.ExecuteAsync();
        Assert.That(viewModel.SaveProxyCommand.LastError, Is.Null);
        var edited = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(edited.Settings?.Network?.Proxies.Single().Host, Is.EqualTo("127.0.0.2"));
        Assert.That(edited.Settings?.Network?.DefaultRoute.ProxyId, Is.EqualTo(proxy.Id));
        Assert.That(edited.Profiles.Single().Network?.ProxyId, Is.EqualTo(proxy.Id));
    }

    [Test]
    public async Task NetworkPageSavesProxyAuthenticationOnlyToCredentialStore()
    {
        var path        = NewConfigPath();
        var credentials = new MemoryCredentials();
        var viewModel   = new NetworkSettingsViewModel(path, credentials : credentials);
        await viewModel.LoadAsync();
        viewModel.AddProxyCommand.Execute(null);
        viewModel.ProxyName     = "Local proxy";
        viewModel.ProxyHost     = "127.0.0.1";
        viewModel.ProxyPort     = "7890";
        viewModel.ProxyUsername = "proxy-user";
        viewModel.ProxyPassword = "proxy-password";

        await viewModel.SaveProxyCommand.ExecuteAsync();

        Assert.That(viewModel.SaveProxyCommand.LastError, Is.Null);
        var saved = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        var proxy = saved.Settings!.Network!.Proxies.Single();
        var json  = await File.ReadAllTextAsync(path);
        Assert.That(proxy.CredentialTarget, Is.Not.Null.And.Not.Empty);
        Assert.That(credentials.Read(proxy.CredentialTarget!), Is.Not.EqualTo("proxy-password"));
        Assert.That(ProxyCredentialCodec.Decode(credentials.Read(proxy.CredentialTarget!)!),
                    Is.EqualTo(("proxy-user", "proxy-password")));
        Assert.That(json, Does.Not.Contain("proxy-user").And.Not.Contain("proxy-password"));

        viewModel.Proxies.Single().EditCommand.Execute(null);
        viewModel.ProxyUsername = "updated-user";
        viewModel.ProxyPassword = "updated-password";
        await viewModel.SaveProxyCommand.ExecuteAsync();
        var updated =
            (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Settings!.Network!.Proxies.Single();
        Assert.That(credentials.DeleteCalls, Is.EqualTo([proxy.CredentialTarget]));
        Assert.That(updated.CredentialTarget, Is.Not.EqualTo(proxy.CredentialTarget));
        Assert.That(credentials.Read(updated.CredentialTarget!), Is.Not.Null);
        Assert.That(credentials.Read(proxy.CredentialTarget!), Is.Null);
        var updatedJson = await File.ReadAllTextAsync(path);
        Assert.That(updatedJson, Does.Not.Contain("updated-user").And.Not.Contain("updated-password"));
    }

    [Test]
    public async Task NetworkChangeDuringRunWaitsForTheNextRunBoundary()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "network-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _temporaryDirectories.Add(root);
        var workspace = Path.Combine(root, "workspace");
        var store     = Path.Combine(root, "sessions");
        var path      = Path.Combine(root, "user.json");
        Directory.CreateDirectory(workspace);
        var firstProxy = new ProxyDefinition
        {
            Id = "first", Name = "First", Host = "127.0.0.1", Port = 7890
        };
        var secondProxy = new ProxyDefinition
        {
            Id = "second", Name = "Second", Host = "127.0.0.1", Port = 7891
        };
        var profile = new UserProfile
        {
            Name         = "work", Endpoint = "https://api.example.invalid/v1", ApiKeyCredentialTarget = "api-key",
            DefaultModel = "model", Models  = [new UserProfileModel { Id = "model", ContextWindowTokens = 32_000 }]
        };
        var baseline = new UserConfig
        {
            DefaultProfile = profile.Name,
            Profiles       = [profile],
            Settings = new UserConfigSettings
            {
                SessionStoreDirectory = store,
                Network = new NetworkSettings
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = firstProxy.Id },
                    Proxies      = [firstProxy, secondProxy]
                }
            }
        };
        await UserConfigStore.SaveAsync(path, baseline, CancellationToken.None);
        var clients = new List<RunClient>();
        var factory = new DesktopSessionFactory((_, _) =>
        {
            var client = new RunClient(clients.Count == 0);
            clients.Add(client);
            return client;
        }, new MemoryCredentials("api-key", "fixture-api-key"), path);
        await using var session = await factory.OpenAsync(
                                                          new DesktopSessionRequest(workspace,
                                                              ProfileName : profile.Name, ModelId : "model")
                                                          {
                                                              ProjectAssignmentState =
                                                                  SessionProjectAssignmentState.NoProject
                                                          },
                                                          _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask,
                                                          CancellationToken.None);

        var firstRun = session.SubmitAsync("first run");
        await clients[0].Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var changed = baseline with
        {
            Settings = baseline.Settings! with
            {
                Network = baseline.Settings.Network! with
                {
                    DefaultRoute = new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = secondProxy.Id }
                }
            }
        };
        await UserConfigStore.SaveAsync(path, changed, baseline, CancellationToken.None);

        Assert.That(clients.Count, Is.EqualTo(1), "saving settings during a run must not rebuild its client");
        Assert.That(clients[0].DisposeCount, Is.Zero, "the in-flight client stays alive until its run settles");
        clients[0].Release();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(10));

        var secondRun = await session.SubmitAsync("second run").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(clients.Count, Is.EqualTo(2), "the next run adopts the new route at its pre-send boundary");
        Assert.That(clients[0].DisposeCount, Is.EqualTo(1));
        Assert.That(clients[1].Requests, Is.EqualTo(1));
        Assert.That(secondRun.Result?.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(session.Host.Session.ModelSelection!.NetworkRouteIdentity,
                    Is.EqualTo(NetworkRouteResolver.BindingIdentity(
                                                                    NetworkRouteResolver.Resolve(changed.Settings
                                                                      ?.Network, profile.Network))));
    }

    private string NewConfigPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tinyharness-network-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temporaryDirectories.Add(directory);
        return Path.Combine(directory, "user.json");
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);
        private readonly string?                    _secret;
        private readonly string?                    _target;

        public MemoryCredentials(string? target = null, string? secret = null)
        {
            _target = target;
            _secret = secret;
        }

        public List<string> DeleteCalls { get; } = [];

        public bool IsSupported                            => true;
        public void Save(string targetName, string secret) => _entries[targetName] = secret;

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName) ??
                                                  (targetName == _target ? _secret : null);

        public bool Delete(string targetName)
        {
            DeleteCalls.Add(targetName);
            return _entries.Remove(targetName);
        }
    }

    private sealed class RunClient(bool waitForRelease) : IChatCompletionClient, IDisposable
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        public int DisposeCount { get; private set; }

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            Entered.TrySetResult();
            if (waitForRelease) await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
        }

        public void Dispose() => DisposeCount++;

        public void Release() => _release.TrySetResult();
    }
}
