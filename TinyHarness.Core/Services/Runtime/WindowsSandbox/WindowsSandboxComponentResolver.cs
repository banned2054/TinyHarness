using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>Windows 沙箱组件种类。</para>
///     <para>The kind of Windows sandbox executable component.</para>
/// </summary>
public enum WindowsSandboxComponentKind
{
    Setup,
    Runner
}

/// <summary>
///     <para>沙箱组件路径的来源：随当前安装包默认定位，或来自可信用户配置的显式绝对路径覆盖。</para>
///     <para>
///         The source of a sandbox component path: bundled beside the current installation, or an explicit
///         absolute-path override from trusted user settings.
///     </para>
/// </summary>
public enum WindowsSandboxComponentSource
{
    Bundled,
    Configured
}

/// <summary>
///     <para>单个沙箱组件的路径推导结果（纯路径计算，不触碰文件系统）。</para>
///     <para>Path-derivation result for a single sandbox component (pure path derivation without filesystem I/O).</para>
/// </summary>
public sealed record WindowsSandboxResolvedComponent
{
    public required WindowsSandboxComponentKind   Kind            { get; init; }
    public required string                        SettingKey      { get; init; }
    public required string                        FileName        { get; init; }
    public required WindowsSandboxComponentSource Source          { get; init; }
    public          string?                       ConfiguredValue { get; init; }
    public          string?                       CandidatePath   { get; init; }
    public          string?                       Error           { get; init; }

    public bool IsValid => Error is null && CandidatePath is not null;

    public string SourceDisplayZh => Source == WindowsSandboxComponentSource.Bundled ? "当前安装包默认" : "自定义路径";
}

/// <summary>
///     <para>沙箱组件路径的成对推导结果（setup、runner 及与有效 runner 同目录的 deny probe）。</para>
///     <para>
///         Paired path-derivation result for the sandbox components (setup, runner, and the deny probe
///         beside the effective runner).
///     </para>
/// </summary>
public sealed record WindowsSandboxComponentResolution
{
    public required string                        AppBaseDirectory { get; init; }
    public required WindowsSandboxResolvedComponent Setup          { get; init; }
    public required WindowsSandboxResolvedComponent Runner         { get; init; }

    public bool IsValid => Setup.IsValid && Runner.IsValid;

    public string? EffectiveProbePath => Runner.CandidatePath is null
        ? null
        : Path.Combine(Path.GetDirectoryName(Runner.CandidatePath)!,
                       WindowsSandboxComponentResolver.DenyProbeExecutableFileName);
}

/// <summary>
///     <para>单个沙箱组件的只读文件系统诊断结果。</para>
///     <para>Read-only filesystem inspection result for a single sandbox component.</para>
/// </summary>
public sealed record WindowsSandboxComponentInspection
{
    public required WindowsSandboxResolvedComponent Resolved                   { get; init; }
    public required bool                            Exists                     { get; init; }
    public          string?                         FinalPath                  { get; init; }
    public          SandboxFileIdentity?            Identity                   { get; init; }
    public required bool                            IsReleasePackageBundled    { get; init; }
    public          string?                         Error                      { get; init; }

    public bool IsUsable => Error is null && Exists;
}

/// <summary>
///     <para>deny-probe 辅助组件的只读诊断结果（始终定位在有效 runner 同目录）。</para>
///     <para>Read-only inspection result for the deny-probe executable (always beside the effective runner).</para>
/// </summary>
public sealed record WindowsSandboxProbeInspection(
    string?                        ExpectedPath,
    WindowsSandboxComponentSource? Source,
    bool                           Exists);

