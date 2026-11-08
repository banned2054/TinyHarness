using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         组合授权指纹的单一事实来源：对"决定实际授权及执行边界的配置"计算规范化 SHA-256 指纹，
///         供组合点捕获与守卫取值器重算共用同一函数，防止两端漂移。
///     </para>
///     <para>
///         The single source of truth for composition authorization fingerprints: a normalized SHA-256
///         over the configuration that decides actual authorization and execution boundaries, shared by
///         the composition capture and the guard's re-evaluation so the two sides cannot drift.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         覆盖成分：常设信任三态（含确认记录一致性）、生效网络策略、原始附加读写根（Confirmed 时即
///         生效根；未确认时是资源策略分类的消费输入）、resolved 沙箱 home、组件路径、代理策略及其
///         命名/应用默认代理解析输入、派生运行时写根、基线广泛读根与 deny 集、受保护会话存储目录、
///         setup/ipc 协议版本与沙箱组合标志。模型列表、审批模式选择、运行预算、超时等无关用户配置
///         不进入指纹；这些变化不触发会话失效。
///     </para>
///     <para>
///         Covered inputs: the standing-trust state (including confirmation-record consistency), the
///         effective network policy, the raw additional read/write roots (the effective roots when
///         Confirmed; otherwise the resource policy's classification inputs), the resolved sandbox home,
///         component paths, the proxy policy with its named and app-default resolution inputs, derived
///         runtime write roots, the baseline broad-read root and deny set, the protected session-store
///         directory, setup/ipc protocol versions and the sandbox composition flag. Unrelated user
///         configuration such as model catalogs, approval-mode selections, budgets and timeouts never
///         enters the fingerprint; those changes do not invalidate a session.
///     </para>
/// </remarks>
public static class PermissionCompositionFingerprint
{
    /// <summary>
    ///     <para>指纹规范化格式版本；格式语义变化时递增，使旧指纹整体失效。</para>
    ///     <para>
    ///         The canonical format version of the fingerprint; increment on semantic format changes so
    ///         old fingerprints expire wholesale.
    ///     </para>
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>
    ///     <para>
    ///         计算组合授权指纹。常设信任与生效网络策略由 <see cref="StandingTrustEvaluation" /> 的同一
    ///         纯函数派生；运行时根与基线读保护仅在沙箱实际组合时计算，与组合点真实消费范围一致。
    ///         输入派生抛出（配置非法、受保护路径缺失等）时异常向上传播，调用方按失败处理。
    ///     </para>
    ///     <para>
    ///         Computes the composition authorization fingerprint. The standing-trust state and the
    ///         effective network policy derive through the same pure functions of
    ///         <see cref="StandingTrustEvaluation" />; runtime roots and the baseline read protection are
    ///         computed only while the sandbox was actually composed, matching the composition's real
    ///         consumption. Input derivation that throws (invalid configuration, missing protected paths)
    ///         propagates; callers treat it as a failure.
    ///     </para>
    /// </summary>
    /// <param name="sandboxSettings">
    ///     <para>可信用户配置的 settings.windowsSandbox 原始值；null 表示未配置沙箱。</para>
    ///     <para>
    ///         The raw settings.windowsSandbox from the trusted user config; null when no sandbox is
    ///         configured.
    ///     </para>
    /// </param>
    /// <param name="permissionSettings">
    ///     <para>可信用户配置的 settings.permissions，提供常设信任确认记录。</para>
    ///     <para>
    ///         The trusted settings.permissions supplying the standing-trust confirmation record.
    ///     </para>
    /// </param>
    /// <param name="networkSettings">
    ///     <para>可信用户配置的 settings.network，供沙箱代理解析输入进入指纹。</para>
    ///     <para>
    ///         The trusted settings.network feeding the sandbox proxy resolution inputs into the
    ///         fingerprint.
    ///     </para>
    /// </param>
    /// <param name="workspaceRoot">
    ///     <para>会话工作区根（组合期固定），参与工作区与存储目录解析。</para>
    ///     <para>The session workspace root (fixed at composition) feeding workspace and store resolution.</para>
    /// </param>
    /// <param name="sandboxComposed">
    ///     <para>组合点是否实际组合了沙箱（决定引擎的进程执行可用性与运行时根消费）。</para>
    ///     <para>
    ///         Whether the composition actually composed the sandbox (deciding process-execution
    ///         availability and runtime-root consumption).
    ///     </para>
    /// </param>
    /// <param name="protectedSecretPaths">
    ///     <para>
    ///         基线广泛读保护的 C1 宿主密钥路径；与组合点传给
    ///         <see cref="WindowsSandboxComposer.PlanBaselineReadProtection" /> 的列表同源。
    ///     </para>
    ///     <para>
    ///         The baseline broad-read protection's C1 host secret paths; same source as the list the
    ///         composition passes to <see cref="WindowsSandboxComposer.PlanBaselineReadProtection" />.
    ///     </para>
    /// </param>
    /// <param name="configuredSessionStoreDirectory">
    ///     <para>
    ///         settings.sessionStoreDirectory 原始配置值；非空时经
    ///         <see cref="SessionStorePaths.ResolveProtected" /> 解析后进入指纹。
    ///     </para>
    ///     <para>
    ///         The raw settings.sessionStoreDirectory value; when set, resolved through
    ///         <see cref="SessionStorePaths.ResolveProtected" /> before entering the fingerprint.
    ///     </para>
    /// </param>
    public static string Compute(
        WindowsSandboxSettings? sandboxSettings,
        PermissionSettings?     permissionSettings,
        NetworkSettings?        networkSettings,
        string                  workspaceRoot,
        bool                    sandboxComposed,
        IReadOnlyList<string>   protectedSecretPaths,
        string?                 configuredSessionStoreDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);

