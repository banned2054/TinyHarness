namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>构造沙箱目标环境：白名单复制系统变量，用户目录默认限制在临时根内，可信附加变量覆盖后移除已知密钥。</para>
///     <para>
///         Builds the sandbox target environment: copies allowlisted system variables, defaults user directories
///         to the temp root, applies trusted overrides, then removes known secret keys.
///     </para>
/// </summary>
public static class SandboxTargetEnvironment
{
    /// <summary>
    ///     <para>只复制命令发现和系统路径基础变量；其他宿主变量须经可信 extraEnvironment 显式声明。</para>
    ///     <para>
    ///         Copies only command discovery and system path basics; other host variables require explicit
    ///         declaration through trusted extraEnvironment settings.
    ///     </para>
    /// </summary>
    private static readonly string[] HostAllowList =
    [
        "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
        "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "ProgramData"
    ];

    /// <summary>
    ///     <para>从绝对临时根构造目标环境；只推导路径，不创建目录，供策略规划与执行共用。</para>
    ///     <para>
    ///         Builds the target environment from an absolute temp root; derives paths without creating
    ///         directories so policy planning and execution share the same environment.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>可信附加变量覆盖同名默认项；knownSecrets 的键最后移除，即使覆盖默认目录也不额外授予读写根。</para>
    ///     <para>
    ///         Trusted extras override defaults; knownSecrets keys are removed last. Overriding a default
    ///         directory does not itself grant additional read or write roots.
    ///     </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Build(string                               tempRoot,
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

        // NuGet 在 Windows 上直接读取 APPDATA/LOCALAPPDATA；home 则优先使用 DOTNET_CLI_HOME，
        // 否则调用账户的已知文件夹 API，不能只依赖 USERPROFILE 环境变量重定向。
        environment["TEMP"]            = tempRoot;
        environment["TMP"]             = tempRoot;
        environment["USERPROFILE"]     = tempRoot;
        environment["HOME"]            = tempRoot;
        environment["DOTNET_CLI_HOME"] = tempRoot;
        environment["APPDATA"]         = Path.Combine(tempRoot, "AppData", "Roaming");
        environment["LOCALAPPDATA"]    = Path.Combine(tempRoot, "AppData", "Local");

        if (extraVariables is not null)
            foreach (var (name, value) in extraVariables)
            {
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("An extra environment variable name must be a non-empty string.",
                                                nameof(extraVariables));

                environment[name.Trim()] = value;
            }

        // 密钥键最后移除，可信覆盖也不能把它们带入 spawn 请求。
        if (knownSecrets is null) return environment;
        foreach (var secret in knownSecrets)
            environment.Remove(secret.Key);

        return environment;
    }
}