/// <summary>
///     <para>
///         Windows 沙箱组件的统一路径解析与严格校验器：留空或省略时定位到应用基准目录下的
///         <c>sandbox\&lt;固定文件名&gt;</c>；显式填写时要求通过 <see cref="Path.IsPathFullyQualified" />
///         的绝对路径。组合器、provisioning、状态诊断、指纹与双端入口共用同一事实来源。
///     </para>
///     <para>
///         Unified path resolver and strict validator for Windows sandbox components: blank or omitted
///         entries resolve to <c>sandbox\&lt;fixed-file-name&gt;</c> under the application base directory,
///         while explicit values must satisfy <see cref="Path.IsPathFullyQualified" />. Shared across the
///         composer, provisioner, status diagnostics, fingerprint, and both CLI and Desktop hosts.
///     </para>
/// </summary>
public static class WindowsSandboxComponentResolver
{
    public const string SandboxSubdirectoryName     = "sandbox";
    public const string SetupExecutableFileName     = "codex-windows-sandbox-setup.exe";
    public const string RunnerExecutableFileName    = "codex-command-runner.exe";
    public const string DenyProbeExecutableFileName = "codex-windows-managed-deny-probe.exe";
    public const string BuildInfoFileName           = "build-info.json";

    /// <summary>
    ///     <para>规范化应用基准目录；缺省使用 <see cref="AppContext.BaseDirectory" />。</para>
    ///     <para>Normalizes the application base directory, defaulting to <see cref="AppContext.BaseDirectory" />.</para>
    /// </summary>
    public static string NormalizeAppBaseDirectory(string? appBaseDirectory = null)
    {
        var raw = string.IsNullOrWhiteSpace(appBaseDirectory) ? AppContext.BaseDirectory : appBaseDirectory.Trim();
        if (string.IsNullOrWhiteSpace(raw) || !Path.IsPathFullyQualified(raw))
            throw new InvalidOperationException($"The application base directory must be a fully qualified absolute path: '{raw}'.");

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
    }

    /// <summary>
    ///     <para>
    ///         纯路径推导：逐字段解析 setup 与 runner；留空指向 <c>&lt;appBaseDirectory&gt;\sandbox\&lt;固定名&gt;</c>，
    ///         显式非空值须为完全限定绝对路径。不触碰文件系统。
    ///     </para>
    ///     <para>
    ///         Pure path derivation: resolves setup and runner independently; blank entries point to
    ///         <c>&lt;appBaseDirectory&gt;\sandbox\&lt;fixed-name&gt;</c>, while explicit non-blank entries
    ///         must be fully qualified absolute paths. Never touches the filesystem.
    ///     </para>
    /// </summary>
    public static WindowsSandboxComponentResolution Resolve(
        WindowsSandboxSettings? settings, string? appBaseDirectory = null)
    {
        var baseDir = NormalizeAppBaseDirectory(appBaseDirectory);
        return new WindowsSandboxComponentResolution
        {
            AppBaseDirectory = baseDir,
            Setup = ResolveOne(WindowsSandboxComponentKind.Setup, "setupExecutablePath", SetupExecutableFileName,
                               settings?.SetupExecutablePath, baseDir),
            Runner = ResolveOne(WindowsSandboxComponentKind.Runner, "runnerExecutablePath", RunnerExecutableFileName,
                                settings?.RunnerExecutablePath, baseDir)
        };
    }

    /// <summary>
    ///     <para>
    ///         要求两个组件的路径推导都合法，否则抛出带字段与原因上下文的异常。不触碰文件系统。
    ///     </para>
    ///     <para>
    ///         Requires both components to resolve validly, throwing with field and cause context otherwise.
    ///         Never touches the filesystem.
    ///     </para>
    /// </summary>
    public static WindowsSandboxComponentResolution RequireValidPaths(
        WindowsSandboxSettings? settings, string? appBaseDirectory = null)
    {
        var resolution = Resolve(settings, appBaseDirectory);
        if (!resolution.Setup.IsValid) throw new InvalidOperationException(resolution.Setup.Error);
        if (!resolution.Runner.IsValid) throw new InvalidOperationException(resolution.Runner.Error);
        return resolution;
    }

