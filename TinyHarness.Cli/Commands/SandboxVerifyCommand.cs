using System.Diagnostics;
using System.Text;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>
///         `tinyharness sandbox verify`：opt-in 的 Windows 沙箱隔离验收（真实系统测试）。要求沙箱已配置、已启用且就绪——否则拒绝执行并提示先 provision；绝不自动
///         provisioning、绝不回退宿主执行。组合逻辑与 run 共享（<see cref="SandboxCliComposition" />），knownSecrets 解析一致；每个用例按策略种类克隆可信设置后走
///         <see cref="WindowsSandboxComposer.Compose" />，探针环境变量经 extraEnvironment 进入同一条清理环境路径。隐藏子命令 `sandbox
///         verify-worker` 是宿主异常退出用例的子进程模式。两个入口都不把输入发送给模型。
///     </para>
///     <para>
///         `tinyharness sandbox verify`: the opt-in Windows sandbox isolation
///         acceptance (a real system test). It requires the sandbox to be
///         configured, enabled, and ready — otherwise it refuses and suggests
///         provisioning first; it never provisions and never falls back to host
///         execution. Composition is shared with run
///         (<see cref="SandboxCliComposition" />) including the knownSecrets
///         resolution; every case clones the trusted settings per policy kind and
///         goes through <see cref="WindowsSandboxComposer.Compose" />, with the
///         probe variables entering the same cleaned-environment path via
///         extraEnvironment. The hidden `sandbox verify-worker` subcommand is the
///         child-process mode of the host-abnormal-exit case. Neither entry sends
///         input to a model.
///     </para>
/// </summary>
internal static class SandboxVerifyCommand
{
    /// <summary>
    ///     <para>存在 FAIL / INDETERMINATE / SKIPPED 时的退出码（0 仅当全部所选用例 PASS）。</para>
    ///     <para>The exit code when any FAIL / INDETERMINATE / SKIPPED exists (0 only when every selected case passes).</para>
    /// </summary>
    private const int AcceptanceFailedExitCode = 4;

    private const int WorkerRunnerStartupDelaySeconds = 5;
    private const int WorkerSleeperDurationSeconds    = 600;
    private const int WorkerCleanupGraceSeconds       = 10;

