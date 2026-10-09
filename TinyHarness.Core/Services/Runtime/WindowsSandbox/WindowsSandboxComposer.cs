using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     把可信的 WindowsSandboxSettings 组合成可执行要素。所有校验 fail closed：路径缺失、非绝对、
///     组件不存在或平台不符都抛带上下文的异常，由调用方终止运行；绝不静默回退宿主执行。网络上限
///     固定断网（策略与 offline 账户共同保证），此处不提供任何联网选项。
///     Composes trusted WindowsSandboxSettings into executable pieces. Every
///     validation fails closed: missing or non-absolute paths, absent components,
///     or a wrong platform throw contextual exceptions for the caller to stop the
///     run; host execution is never a silent fallback. The network ceiling is
///     fixed offline (enforced jointly by the policy and the offline account);
///     no networked option exists here.
/// </summary>
public static class WindowsSandboxComposer
{
    /// <summary>
    ///     本地策略身份语义版本；读写范围、环境或元数据保护的含义变化时递增，使旧授权身份失效。
    ///     The local policy identity semantics version; bump it whenever the meaning
    ///     of scopes, environment, or metadata protection changes so old identities expire.
    /// </summary>
    internal const int PolicyIdentityVersion = 1;

    /// <summary>
    ///     校验设置并组合执行要素。workspaceRoot 必须是绝对路径；knownSecrets 用于目标环境的
    ///     最后防线过滤。会创建沙箱临时写根目录（harness 自有基础设施，不属于目标命令副作用）。
    ///     Validates the settings and composes the execution pieces. workspaceRoot
    ///     must be absolute; knownSecrets feed the target environment's last-line
    ///     filtering. The sandbox temp write root directory is created here
    ///     (harness-owned infrastructure, not a target-command side effect).
    /// </summary>
    public static WindowsSandboxExecution Compose(WindowsSandboxSettings               settings,
                                                  string                               workspaceRoot,
                                                  IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new InvalidOperationException(
                                                $"The workspace root must be a fully qualified absolute path to enable the Windows sandbox: '{workspaceRoot}'.");

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                                                    "The Windows sandbox is enabled in the trusted user settings but this platform is not Windows; " +
                                                    "refusing to fall back to host execution. Disable settings.windowsSandbox.enabled to run host execution.");

        // Root/environment derivation is shared with the provisioning entry
        // point (WindowsSandboxPolicyPlanner) so refresh ACLs and provisioning
        // payloads can never drift apart.
        var plan = WindowsSandboxPolicyPlanner.Plan(settings, workspaceRoot, knownSecrets);

        // Trusted pieces must never be writable by the target command: reject
        // any of them inside the workspace or a declared additional write root.
        IReadOnlyList<string> writeRoots = [workspaceRoot, .. plan.AdditionalWriteRoots];
        RequireTrustedPathOutsideWriteRoots(plan.SetupExecutablePath, "setup executable", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.RunnerExecutablePath, "runner executable", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.SandboxHome, "sandbox home", writeRoots);
        RequireTrustedPathOutsideWriteRoots(plan.TempRoot, "sandbox temp root", writeRoots);

        if (!File.Exists(plan.SetupExecutablePath))
            throw new InvalidOperationException(
                                                $"The Windows sandbox setup executable was not found at '{plan.SetupExecutablePath}'; refusing to fall back to host execution.");

        if (!File.Exists(plan.RunnerExecutablePath))
            throw new InvalidOperationException(
                                                $"The Windows sandbox runner executable was not found at '{plan.RunnerExecutablePath}'; refusing to fall back to host execution.");

        if (!Directory.Exists(plan.SandboxHome))
            throw new InvalidOperationException(
                                                $"The Windows sandbox home was not found at '{plan.SandboxHome}'; run provisioning first. " +
                                                "Refusing to fall back to host execution.");