    /// <summary>
    ///     <para>
    ///         只读诊断单个组件：检查存在性、通过 <c>build-info.json</c> 识别发布包缺件，并在 Windows 上
    ///         校验句柄最终路径与随包目录边界。不创建或修改任何文件。
    ///     </para>
    ///     <para>
    ///         Read-only inspection of a single component: checks existence, identifies missing release-package
    ///         files via <c>build-info.json</c>, and verifies the handle-resolved final path and bundled
    ///         directory boundary on Windows. Creates or modifies nothing.
    ///     </para>
    /// </summary>
    public static WindowsSandboxComponentInspection InspectComponent(
        WindowsSandboxResolvedComponent component, string appBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(component);
        var baseDir = NormalizeAppBaseDirectory(appBaseDirectory);

        if (!component.IsValid)
            return new WindowsSandboxComponentInspection
            {
                Resolved                = component,
                Exists                  = false,
                IsReleasePackageBundled = false,
                Error                   = component.Error
            };

        var candidate = component.CandidatePath!;
        if (!File.Exists(candidate))
        {
            // 仅在缺件需要诊断时读取 build-info.json；正常存在路径不依赖诊断清单。
            var isReleaseBundled = component.Source == WindowsSandboxComponentSource.Bundled &&
                                   IsListedInBuildInfo(baseDir, component.FileName);
            return new WindowsSandboxComponentInspection
            {
                Resolved                = component,
                Exists                  = false,
                IsReleasePackageBundled = isReleaseBundled,
                Error                   = BuildMissingComponentMessage(component, isReleaseBundled)
            };
        }

        var identity = SandboxFinalPathResolver.TryResolveFileIdentity(candidate);
        if (identity is null)
            return new WindowsSandboxComponentInspection
            {
                Resolved                = component,
                Exists                  = true,
                IsReleasePackageBundled = false,
                Error = $"The Windows sandbox component '{component.FileName}' ({component.SettingKey}, " +
                        $"source: {SourceTag(component.Source)}) at '{candidate}' could not be opened or " +
                        "resolved to a final regular file path (fail closed)."
            };

        if (component.Source == WindowsSandboxComponentSource.Bundled)
        {
            var bundledError = ValidateBundledFinalLocation(component, baseDir, identity.FinalPath);
            if (bundledError is not null)
                return new WindowsSandboxComponentInspection
                {
                    Resolved                = component,
                    Exists                  = true,
                    FinalPath               = identity.FinalPath,
                    Identity                = identity,
                    IsReleasePackageBundled = false,
                    Error                   = bundledError
                };
        }

        return new WindowsSandboxComponentInspection
        {
            Resolved                = component,
            Exists                  = true,
            FinalPath               = identity.FinalPath,
            Identity                = identity,
            IsReleasePackageBundled = false
        };
    }

    /// <summary>
    ///     <para>只读诊断 deny-probe（始终位于有效 runner 同目录）。</para>
    ///     <para>Read-only inspection for the deny-probe executable (always in the effective runner's directory).</para>
    /// </summary>
    public static WindowsSandboxProbeInspection InspectDenyProbe(WindowsSandboxComponentResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        var probePath = resolution.EffectiveProbePath;
        if (probePath is null) return new WindowsSandboxProbeInspection(null, null, false);

        return new WindowsSandboxProbeInspection(probePath, resolution.Runner.Source, File.Exists(probePath));
    }

    /// <summary>
    ///     <para>
    ///         组合与 provisioning 共用的严格组件校验：要求路径合法、文件存在、可经句柄解析出最终普通文件
    ///         路径、随包项最终落点位于 <c>&lt;appBaseDirectory&gt;\sandbox</c> 实际子树内，且词法路径与
    ///         最终落点均不与任何沙箱写根重叠。校验失败抛出带上下文异常（fail closed）。
    ///     </para>
    ///     <para>
    ///         Strict component validation shared by composition and provisioning: requires a valid path,
    ///         an existing regular file with a handle-resolved final path, containment within the real
    ///         <c>&lt;appBaseDirectory&gt;\sandbox</c> subtree for bundled items, and non-overlap of both
    ///         lexical and final paths with every sandbox write root. Throws contextual errors on failure
    ///         (fail closed).
    ///     </para>
    /// </summary>
    public static SandboxFileIdentity ValidateExecutableForUse(
        WindowsSandboxResolvedComponent component,
        string                          appBaseDirectory,
        IReadOnlyList<string>           writeRoots)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(writeRoots);

        var inspection = InspectComponent(component, appBaseDirectory);
        if (!inspection.IsUsable || inspection.Identity is null)
            throw new InvalidOperationException(inspection.Error + " refusing to fall back to host execution.");

        var candidate = component.CandidatePath!;
        var finalPath = inspection.Identity.FinalPath;

