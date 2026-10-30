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
    ///         校验可信设置并推导策略要素。路径缺失/非绝对或额外写根相对时抛带上下文的异常；
    ///         workspaceRoot 必须是完全限定的绝对路径。不触碰文件系统。
    ///     </para>
    ///     <para>
    ///         Validates the trusted settings and derives the policy pieces. Missing
    ///         or non-absolute paths and relative additional write roots throw
    ///         contextual exceptions; workspaceRoot must be a fully qualified
    ///         absolute path. The file system is never touched.
    ///     </para>
    /// </summary>
    internal static PolicyPlan Plan(
        WindowsSandboxSettings settings, string workspaceRoot, IReadOnlyDictionary<string, string>? knownSecrets = null)
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
        var extraRoots =
            SandboxPolicyResolver.NormalizeAdditionalWriteRoots(settings.AdditionalWriteRoots, workspaceRoot);

        var tempRoot = string.IsNullOrWhiteSpace(settings.SandboxTempRoot)
            ? Path.Combine(sandboxHome, "tmp")
            : RequireAbsoluteSetting(settings.SandboxTempRoot, "sandboxTempRoot");

        var gitEnvironment    = BuildGitEnvironment(settings.ExtraEnvironment, workspaceRoot);
        var targetEnvironment = SandboxTargetEnvironment.Build(tempRoot, gitEnvironment, knownSecrets);
        var policy = SandboxPolicyResolver.Resolve(settings.Policy, workspaceRoot, targetEnvironment,
                                                   settings.AdditionalWriteRoots, settings.AllowTcpLoopback);
        return new PolicyPlan
        {
            SetupExecutablePath  = setupPath,
            RunnerExecutablePath = runnerPath,
            SandboxHome          = sandboxHome,
            TempRoot             = tempRoot,
            AdditionalWriteRoots = extraRoots,
            TargetEnvironment    = targetEnvironment,
            Policy               = policy
        };
    }

    /// <summary>
    ///     <para>保留可信附加配置，以进程级 Git 配置精确信任当前工作区；畸形配置拒绝启动。</para>
    ///     <para>
    ///         Preserves trusted extras and trusts the exact workspace through process-scoped Git configuration; rejects
    ///         malformed configuration.
    ///     </para>
    /// </summary>
    private static IReadOnlyDictionary<string, string> BuildGitEnvironment(
        IReadOnlyDictionary<string, string>? extras, string workspaceRoot)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
    ///         规划结果：已校验的组件绝对路径（去空白）、tempRoot 绝对路径（不创建）、规范化的额外写根、
    ///         清理后的目标环境与有效隔离策略。
    ///     </para>
    ///     <para>
    ///         The plan: validated absolute component paths (trimmed), the absolute
    ///         temp root (not created), normalized additional write roots, the
    ///         cleaned target environment, and the effective isolation policy.
    ///     </para>
    /// </summary>
    internal sealed record PolicyPlan
    {
        public required string SetupExecutablePath { get; init; }

        public required string RunnerExecutablePath { get; init; }

        public required string SandboxHome { get; init; }

        public required string TempRoot { get; init; }

        public required IReadOnlyList<string> AdditionalWriteRoots { get; init; }

        public required IReadOnlyDictionary<string, string> TargetEnvironment { get; init; }

        public required SandboxIsolationPolicy Policy { get; init; }
    }
}