        Directory.CreateDirectory(plan.TempRoot);

        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = plan.SetupExecutablePath,
            RunnerExecutablePath = plan.RunnerExecutablePath,
            SandboxHome          = plan.SandboxHome
        };

        var executionPolicy = new ProcessExecutionPolicy
        {
            Backend        = ProcessExecutionBackendKind.WindowsSandbox,
            DisplayName    = $"windows-sandbox: {PolicyKindDisplay(settings.Policy)}, network: restricted",
            PolicyIdentity = BuildPolicyIdentity(plan.Policy, plan.TargetEnvironment)
        };

        var backend = new WindowsSandboxBackend(components, plan.Policy, plan.TargetEnvironment, knownSecrets);
        return new WindowsSandboxExecution(components, plan.Policy, plan.TargetEnvironment, executionPolicy, backend);
    }

    /// <summary>
    ///     绑定后端、策略语义版本、组件协议版本、有效读写范围、deny、网络、临时根、元数据保护和
    ///     目标环境哈希的规范化身份串。同一配置稳定；任何一项变化都会使旧会话授权不再匹配。
    ///     The normalized identity binding the backend, policy semantics version,
    ///     component protocol versions, effective read/write scopes, deny paths,
    ///     network, temp roots, metadata protection, and the target environment
    ///     hash. Stable for identical configuration; any change makes old session
    ///     grants stop matching.
    /// </summary>
    private static string BuildPolicyIdentity(SandboxIsolationPolicy              policy,
                                              IReadOnlyDictionary<string, string> targetEnvironment)
    {
        var canonical = new StringBuilder();
        canonical.Append("backend=windows-sandbox\n");
        canonical.Append("policyVersion=").Append(PolicyIdentityVersion).Append('\n');
        canonical.Append("kind=").Append(PolicyKindDisplay(policy.Kind)).Append('\n');
        canonical.Append("setupVersion=").Append(WindowsSandboxComponents.SetupVersion).Append('\n');
        canonical.Append("ipcVersion=").Append(WindowsSandboxComponents.IpcVersion).Append('\n');
        canonical.Append("workspace=").Append(SandboxCapabilitySidStore.CanonicalRootKey(policy.WorkspaceRoot))
                 .Append('\n');
        AppendRoots(canonical, "writeRoots", policy.EffectiveWriteRoots);
        AppendRoots(canonical, "denyWritePaths", policy.DenyWritePaths);
        AppendRoots(canonical, "tempRoots", policy.TempWriteRoots);
        canonical.Append("network=restricted\n");
        canonical.Append("metadataProtection=")
                 .Append(string.Join("|", SandboxIsolationPolicy.ProtectedMetadataSubpaths))
                 .Append('\n');
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
    ///     目标环境的稳定哈希：键排序（大小写不敏感）后的 name=value 行整体 SHA-256。策略身份由此
    ///     绑定环境策略身份——附加环境变量或临时根变化都会使旧授权失效。
    ///     A stable hash of the target environment: SHA-256 over the name=value
    ///     lines sorted by case-insensitive key. This binds the environment policy
    ///     identity — extra variables or a changed temp root expire old grants.
    /// </summary>
    private static string ComputeEnvironmentHash(IReadOnlyDictionary<string, string> targetEnvironment)
    {
        var lines = targetEnvironment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                                     .Select(pair => pair.Key + "=" + pair.Value);
        var canonical = string.Join("\n", lines);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    ///     可信路径落在任一可写根（工作区或额外写根）内时拒绝执行：沙箱内命令可能篡改该组件，
    ///     绝不回退宿主执行。
    ///     Rejects a trusted path that lies inside any writable root (the workspace
    ///     or an additional write root): sandboxed commands could tamper with it,
    ///     so host execution is never a fallback.
    /// </summary>
    private static void RequireTrustedPathOutsideWriteRoots(string                trustedPath,
                                                            string                label,
                                                            IReadOnlyList<string> writeRoots)
    {
        foreach (var root in writeRoots)
        {
            if (!Workspace.IsInside(root, trustedPath)) continue;

            throw new InvalidOperationException(
                                                $"The {label} '{trustedPath}' lies inside the writable root '{root}'; " +
                                                "sandboxed commands could tamper with it, refusing to fall back to host execution.");
        }
    }

    private static string PolicyKindDisplay(SandboxPolicyKind kind)
    {
        return kind switch
        {
            SandboxPolicyKind.ReadOnly       => "read-only",
            SandboxPolicyKind.WorkspaceWrite => "workspace-write",
            _                                => kind.ToString()
        };
    }
}