        // 常设信任与生效网络策略由同一纯函数派生：组合点与守卫重算共享同一事实来源，不各自推断。
        var standingTrust = StandingTrustEvaluation.Evaluate(sandboxSettings, permissionSettings);
        var effectivePolicy = StandingTrustEvaluation.EffectiveSandboxSettings(sandboxSettings, standingTrust)
                                                    ?.NetworkPolicy ?? SandboxNetworkPolicy.Restricted;
        var storeDirectory = string.IsNullOrWhiteSpace(configuredSessionStoreDirectory)
            ? null
            : SessionStorePaths.ResolveProtected(configuredSessionStoreDirectory, root);

        var canonical = new StringBuilder();
        canonical.Append("schema=").Append(SchemaVersion).Append('\n');
        canonical.Append("workspace=").Append(root).Append('\n');
        canonical.Append("sandboxComposed=").Append(sandboxComposed ? '1' : '0').Append('\n');
        canonical.Append("standingTrust=").Append(standingTrust).Append('\n');
        canonical.Append("standingNetwork=")
                 .Append(effectivePolicy == SandboxNetworkPolicy.Enabled ? '1' : '0')
                 .Append('\n');
        canonical.Append("ipcVersion=").Append(WindowsSandboxComponents.IpcVersion).Append('\n');
        canonical.Append("setupVersion=")
                 .Append(sandboxSettings is { AllowTcpLoopback: true }
                             ? WindowsSandboxComponents.TcpLoopbackSetupVersion
                             : WindowsSandboxComponents.SetupVersion)
                 .Append('\n');
        AppendSandbox(canonical, sandboxSettings, networkSettings, sandboxComposed, protectedSecretPaths,
                      storeDirectory);
        if (storeDirectory is not null)
            canonical.Append("sessionStore=").Append(storeDirectory).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>
    ///     <para>
    ///         构造守卫取值器：每次调用重读该路径的可信用户配置，按 <see cref="Compute" /> 重算指纹。
    ///         读取、解析或派生失败时异常向上传播，由守卫按失配处理；权限、常设信任与沙箱边界
    ///         始终来自该可信配置，项目配置不能覆盖。
    ///     </para>
    ///     <para>
    ///         Builds the guard's fingerprint provider: every invocation re-reads the trusted user config
    ///         at this path and recomputes through <see cref="Compute" />. Read, parse or derivation
    ///         failures propagate for the guard to treat as a mismatch; permissions, standing trust and
    ///         sandbox boundaries always come from that trusted config, never overridden by project
    ///         configuration.
    ///     </para>
    /// </summary>
    /// <param name="userConfigPath">
    ///     <para>组合点实际消费的可信用户配置文件绝对路径。</para>
    ///     <para>The absolute path of the trusted user config the composition actually consumed.</para>
    /// </param>
    /// <param name="workspaceRoot">
    ///     <para>组合期固定的工作区根，与捕获指纹一致。</para>
    ///     <para>The workspace root fixed at composition, matching the captured fingerprint.</para>
    /// </param>
    /// <param name="sandboxComposed">
    ///     <para>组合期捕获的沙箱组合标志（机器级结果，不从配置重推导）。</para>
    ///     <para>
    ///         The sandbox composition flag captured at composition time (a machine-level result, not
    ///         re-derived from config).
    ///     </para>
    /// </param>
    /// <param name="protectedSecretPaths">
    ///     <para>组合期捕获的 C1 宿主密钥路径列表。</para>
    ///     <para>The C1 host secret paths captured at composition time.</para>
    /// </param>
    /// <param name="trackSessionStoreDirectory">
    ///     <para>组合点是否消费了会话存储目录；true 时取值器从重读配置重新解析该目录。</para>
    ///     <para>
    ///         Whether the composition consumed a session-store directory; when true the provider
    ///         re-resolves it from the re-read config.
    ///     </para>
    /// </param>
    public static Func<string?> CreateEvaluator(
        string                userConfigPath,
        string                workspaceRoot,
        bool                  sandboxComposed,
        IReadOnlyList<string> protectedSecretPaths,
        bool                  trackSessionStoreDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userConfigPath);
        return () =>
        {
            // 每次取值重读配置（LLM 节奏秒级，可接受）；同步桥接仅发生在无同步上下文的执行线程。
            var user = UserConfigStore.LoadAsync(userConfigPath, CancellationToken.None)
                                      .ConfigureAwait(false).GetAwaiter().GetResult();
            return Compute(user.Settings?.WindowsSandbox,
                           user.Settings?.Permissions,
                           user.Settings?.Network,
                           workspaceRoot,
                           sandboxComposed,
                           protectedSecretPaths,
                           trackSessionStoreDirectory ? user.Settings?.SessionStoreDirectory : null);
        };
    }

    /// <summary>
    ///     <para>追加沙箱边界成分；未配置沙箱时只写占位行，不推导任何派生路径。</para>
    ///     <para>
    ///         Appends the sandbox boundary inputs; without a configured sandbox only a placeholder line
    ///         is written and no derived path is computed.
    ///     </para>
    /// </summary>
    private static void AppendSandbox(
        StringBuilder           canonical,
        WindowsSandboxSettings? settings,
        NetworkSettings?        networkSettings,
        bool                    sandboxComposed,
        IReadOnlyList<string>   protectedSecretPaths,
        string?                 storeDirectory)
    {
        if (settings is null)
        {
            canonical.Append("sandbox=none\n");
            return;
        }

        canonical.Append("sandbox.enabled=").Append(settings.Enabled ? '1' : '0').Append('\n');
        canonical.Append("sandbox.policy=").Append(settings.Policy).Append('\n');
        canonical.Append("sandbox.tcpLoopback=").Append(settings.AllowTcpLoopback ? '1' : '0').Append('\n');
        canonical.Append("sandbox.setup=").Append(settings.SetupExecutablePath).Append('\n');
        canonical.Append("sandbox.runner=").Append(settings.RunnerExecutablePath).Append('\n');
        canonical.Append("sandbox.home=")
                 .Append(WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome,
                                                                     settings.AllowTcpLoopback))
                 .Append('\n');
        canonical.Append("sandbox.tempRoot=").Append(settings.SandboxTempRoot ?? "-").Append('\n');
        AppendRoots(canonical, "readRoots", settings.AdditionalReadRoots);
        AppendRoots(canonical, "writeRoots", settings.AdditionalWriteRoots);
        AppendEnvironment(canonical, settings.ExtraEnvironment);
        AppendProxy(canonical, settings.Proxy, networkSettings);

        // 运行时根与基线读保护仅在沙箱实际组合时进入指纹：与组合点真实消费范围一致，
        // 未组合（未信任工作区等）时保持占位，不因不可达的派生输入制造失配。
        if (!sandboxComposed)
        {
            canonical.Append("runtimeRoots=none\n");
            canonical.Append("baselineReadRoot=-\n");
            canonical.Append("baselineDeny=none\n");
            return;
        }

        AppendRoots(canonical, "runtimeRoots", WindowsSandboxComposer.DeriveRuntimeWriteRoots(settings));
        // 指纹显式传空敏感名扫描：扫描命中是组合期文件系统快照而非配置成分；逐次重扫会因
        // 会话中途的文件增删制造指纹失配，误撤销授权并闩死会话。快照漂移由策略身份（含
        // denyReadPaths 段）在授权复核层自然表达，不属于配置指纹的职责。
        // The fingerprint passes an empty sensitive-name scan explicitly: scan hits are a
        // composition-time filesystem snapshot, not a configuration input; rescanning per
        // evaluation would let mid-session file churn fake a fingerprint mismatch, revoking
        // grants and latching the session. Snapshot drift is expressed naturally by the policy
        // identity (carrying the denyReadPaths section) at the revalidation layer, which is not
        // the configuration fingerprint's responsibility.
        var baseline = WindowsSandboxComposer.PlanBaselineReadProtection(protectedSecretPaths, storeDirectory,
                                                                         settings,
                                                                         static _ => []);
        canonical.Append("baselineReadRoot=").Append(baseline?.ReadRoots.Count == 1
                                                         ? baseline.ReadRoots[0]
                                                         : "-")
                 .Append('\n');
        AppendRoots(canonical, "baselineDeny", baseline?.DenyReadPaths ?? []);
    }

    /// <summary>追加沙箱代理策略及其配置可解析的代理定义（凭据引用不进入指纹）。</summary>
    private static void AppendProxy(StringBuilder canonical, SandboxProxyPolicy proxy, NetworkSettings? network)
    {
        canonical.Append("proxy.mode=").Append(proxy.Mode).Append('\n');
        if (!string.IsNullOrWhiteSpace(proxy.ProxyName))
            canonical.Append("proxy.name=").Append(proxy.ProxyName).Append('\n');

        switch (proxy.Mode)
        {
            case SandboxProxyMode.Named :
                // 命名代理解析失败时组合器 fail closed；这里如实注记 missing，配置修复即产生新指纹。
                var named = (network?.Proxies ?? [])
                           .Where(candidate =>
                                      string.Equals(candidate.Name, proxy.ProxyName,
                                                    StringComparison.OrdinalIgnoreCase))
                           .ToArray();
                canonical.Append("proxy.definition=")
                         .Append(named.Length == 1
                                     ? $"{named[0].Protocol}://{named[0].Host}:{named[0].Port}"
                                     : "missing")
                         .Append('\n');
                break;
            case SandboxProxyMode.FollowAppDefault :
                var route = network?.DefaultRoute;
                canonical.Append("proxy.appDefault=")
                         .Append(route is null ? "-" : $"{route.Mode}:{route.ProxyId ?? "-"}")
                         .Append('\n');
                if (route?.ProxyId is { } proxyId)
                {
                    var referenced = network?.Proxies
                                             .Where(candidate => string.Equals(candidate.Id, proxyId,
                                                                               StringComparison.Ordinal))
                                             .ToArray();
                    if (referenced is { Length: 1 })
                        canonical.Append("proxy.appDefaultDefinition=")
                                 .Append($"{referenced[0].Protocol}://{referenced[0].Host}:{referenced[0].Port}")
                                 .Append('\n');
                }

                break;
        }
    }

    /// <summary>追加显式目标命令环境（键值排序规范化；密钥过滤属于执行期行为，不在指纹内）。</summary>
    private static void AppendEnvironment(StringBuilder canonical, IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is not { Count: > 0 })
        {
            canonical.Append("sandbox.environment=none\n");
            return;
        }

        canonical.Append("sandbox.environment=")
                 .Append(string.Join("|", environment.Select(pair => $"{pair.Key}={pair.Value}")
                                                     .OrderBy(item => item, StringComparer.Ordinal)))
                 .Append('\n');
    }

    /// <summary>
    ///     追加根集合：按 <see cref="SandboxCapabilitySidStore.CanonicalRootKey" /> 的同一规范
    ///     （规范化绝对路径、分隔符与大小写归一）去重排序，与组合器策略身份的根规范一致。
    /// </summary>
    private static void AppendRoots(StringBuilder canonical, string name, IReadOnlyList<string> roots)
    {
        canonical.Append(name).Append('=');
        if (roots.Count == 0)
        {
            canonical.Append("none").Append('\n');
            return;
        }

        var normalized = roots.Select(SandboxCapabilitySidStore.CanonicalRootKey)
                              .Distinct(StringComparer.Ordinal)
                              .OrderBy(path => path, StringComparer.Ordinal);
        canonical.Append(string.Join("|", normalized)).Append('\n');
    }
}
