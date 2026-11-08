using System.Globalization;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         从可信设置推导沙箱策略要素（组件绝对路径、tempRoot 路径、清理后的目标环境与有效隔离策略）的
///         共享规划器。组合器（WindowsSandboxComposer）与管理入口（WindowsSandboxProvisioner）共用同一份
///         推导，保证 refresh 与 provisioning 的 root/环境语义不会漂移。规划本身无副作用：它不创建任何
///         目录或文件，因此可在 provisioning 之前的机器上运行。
///     </para>
///     <para>
///         Shared planner that derives the sandbox policy pieces (absolute
///         component paths, the temp root path, the cleaned target environment, and
///         the effective isolation policy) from trusted settings. The composer
///         (WindowsSandboxComposer) and the management entry point
///         (WindowsSandboxProvisioner) consume the same derivation so refresh and
///         provisioning can never drift apart on roots or environment semantics.
///         Planning itself is side-effect free: it creates no directory or file and
///         therefore runs on machines that are not provisioned yet.
///     </para>
/// </summary>
internal static class WindowsSandboxPolicyPlanner
{
    /// <summary>
    ///     <para>
    ///         校验可信设置并推导策略要素。路径缺失/非绝对、额外写根或只读根非法、NUGET_PACKAGES
    ///         覆盖非绝对时抛带上下文的异常；workspaceRoot 必须是完全限定的绝对路径。
    ///         baselineReadProtection 携带基线广泛读根与 deny-read 保护集（GAP-1），形态已由
    ///         组合器校验。不触碰文件系统。
    ///     </para>
    ///     <para>
    ///         Validates the trusted settings and derives the policy pieces. Missing
    ///         or non-absolute paths, invalid additional write or read roots, and a
    ///         non-absolute NUGET_PACKAGES override throw contextual exceptions;
    ///         workspaceRoot must be a fully qualified absolute path. baselineReadProtection
    ///         carries the baseline broad-read root and the deny-read protection set (GAP-1),
    ///         whose shape the composer has already validated. The file system is never touched.
    ///     </para>
    /// </summary>
    internal static PolicyPlan Plan(
        WindowsSandboxSettings               settings,
        string                               workspaceRoot,
        IReadOnlyDictionary<string, string>? knownSecrets           = null,
        SandboxBaselineReadProtection?       baselineReadProtection = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new
                InvalidOperationException($"The workspace root must be a fully qualified absolute path to enable the Windows sandbox: '{workspaceRoot}'.");

        var setupPath   = RequireAbsoluteSetting(settings.SetupExecutablePath, "setupExecutablePath");
        var runnerPath  = RequireAbsoluteSetting(settings.RunnerExecutablePath, "runnerExecutablePath");
        // 空白 home 解析为独立的固定默认 home；显式值必须保持绝对（fail closed）。
        var sandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback);
        var (nugetCacheRoot, toolsRoot, tempRoot) = DeriveInfrastructureRoots(settings, sandboxHome);
        var extraRoots = SandboxPolicyResolver.NormalizeAdditionalWriteRoots(
        [
            nugetCacheRoot, toolsRoot, .. settings.AdditionalWriteRoots
        ], workspaceRoot, sandboxHome);

