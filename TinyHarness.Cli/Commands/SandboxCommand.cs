using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     `tinyharness sandbox`：Windows 沙箱的管理入口。status 严格只读（不创建任何目录/文件、
///     不提权、绝不调用组合器）；provision 是唯一的机器级修改路径——展示副作用、过确认门后经
///     UAC 运行 setup.exe 一次。两个子命令都绝不把输入发送给模型。
///     `tinyharness sandbox`: the management entry for the Windows sandbox.
///     status is strictly read-only (it creates no directory or file, never
///     elevates, and never touches the composer); provision is the only
///     machine-level mutation path — it presents the side effects, passes the
///     confirmation gate, then runs setup.exe elevated once. Neither
///     subcommand ever sends input to a model.
/// </summary>
internal static class SandboxCommand
{
    public static async Task<int> ExecuteAsync(CommandContext      context,
                                               CliOptions           options,
                                               CancellationToken    cancellationToken,
                                               WindowsSandboxProvisioner? provisioner = null)
    {
        return options.Subcommand switch
        {
            "status"    => await StatusAsync(context, cancellationToken).ConfigureAwait(false),
            "provision" => await ProvisionAsync(context, options, cancellationToken, provisioner).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unhandled sandbox subcommand '{options.Subcommand}'.")
        };
    }

