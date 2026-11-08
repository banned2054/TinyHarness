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
    /// <remarks>
    ///     <para>v6 引入 grant 段：活动授权根进入身份串，旧（v5）身份随之失效，属预期语义成本。</para>
    ///     <para>
    ///         v6 introduces the grant section: active grant roots enter the identity, which by
    ///         design expires all v5 identities.
    ///     </para>
    ///     <para>
    ///         v7 引入基线广泛读根与 deny-read 保护集（GAP-1 OS 读边界）：两集进入身份串，
    ///         旧（v6）身份随之失效，属预期语义成本。
    ///     </para>
    ///     <para>
    ///         v7 introduces the baseline broad-read root and the deny-read protection set
    ///         (the GAP-1 OS read boundary): both sets enter the identity, which by design
    ///         expires all v6 identities.
    ///     </para>
    ///     <para>
    ///         v8 引入授权读段（authorizedReadPaths）：C2 获批读授权通道使授权执行与普通执行、
    ///         以及不同授权集之间身份必不相同，旧（v7）身份随之失效，属预期语义成本。
    ///     </para>
    ///     <para>
    ///         v8 introduces the authorized-read section (authorizedReadPaths): the C2 approved-read
    ///         authorization channel makes authorized executions always differ from normal ones and
    ///         from each other, which by design expires all v7 identities.
    ///     </para>
    /// </remarks>
    internal const int PolicyIdentityVersion = 8;

    /// <summary>环回目标恒不走代理。Loopback destinations never go through the proxy.</summary>
    private const string NoProxyValue = "localhost,127.0.0.1,::1";

    /// <summary>
    ///     <para>
    ///         基线敏感名扫描的默认枚举预算（文件系统条目数）；耗尽即抛出，由组合点 fail-closed。
    ///     </para>
    ///     <para>
    ///         The default enumeration budget (filesystem entries) for the baseline sensitive-name
    ///         scan; exhaustion throws, and the composition site fails closed.
    ///     </para>
    /// </summary>
    internal const int BaselineSensitiveScanBudget = 500_000;

    /// <summary>
    ///     <para>校验设置并组合执行要素。</para>
    ///     <para>Validates the settings and composes the execution pieces.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         workspaceRoot 必须是绝对路径；knownSecrets 用于目标环境的最后防线过滤；networkSettings
    ///         供沙箱代理环境解析，常设在线时代理环境并入冻结目标环境；baselineReadProtection 携带
    ///         基线广泛读根与 deny-read 保护集（结构校验 fail-closed，deny 条目不允许盘根或读根
    ///         本身，且必须落在某读根覆盖内）。会创建沙箱临时写根、NuGet 缓存根与工具根目录
    ///         （harness 自有基础设施，不属于目标命令副作用）。
    ///     </para>
    ///     <para>
    ///         workspaceRoot must be absolute; knownSecrets feed the target environment's last-line
    ///         filtering; networkSettings feed the sandbox proxy-environment resolution, and under a
    ///         standing-online policy the proxy environment merges into the frozen target environment;
    ///         baselineReadProtection carries the baseline broad-read root and the deny-read
    ///         protection set (structural validation fails closed: deny entries may be neither
    ///         filesystem roots nor a read root itself, and must sit inside a read root). The sandbox
    ///         temp write root, NuGet cache root, and tools root directories are created here
    ///         (harness-owned infrastructure, not a target-command side effect).
    ///     </para>
    /// </remarks>
    /// <param name="baselineReadProtection">
    ///     <para>
    ///         基线广泛读保护计划（见 <see cref="PlanBaselineReadProtection" />）；缺省 null 表示
    ///         不引入 OS 广泛读基线，沙箱读边界维持工作区+可信根。
    ///     </para>
    ///     <para>
    ///         The baseline broad-read protection plan (see
    ///         <see cref="PlanBaselineReadProtection" />); null by default keeps the sandbox read
    ///         boundary at the workspace plus trusted roots without an OS broad-read baseline.
    ///     </para>
    /// </param>
    /// <param name="permissionSystemPaths">
    ///     <para>
    ///         权限系统 C1 实例路径（用户配置文件、共享 workspace-trust.json 与受保护会话存储目录），
    ///         由组合调用方以实际路径传入；任一写根与之双向重叠（覆盖或落入）都拒绝组合。底层绝不
    ///         重新猜测默认路径；缺省 null 表示调用方无此类实例（如离线验收）。
    ///     </para>
    ///     <para>
    ///         The permission-system C1 instance paths (the user config file, the shared
    ///         workspace-trust.json, and the protected session-store directory) passed by the composition
    ///         caller as the actual paths; any write root overlapping one of them bidirectionally (covering
    ///         it or falling inside it) refuses the composition. The composer never re-guesses default
    ///         paths; null by default means the caller has no such instances (offline acceptance).
    ///     </para>
    /// </param>
    public static WindowsSandboxExecution Compose(
        WindowsSandboxSettings               settings,
        string                               workspaceRoot,
        IReadOnlyDictionary<string, string>? knownSecrets           = null,
        NetworkSettings?                     networkSettings        = null,
        SandboxBaselineReadProtection?       baselineReadProtection = null,
        IReadOnlyList<string>?               permissionSystemPaths  = null)
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

        // 基线广泛读保护结构校验（fail-closed）：C1/C2 与读根的配对由规划方法保证并就地抛错，
        // 这里兜底校验 deny 集自身的形态——盘根与读根本身绝不可 deny，脱离读根覆盖的 deny 无意义。
        if (baselineReadProtection is { } readProtection)
        {
            foreach (var readRoot in readProtection.ReadRoots)
                RequireValidBaselineRoot(readRoot, "baseline sandbox read root");
            foreach (var denyPath in readProtection.DenyReadPaths)
            {
                RequireValidBaselineRoot(denyPath, "baseline sandbox deny-read path");
                var denyFull = Path.GetFullPath(denyPath);
                if (readProtection.ReadRoots.Select(Path.GetFullPath)
                                  .Any(readRoot => string.Equals(readRoot, denyFull,
                                                                 StringComparison.OrdinalIgnoreCase)))
                    throw new
                        InvalidOperationException($"A baseline sandbox deny-read path must not be a baseline read root itself: '{denyPath}'; " +
                                                  "denying the read root would deny every baseline read.");
                if (!readProtection.ReadRoots.Any(readRoot => Workspace.IsInside(readRoot, denyFull)))
                    throw new
                        InvalidOperationException($"A baseline sandbox deny-read path '{denyPath}' is not covered by any baseline read root; " +
                                                  "a deny entry outside the broad-read scope cannot pair with it, refusing the baseline.");
            }
        }

        // 根与环境推导与 provisioning 入口（WindowsSandboxPolicyPlanner）共用，防止 refresh ACL 与 provisioning payload 漂移。
        var plan = WindowsSandboxPolicyPlanner.Plan(settings, workspaceRoot, knownSecrets, baselineReadProtection);

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

        // C1 权限系统资源（用户配置、共享信任文件与会话存储目录实例）与任何写根双向重叠都拒绝：
        // 覆盖会让沙箱命令改写权限系统状态，落入则可在保护目录内部创建或改写条目。词法与最终
        // 形态都参与比对，绝不静默回退宿主执行。
        if (permissionSystemPaths is not null)
            foreach (var root in writeRoots)
            foreach (var protectedPath in permissionSystemPaths)
                if (OverlapsProtection(root, protectedPath))
                    throw new
                        InvalidOperationException($"The write root '{root}' overlaps the permission-system protected path '{protectedPath}'; " +
                                                  "permission-system resources cannot be exposed as sandbox write roots (covering " +
                                                  "or falling inside both count), refusing to fall back to host execution.");

        // 用户显式配置的运行时根（temp 根与 NuGet 缓存根）与完整性集合或 C1 集重叠即组合失败：
        // 运行时根授予沙箱真实写入，不得覆盖沙箱自身完整性资产或权限系统保护资源。运行时根
        // （含默认派生根与 home 内的显式覆盖）位于 home 内是预期布局，因此完整性键是 home 的
        // 敏感子路径（见 WindowsSandboxComponents.ProtectedHomeSubpaths）与组件、凭据，而非整个
        // home；覆盖整个 home 的根经子路径命中同样被拒。
        foreach (var configuredRoot in WindowsSandboxPolicyPlanner.ConfiguredInfrastructureRoots(settings))
        {
            foreach (var integrityPath in new[]
                     {
                         credentialsDirectory, plan.SetupExecutablePath, plan.RunnerExecutablePath,
                         Path.Combine(plan.SandboxHome, ".sandbox"),
                         Path.Combine(plan.SandboxHome, "cap_sid"),
                         Path.Combine(plan.SandboxHome, ".sandbox-bin")
                     })
                if (OverlapsProtection(configuredRoot, integrityPath))
                    throw new
                        InvalidOperationException($"The configured sandbox runtime root '{configuredRoot}' overlaps the sandbox integrity path '{integrityPath}'; " +
                                                  "runtime roots grant real writes and must never cover the sandbox's own " +
                                                  "integrity assets, refusing the composition.");

            if (permissionSystemPaths is not null)
                foreach (var protectedPath in permissionSystemPaths)
                    if (OverlapsProtection(configuredRoot, protectedPath))
                        throw new
                            InvalidOperationException($"The configured sandbox runtime root '{configuredRoot}' overlaps the permission-system protected path '{protectedPath}'; " +
                                                      "runtime roots grant real writes and must never cover permission-system " +
                                                      "resources, refusing the composition.");
        }

        // 基线 deny-read 集不得覆盖沙箱运行时路径（home/组件/凭据目录）：例如把沙箱 home 配置在
        // 被 deny 的目录（如上游 .codex）之下时，helper 的 deny 会切断沙箱读取自身运行时状态与
        // 组件，必须 fail-closed 指向配置错误。Workspace.IsInside 含相等语义，等于运行时路径的
        // deny 条目同样被拒。
        if (baselineReadProtection is { } plannedReadProtection)
            foreach (var denyPath in plannedReadProtection.DenyReadPaths)
            foreach (var runtimePath in new[]
                     {
                         plan.SandboxHome, credentialsDirectory, plan.SetupExecutablePath,
                         plan.RunnerExecutablePath
                     })
                if (Workspace.IsInside(denyPath, runtimePath))
                    throw new
                        InvalidOperationException($"The baseline deny-read path '{denyPath}' covers the sandbox runtime path '{runtimePath}'; " +
                                                  "the sandbox must read its own runtime state and components, so keep " +
                                                  "settings.windowsSandbox.sandboxHome and the component executables outside " +
                                                  "every denied directory.");

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
        var proxyEnvironment       = ResolveProxyEnvironment(settings.Proxy, networkSettings);

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

        // C1 可信名单随组合成功写入 helper 可读位置：授权豁免的 helper 侧"绝不触碰 C1"第二道防线；
        // 写入失败 fail closed（见 SandboxProtectedPathsStore）。
        SandboxProtectedPathsStore.WriteIfChanged(components,
                                                  SandboxProtectedPathsStore.ComposeManifest(components,
                                                      permissionSystemPaths));

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

        // 授权读协调器包裹执行后端：普通执行透传零变化；携带获批 C2 读路径的执行进入三道门与
        // 授权窗口（见 AuthorizedReadCoordinator）。CLI 与 Desktop 共用本组合点，无需分别接线。
        var backend = new AuthorizedReadCoordinator(
                                                    new WindowsSandboxBackend(components, plan.Policy,
                                                                              targetEnvironment, knownSecrets,
                                                                              standingNetworkAccessEnabled :
                                                                              standingNetworkEnabled,
                                                                              proxyEnvironment : proxyEnvironment),
                                                    components, plan.Policy);
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
    ///     <para>
    ///         派生沙箱基础设施写根（NuGet 缓存根、工具根与临时写根），供组合点注入资源访问策略，
    ///         使应用层分类与沙箱 OS 侧基线可写范围对齐。
    ///     </para>
    ///     <para>
    ///         Derives the sandbox infrastructure write roots (the NuGet cache root, the tools root,
    ///         and the temp write root) for composition sites to inject into the resource access
    ///         policy, aligning the application-layer classification with the sandbox's OS-writable
    ///         baseline.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         推导与 <see cref="Compose" /> 共用同一份实现（纯函数、不创建目录，配置非法时抛出
    ///         与组合一致的异常）；settings 为 null 时返回空列表。是否注入由组合点按沙箱
    ///         是否启用决定。
    ///     </para>
    ///     <para>
    ///         The derivation shares one implementation with <see cref="Compose" /> (pure, creating
    ///         no directories, and throwing the same composition-time exceptions for invalid
    ///         configuration); null settings yield an empty list. Injection is decided by the
    ///         composition sites based on sandbox enablement.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> DeriveRuntimeWriteRoots(WindowsSandboxSettings? settings)
    {
        if (settings is null) return [];

        var sandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback);
        var (nugetCacheRoot, toolsRoot, tempRoot) =
            WindowsSandboxPolicyPlanner.DeriveInfrastructureRoots(settings, sandboxHome);
        return [nugetCacheRoot, toolsRoot, tempRoot];
    }

    /// <summary>
    ///     <para>
    ///         规划基线广泛读保护（GAP-1 冻结参数）：读根=用户 profile 根；deny 集=profile 覆盖
    ///         范围内的 C1 宿主密钥路径（必须已存在，缺失即 fail-closed，绝不把文件物化成目录）、
    ///         沙箱 home 的 cap_sid 授权映射表、分类器目录段在 profile 下的直接子目录与上游
    ///         Codex CLI home（缺失由 helper 物化为目录，防同轮 create-then-read）。profile 根
    ///         不可用时返回 null，调用方如实降级为无广泛读基线。
    ///     </para>
    ///     <para>
    ///         Plans the baseline broad-read protection (the GAP-1 frozen parameters): the read
    ///         root is the user profile root; the deny set covers C1 host secret paths inside the
    ///         profile (which must already exist — a missing path fails closed and is never
    ///         materialized as a directory), the sandbox home's cap_sid authorization table, the
    ///         classifier's directory segments as direct children of the profile, and the upstream
    ///         Codex CLI home (missing ones are materialized as directories by the helper,
    ///         preventing create-then-read within one run). Returns null when the profile root is
    ///         unavailable so callers degrade honestly to a baseline without broad read.
    ///     </para>
    /// </summary>
    /// <param name="protectedSecretPaths">
    ///     <para>
    ///         两端各自的 C1 宿主密钥路径（如 trust.json、用户配置文件、会话存储）；沙箱 home、
    ///         组件与凭据目录不在此列——凭据目录已由 provisioning 对沙箱组永久 DENY，组件需被
    ///         沙箱读取执行，经 refresh deny 簿记它们反而引入回收风险。上游 Codex CLI home
    ///         （profile\.codex）由本方法固定加入：auth.json/cap_sid 等授权关键内容属 C1，
    ///         沙箱链路对它无读/执行依赖。
    ///     </para>
    ///     <para>
    ///         Each end's C1 host secret paths (such as trust.json, the user config file, and the
    ///         session store); the sandbox home, components, and the credentials directory stay out —
    ///         the credentials directory is already permanently denied to the sandbox group by
    ///         provisioning, the components must stay readable for execution, and routing them
    ///         through refresh deny bookkeeping would add revocation risk. The upstream Codex CLI
    ///         home (profile\.codex) is always added by this method: its auth.json/cap_sid and
    ///         archive content is C1, with no read or execute dependency from the sandbox chain.
    ///     </para>
    /// </param>
    /// <param name="ensureDirectory">
    ///     <para>
    ///         需要保证存在以便配对 deny 的受保护目录（如会话存储目录）；仅当其落在读根覆盖内时
    ///         才创建（持久化开启时本就会创建）。
    ///     </para>
    ///     <para>
    ///         A protected directory to ensure exists so its deny entry can pair (such as the
    ///         session-store directory); created only when the read root covers it (persistence
    ///         would create it anyway).
    ///     </para>
    /// </param>
    /// <param name="sandboxSettings">
    ///     <para>
    ///         可信沙箱设置：用于按 <see cref="WindowsSandboxComponents.ResolveSandboxHome" />
    ///         解析沙箱 home，并把 <c>&lt;home&gt;\cap_sid</c>（与
    ///         <see cref="SandboxCapabilitySidStore" /> 同一解析来源）加入 deny 集；解析失败时
    ///         跳过该条目（组合期会以同一配置失败，fail-closed 方向不变）。
    ///     </para>
    ///     <para>
    ///         The trusted sandbox settings: used to resolve the sandbox home via
    ///         <see cref="WindowsSandboxComponents.ResolveSandboxHome" /> and add
    ///         <c>&lt;home&gt;\cap_sid</c> (the same resolution source as
    ///         <see cref="SandboxCapabilitySidStore" />) to the deny set; a resolution failure
    ///         skips the entry (composition fails on the same configuration, keeping the
    ///         fail-closed direction).
    ///     </para>
    /// </param>
    /// <param name="sensitiveScan">
    ///     <para>
    ///         敏感名扫描函数（输入=读根，输出=敏感名命中路径）；缺省 null 使用真实的有界 profile
    ///         枚举。指纹取值器显式传空扫描：扫描命中是组合期文件系统快照而非配置成分，逐次重扫
    ///         既带来每次发送的枚举开销，也会因会话中途的文件增删制造指纹失配误撤销授权；组合期
    ///         快照的漂移由策略身份（含 denyReadPaths 段）自然表达。
    ///     </para>
    ///     <para>
    ///         The sensitive-name scan function (input = the read root, output = sensitive-name hit
    ///         paths); the default null uses the real bounded profile enumeration. The fingerprint
    ///         evaluator passes an empty scan explicitly: scan hits are a composition-time
    ///         filesystem snapshot, not a configuration input — rescanning on every evaluation adds
    ///         per-send enumeration cost and would revoke grants on mid-session file churn through
    ///         fingerprint mismatch; snapshot drift is already expressed naturally by the policy
    ///         identity (which carries the denyReadPaths section).
    ///     </para>
    /// </param>
    public static SandboxBaselineReadProtection? PlanBaselineReadProtection(
        IReadOnlyList<string>                protectedSecretPaths, string? ensureDirectory = null,
        WindowsSandboxSettings?              sandboxSettings = null,
        Func<string, IReadOnlyList<string>>? sensitiveScan   = null)
    {
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathFullyQualified(profileRoot)) return null;

        return PlanBaselineReadProtectionCore(Path.GetFullPath(profileRoot), protectedSecretPaths, ensureDirectory,
                                              sandboxSettings, sensitiveScan);
    }

    /// <summary>
    ///     <para>
    ///         基线读保护计划的显式读根形态（internal）：组合点以用户 profile 调用，测试以合成目录
    ///         调用。计划内容与覆盖不变量以该根为准。
    ///     </para>
    ///     <para>
    ///         The explicit-read-root form of the baseline read-protection plan (internal):
    ///         composition calls it with the user profile, tests with synthetic directories. The
    ///         plan's content and coverage invariants are relative to this root.
    ///     </para>
    /// </summary>
    internal static SandboxBaselineReadProtection PlanBaselineReadProtectionCore(
        string                               profileRoot,
        IReadOnlyList<string>                protectedSecretPaths, string? ensureDirectory,
        WindowsSandboxSettings?              sandboxSettings,
        Func<string, IReadOnlyList<string>>? sensitiveScan)
    {
        var          root      = Path.GetFullPath(profileRoot);
        List<string> denyPaths = [];

        // 沙箱 home 解析一次并复用：cap_sid 条目与扫描的 home 排除共用同一结果；解析失败时
        // 两者都跳过（组合期对同一配置失败，fail-closed 方向不变）。
        var sandboxHomeInsideProfile = TryResolveSandboxHomeInsideProfile(sandboxSettings, root);

        // 沙箱 home 的 cap_sid 授权映射表（C1 授权/可信配置）：home 经 ResolveSandboxHome 解析，
        // 与 SandboxCapabilitySidStore 的 <home>\cap_sid 契约同一来源。文件级 deny 无执行风险
        // （deny-read 只含读位）；cap_sid 不在 provisioning 锁内（锁仅覆盖 .sandbox、
        // .sandbox-secrets 与 .sandbox-bin）；deny 集每组合恒定含此条目，不存在收缩回收。
        // 沙箱子进程经 SpawnRequest 帧接收 SID，无需读取该表（pinned 复核：仅依赖 .sandbox/
        // .sandbox-bin/组件路径）。仅当 home 落在读根覆盖内才加入（profile 外的自定义 home
        // 无 OS 暴露，加入反而违反覆盖校验）。
        if (sandboxHomeInsideProfile is { } homeForCapSid)
            denyPaths.Add(Path.Combine(homeForCapSid, "cap_sid"));

        // 会话存储等受保护目录：覆盖范围内先创建再配对，使 deny 自目录诞生即生效并被继承。
        if (!string.IsNullOrWhiteSpace(ensureDirectory))
        {
            var ensuredDirectory = Path.GetFullPath(ensureDirectory);
            if (Workspace.IsInside(root, ensuredDirectory))
            {
                Directory.CreateDirectory(ensuredDirectory);
                denyPaths.Add(ensuredDirectory);
            }
        }

        foreach (var path in protectedSecretPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var full = Path.GetFullPath(path);
            if (!Workspace.IsInside(root, full)) continue;

            // 缺失的 C1 文件不可安全 deny（helper 会把缺失路径物化为目录，破坏以文件形态写入的
            // 受保护路径），覆盖范围内缺失即 fail-closed。
            if (!File.Exists(full) && !Directory.Exists(full))
                throw new
                    InvalidOperationException($"Baseline broad-read pairing failed: the protected path '{full}' is covered by the profile read root " +
                                              "but does not exist, so it cannot be denied safely; refusing the sandbox baseline (fail closed). " +
                                              "Restore or remove the path before enabling the sandbox.");
            denyPaths.Add(full);
        }

        foreach (var name in SensitivePathClassifier.KnownSensitiveDirectoryNames)
        {
            // 凭据目录（.sandbox-secrets）由 provisioning 对沙箱组永久 DENY；在 profile 下
            // 物化同名空目录只是噪音，不进 deny 集。
            if (name.Equals(".sandbox-secrets", StringComparison.OrdinalIgnoreCase)) continue;

            denyPaths.Add(Path.Combine(root, name));
        }

        // 上游 Codex CLI home（profile\.codex）：auth.json、cap_sid、归档与备份等授权关键内容
        // 属 C1。TinyHarness 沙箱链路只使用独立命名空间 home（payload codex_home=组合的
        // SandboxHome），对该目录无任何读/执行依赖，整目录 deny 最干净；缺失时由 helper
        // 物化为空目录，防止沙箱内先创建后读取。
        denyPaths.Add(Path.Combine(root, ".codex"));

        // C2 OS 层补强（有界敏感名扫描）：基线 deny 集此前只覆盖 profile 顶层敏感目录段，
        // 嵌套敏感目录与文件名/扩展名级敏感命中（.env、id_rsa、.pem 等）没有 OS 读拒，
        // Shell 可绕过文件工具的应用层 C2 分类直读。有界枚举读根，把已存在的命中并入 deny 集：
        // 沙箱 home 子树跳过（deny 不碰沙箱运行时）；已有 deny 祖先覆盖的命中跳过（继承已覆盖）；
        // 扫描后消失的命中跳过（绝不物化成目录）；重解析点只做词法判定、不下降。扫描是组合期
        // 快照——会话中途新建的敏感路径要等下次组合进入 OS deny（文件工具应用层分类不受影响），
        // 边界如实声明而非宣称完整。预算耗尽即组合失败（fail-closed），绝不静默证明一个没扫完的边界。
        // C2 OS-layer reinforcement (a bounded sensitive-name scan): the baseline deny set only
        // covered top-level sensitive directory segments under the profile, leaving nested
        // sensitive directories and file-name/extension-level hits (.env, id_rsa, .pem, ...)
        // without an OS read deny, so the shell could bypass the file tools' application-layer
        // C2 classification and read them directly. A bounded enumeration of the read root folds
        // existing hits into the deny set: the sandbox home subtree is skipped (the deny set
        // never touches sandbox runtime), hits already covered by a deny ancestor are skipped
        // (inheritance covers them), hits that vanished after the scan are skipped (never
        // materialized as directories), and reparse points are judged lexically without
        // descending. The scan is a composition-time snapshot — sensitive paths created
        // mid-session reach the OS deny at the next composition while the file tools'
        // application-layer classification keeps protecting immediately; the boundary is stated
        // honestly rather than claimed complete. Budget exhaustion fails the composition
        // (fail-closed), never silently certifying a partially scanned boundary.
        var scanHits = sensitiveScan?.Invoke(root) ?? EnumerateBaselineSensitiveNameHits(root);
        foreach (var hit in scanHits)
        {
            if (string.IsNullOrWhiteSpace(hit)) continue;

            var full = Path.GetFullPath(hit);
            if (!Workspace.IsInside(root, full)) continue;

            // 沙箱 home 子树：deny 不进入沙箱运行时面（与 cap_sid 显式条目互补，宁可少收窄）。
            if (sandboxHomeInsideProfile is { } homeRoot && Workspace.IsInside(homeRoot, full)) continue;

            // 扫描与配对之间消失的命中跳过：helper 会把缺失 deny 路径物化为目录，绝不制造。
            if (!File.Exists(full) && !Directory.Exists(full)) continue;

            // 已被 deny 祖先覆盖的命中跳过：继承已提供 OS 保护，条目只会膨胀 deny 集。
            if (denyPaths.Any(entry => Workspace.IsInside(entry, full))) continue;

            denyPaths.Add(full);
        }

        return new SandboxBaselineReadProtection([root], SandboxIsolationPolicy.Deduplicate(denyPaths));
    }

    /// <summary>
    ///     <para>
    ///         解析沙箱 home 并判断其是否落在读根覆盖内；解析失败返回 null（组合期对同一配置
    ///         失败），profile 外的自定义 home 同样返回 null（无 OS 暴露，无需排除）。
    ///     </para>
    ///     <para>
    ///         Resolves the sandbox home and decides whether it sits under the read-root coverage;
    ///         a resolution failure returns null (composition fails on the same configuration) and
    ///         a custom home outside the profile also returns null (no OS exposure, nothing to
    ///         exclude).
    ///     </para>
    /// </summary>
    private static string? TryResolveSandboxHomeInsideProfile(WindowsSandboxSettings? sandboxSettings,
                                                              string                  profileRoot)
    {
        if (sandboxSettings is null) return null;

        try
        {
            var sandboxHome = WindowsSandboxComponents.ResolveSandboxHome(sandboxSettings.SandboxHome,
                                                                          sandboxSettings.AllowTcpLoopback);
            return Workspace.IsInside(profileRoot, sandboxHome) ? Path.GetFullPath(sandboxHome) : null;
        }
        catch (InvalidOperationException)
        {
            // 显式 sandboxHome 非绝对等配置错误：此处跳过，组合器对同一配置会抛出带上下文的失败。
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         有界枚举一个根下的敏感名命中（目录段含末段、文件名与扩展名，大小写不敏感）。
    ///         重解析点只做词法判定、绝不下降；不可读子树按 Windows 语义跳过；目录命中不再下降
    ///         （其子树已随 deny 覆盖，节约预算）。预算按枚举条目计，耗尽即抛出——调用方
    ///         fail-closed，绝不返回未扫完的部分结果。
    ///     </para>
    ///     <para>
    ///         Boundedly enumerates sensitive-name hits under one root (directory segments
    ///         including the final one, file names, and extensions, case-insensitive). Reparse
    ///         points are judged lexically and never descended; unreadable subtrees are skipped
    ///         with the Windows semantics; hit directories are not descended (their subtrees are
    ///         already covered by the deny, saving budget). The budget counts enumerated entries
    ///         and exhaustion throws — the caller fails closed, never returning a partially
    ///         scanned result.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> EnumerateBaselineSensitiveNameHits(string root, int? budget = null)
    {
        var remaining = budget ?? BaselineSensitiveScanBudget;
        var hits      = new List<string>();
        var stack     = new Stack<string>();
        stack.Push(Path.GetFullPath(root));

        while (stack.Count > 0)
        {
            IEnumerable<string> children;
            var                 directory = stack.Pop();
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 不可读子树（权限或瞬时 IO）：跳过并在命中口径上如实“不声称完整”。
                continue;
            }

            foreach (var child in children)
            {
                if (--remaining < 0)
                    throw new
                        InvalidOperationException($"The baseline sensitive-name scan exceeded its enumeration budget of {budget ?? BaselineSensitiveScanBudget} entries under '{root}'; " +
                                                  "the sensitive-name deny set cannot be certified on a partially scanned profile, so the baseline fails closed. " +
                                                  "Reduce the profile's entry count under the read root or exclude the sandbox to proceed.");

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(child);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException
                                              or DirectoryNotFoundException)
                {
                    // 属性不可读：按词法判定（fail-safe 方向），不下降。
                    if (SensitivePathClassifier.IsKnownSensitivePath(child)) hits.Add(child);

                    continue;
                }

                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var isReparse   = attributes.HasFlag(FileAttributes.ReparsePoint);

                // 词法敏感判定（目录段含末段、文件名与扩展名）；命中不再下降
                // （命中目录的子树已随 deny 覆盖），普通目录继续下降。
                if (SensitivePathClassifier.IsKnownSensitivePath(child))
                {
                    hits.Add(child);
                    continue;
                }

                if (isDirectory && !isReparse) stack.Push(child);
            }
        }

        return hits;
    }

    /// <summary>
    ///     <para>校验基线根路径形态：绝对路径且不是文件系统根（helper 对盘根 deny 显式拒绝）。</para>
    ///     <para>
    ///         Validates a baseline root's shape: an absolute path that is not a filesystem root
    ///         (the helper explicitly refuses root denies).
    ///     </para>
    /// </summary>
    private static void RequireValidBaselineRoot(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new
                InvalidOperationException($"A {label} must be a fully qualified absolute path: '{path}'.");

        var full = Path.GetFullPath(path);
        if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
            throw new
                InvalidOperationException($"A {label} must not be a filesystem root: '{full}'; disk-level read or deny roots are not supported.");
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
    ///         绑定后端、策略语义版本、组件协议版本、有效读写范围、基线广泛读根、deny-read 保护集、
    ///         deny 写保护、常设网络、临时根、元数据保护、目标环境与代理环境哈希的规范化身份串
    ///         （基线形态，不含 grant 段）。
    ///     </para>
    ///     <para>
    ///         The normalized baseline identity binding the backend, policy semantics version, component
    ///         protocol versions, effective read/write scopes, the baseline broad-read root, the
    ///         deny-read protection set, deny-write paths, standing network, temp roots, metadata
    ///         protection, and the target and proxy environment hashes (no grant section).
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         同一配置稳定；任何一项变化都会使旧会话授权不再匹配。代理环境只以哈希进入身份串。
    ///         grant 段由 <see cref="AppendGrantSection" /> 统一追加，授权读段由
    ///         <see cref="AppendAuthorizedReadSection" /> 追加，基线串保持两者皆无的形态。
    ///         internal 供测试构造基线对照身份。
    ///     </para>
    ///     <para>
    ///         Stable for identical configuration; any change makes old session grants stop matching.
    ///         The proxy environment enters the identity as a hash only. The grant section is appended
    ///         exclusively by <see cref="AppendGrantSection" /> and the authorized-read section by
    ///         <see cref="AppendAuthorizedReadSection" />, so the baseline carries neither.
    ///         internal for baseline-identity tests.
    ///     </para>
    /// </remarks>
    internal static string BuildPolicyIdentity(
        SandboxIsolationPolicy              policy,
        IReadOnlyDictionary<string, string> targetEnvironment,
        IReadOnlyDictionary<string, string> proxyEnvironment)
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
        AppendRoots(canonical, "baselineReadRoots", policy.BaselineReadRoots);
        AppendRoots(canonical, "denyReadPaths", policy.DenyReadPaths);
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
    ///     <para>
    ///         校验并规范化 grant 根范围：全部根必须为绝对路径；任一根与沙箱凭据目录重叠即拒绝
    ///         （词法加最终路径比对，与可信附加根同一套校验）；写根与元数据写路径不得覆盖沙箱组件、
    ///         home、凭据目录或 users 文件，也不得与 C1 权限系统实例路径（用户配置、信任文件、会话
    ///         存储目录）双向重叠；元数据写路径只允许指向内置受保护元数据子目录。
    ///         任何违规都 fail closed，绝不静默丢弃部分根。
    ///     </para>
    ///     <para>
    ///         Validates and normalizes a grant-root scope: every root must be an absolute path; any
    ///         root overlapping the sandbox credentials directory is refused (lexical plus final-path
    ///         comparison, the same validation trusted additional roots go through); write roots and
    ///         metadata write paths must not cover sandbox components, the home, the credentials
    ///         directory, or the users file, and must never overlap the C1 permission-system instance
    ///         paths (the user config, the trust file, the session-store directory) bidirectionally;
    ///         metadata write paths may only address the built-in protected metadata subdirectories.
    ///         Every violation fails closed; roots are never silently dropped.
    ///     </para>
    /// </summary>
    /// <param name="permissionSystemPaths">
    ///     <para>
    ///         组合调用方传入的 C1 实例路径（实际路径，底层不重新猜测）；null 表示调用方无此类实例。
    ///         The C1 instance paths passed by the composition caller (actual paths; the composer never
    ///         re-guesses); null means the caller has no such instances.
    ///     </para>
    /// </param>
    /// <remarks>
    ///     <para>
    ///         grant 根落进工作区内部时按无害超集接受（工作区本身已是基线范围），不额外拒绝；
    ///         空输入返回 <see cref="SandboxGrantScope.Empty" />。
    ///     </para>
    ///     <para>
    ///         A grant root inside the workspace is accepted as a harmless superset (the workspace is
    ///         already the baseline scope) and is not refused; empty input yields
    ///         <see cref="SandboxGrantScope.Empty" />.
    ///     </para>
    /// </remarks>
    internal static SandboxGrantScope ResolveGrantScope(
        SandboxIsolationPolicy   policy,
        WindowsSandboxComponents components,
        IReadOnlyList<string>?   grantReadRoots,
        IReadOnlyList<string>?   grantWriteRoots,
        IReadOnlyList<string>?   grantMetadataWritePaths,
        IReadOnlyList<string>?   permissionSystemPaths = null)
    {
        var readRoots      = NormalizeGrantRoots(grantReadRoots, "grant read root");
        var writeRoots     = NormalizeGrantRoots(grantWriteRoots, "grant write root");
        var metadataWrites = NormalizeGrantRoots(grantMetadataWritePaths, "grant metadata write path");
        if (readRoots.Count == 0 && writeRoots.Count == 0 && metadataWrites.Count == 0)
            return SandboxGrantScope.Empty;

        // 凭据保护：读/写/元数据写任一根与凭据目录重叠都拒绝，防止 grant 把 DPAPI 凭据暴露给沙箱组。
        foreach (var root in readRoots.Concat(writeRoots).Concat(metadataWrites))
            if (SandboxPolicyResolver.TouchesSandboxCredentials(root, components.SandboxHome))
                throw new
                    InvalidOperationException($"The {GrantRootLabel(root, readRoots, writeRoots, metadataWrites)} '{root}' overlaps the sandbox " +
                                              $"credentials directory of '{components.SandboxHome}'; refusing the grant scope without falling back.");

        // 可信组件保护：写根与元数据写路径不得覆盖组件、home、凭据目录或 users 文件，
        // 否则沙箱内命令可以篡改执行基础设施。读根不在此列：沙箱组本就需要读取并执行组件。
        var protectedPaths = new List<string>
        {
            components.SetupExecutablePath, components.RunnerExecutablePath, components.SandboxHome,
            components.CredentialsDirectory, components.UsersFilePath
        };
        foreach (var root in writeRoots.Concat(metadataWrites))
        {
            foreach (var trustedPath in protectedPaths)
                if (Workspace.IsInside(root, trustedPath))
                    throw new
                        InvalidOperationException($"The {GrantRootLabel(root, readRoots, writeRoots, metadataWrites)} '{root}' covers the trusted " +
                                                  $"sandbox path '{trustedPath}'; sandboxed commands could tamper with it, refusing the grant scope.");

            // C1 权限系统资源双向重叠：覆盖（改写保护文件/目录内容）与落入（在保护目录内部创建
            // 或改写条目）都拒绝；词法与最终形态都参与比对。
            if (permissionSystemPaths is null) continue;

            foreach (var protectedPath in permissionSystemPaths)
                if (OverlapsProtection(root, protectedPath))
                    throw new
                        InvalidOperationException($"The {GrantRootLabel(root, readRoots, writeRoots, metadataWrites)} '{root}' overlaps the permission-system " +
                                                  $"protected path '{protectedPath}'; permission-system resources cannot be exposed " +
                                                  "as grant write scope, refusing the grant scope.");
        }

        // 元数据写白名单：只允许移除内置受保护元数据子目录的 deny 项，其他 deny 路径不可经 grant 收缩。
        var metadataAllowed = SandboxIsolationPolicy.ProtectedMetadataSubpaths
                                                    .Select(subpath => Path.Combine(policy.WorkspaceRoot, subpath))
                                                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in metadataWrites)
            if (!metadataAllowed.Contains(path))
                throw new
                    InvalidOperationException($"The grant metadata write path '{path}' is not a protected metadata directory of " +
                                              $"'{policy.WorkspaceRoot}'; only the built-in metadata subdirectories " +
                                              $"({string.Join(", ", SandboxIsolationPolicy.ProtectedMetadataSubpaths)}) may be granted for writes.");

        return new SandboxGrantScope
        {
            ReadRoots          = readRoots,
            WriteRoots         = writeRoots,
            MetadataWritePaths = metadataWrites
        };
    }

    /// <summary>
    ///     <para>
    ///         把 grant 段追加到基线策略身份：空范围原样返回基线（撤销即回基线身份），非空范围
    ///         追加 grantReadRoots/grantWriteRoots/grantMetadataWrite 三行（规范化根、排序稳定）。
    ///         身份串格式单点维护，供 ShellTool 的冻结身份与测试构造共用。
    ///     </para>
    ///     <para>
    ///         Appends the grant section to a baseline policy identity: an empty scope returns the
    ///         baseline unchanged (revocation returns to the baseline identity), and a nonempty scope
    ///         appends the grantReadRoots/grantWriteRoots/grantMetadataWrite lines (normalized roots,
    ///         stable ordering). The identity format lives in one place, shared by the ShellTool's
    ///         frozen identity and test construction.
    ///     </para>
    /// </summary>
    internal static string AppendGrantSection(string baselineIdentity, SandboxGrantScope scope)
    {
        ArgumentNullException.ThrowIfNull(baselineIdentity);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEmpty) return baselineIdentity;

        var canonical = new StringBuilder(baselineIdentity);
        AppendRoots(canonical, "grantReadRoots", scope.ReadRoots);
        AppendRoots(canonical, "grantWriteRoots", scope.WriteRoots);
        AppendRoots(canonical, "grantMetadataWrite", scope.MetadataWritePaths);
        return canonical.ToString();
    }

    /// <summary>
    ///     <para>以基线身份加 grant 范围构造冻结审批身份；grant 撤销后回落基线身份。</para>
    ///     <para>
    ///         Builds the frozen approval identity from a baseline identity plus a grant scope;
    ///         revoking the grant returns to the baseline identity.
    ///     </para>
    /// </summary>
    internal static string BuildGrantPolicyIdentity(string baselineIdentity, SandboxGrantScope grantScope) =>
        AppendGrantSection(baselineIdentity, grantScope);

    /// <summary>
    ///     <para>
    ///         把授权读段追加到策略身份：空集原样返回基线，非空集追加 authorizedReadPaths 一行
    ///         （规范化根、排序稳定），使"带授权执行"与普通执行、以及不同授权集之间身份必不相同，
    ///         授权/复用键不得混用。
    ///     </para>
    ///     <para>
    ///         Appends the authorized-read section to a policy identity: an empty set returns the
    ///         baseline unchanged, a nonempty set appends one authorizedReadPaths line (normalized
    ///         roots, stable ordering) so authorized executions always differ from normal ones and from
    ///         each other — authorization and reuse keys never mix.
    ///     </para>
    /// </summary>
    internal static string AppendAuthorizedReadSection(
        string baselineIdentity, IReadOnlyList<string> authorizedReadRoots)
    {
        ArgumentNullException.ThrowIfNull(baselineIdentity);
        ArgumentNullException.ThrowIfNull(authorizedReadRoots);
        if (authorizedReadRoots.Count == 0) return baselineIdentity;

        var canonical = new StringBuilder(baselineIdentity);
        AppendRoots(canonical, "authorizedReadPaths", authorizedReadRoots);
        return canonical.ToString();
    }

    /// <summary>
    ///     <para>
    ///         C1 恒拒断言：authorized_read_paths 构造处 fail-closed 校验——任一授权路径与组合期 C1
    ///         权限系统实例路径双向重叠（覆盖或落入）即拒绝该次授权执行，错误可见；绝不静默丢弃。
    ///     </para>
    ///     <para>
    ///         The C1 constant-refusal assertion at the authorized_read_paths construction site:
    ///         fail-closed validation refusing the whole authorized execution when any authorized path
    ///         overlaps a composition-time C1 permission-system instance path bidirectionally (covering
    ///         or falling inside), with a visible error; paths are never silently dropped.
    ///     </para>
    /// </summary>
    internal static void RefusePermissionSystemOverlappingAuthorizedReads(
        IReadOnlyList<string>  authorizedReadRoots,
        IReadOnlyList<string>? permissionSystemPaths)
    {
        if (permissionSystemPaths is null) return;

        foreach (var root in authorizedReadRoots)
        foreach (var protectedPath in permissionSystemPaths)
            if (OverlapsProtection(root, protectedPath))
                throw new
                    InvalidOperationException($"The approved read root '{root}' overlaps the permission-system protected path '{protectedPath}'; " +
                                              "permission-system resources cannot be routed through the authorized-read channel, " +
                                              "refusing the grant without dropping the path.");
    }

    /// <summary>
    ///     <para>
    ///         规范化 grant 根列表：绝对化、去重（大小写不敏感）、保持顺序；相对路径与文件系统根
    ///         （盘根）一律拒绝——根级 grant 范围等于全盘授权，绝不放行。
    ///     </para>
    ///     <para>
    ///         Normalizes a grant-root list: absolutized, deduplicated case-insensitively,
    ///         order-preserving; relative paths and file-system roots (drive roots) are refused —
    ///         a root-level grant scope equals whole-drive authorization and never passes.
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> NormalizeGrantRoots(IReadOnlyList<string>? roots, string label)
    {
        if (roots is null || roots.Count == 0) return [];

        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException($"A {label} must not be null or whitespace.");

            if (!Path.IsPathFullyQualified(root))
                throw new InvalidOperationException($"A {label} must be a fully qualified absolute path: '{root}'.");

            var full = Path.GetFullPath(root);
            if (string.Equals(Path.GetPathRoot(full), full, StringComparison.OrdinalIgnoreCase))
                throw new
                    InvalidOperationException($"A {label} must not be a file-system root: '{full}'; a root-level " +
                                              "grant equals whole-drive authorization and is refused.");

            if (seen.Add(full)) results.Add(full);
        }

        return results;
    }

    /// <summary>
    ///     <para>为错误消息标注违规根的角色，便于审批与诊断定位 grant 来源。</para>
    ///     <para>Labels the offending root's role for approval and diagnostic messages.</para>
    /// </summary>
    private static string GrantRootLabel(
        string                root, IReadOnlyList<string> readRoots, IReadOnlyList<string> writeRoots,
        IReadOnlyList<string> metadataWrites) =>
        metadataWrites.Contains(root, StringComparer.OrdinalIgnoreCase) ? "grant metadata write path"
        : writeRoots.Contains(root, StringComparer.OrdinalIgnoreCase)   ? "grant write root"
                                                                          : "grant read root";

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
            SandboxProxyMode.Direct => EmptyProxyEnvironment(),
            SandboxProxyMode.System => BuildProxyEnvironment(ResolveSystemProxyDefinition()),
            SandboxProxyMode.Named => BuildProxyEnvironment(RequireNamedProxy(effective.ProxyName, networkSettings)),
            SandboxProxyMode.FollowAppDefault => FollowApplicationDefault(networkSettings),
            _ => throw new
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
                NetworkMode.Proxy => BuildProxyEnvironment(route.Proxy),
                _ => throw new ArgumentOutOfRangeException(nameof(route), route.Mode, "Unknown network mode.")
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
        var  destination = new Uri("https://tinyharness.sandbox-probe.invalid/");
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
            Port     = resolved.IsDefaultPort ? selectedProtocol == ProxyProtocol.Https ? 443 : 80 : resolved.Port
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
                        .Where(candidate =>
                                   string.Equals(candidate.Name, proxyName, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    ///     <para>
    ///         两路径的双向重叠判定：任一方向包含或相等都算重叠，词法形态与最终落点（展开 8.3
    ///         短名与 junction/symlink）四种形态两两比对；解析失败回落词法路径继续校验，绝不跳过。
    ///     </para>
    ///     <para>
    ///         The bidirectional overlap verdict for two paths: containment or equality in either direction
    ///         overlaps, with all four pairings of the lexical form and the final landing (8.3 short names
    ///         and junctions/symlinks expanded) compared; a failed resolution falls back to the lexical path
    ///         and keeps validating, never skipping.
    ///     </para>
    /// </summary>
    private static bool OverlapsProtection(string left, string right)
    {
        // 解析失败回落词法路径（同值），词法×词法、最终×最终与交叉四种配对全部比对。
        var leftFinal  = SandboxFinalPathResolver.TryGetFinalPath(left)  ?? left;
        var rightFinal = SandboxFinalPathResolver.TryGetFinalPath(right) ?? right;
        return Workspace.IsInside(left, right)           || Workspace.IsInside(right, left)           ||
               Workspace.IsInside(leftFinal, rightFinal) || Workspace.IsInside(rightFinal, leftFinal) ||
               Workspace.IsInside(left, rightFinal)      || Workspace.IsInside(rightFinal, left)      ||
               Workspace.IsInside(leftFinal, right)      || Workspace.IsInside(right, leftFinal);
    }

    private static string PolicyKindDisplay(SandboxPolicyKind kind) => kind switch
    {
        SandboxPolicyKind.ReadOnly       => "read-only",
        SandboxPolicyKind.WorkspaceWrite => "workspace-write",
        _                                => kind.ToString()
    };
}
