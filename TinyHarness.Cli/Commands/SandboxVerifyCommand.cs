using System.Diagnostics;
using System.Text;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Cli.Commands;

/// <summary>
///     `tinyharness sandbox verify`：opt-in 的 Windows 沙箱隔离验收（真实系统测试）。要求沙箱已配置、
///     已启用且就绪——否则拒绝执行并提示先 provision；绝不自动 provisioning、绝不回退宿主执行。
///     组合逻辑与 run 共享（<see cref="SandboxCliComposition" />），knownSecrets 解析一致；每个用例按
///     策略种类克隆可信设置后走 <see cref="WindowsSandboxComposer.Compose" />，探针环境变量经
///     extraEnvironment 进入同一条清理环境路径。隐藏子命令 `sandbox verify-worker` 是宿主异常退出
///     用例的子进程模式。两个入口都不把输入发送给模型。
///     `tinyharness sandbox verify`: the opt-in Windows sandbox isolation
///     acceptance (a real system test). It requires the sandbox to be
///     configured, enabled, and ready — otherwise it refuses and suggests
///     provisioning first; it never provisions and never falls back to host
///     execution. Composition is shared with run
///     (<see cref="SandboxCliComposition" />) including the knownSecrets
///     resolution; every case clones the trusted settings per policy kind and
///     goes through <see cref="WindowsSandboxComposer.Compose" />, with the
///     probe variables entering the same cleaned-environment path via
///     extraEnvironment. The hidden `sandbox verify-worker` subcommand is the
///     child-process mode of the host-abnormal-exit case. Neither entry sends
///     input to a model.
/// </summary>
internal static class SandboxVerifyCommand
{
    /// <summary>存在 FAIL / INDETERMINATE / SKIPPED 时的退出码（0 仅当全部所选用例 PASS）。</summary>
    private const int AcceptanceFailedExitCode = 4;

    private const int WorkerRunnerStartupDelaySeconds = 5;
    private const int WorkerSleeperDurationSeconds    = 600;
    private const int WorkerCleanupGraceSeconds       = 10;