    /// <summary>
    ///     严格只读的状态检查。未配置或就绪返回 0；有任何问题返回 1。不要求 enabled=true。
    ///     The strictly read-only status check. Not configured or ready returns
    ///     0; any reported problem returns 1. enabled=true is not required.
    /// </summary>
    private static async Task<int> StatusAsync(CommandContext context, CancellationToken cancellationToken)
    {
        var io = context.Io;
        var configPath = context.ResolveUserConfigPath();
        var userConfig = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings   = userConfig.Settings?.WindowsSandbox;

        await io.WriteLineAsync("TinyHarness sandbox status", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);

        if (settings is null || IsBlankSettings(settings))
        {
            await io
                 .WriteLineAsync("  [ok]   未配置 windowsSandbox；run/smoke/doctor 保持宿主执行（无 OS 隔离），也不会修改机器",
                                 cancellationToken)
                 .ConfigureAwait(false);
            await io
                 .WriteLineAsync("  要启用：在上述用户配置的 settings.windowsSandbox 中设置 setupExecutablePath、" +
                                 "runnerExecutablePath（两个组件 exe 的绝对路径）；sandboxHome 可省略（缺省使用独立固定默认 " +
                                 $"{WindowsSandboxComponents.DefaultSandboxHome}），再运行 tinyharness sandbox provision；" +
                                 "enabled 显式设为 true 后 run 才会使用沙箱。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 0;
        }

        await io.WriteLineAsync($"  enabled  : {(settings.Enabled ? "true" : "false")}（false 时 run 保持宿主执行）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  policy   : {PolicyDisplay(settings.Policy)}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  setup    : {settings.SetupExecutablePath}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  runner   : {settings.RunnerExecutablePath}", cancellationToken).ConfigureAwait(false);
        // A relative home is reported by the problems section below instead of
        // throwing here, so status stays a read-only diagnostic for bad input.
        await io.WriteLineAsync(string.IsNullOrWhiteSpace(settings.SandboxHome)
                                    ? $"  home     : {WindowsSandboxComponents.DefaultSandboxHome}（sandboxHome 未设置，使用固定默认）"
                                    : $"  home     : {settings.SandboxHome}",
                                cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync(
                            $"  versions : 本 build 期望 setup v{WindowsSandboxComponents.SetupVersion} / ipc v{WindowsSandboxComponents.IpcVersion}",
                            cancellationToken)
             .ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
            await io
                 .WriteLineAsync("  platform : 当前平台不是 Windows；enabled=true 时 run 在本平台会直接拒绝执行（不回退宿主执行）",
                                 cancellationToken)
                 .ConfigureAwait(false);

        var pathProblems = CollectPathProblems(settings);
        if (pathProblems.Count > 0)
        {
            foreach (var problem in pathProblems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            await io.WriteLineAsync($"{pathProblems.Count} 项问题；修正 settings.windowsSandbox 的路径后重试。",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = settings.SetupExecutablePath.Trim(),
            RunnerExecutablePath = settings.RunnerExecutablePath.Trim(),
            SandboxHome          = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome)
        };
        var status = await new WindowsSandboxStateInspector(components).InspectAsync(cancellationToken)
                                       .ConfigureAwait(false);
        if (status.Ready)
        {
            await io.WriteLineAsync("  [ok]   沙箱就绪：组件存在，marker 版本一致，cap_sid 与账户凭据可解析",
                                    cancellationToken)
                    .ConfigureAwait(false);
            return 0;
        }

        foreach (var problem in status.Problems) await io.WriteLineAsync($"  [fail] {problem}", cancellationToken)
           .ConfigureAwait(false);

        await io.WriteLineAsync($"{status.Problems.Count} 项问题；未就绪时沙箱命令会拒绝执行（fail closed）。",
                                cancellationToken)
                .ConfigureAwait(false);
        return 1;
    }

    /// <summary>
    ///     显式 provisioning：校验并构造 payload（不提权）→ 展示机器级副作用 → 确认门 → UAC 运行
    ///     setup.exe → 事后重新检查。exit 0 仅当 helper 成功且事后检查就绪。
    ///     Explicit provisioning: validate and build the payload (no elevation) →
    ///     present the machine-level side effects → confirmation gate → run
    ///     setup.exe via UAC → re-inspect afterward. exit 0 only when the helper
    ///     succeeded and the re-inspection is ready.
    /// </summary>
    private static async Task<int> ProvisionAsync(CommandContext             context,
                                                  CliOptions                  options,
                                                  CancellationToken           cancellationToken,
                                                  WindowsSandboxProvisioner?  provisioner)
    {
        var io = context.Io;
        if (!OperatingSystem.IsWindows())
        {
            await io.WriteLineAsync("  [fail] sandbox provision 仅支持 Windows", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var configPath = context.ResolveUserConfigPath();
        var userConfig = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings   = userConfig.Settings?.WindowsSandbox;

        await io.WriteLineAsync("TinyHarness sandbox provision", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);

        if (settings is null)
        {
            await io
                 .WriteLineAsync("  [fail] 用户配置未设置 settings.windowsSandbox；先配置 setupExecutablePath 与 " +
                                 "runnerExecutablePath（sandboxHome 可省略，缺省使用独立固定默认 home）",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        if (IsMissingOrRelative(settings.SetupExecutablePath))
        {
            await io
                 .WriteLineAsync("  [fail] settings.windowsSandbox.setupExecutablePath 缺失或不是绝对路径" +
                                 DescribeSetting(settings.SetupExecutablePath),
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        if (IsExplicitRelative(settings.SandboxHome))
        {
            await io
                 .WriteLineAsync("  [fail] settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）" +
                                 DescribeSetting(settings.SandboxHome),
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        WindowsSandboxProvisionPlan plan;
        try
        {
            provisioner ??= new WindowsSandboxProvisioner(new ProcessSandboxSetupElevator());
            plan = provisioner.BuildPlan(settings, context.ResolveWorkingDirectory(), Environment.UserName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or PlatformNotSupportedException)
        {
            await io.WriteLineAsync($"  [fail] 无法构造 provisioning 计划：{ex.Message}", cancellationToken)
                    .ConfigureAwait(false);
            return 1;
        }

        if (!File.Exists(plan.Components.SetupExecutablePath))
        {
            await io.WriteLineAsync($"  [fail] setup 可执行文件不存在：{plan.Components.SetupExecutablePath}",
                                    cancellationToken)
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

        var result = await provisioner.ProvisionAsync(plan, cancellationToken).ConfigureAwait(false);
        if (result.AuditWriteError is not null)
            await io.WriteLineAsync($"  [warn] {result.AuditWriteError}", cancellationToken).ConfigureAwait(false);

        return await ReportProvisionResultAsync(io, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     打印机器级副作用清单（固定文案，来自接入文档 §4）与重复 provisioning 警告。
    ///     Prints the machine-level side-effect list (fixed wording from the
    ///     integration document §4) and the re-provisioning warning.
    /// </summary>
    private static async Task PrintSideEffectsAsync(ICliConsole io, WindowsSandboxProvisionPlan plan,
                                                    CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  setup    : {plan.Components.SetupExecutablePath}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  home     : {plan.Components.SandboxHome}", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  tempRoot : {plan.TempRoot}（仅作为路径进入 payload，本命令不创建）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  writeRoots（将获得写授权）:", cancellationToken).ConfigureAwait(false);
        foreach (var root in plan.Payload.WriteRoots)
            await io.WriteLineAsync($"      {root}", cancellationToken).ConfigureAwait(false);

        await io.WriteLineAsync("  本命令将以 UAC 提权运行 setup.exe 一次，修改以下机器级状态：", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("    - 本地组 TinyHarnessUsers", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("    - 本地账户 TinyHarnessOffline / TinyHarnessOnline（随机密码、永不过期）",
                                cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("    - 注册表条目隐藏上述账户", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync("    - 防火墙规则，以及 offline 账户的 WFP BLOCK 过滤器（断网）", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("    - sandbox home 目录 ACL 与上述 write roots 的授权", cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync("  警告：重复 provisioning 会重置本命名空间账户（TinyHarnessOffline/TinyHarnessOnline）的密码，" +
                             "同机共用这套 fork 命名空间的其他 home 会受影响。本 fork 与 Codex 发布版使用独立命名空间" +
                             "（账户、组、防火墙、WFP 互不共享），但其共存尚未验收。本命令没有卸载或回滚。",
                             cancellationToken)
             .ConfigureAwait(false);
    }

    private static async Task<int> ReportProvisionResultAsync(ICliConsole io, WindowsSandboxProvisionResult result,
                                                             CancellationToken cancellationToken)
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

    private static bool IsBlankSettings(WindowsSandboxSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.SetupExecutablePath) &&
               string.IsNullOrWhiteSpace(settings.RunnerExecutablePath) &&
               string.IsNullOrWhiteSpace(settings.SandboxHome);
    }

    private static bool IsMissingOrRelative(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value);
    }

    /// <summary>
    ///     显式设置但非绝对（home 可留空使用固定默认，只有显式相对路径才是错误）。
    ///     Explicitly set but not absolute (the home may stay blank for the fixed
    ///     default; only an explicitly relative value is an error).
    /// </summary>
    private static bool IsExplicitRelative(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && !Path.IsPathFullyQualified(value);
    }

    private static string DescribeSetting(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "。" : $"：'{value}'。";
    }

    private static List<string> CollectPathProblems(WindowsSandboxSettings settings)
    {
        var problems = new List<string>();
        if (IsMissingOrRelative(settings.SetupExecutablePath))
            problems.Add("settings.windowsSandbox.setupExecutablePath 缺失或不是绝对路径" +
                         DescribeSetting(settings.SetupExecutablePath));

        if (IsMissingOrRelative(settings.RunnerExecutablePath))
            problems.Add("settings.windowsSandbox.runnerExecutablePath 缺失或不是绝对路径" +
                         DescribeSetting(settings.RunnerExecutablePath));

        if (IsExplicitRelative(settings.SandboxHome))
            problems.Add("settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）" +
                         DescribeSetting(settings.SandboxHome));

        return problems;
    }

    private static string PolicyDisplay(SandboxPolicyKind kind)
    {
        return kind switch
        {
            SandboxPolicyKind.ReadOnly       => "read-only",
            SandboxPolicyKind.WorkspaceWrite => "workspace-write",
            _                                => kind.ToString()
        };
    }
}
