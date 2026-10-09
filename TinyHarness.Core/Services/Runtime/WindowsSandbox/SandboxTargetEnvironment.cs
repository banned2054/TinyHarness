namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     构造沙箱目标命令的清理环境：只从宿主复制固定 allowlist（PATH、系统基础变量），把
///     TEMP/TMP/USERPROFILE/HOME 重定向到沙箱临时写根，再叠加可信设置的附加变量，最后移除
///     已知 secret 键。绝不继承全量宿主环境——沙箱执行的是不可信命令，宿主环境的其余部分
///     （凭据、内部主机名、任意工具变量）对其不可见。
///     Builds the cleaned environment for sandboxed target commands: it copies
///     only a fixed host allowlist (PATH and system basics), redirects
///     TEMP/TMP/USERPROFILE/HOME to the sandbox temp write root, appends the
///     trusted settings' extra variables, and finally removes known secret keys.
///     It never inherits the full host environment — the sandbox runs untrusted
///     commands, and the rest of the host environment (credentials, internal
///     hostnames, arbitrary tool variables) stays invisible to them.
/// </summary>
public static class SandboxTargetEnvironment
{
    /// <summary>
    ///     允许从宿主复制的变量名（大小写不敏感查找）。PATH 是命令发现所必需；其余是
    ///     Windows 程序普遍依赖的系统基础。任何其他宿主变量都必须经可信设置的
    ///     extraEnvironment 显式声明。
    ///     Variable names allowed to be copied from the host (case-insensitive
    ///     lookup). PATH is required for command discovery; the rest are system
    ///     basics most Windows programs depend on. Any other host variable must be
    ///     declared explicitly through the trusted settings' extraEnvironment.
    /// </summary>
    private static readonly string[] HostAllowList =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE"
    ];

    /// <summary>
    ///     构造目标环境。tempRoot 必须是已存在的绝对临时写根（由组合器创建）；附加变量覆盖
    ///     同名默认项；knownSecrets 的键在最后移除，保证密钥值不进入 spawn 请求。
    ///     Builds the target environment. tempRoot must be an existing absolute
    ///     temp write root (created by the composer); extra variables override
    ///     same-named defaults; knownSecrets keys are removed last so secret
    ///     values never reach the spawn request.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Build(
        string                               tempRoot,
        IReadOnlyDictionary<string, string>? extraVariables = null,
        IReadOnlyDictionary<string, string>? knownSecrets   = null)
    {
        if (string.IsNullOrWhiteSpace(tempRoot) || !Path.IsPathFullyQualified(tempRoot))
            throw new ArgumentException($"The sandbox temp root must be a fully qualified absolute path: '{tempRoot}'.",
                                        nameof(tempRoot));

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in HostAllowList)
        {
            var value                                           = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value)) environment[name] = value;
        }

        // Temp and profile lookups stay inside the sandbox temp write root: the
        // host user's TEMP and profile must not become writable (or readable
        // beyond policy) for untrusted commands, and tools resolving ~ land in
        // a contained, policy-declared location.
        environment["TEMP"]        = tempRoot;
        environment["TMP"]         = tempRoot;
        environment["USERPROFILE"] = tempRoot;
        environment["HOME"]        = tempRoot;

        if (extraVariables is not null)
            foreach (var (name, value) in extraVariables)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("An extra environment variable name must be a non-empty string.",
                                                nameof(extraVariables));

                environment[name.Trim()] = value;
            }

        // Trusted extras win over host values, but known secret keys never do:
        // remove them last so an API-key environment variable can never leak
        // into the spawn request regardless of how it was named.
        if (knownSecrets is null) return environment;
        foreach (var secret in knownSecrets)
            environment.Remove(secret.Key);

        return environment;
    }
}