    public static async Task<int> ExecuteAsync(CommandContext             context,
                                               CliOptions                  options,
                                               CancellationToken           cancellationToken,
                                               WindowsSandboxBackendFactory? backendFactory = null,
                                               SandboxVerifyWorkerSpawner? workerSpawner  = null)
    {
        var io = context.Io;
        if (!OperatingSystem.IsWindows())
        {
            await io.WriteLineAsync("  [fail] sandbox verify 仅支持 Windows", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        var configPath = context.ResolveUserConfigPath();
        var userConfig = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings   = userConfig.Settings?.WindowsSandbox;

        await io.WriteLineAsync("TinyHarness sandbox verify", cancellationToken).ConfigureAwait(false);
        await io.WriteLineAsync($"  config   : {configPath}", cancellationToken).ConfigureAwait(false);

        if (settings is null || IsBlankSettings(settings))
        {
            await io
                 .WriteLineAsync("  [fail] 用户配置未设置 settings.windowsSandbox；sandbox verify 需要先配置组件路径" +
                                 "（sandboxHome 可省略，缺省使用独立固定默认 home）并运行 tinyharness sandbox provision",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        if (!settings.Enabled)
        {
            await io
                 .WriteLineAsync("  [fail] settings.windowsSandbox.enabled 不是 true；sandbox verify 不做任何事。" +
                                 "确认隔离配置后显式启用再验收。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var pathProblems = CollectPathProblems(settings);
        if (pathProblems.Count > 0)
        {
            foreach (var problem in pathProblems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            return 1;
        }

        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = settings.SetupExecutablePath.Trim(),
            RunnerExecutablePath = settings.RunnerExecutablePath.Trim(),
            SandboxHome          = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome)
        };
        var readiness = await new WindowsSandboxStateInspector(components).InspectAsync(cancellationToken)
                                          .ConfigureAwait(false);
        if (!readiness.Ready)
        {
            foreach (var problem in readiness.Problems)
                await io.WriteLineAsync($"  [fail] {problem}", cancellationToken).ConfigureAwait(false);

            await io
                 .WriteLineAsync("沙箱未就绪；先运行 tinyharness sandbox provision，verify 不会自动 provisioning。",
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var caseFilter = options.CaseFilter is { Count: > 0 } ? options.CaseFilter : null;
        var unknownCases = (caseFilter ?? WindowsSandboxAcceptanceRunner.CaseNames)
                          .Where(name => !WindowsSandboxAcceptanceRunner.CaseNames.Contains(name, StringComparer.Ordinal))
                          .ToArray();
        if (unknownCases.Length > 0)
        {
            await io
                 .WriteLineAsync($"  [fail] 未知验收用例名: {string.Join(", ", unknownCases)}；有效名称: " +
                                 string.Join(", ", WindowsSandboxAcceptanceRunner.CaseNames),
                                 cancellationToken)
                 .ConfigureAwait(false);
            return 1;
        }

        var caseCount = (caseFilter ?? WindowsSandboxAcceptanceRunner.CaseNames).Count;
        var workspaceRoot = ResolveWorkspace(options.VerifyWorkspace);

        // knownSecrets resolution is identical to run's so the acceptance
        // environment matches production composition semantics. An unavailable
        // key source (e.g. a credential store unreadable here) only means no
        // known secrets: verify never calls a model and must not be blocked by
        // the key.
        var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        var knownSecrets = ResolveKnownSecrets(context, resolution, cancellationToken);

        // The shared composition path (session-directory guard + Compose) also
        // validates the acceptance workspace before the confirmation gate.
        WindowsSandboxExecution preview;
        try
        {
            preview = SandboxCliComposition.TryCompose(settings, resolution.Config.SessionDirectory, workspaceRoot,
                                                       knownSecrets)
                     ?? throw new InvalidOperationException("settings.windowsSandbox.enabled is required for verify.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await io.WriteLineAsync($"  [fail] 沙箱组合失败：{ex.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        await PrintOverviewAsync(io, preview, components, caseCount, workspaceRoot, cancellationToken)
           .ConfigureAwait(false);

        if (!options.AssumeYes)
        {
            if (!io.IsInteractive)
            {
                await io
                     .WriteLineAsync("  [fail] 非交互环境必须使用 --yes 才能执行 sandbox verify；未执行任何命令",
                                     cancellationToken)
                     .ConfigureAwait(false);
                return 1;
            }

            if (!await CliPrompt.ConfirmAsync(io, "确认执行上述真实沙箱命令？", false, cancellationToken)
                                .ConfigureAwait(false))
            {
                await io.WriteLineAsync("  已取消；未执行任何命令。", cancellationToken).ConfigureAwait(false);
                return 0;
            }
        }

        WindowsSandboxBackendFactory factory = backendFactory
                                               ?? ((kind, extraEnvironment) => ComposeCase(settings, kind,
                                                                                            extraEnvironment,
                                                                                            workspaceRoot,
                                                                                            knownSecrets));
        SandboxVerifyWorkerSpawner spawner = workerSpawner
                                              ?? ((markerPath, holdSeconds) => StartWorkerProcess(markerPath,
                                                                                                 holdSeconds,
                                                                                                 workspaceRoot));
        var runner = new WindowsSandboxAcceptanceRunner(factory, workspaceRoot, spawner);
        WindowsSandboxAcceptanceReport report;
        try
        {
            report = await runner.RunAsync(caseFilter, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await io.WriteLineAsync($"  [fail] {ex.Message}", cancellationToken).ConfigureAwait(false);
            return 1;
        }

        return await ReportAsync(io, report, workspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     隐藏子命令 `sandbox verify-worker`：组合沙箱并启动长 sleeper，就绪后写 marker 并保持
    ///     holdSeconds 秒，供父进程 Kill 以检验遗弃回收。marker 只在 runner 启动稳定后写出；写出前
    ///     的任何失败都不写 marker（父进程按 marker 超时记 FAIL）。marker 路径必须位于 --workspace
    ///     的 artifacts 目录内，并由父进程在 spawn 前清理——本命令不删除任何传入路径。
    ///     绝不提权、绝不 provisioning。
    ///     The hidden `sandbox verify-worker` subcommand: composes the sandbox,
    ///     starts a long sleeper, writes the marker once the runner is settled,
    ///     and stays alive for holdSeconds so the parent can Kill it and check
    ///     abandoned-process reclamation. The marker is written only after the
    ///     runner startup settles; any earlier failure leaves no marker (the
    ///     parent records a marker timeout as FAIL). The marker path must stay
    ///     inside --workspace's artifacts directory and is cleared by the parent
    ///     before spawning — this command never deletes a caller-supplied path.
    ///     It never elevates and never provisions.
    /// </summary>
    public static async Task<int> ExecuteWorkerAsync(CommandContext    context,
                                                     CliOptions         options,
                                                     CancellationToken  cancellationToken)
    {
        var markerPath    = Path.GetFullPath(options.VerifyMarkerPath!);
        var holdSeconds   = options.VerifyHoldSeconds!.Value;
        var workspaceRoot = Path.GetFullPath(options.VerifyWorkspace!);
        // The worker only ever writes the marker; the parent clears a stale one
        // before spawning. Constraining the marker to the acceptance
        // workspace's artifacts directory keeps a manually invoked worker from
        // writing outside the expected layout.
        var markerDirectory = Path.Combine(workspaceRoot, "artifacts");
        if (!Workspace.IsInside(markerDirectory, markerPath))
        {
            await context.Io.WriteErrorLineAsync(
                $"sandbox verify-worker: marker 必须位于验收工作区的 artifacts 目录内（{markerDirectory}）；" +
                $"收到 '{markerPath}'；退出。",
                cancellationToken)
                         .ConfigureAwait(false);
            return 1;
        }

        var configPath = context.ResolveUserConfigPath();
        var userConfig = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);
        var settings   = userConfig.Settings?.WindowsSandbox;
        if (settings is not { Enabled: true })
        {
            await context.Io.WriteErrorLineAsync("sandbox verify-worker: settings.windowsSandbox.enabled 不是 true；退出。",
                                                 cancellationToken)
                         .ConfigureAwait(false);
            return 1;
        }

        try
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            var waitfor = string.IsNullOrWhiteSpace(systemRoot)
                ? null
                : Path.Combine(systemRoot, "System32", "waitfor.exe");
            if (waitfor is null || !File.Exists(waitfor))
            {
                await context.Io.WriteErrorLineAsync("sandbox verify-worker: 缺少 waitfor.exe（System32），无法布置长 sleeper。",
                                                     cancellationToken)
                             .ConfigureAwait(false);
                return 1;
            }

            // Composition matches the parent verify: the same session-directory
            // guard and knownSecrets resolution as run.
            var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
            var knownSecrets = ResolveKnownSecrets(context, resolution, cancellationToken);
            var execution = SandboxCliComposition.TryCompose(settings, resolution.Config.SessionDirectory,
                                                             workspaceRoot, knownSecrets);
            if (execution is null)
            {
                await context.Io.WriteErrorLineAsync("sandbox verify-worker: 沙箱未启用；退出。", cancellationToken)
                             .ConfigureAwait(false);
                return 1;
            }

            var plan = new PreparedProcessExecution(waitfor,
                                                    ["/t", WorkerSleeperDurationSeconds.ToString(),
                                                     $"th-{Guid.NewGuid():N}"],
                                                    workspaceRoot, WorkerSleeperDurationSeconds + 60, false, null);
            var artifactDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath))!;
            var stdout = new VerifyOutputCapture(Path.Combine(artifactDirectory, "worker.stdout.txt"));
            var stderr = new VerifyOutputCapture(Path.Combine(artifactDirectory, "worker.stderr.txt"));
            using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runTask = execution.Backend.ExecuteAsync(plan, stdout, stderr, workerCancellation.Token);

            await Task.Delay(TimeSpan.FromSeconds(WorkerRunnerStartupDelaySeconds), cancellationToken)
                      .ConfigureAwait(false);
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                      .ConfigureAwait(false);

            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(holdSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The parent never arrived; clean up instead of lingering.
                await workerCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await runTask.WaitAsync(TimeSpan.FromSeconds(WorkerCleanupGraceSeconds), CancellationToken.None)
                                 .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Best-effort bounded cleanup of this worker's own run.
                }
            }

            stdout.Complete();
            stderr.Complete();
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await context.Io.WriteErrorLineAsync($"sandbox verify-worker failed: {ex.Message}", cancellationToken)
                         .ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>
    ///     与 run 一致地解析 knownSecrets；密钥源不可读（如凭据存储在当前平台不可用）只意味着没有
    ///     已知 secret——verify 不调用模型，不得被密钥可用性阻塞。
    ///     Resolves knownSecrets exactly like run; an unreadable key source (for
    ///     example a credential store unavailable on this platform) merely means
    ///     no known secrets — verify never calls a model and must not be blocked
    ///     by key availability.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ResolveKnownSecrets(
        CommandContext context, ConfigResolution resolution, CancellationToken cancellationToken)
    {
        try
        {
            var apiKey = ApiKeyReader.Read(resolution.Config.ApiKeyEnvironmentVariable,
                                           resolution.Config.ApiKeyCredentialTarget, context.Credentials,
                                           resolution.ProfileName);
            return SandboxCliComposition.KnownSecrets(resolution.Config, apiKey);
        }
        catch (ConfigException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    ///     按策略种类与用例附加环境克隆可信设置并组合：探针变量并入 extraEnvironment，与 run 走同一条
    ///     清理环境组合路径（allowlist + TEMP/TMP 重定向 + secret 移除），绝不旁路拼 spawn env。
    ///     Clones the trusted settings per policy kind plus case-scoped extra
    ///     environment and composes: probe variables merge into
    ///     extraEnvironment, going through the same cleaned-environment
    ///     composition path as run (allowlist + TEMP/TMP redirect + secret
    ///     removal); the spawn env is never hand-built around it.
    /// </summary>
    private static WindowsSandboxExecution ComposeCase(WindowsSandboxSettings             settings,
                                                       SandboxPolicyKind                  policyKind,
                                                       IReadOnlyDictionary<string, string>? extraEnvironment,
                                                       string                             workspaceRoot,
                                                       IReadOnlyDictionary<string, string> knownSecrets)
    {
        var merged = extraEnvironment is null
            ? settings.ExtraEnvironment
            : MergeEnvironment(settings.ExtraEnvironment, extraEnvironment);
        var caseSettings = settings with { Policy = policyKind, ExtraEnvironment = merged };
        return WindowsSandboxComposer.Compose(caseSettings, workspaceRoot, knownSecrets);
    }

    private static IReadOnlyDictionary<string, string> MergeEnvironment(
        IReadOnlyDictionary<string, string>? baseEnvironment, IReadOnlyDictionary<string, string> extraEnvironment)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (baseEnvironment is not null)
            foreach (var (name, value) in baseEnvironment)
                merged[name] = value;

        foreach (var (name, value) in extraEnvironment) merged[name] = value;

        return merged;
    }

    private static string ResolveWorkspace(string? requested)
    {
        return requested is null
            ? Path.Combine(Path.GetTempPath(), "TinyHarness",
                           $"sandbox-verify-{DateTimeOffset.Now:yyyyMMdd-HHmmss}")
            : Path.GetFullPath(requested);
    }

    private static ISandboxVerifyWorkerProcess StartWorkerProcess(string markerPath, int holdSeconds,
                                                                  string workspaceRoot)
    {
        var executable = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Cannot determine the current process executable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        };
        // Mirror the process-probe precedent: a dotnet host needs the entry
        // assembly as the first argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);

        foreach (var argument in new[]
                 {
                     "sandbox", "verify-worker", "--marker", markerPath, "--hold-seconds", holdSeconds.ToString(),
                     "--workspace", workspaceRoot
                 })
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
                     ?? throw new InvalidOperationException("Failed to start the sandbox verify-worker process.");
        return new WorkerProcessHandle(process);
    }

    private static async Task PrintOverviewAsync(ICliConsole io, WindowsSandboxExecution preview,
                                                 WindowsSandboxComponents components, int caseCount,
                                                 string workspaceRoot, CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  setup    : {components.SetupExecutablePath}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  runner   : {components.RunnerExecutablePath}", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  home     : {components.SandboxHome}" +
                                (string.Equals(components.SandboxHome,
                                               WindowsSandboxComponents.DefaultSandboxHome,
                                               StringComparison.OrdinalIgnoreCase)
                                    ? "（独立固定默认 home）"
                                    : string.Empty),
                                cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync(
                            $"  versions : 本 build 期望 setup v{WindowsSandboxComponents.SetupVersion} / ipc v{WindowsSandboxComponents.IpcVersion}",
                            cancellationToken)
             .ConfigureAwait(false);
        await io.WriteLineAsync($"  account  : {components.OfflineUsername}（offline，断网）", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync($"  workspace: {workspaceRoot}（host 侧布置 fixture，产物保留）", cancellationToken)
                .ConfigureAwait(false);
        await io.WriteLineAsync("  policies : read-only 与 workspace-write 两种都会被执行", cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync($"  将执行 {caseCount} 条真实沙箱命令：每条命令一次 ACL refresh（普通权限）与一个 runner 进程。",
                             cancellationToken)
                .ConfigureAwait(false);
        await io
             .WriteLineAsync("  本命令不执行 provisioning、不修改机器配置（账户/ACL/注册表/防火墙），也不回退宿主执行；" +
                             "这是真实系统测试，应在获得授权、可接受该测试的环境执行。",
                             cancellationToken)
                .ConfigureAwait(false);
    }

    private static async Task<int> ReportAsync(ICliConsole io, WindowsSandboxAcceptanceReport report,
                                               string workspaceRoot, CancellationToken cancellationToken)
    {
        await io.WriteLineAsync($"  workspace: {workspaceRoot}（产物保留）", cancellationToken).ConfigureAwait(false);
        foreach (var result in report.Cases)
        {
            await io.WriteLineAsync($"  [{result.Verdict,-13}] {result.Name} ({result.Duration})", cancellationToken)
                    .ConfigureAwait(false);
            if (result.Verdict == SandboxAcceptanceVerdict.Pass) continue;

            if (result.ActualSummary is { Length: > 0 } actual)
                await io.WriteLineAsync($"      actual : {actual}", cancellationToken).ConfigureAwait(false);

            if (result.Detail is { Length: > 0 } detail)
                await io.WriteLineAsync($"      detail : {detail}", cancellationToken).ConfigureAwait(false);
        }

        var passed = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Pass);
        var failed = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Fail);
        var indeterminate = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Indeterminate);
        var skipped = report.Cases.Count(result => result.Verdict == SandboxAcceptanceVerdict.Skipped);
        await io
             .WriteLineAsync($"  合计: {report.Cases.Count} 用例；PASS {passed}，FAIL {failed}，INDETERMINATE {indeterminate}，SKIPPED {skipped}",
                             cancellationToken)
             .ConfigureAwait(false);

        if (report.ReportPath is { } reportPath)
            await io.WriteLineAsync($"  report   : {reportPath}", cancellationToken).ConfigureAwait(false);
        else
            await io.WriteLineAsync("  [warn] 报告文件写入失败；上面是完整结果，但未落盘。", cancellationToken)
                    .ConfigureAwait(false);

        await io.WriteLineAsync("  limitations 已写入报告：结论范围以报告中的局限清单为准。", cancellationToken)
                .ConfigureAwait(false);

        var clean = report.Cases.Count > 0 &&
                    report.Cases.All(result => result.Verdict == SandboxAcceptanceVerdict.Pass);
        return clean ? 0 : AcceptanceFailedExitCode;
    }

    private static bool IsBlankSettings(WindowsSandboxSettings settings)
    {
        return string.IsNullOrWhiteSpace(settings.SetupExecutablePath) &&
               string.IsNullOrWhiteSpace(settings.RunnerExecutablePath) &&
               string.IsNullOrWhiteSpace(settings.SandboxHome);
    }

    private static List<string> CollectPathProblems(WindowsSandboxSettings settings)
    {
        var problems = new List<string>();
        foreach (var (value, field) in new[]
                 {
                     (settings.SetupExecutablePath, "setupExecutablePath"),
                     (settings.RunnerExecutablePath, "runnerExecutablePath")
                 })
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                problems.Add($"settings.windowsSandbox.{field} 缺失或不是绝对路径" +
                             (string.IsNullOrWhiteSpace(value) ? "。" : $"：'{value}'。"));

        // The home may stay blank (fixed independent default); only an
        // explicitly relative value is a configuration error.
        if (!string.IsNullOrWhiteSpace(settings.SandboxHome) && !Path.IsPathFullyQualified(settings.SandboxHome))
            problems.Add($"settings.windowsSandbox.sandboxHome 不是绝对路径（留空则使用固定默认 home）：'{settings.SandboxHome}'。");

        return problems;
    }

    /// <summary>
    ///     verify-worker 的进程句柄：Kill 只终止 worker 本身（不杀树）——遗弃的 runner/目标进程正是
    ///     host-exit-reclaim 用例要观测的对象。
    ///     The verify-worker process handle: Kill terminates only the worker
    ///     itself (never the tree) — the abandoned runner/target processes are
    ///     exactly what the host-exit-reclaim case observes.
    /// </summary>
    private sealed class WorkerProcessHandle(Process process) : ISandboxVerifyWorkerProcess
    {
        public void Kill()
        {
            process.Kill();
        }
    }
}

/// <summary>
///     CLI 侧的轻量输出捕获（Core 的 internal <c>ProcessOutputCapture</c> 对 CLI 不可见）：增量接收
///     每个输出块、把完整输出流式写入 artifact 文件、在内存中只保留 head+tail 的有界模型视图，并记录
///     原始字符数与截断方式。供 verify-worker 使用；不做跨块脱敏（worker 上下文没有已知 secret）。
///     A lightweight CLI-side output capture (Core's internal
///     <c>ProcessOutputCapture</c> is invisible to the CLI): it receives every
///     chunk incrementally, streams the full output to an artifact file, keeps
///     only a bounded head+tail model view in memory, and records the original
///     character count and the truncation mode. Used by verify-worker; no
///     cross-chunk redaction (the worker context has no known secrets).
/// </summary>
internal sealed class VerifyOutputCapture(string artifactPath, int modelCharacterLimit = 16 * 1024)
    : IProcessOutputCapture
{
    private readonly int           _headLimit = modelCharacterLimit / 2;
    private readonly int           _tailLimit = modelCharacterLimit - modelCharacterLimit / 2;
    private readonly StringBuilder _view      = new(modelCharacterLimit);
    private          FileStream?   _artifactStream;
    private          StreamWriter? _artifactWriter;
    private          long          _totalCharacters;
    private          bool          _truncated;

    public async ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken)
    {
        if (chunk.IsEmpty) return;

        if (_artifactWriter is null)
        {
            _artifactStream = new FileStream(artifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                             16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _artifactWriter = new StreamWriter(_artifactStream, new UTF8Encoding(false));
        }

        await _artifactWriter.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        _totalCharacters += chunk.Length;
        if (!_truncated)
        {
            _view.Append(chunk.Span);
            if (_view.Length <= _headLimit + _tailLimit) return;

            _truncated = true;
            _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
            return;
        }

        _view.Append(chunk.Span);
        if (_view.Length > _headLimit + _tailLimit) _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
    }

    public CapturedProcessOutput Complete()
    {
        CloseArtifact();
        if (!_truncated)
        {
            TryDeleteArtifact();
            return new CapturedProcessOutput(_view.ToString(), false, _totalCharacters, null);
        }

        var omitted = Math.Max(0, _totalCharacters - _view.Length);
        var content = _view.ToString(0, _headLimit)                                              +
                      $"\n... [truncated {omitted} chars; full output: {artifactPath}] ...\n" +
                      _view.ToString(_view.Length - _tailLimit, _tailLimit);
        return new CapturedProcessOutput(content, true, _totalCharacters, artifactPath);
    }

    public void Discard()
    {
        CloseArtifact();
        TryDeleteArtifact();
    }

    private void CloseArtifact()
    {
        _artifactWriter?.Dispose();
        _artifactWriter = null;
        _artifactStream = null;
    }

    private void TryDeleteArtifact()
    {
        try
        {
            File.Delete(artifactPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale temporary artifact is safer than masking the command result.
        }
    }
}