    public static async Task<int> ExecuteAsync(
        CommandContext                context,
        CliOptions                    options,
        CancellationToken             cancellationToken,
        WindowsSandboxBackendFactory? backendFactory = null,
        SandboxVerifyWorkerSpawner?   workerSpawner  = null)
    {
        var io = context.Io;
        if (!OperatingSystem.IsWindows())
        {
            await io.WriteLineAsync("  [fail] sandbox verify 仅支持 Windows", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var configPath      = context.ResolveUserConfigPath();
        var userConfig      = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings        = userConfig.Settings?.WindowsSandbox;
        var networkSettings = userConfig.Settings?.Network;
        // --network 决定验收的网络模式与用例集；restricted（默认）保持原有 17 用例行为零变化。
        var networkPolicy = ResolveNetworkPolicy(options.VerifyNetwork);

        await io.WriteLineAsync("TinyHarness sandbox verify", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  network  : {NetworkDisplay(networkPolicy)}", cancellationToken)
                .ConfigureAwait(false);

        if (settings is null || IsBlankSettings(settings))
        {
            await io
                 .WriteLineAsync("  [fail] 用户配置未设置 settings.windowsSandbox；sandbox verify 需要先配置组件路径" +
                                 "（sandboxHome 可省略，缺省使用独立固定默认 home）并运行 tinyharness sandbox provision",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        if (!settings.Enabled)
        {
            await io
                 .WriteLineAsync("  [fail] settings.windowsSandbox.enabled 不是 true；sandbox verify 不做任何事。" +
                                 "确认隔离配置后显式启用再验收。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var pathProblems = CollectPathProblems(settings);
        if (pathProblems.Count > 0)
        {
            foreach (var problem in pathProblems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            return 1;
        }

        var components = new WindowsSandboxComponents
        {
            AllowTcpLoopback = settings.AllowTcpLoopback,
            SetupExecutablePath = settings.SetupExecutablePath.Trim(),
            RunnerExecutablePath = settings.RunnerExecutablePath.Trim(),
            SandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback)
        };
        var readiness = await new WindowsSandboxStateInspector(components).InspectAsync(cancellationToken)
                                                                          .ConfigureAwait(false);
        if (!readiness.Ready)
        {
            foreach (var problem in readiness.Problems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            await io
                 .WriteLineAsync("沙箱未就绪；先运行 tinyharness sandbox provision，verify 不会自动 provisioning。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var caseFilter    = options.CaseFilter is { Count: > 0 } ? options.CaseFilter : null;
        var supportedCases = WindowsSandboxAcceptanceRunner.SupportedCaseNames(networkPolicy);
        var unknownCases  = (caseFilter ?? supportedCases)
                           .Where(name => !supportedCases.Contains(name, StringComparer.Ordinal))
                           .ToArray();
        if (unknownCases.Length > 0)
        {
            await io
                 .WriteLineAsync($"  [fail] 未知验收用例名: {string.Join(", ", unknownCases)}；有效名称: " +
                                 string.Join(", ", supportedCases),
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var caseCount     = (caseFilter ?? WindowsSandboxAcceptanceRunner.DefaultCaseNames(networkPolicy)).Count;
        var workspaceRoot = ResolveWorkspace(options.VerifyWorkspace);

        // knownSecrets 的解析与 run 完全一致，使验收环境匹配生产组合语义。
        // 密钥源不可用（如凭据存储在当前环境不可读）只意味着没有已知 secret：
        // verify 不调用模型，不得被密钥可用性阻塞。
        var resolution   = await ConfigResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        var knownSecrets = ResolveKnownSecrets(context, resolution, cancellationToken);

        // 验收不写持久会话日志；共享组合路径在确认门之前校验组件与验收工作区。
        // networkSettings 与生产 run 同源传入（同一用户配置读取）：Named 代理等沙箱代理解析
        // 与生产一致，绝不因验收路径缺配置而误判"代理不存在"；restricted 且未配置代理时解析
        // 结果为空，政策身份与历史基线一致。
        WindowsSandboxExecution preview;
        try
        {
            preview = SandboxCliComposition.TryCompose(WithNetworkPolicy(settings, networkPolicy), workspaceRoot,
                                                       knownSecrets, null, networkSettings)
                   ?? throw new InvalidOperationException("settings.windowsSandbox.enabled is required for verify.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await io.WriteLineAsync($"  [fail] 沙箱组合失败：{ex.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await PrintOverviewAsync(io, preview, components, caseCount, workspaceRoot, networkPolicy,
                                 NamedProxyHostPort(networkSettings, settings.Proxy),
                                 WindowsSandboxAcceptanceLimits.Default.OnlineExternalProbeUrl, cancellationToken)
           .ConfigureAwait(false);

        if (!options.AssumeYes)
        {
            if (!io.IsInteractive)
            {
                await io
                     .WriteLineAsync("  [fail] 非交互环境必须使用 --yes 才能执行 sandbox verify；未执行任何命令",
                                     cancellationToken)
                     .ConfigureAwait(false);
                return 1;
            }

            if (!await CliPrompt.ConfirmAsync(io, "确认执行上述真实沙箱命令？", false, cancellationToken)
                                .ConfigureAwait(false))
            {
                await io.WriteLineAsync("  已取消；未执行任何命令。", cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }

        var factory = backendFactory ?? ((kind, extraEnvironment) =>
            ComposeCase(settings, kind, extraEnvironment, workspaceRoot, knownSecrets, networkPolicy,
                        networkSettings));
        var spawner = workerSpawner ?? ((markerPath, holdSeconds) =>
            StartWorkerProcess(markerPath, holdSeconds, workspaceRoot, networkPolicy));
        var onlineProxyHostPort = NamedProxyHostPort(networkSettings, settings.Proxy);
        var runner = new WindowsSandboxAcceptanceRunner(factory, workspaceRoot, spawner,
                                                        limits : new WindowsSandboxAcceptanceLimits
                                                        {
                                                            NetworkTargetObserver = (message, token) =>
                                                                io.WriteLineAsync($"  network: {message}", token),
                                                            OnlineProxyHostPort    = onlineProxyHostPort,
                                                            HostLoopbackProbeTarget =
                                                                IsLoopbackHostPort(onlineProxyHostPort)
                                                                    ? onlineProxyHostPort
                                                                    : null
                                                        },
                                                        networkPolicy : networkPolicy);

        WindowsSandboxAcceptanceReport report;
        try
        {
            report = await runner.RunAsync(caseFilter, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await io.WriteLineAsync($"  [fail] {ex.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        return await ReportAsync(io, report, workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>
    ///         隐藏子命令 `sandbox verify-worker`：组合沙箱并启动长 sleeper，就绪后写 marker 并保持 holdSeconds 秒，供父进程 Kill 以检验遗弃回收。marker 只在
    ///         runner 启动稳定后写出；写出前的任何失败都不写 marker（父进程按 marker 超时记 FAIL）。marker 路径必须位于 --workspace 的 artifacts 目录内，并由父进程在 spawn
    ///         前清理——本命令不删除任何传入路径。--network 携带父进程解析出的验收网络模式，组合与父进程同源（同 networkSettings）；缺省时按常设设置同路。绝不提权、绝不 provisioning。
    ///     </para>
    ///     <para>
    ///         The hidden `sandbox verify-worker` subcommand: composes the sandbox,
    ///         starts a long sleeper, writes the marker once the runner is settled,
    ///         and stays alive for holdSeconds so the parent can Kill it and check
    ///         abandoned-process reclamation. The marker is written only after the
    ///         runner startup settles; any earlier failure leaves no marker (the
    ///         parent records a marker timeout as FAIL). The marker path must stay
    ///         inside --workspace's artifacts directory and is cleared by the parent
    ///         before spawning — this command never deletes a caller-supplied path.
    ///         --network carries the acceptance network mode resolved by the parent,
    ///         and composition shares the parent's source of networkSettings; when
    ///         absent (manual invocation) it follows the standing setting.
    ///         It never elevates and never provisions.
    ///     </para>
    /// </summary>
    public static async Task<int> ExecuteWorkerAsync(
        CommandContext context, CliOptions options, CancellationToken cancellationToken)
    {
        var markerPath      = Path.GetFullPath(options.VerifyMarkerPath!);
        var holdSeconds     = options.VerifyHoldSeconds!.Value;
        var workspaceRoot   = Path.GetFullPath(options.VerifyWorkspace!);
        // worker 只写 marker；陈旧 marker 由父进程在 spawn 前清理。把 marker
        // 限制在验收工作区的 artifacts 目录内，防止手工调用的 worker 写到
        // 预期布局之外。
        var markerDirectory = Path.Combine(workspaceRoot, "artifacts");
        if (!Workspace.IsInside(markerDirectory, markerPath))
        {
            await context
                 .Io.WriteErrorLineAsync($"sandbox verify-worker: marker 必须位于验收工作区的 artifacts 目录内（{markerDirectory}）；" +
                                         $"收到 '{markerPath}'；退出。", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var configPath = context.ResolveUserConfigPath();
        var userConfig = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings   = userConfig.Settings?.WindowsSandbox;
        if (settings is not { Enabled: true })
        {
            await context.Io.WriteErrorLineAsync("sandbox verify-worker: settings.windowsSandbox.enabled 不是 true；退出。",
                                                 cancellationToken).ConfigureAwait(false);
            return 1;
        }

        // 与父进程 verify 同源组合：--network 显式携带父进程解析出的验收网络模式（缺省仅出现在
        // 手工调用，按常设设置同路），networkSettings 从同一用户配置读取——Named 代理等沙箱代理
        // 解析与父进程和生产 run 完全一致，绝不因 worker 组合缺配置而误判"代理不存在"。
        var networkSettings = userConfig.Settings?.Network;
        var networkPolicy   = options.VerifyNetwork is null
            ? settings.NetworkPolicy
            : ResolveNetworkPolicy(options.VerifyNetwork);

        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            var waitfor = string.IsNullOrWhiteSpace(systemRoot)
                ? null
                : Path.Combine(systemRoot, "System32", "waitfor.exe");
            if (waitfor is null || !File.Exists(waitfor))
            {
                await context.Io.WriteErrorLineAsync("sandbox verify-worker: 缺少 waitfor.exe（System32），无法布置长 sleeper。",
                                                     cancellationToken)
                             .ConfigureAwait(false);
                return 1;
            }

            // 组合方式与父进程 verify 一致：同一验收网络模式与同源 networkSettings，不检查未使用的旧快照目录。
            var resolution   = await ConfigResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            var knownSecrets = ResolveKnownSecrets(context, resolution, cancellationToken);
            var execution    = SandboxCliComposition.TryCompose(WithNetworkPolicy(settings, networkPolicy),
                                                                workspaceRoot, knownSecrets, null, networkSettings);
            if (execution is null)
            {
                await context.Io.WriteErrorLineAsync("sandbox verify-worker: 沙箱未启用；退出。", cancellationToken)
                             .ConfigureAwait(false);
                return 1;
            }

            var plan = new PreparedProcessExecution(waitfor,
                                                    [
                                                        "/t", WorkerSleeperDurationSeconds.ToString(),
                                                        $"th-{Guid.NewGuid():N}"
                                                    ],
                                                    workspaceRoot, WorkerSleeperDurationSeconds + 60, false, null);
            var       artifactDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath))!;
            var       stdout = new VerifyOutputCapture(Path.Combine(artifactDirectory, "worker.stdout.txt"));
            var       stderr = new VerifyOutputCapture(Path.Combine(artifactDirectory, "worker.stderr.txt"));
            using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var       runTask = execution.Backend.ExecuteAsync(plan, stdout, stderr, workerCancellation.Token);

            await Task.Delay(TimeSpan.FromSeconds(WorkerRunnerStartupDelaySeconds), cancellationToken)
                      .ConfigureAwait(false);
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                      .ConfigureAwait(false);

            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(holdSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 父进程始终没有到来；清理退出，而不是继续滞留。
                await workerCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(WorkerCleanupGraceSeconds), CancellationToken.None)
                                 .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 对本 worker 自身的 run 做有界的尽力清理。
                }
            }

            stdout.Complete();
            stderr.Complete();
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await context.Io.WriteErrorLineAsync($"sandbox verify-worker failed: {ex.Message}", cancellationToken)
                         .ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    ///     <para>与 run 一致地解析 knownSecrets；密钥源不可读（如凭据存储在当前平台不可用）只意味着没有已知 secret——verify 不调用模型，不得被密钥可用性阻塞。</para>
    ///     <para>
    ///         Resolves knownSecrets exactly like run; an unreadable key source (for
    ///         example a credential store unavailable on this platform) merely means
    ///         no known secrets — verify never calls a model and must not be blocked
    ///         by key availability.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> ResolveKnownSecrets(
        CommandContext context, ConfigResolution resolution, CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = ApiKeyReader.Read(resolution.Config.ApiKeyEnvironmentVariable,
                                           resolution.Config.ApiKeyCredentialTarget, context.Credentials,
                                           resolution.ProfileName);
            return SandboxCliComposition.KnownSecrets(resolution.Config, apiKey);
        }
        catch (ConfigException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     <para>
    ///         按策略种类与用例附加环境克隆可信设置并组合：探针变量并入 extraEnvironment，与 run 走同一条清理环境组合路径（allowlist + TEMP/TMP 重定向 + secret 移除），绝不旁路拼
    ///         spawn env。networkSettings 与生产 run 同源无条件传入，代理解析与生产一致。
    ///     </para>
    ///     <para>
    ///         Clones the trusted settings per policy kind plus case-scoped extra
    ///         environment and composes: probe variables merge into
    ///         extraEnvironment, going through the same cleaned-environment
    ///         composition path as run (allowlist + TEMP/TMP redirect + secret
    ///         removal); the spawn env is never hand-built around it.
    ///         networkSettings is always passed from the same source as the
    ///         production run, keeping proxy resolution identical.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     <para>解析 --network 的取值；解析器已把取值收敛为 restricted|enabled，缺省与 restricted 同路。</para>
    ///     <para>
    ///         Resolves the --network value; the parser already narrows it to
    ///         restricted|enabled, and the default shares the restricted path.
    ///     </para>
    /// </summary>
    private static SandboxNetworkPolicy ResolveNetworkPolicy(string? requested) =>
        requested == "enabled" ? SandboxNetworkPolicy.Enabled : SandboxNetworkPolicy.Restricted;

    private static string NetworkDisplay(SandboxNetworkPolicy networkPolicy) => networkPolicy switch
    {
        SandboxNetworkPolicy.Enabled    => "enabled（常设在线：全部命令以在线账户运行并注入代理环境；" +
                                           "外网探测仅限轻量只读端点）",
        SandboxNetworkPolicy.Restricted => "restricted（离线账户，外部网络阻断；默认行为与历史基线一致）",
        _                               => networkPolicy.ToString()
    };

    /// <summary>
    ///     <para>把验收网络模式应用到可信设置：--network 对验收运行是权威值，覆盖常设设置。</para>
    ///     <para>
    ///         Applies the acceptance network mode to the trusted settings; --network
    ///         is authoritative for the acceptance run and overrides the standing
    ///         setting.
    ///     </para>
    /// </summary>
    private static WindowsSandboxSettings WithNetworkPolicy(WindowsSandboxSettings settings,
                                                            SandboxNetworkPolicy    networkPolicy) =>
        settings.NetworkPolicy == networkPolicy ? settings : settings with { NetworkPolicy = networkPolicy };

    /// <summary>
    ///     <para>
    ///         Named 代理模式的 host:port（仅用于显示与在线断言，绝不携带 scheme 或凭据）；
    ///         其他代理模式返回 null，在线断言退化为结构检查。
    ///     </para>
    ///     <para>
    ///         The named-mode proxy host:port (display and online assertions only,
    ///         never a scheme or credentials); other proxy modes return null and the
    ///         online assertions degrade to structural checks.
    ///     </para>
    /// </summary>
    private static string? NamedProxyHostPort(NetworkSettings? networkSettings, SandboxProxyPolicy proxy)
    {
        if (proxy.Mode != SandboxProxyMode.Named) return null;

        return networkSettings?.Proxies
                              .FirstOrDefault(candidate => string.Equals(candidate.Name, proxy.ProxyName,
                                                                          StringComparison.OrdinalIgnoreCase))
                   is { } proxyDefinition
            ? $"{proxyDefinition.Host}:{proxyDefinition.Port}"
            : null;
    }

    /// <summary>host:port 是否指向环回地址；只有环回端点才能作为宿主环回探测目标。Whether host:port points at a loopback address; only loopback endpoints qualify as host-loopback probe targets.</summary>
    private static bool IsLoopbackHostPort(string? hostPort)
    {
        if (string.IsNullOrWhiteSpace(hostPort)) return false;

        var separator = hostPort.LastIndexOf(':');
        var host      = separator > 0 ? hostPort[..separator].Trim('[', ']') : hostPort;
        return host == "127.0.0.1" || host == "::1" || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static WindowsSandboxExecution ComposeCase(
        WindowsSandboxSettings               settings,
        SandboxPolicyKind                    policyKind,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        string                               workspaceRoot,
        IReadOnlyDictionary<string, string>  knownSecrets,
        SandboxNetworkPolicy                 networkPolicy,
        NetworkSettings?                     networkSettings)
    {
        var merged = extraEnvironment is null
            ? settings.ExtraEnvironment
            : MergeEnvironment(settings.ExtraEnvironment, extraEnvironment);
        var caseSettings = settings with
        {
            Policy           = policyKind,
            ExtraEnvironment = merged,
            NetworkPolicy    = networkPolicy
        };
        return WindowsSandboxComposer.Compose(caseSettings, workspaceRoot, knownSecrets, networkSettings);
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string>? baseEnvironment, IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseEnvironment is not null)
            foreach (var (name, value) in baseEnvironment)
                merged[name] = value;

        foreach (var (name, value) in extraEnvironment) merged[name] = value;

        return merged;
    }

    private static string ResolveWorkspace(string? requested) => requested is null
        ? Path.Combine(Path.GetTempPath(), "TinyHarness", $"sandbox-verify-{DateTimeOffset.Now:yyyyMMdd-HHmmss}")
        : Path.GetFullPath(requested);

    private static ISandboxVerifyWorkerProcess StartWorkerProcess(
        string markerPath, int holdSeconds, string workspaceRoot, SandboxNetworkPolicy networkPolicy)
    {
        var executable = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Cannot determine the current process executable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        // 与进程探针的先例一致：dotnet host 需要把入口程序集作为第一个参数。
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);

        foreach (var argument in new[]
                 {
                     "sandbox", "verify-worker", "--marker", markerPath, "--hold-seconds", holdSeconds.ToString(),
                     "--workspace", workspaceRoot,
                     "--network", networkPolicy == SandboxNetworkPolicy.Enabled ? "enabled" : "restricted"
                 })
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Failed to start the sandbox verify-worker process.");
        return new WorkerProcessHandle(process);
    }

    private static async Task PrintOverviewAsync(ICliConsole io, WindowsSandboxExecution preview,
                                                 WindowsSandboxComponents components, int caseCount,
                                                 string workspaceRoot, SandboxNetworkPolicy networkPolicy,
                                                 string? onlineProxyHostPort, string probeUrl,
                                                 CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  setup    : {components.SetupExecutablePath}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  runner   : {components.RunnerExecutablePath}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  home     : {components.SandboxHome}" +
                                (string.Equals(components.SandboxHome,
                                               WindowsSandboxComponents.DefaultSandboxHome,
                                               StringComparison.OrdinalIgnoreCase)
                                    ? "（独立固定默认 home）"
                                    : string.Empty),
                                cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"  versions : 本 build 期望 setup v{components.RequiredSetupVersion} / ipc v{WindowsSandboxComponents.IpcVersion}",
                             cancellationToken)
             .ConfigureAwait(false);
        if (networkPolicy == SandboxNetworkPolicy.Enabled)
            await io
                 .WriteLineAsync($"  account  : {components.OnlineUsername}（常设在线；代理 host:port: {onlineProxyHostPort ?? "<未提供>"}）",
                                 cancellationToken)
                 .ConfigureAwait(false);
        else
            await io
                 .WriteLineAsync($"  account  : {components.OfflineUsername}（外部网络阻断，TCP 环回{(components.AllowTcpLoopback ? "允许" : "阻断")}）",
                                 cancellationToken)
                 .ConfigureAwait(false);
        await io.WriteLineAsync($"  workspace: {workspaceRoot}（host 侧布置 fixture，产物保留）", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("  policies : read-only 与 workspace-write 两种都会被执行", cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"  将执行 {caseCount} 个验收用例（网络用例各含多条命令）：每条命令一次 ACL refresh（普通权限）与一个 runner 进程。",
                             cancellationToken)
             .ConfigureAwait(false);
        await io
             .WriteLineAsync("  本命令不执行 provisioning、不自动配置账户或 Firewall/WFP；普通 refresh 会准备验收目录 ACL，也不回退宿主执行；" +
                             "这是真实系统测试，应在获得授权、可接受该测试的环境执行。",
                             cancellationToken)
             .ConfigureAwait(false);
        if (networkPolicy == SandboxNetworkPolicy.Enabled)
        {
            await io
                 .WriteLineAsync($"  enabled 模式：不建立任何监听；外网探测仅一次轻量只读请求（目标 {probeUrl}）并经配置代理发出" +
                                 "（代理地址仅以 host:port 记录），失败会如实记录为 FAIL，不做重试或放宽。", cancellationToken)
                 .ConfigureAwait(false);
            await io
                 .WriteLineAsync($"  宿主环回探测目标: {onlineProxyHostPort ?? "<未提供，环回用例记 INDETERMINATE>"}（NO_PROXY 恒排除环回，该请求为直连）。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            await io
                 .WriteLineAsync("  安全不变量（元数据保护、写根之外拒绝写、凭据目录不可读、deny-probe）在在线账户下仍必须成立。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return;
        }

        await io.WriteLineAsync("  网络监听范围：仅 127.0.0.1、::1 和枚举的一个本机非环回 IPv4；TCP/UDP 随机独占端口，" +
                                "DNS 端口检查仅在 127.0.0.1 / ::1 独占绑定 TCP/UDP 53、853，已占用则未验证。", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("  每个目标先后执行宿主 nonce ACK 正对照，再判定沙箱观测；可能出现 Windows 防火墙提示，" +
                                "用户选择可能保存规则。不要为通过测试开放公用网络；无法建立正对照则未验证。", cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<int> ReportAsync(ICliConsole io,            WindowsSandboxAcceptanceReport report,
                                               string      workspaceRoot, CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  workspace: {workspaceRoot}（产物保留）", cancellationToken).ConfigureAwait(false);
        foreach (var result in report.Cases)
        {
            await io.WriteLineAsync($"  [{result.Verdict,-13}] {result.Name} ({result.Duration})", cancellationToken)
                    .ConfigureAwait(false);
            if (result.Verdict == SandboxAcceptanceVerdict.Pass) continue;

            if (result.ActualSummary is { Length: > 0 } actual)
                await io.WriteLineAsync($"      actual : {actual}", cancellationToken).ConfigureAwait(false);

            if (result.Detail is { Length: > 0 } detail)
                await io.WriteLineAsync($"      detail : {detail}", cancellationToken).ConfigureAwait(false);
        }

        var passed        = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Pass);
        var failed        = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Fail);
        var indeterminate = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Indeterminate);
        var skipped       = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Skipped);
        await io
             .WriteLineAsync($"  合计: {report.Cases.Count} 用例；PASS {passed}，FAIL {failed}，INDETERMINATE {indeterminate}，SKIPPED {skipped}",
                             cancellationToken)
             .ConfigureAwait(false);

        if (report.ReportPath is { } reportPath)
            await io.WriteLineAsync($"  report   : {reportPath}", cancellationToken).ConfigureAwait(false);
        else
            await io.WriteLineAsync("  [warn] 报告文件写入失败；上面是完整结果，但未落盘。", cancellationToken)
                    .ConfigureAwait(false);

        await io.WriteLineAsync("  limitations 已写入报告：结论范围以报告中的局限清单为准。", cancellationToken)
                .ConfigureAwait(false);

        var clean = report.Cases.Count > 0 &&
                    report.Cases.All(result => result.Verdict == SandboxAcceptanceVerdict.Pass);
        return clean ? 0 : AcceptanceFailedExitCode;
    }

    private static bool IsBlankSettings(WindowsSandboxSettings settings) =>
        string.IsNullOrWhiteSpace(settings.SetupExecutablePath)  &&
        string.IsNullOrWhiteSpace(settings.RunnerExecutablePath) &&
        string.IsNullOrWhiteSpace(settings.SandboxHome);

    private static List<string> CollectPathProblems(WindowsSandboxSettings settings)
    {
        var problems = new List<string>();
        foreach (var (value, field) in new[]
                 {
                     (settings.SetupExecutablePath, "setupExecutablePath"),
                     (settings.RunnerExecutablePath, "runnerExecutablePath")
                 })
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                problems.Add($"settings.windowsSandbox.{field} 缺失或不是绝对路径" +
                             (string.IsNullOrWhiteSpace(value) ? "。" : $"：'{value}'。"));

        // home 可留空（使用固定的独立默认值）；只有显式相对路径才是配置错误。
        if (!string.IsNullOrWhiteSpace(settings.SandboxHome) && !Path.IsPathFullyQualified(settings.SandboxHome))
            problems.Add($"settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）：'{settings.SandboxHome}'。");

        return problems;
    }

    /// <summary>
    ///     <para>verify-worker 的进程句柄：Kill 只终止 worker 本身（不杀树）——遗弃的 runner/目标进程正是 host-exit-reclaim 用例要观测的对象。</para>
    ///     <para>
    ///         The verify-worker process handle: Kill terminates only the worker
    ///         itself (never the tree) — the abandoned runner/target processes are
    ///         exactly what the host-exit-reclaim case observes.
    ///     </para>
    /// </summary>
    private sealed class WorkerProcessHandle(Process process) : ISandboxVerifyWorkerProcess
    {
        public void Kill()
        {
            process.Kill();
        }
    }
}

/// <summary>
///     CLI 侧的轻量输出捕获（Core 的 internal <c>ProcessOutputCapture</c> 对 CLI 不可见）：增量接收
///     每个输出块、把完整输出流式写入 artifact 文件、在内存中只保留 head+tail 的有界模型视图，并记录
///     原始字符数与截断方式。供 verify-worker 使用；不做跨块脱敏（worker 上下文没有已知 secret）。
///     A lightweight CLI-side output capture (Core's internal
///     <c>ProcessOutputCapture</c> is invisible to the CLI): it receives every
///     chunk incrementally, streams the full output to an artifact file, keeps
///     only a bounded head+tail model view in memory, and records the original
///     character count and the truncation mode. Used by verify-worker; no
///     cross-chunk redaction (the worker context has no known secrets).
/// </summary>
internal sealed class VerifyOutputCapture(string artifactPath, int modelCharacterLimit = 16 * 1024)
    : IProcessOutputCapture
{
    private readonly int           _headLimit = modelCharacterLimit / 2;
    private readonly int           _tailLimit = modelCharacterLimit - modelCharacterLimit / 2;
    private readonly StringBuilder _view      = new(modelCharacterLimit);
    private          FileStream?   _artifactStream;
    private          StreamWriter? _artifactWriter;
    private          long          _totalCharacters;
    private          bool          _truncated;

    public async ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken)
    {
        if (chunk.IsEmpty) return;

        if (_artifactWriter is null)
        {
            _artifactStream = new FileStream(artifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                             16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _artifactWriter = new StreamWriter(_artifactStream, new UTF8Encoding(false));
        }

        await _artifactWriter.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        _totalCharacters += chunk.Length;
        if (!_truncated)
        {
            _view.Append(chunk.Span);
            if (_view.Length <= _headLimit + _tailLimit) return;

            _truncated = true;
            _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
            return;
        }

        _view.Append(chunk.Span);
        if (_view.Length > _headLimit + _tailLimit) _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
    }

    public CapturedProcessOutput Complete()
    {
        CloseArtifact();
        if (!_truncated)
        {
            TryDeleteArtifact();
            return new CapturedProcessOutput(_view.ToString(), false, _totalCharacters, null);
        }

        var omitted = Math.Max(0, _totalCharacters - _view.Length);
        var content = _view.ToString(0, _headLimit)                                           +
                      $"\n... [truncated {omitted} chars; full output: {artifactPath}] ...\n" +
                      _view.ToString(_view.Length - _tailLimit, _tailLimit);
        return new CapturedProcessOutput(content, true, _totalCharacters, artifactPath);
    }

    public void Discard()
    {
        CloseArtifact();
        TryDeleteArtifact();
    }

    private void CloseArtifact()
    {
        _artifactWriter?.Dispose();
        _artifactWriter = null;
        _artifactStream = null;
    }

    private void TryDeleteArtifact()
    {
        try
        {
            File.Delete(artifactPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 留下陈旧的临时 artifact 比掩盖命令结果更安全。
        }
    }
}
