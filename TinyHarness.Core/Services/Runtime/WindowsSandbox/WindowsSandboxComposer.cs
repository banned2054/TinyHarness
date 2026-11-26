using System.Diagnostics;
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
    ///         基线敏感名扫描的默认枚举预算（文件系统条目数）；仅在调用方不显式传入
    ///         <c>scanEntryBudget</c>（null）时生效，耗尽即抛出，由组合点 fail-closed。
    ///     </para>
    ///     <para>
    ///         The default enumeration budget (filesystem entries) for the baseline sensitive-name
    ///         scan; it applies only when the caller passes no explicit <c>scanEntryBudget</c>
    ///         (null), and exhaustion throws so the composition site fails closed.
    ///     </para>
    /// </summary>
    internal const int BaselineSensitiveScanBudget = 500_000;

    /// <summary>
    ///     <para>
    ///         基线敏感名扫描的默认墙钟时间预算：仅在调用方不显式传入 <c>scanTimeBudget</c>
    ///         （null）时生效，随整个扫描阶段（枚举、命中复检、祖先覆盖判断与最终拒读集生成）
    ///         的同步检查点检查，超界即抛出并由组合点 fail-closed。单次同步 I/O（如一次
    ///         MoveNext 或一次属性探测）阻塞期间检查不生效；TimeSpan 不能声明为 C# 常量，
    ///         故为只读字段。
    ///     </para>
    ///     <para>
    ///         The default wall-clock time budget for the baseline sensitive-name scan; it applies
    ///         only when the caller passes no explicit <c>scanTimeBudget</c> (null), is checked
    ///         at the synchronous checkpoints of the whole scan phase (enumeration, hit
    ///         re-checks, ancestor coverage decisions, and final deny-set generation), and
    ///         exceeding it throws so the composition site fails closed. Checks do not fire
    ///         while a single synchronous I/O step (such as one MoveNext or one attribute probe)
    ///         blocks; TimeSpan cannot be declared as a C# constant, hence the read-only field.
    ///     </para>
    /// </summary>
    internal static readonly TimeSpan BaselineSensitiveScanTimeBudget = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     <para>校验设置并组合执行要素。</para>
    ///     <para>Validates the settings and composes the execution pieces.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         workspaceRoot 必须是绝对路径；knownSecrets 用于目标环境的最后防线过滤；networkSettings
    ///         供沙箱代理环境解析，常设在线时代理环境并入冻结目标环境；baselineReadProtection 携带
    ///         基线广泛读根与 deny-read 保护集（结构校验 fail-closed，deny 条目不允许盘根或读根
    ///         本身，且必须落在某读根覆盖内或与读根互为兄弟——即已被排除出读根的顶层项）。
    ///         会创建沙箱临时写根、NuGet 缓存根与工具根目录（harness 自有基础设施，不属于目标
    ///         命令副作用）。
    ///     </para>
    ///     <para>
    ///         workspaceRoot must be absolute; knownSecrets feed the target environment's last-line
    ///         filtering; networkSettings feed the sandbox proxy-environment resolution, and under a
    ///         standing-online policy the proxy environment merges into the frozen target environment;
    ///         baselineReadProtection carries the baseline broad-read root and the deny-read
    ///         protection set (structural validation fails closed: deny entries may be neither
    ///         filesystem roots nor a read root itself, and must sit inside a read root or be a
    ///         sibling of one — an already-excluded top-level entry). The sandbox temp write root,
    ///         NuGet cache root, and tools root directories are created here (harness-owned
    ///         infrastructure, not a target-command side effect).
    ///     </para>
    /// </remarks>
    /// <param name="baselineReadProtection">
    ///     <para>
    ///         基线广泛读保护计划（见 <see cref="PlanBaselineReadProtection" /> 与
    ///         <see cref="PlanBaselineReadProtectionTopLevel" />）；缺省 null 表示不引入 OS 广泛读
    ///         基线，沙箱读边界维持工作区+可信根。
    ///     </para>
    ///     <para>
    ///         The baseline broad-read protection plan (see
    ///         <see cref="PlanBaselineReadProtection" /> and
    ///         <see cref="PlanBaselineReadProtectionTopLevel" />); null by default keeps the sandbox
    ///         read boundary at the workspace plus trusted roots without an OS broad-read baseline.
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
        IReadOnlyList<string>?               permissionSystemPaths  = null,
        string?                              appBaseDirectory       = null)
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
                // 兄弟放宽：已被排除出读根的顶层项（固定敏感段、.codex、顶层 C1/会话存储形态）
                // 与读根同父（GetDirectoryName 相同）即合法配对——helper 对缺失 deny 的物化语义
                // 只适用于这类已存在或固定名的顶层项；深层路径既不在覆盖内也非兄弟仍拒绝。
                if (!readProtection.ReadRoots.Any(readRoot => Workspace.IsInside(readRoot, denyFull)) &&
                    !readProtection.ReadRoots.Any(readRoot =>
                                                       string.Equals(Path.GetDirectoryName(Path.GetFullPath(readRoot)),
                                                                     Path.GetDirectoryName(denyFull),
                                                                     StringComparison.OrdinalIgnoreCase)))
                    throw new
                        InvalidOperationException($"A baseline sandbox deny-read path '{denyPath}' is not covered by any baseline read root and is not a sibling of one; " +
                                                  "a deny entry outside the broad-read scope can only pair as an excluded top-level sibling, refusing the baseline.");
            }
        }

        // 根与环境推导与 provisioning 入口（WindowsSandboxPolicyPlanner）共用，防止 refresh ACL 与 provisioning payload 漂移。
        var plan = WindowsSandboxPolicyPlanner.Plan(settings, workspaceRoot, knownSecrets, baselineReadProtection,
                                                    appBaseDirectory);

        // 可信组件绝不允许被目标命令写入：拒绝位于工作区、额外写根或实际临时写根（含 extraEnvironment 覆盖的 TEMP/TMP）内的组件。
        IReadOnlyList<string> writeRoots = [workspaceRoot, .. plan.AdditionalWriteRoots];
        IReadOnlyList<string> effectiveComponentWriteRoots = SandboxIsolationPolicy.Deduplicate(
        [
            workspaceRoot,
            .. plan.Policy.EffectiveWriteRoots,
            .. plan.Policy.TempWriteRoots,
            plan.TempRoot,
            .. plan.AdditionalWriteRoots
        ]);
        RequireTrustedPathOutsideWriteRoots(plan.SetupExecutablePath, "setup executable", effectiveComponentWriteRoots);
        RequireTrustedPathOutsideWriteRoots(plan.RunnerExecutablePath, "runner executable",
                                            effectiveComponentWriteRoots);
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
                         Path.Combine(plan.SandboxHome, ".sandbox-bin"),
                         SandboxProvisionLeaseStore.ResolveDefaultLeaseDirectory()
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

        // 对必须存在的可执行组件做严格句柄最终路径校验、随包目录边界校验及词法/最终落点写根互斥（含实际临时写根，不回落词法）。
        WindowsSandboxComponentResolver.ValidateExecutableForUse(plan.ResolvedComponents.Setup,
                                                                 plan.ResolvedComponents.AppBaseDirectory,
                                                                 effectiveComponentWriteRoots);
        WindowsSandboxComponentResolver.ValidateExecutableForUse(plan.ResolvedComponents.Runner,
                                                                 plan.ResolvedComponents.AppBaseDirectory,
                                                                 effectiveComponentWriteRoots);

        if (!Directory.Exists(plan.SandboxHome))
            throw new
                InvalidOperationException($"The Windows sandbox home was not found at '{plan.SandboxHome}'; run provisioning first. " +
                                          "Refusing to fall back to host execution.");

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

        // 与执行期 refresh 共用按 home 键控的跨进程同步边界（GlobalDenyReadRefreshGate）：
        // 在同一临界区内复核已有登记保护路径并合并写入本次 C1 清单，防止另一端并发刷新或刚登记时产生竞态。
        using (AcquireHomeProtectionGate(plan.SandboxHome))
        {
            ValidateWriteRootsAgainstRegisteredProtectedPaths(effectiveComponentWriteRoots, plan.SandboxHome,
                                                              "sandbox write root");
            SandboxProtectedPathsStore.WriteIfChanged(components,
                                                      SandboxProtectedPathsStore.ComposeManifest(components,
                                                          permissionSystemPaths));
        }

        Directory.CreateDirectory(plan.TempRoot);
        Directory.CreateDirectory(plan.NuGetCacheRoot);
        Directory.CreateDirectory(plan.ToolsRoot);

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
        Func<string, IReadOnlyList<string>>? sensitiveScan   = null) =>
        PlanBaselineReadProtection(protectedSecretPaths, ensureDirectory, sandboxSettings, sensitiveScan,
                                   CancellationToken.None);

    /// <summary>
    ///     <para>可取消地构建基线读保护；取消或预算耗尽时不返回部分保护集。</para>
    ///     <para>
    ///         Builds baseline read protection with cancellation; cancellation or budget exhaustion never returns a partial
    ///         deny set.
    ///     </para>
    /// </summary>
    public static SandboxBaselineReadProtection? PlanBaselineReadProtection(
        IReadOnlyList<string>   protectedSecretPaths, string?                              ensureDirectory,
        WindowsSandboxSettings? sandboxSettings,      Func<string, IReadOnlyList<string>>? sensitiveScan,
        CancellationToken       cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathFullyQualified(profileRoot)) return null;

        return PlanBaselineReadProtectionCore(Path.GetFullPath(profileRoot), protectedSecretPaths, ensureDirectory,
                                              sandboxSettings, sensitiveScan, cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         基线读保护计划的显式读根形态（internal）：组合点以用户 profile 调用，测试以合成目录
    ///         调用。计划内容与覆盖不变量以该根为准。条目预算 null=默认 500k（耗尽 fail-closed）、
    ///         正数=显式上限、负数=立即耗尽；时间预算 null=默认 10 分钟、0=不设、正数=显式上限、
    ///         负数=立即超界；枚举生成器、耗时时钟与命中属性探测均为测试注入，null 分别走真实
    ///         文件系统枚举、真实墙钟与 <see cref="File.GetAttributes(string)" />。
    ///     </para>
    ///     <para>
    ///         The explicit-read-root form of the baseline read-protection plan (internal):
    ///         composition calls it with the user profile, tests with synthetic directories. The
    ///         plan's content and coverage invariants are relative to this root. The entry budget is
    ///         null for the default 500k (exhaustion fails closed), a positive explicit cap, or
    ///         negative to exhaust immediately; the time budget is null for the default 10 minutes,
    ///         0 to disable, a positive explicit cap, or negative to exceed immediately; the entry
    ///         generator, the elapsed-time clock, and the hit attribute probe are test-only
    ///         injections, and null uses the real filesystem enumeration, the real wall clock, and
    ///         <see cref="File.GetAttributes(string)" /> respectively.
    ///     </para>
    /// </summary>
    internal static SandboxBaselineReadProtection PlanBaselineReadProtectionCore(
        string                               profileRoot,
        IReadOnlyList<string>                protectedSecretPaths, string? ensureDirectory,
        WindowsSandboxSettings?              sandboxSettings,
        Func<string, IReadOnlyList<string>>? sensitiveScan,
        CancellationToken                    cancellationToken = default,
        int?                                 scanEntryBudget    = null,
        Func<string, IEnumerable<string>>?   enumerateEntries   = null,
        TimeSpan?                            scanTimeBudget     = null,
        Func<TimeSpan>?                      elapsedSinceStart  = null,
        Func<string, FileAttributes>?        inspectEntry       = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

        // C2 OS 层补强（敏感名扫描）：基线 deny 集此前只覆盖 profile 顶层敏感目录段，
        // 嵌套敏感目录与文件名/扩展名级敏感命中（.env、id_rsa、.pem 等）没有 OS 读拒，
        // Shell 可绕过文件工具的应用层 C2 分类直读。枚举读根，把已存在的命中并入 deny 集：
        // 沙箱 home 子树跳过（deny 不碰沙箱运行时）；已有整目录 deny 覆盖的子树下降前剪枝
        // （内部命中已被祖先条目继承，未覆盖子树的枚举错误仍 fail-closed）；已有 deny 祖先
        // 覆盖的命中跳过（继承已覆盖）；命中复检仅在条目已消失时跳过（绝不物化成目录），
        // 权限与其他 I/O 错误使整个规划失败（Shell 不启动）；重解析点只做词法判定、不下降。
        // 扫描是组合期快照——会话中途新建的敏感路径要等下次组合进入 OS deny（文件工具
        // 应用层分类不受影响）。预算语义：null（默认 500k）、正数显式上限与负数立即耗尽的
        // 条目预算超限即组合失败（fail-closed），绝不静默证明一个没扫完的边界；默认 10 分钟
        // 墙钟时间预算覆盖整个扫描阶段（枚举或注入扫描调用、命中复检、祖先覆盖判断与最终
        // 拒读集生成），配对期间与返回前同步检查，超界即丢弃整个计划（0=不设、负数=立即
        // 超界）。不可读子树与不可检查条目（含不可检查的已命中条目）使扫描不可认证，同样
        // fail-closed，命中一律丢弃；仅已消失条目按词法判定处理。祖先覆盖判断按命中路径
        // 逐级上溯查规范化拒读集（深度级），不做命中数×集合大小的平方级扫描。
        // C2 OS-layer reinforcement (the sensitive-name scan): the baseline deny set only
        // covered top-level sensitive directory segments under the profile, leaving nested
        // sensitive directories and file-name/extension-level hits (.env, id_rsa, .pem, ...)
        // without an OS read deny, so the shell could bypass the file tools' application-layer
        // C2 classification and read them directly. The read root is enumerated, folding
        // existing hits into the deny set: the sandbox home subtree is skipped (the deny set
        // never touches sandbox runtime), subtrees already covered by a whole-directory deny
        // entry are pruned before descent (their hits are inherited from the ancestor entry,
        // while enumeration errors in uncovered subtrees still fail closed), hits already
        // covered by a deny ancestor are skipped (inheritance covers them), the hit re-check
        // skips only entries confirmed vanished (never materialized as directories) while
        // permission and other I/O errors fail the whole plan (the shell never starts), and
        // reparse points are judged lexically without descending. The scan is a
        // composition-time snapshot — sensitive paths created mid-session reach the OS deny
        // at the next composition while the file tools' application-layer classification keeps
        // protecting immediately. Budget semantics: overrunning the entry budget (null for the
        // default 500k, a positive explicit cap, or negative to exhaust immediately) fails the
        // composition (fail-closed), never silently certifying a partially scanned boundary;
        // the default 10-minute wall-clock time budget covers the whole scan phase (the
        // enumeration or the injected scan call, hit re-checks, the ancestor coverage
        // decisions, and the final deny-set generation), is checked synchronously during
        // pairing and before return, and discards the whole plan on overrun (0 disables it,
        // negative exceeds immediately). Unreadable subtrees and uninspectable entries
        // (including uninspectable already-hit entries) make the scan unauthenticatable and
        // fail closed the same way, discarding the hits; only vanished entries are handled
        // lexically. The ancestor coverage decision walks each hit's ancestor chain against
        // the normalized deny set (depth-proportional) instead of a hits-times-set quadratic
        // scan.
        // 同一墙钟预算覆盖整个扫描阶段；枚举与配对共用同一时钟原点，测试可注入确定性时钟。
        var scanStarted = Stopwatch.GetTimestamp();
        Func<TimeSpan> elapsedSinceScanStart =
            elapsedSinceStart ?? (Func<TimeSpan>)(() => Stopwatch.GetElapsedTime(scanStarted));
        var effectiveTimeBudget = scanTimeBudget ?? BaselineSensitiveScanTimeBudget;
        var unlimitedTime       = effectiveTimeBudget == TimeSpan.Zero;

        void ThrowIfOverScanTimeBudget(string phase)
        {
            if (unlimitedTime) return;

            var elapsed = elapsedSinceScanStart();
            if (elapsed <= effectiveTimeBudget) return;

            throw new
                InvalidOperationException($"The baseline sensitive-name scan exceeded its wall-clock time budget of {effectiveTimeBudget.TotalMilliseconds:F0} ms while {phase} under '{root}' (elapsed {elapsed.TotalMilliseconds:F0} ms); " +
                                          "the sensitive-name deny set cannot be certified on a partially processed scan, so the baseline fails closed. " +
                                          "Check the read root for an unresponsive or network enumeration source to proceed.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverScanTimeBudget("starting the baseline sensitive-name scan");

        // 基线拒读查找集：枚举下降剪枝与命中配对的祖先判断共用一份集合；配对新增命中时
        // 同步并入。查找键统一裁剪尾部分隔符，是否保留由 .NET 的根识别决定：驱动器根
        // （如 C:\）的分隔符是根的一部分、保留；UNC 共享根末尾超出根的额外分隔符会被
        // 裁剪为 \\server\share 形态，恰与枚举条目及 GetDirectoryName 祖先的形态对齐。
        // ensureDirectory 等可带尾分隔符进入 denyPaths（payload 保持原样），而枚举条目与
        // GetDirectoryName 祖先不带，不裁剪则剪枝与祖先去重全部失配；集合只作查找索引，
        // 与列表不逐字一致。
        var denyCovered = new HashSet<string>(denyPaths.Select(NormalizeDenyLookupKey),
                                               StringComparer.OrdinalIgnoreCase);
        var scanHits = sensitiveScan?.Invoke(root) ??
                       EnumerateBaselineSensitiveNameHits(root,
                                                          budget : scanEntryBudget,
                                                          cancellationToken : cancellationToken,
                                                          enumerateEntries : enumerateEntries,
                                                          timeBudget : scanTimeBudget,
                                                          elapsedSinceStart : elapsedSinceScanStart,
                                                          denyCoveredSubtreeRoots : denyCovered);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverScanTimeBudget("pairing sensitive-name hits");
        Func<string, FileAttributes> recheckEntry = inspectEntry ?? File.GetAttributes;
        foreach (var hit in scanHits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfOverScanTimeBudget("pairing sensitive-name hits");
            if (string.IsNullOrWhiteSpace(hit)) continue;

            var full = Path.GetFullPath(hit);
            if (!Workspace.IsInside(root, full)) continue;

            // 沙箱 home 子树：deny 不进入沙箱运行时面（与 cap_sid 显式条目互补，宁可少收窄）。
            if (sandboxHomeInsideProfile is { } homeRoot && Workspace.IsInside(homeRoot, full)) continue;

            // 命中复检区分错误类型：File/Directory.Exists 会把权限与 I/O 错误吞成 false，
            // 已命中的敏感路径绝不能因不可检查被当作消失跳过。仅 FileNotFoundException/
            // DirectoryNotFoundException 按"已消失"跳过；权限与其他 I/O 错误带上下文向上
            // 传播使整个规划失败（原异常保留为 inner exception）。
            try
            {
                recheckEntry(full);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                         or UnauthorizedAccessException or IOException)
            {
                // 扫描与配对之间消失的命中跳过：helper 会把缺失 deny 路径物化为目录，绝不制造。
                if (ex is FileNotFoundException or DirectoryNotFoundException) continue;

                throw new
                    InvalidOperationException($"The baseline sensitive-name scan could not re-check the hit '{full}' during deny pairing under the read root '{root}': {ex.Message}; " +
                                              "an uninspectable hit cannot be authenticated either, so the collected hits are discarded and the baseline fails closed. " +
                                              "Restore read access to the path to proceed.", ex);
            }

            // 已被 deny 祖先覆盖的命中跳过：继承已提供 OS 保护，条目只会膨胀 deny 集。
            if (IsCoveredByDenyAncestor(denyCovered, full)) continue;

            denyPaths.Add(full);
            denyCovered.Add(NormalizeDenyLookupKey(full));
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverScanTimeBudget("generating the final deny-read set");
        var finalDenyPaths = SandboxIsolationPolicy.Deduplicate(denyPaths);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverScanTimeBudget("returning the baseline plan");

        return new SandboxBaselineReadProtection([root], finalDenyPaths);
    }

    /// <summary>
    ///     <para>
    ///         规划顶层读根基线（替换全 profile 敏感名扫描的 Core 侧基础）：只枚举用户 profile 的
    ///         直接子项一层，绝不递归。固定敏感目录段（含上游 <c>.codex</c> 恒定加入）与顶层敏感
    ///         子项既从读根排除，也作为精确路径下发拒读（缺失的固定名由 helper 物化为目录）；普通
    ///         重解析点解析真实落点后按落点归属授予或跳过（不可解析、profile 外或被排除/拒读覆盖
    ///         一律跳过，不抛异常、不下发 deny）；其余普通顶层子项作为读根交给 helper。读根集合
    ///         定型后并入 helper 拒读簿记条目。profile 根不可用时返回 null，调用方如实降级为无
    ///         广泛读基线；顶层枚举失败绝不回退到授权整个 profile。
    ///     </para>
    ///     <para>
    ///         Plans the top-level read-root baseline (the Core-side basis replacing the full-profile
    ///         sensitive-name scan): only the direct children of the user profile are enumerated, one
    ///         level, never recursively. Fixed sensitive directory segments (including the upstream
    ///         <c>.codex</c>, always added) and top-level sensitive children are excluded from the
    ///         read roots and issued as precise deny paths (missing fixed names are materialized as
    ///         directories by the helper); ordinary reparse points are granted or skipped by their
    ///         resolved real landing (unresolvable, outside the profile, or covered by an
    ///         excluded/denied path all skip without throwing or issuing a deny); the remaining
    ///         ordinary top-level children become the read roots for the helper. After the read-root
    ///         set is final, helper deny-read bookkeeping entries are merged in. An unavailable
    ///         profile root returns null so callers degrade honestly to a baseline without broad
    ///         read; a failed top-level enumeration never falls back to authorizing the whole
    ///         profile.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         语义边界：普通读根内任意深度的同名 <c>.env</c>、<c>.pem</c> 等不再由启动规划发现
    ///         （首版明确收敛 C2 的 OS 承诺，内置文件工具继续应用自己的分类与审批）；条目消失
    ///         （并发删除竞态）按词法跳过，绝不物化；顶层条目属性不可检查仍 fail-closed，而链接
    ///         落点不可解析（云端占位符形态）、解析 IO/权限错、落点在 profile 外或被排除/拒读
    ///         覆盖一律跳过授读——helper 把 junction 读根的授予打到规范化目标子树，而 NTFS 继承
    ///         不跨 junction、占位符 fail-closed 只会因一个条目拒绝整个基线且无安全增益；落点为
    ///         profile 根本身（junction → profile 根）等价授予 profile 根为读根，按覆盖内授读；
    ///         <c>.sandbox-secrets</c> 沿用现状只排除出读根、不入 deny 集（凭据目录已由
    ///         provisioning 永久 DENY，缺失拒读只会物化出噪音目录）；沙箱 home 本身不排除、
    ///         不 deny，仅 <c>&lt;home&gt;\cap_sid</c> 文件级拒读。C1 宿主密钥路径落在 profile 内
    ///         必须已存在（缺失 fail-closed，绝不物化成目录）；ensureDirectory 落在覆盖内先创建
    ///         再配对。诊断只经 <see cref="SandboxDiagnosticLog" /> 输出一条事件，不新增计量属性。
    ///     </para>
    ///     <para>
    ///         Semantic boundaries: same-name <c>.env</c> and <c>.pem</c> entries at any depth inside
    ///         ordinary read roots are no longer discovered by the startup planning (the first version
    ///         deliberately narrows the C2 OS promise; the built-in file tools keep applying their own
    ///         classification and approval); vanished entries (a concurrent-deletion race) are skipped
    ///         lexically and never materialized; uninspectable top-level entries still fail closed,
    ///         while unresolvable link landings (the cloud-placeholder shape), resolution I/O or
    ///         permission errors, and landings outside the profile or covered by an excluded/denied
    ///         path all skip the grant — the helper lands a junction read root's grant on the
    ///         canonicalized target subtree while NTFS inheritance does not cross a junction, so
    ///         failing closed on placeholders would refuse the whole baseline over one entry with no
    ///         security gain; a landing equal to the profile root itself (a junction to the profile
    ///         root) grants the profile root as a read root, treated as inside the coverage;
    ///         <c>.sandbox-secrets</c> stays excluded from the read roots without a deny entry (the
    ///         credentials directory is already permanently denied by provisioning, and a missing
    ///         deny would only materialize noise); the sandbox home itself is neither excluded nor
    ///         denied, with only a file-level deny on <c>&lt;home&gt;\cap_sid</c>. C1 host secret
    ///         paths inside the profile must already exist (missing fails closed and is never
    ///         materialized as a directory); an ensureDirectory inside the coverage is created before
    ///         pairing. Diagnostics emit one <see cref="SandboxDiagnosticLog" /> event; no metering
    ///         property is added.
    ///     </para>
    ///     <para>
    ///         拒读簿记协调：读根集合定型后读取 <c>&lt;resolvedHome&gt;\.sandbox\deny_read_acl_state.json</c>
    ///         （helper 按安全主体全量同步 deny，不在 payload 即撤销），把全部主体的条目并入最终
    ///         deny 集、永不收缩——共享主体与 home 的旧 CLI 依赖的扫描命中 deny 不得被 Desktop
    ///         refresh 撤销。文件缺失=空集（全新 home 无 deny 历史）；不可读或损坏（JSON 解析、
    ///         IO/权限错）fail-closed，协调失败绝不静默收缩。并入条目按 Desktop 自身配对校验
    ///         过滤：既不落在任何读根覆盖内也与读根非兄弟、或等于某读根本身的条目丢弃并记诊断
    ///         （由 CLI 下次 refresh 自行恢复）；覆盖沙箱运行时路径的条目不丢弃，交给
    ///         <see cref="Compose" /> 既有校验 fail-closed——宁可拒绝组合也不静默删保护。簿记可能
    ///         含数千条 CLI 扫描命中，超出 setup payload base64 的 UAC 位置参数上限
    ///         （<c>ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit</c>）时由 payload
    ///         构造处既有检查 fail-closed，本层不加预检。
    ///     </para>
    ///     <para>
    ///         Deny bookkeeping coordination: once the read-root set is final, the planner reads
    ///         <c>&lt;resolvedHome&gt;\.sandbox\deny_read_acl_state.json</c> (the helper syncs denies
    ///         wholesale per security principal, revoking anything absent from the payload) and folds
    ///         every principal's entries into the final deny set, never shrinking it — scan-hit
    ///         denies that the legacy CLI sharing the principal and home relies on must not be
    ///         revoked by a Desktop refresh. A missing file is an empty set (a fresh home has no deny
    ///         history); an unreadable or corrupt file (JSON parsing, I/O or permission errors) fails
    ///         closed, because a failed coordination must never silently shrink. Merged entries are
    ///         filtered by the Desktop's own pairing validation: entries neither covered by any read
    ///         root nor a sibling of one, or equal to a read root itself, are dropped with a
    ///         diagnostic (the CLI restores them on its next refresh); entries covering a sandbox
    ///         runtime path are kept and left to the existing <see cref="Compose" /> validation to
    ///         fail closed — refusing the composition beats silently deleting protection. The
    ///         bookkeeping can hold thousands of CLI scan hits; a payload past the setup base64 UAC
    ///         positional-argument limit
    ///         (<c>ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit</c>) fails closed at the
    ///         existing payload-construction check, and this layer adds no pre-check.
    ///     </para>
    /// </remarks>
    public static SandboxBaselineReadProtection? PlanBaselineReadProtectionTopLevel(
        IReadOnlyList<string>   protectedSecretPaths, string?           ensureDirectory = null,
        WindowsSandboxSettings? sandboxSettings       = null,
        CancellationToken       cancellationToken     = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profileRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profileRoot) || !Path.IsPathFullyQualified(profileRoot)) return null;

        return PlanBaselineReadProtectionTopLevelCore(Path.GetFullPath(profileRoot), protectedSecretPaths,
                                                      ensureDirectory, sandboxSettings, cancellationToken);
    }

    /// <summary>
    ///     <para>
    ///         顶层读基线规划的显式读根形态（internal）：组合点以用户 profile 调用，测试以合成
    ///         目录与注入点调用，风格对齐 <see cref="PlanBaselineReadProtectionCore" />。
    ///         <paramref name="enumerateEntries" />（输入=根，输出=直接子项一层）、
    ///         <paramref name="resolveLanding" />（输入=重解析点路径与属性，输出=最终落点；null=
    ///         不可解析）与 <paramref name="readDenyBookkeeping" />（输入=簿记状态文件路径，
    ///         输出=原始 JSON 内容，null=文件缺失，抛出=fail-closed）为测试注入，null 分别走
    ///         真实文件系统枚举、BCL <see cref="FileSystemInfo.ResolveLinkTarget" /> 与真实簿记
    ///         读取。
    ///     </para>
    ///     <para>
    ///         The explicit-read-root form of the top-level baseline planning (internal):
    ///         composition calls it with the user profile, tests with synthetic directories and
    ///         injections, styled after <see cref="PlanBaselineReadProtectionCore" />.
    ///         <paramref name="enumerateEntries" /> (input = the root, output = the direct children,
    ///         one level), <paramref name="resolveLanding" /> (input = the reparse path and its
    ///         attributes, output = the final landing; null = unresolvable), and
    ///         <paramref name="readDenyBookkeeping" /> (input = the bookkeeping state-file path,
    ///         output = the raw JSON content, null = file missing, throwing = fail closed) are
    ///         test-only injections; null uses the real filesystem enumeration, the BCL
    ///         <see cref="FileSystemInfo.ResolveLinkTarget" />, and the real bookkeeping read
    ///         respectively.
    ///     </para>
    /// </summary>
    internal static SandboxBaselineReadProtection PlanBaselineReadProtectionTopLevelCore(
        string                                profileRoot,
        IReadOnlyList<string>                 protectedSecretPaths, string?           ensureDirectory = null,
        WindowsSandboxSettings?               sandboxSettings       = null,
        CancellationToken                     cancellationToken     = default,
        Func<string, IEnumerable<string>>?    enumerateEntries      = null,
        Func<string, FileAttributes, string?>? resolveLanding       = null,
        Func<string, string?>?                readDenyBookkeeping   = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var root    = Path.GetFullPath(profileRoot);
        List<string> denyPaths = [];
        var          readRoots = new List<string>();

        // 沙箱 home 解析与 cap_sid 条目沿用旧 planner 同一来源与语义：home 本身不排除、不 deny，
        // 仅文件级拒读 cap_sid；新读根形态下 home（直接子项或经普通读根覆盖）使 cap_sid 自然
        // 落在读根覆盖内。
        var sandboxHomeInsideProfile = TryResolveSandboxHomeInsideProfile(sandboxSettings, root);
        if (sandboxHomeInsideProfile is { } homeForCapSid)
            denyPaths.Add(Path.Combine(homeForCapSid, "cap_sid"));

        // helper 拒读簿记协调按解析成功的 home 进行（无论是否落在 profile 内）：共享主体与 home
        // 的旧 CLI 把簿记写在其 home 下，解析失败时跳过（组合器对同一配置会抛出带上下文的失败）。
        var bookkeepingHome = TryResolveSandboxHomeForBookkeeping(sandboxSettings);

        // 会话存储等受保护目录：覆盖范围内先创建再配对，使 deny 自目录诞生即生效并被继承；
        // 顶层同名子项随后经拒读覆盖检查被排除出读根（deny 等于读根会被组合校验拒绝）。
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

        // 固定敏感目录段（顶层拒读）：.sandbox-secrets 沿用现状不入集（provisioning 永久 DENY，
        // 缺失拒读只会物化噪音目录）；上游 .codex 恒定加入（授权关键内容属 C1，沙箱链路无
        // 读/执行依赖）。固定名不必存在：helper 会把缺失路径物化为目录，防同轮 create-then-read。
        foreach (var name in SensitivePathClassifier.KnownSensitiveDirectoryNames)
        {
            if (name.Equals(".sandbox-secrets", StringComparison.OrdinalIgnoreCase)) continue;

            denyPaths.Add(Path.Combine(root, name));
        }

        denyPaths.Add(Path.Combine(root, ".codex"));

        // 拒读查找集：顶层条目的读根排除（精确命中）与重解析点落点归属判断共用；规范化口径与
        // 旧 planner 一致（见 NormalizeDenyLookupKey）。
        var denyCovered = new HashSet<string>(denyPaths.Select(NormalizeDenyLookupKey),
                                              StringComparer.OrdinalIgnoreCase);
        // 被排除出读根但不下发拒读的路径（现状 .sandbox-secrets）：重解析点落点归属判断仍须
        // 把它们当作排除范围。
        var excludedWithoutDeny = new List<string>();

        // 顶层枚举：只取直接子项一层，绝不递归；失败 fail-closed 并指明根与阶段，绝不回退到
        // 授权整个 profile。
        cancellationToken.ThrowIfCancellationRequested();
        List<string> children;
        try
        {
            children = (enumerateEntries?.Invoke(root) ?? Directory.EnumerateFileSystemEntries(root)).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new
                InvalidOperationException($"The top-level baseline read-root planning could not enumerate the profile root '{root}': {ex.Message}; " +
                                          "the read boundary cannot be certified, so the baseline fails closed instead of authorizing the whole profile. " +
                                          "Restore read access to the profile root to proceed.", ex);
        }

        var             reparseChildren = new List<(string Entry, FileAttributes Attributes)>();
        var             topLevelEntries = 0;
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            topLevelEntries++;

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(child);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 并发删除竞态消失的条目无暴露面：按词法跳过（固定名拒读已无条件在集内），
                // 绝不物化、绝不授读。
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 顶层条目存在但属性不可检查（权限或 IO）：不可认证即 fail-closed，绝不吞掉。
                throw new
                    InvalidOperationException($"The top-level baseline read-root planning could not inspect the entry '{child}' under the profile root '{root}': {ex.Message}; " +
                                              "an uninspectable entry cannot be authenticated, so the baseline fails closed instead of authorizing the whole profile. " +
                                              "Restore read access to the entry to proceed.", ex);
            }

            // 已被拒读覆盖的顶层条目（固定敏感段、.codex、顶层 C1/ensureDirectory 形态）：
            // 拒读已在集内，绝不再授读（deny 等于读根会被组合校验拒绝），也不重复入集。
            if (IsCoveredByDenyAncestor(denyCovered, child)) continue;

            // 顶层敏感命中：从读根排除并精确拒读（目录段、文件名与扩展名口径一致）。
            if (SensitivePathClassifier.IsKnownSensitivePath(child))
            {
                if (Path.GetFileName(child).Equals(".sandbox-secrets", StringComparison.OrdinalIgnoreCase))
                {
                    excludedWithoutDeny.Add(child);
                    continue;
                }

                denyPaths.Add(child);
                denyCovered.Add(NormalizeDenyLookupKey(child));
                continue;
            }

            // 重解析点不在此授读：统一在全部排除集就绪后解析落点再判断，避免漏判后枚举的排除项。
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                reparseChildren.Add((child, attributes));
                continue;
            }

            readRoots.Add(child);
        }

        // 重解析点落点裁决（收窄契约）：只有落点可解析、规范化后在 profile 覆盖内（含相等语义，
        // junction → profile 根等价授予 profile 根为读根）且不被 deny/排除集覆盖才授读；其余一切
        // 情形（落点不可解析、解析 IO/权限错、消失、落点在 profile 外、落点被覆盖）一律跳过授读，
        // 不抛异常、不下发 deny，经诊断事件记录原因与条目。背景：helper 把 junction 读根的授予
        // 打到规范化目标子树，而 NTFS 继承不跨 junction，旧策略下 junction 目标本就不可读；云端
        // 占位符（OneDrive on-demand 等）是 reparse 但 ResolveLinkTarget 返回 null，fail-closed 会
        // 因一个占位符拒绝整个基线且无安全增益。解析用 BCL API，无反射、无新依赖；注入委托默认
        // 真实解析，便于测试。
        var skippedReparse = new List<string>();
        foreach (var (child, attributes) in reparseChildren)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? landing;
            try
            {
                landing = (resolveLanding ?? ResolveReparseLanding)(child, attributes);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 解析前条目或链接链消失（并发删除竞态）：无暴露面，按词法跳过。
                skippedReparse.Add($"{child}:vanished");
                continue;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 解析 IO/权限错（含 UAC 保护目标）：跳过授读，绝不让一个条目阻断整个基线。
                skippedReparse.Add($"{child}:resolve-error");
                continue;
            }

            if (landing is null)
            {
                // 落点不可解析（云端占位符形态）：跳过授读，不下发 deny（条目已存在，helper 物化
                // 语义不适用）。
                skippedReparse.Add($"{child}:unresolvable");
                continue;
            }

            var landingFull = Path.GetFullPath(landing);
            if (!Workspace.IsInside(root, landingFull))
            {
                skippedReparse.Add($"{child}:landing-outside-profile");
                continue;
            }

            if (IsCoveredByDenyAncestor(denyCovered, landingFull) ||
                excludedWithoutDeny.Any(excluded => Workspace.IsInside(excluded, landingFull)))
            {
                skippedReparse.Add($"{child}:landing-covered");
                continue;
            }

            readRoots.Add(child);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 语义 deny 集与读根集合都定型后（重解析点裁决之后）合并 helper 拒读簿记：最终
        // DenyReadPaths = Deduplicate(语义集 ∪ 簿记有效条目)，永不收缩共享主体的既有 deny。
        // 首次准备与每命令 refresh 前的并集共用同一共享实现（SandboxDenyReadBookkeeping）：
        // deny-only 并集只收窄不扩权，grant 范围仍冻结于 Prepare，不构成授权与执行之间的竞态。
        var (denyPathsWithBookkeeping, bookkeepingEntries, bookkeepingMerged) =
            SandboxDenyReadBookkeeping.Merge("top-level baseline read-root planning", denyPaths, readRoots,
                                             bookkeepingHome, readDenyBookkeeping, cancellationToken);
        var finalDenyPaths = SandboxIsolationPolicy.Deduplicate(denyPathsWithBookkeeping);

        // 顶层规划诊断（顶层条目数/耗时/实际读根清单/落点跳过与簿记计数）：只进 Debug 诊断日志，
        // 不进身份串、指纹或 payload；不记录敏感命中路径名（重解析条目名非敏感，簿记只记计数）。
        SandboxDiagnosticLog.Event("baseline-top-level",
                                   $"topLevelEntries={topLevelEntries} " +
                                   $"durationMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} " +
                                   $"denyCount={finalDenyPaths.Count} " +
                                   $"bookkeepingEntries={bookkeepingEntries} bookkeepingMerged={bookkeepingMerged} " +
                                   $"skippedReparse=[{string.Join(";", skippedReparse)}] " +
                                   $"readRoots=[{string.Join(";", readRoots)}]");
        return new SandboxBaselineReadProtection([.. readRoots], finalDenyPaths);
    }

    /// <summary>
    ///     <para>
    ///         解析重解析点的最终落点（BCL API，无反射、无新依赖）：目录经 DirectoryInfo、其余经
    ///         FileInfo 调用 <see cref="FileSystemInfo.ResolveLinkTarget(bool)" /> 展开完整链接链；
    ///         BCL 判定不可解析时返回 null（调用方跳过授读，占位符形态）。
    ///     </para>
    ///     <para>
    ///         Resolves the final landing of a reparse point (a BCL API, no reflection, no new
    ///         dependency): directories go through DirectoryInfo and the rest through FileInfo, calling
    ///         <see cref="FileSystemInfo.ResolveLinkTarget(bool)" /> to expand the full link chain;
    ///         null is returned when the BCL cannot resolve the link (the caller skips the grant —
    ///         the cloud-placeholder shape).
    ///     </para>
    /// </summary>
    private static string? ResolveReparseLanding(string entry, FileAttributes attributes)
    {
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory)
            ? new DirectoryInfo(entry)
            : new FileInfo(entry);
        return info.ResolveLinkTarget(returnFinalTarget : true)?.FullName;
    }

    /// <summary>
    ///     <para>
    ///         解析沙箱 home 供 helper 拒读簿记协调使用（无论是否落在 profile 内）；settings 为
    ///         null 或解析失败（显式 sandboxHome 非绝对等配置错误）返回 null，跳过协调——组合器
    ///         对同一配置会抛出带上下文的失败。
    ///     </para>
    ///     <para>
    ///         Resolves the sandbox home for the helper deny-read bookkeeping coordination (whether
    ///         or not it sits inside the profile); null settings or a resolution failure (a
    ///         configuration error such as a non-absolute explicit sandboxHome) returns null and skips
    ///         the coordination — the composer throws a contextual failure for the same
    ///         configuration.
    ///     </para>
    /// </summary>
    private static string? TryResolveSandboxHomeForBookkeeping(WindowsSandboxSettings? sandboxSettings)
    {
        if (sandboxSettings is null) return null;

        try
        {
            return Path.GetFullPath(WindowsSandboxComponents.ResolveSandboxHome(sandboxSettings.SandboxHome,
                                                                                sandboxSettings.AllowTcpLoopback));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         拒读查找键规范化：裁剪路径末尾的目录分隔符；尾分隔符是否保留由 .NET 的根识别
    ///         决定。驱动器根（如 <c>C:\</c>）的分隔符是根的一部分、保留；UNC 共享根末尾超出
    ///         根的额外分隔符会被裁剪为 <c>\\server\share</c> 形态，与枚举条目及
    ///         GetDirectoryName 祖先的形态一致，查找键因此对齐。
    ///     </para>
    ///     <para>
    ///         Normalizes a deny lookup key by trimming the trailing directory separator; whether
    ///         a trailing separator survives is decided by .NET root recognition. A drive root
    ///         (such as <c>C:\</c>) keeps its separator as part of the root, while extra
    ///         separators beyond a UNC share root are trimmed to the <c>\\server\share</c> form,
    ///         aligning the lookup key with enumeration entries and GetDirectoryName ancestors.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         仅用于查找/比较键（下降剪枝集合与祖先去重）；进入 payload、指纹与策略身份的
    ///         拒读项保持接收原样，不经此规范化。
    ///     </para>
    ///     <para>
    ///         Only for lookup/comparison keys (the descent-pruning set and the ancestor dedup);
    ///         deny entries that reach the payload, the fingerprint, or the policy identity keep
    ///         the received form and never pass through this normalization.
    ///     </para>
    /// </remarks>
    private static string NormalizeDenyLookupKey(string path) => Path.TrimEndingDirectorySeparator(path);

    /// <summary>
    ///     <para>判断候选路径自身或任一祖先是否已在拒读集合中（等价于被整目录 deny 覆盖）。</para>
    ///     <para>
    ///         Returns whether the candidate path itself or any ancestor is already in the deny
    ///         set (equivalent to whole-directory deny coverage).
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         集合元素与候选键均为规范化绝对路径，查找键按尾部分隔符裁剪口径对齐（见
    ///         <see cref="NormalizeDenyLookupKey" />）：沿 GetDirectoryName 逐级上溯做精确
    ///         （大小写不敏感）匹配，与逐条目 IsInside 扫描语义等价，复杂度为路径深度而非
    ///         集合大小。
    ///     </para>
    ///     <para>
    ///         Set elements and the candidate key are canonical absolute paths with lookup keys
    ///         aligned on the trailing-separator rule (see <see cref="NormalizeDenyLookupKey" />):
    ///         the walk compares each ancestor exactly (case-insensitive), which is equivalent to
    ///         a per-entry IsInside scan at path-depth cost instead of set-size cost.
    ///     </para>
    /// </remarks>
    private static bool IsCoveredByDenyAncestor(HashSet<string> denyCovered, string candidate)
    {
        // GetDirectoryName 的祖先不带尾分隔符，候选键在入口裁剪一次即可对齐查找口径。
        var current = NormalizeDenyLookupKey(candidate);
        while (current is not null)
        {
            if (denyCovered.Contains(current)) return true;

            current = Path.GetDirectoryName(current);
        }

        return false;
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
    ///         枚举一个根下的敏感名命中（目录段含末段、文件名与扩展名，大小写不敏感）。
    ///         重解析点只做词法判定、绝不下降；目录命中不再下降（其子树已随 deny 覆盖）；
    ///         已有整目录 deny 覆盖的子树在下降前剪枝（见 denyCoveredSubtreeRoots）。
    ///         条目预算按枚举条目计：null=默认 500k、正数=显式上限、负数=立即耗尽；时间预算
    ///         null=默认 10 分钟、0=不设、正数=显式上限、负数=立即超界；任一超界即抛出——
    ///         调用方 fail-closed，绝不返回未扫完的部分结果。不可读子树与不可检查条目使扫描
    ///         不可认证，同样 fail-closed 并丢弃已收集命中；仅已消失条目（并发删除竞态，
    ///         无暴露面）按词法判定处理。取消语义不变。时间检查在取消检查点同步执行：单次
    ///         MoveNext 同步阻塞期间取消与时间检查均不生效；内存上界随命中数增长
    ///         （typical 数千条）。
    ///     </para>
    ///     <para>
    ///         Enumerates sensitive-name hits under one root (directory segments including the
    ///         final one, file names, and extensions, case-insensitive). Reparse points are
    ///         judged lexically and never descended; hit directories are not descended (their
    ///         subtrees are already covered by the deny), and subtrees already covered by a
    ///         whole-directory deny entry are pruned before descent (see
    ///         denyCoveredSubtreeRoots). The entry budget counts enumerated entries: null
    ///         selects the default 500k, a positive value is an explicit cap, and a negative
    ///         value exhausts immediately; the time budget is null for the default 10 minutes,
    ///         0 to disable, a positive explicit cap, or negative to exceed immediately; any
    ///         overrun throws — the caller fails closed, never returning a partially scanned
    ///         result. Unreadable subtrees and uninspectable entries make the scan
    ///         unauthenticatable and fail closed the same way, discarding the collected hits;
    ///         only vanished entries (a concurrent-deletion race with no exposure surface) are
    ///         handled lexically. Cancellation semantics are unchanged. Time checks run
    ///         synchronously at the cancellation checkpoints: they do not fire while a single
    ///         MoveNext blocks synchronously, and memory grows with the hit count (typically a
    ///         few thousand entries).
    ///     </para>
    /// </summary>
    /// <param name="timeBudget">
    ///     <para>
    ///         墙钟时间预算：null=默认 10 分钟、0=不设时间预算、正数=显式上限、负数=立即超界
    ///         （fail-closed）；与条目预算的哨兵风格一致。
    ///     </para>
    ///     <para>
    ///         The wall-clock time budget: null for the default 10 minutes, 0 to disable the time
    ///         budget, a positive value for an explicit cap, or a negative value to exceed
    ///         immediately (fail-closed); the sentinel style matches the entry budget.
    ///     </para>
    /// </param>
    /// <param name="elapsedSinceStart">
    ///     <para>
    ///         测试注入时钟（返回自扫描阶段起点起算的实际耗时）；null 使用以本方法起点为原点
    ///         的真实墙钟。核心层传入与配对阶段共用的同一时钟，保证一份预算覆盖整个扫描阶段。
    ///     </para>
    ///     <para>
    ///         A test-injected clock (returning the elapsed time since the scan-phase start);
    ///         null uses the real wall clock anchored at this method's start. The core layer
    ///         passes the clock shared with the pairing phase so one budget covers the whole
    ///         scan phase.
    ///     </para>
    /// </param>
    /// <param name="denyCoveredSubtreeRoots">
    ///     <para>
    ///         已有整目录拒读覆盖的路径（规范化绝对路径）：下降前跳过被其覆盖的子树；null
    ///         表示不做剪枝（直接调用方默认）。内部命中已被祖先条目继承，剪枝不会丢失拒读
    ///         覆盖；未覆盖子树的枚举错误仍 fail-closed。
    ///     </para>
    ///     <para>
    ///         Paths already covered by a whole-directory deny entry (canonical absolute paths):
    ///         subtrees under them are skipped before descent; null disables pruning (the default
    ///         for direct callers). Their inner hits are inherited from the ancestor entry, so
    ///         pruning never loses deny coverage, and enumeration errors in uncovered subtrees
    ///         still fail closed.
    ///     </para>
    /// </param>
    internal static IReadOnlyList<string> EnumerateBaselineSensitiveNameHits(
        string root, int? budget = null,
        CancellationToken cancellationToken = default,
        Func<string, IEnumerable<string>>? enumerateEntries = null,
        TimeSpan? timeBudget = null,
        Func<TimeSpan>? elapsedSinceStart = null,
        IReadOnlyCollection<string>? denyCoveredSubtreeRoots = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = budget ?? BaselineSensitiveScanBudget;
        // 时间预算哨兵与条目预算同风格：null=默认 10 分钟、0=不设、正数=上限、负数=立即超界。
        // 时钟原点由调用方共享（核心层以同一预算覆盖枚举与配对）；测试可注入确定性时钟。
        var effectiveTimeBudget = timeBudget ?? BaselineSensitiveScanTimeBudget;
        var unlimitedTime = effectiveTimeBudget == TimeSpan.Zero;
        var scanStarted   = Stopwatch.GetTimestamp();
        Func<TimeSpan> elapsedSinceScanStart =
            elapsedSinceStart ?? (Func<TimeSpan>)(() => Stopwatch.GetElapsedTime(scanStarted));
        long entries = 0;
        var hits      = new List<string>();
        var stack     = new Stack<string>();
        stack.Push(Path.GetFullPath(root));

        // 已有整目录 deny 覆盖的子树在下降前剪枝：覆盖条目已在基线拒读集内，内部命中被祖先
        // 继承，重复下降只会把不可读的已保护子树变成不可认证失败面。下降自根逐级进行，覆盖
        // 条目必然先于其内部目录被精确命中（规范化段边界，a\b 与 a\bc 不会互判覆盖）。
        // 查找键与调用方的拒读集共用同一规范化口径：带尾分隔符的覆盖条目（如会话存储目录）
        // 否则与枚举返回的子目录永不相等，剪枝失效。
        var denyCovered = denyCoveredSubtreeRoots is { } coveredRoots
            ? new HashSet<string>(coveredRoots.Select(NormalizeDenyLookupKey),
                                  StringComparer.OrdinalIgnoreCase)
            : null;

        void ThrowIfOverTimeBudget()
        {
            if (unlimitedTime) return;

            var elapsed = elapsedSinceScanStart();
            if (elapsed <= effectiveTimeBudget) return;

            throw new
                InvalidOperationException($"The baseline sensitive-name scan exceeded its wall-clock time budget of {effectiveTimeBudget.TotalMilliseconds:F0} ms " +
                                          $"after enumerating {entries} entries under '{root}' (elapsed {elapsed.TotalMilliseconds:F0} ms); " +
                                          "the sensitive-name deny set cannot be certified on a partially scanned profile, so the baseline fails closed. " +
                                          "Check the read root for an unresponsive or network enumeration source to proceed.");
        }

        ThrowIfOverTimeBudget();
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfOverTimeBudget();
            var directory = stack.Pop();
            try
            {
                using var children =
                    (enumerateEntries?.Invoke(directory) ?? Directory.EnumerateFileSystemEntries(directory))
                   .GetEnumerator();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfOverTimeBudget();
                    if (!children.MoveNext()) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfOverTimeBudget();
                    var child = children.Current;
                    entries++;
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
                        // 已消失条目（并发删除竞态）无暴露面，无需保护：按词法判定（fail-safe
                        // 方向），不下降。
                        if (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            if (SensitivePathClassifier.IsKnownSensitivePath(child)) hits.Add(child);

                            continue;
                        }

                        // 条目存在但属性不可检查（权限或 IO）：其子树不可枚举=不可认证，
                        // 整个扫描 fail-closed，绝不吞掉返回部分命中。
                        throw new
                            InvalidOperationException($"The baseline sensitive-name scan could not inspect the entry '{child}' under the read root '{root}': {ex.Message}; " +
                                                      "an uninspectable entry cannot be authenticated, so the collected hits are discarded and the baseline fails closed. " +
                                                      "Restore read access to the entry to proceed.", ex);
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

                    // 已有整目录 deny 覆盖的子树不下降：覆盖条目先于其内部目录被精确命中
                    // （下降自根逐级进行），内部命中已被祖先条目继承；查询键与集合同口径。
                    if (denyCovered is not null &&
                        denyCovered.Contains(NormalizeDenyLookupKey(child))) continue;

                    if (isDirectory && !isReparse) stack.Push(child);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // 目录级枚举失败（含根目录自身与枚举初始化）：子树不可枚举=不可认证，整个扫描
                // fail-closed，绝不跳过子树继续；上方已包装的失败不命中此过滤器。
                throw new
                    InvalidOperationException($"The baseline sensitive-name scan could not enumerate the directory '{directory}' under the read root '{root}': {ex.Message}; " +
                                              "an unreadable subtree cannot be authenticated, so the collected hits are discarded and the baseline fails closed. " +
                                              "Restore read access to the directory to proceed.", ex);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfOverTimeBudget();
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

        // 可信组件保护：写根与元数据写路径不得覆盖本端组件、home、凭据目录、users 文件或共享 home 下
        // 已登记的另一端保护路径，否则沙箱内命令可以篡改执行基础设施。读根不在此列：沙箱组本就需要读取并执行组件。
        var protectedPaths = new List<string>
        {
            components.SetupExecutablePath, components.RunnerExecutablePath, components.SandboxHome,
            components.CredentialsDirectory, components.UsersFilePath
        };
        var registeredPaths         = SandboxProtectedPathsStore.ReadRegisteredPathsForHome(components.SandboxHome);
        var provisionLeaseDirectory = SandboxProvisionLeaseStore.ResolveDefaultLeaseDirectory();
        foreach (var root in writeRoots.Concat(metadataWrites))
        {
            var label = GrantRootLabel(root, readRoots, writeRoots, metadataWrites);
            foreach (var trustedPath in protectedPaths)
                if (Workspace.IsInside(root, trustedPath))
                    throw new
                        InvalidOperationException($"The {label} '{root}' covers the trusted " +
                                                  $"sandbox path '{trustedPath}'; sandboxed commands could tamper with it, refusing the grant scope.");

            if (OverlapsProtection(root, provisionLeaseDirectory))
                throw new
                    InvalidOperationException($"The {label} '{root}' overlaps the sandbox provisioning lease directory " +
                                              $"'{provisionLeaseDirectory}'; sandboxed commands could remove active " +
                                              "cross-process provisioning leases, refusing the grant scope.");

            ValidateSingleWriteRootAgainstRegisteredProtectedPaths(root, components.SandboxHome, registeredPaths,
                                                                   label);

            // C1 权限系统资源双向重叠：覆盖（改写保护文件/目录内容）与落入（在保护目录内部创建
            // 或改写条目）都拒绝；词法与最终形态都参与比对。
            if (permissionSystemPaths is null) continue;

            foreach (var protectedPath in permissionSystemPaths)
                if (OverlapsProtection(root, protectedPath))
                    throw new
                        InvalidOperationException($"The {label} '{root}' overlaps the permission-system " +
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
    internal static ISandboxDenyReadRefreshGateHandle? AcquireHomeProtectionGate(string sandboxHome)
    {
        if (!OperatingSystem.IsWindows()) return null;

        var gate = new GlobalDenyReadRefreshGate()
                  .TryAcquireAsync(sandboxHome, WindowsSandboxBackendLimits.Default.DenyReadRefreshGateTimeout,
                                   CancellationToken.None)
                  .ConfigureAwait(false)
                  .GetAwaiter()
                  .GetResult();
        return gate ?? throw new InvalidOperationException(
            $"Timed out waiting for the cross-process sandbox home gate for '{sandboxHome}' while synchronizing " +
            "shared protected paths; refusing the operation (fail closed).");
    }

    internal static void ValidateWriteRootsAgainstRegisteredProtectedPaths(
        IReadOnlyList<string> writeRoots, string sandboxHome, string rootLabel)
    {
        // 无论共享清单是否已存在，持久化提权租约目录始终属于受保护路径，不得与任何有效写根重叠。
        var provisionLeaseDirectory = SandboxProvisionLeaseStore.ResolveDefaultLeaseDirectory();
        foreach (var root in writeRoots)
            if (OverlapsProtection(root, provisionLeaseDirectory))
                throw new InvalidOperationException(
                    $"The {rootLabel} '{root}' overlaps the sandbox provisioning lease directory " +
                    $"'{provisionLeaseDirectory}'; sandboxed commands could remove active cross-process " +
                    "provisioning leases, refusing the operation.");

        var registeredPaths = SandboxProtectedPathsStore.ReadRegisteredPathsForHome(sandboxHome);
        if (registeredPaths.Count == 0) return;

        foreach (var root in writeRoots)
            ValidateSingleWriteRootAgainstRegisteredProtectedPaths(root, sandboxHome, registeredPaths, rootLabel);
    }

    private static void ValidateSingleWriteRootAgainstRegisteredProtectedPaths(
        string writeRoot, string sandboxHome, IReadOnlyList<string> registeredPaths, string rootLabel)
    {
        var fullHome  = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sandboxHome));
        var finalHome = SandboxFinalPathResolver.TryGetFinalPath(fullHome);
        var fullRoot  = Path.GetFullPath(writeRoot);
        var finalRoot = SandboxFinalPathResolver.TryGetFinalPath(fullRoot) ?? fullRoot;

        foreach (var registered in registeredPaths)
        {
            var fullRegistered = Path.TrimEndingDirectorySeparator(Path.GetFullPath(registered));
            var isHomeItself = string.Equals(fullRegistered, fullHome, StringComparison.OrdinalIgnoreCase) ||
                               (finalHome is not null &&
                                string.Equals(SandboxFinalPathResolver.TryGetFinalPath(fullRegistered),
                                              Path.TrimEndingDirectorySeparator(finalHome),
                                              StringComparison.OrdinalIgnoreCase));
            if (isHomeItself)
            {
                // 默认基础设施根（tmp/tools/nuget-cache）物理位于 sandboxHome 内，因此对 home 自身仅拒绝
                // 覆盖或等于 home 的写根，不反向拒绝 home 内的基础设施根。
                var finalRegistered = SandboxFinalPathResolver.TryGetFinalPath(fullRegistered) ?? fullRegistered;
                if (Workspace.IsInside(fullRoot, fullRegistered)  ||
                    Workspace.IsInside(fullRoot, finalRegistered) ||
                    Workspace.IsInside(finalRoot, fullRegistered) ||
                    Workspace.IsInside(finalRoot, finalRegistered))
                    throw new InvalidOperationException(
                        $"The {rootLabel} '{writeRoot}' covers the registered sandbox home '{registered}'; " +
                        "sandboxed commands could tamper with shared sandbox infrastructure, refusing the operation.");
                continue;
            }

            if (OverlapsProtection(fullRoot, fullRegistered))
                throw new InvalidOperationException(
                    $"The {rootLabel} '{writeRoot}' overlaps the shared-home registered protected path '{registered}'; " +
                    "another installation or permission-system asset sharing this sandbox home would become writable, " +
                    "refusing the operation.");
        }
    }

    internal static bool OverlapsProtection(string left, string right)
    {
        // 优先展开真实最终落点；路径尚未创建时按最近已存在祖先展开最终落点再拼回缺失尾部段，
        // 防止写根或尚未物化的保护目录通过祖先 junction/8.3 短名绕过重叠校验。
        // 在 Windows 上若任一侧最终落点无法确认（例如悬空 junction/symlink、无权限或不可解析卷），
        // 绝不回落词法路径放行，直接 fail-closed 视作重叠拒绝授权。
        var leftFinal = SandboxFinalPathResolver.TryGetFinalPath(left)
                        ?? GlobalDenyReadRefreshGate.TryResolveCanonicalPathAllowMissing(left);
        var rightFinal = SandboxFinalPathResolver.TryGetFinalPath(right)
                         ?? GlobalDenyReadRefreshGate.TryResolveCanonicalPathAllowMissing(right);
        if (OperatingSystem.IsWindows() && (leftFinal is null || rightFinal is null))
            return true;

        var effectiveLeft  = leftFinal  ?? left;
        var effectiveRight = rightFinal ?? right;
        return Workspace.IsInside(left, right)                   || Workspace.IsInside(right, left)                   ||
               Workspace.IsInside(effectiveLeft, effectiveRight) || Workspace.IsInside(effectiveRight, effectiveLeft) ||
               Workspace.IsInside(left, effectiveRight)          || Workspace.IsInside(effectiveRight, left)          ||
               Workspace.IsInside(effectiveLeft, right)          || Workspace.IsInside(right, effectiveLeft);
    }

    private static string PolicyKindDisplay(SandboxPolicyKind kind) => kind switch
    {
        SandboxPolicyKind.ReadOnly       => "read-only",
        SandboxPolicyKind.WorkspaceWrite => "workspace-write",
        _                                => kind.ToString()
    };
}
