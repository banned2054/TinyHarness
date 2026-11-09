using System.Security.Principal;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         沙箱网络策略闭环：生效网络（常设信任或执行计划标志）决定 spawn profile 与启动账户；
///         restricted 保持离线账户且不出现代理键；代理环境经组合器冻结并注入子进程，代理 URL 不进入
///         展示与身份串；在线凭据缺失 fail closed；读根覆盖 sandbox home 拒绝。
///         全部使用内存管道与 fake 凭据源，不启动真实进程或修改机器状态。
///     </para>
///     <para>
///         Sandbox network-policy closure: the effective network (standing trust or
///         the execution-plan flag) decides the spawn profile and the launch
///         account; restricted keeps the offline account with no proxy keys; the
///         proxy environment is frozen by the composer and injected into the child
///         while the proxy URL never reaches display strings or the identity;
///         missing online credentials fail closed; a read root covering the sandbox
///         home is rejected. Everything uses in-memory pipes and fake credential
///         sources, launching no real processes and touching no machine state.
///     </para>
/// </summary>
public class WindowsSandboxNetworkTests
{
    private const string LocalNoProxy = "localhost,127.0.0.1,::1";

    private static readonly WindowsSandboxBackendLimits TestLimits = new()
    {
        RefreshTimeout          = TimeSpan.FromSeconds(5),
        SpawnReadyTimeout       = TimeSpan.FromSeconds(3),
        CommunicationGrace      = TimeSpan.FromMilliseconds(300),
        TerminationDrainTimeout = TimeSpan.FromMilliseconds(500),
        RunnerExitTimeout       = TimeSpan.FromMilliseconds(500),
        TerminationKillGrace    = TimeSpan.FromSeconds(1),
        TerminateWriteTimeout   = TimeSpan.FromSeconds(1)
    };

    [Test]
    public async Task StandingEnabled_LaunchesOnlineAccountWithEnabledProfileAndBoundIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen   = SpawnSeen();
        var credentials = new RecordingCredentialSource();
        var (backend, _, desktops, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                                   SandboxNetworkPolicy.Enabled,
                                                                   credentials);
        using var homeScope = home;

        var result = await backend.ExecuteAsync(PlainPlan(home.Components.SandboxHome),
                                                new RecordingOutputCapture(), new RecordingOutputCapture(),
                                                CancellationToken.None);
        Assert.That(result.ExitCode, Is.EqualTo(0));