        foreach (var root in writeRoots)
        {
            var rootFull  = Path.GetFullPath(root);
            var rootFinal = SandboxFinalPathResolver.TryGetFinalPath(rootFull) ?? rootFull;
            var overlaps = Workspace.IsInside(rootFull, candidate)  || Workspace.IsInside(candidate, rootFull)  ||
                           Workspace.IsInside(rootFull, finalPath)  || Workspace.IsInside(finalPath, rootFull)  ||
                           Workspace.IsInside(rootFinal, candidate) || Workspace.IsInside(candidate, rootFinal) ||
                           Workspace.IsInside(rootFinal, finalPath) || Workspace.IsInside(finalPath, rootFinal);
            if (!overlaps) continue;

            var locationDetail = string.Equals(candidate, finalPath, StringComparison.OrdinalIgnoreCase)
                ? $"'{candidate}'"
                : $"'{candidate}' (resolves to '{finalPath}')";
            throw new InvalidOperationException(
                $"The Windows sandbox component '{component.FileName}' ({component.SettingKey}, " +
                $"source: {SourceTag(component.Source)}) at {locationDetail} overlaps the writable root '{root}'; " +
                "sandboxed commands could tamper with it. Move the entire TinyHarness installation (or the " +
                "configured component) outside every sandbox write root; refusing to fall back to host execution.");
        }

