using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     从可信设置推导沙箱策略要素（组件绝对路径、tempRoot 路径、清理后的目标环境与有效隔离策略）的
///     共享规划器。组合器（WindowsSandboxComposer）与管理入口（WindowsSandboxProvisioner）共用同一份
///     推导，保证 refresh 与 provisioning 的 root/环境语义不会漂移。规划本身无副作用：它不创建任何
///     目录或文件，因此可在 provisioning 之前的机器上运行。
///     Shared planner that derives the sandbox policy pieces (absolute
///     component paths, the temp root path, the cleaned target environment, and
///     the effective isolation policy) from trusted settings. The composer
///     (WindowsSandboxComposer) and the management entry point
///     (WindowsSandboxProvisioner) consume the same derivation so refresh and
///     provisioning can never drift apart on roots or environment semantics.
///     Planning itself is side-effect free: it creates no directory or file and
///     therefore runs on machines that are not provisioned yet.
/// </summary>
internal static class WindowsSandboxPolicyPlanner
{
    /// <summary>
    ///     规划结果：已校验的组件绝对路径（去空白）、tempRoot 绝对路径（不创建）、规范化的额外写根、
    ///     清理后的目标环境与有效隔离策略。
    ///     The plan: validated absolute component paths (trimmed), the absolute
    ///     temp root (not created), normalized additional write roots, the
    ///     cleaned target environment, and the effective isolation policy.
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

    /// <summary>
    ///     校验可信设置并推导策略要素。路径缺失/非绝对或额外写根相对时抛带上下文的异常；
    ///     workspaceRoot 必须是完全限定的绝对路径。不触碰文件系统。
    ///     Validates the trusted settings and derives the policy pieces. Missing
    ///     or non-absolute paths and relative additional write roots throw
    ///     contextual exceptions; workspaceRoot must be a fully qualified
    ///     absolute path. The file system is never touched.
    /// </summary>
    internal static PolicyPlan Plan(WindowsSandboxSettings               settings,
                                    string                               workspaceRoot,
                                    IReadOnlyDictionary<string, string>? knownSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new InvalidOperationException(
                                                $"The workspace root must be a fully qualified absolute path to enable the Windows sandbox: '{workspaceRoot}'.");

        var setupPath   = RequireAbsoluteSetting(settings.SetupExecutablePath, "setupExecutablePath");
        var runnerPath  = RequireAbsoluteSetting(settings.RunnerExecutablePath, "runnerExecutablePath");
        // A blank home resolves to the independent fixed default home; an
        // explicit value must stay absolute (fail closed).
        var sandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome);
        var extraRoots =
            SandboxPolicyResolver.NormalizeAdditionalWriteRoots(settings.AdditionalWriteRoots, workspaceRoot);

        var tempRoot = string.IsNullOrWhiteSpace(settings.SandboxTempRoot)
            ? Path.Combine(sandboxHome, "tmp")
            : RequireAbsoluteSetting(settings.SandboxTempRoot, "sandboxTempRoot");

        var targetEnvironment = SandboxTargetEnvironment.Build(tempRoot, settings.ExtraEnvironment, knownSecrets);
        var policy = SandboxPolicyResolver.Resolve(settings.Policy, workspaceRoot, targetEnvironment,
                                                   settings.AdditionalWriteRoots);
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

    private static string RequireAbsoluteSetting(string? value, string fieldName)
    {
        return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            ? throw new InvalidOperationException(
                                                  $"The trusted Windows sandbox setting '{fieldName}' must be a fully qualified absolute path" +
                                                  (string.IsNullOrWhiteSpace(value) ? "." : $": '{value}'."))
            : value.Trim();
    }
}