        // 生效网络在线：spawn profile 与 runner 账户都必须是在线形态，桌面 SID 与账户一致。
        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("enabled"));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessOnline"));
        // 生效在线启动必须加载账户 profile：登录会话需 schannel 客户端凭据。
        Assert.That(launcher.LastRequest.LoadUserProfile, Is.True);
        if (OperatingSystem.IsWindows())
            Assert.That(launcher.LastRequest.Account.AccountSid.Value,
                        Is.EqualTo(desktops.GrantedSids.Single()));
        Assert.That(credentials.Calls, Does.Contain("online"));
        Assert.That(credentials.Calls, Does.Not.Contain("offline"));

        // 组合身份串绑定启用网络与升级后的策略语义版本。
        using var workspace = new TestTempDir();
        var composed = WindowsSandboxComposer.Compose(TrustedSettings(home.Components) with
                                                      {
                                                          NetworkPolicy = SandboxNetworkPolicy.Enabled
                                                      },
                                                      workspace.Root);
        var identity = composed.ExecutionPolicy.PolicyIdentity;
        TestAssert.Contains("network=enabled", identity, StringComparison.Ordinal);
        // v8 引入授权读段（C2 获批读授权通道），旧 v7 身份随之失效（预期语义成本）。
        Assert.That(WindowsSandboxComposer.PolicyIdentityVersion, Is.EqualTo(8));
        TestAssert.Contains("policyVersion=8", identity, StringComparison.Ordinal);
        Assert.That(composed.ExecutionPolicy.DisplayName, Does.Contain("enabled"));
    }

    [Test]
    public async Task PlanNetworkFlag_ExecutesOnlineWhileIdentityStaysRestricted()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen   = SpawnSeen();
        var credentials = new RecordingCredentialSource();
        var (backend, _, _, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                            SandboxNetworkPolicy.Restricted,
                                                            credentials);
        using var homeScope = home;

        // 常设信任受限但单次执行计划显式要求网络：本次执行按在线处理，身份串仍保持 restricted。
        var plan = PlainPlan(home.Components.SandboxHome) with { NetworkAccessEnabled = true };
        await backend.ExecuteAsync(plan, new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("enabled"));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessOnline"));
        // 逐次授予在线同样以在线账户加载 profile 启动。
        Assert.That(launcher.LastRequest.LoadUserProfile, Is.True);

        using var workspace = new TestTempDir();
        var       composed  = WindowsSandboxComposer.Compose(TrustedSettings(home.Components), workspace.Root);
        TestAssert.Contains("network=restricted", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
        TestAssert.DoesNotContain("network=enabled", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
    }

    [Test]
    public async Task RestrictedDefault_KeepsOfflineAccountAndNeverInjectsProxyKeys()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawnSeen   = SpawnSeen();
        var credentials = new RecordingCredentialSource();
        var (backend, _, _, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                            SandboxNetworkPolicy.Restricted,
                                                            credentials);
        using var homeScope = home;

        var result = await backend.ExecuteAsync(PlainPlan(home.Components.SandboxHome),
                                                new RecordingOutputCapture(), new RecordingOutputCapture(),
                                                CancellationToken.None);
        Assert.That(result.ExitCode, Is.EqualTo(0));

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("restricted"));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessOffline"));
        // offline 保持不加载 profile，不扩大账户可写面。
        Assert.That(launcher.LastRequest.LoadUserProfile, Is.False);
        Assert.That(credentials.Calls, Does.Not.Contain("online"));
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" })
            Assert.That(request.Environment.ContainsKey(key), Is.False, key);

        // 默认组合（无代理配置）不得在身份串中携带代理环境哈希。
        using var workspace = new TestTempDir();
        var       composed  = WindowsSandboxComposer.Compose(TrustedSettings(home.Components), workspace.Root);
        TestAssert.Contains("network=restricted", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
        TestAssert.DoesNotContain("proxyEnvironment=", composed.ExecutionPolicy.PolicyIdentity,
                                  StringComparison.Ordinal);
    }

    [Test]
    public async Task EnabledNetwork_InjectsComposedProxyEnvironmentWithLocalNoProxy()
    {
        if (!OperatingSystem.IsWindows()) return;

        // 组合层解析的代理环境注入在线子进程；NO_PROXY 恒保护本机环回。
        var proxyEnvironment = new Dictionary<string, string>
        {
            ["HTTP_PROXY"]  = "http://127.0.0.1:7890",
            ["HTTPS_PROXY"] = "http://127.0.0.1:7890",
            ["NO_PROXY"]    = LocalNoProxy
        };
        var spawnSeen   = SpawnSeen();
        var credentials = new RecordingCredentialSource();
        var (backend, _, _, _, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                     SandboxNetworkPolicy.Enabled,
                                                     credentials,
                                                     proxyEnvironment);
        using var homeScope = home;

        await backend.ExecuteAsync(PlainPlan(home.Components.SandboxHome), new RecordingOutputCapture(),
                                   new RecordingOutputCapture(), CancellationToken.None);

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("enabled"));
        Assert.That(request.Environment["HTTP_PROXY"], Is.EqualTo("http://127.0.0.1:7890"));
        Assert.That(request.Environment["HTTPS_PROXY"], Is.EqualTo("http://127.0.0.1:7890"));
        Assert.That(request.Environment["NO_PROXY"], Is.EqualTo(LocalNoProxy));
    }

    [Test]
    public void EnabledNamedProxy_ComposesProxyEnvironmentWithoutExposingProxyUrlInDisplay()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = TrustedSettingsFor(home) with
        {
            NetworkPolicy = SandboxNetworkPolicy.Enabled,
            Proxy = new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = "corp-proxy" }
        };
        var appNetwork = new NetworkSettings
        {
            Proxies =
            [
                new ProxyDefinition
                {
                    Name = "corp-proxy", Protocol = ProxyProtocol.Http, Host = "127.0.0.1", Port = 7890
                }
            ]
        };

        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root, null, appNetwork);

        Assert.That(composed.TargetEnvironment["NO_PROXY"], Is.EqualTo(LocalNoProxy));
        Assert.That(composed.TargetEnvironment["HTTP_PROXY"], Is.EqualTo("http://127.0.0.1:7890"));
        Assert.That(composed.TargetEnvironment["HTTPS_PROXY"], Is.EqualTo("http://127.0.0.1:7890"));
        TestAssert.Contains("proxyEnvironment=", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
        TestAssert.Contains("network=enabled", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
        // 代理地址按凭据级敏感信息处理：展示与身份串均不得出现可解析的代理 URL。
        TestAssert.DoesNotContain("http://", composed.ExecutionPolicy.DisplayName, StringComparison.Ordinal);
        TestAssert.DoesNotContain("http://", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);
    }

    [Test]
    public async Task MissingOnlineCredentials_FailClosedWithoutOfflineDowngradeOrLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var credentials = new RecordingCredentialSource
        {
            ThrowOnLoadOnline =
                new InvalidOperationException("Sandbox account credentials contain a null online account entry.")
        };
        var (backend, _, desktops, launcher, home) = CreateBackend(NormalRunner, SandboxNetworkPolicy.Enabled,
                                                                   credentials);
        using var homeScope = home;

        await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                                                                      PlainPlan(home.Components.SandboxHome),
                                                                      new RecordingOutputCapture(),
                                                                      new RecordingOutputCapture(),
                                                                      CancellationToken.None));

        // fail closed：不启动 runner、不创建桌面、绝不降级到离线账户。
        Assert.That(launcher.LastRequest, Is.Null);
        Assert.That(desktops.GrantedSids, Is.Empty);
        Assert.That(credentials.Calls, Does.Not.Contain("offline"));
        await Task.CompletedTask;
    }

    [Test]
    public void ReadRootCoveringSandboxHome_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var       settings  = TrustedSettingsFor(home);

        // 读根包含或等于 sandbox home 会暴露其中的 DPAPI 凭据；必须拒绝且不回退宿主执行。
        Assert.Throws<ArgumentException>(() => WindowsSandboxComposer.Compose(
                                                                              settings with
                                                                              {
                                                                                  AdditionalReadRoots = [home.Root]
                                                                              }, workspace.Root));
    }

    [Test]
    public async Task OnlineReadinessCheck_FollowsEffectiveNetworkAndBlocksWhenNotReady()
    {
        if (!OperatingSystem.IsWindows()) return;

        var offlineChecks = 0;
        var (offlineBackend, _, _, _, offlineHome) =
            CreateBackend(NormalRunner, SandboxNetworkPolicy.Restricted,
                          inspectOnlineAccount : _ =>
                          {
                              offlineChecks++;
                              return Task.FromResult(WindowsSandboxStatus.Ok());
                          });
        using (offlineHome)
        {
            await offlineBackend.ExecuteAsync(PlainPlan(offlineHome.Components.SandboxHome),
                                              new RecordingOutputCapture(), new RecordingOutputCapture(),
                                              CancellationToken.None);
        }

        Assert.That(offlineChecks, Is.Zero, "Offline execution must not inspect the online account.");

        var onlineChecks = 0;
        var (onlineBackend, _, _, _, onlineHome) = CreateBackend(NormalRunner, SandboxNetworkPolicy.Restricted,
                                                                 inspectOnlineAccount : _ =>
                                                                 {
                                                                     onlineChecks++;
                                                                     return Task.FromResult(WindowsSandboxStatus.Ok());
                                                                 });
        using (onlineHome)
        {
            await onlineBackend.ExecuteAsync(PlainPlan(onlineHome.Components.SandboxHome) with
            {
                NetworkAccessEnabled = true
            }, new RecordingOutputCapture(), new RecordingOutputCapture(), CancellationToken.None);
        }

        Assert.That(onlineChecks, Is.EqualTo(1), "Effective online execution must inspect the account once.");

        var blockedChecks      = 0;
        var blockedCredentials = new RecordingCredentialSource();
        var (blockedBackend, blockedSetup, blockedDesktops, blockedLauncher, blockedHome) =
            CreateBackend(NormalRunner, SandboxNetworkPolicy.Restricted, blockedCredentials,
                          inspectOnlineAccount : _ =>
                          {
                              blockedChecks++;
                              return Task.FromResult(WindowsSandboxStatus.Blocked(["online account unavailable"]));
                          });
        using (blockedHome)
        {
            var exception = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
                blockedBackend.ExecuteAsync(PlainPlan(blockedHome.Components.SandboxHome) with
                {
                    NetworkAccessEnabled = true
                }, new RecordingOutputCapture(), new RecordingOutputCapture(), CancellationToken.None));

            Assert.That(exception.Message, Does.Contain("online account unavailable"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(blockedChecks, Is.EqualTo(1));
            Assert.That(blockedSetup.RefreshPayloads, Is.Empty);
            Assert.That(blockedLauncher.LastRequest, Is.Null);
            Assert.That(blockedDesktops.GrantedSids, Is.Empty);
            Assert.That(blockedCredentials.Calls, Is.Empty);
        });
    }

    [Test]
    public async Task ApprovedReadExecutions_LaunchDedicatedAuthorizationOfflineAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        var       spawnSeen   = SpawnSeen();
        var       credentials = new RecordingCredentialSource();
        using var approved    = new TestTempDir();
        var (backend, _, _, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                            SandboxNetworkPolicy.Restricted,
                                                            credentials,
                                                            policyOverride : policy => policy with
                                                            {
                                                                DenyReadPaths = [approved.Root]
                                                            });
        using var homeScope = home;

        // 获批读执行必须以专用授权账户启动：其授权组 deny 面 = 全量 deny − 获批路径；
        // 普通沙箱账户的全量 deny 会压制获批读，绝不回退。
        await backend.ExecuteAsync(AuthorizedPlan(home.Components.SandboxHome, approved.Root),
                                   new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessAuthOff"));
        // 读授权不顺带开放网络：离线授权账户保持不加载 profile。
        Assert.That(launcher.LastRequest.LoadUserProfile, Is.False);
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("restricted"));
        Assert.That(credentials.Calls, Does.Contain("authorized-offline"));
        Assert.That(credentials.Calls, Does.Not.Contain("offline"));
        Assert.That(credentials.Calls, Does.Not.Contain("online"));
    }

    [Test]
    public async Task ApprovedReadExecutions_WhileEffectivelyOnline_LaunchDedicatedAuthorizationOnlineAccount()
    {
        if (!OperatingSystem.IsWindows()) return;

        var       spawnSeen   = SpawnSeen();
        var       credentials = new RecordingCredentialSource();
        using var approved    = new TestTempDir();
        var (backend, _, _, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                            SandboxNetworkPolicy.Enabled,
                                                            credentials,
                                                            policyOverride : policy => policy with
                                                            {
                                                                DenyReadPaths = [approved.Root]
                                                            });
        using var homeScope = home;

        await backend.ExecuteAsync(AuthorizedPlan(home.Components.SandboxHome, approved.Root),
                                   new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessAuthOn"));
        // 生效在线的授权执行加载授权在线账户 profile（schannel 客户端凭据语义与普通在线一致）。
        Assert.That(launcher.LastRequest.LoadUserProfile, Is.True);
        Assert.That(request.PermissionProfile.Network, Is.EqualTo("enabled"));
        Assert.That(credentials.Calls, Does.Contain("authorized-online"));
        Assert.That(credentials.Calls, Does.Not.Contain("online"));
    }

    [Test]
    public async Task ApprovedReadExecutions_MissingAuthorizationCredentials_FailClosedWithoutLaunch()
    {
        if (!OperatingSystem.IsWindows()) return;

        var credentials = new RecordingCredentialSource
        {
            ThrowOnLoadAuthorized =
                new InvalidOperationException("Sandbox account credentials version 5 does not carry the " +
                                              "authorized generation 7.")
        };
        using var approved = new TestTempDir();
        var (backend, _, desktops, launcher, home) = CreateBackend(NormalRunner,
                                                                   SandboxNetworkPolicy.Restricted,
                                                                   credentials,
                                                                   policyOverride : policy => policy with
                                                                   {
                                                                       DenyReadPaths = [approved.Root]
                                                                   });
        using var homeScope = home;

        await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() => backend.ExecuteAsync(
                                                                      AuthorizedPlan(home.Components.SandboxHome,
                                                                          approved.Root),
                                                                      new RecordingOutputCapture(),
                                                                      new RecordingOutputCapture(),
                                                                      CancellationToken.None));

        // fail closed：不启动 runner、不创建桌面、绝不回退普通沙箱账户。
        Assert.That(launcher.LastRequest, Is.Null);
        Assert.That(desktops.GrantedSids, Is.Empty);
        Assert.That(credentials.Calls, Does.Not.Contain("offline"));
        Assert.That(credentials.Calls, Does.Not.Contain("online"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task OrdinaryExecutions_WithAuthorizationCapabilityPresent_NeverSelectAuthorizationAccounts()
    {
        if (!OperatingSystem.IsWindows()) return;

        var       spawnSeen   = SpawnSeen();
        var       credentials = new RecordingCredentialSource();
        using var approved    = new TestTempDir();
        var (backend, _, _, launcher, home) = CreateBackend(SpawnCapturingRunner(spawnSeen),
                                                            SandboxNetworkPolicy.Restricted,
                                                            credentials,
                                                            policyOverride : policy => policy with
                                                            {
                                                                DenyReadPaths = [approved.Root]
                                                            });
        using var homeScope = home;

        // 普通 grant（只含写根）不触发授权账户：专用账户只在获批读执行上接管。
        var plan = PlainPlan(home.Components.SandboxHome) with
        {
            GrantScope = new SandboxGrantScope
            {
                ReadRoots = [], WriteRoots = [approved.Root], MetadataWritePaths = []
            }
        };
        await backend.ExecuteAsync(plan, new RecordingOutputCapture(), new RecordingOutputCapture(),
                                   CancellationToken.None);

        var request = await spawnSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(launcher.LastRequest, Is.Not.Null);
        Assert.That(launcher.LastRequest!.Account.Username, Is.EqualTo("TinyHarnessOffline"));
        Assert.That(credentials.Calls, Does.Contain("offline"));
        Assert.That(credentials.Calls, Does.Not.Contain("authorized-offline"));
        Assert.That(credentials.Calls, Does.Not.Contain("authorized-online"));
        await Task.CompletedTask;
    }

    // ---- 工具与 fake ----

    private static TaskCompletionSource<RunnerSpawnRequest> SpawnSeen() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static PreparedProcessExecution PlainPlan(string workingDirectory) =>
        new("cmd.exe", ["/c", "whoami"], workingDirectory, 30, false, null);

    /// <summary>
    ///     构造携带获批只读根的执行计划：ReadRoots 命中策略 deny 集时走授权通道。
    ///     Builds a plan carrying an approved read root; a ReadRoots entry hitting the
    ///     policy's deny set routes the execution onto the authorization channel.
    /// </summary>
    private static PreparedProcessExecution AuthorizedPlan(string workingDirectory, string approvedReadRoot) =>
        PlainPlan(workingDirectory) with
        {
            GrantScope = new SandboxGrantScope
            {
                ReadRoots = [approvedReadRoot], WriteRoots = [], MetadataWritePaths = []
            }
        };

    /// <summary>
    ///     构造组件齐全的可信设置：setup/runner 为占位文件，Compose 的存在性校验可通过。
    ///     Builds trusted settings with present components: setup/runner are stub
    ///     files so the composer's existence checks pass.
    /// </summary>
    private static WindowsSandboxSettings TrustedSettingsFor(TestTempDir home) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = home.WriteBytes("setup.exe", [0x4d, 0x5a]),
        RunnerExecutablePath = home.WriteBytes("runner.exe", [0x4d, 0x5a]),
        SandboxHome          = home.Root
    };

    private static WindowsSandboxSettings TrustedSettings(WindowsSandboxComponents components) => new()
    {
        Enabled              = true,
        SetupExecutablePath  = components.SetupExecutablePath,
        RunnerExecutablePath = components.RunnerExecutablePath,
        SandboxHome          = components.SandboxHome
    };

    /// <summary>
    ///     构造使用内存管道的沙箱后端；workspaceRoot 取 sandbox home。standing 与 proxyEnvironment
    ///     按组合器的调用形态传入，分别表达常设网络信任与已冻结的代理环境。
    ///     Builds a sandbox backend over in-memory pipes; the workspace root is the
    ///     sandbox home. standing and proxyEnvironment mirror the composer's call
    ///     shape, expressing standing network trust and the frozen proxy environment.
    /// </summary>
    private static (WindowsSandboxBackend Backend, FakeSetupInvoker Setup, FakeDesktopFactory Desktops,
        FakeSandboxRunnerLauncher Launcher, SandboxTestHome Home) CreateBackend(
            Func<Stream, Stream, CancellationToken, Task>         runnerBody,
            SandboxNetworkPolicy                                  network,
            RecordingCredentialSource?                            credentials          = null,
            IReadOnlyDictionary<string, string>?                  proxyEnvironment     = null,
            Func<SandboxIsolationPolicy, SandboxIsolationPolicy>? policyOverride       = null,
            Func<CancellationToken, Task<WindowsSandboxStatus>>?  inspectOnlineAccount = null)
    {
        var home = new SandboxTestHome();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEMP"] = home.Components.SandboxHome
        };
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome, environment,
                                                   network : network);
        // 测试注入的策略改写（如 deny 集构造授权分流）在解析后立即生效。
        if (policyOverride is not null) policy = policyOverride(policy);
        var standingEnabled                    = network == SandboxNetworkPolicy.Enabled;
        var setup                              = new FakeSetupInvoker();
        var desktops                           = new FakeDesktopFactory();
        var launcher                           = new FakeSandboxRunnerLauncher { RunnerBody = runnerBody };
        var backend = new WindowsSandboxBackend(home.Components, policy, environment, null,
                                                setup,
                                                credentials ?? new RecordingCredentialSource(),
                                                null,
                                                desktops, launcher,
                                                TestLimits, _ => { },
                                                (_, _) => Task.CompletedTask,
                                                standingEnabled,
                                                proxyEnvironment,
                                                inspectOnlineAccount ??
                                                (_ => Task.FromResult(WindowsSandboxStatus.Ok())));
        return (backend, setup, desktops, launcher, home);
    }

    private static Func<Stream, Stream, CancellationToken, Task> SpawnCapturingRunner(
        TaskCompletionSource<RunnerSpawnRequest> spawnSeen) =>
        async (downstream, upstream, end) =>
        {
            var frame = await RunnerFrameCodec.ReadFrameAsync(downstream, end);
            TestAssert.NotNull(frame);
            spawnSeen.TrySetResult(RunnerFrameCodec.ParsePayload<RunnerSpawnRequest>(frame!));
            await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready",
                                                   new RunnerSpawnReady { ProcessId = 4321 }, end);
            await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                                   new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
        };

    private static async Task NormalRunner(Stream downstream, Stream upstream, CancellationToken end)
    {
        await RunnerFrameCodec.ReadFrameAsync(downstream, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "spawn_ready", new RunnerSpawnReady { ProcessId = 1 }, end);
        await RunnerFrameCodec.WriteFrameAsync(upstream, "exit",
                                               new RunnerExitPayload { ExitCode = 0, TimedOut = false }, end);
    }

    /// <summary>
    ///     记录 offline/online 加载调用的 fake 凭据源；可注入在线加载失败以模拟 users 文件缺失 online 条目。
    ///     A fake credential source recording offline/online load calls; injects an
    ///     online-load failure to simulate a users file without the online entry.
    /// </summary>
    private sealed class RecordingCredentialSource : ISandboxCredentialSource
    {
        public Exception? ThrowOnLoadOnline { get; init; }

        public Exception? ThrowOnLoadAuthorized { get; init; }

        public List<string> Calls { get; } = [];

        public Task<WindowsSandboxAccount> LoadOfflineAccount(CancellationToken cancellationToken = default)
        {
            Calls.Add("offline");
            return Task.FromResult(Account("TinyHarnessOffline"));
        }

        public Task<WindowsSandboxAccount> LoadOnlineAccount(CancellationToken cancellationToken = default)
        {
            Calls.Add("online");
            return ThrowOnLoadOnline is not null
                ? Task.FromException<WindowsSandboxAccount>(ThrowOnLoadOnline)
                : Task.FromResult(Account("TinyHarnessOnline"));
        }

        public Task<WindowsSandboxAccount> LoadAuthorizedOfflineAccount(CancellationToken cancellationToken = default)
        {
            Calls.Add("authorized-offline");
            return ThrowOnLoadAuthorized is not null
                ? Task.FromException<WindowsSandboxAccount>(ThrowOnLoadAuthorized)
                : Task.FromResult(Account("TinyHarnessAuthOff"));
        }

        public Task<WindowsSandboxAccount> LoadAuthorizedOnlineAccount(CancellationToken cancellationToken = default)
        {
            Calls.Add("authorized-online");
            return ThrowOnLoadAuthorized is not null
                ? Task.FromException<WindowsSandboxAccount>(ThrowOnLoadAuthorized)
                : Task.FromResult(Account("TinyHarnessAuthOn"));
        }

        private static WindowsSandboxAccount Account(string username)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return new WindowsSandboxAccount(username, "test-password",
                                             new SecurityIdentifier("S-1-5-21-100-200-300-404"));
        }
    }
}