        var gitEnvironment = BuildGitEnvironment(settings.ExtraEnvironment, workspaceRoot, nugetCacheRoot);
        var targetEnvironment = WithToolsPath(SandboxTargetEnvironment.Build(tempRoot, gitEnvironment, knownSecrets),
                                              toolsRoot);
        var policy = SandboxPolicyResolver.Resolve(settings.Policy, workspaceRoot, targetEnvironment,
                                                   [nugetCacheRoot, toolsRoot, .. settings.AdditionalWriteRoots],
                                                   settings.AllowTcpLoopback,
                                                   settings.NetworkPolicy,
                                                   settings.AdditionalReadRoots,
                                                   sandboxHome,
                                                   baselineReadProtection?.ReadRoots,
                                                   baselineReadProtection?.DenyReadPaths);
        return new PolicyPlan
        {
            SetupExecutablePath  = setupPath,
            RunnerExecutablePath = runnerPath,
            SandboxHome          = sandboxHome,
            TempRoot             = tempRoot,
            NuGetCacheRoot       = nugetCacheRoot,
            ToolsRoot            = toolsRoot,
            AdditionalWriteRoots = extraRoots,
            TargetEnvironment    = targetEnvironment,
            Policy               = policy
        };
    }

    /// <summary>
    ///     <para>
    ///         派生沙箱基础设施写根：NuGet 缓存根（可信 NUGET_PACKAGES 覆盖或 home 派生）、
    ///         工具根（home\tools）与临时写根（可信设置或 home\tmp）。派生根先于用户声明根进入
    ///         resolver，与用户声明根一起接受凭据目录重叠校验，不绕过规范化。
    ///     </para>
    ///     <para>
    ///         Derives the sandbox infrastructure write roots: the NuGet cache root (a trusted
    ///         NUGET_PACKAGES override or derived from the home), the tools root (home\tools),
    ///         and the temp write root (a trusted setting or home\tmp). Derived roots enter the
    ///         resolver ahead of user-declared roots and go through the same credentials-overlap
    ///         validation, bypassing no normalization.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>纯推导，无副作用；组合器与管理入口共用，防止派生语义漂移。</para>
    ///     <para>
    ///         Pure derivation without side effects; shared by the composer and the provisioning
    ///         entry so the derivation can never drift.
    ///     </para>
    /// </remarks>
    internal static (string NuGetCacheRoot, string ToolsRoot, string TempRoot) DeriveInfrastructureRoots(
        WindowsSandboxSettings settings, string sandboxHome)
    {
        var tempRoot = string.IsNullOrWhiteSpace(settings.SandboxTempRoot)
            ? Path.Combine(sandboxHome, "tmp")
            : RequireAbsoluteSetting(settings.SandboxTempRoot, "sandboxTempRoot");
        var nugetCacheRoot = ResolveNuGetCacheRoot(settings, sandboxHome);
        var toolsRoot      = Path.Combine(sandboxHome, "tools");
        return (nugetCacheRoot, toolsRoot, tempRoot);
    }

    /// <summary>
    ///     <para>
    ///         解析用户显式配置的运行时根（temp 根与 NuGet 缓存根）：只有可信设置实际声明的条目
    ///         进入结果，默认派生根（home\tmp、home\nuget-cache）不在此列。识别逻辑与
    ///         <see cref="DeriveInfrastructureRoots" /> 同源，供组合器对用户配置根做组合期重叠校验。
    ///     </para>
    ///     <para>
    ///         Resolves the user-configured runtime roots (the temp root and the NuGet cache root): only
    ///         entries actually declared by trusted settings enter the result; the derived defaults
    ///         (home\tmp, home\nuget-cache) never do. The detection shares one source with
    ///         <see cref="DeriveInfrastructureRoots" /> so the composer can run its composition-time
    ///         overlap validation on user-configured roots only.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredInfrastructureRoots(WindowsSandboxSettings settings)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.SandboxTempRoot)) roots.Add(settings.SandboxTempRoot.Trim());

        if (settings.ExtraEnvironment is not null)
            foreach (var (name, value) in settings.ExtraEnvironment)
                if (name.Trim().Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                    roots.Add(value.Trim());

        return roots;
    }

    /// <summary>
    ///     <para>
    ///         解析 NuGet 缓存根：可信 extraEnvironment 的 NUGET_PACKAGES（绝对路径）优先，否则派生
    ///         &lt;sandboxHome&gt;\nuget-cache；非绝对覆盖 fail closed。
    ///     </para>
    ///     <para>
    ///         Resolves the NuGet cache root: a trusted extraEnvironment NUGET_PACKAGES
    ///         (absolute path) wins, otherwise &lt;sandboxHome&gt;\nuget-cache is derived;
    ///         a non-absolute override fails closed.
    ///     </para>
    /// </summary>
    private static string ResolveNuGetCacheRoot(WindowsSandboxSettings settings, string sandboxHome)
    {
        string? configured = null;
        if (settings.ExtraEnvironment is not null)
            foreach (var (name, value) in settings.ExtraEnvironment)
                if (name.Trim().Equals("NUGET_PACKAGES", StringComparison.OrdinalIgnoreCase))
                {
                    configured = value;
                    break;
                }

        if (configured is null) return Path.Combine(sandboxHome, "nuget-cache");

        if (string.IsNullOrWhiteSpace(configured) || !Path.IsPathFullyQualified(configured))
            throw new
                InvalidOperationException($"The trusted sandbox environment variable 'NUGET_PACKAGES' must be a fully qualified absolute path: '{configured}'.");

        return configured.Trim();
    }

    /// <summary>
    ///     <para>向目标环境的 PATH 追加工具根（大小写不敏感；缺失时以工具根为唯一 PATH）。</para>
    ///     <para>
    ///         Appends the tools root to the target environment's PATH
    ///         (case-insensitive; a missing PATH becomes the tools root alone).
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> WithToolsPath(
        IReadOnlyDictionary<string, string> targetEnvironment, string toolsRoot)
    {
        var environment = new Dictionary<string, string>(targetEnvironment, StringComparer.OrdinalIgnoreCase);
        environment["PATH"] = environment.TryGetValue("PATH", out var path)
            ? path + Path.PathSeparator + toolsRoot
            : toolsRoot;
        return environment;
    }

    /// <summary>
    ///     <para>
    ///         保留可信附加配置，以进程级 Git 配置精确信任当前工作区；默认 NuGet 缓存根先注入、可信
    ///         extraEnvironment 覆盖在后；畸形配置拒绝启动。
    ///     </para>
    ///     <para>
    ///         Preserves trusted extras and trusts the exact workspace through
    ///         process-scoped Git configuration; the default NuGet cache root is
    ///         injected first so trusted extras still override it; rejects malformed
    ///         configuration.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildGitEnvironment(
        IReadOnlyDictionary<string, string>? extras, string workspaceRoot, string nugetCacheRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NUGET_PACKAGES"] = nugetCacheRoot
        };
        if (extras is not null)
            foreach (var (name, value) in extras)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                environment[name.Trim()] = value;
            }

        var count = 0;
        if (environment.TryGetValue("GIT_CONFIG_COUNT", out var configuredCount) &&
            !string.IsNullOrEmpty(configuredCount)                               &&
            (!int.TryParse(configuredCount, NumberStyles.None, CultureInfo.InvariantCulture, out count) ||
             count > 1024))
            throw new InvalidOperationException("Sandbox GIT_CONFIG_COUNT must be an integer between 0 and 1024.");

        for (var index = 0; index < count; index++)
        {
            var suffix = index.ToString(CultureInfo.InvariantCulture);
            if (!environment.TryGetValue("GIT_CONFIG_KEY_" + suffix, out var key) ||
                string.IsNullOrEmpty(key)                                         ||
                !environment.ContainsKey("GIT_CONFIG_VALUE_" + suffix))
                throw new InvalidOperationException($"Sandbox Git configuration pair {suffix} is incomplete.");
        }

        // 清除系统、全局及附加配置中的旧信任列表，只自动信任本工作区，不包含子仓库通配符。
        var resetIndex     = count.ToString(CultureInfo.InvariantCulture);
        var workspaceIndex = (count + 1).ToString(CultureInfo.InvariantCulture);
        environment["GIT_CONFIG_KEY_"   + resetIndex]     = "safe.directory";
        environment["GIT_CONFIG_VALUE_" + resetIndex]     = "";
        environment["GIT_CONFIG_KEY_"   + workspaceIndex] = "safe.directory";
        environment["GIT_CONFIG_VALUE_" + workspaceIndex] =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot)).Replace('\\', '/');
        environment["GIT_CONFIG_COUNT"] = (count + 2).ToString(CultureInfo.InvariantCulture);
        return environment;
    }

    private static string RequireAbsoluteSetting(string? value, string fieldName) =>
        string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            ? throw new
                InvalidOperationException($"The trusted Windows sandbox setting '{fieldName}' must be a fully qualified absolute path" +
                                          (string.IsNullOrWhiteSpace(value) ? "." : $": '{value}'."))
            : value.Trim();

    /// <summary>
    ///     <para>
    ///         规划结果：已校验的组件绝对路径（去空白）、tempRoot/NuGet 缓存根/工具根绝对路径（不创建）、
    ///         规范化的有效额外写根（含派生根）、清理后的目标环境与有效隔离策略。
    ///     </para>
    ///     <para>
    ///         The plan: validated absolute component paths (trimmed), the absolute
    ///         temp root, NuGet cache root, and tools root (none created), the
    ///         normalized effective additional write roots (derived roots included),
    ///         the cleaned target environment, and the effective isolation policy.
    ///     </para>
    /// </summary>
    internal sealed record PolicyPlan
    {
        public required string SetupExecutablePath { get; init; }

        public required string RunnerExecutablePath { get; init; }

        public required string SandboxHome { get; init; }

        public required string TempRoot { get; init; }

        /// <summary>
        ///     <para>派生或可信覆盖的 NuGet 缓存根（绝对路径，组合器负责创建目录）。</para>
        ///     <para>
        ///         The derived or trusted-override NuGet cache root (absolute; the composer
        ///         creates the directory).
        ///     </para>
        /// </summary>
        public required string NuGetCacheRoot { get; init; }

        /// <summary>
        ///     <para>沙箱工具根 &lt;sandboxHome&gt;\tools（绝对路径，组合器负责创建目录）。</para>
        ///     <para>
        ///         The sandbox tools root &lt;sandboxHome&gt;\tools (absolute; the composer
        ///         creates the directory).
        ///     </para>
        /// </summary>
        public required string ToolsRoot { get; init; }

        public required IReadOnlyList<string> AdditionalWriteRoots { get; init; }

        public required IReadOnlyDictionary<string, string> TargetEnvironment { get; init; }

        public required SandboxIsolationPolicy Policy { get; init; }
    }
}
