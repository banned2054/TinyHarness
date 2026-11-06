using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>把可信的 WindowsSandboxSettings 组合成可执行要素。</para>
///     <para>Composes trusted WindowsSandboxSettings into executable pieces.</para>
/// </summary>
/// <remarks>
///     <para>
///         所有校验 fail closed：路径缺失、非绝对、组件不存在或平台不符都抛带上下文的异常，
///         由调用方终止运行，绝不静默回退宿主执行。网络缺省受限（外部阻断，命令可经逐次审批
///         临时在线）；可信设置可显式选择常设在线或独立账户的 TCP 环回模式（包括宿主环回服务），
///         二者互斥，不得组合。
///     </para>
///     <para>
///         Every validation fails closed: missing or non-absolute paths, absent components, or a wrong
///         platform throw contextual exceptions for the caller to stop the run; host execution is never
///         a silent fallback. Networking defaults to restricted (external blocked, per-command approval
///         may grant online execution); trusted settings may explicitly select standing online access
///         or TCP loopback with separate accounts, including host loopback services; the two are
///         mutually exclusive and must never be combined.
///     </para>
/// </remarks>
public static class WindowsSandboxComposer
{
    /// <summary>
    ///     <para>本地策略身份语义版本；读写范围、环境、网络或元数据保护的含义变化时递增，使旧授权身份失效。</para>
    ///     <para>
    ///         The local policy identity semantics version; bump it whenever the meaning of scopes,
    ///         environment, network, or metadata protection changes so old identities expire.
    ///     </para>
    /// </summary>
    internal const int PolicyIdentityVersion = 5;

    /// <summary>
    ///     <para>校验设置并组合执行要素。</para>
    ///     <para>Validates the settings and composes the execution pieces.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         workspaceRoot 必须是绝对路径；knownSecrets 用于目标环境的最后防线过滤；networkSettings
    ///         供沙箱代理环境解析，常设在线时代理环境并入冻结目标环境。会创建沙箱临时写根、
    ///         NuGet 缓存根与工具根目录（harness 自有基础设施，不属于目标命令副作用）。
    ///     </para>
    ///     <para>
    ///         workspaceRoot must be absolute; knownSecrets feed the target environment's last-line
    ///         filtering; networkSettings feed the sandbox proxy-environment resolution, and under a
    ///         standing-online policy the proxy environment merges into the frozen target environment.
    ///         The sandbox temp write root, NuGet cache root, and tools root directories are created here
    ///         (harness-owned infrastructure, not a target-command side effect).
    ///     </para>
    /// </remarks>
    public static WindowsSandboxExecution Compose(
        WindowsSandboxSettings             settings,
        string                             workspaceRoot,
        IReadOnlyDictionary<string, string>? knownSecrets    = null,
        NetworkSettings?                     networkSettings = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // TCP 环回模式使用独立账户对（TcpOff/TcpOn）与独立 home，其凭据文件 online 条目不具备外联能力；
        // 常设联网与之组合会呈现误导性网络身份。二者互斥，用户必须显式选择其一，组合即 fail closed。
        if (settings.NetworkPolicy == SandboxNetworkPolicy.Enabled && settings.AllowTcpLoopback)
            throw new
                InvalidOperationException("The standing-online network policy and TCP loopback mode are mutually exclusive: " +
                                          "tcp-loopback runs on a separate TcpOff/TcpOn account pair with a separate home whose " +
                                          "credentials file online entry carries no egress capability, so combining it with standing " +
                                          "online access would present a misleading network identity; explicitly choose exactly one.");

        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new
                InvalidOperationException($"The workspace root must be a fully qualified absolute path to enable the Windows sandbox: '{workspaceRoot}'.");

        if (!OperatingSystem.IsWindows())
            throw new
                PlatformNotSupportedException("The Windows sandbox is enabled in the trusted user settings but this platform is not Windows; " +
                                              "refusing to fall back to host execution. Disable settings.windowsSandbox.enabled to run host execution.");

        // 根与环境推导与 provisioning 入口（WindowsSandboxPolicyPlanner）共用，防止 refresh ACL 与 provisioning payload 漂移。
        var plan = WindowsSandboxPolicyPlanner.Plan(settings, workspaceRoot, knownSecrets);

        // 可信组件绝不允许被目标命令写入：拒绝位于工作区或任一额外写根（含派生根）内的组件。
        IReadOnlyList<string> writeRoots = [workspaceRoot, .. plan.AdditionalWriteRoots];
        RequireTrustedPathOutsideWriteRoots(plan.SetupExecutablePath, "setup executable", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.RunnerExecutablePath, "runner executable", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.SandboxHome, "sandbox home", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.TempRoot, "sandbox temp root", writeRoots);

        // 凭据目录与其中的 users 文件同样不得位于任何写根内（写根覆盖凭据目录会把 DPAPI 凭据
        // 暴露给沙箱账户组，写根落入凭据目录下则可篡改账户映射）：拒绝深度更大的写根与
        // 恰好等于凭据目录的写根；覆盖 home 的写根已由上面的 sandbox home 检查拒绝。
        var credentialsDirectory = Path.Combine(plan.SandboxHome, ".sandbox-secrets");
        RequireTrustedPathOutsideWriteRoots(credentialsDirectory, "sandbox credentials directory", writeRoots);
        RequireTrustedPathOutsideWriteRoots(Path.Combine(credentialsDirectory, "sandbox_users.json"),
                                            "sandbox credentials file", writeRoots);

        if (!File.Exists(plan.SetupExecutablePath))
            throw new
                InvalidOperationException($"The Windows sandbox setup executable was not found at '{plan.SetupExecutablePath}'; refusing to fall back to host execution.");

        if (!File.Exists(plan.RunnerExecutablePath))
            throw new
                InvalidOperationException($"The Windows sandbox runner executable was not found at '{plan.RunnerExecutablePath}'; refusing to fall back to host execution.");

        if (!Directory.Exists(plan.SandboxHome))
            throw new
                InvalidOperationException($"The Windows sandbox home was not found at '{plan.SandboxHome}'; run provisioning first. " +
                                          "Refusing to fall back to host execution.");

        Directory.CreateDirectory(plan.TempRoot);
        Directory.CreateDirectory(plan.NuGetCacheRoot);
        Directory.CreateDirectory(plan.ToolsRoot);

        var standingNetworkEnabled = settings.NetworkPolicy == SandboxNetworkPolicy.Enabled;
        var proxyEnvironment = ResolveProxyEnvironment(settings.Proxy, networkSettings);

        // 常设在线时把代理环境并入冻结目标环境（受限执行在该配置下不存在）；
        // 常设受限时保持目标环境无代理键，由后端按逐次在线授予注入。
        var targetEnvironment =
            standingNetworkEnabled && proxyEnvironment.Count > 0
                ? WithProxyEnvironment(plan.TargetEnvironment, proxyEnvironment)
                : plan.TargetEnvironment;

        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = plan.SetupExecutablePath,
            RunnerExecutablePath = plan.RunnerExecutablePath,
            AllowTcpLoopback     = settings.AllowTcpLoopback,
            SandboxHome          = plan.SandboxHome
        };