        return inspection.Identity;
    }

    /// <summary>
    ///     <para>提权前复核计划中的可执行文件身份未发生变化，变化或不可解析时立即拒绝（fail closed）。</para>
    ///     <para>
    ///         Re-verifies that the planned executable's file identity has not changed before elevation,
    ///         refusing immediately (fail closed) on any mismatch or resolution failure.
    ///     </para>
    /// </summary>
    public static void VerifyFileIdentityUnchanged(string path, SandboxFileIdentity expected, string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var current = SandboxFinalPathResolver.TryResolveFileIdentity(path);
        if (current is null)
            throw new InvalidOperationException(
                $"The {label} at '{path}' could no longer be resolved before elevation; refusing to launch.");

        if (!string.Equals(current.FinalPath, expected.FinalPath, StringComparison.OrdinalIgnoreCase) ||
            current.VolumeSerialNumber != expected.VolumeSerialNumber                                 ||
            current.FileIndexHigh      != expected.FileIndexHigh                                      ||
            current.FileIndexLow       != expected.FileIndexLow)
            throw new InvalidOperationException(
                $"The {label} at '{path}' changed identity after planning (planned '{expected.FinalPath}', " +
                $"current '{current.FinalPath}'); refusing to elevate.");
    }

    /// <summary>
    ///     <para>判断应用目录下 <c>build-info.json</c> 是否声明了指定随包沙箱文件（用于缺失诊断，不做安全信任）。</para>
    ///     <para>
    ///         Checks whether <c>build-info.json</c> under the app directory lists the given bundled sandbox
    ///         file (used for diagnostics only, never as a trust anchor).
    ///     </para>
    /// </summary>
    internal static bool IsListedInBuildInfo(string appBaseDirectory, string componentFileName)
    {
        var manifestPath = Path.Combine(appBaseDirectory, BuildInfoFileName);
        if (!File.Exists(manifestPath)) return false;

        try
        {
            using var stream   = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object         ||
                !document.RootElement.TryGetProperty("files", out var files)   ||
                files.ValueKind != JsonValueKind.Object)
                return false;

            var expectedKey = $"{SandboxSubdirectoryName}/{componentFileName}";
            foreach (var property in files.EnumerateObject())
                if (string.Equals(property.Name, expectedKey, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static WindowsSandboxResolvedComponent ResolveOne(
        WindowsSandboxComponentKind kind,
        string                      settingKey,
        string                      fileName,
        string?                     configuredValue,
        string                      appBaseDirectory)
    {
        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            var bundledPath = Path.Combine(appBaseDirectory, SandboxSubdirectoryName, fileName);
            return new WindowsSandboxResolvedComponent
            {
                Kind            = kind,
                SettingKey      = settingKey,
                FileName        = fileName,
                Source          = WindowsSandboxComponentSource.Bundled,
                ConfiguredValue = null,
                CandidatePath   = Path.GetFullPath(bundledPath)
            };
        }

        var trimmed = configuredValue.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            return new WindowsSandboxResolvedComponent
            {
                Kind            = kind,
                SettingKey      = settingKey,
                FileName        = fileName,
                Source          = WindowsSandboxComponentSource.Configured,
                ConfiguredValue = trimmed,
                CandidatePath   = null,
                Error = $"The trusted Windows sandbox setting '{settingKey}' must be a fully qualified " +
                        $"absolute path (or left blank to use the bundled component): '{trimmed}'."
            };

        return new WindowsSandboxResolvedComponent
        {
            Kind            = kind,
            SettingKey      = settingKey,
            FileName        = fileName,
            Source          = WindowsSandboxComponentSource.Configured,
            ConfiguredValue = trimmed,
            CandidatePath   = Path.GetFullPath(trimmed)
        };
    }

    private static string? ValidateBundledFinalLocation(
        WindowsSandboxResolvedComponent component, string appBaseDirectory, string fileFinalPath)
    {
        var appBaseFinal = SandboxFinalPathResolver.TryGetFinalDirectoryPath(appBaseDirectory);
        if (appBaseFinal is null)
            return $"The application base directory '{appBaseDirectory}' for '{component.FileName}' " +
                   $"({component.SettingKey}) could not be resolved to a real directory path (fail closed).";

        var expectedBundledRoot = Path.Combine(appBaseFinal, SandboxSubdirectoryName);
        var bundledRoot         = Path.Combine(appBaseDirectory, SandboxSubdirectoryName);
        var bundledRootFinal    = SandboxFinalPathResolver.TryGetFinalDirectoryPath(bundledRoot);
        if (bundledRootFinal is null)
            return $"The bundled sandbox directory '{bundledRoot}' for '{component.FileName}' ({component.SettingKey}) " +
                   "could not be resolved to a real directory path (fail closed).";

        // 既要求 sandbox 子目录本身未被联接点/符号链接重定向出应用目录，也要求最终文件位于该实际子树内。
        if (!Workspace.IsInside(expectedBundledRoot, bundledRootFinal) ||
            !Workspace.IsInside(expectedBundledRoot, fileFinalPath))
            return $"The bundled sandbox component '{component.FileName}' ({component.SettingKey}) at " +
                   $"'{component.CandidatePath}' resolves outside the application's bundled sandbox directory " +
                   $"('{fileFinalPath}' is outside '{expectedBundledRoot}'). Remove any symbolic link or junction " +
                   "redirecting sandbox components, or configure an explicit absolute path in trusted settings.";

        return null;
    }

    private static string BuildMissingComponentMessage(
        WindowsSandboxResolvedComponent component, bool isReleasePackageBundled)
    {
        var candidate = component.CandidatePath!;
        var kindLabel = component.Kind == WindowsSandboxComponentKind.Setup ? "setup executable" : "runner executable";
        if (component.Source == WindowsSandboxComponentSource.Configured)
            return $"The configured Windows sandbox {kindLabel} was not found at '{candidate}' " +
                   $"('{component.FileName}', {component.SettingKey}, source: configured). " +
                   $"Fix settings.windowsSandbox.{component.SettingKey} or clear it to use the bundled component;";

        if (isReleasePackageBundled)
            return $"Release package is missing bundled Windows sandbox {kindLabel} (was not found at '{candidate}'; " +
                   $"'{component.FileName}', {component.SettingKey}, source: bundled). Re-extract the complete " +
                   "same-version TinyHarness ZIP so all files beside TinyHarness.exe stay intact;";

        return $"The current application directory is missing bundled Windows sandbox {kindLabel} " +
               $"(was not found at '{candidate}'; '{component.FileName}', {component.SettingKey}, source: bundled). " +
               $"If running from source, set settings.windowsSandbox.{component.SettingKey} to the absolute path " +
               "of your external fork build (or use TINYHARNESS_USER_CONFIG_DIR for a dev config); if using a " +
               "release ZIP, re-extract the complete archive;";
    }

    private static string SourceTag(WindowsSandboxComponentSource source) =>
        source == WindowsSandboxComponentSource.Bundled ? "bundled" : "configured";
}
