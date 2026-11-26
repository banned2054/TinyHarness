using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     <para>
///         `tinyharness sandbox`：Windows 沙箱的管理入口。status 严格只读（不创建任何目录/文件、不提权、绝不调用组合器）；provision 是唯一的机器级修改路径——展示副作用、过确认门后经
///         UAC 运行 setup.exe 一次。两个子命令都绝不把输入发送给模型。
///     </para>
///     <para>
///         `tinyharness sandbox`: the management entry for the Windows sandbox.
///         status is strictly read-only (it creates no directory or file, never
///         elevates, and never touches the composer); provision is the only
///         machine-level mutation path — it presents the side effects, passes the
///         confirmation gate, then runs setup.exe elevated once. Neither
///         subcommand ever sends input to a model.
///     </para>
/// </summary>
internal static class SandboxCommand
{
    public static async Task<int> ExecuteAsync(
        CommandContext             context,
        CliOptions                 options,
        CancellationToken          cancellationToken,
        WindowsSandboxProvisioner? provisioner = null) => options.Subcommand switch
    {
        "status"    => await StatusAsync(context, cancellationToken).ConfigureAwait(false),
        "provision" => await ProvisionAsync(context, options, cancellationToken, provisioner).ConfigureAwait(false),
        "verify" => await SandboxVerifyCommand.ExecuteAsync(context, options, cancellationToken)
                                              .ConfigureAwait(false),
        "verify-worker" => await SandboxVerifyCommand.ExecuteWorkerAsync(context, options, cancellationToken)
                                                     .ConfigureAwait(false),
        _ => throw new InvalidOperationException($"Unhandled sandbox subcommand '{options.Subcommand}'.")
    };

    /// <summary>
    ///     <para>
    ///         严格只读的状态检查。逐项展示组件来源、有效绝对路径、文件与探针状态以及机器就绪状态。
    ///         禁用且无显式配置错误、无发布包缺件时返回 0 并给出操作提示；启用后未就绪、显式配置错误
    ///         或已识别发布包缺件返回 1。
    ///     </para>
    ///     <para>
    ///         Strictly read-only status check. Reports each component's source, effective absolute path,
    ///         file and probe state, and machine readiness. Returns 0 with guidance when disabled without
    ///         configuration errors or missing release-package files; returns 1 when enabled and not ready,
    ///         explicitly misconfigured, or missing release-package files.
    ///     </para>
    /// </summary>
    private static async Task<int> StatusAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io               = context.Io;
        var configPath       = context.ResolveUserConfigPath();
        var appBaseDirectory = context.ResolveAppBaseDirectory();
        var userConfig       = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var configured       = userConfig.Settings?.WindowsSandbox;
        var effective        = configured ?? new WindowsSandboxSettings();
        var hasExplicitSetup = HasExplicitSandboxSettings(configured);

        var resolution       = WindowsSandboxComponentResolver.Resolve(effective, appBaseDirectory);
        var setupInspection  = WindowsSandboxComponentResolver.InspectComponent(resolution.Setup, appBaseDirectory);
        var runnerInspection = WindowsSandboxComponentResolver.InspectComponent(resolution.Runner, appBaseDirectory);
        var probeInspection  = WindowsSandboxComponentResolver.InspectDenyProbe(resolution);