        var executionPolicy = new ProcessExecutionPolicy
        {
            Backend = ProcessExecutionBackendKind.WindowsSandbox,
            DisplayName = $"windows-sandbox: {PolicyKindDisplay(settings.Policy)}, " +
                          (settings.AllowTcpLoopback
                              ? "network: external-blocked, tcp-loopback: allowed (host services included), udp: blocked"
                              : standingNetworkEnabled
                                  ? "network: enabled (online account)"
                                  : "network: restricted (per-command approval may enable)"),
            PolicyIdentity = BuildPolicyIdentity(plan.Policy, targetEnvironment, proxyEnvironment)
        };

        var backend = new WindowsSandboxBackend(components, plan.Policy, targetEnvironment, knownSecrets,
                                                standingNetworkAccessEnabled : standingNetworkEnabled,
                                                proxyEnvironment : proxyEnvironment);
        SandboxDiagnosticLog.Event("compose",
                                   $"kind={PolicyKindDisplay(plan.Policy.Kind)} tcpLoopback={settings.AllowTcpLoopback} " +
                                   $"network={(standingNetworkEnabled ? "enabled" : "restricted")} " +
                                   $"proxyVariables={(standingNetworkEnabled ? proxyEnvironment.Count : 0)} " +
                                   $"workspace='{workspaceRoot}' writeRoots=[{string.Join(";", plan.Policy.EffectiveWriteRoots)}] " +
                                   $"tempRoot='{plan.TempRoot}' setup='{plan.SetupExecutablePath}' runner='{plan.RunnerExecutablePath}' " +
                                   $"home='{plan.SandboxHome}'");
        return new WindowsSandboxExecution(components, plan.Policy, targetEnvironment, executionPolicy, backend);
    }

    /// <summary>
    ///     <para>把解析出的代理环境并入冻结目标环境（常设在线专用），键大小写不敏感覆盖。</para>
    ///     <para>
    ///         Merges the resolved proxy environment into the frozen target environment
    ///         (standing-online only), overriding keys case-insensitively.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithProxyEnvironment(
        IReadOnlyDictionary<string, string> targetEnvironment, IReadOnlyDictionary<string, string> proxyEnvironment)
    {
        var environment = new Dictionary<string, string>(targetEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in proxyEnvironment) environment[name] = value;

        return environment;
    }

    /// <summary>
    ///     <para>
    ///         绑定后端、策略语义版本、组件协议版本、有效读写范围、deny、常设网络、临时根、元数据保护、
    ///         目标环境与代理环境哈希的规范化身份串。
    ///     </para>
    ///     <para>
    ///         The normalized identity binding the backend, policy semantics version, component protocol
    ///         versions, effective read/write scopes, deny paths, standing network, temp roots, metadata
    ///         protection, and the target and proxy environment hashes.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>同一配置稳定；任何一项变化都会使旧会话授权不再匹配。代理环境只以哈希进入身份串。</para>
    ///     <para>
    ///         Stable for identical configuration; any change makes old session grants stop matching.
    ///         The proxy environment enters the identity as a hash only.
    ///     </para>
    /// </remarks>
    private static string BuildPolicyIdentity(
        SandboxIsolationPolicy                 policy,
        IReadOnlyDictionary<string, string>    targetEnvironment,
        IReadOnlyDictionary<string, string>    proxyEnvironment)
    {
        var canonical = new StringBuilder();
        canonical.Append("backend=windows-sandbox\n");
        canonical.Append("policyVersion=").Append(PolicyIdentityVersion).Append('\n');
        canonical.Append("kind=").Append(PolicyKindDisplay(policy.Kind)).Append('\n');
        canonical.Append("setupVersion=").Append(policy.AllowTcpLoopback
                                                     ? WindowsSandboxComponents.TcpLoopbackSetupVersion
                                                     : WindowsSandboxComponents.SetupVersion).Append('\n');
        canonical.Append("ipcVersion=").Append(WindowsSandboxComponents.IpcVersion).Append('\n');
        canonical.Append("workspace=").Append(SandboxCapabilitySidStore.CanonicalRootKey(policy.WorkspaceRoot))
                 .Append('\n');
        AppendRoots(canonical, "writeRoots", policy.EffectiveWriteRoots);
        AppendRoots(canonical, "readRoots", policy.EffectiveReadRoots);
        AppendRoots(canonical, "denyWritePaths", policy.DenyWritePaths);
        AppendRoots(canonical, "tempRoots", policy.TempWriteRoots);
        canonical.Append("network=")
                 .Append(SandboxNetworkPolicyParser.ToProfileValue(policy.NetworkEnabled
                                                                       ? SandboxNetworkPolicy.Enabled
                                                                       : SandboxNetworkPolicy.Restricted))
                 .Append('\n');
        canonical.Append("tcpLoopback=").Append(policy.AllowTcpLoopback ? "allowed" : "blocked").Append('\n');
        if (proxyEnvironment.Count > 0)
            canonical.Append("proxyEnvironment=").Append(ComputeEnvironmentHash(proxyEnvironment)).Append('\n');
        canonical.Append("metadataProtection=")
                 .Append(string.Join("|", SandboxIsolationPolicy.ProtectedMetadataSubpaths))
                 .Append('\n');
        canonical.Append("metadataMissing=skip\n");
        canonical.Append("environment=").Append(ComputeEnvironmentHash(targetEnvironment));
        return canonical.ToString();
    }

    private static void AppendRoots(StringBuilder canonical, string name, IEnumerable<string> roots)
    {
        canonical.Append(name)
                 .Append('=')
                 .Append(string.Join("|", roots.Select(SandboxCapabilitySidStore.CanonicalRootKey)
                                               .OrderBy(key => key, StringComparer.Ordinal)))
                 .Append('\n');
    }

    /// <summary>
    ///     <para>目标环境的稳定哈希：键排序（大小写不敏感）后的 name=value 行整体 SHA-256。</para>
    ///     <para>
    ///         A stable hash of the target environment: SHA-256 over the name=value lines
    ///         sorted by case-insensitive key.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>策略身份由此绑定环境——附加环境变量或临时根变化都会使旧授权失效。</para>
    ///     <para>
    ///         This binds the environment policy identity — extra variables or a changed temp root
    ///         expire old grants.
    ///     </para>
    /// </remarks>
    private static string ComputeEnvironmentHash(IReadOnlyDictionary<string, string> targetEnvironment)
    {
        var lines = targetEnvironment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                                     .Select(pair => pair.Key + "=" + pair.Value);
        var canonical = string.Join("\n", lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>环回目标恒不走代理。Loopback destinations never go through the proxy.</summary>
    private const string NoProxyValue = "localhost,127.0.0.1,::1";

    /// <summary>
    ///     <para>
    ///         解析沙箱代理环境：http/https 代理产出 HTTP_PROXY+HTTPS_PROXY，socks5 产出 ALL_PROXY，
    ///         NO_PROXY 恒排除环回。任何日志与显示串都只允许 host:port，绝不携带完整代理 URL。
    ///     </para>
    ///     <para>
    ///         Resolves the sandbox proxy environment: http/https proxies produce
    ///         HTTP_PROXY+HTTPS_PROXY, socks5 produces ALL_PROXY, and NO_PROXY always
    ///         excludes loopback. Logs and display strings may carry host:port only,
    ///         never a full proxy URL.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         代理认证不进入子进程环境（无凭据存储输入，环境变量 URL 也不携带 userinfo）；
    ///         需认证代理在沙箱内不可用是已知限制。Named 找不到按名称的代理时 fail closed。
    ///     </para>
    ///     <para>
    ///         Proxy credentials never enter the child environment (no credential-store
    ///         input, and env-var URLs carry no userinfo); authenticated proxies being
    ///         unusable inside the sandbox is a known limitation. A Named lookup that
    ///         finds nothing fails closed.
    ///     </para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ResolveProxyEnvironment(
        SandboxProxyPolicy? policy, NetworkSettings? networkSettings)
    {
        var effective = policy ?? new SandboxProxyPolicy();
        return effective.Mode switch
        {
            SandboxProxyMode.Direct           => EmptyProxyEnvironment(),
            SandboxProxyMode.System           => BuildProxyEnvironment(ResolveSystemProxyDefinition()),
            SandboxProxyMode.Named            => BuildProxyEnvironment(RequireNamedProxy(effective.ProxyName, networkSettings)),
            SandboxProxyMode.FollowAppDefault => FollowApplicationDefault(networkSettings),
            _                                 => throw new
                ArgumentOutOfRangeException(nameof(policy), effective.Mode, "Unknown sandbox proxy mode.")
        };
    }

    /// <summary>
    ///     <para>
    ///         复用应用默认路由解析（NetworkRouteResolver）；解析不可用（无 NetworkSettings 或解析失败）
    ///         时返回空环境，绝不抛出。
    ///     </para>
    ///     <para>
    ///         Reuses the application's default-route resolution (NetworkRouteResolver);
    ///         when unavailable (no NetworkSettings or the resolution fails) it returns
    ///         an empty environment instead of throwing.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> FollowApplicationDefault(NetworkSettings? networkSettings)
    {
        if (networkSettings is null) return EmptyProxyEnvironment();

        try
        {
            var route = NetworkRouteResolver.Resolve(networkSettings, null);
            return route.Mode switch
            {
                NetworkMode.System => BuildProxyEnvironment(ResolveSystemProxyDefinition()),
                NetworkMode.Direct => EmptyProxyEnvironment(),
                NetworkMode.Proxy  => BuildProxyEnvironment(route.Proxy),
                _                  => throw new ArgumentOutOfRangeException(nameof(route), route.Mode, "Unknown network mode.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 应用默认路由不可解析（如配置校验失败）时按无代理处理，并留下无地址的诊断线索。
            SandboxDiagnosticLog.Event("compose", $"proxy=follow-app-default-unavailable type={ex.GetType().Name}");
            return EmptyProxyEnvironment();
        }
    }

    /// <summary>
    ///     <para>
    ///         以 HttpClient.DefaultProxy 解析系统代理（与 ProviderTransportFactory 的 System 模式同一
    ///         出口语义）；无系统代理或方案不受支持时返回 null。
    ///     </para>
    ///     <para>
    ///         Resolves the system proxy via HttpClient.DefaultProxy (the same egress
    ///         semantics as ProviderTransportFactory's System mode); returns null when
    ///         no system proxy exists or its scheme is unsupported.
    ///     </para>
    /// </summary>
    private static ProxyDefinition? ResolveSystemProxyDefinition()
    {
        // 探测目标不发起网络请求：GetProxy 只读取本机代理配置。
        var destination = new Uri("https://tinyharness.sandbox-probe.invalid/");
        Uri? resolved;
        try
        {
            resolved = HttpClient.DefaultProxy.GetProxy(destination);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            SandboxDiagnosticLog.Event("compose", $"proxy=system-unavailable type={ex.GetType().Name}");
            return null;
        }

        if (resolved is null || resolved.Equals(destination)) return null;

        var protocol = resolved.Scheme switch
        {
            "http"  => ProxyProtocol.Http,
            "https" => ProxyProtocol.Https,
            _       => (ProxyProtocol?)null
        };
        if (protocol is not { } selectedProtocol) return null;

        return new ProxyDefinition
        {
            Protocol = selectedProtocol,
            Host     = resolved.IdnHost,
            Port     = resolved.IsDefaultPort ? (selectedProtocol == ProxyProtocol.Https ? 443 : 80) : resolved.Port
        };
    }

    /// <summary>
    ///     <para>按名称在已有代理列表中查找；名称为空或找不到都抛 InvalidOperationException（fail closed）。</para>
    ///     <para>
    ///         Finds a proxy by name in the saved proxy list; an empty name or a
    ///         missing entry throws InvalidOperationException (fail closed).
    ///     </para>
    /// </summary>
    private static ProxyDefinition RequireNamedProxy(string? proxyName, NetworkSettings? networkSettings)
    {
        if (string.IsNullOrWhiteSpace(proxyName))
            throw new InvalidOperationException("The sandbox proxy policy requires a proxy name for the named mode.");

        var candidates = (networkSettings?.Proxies ?? [])
                         .Where(candidate => string.Equals(candidate.Name, proxyName, StringComparison.OrdinalIgnoreCase))
                         .ToList();
        if (candidates.Count == 0)
            throw new
                InvalidOperationException($"The sandbox proxy policy names proxy '{proxyName}', which does not exist in the network settings; refusing to run the sandbox without a proxy.");
        if (candidates.Count > 1)
            throw new
                InvalidOperationException($"The sandbox proxy policy names proxy '{proxyName}', which matches more than one saved proxy; refusing an ambiguous selection.");

        return candidates[0];
    }

    /// <summary>
    ///     <para>把代理定义展开为子进程代理环境；无代理定义时返回空（受限执行环境不得出现代理键）。</para>
    ///     <para>
    ///         Expands a proxy definition into the child proxy environment; without a
    ///         definition it returns empty (a restricted environment never carries
    ///         proxy keys).
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildProxyEnvironment(ProxyDefinition? proxy)
    {
        if (proxy is null) return EmptyProxyEnvironment();

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (proxy.Protocol == ProxyProtocol.Socks5)
        {
            // .NET 系工具不读取 ALL_PROXY，socks5 代理只对读取该变量的工具生效；这是已知限制。
            environment["ALL_PROXY"] = $"socks5://{proxy.Host}:{proxy.Port}";
        }
        else
        {
            var scheme   = proxy.Protocol == ProxyProtocol.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var proxyUrl = $"{scheme}://{proxy.Host}:{proxy.Port}";
            environment["HTTP_PROXY"]  = proxyUrl;
            environment["HTTPS_PROXY"] = proxyUrl;
        }

        environment["NO_PROXY"] = NoProxyValue;
        return environment;
    }

    private static IReadOnlyDictionary<string, string> EmptyProxyEnvironment() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     <para>
    ///         可信路径落在任一可写根（工作区或额外写根）内时拒绝执行；沙箱内命令可能篡改该组件，
    ///         绝不回退宿主执行。
    ///     </para>
    ///     <para>
    ///         Rejects a trusted path that lies inside any writable root (the workspace or an additional
    ///         write root): sandboxed commands could tamper with it, so host execution is never a fallback.
    ///     </para>
    /// </summary>
    private static void RequireTrustedPathOutsideWriteRoots(
        string trustedPath, string label, IReadOnlyList<string> writeRoots)
    {
        foreach (var root in writeRoots)
        {
            if (!Workspace.IsInside(root, trustedPath)) continue;

            throw new
                InvalidOperationException($"The {label} '{trustedPath}' lies inside the writable root '{root}'; " +
                                          "sandboxed commands could tamper with it, refusing to fall back to host execution.");
        }
    }

    private static string PolicyKindDisplay(SandboxPolicyKind kind) => kind switch
    {
        SandboxPolicyKind.ReadOnly       => "read-only",
        SandboxPolicyKind.WorkspaceWrite => "workspace-write",
        _                                => kind.ToString()
    };
}