        await io.WriteLineAsync("TinyHarness sandbox status", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  appDir   : {appBaseDirectory}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  enabled  : {(effective.Enabled ? "true" : "false")}（false 时 run 保持宿主执行）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  policy   : {PolicyDisplay(effective.Policy)}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync(effective.AllowTcpLoopback
                                    ? "  network  : external-blocked; TCP loopback allowed (host services included); UDP blocked"
                                    : "  network  : restricted; TCP loopback blocked", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  setup    : {FormatComponentLine(setupInspection)}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  runner   : {FormatComponentLine(runnerInspection)}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  probe    : {FormatProbeLine(probeInspection)}", cancellationToken)
                .ConfigureAwait(false);
        // 相对 home 不在此抛出，改由下方问题清单报告，status 对错误输入保持只读诊断。
        await io.WriteLineAsync(string.IsNullOrWhiteSpace(effective.SandboxHome)
                                    ? $"  home     : {WindowsSandboxComponents.ResolveSandboxHome(null, effective.AllowTcpLoopback)}（sandboxHome 未设置，使用固定默认）"
                                    : $"  home     : {effective.SandboxHome}",
                                cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"  versions : 本 build 期望 setup v{(effective.AllowTcpLoopback ? WindowsSandboxComponents.TcpLoopbackSetupVersion : WindowsSandboxComponents.SetupVersion)} / ipc v{WindowsSandboxComponents.IpcVersion}",
                             cancellationToken)
             .ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
            await io
                 .WriteLineAsync("  platform : 当前平台不是 Windows；enabled=true 时 run 在本平台会直接拒绝执行（不回退宿主执行）",
                                 cancellationToken)
                 .ConfigureAwait(false);

        var pathProblems = CollectPathProblems(effective, resolution);
        if (pathProblems.Count > 0)
        {
            foreach (var problem in pathProblems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            await io.WriteLineAsync($"{pathProblems.Count} 项问题；修正 settings.windowsSandbox 的路径后重试。",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        var componentErrors = CollectComponentErrors(setupInspection, runnerInspection);
        var releasePackageMissing = setupInspection.IsReleasePackageBundled  && !setupInspection.Exists ||
                                    runnerInspection.IsReleasePackageBundled && !runnerInspection.Exists;
        var hasExplicitComponentOrBoundaryError =
            (!setupInspection.IsUsable &&
             (setupInspection.Resolved.Source == WindowsSandboxComponentSource.Configured ||
              setupInspection.Exists)) ||
            (!runnerInspection.IsUsable &&
             (runnerInspection.Resolved.Source == WindowsSandboxComponentSource.Configured ||
              runnerInspection.Exists));

        var components = new WindowsSandboxComponents
        {
            AllowTcpLoopback     = effective.AllowTcpLoopback,
            SetupExecutablePath  = resolution.Setup.CandidatePath!,
            RunnerExecutablePath = resolution.Runner.CandidatePath!,
            SandboxHome          = WindowsSandboxComponents.ResolveSandboxHome(effective.SandboxHome,
                                                                               effective.AllowTcpLoopback)
        };
        var status = await new WindowsSandboxStateInspector(components).InspectAsync(cancellationToken)
                                                                       .ConfigureAwait(false);
        // 机器级问题（marker/cap_sid/credentials）：组件缺失已由上方的结构化诊断覆盖，此处过滤重复文件缺失项。
        var machineProblems = status.Problems
                                    .Where(problem => !problem.StartsWith("Setup executable not found:",
                                                                          StringComparison.Ordinal) &&
                                                      !problem.StartsWith("Runner executable not found:",
                                                                          StringComparison.Ordinal))
                                    .ToList();

        if (componentErrors.Count > 0)
        {
            var mustFail = effective.Enabled || hasExplicitSetup || releasePackageMissing ||
                           hasExplicitComponentOrBoundaryError;
            if (!mustFail)
            {
                await io.WriteLineAsync("  [ok]   未启用 windowsSandbox，且当前应用目录没有随包沙箱组件；run/smoke/doctor 保持宿主执行（无 OS 隔离）",
                                        cancellationToken)
                        .ConfigureAwait(false);
                foreach (var error in componentErrors)
                    await io.WriteLineAsync($"  [info] {error}", cancellationToken).ConfigureAwait(false);
                await io.WriteLineAsync("  要启用：发布包请完整解压同版本 ZIP；源码运行请在用户配置的 settings.windowsSandbox 中设置 " +
                                        "setupExecutablePath 与 runnerExecutablePath 绝对路径，再运行 tinyharness sandbox provision，" +
                                        "最后将 enabled 设为 true。",
                                        cancellationToken)
                        .ConfigureAwait(false);
                return 0;
            }

            foreach (var error in componentErrors)
                await io.WriteLineAsync($"  [fail] {error}", cancellationToken).ConfigureAwait(false);
            foreach (var problem in machineProblems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            var totalProblems = componentErrors.Count + machineProblems.Count;
            await io.WriteLineAsync($"{totalProblems} 项问题；修复后再运行 tinyharness sandbox provision 或启用沙箱。",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        if (status.Ready)
        {
            await io.WriteLineAsync(effective.Enabled
                                        ? "  [ok]   沙箱就绪：组件存在，marker 版本一致，cap_sid 与账户凭据可解析"
                                        : "  [ok]   沙箱组件与机器状态已就绪（当前 enabled=false，run 保持宿主执行；将 settings.windowsSandbox.enabled 设为 true 即可启用）",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 0;
        }

        if (!effective.Enabled && !hasExplicitSetup)
        {
            await io.WriteLineAsync("  [ok]   未启用 windowsSandbox；已发现当前安装包的随包组件，机器尚未初始化，run/smoke/doctor 保持宿主执行（无 OS 隔离）",
                                    cancellationToken)
                    .ConfigureAwait(false);
            foreach (var problem in status.Problems)
                await io.WriteLineAsync($"  [info] {problem}", cancellationToken).ConfigureAwait(false);
            await io.WriteLineAsync("  要启用：直接运行 tinyharness sandbox provision（无需手动填写随包组件路径），完成后将 " +
                                    "settings.windowsSandbox.enabled 设为 true。",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 0;
        }

        foreach (var problem in status.Problems)
            await io.WriteLineAsync($"  [fail] {problem}", cancellationToken)
                    .ConfigureAwait(false);

        await io.WriteLineAsync($"{status.Problems.Count} 项问题；未就绪时沙箱命令会拒绝执行（fail closed）。",
                                cancellationToken)
                .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     <para>显式 provisioning：校验并构造 payload（不提权）→ 展示机器级副作用 → 确认门 → UAC 运行 setup.exe → 事后重新检查。exit 0 仅当 helper 成功且事后检查就绪。</para>
    ///     <para>
    ///         Explicit provisioning: validate and build the payload (no elevation) →
    ///         present the machine-level side effects → confirmation gate → run
    ///         setup.exe via UAC → re-inspect afterward. exit 0 only when the helper
    ///         succeeded and the re-inspection is ready.
    ///     </para>
    /// </summary>
    private static async Task<int> ProvisionAsync(
        CommandContext             context,
        CliOptions                 options,
        CancellationToken          cancellationToken,
        WindowsSandboxProvisioner? provisioner)
    {
        var io = context.Io;
        if (!OperatingSystem.IsWindows())
        {
            await io.WriteLineAsync("  [fail] sandbox provision 仅支持 Windows", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var configPath       = context.ResolveUserConfigPath();
        var appBaseDirectory = context.ResolveAppBaseDirectory();
        var userConfig       = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var effective        = userConfig.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();

        await io.WriteLineAsync("TinyHarness sandbox provision", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);

        if (IsExplicitRelative(effective.SandboxHome))
        {
            await io
                 .WriteLineAsync("  [fail] settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）" +
                                 DescribeSetting(effective.SandboxHome),
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        WindowsSandboxProvisionPlan plan;
        try
        {
            provisioner ??= new WindowsSandboxProvisioner(new ProcessSandboxSetupElevator());
            plan = provisioner.BuildPlan(effective, context.ResolveWorkingDirectory(), Environment.UserName,
                                         appBaseDirectory);
        }
        catch (Exception ex) when
            (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            await io.WriteLineAsync($"  [fail] 无法构造 provisioning 计划：{ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        await PrintSideEffectsAsync(io, plan, cancellationToken).ConfigureAwait(false);

        if (!options.AssumeYes)
        {
            if (!io.IsInteractive)
            {
                await io
                     .WriteLineAsync("  [fail] 非交互环境必须使用 --yes 才能执行 sandbox provision；未做任何更改",
                                     cancellationToken)
                     .ConfigureAwait(false);
                return 1;
            }

            if (!await CliPrompt.ConfirmAsync(io, "确认执行上述机器级修改？", false, cancellationToken)
                                .ConfigureAwait(false))
            {
                await io.WriteLineAsync("  已取消；未做任何更改。", cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }

        WindowsSandboxProvisionResult result;
        try
        {
            result = await provisioner.ProvisionAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await io.WriteLineAsync($"  [fail] 提权前校验失败，未启动 setup helper：{ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        if (result.AuditWriteError is not null)
            await io.WriteLineAsync($"  [warn] {result.AuditWriteError}", cancellationToken).ConfigureAwait(false);

        return await ReportProvisionResultAsync(io, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>打印机器级副作用清单（固定文案，来自接入文档 §4）与重复 provisioning 警告。</para>
    ///     <para>
    ///         Prints the machine-level side-effect list (fixed wording from the
    ///         integration document §4) and the re-provisioning warning.
    ///     </para>
    /// </summary>
    private static async Task PrintSideEffectsAsync(
        ICliConsole io, WindowsSandboxProvisionPlan plan, CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  setup    : {plan.Components.SetupExecutablePath}（{plan.ResolvedComponents.Setup.SourceDisplayZh}）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  runner   : {plan.Components.RunnerExecutablePath}（{plan.ResolvedComponents.Runner.SourceDisplayZh}）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  home     : {plan.Components.SandboxHome}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  tempRoot : {plan.TempRoot}（仅作为路径进入 payload，本命令不创建）",
                                cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("  writeRoots（将获得写授权）:", cancellationToken).ConfigureAwait(false);
        foreach (var root in plan.Payload.WriteRoots)
            await io.WriteLineAsync($"      {root}", cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync("  本命令将以 UAC 提权运行 setup.exe 一次，修改以下机器级状态：", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("    - 本地组 TinyHarnessUsers", cancellationToken).ConfigureAwait(false);
        await io
             .WriteLineAsync($"    - 本地账户 {plan.Components.OfflineUsername} / {plan.Components.OnlineUsername}（随机密码、永不过期）",
                             cancellationToken)
             .ConfigureAwait(false);
        await io.WriteLineAsync("    - 注册表条目隐藏上述账户", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync(plan.Components.AllowTcpLoopback
                                    ? "    - 独立防火墙/WFP 规则：阻断外部网络和 UDP，允许 TCP 环回访问宿主服务"
                                    : "    - 防火墙规则，以及 offline 账户的 WFP BLOCK 过滤器（断网）", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("    - sandbox home 目录 ACL 与上述 write roots 的授权", cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"  警告：重复 provisioning 会重置所选账户（{plan.Components.OfflineUsername}/{plan.Components.OnlineUsername}）的密码，" +
                             "同机共用这套 fork 命名空间的其他 home 会受影响。本 fork 与 Codex 发布版使用独立命名空间" +
                             "（账户、组、防火墙、WFP 互不共享），但其共存尚未验收。本命令没有卸载或回滚。", cancellationToken)
             .ConfigureAwait(false);
    }

    private static async Task<int> ReportProvisionResultAsync(
        ICliConsole io, WindowsSandboxProvisionResult result, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case SandboxProvisionOutcome.Declined :
                await io.WriteLineAsync("  [fail] 用户在 UAC 对话框拒绝了提权；未做任何更改", cancellationToken)
                        .ConfigureAwait(false);
                return 1;

            case SandboxProvisionOutcome.NotFinished :
                await io
                     .WriteLineAsync("  [fail] provisioning 未在期限内完成，机器状态未知；请运行 tinyharness sandbox status 检查",
                                     cancellationToken)
                     .ConfigureAwait(false);
                return 1;

            case SandboxProvisionOutcome.Failed :
                await io
                     .WriteLineAsync($"  [fail] setup helper 以退出码 {result.ExitCode} 失败：{result.SetupError}",
                                     cancellationToken)
                     .ConfigureAwait(false);
                return 1;

            default :
                var post = result.PostInspection!;
                if (post.Ready)
                {
                    await io.WriteLineAsync("  [ok]   provisioning 完成，沙箱就绪", cancellationToken)
                            .ConfigureAwait(false);
                    return 0;
                }

                foreach (var problem in post.Problems)
                    await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

                await io.WriteLineAsync("  provisioning 已完成，但事后检查仍报告上述问题。", cancellationToken)
                        .ConfigureAwait(false);
                return 1;
        }
    }

    private static bool HasExplicitSandboxSettings(WindowsSandboxSettings? settings) =>
        settings is not null &&
        (settings.Enabled                                         ||
         settings.AllowTcpLoopback                                ||
         !string.IsNullOrWhiteSpace(settings.SetupExecutablePath) ||
         !string.IsNullOrWhiteSpace(settings.RunnerExecutablePath) ||
         !string.IsNullOrWhiteSpace(settings.SandboxHome));

    /// <summary>
    ///     <para>显式设置但非绝对（home 可留空使用固定默认，只有显式相对路径才是错误）。</para>
    ///     <para>
    ///         Explicitly set but not absolute (the home may stay blank for the fixed
    ///         default; only an explicitly relative value is an error).
    ///     </para>
    /// </summary>
    private static bool IsExplicitRelative(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !Path.IsPathFullyQualified(value.Trim());

    private static string DescribeSetting(string? value) => string.IsNullOrWhiteSpace(value) ? "。" : $"：'{value}'。";

    private static List<string> CollectPathProblems(
        WindowsSandboxSettings settings, WindowsSandboxComponentResolution resolution)
    {
        var problems = new List<string>();
        if (!resolution.Setup.IsValid)
            problems.Add($"settings.windowsSandbox.setupExecutablePath 非法：{resolution.Setup.Error}");

        if (!resolution.Runner.IsValid)
            problems.Add($"settings.windowsSandbox.runnerExecutablePath 非法：{resolution.Runner.Error}");

        if (IsExplicitRelative(settings.SandboxHome))
            problems.Add("settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）" +
                         DescribeSetting(settings.SandboxHome));

        return problems;
    }

    private static List<string> CollectComponentErrors(
        WindowsSandboxComponentInspection setup, WindowsSandboxComponentInspection runner)
    {
        var errors = new List<string>();
        if (!setup.IsUsable && setup.Error is not null) errors.Add(setup.Error);
        if (!runner.IsUsable && runner.Error is not null) errors.Add(runner.Error);
        return errors;
    }

    private static string FormatComponentLine(WindowsSandboxComponentInspection inspection)
    {
        var resolved = inspection.Resolved;
        var pathText = resolved.CandidatePath ?? resolved.ConfiguredValue ?? "<invalid>";
        var state = !resolved.IsValid
            ? "非法路径"
            : inspection.IsUsable
                ? "存在"
                : inspection.Exists
                    ? "边界校验失败"
                    : "缺失";
        return $"{pathText}（{resolved.SourceDisplayZh}，{state}）";
    }

    private static string FormatProbeLine(WindowsSandboxProbeInspection probe)
    {
        if (probe.ExpectedPath is null) return "<不可用：runner 路径非法>";

        var sourceText = probe.Source == WindowsSandboxComponentSource.Configured ? "自定义 runner 同目录" : "当前安装包默认";
        return $"{probe.ExpectedPath}（{sourceText}，{(probe.Exists ? "存在" : "缺失；仅 deny-probe 验收需要")}）";
    }

    private static string PolicyDisplay(SandboxPolicyKind kind) => kind switch
    {
        SandboxPolicyKind.ReadOnly       => "read-only",
        SandboxPolicyKind.WorkspaceWrite => "workspace-write",
        _                                => kind.ToString()
    };
}
