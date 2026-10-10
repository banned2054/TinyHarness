using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     按策略种类与用例附加环境组合一次沙箱执行的工厂。extraTargetEnvironment 必须经与 ShellTool
///     一致的清理环境路径进入目标命令——实现方应把它合并进可信设置的 extraEnvironment 再走
///     <see cref="WindowsSandboxComposer.Compose" />，绝不旁路拼 spawn env。
///     Composes one sandbox execution for a policy kind plus case-scoped extra
///     environment. The extra variables must reach the target command through
///     the same cleaned-environment path ShellTool uses — implementations merge
///     them into the trusted settings' extraEnvironment and call
///     <see cref="WindowsSandboxComposer.Compose" />; bypassing that path to
///     hand-build the spawn env is never acceptable.
/// </summary>
public delegate WindowsSandboxExecution WindowsSandboxBackendFactory(
    SandboxPolicyKind                    policyKind,
    IReadOnlyDictionary<string, string>? extraTargetEnvironment);

/// <summary>
///     host-exit-reclaim 用例可控杀除的 worker 进程句柄。Kill 只终止该进程本身（不杀进程树），
///     用于模拟宿主异常退出后遗弃 runner/目标进程的场景。
///     A controllable worker process handle for the host-exit-reclaim case.
///     Kill terminates only this process (never the tree), simulating a host
///     that dies and abandons the runner and target processes.
/// </summary>
public interface ISandboxVerifyWorkerProcess
{
    void Kill();
}

/// <summary>
///     启动隐藏的 verify-worker 子进程：就绪后写 marker，再保持 holdSeconds 秒。真实实现是本 CLI
///     自身的 `sandbox verify-worker` 进程；离线测试注入 fake。
///     Spawns the hidden verify-worker child process: it writes the marker once
///     ready, then stays alive for holdSeconds. The real implementation is this
///     CLI's own `sandbox verify-worker` process; offline tests inject a fake.
/// </summary>
public delegate ISandboxVerifyWorkerProcess SandboxVerifyWorkerSpawner(string markerPath, int holdSeconds);

/// <summary>
///     验收运行的期限与可调参数。默认值面向真实沙箱（每命令一次 refresh + runner）；离线测试注入
///     缩短的期限与替换的地址/curl 解析器。
///     Deadlines and tunables of an acceptance run. Defaults target the real
///     sandbox (one refresh + runner per command); offline tests inject
///     shortened deadlines and replacement address/curl resolvers.
/// </summary>
public sealed record WindowsSandboxAcceptanceLimits
{
    public static readonly WindowsSandboxAcceptanceLimits Default = new();

    /// <summary>整个验收矩阵的总期限；到期后未开始的用例记 SKIPPED、被中断的用例记 FAIL。</summary>
    public TimeSpan OverallDeadline { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>回收/遗弃用例等待镜像消失的扫描窗口。</summary>
    public TimeSpan ProcessScanTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ProcessScanInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>host-exit-reclaim 等待 worker marker 就绪的期限。</summary>
    public TimeSpan MarkerWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>terminate-active 启动后到取消之间的等待。</summary>
    public TimeSpan TerminateCancelDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>verify-worker 保持存活的秒数（须大于父进程 kill + 扫描窗口）。</summary>
    public int WorkerHoldSeconds { get; init; } = 300;

    /// <summary>large-output 用例生成的目标字符数。</summary>
    public int LargeOutputTargetCharacters { get; init; } = 4_000_000;

    /// <summary>模型输出视图的字符上限（head+tail 截断，与 ShellTool 缺省一致）。</summary>
    public int ModelOutputCharacterLimit { get; init; } = 16 * 1024;

    /// <summary>外部击杀与回收扫描针对的 runner 镜像名（不含 .exe）。</summary>
    public string RunnerImageName { get; init; } = "codex-command-runner";

    /// <summary>
    ///     回收/遗弃扫描覆盖的镜像名（不含 .exe）；测试可替换以模拟残留。sleeper 用 powershell
    /// （Start-Sleep 不依赖网络；waitfor 走 SMB mailslot，会被断网过滤秒退，不是有效 sleeper）。
    ///     Survivor image names covered by the reclamation/abandonment scans
    ///     (without .exe); tests replace them to simulate leftovers. The sleeper
    ///     is powershell (Start-Sleep needs no network; waitfor uses SMB
    ///     mailslots and dies instantly under the network block, so it is not a
    ///     valid sleeper inside the sandbox).
    /// </summary>
    public IReadOnlyList<string> SurvivorImageNames { get; init; } = ["codex-command-runner", "powershell"];

    /// <summary>
    ///     断网用例的本机非回环地址解析；null 时用 Dns.GetHostAddresses 解析。测试注入以稳定
    ///     INDETERMINATE/PASS/FAIL 三态。
    ///     The non-loopback local address resolver for the network case; null
    ///     resolves via Dns.GetHostAddresses. Tests inject it to stabilize the
    ///     INDETERMINATE/PASS/FAIL triad.
    /// </summary>
    public Func<string?>? NonLoopbackAddressResolver { get; init; }

    /// <summary>curl.exe 绝对路径解析；null 时按 %SystemRoot%\System32\curl.exe 探测。</summary>
    public Func<string?>? CurlPathResolver { get; init; }
}

/// <summary>
///     M11.5 Windows 沙箱隔离验收运行器：在专用验收工作区内按固定矩阵执行真实沙箱命令，逐例判定
///     PASS/FAIL/INDETERMINATE/SKIPPED 并写 source-gen JSON 报告到 &lt;sandbox home&gt;\.sandbox\。
///     每用例独立组合/执行（每命令一次 refresh + runner 是已知开销，正确性优先）；单用例失败记录后
///     继续其余用例。后端经 <see cref="WindowsSandboxBackendFactory" /> 注入——真实运行由 CLI 传调用
///     Compose 的工厂，离线测试传 fake。绝不自动 provisioning、绝不回退宿主执行。
///     The M11.5 Windows sandbox isolation acceptance runner: it executes real
///     sandboxed commands from a fixed matrix inside a dedicated acceptance
///     workspace, judges each case PASS/FAIL/INDETERMINATE/SKIPPED, and writes a
///     source-generated JSON report into &lt;sandbox home&gt;\.sandbox\. Every case
///     composes and executes on its own (one refresh + runner per command is a
///     known cost; correctness first); a failed case is recorded and the matrix
///     continues. The backend arrives via <see cref="WindowsSandboxBackendFactory" />
///     — the CLI passes a factory calling Compose, offline tests pass a fake. It
///     never provisions and never falls back to host execution.
/// </summary>
public sealed class WindowsSandboxAcceptanceRunner
{
    private const int IdentityTimeoutSeconds     = 60;
    private const int DenyProbeTimeoutSeconds    = 120;
    private const int WriteProbeTimeoutSeconds   = 60;
    private const int NetworkProbeTimeoutSeconds = 30;
    private const int ExitProbeTimeoutSeconds    = 30;
    private const int TimeoutCaseCommandSeconds  = 8;
    private const int SleeperDurationSeconds     = 300;
    private const int LargeOutputTimeoutSeconds  = 120;

    private static readonly IReadOnlyList<string> BaseLimitations =
    [
        "进程存活扫描按镜像名 + 启动时间过滤识别：同名无关进程可能被误判为残留，改名或复用 PID 的目标进程无法识别。",
        "断网观测语义：非回环阻断由 connect 失败推断（对照 host 侧全接口监听）；回环可达性仅作观测记录、不构成门控；WFP 的 ICMP/DNS 过滤未被直接探测。",
        "断网对照使用本机地址：Windows 对连接本机 IP 的流量走环回快速路径，可能绕过按远程地址匹配的防火墙规则；按用户作用域（LocalUserAuthorizedList）的 BLOCK 规则是否对沙箱账户进程生效依赖防火墙的归因行为，本用例无法区分这两种未拦截原因。",
        "192,true 只是 runner 的超时判定，不构成进程树已完整回收的证明。",
        "deny-probe 退出 21 属探针/harness fixture 配置问题，不代表隔离失效；20 才是隔离失效信号。",
        "host-exit-reclaim 的幸存扫描窗口有界：窗口内未消失即判 FAIL，窗口结束后自然退出的进程不予区分。",
        "回收失败或进程树状态未知的用例按 FAIL 记录；本报告不进一步区分回收失败与隔离语义失败的根因层次。"
    ];

    private readonly WindowsSandboxBackendFactory         _backendFactory;
    private readonly Func<string, IProcessOutputCapture>? _captureFactory;
    private readonly WindowsSandboxAcceptanceLimits       _limits;
    private readonly SandboxVerifyWorkerSpawner?          _workerSpawner;
    private readonly string                               _workspaceRoot;

    private List<AcceptanceCaseDefinition> _caseDefinitions = [];
    private HashSet<int>                   _baselineIds     = [];
    private DateTimeOffset                 _runStartedLocal = DateTimeOffset.Now;
    private WindowsSandboxExecution?       _snapshotExecution;
    private string                         _artifactDirectory = string.Empty;

    public WindowsSandboxAcceptanceRunner(WindowsSandboxBackendFactory backendFactory,
                                          string                        workspaceRoot,
                                          SandboxVerifyWorkerSpawner?   workerSpawner  = null,
                                          Func<string, IProcessOutputCapture>? captureFactory = null,
                                          WindowsSandboxAcceptanceLimits? limits         = null)
    {
        ArgumentNullException.ThrowIfNull(backendFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (!Path.IsPathFullyQualified(workspaceRoot))
            throw new ArgumentException(
                $"The acceptance workspace root must be a fully qualified absolute path: '{workspaceRoot}'.",
                nameof(workspaceRoot));

        _backendFactory = backendFactory;
        _workspaceRoot  = workspaceRoot;
        _workerSpawner  = workerSpawner;
        _captureFactory = captureFactory;
        _limits         = limits ?? WindowsSandboxAcceptanceLimits.Default;
    }

    /// <summary>固定矩阵的稳定用例名（--case 过滤与命令数预告的依据）。</summary>
    public static IReadOnlyList<string> CaseNames =>
    [
        "identity-whoami", "deny-probe", "write-denied-outside-roots", "readonly-workspace-write-denied",
        "metadata-protected", "workspace-write-allowed", "network-denied", "exit-natural-23", "exit-natural-192",
        "timeout-finite", "terminate-active", "cancel-startup", "runner-broken-pipe", "large-output",
        "host-exit-reclaim"
    ];

    /// <summary>
    ///     执行选定的用例并写报告。caseFilter 为 null 或空时执行全矩阵；未知名称抛
    ///     ArgumentException。调用方取消以 OperationCanceledException 向上传播；总期限到期不抛出，
    ///     改为把剩余用例记 SKIPPED、被中断的用例记 FAIL。
    ///     Runs the selected cases and writes the report. A null or empty
    ///     caseFilter runs the full matrix; unknown names throw
    ///     ArgumentException. Caller cancellation propagates as
    ///     OperationCanceledException; the overall deadline never throws — the
    ///     remaining cases become SKIPPED and the interrupted one FAIL.
    /// </summary>
    public async Task<WindowsSandboxAcceptanceReport> RunAsync(IReadOnlyList<string>? caseFilter = null,
                                                               CancellationToken cancellationToken = default)
    {
        var selected = ValidateCaseFilter(caseFilter);

        Directory.CreateDirectory(_workspaceRoot);
        _artifactDirectory = Path.Combine(_workspaceRoot, "artifacts");
        Directory.CreateDirectory(_artifactDirectory);

        // Snapshot composition validates composition up front and supplies the
        // component/policy evidence, the offline account name, and the
        // deny-probe derivation root for the whole report.
        _snapshotExecution = _backendFactory(SandboxPolicyKind.WorkspaceWrite, null);
        var snapshot = await BuildComponentSnapshotAsync(_snapshotExecution, cancellationToken).ConfigureAwait(false);

        _baselineIds     = SandboxSurvivorScan.CaptureBaseline(_limits.SurvivorImageNames);
        _runStartedLocal = DateTimeOffset.Now;
        var startedAtUtc = DateTimeOffset.UtcNow;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_limits.OverallDeadline);

        _caseDefinitions = BuildCaseDefinitions();
        var results = new List<SandboxAcceptanceCaseResult>();
        foreach (var definition in _caseDefinitions)
        {
            if (!selected.Contains(definition.Name)) continue;

            if (overall.IsCancellationRequested)
            {
                results.Add(Result(definition, SandboxAcceptanceVerdict.Skipped, null, "总期限已到期，用例未开始。"));
                continue;
            }

            var stopwatch = Stopwatch.StartNew();
            try
            {
                results.Add(await definition.Body(overall.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (overall.IsCancellationRequested)
            {
                results.Add(Result(definition, SandboxAcceptanceVerdict.Fail, null, "总期限到期，用例被中断。",
                                   stopwatch));
            }
            catch (Exception ex)
            {
                // A failed case is recorded; the matrix continues.
                results.Add(Result(definition, SandboxAcceptanceVerdict.Fail, null, $"用例执行异常：{ex.Message}",
                                   stopwatch));
            }
        }

        var limitations = new List<string>(BaseLimitations);
        if (snapshot.MarkerError is { } markerError) limitations.Add($"setup marker 版本无法读取：{markerError}");

        var report = new WindowsSandboxAcceptanceReport
        {
            TimestampUtc       = startedAtUtc.ToString("O"),
            MachineName        = Environment.MachineName,
            OsVersion          = Environment.OSVersion.VersionString,
            OfflineAccountName = _snapshotExecution.Components.OfflineUsername,
            PolicyKinds = _caseDefinitions.Where(definition => selected.Contains(definition.Name))
                                          .Select(definition => PolicyKindDisplay(definition.Kind))
                                          .Distinct(StringComparer.Ordinal)
                                          .ToArray(),
            WorkspaceRoot = _workspaceRoot,
            Components    = snapshot.Snapshot,
            Cases         = results,
            Limitations   = limitations
        };
        return report with
        {
            ReportPath = await TryWriteReportAsync(report, startedAtUtc, cancellationToken).ConfigureAwait(false)
        };
    }

    // ---- 用例矩阵 ----

    private sealed record AcceptanceCaseDefinition(
        string                                                    Name,
        SandboxPolicyKind                                         Kind,
        string                                                    Expectation,
        string                                                    CommandSummary,
        Func<CancellationToken, Task<SandboxAcceptanceCaseResult>> Body);

    private List<AcceptanceCaseDefinition> BuildCaseDefinitions()
    {
        return
        [
            new("identity-whoami", SandboxPolicyKind.ReadOnly, "whoami 输出包含所选 offline 沙箱账户名",
                "whoami.exe", IdentityWhoamiAsync),
            new("deny-probe", SandboxPolicyKind.WorkspaceWrite,
                "deny-probe 经完整链路执行并退出 0（deny-read ACL 生效）", "codex-windows-managed-deny-probe.exe",
                DenyProbeAsync),
            new("write-denied-outside-roots", SandboxPolicyKind.WorkspaceWrite,
                "向所有写根之外写入被拒绝（非零退出且文件未创建）", "cmd /c copy NUL <写根之外>",
                WriteDeniedOutsideRootsAsync),
            new("readonly-workspace-write-denied", SandboxPolicyKind.ReadOnly, "read-only 策略下向工作区写入被拒绝",
                "cmd /c copy NUL <工作区>", ReadonlyWorkspaceWriteDeniedAsync),
            new("metadata-protected", SandboxPolicyKind.WorkspaceWrite,
                "workspace-write 策略下向 <ws>\\.git\\ 写入被拒绝（元数据只读保护）", "cmd /c copy NUL <ws>\\.git\\",
                MetadataProtectedAsync),
            new("workspace-write-allowed", SandboxPolicyKind.WorkspaceWrite,
                "workspace-write 策略下向工作区普通路径写入成功（正对照）", "cmd /c copy NUL <工作区普通路径>",
                WorkspaceWriteAllowedAsync),
            new("network-denied", SandboxPolicyKind.WorkspaceWrite,
                "沙箱内对本机非回环 IP 的连接被阻断；回环结果仅作观测", "curl.exe → 127.0.0.1 / 本机非回环 IP",
                NetworkDeniedAsync),
            new("exit-natural-23", SandboxPolicyKind.WorkspaceWrite, "自然退出 23：ExitCode=23 且 TimedOut=false",
                "cmd /c exit 23", ExitNaturalAsync(23)),
            new("exit-natural-192", SandboxPolicyKind.WorkspaceWrite,
                "自然退出 192：ExitCode=192 且 TimedOut=false（绝不判为超时）", "cmd /c exit 192", ExitNaturalAsync(192)),
            new("timeout-finite", SandboxPolicyKind.WorkspaceWrite,
                "有限命令超时：TimedOut=true（runner 超时帧契约为 192,true）", $"长 sleeper + {TimeoutCaseCommandSeconds}s 命令超时",
                TimeoutFiniteAsync),
            new("terminate-active", SandboxPolicyKind.WorkspaceWrite,
                "运行中取消 CancellationToken：以 OperationCanceledException 呈现且无镜像残留", "长 sleeper + 中段取消",
                TerminateActiveAsync),
            new("cancel-startup", SandboxPolicyKind.WorkspaceWrite, "启动阶段即取消：结果正确反映取消且无镜像残留",
                "启动后立即取消", CancelStartupAsync),
            new("runner-broken-pipe", SandboxPolicyKind.WorkspaceWrite,
                "外部击杀本用例 runner：后端报协议失败且不补造退出码，事后无目标进程残留",
                "外部 kill codex-command-runner", RunnerBrokenPipeAsync),
            new("large-output", SandboxPolicyKind.WorkspaceWrite,
                "约 4MB 输出：模型视图按预算截断、完整 artifact 落盘、报告原始长度与截断方式",
                "cmd /c type large-output.txt", LargeOutputAsync),
            new("host-exit-reclaim", SandboxPolicyKind.WorkspaceWrite,
                "宿主（verify-worker）被 Kill 后有界窗口内无 runner/目标镜像幸存",
                "sandbox verify-worker + Process.Kill", HostExitReclaimAsync)
        ];
    }

    private async Task<SandboxAcceptanceCaseResult> IdentityWhoamiAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("identity-whoami");
        var stopwatch  = Stopwatch.StartNew();
        if (SystemTool("whoami.exe") is not { } whoami)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "host 缺少 whoami.exe（System32），无法执行身份对照。", stopwatch);

        var account = _snapshotExecution!.Components.OfflineUsername;
        var run = await ExecuteOnceAsync(SandboxPolicyKind.ReadOnly, null,
                                         new PreparedProcessExecution(whoami, [], _workspaceRoot,
                                                                      IdentityTimeoutSeconds, false, null),
                                         "identity-whoami", cancellationToken).ConfigureAwait(false);

        var actual = Describe(run, $"stdout 首行: {FirstLine(run.Stdout?.Content)}");
        var passed = run.Exception is null && run.Result is { ExitCode: 0, TimedOut: false } &&
                     run.Stdout is { } stdout &&
                     stdout.Content.Contains(account, StringComparison.OrdinalIgnoreCase);
        return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual,
                      $"期望账户名: {account}", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> DenyProbeAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("deny-probe");
        var stopwatch  = Stopwatch.StartNew();
        var runnerDirectory = Path.GetDirectoryName(_snapshotExecution!.Components.RunnerExecutablePath)!;
        var probeSource = Path.Combine(runnerDirectory, "codex-windows-managed-deny-probe.exe");
        if (!File.Exists(probeSource))
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          $"未找到 codex-windows-managed-deny-probe.exe（期望与 runner 同目录: {runnerDirectory}）。",
                          stopwatch);

        if (SystemTool("version.dll") is not { } versionDll)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "host 缺少 System32\\version.dll，无法布置探针模块 fixture。", stopwatch);

        // The denied fixtures must sit outside the read roots of the policy that
        // actually runs: the fixed profiles carry no explicit deny-read entries,
        // so deny-read semantics is "no ACL grant outside the read roots".
        var execution = _backendFactory(SandboxPolicyKind.WorkspaceWrite, null);
        var readRoots = execution.Policy.CreateSetupPayload(execution.Components, _workspaceRoot,
                                                            Environment.UserName, true).ReadRoots;
        var deniedRoot   = DeniedFixtureRoot();
        var deniedText   = Path.Combine(deniedRoot, "denied.txt");
        var deniedModule = Path.Combine(deniedRoot, "denied.dll");
        if (readRoots.Any(root => Workspace.IsInside(root, deniedText) || Workspace.IsInside(root, deniedModule)))
            return Result(definition, SandboxAcceptanceVerdict.Fail, null,
                          $"denied fixture（{deniedRoot}）落在有效策略读根内，fixture 与策略冲突；读根: {string.Join("; ", readRoots)}",
                          stopwatch);

        var allowedDirectory = Path.Combine(_workspaceRoot, "probe-allowed");
        var allowedText      = Path.Combine(allowedDirectory, "allowed.txt");
        var allowedModule    = Path.Combine(allowedDirectory, "allowed.dll");
        var probeCopy        = Path.Combine(_workspaceRoot, "probe", "codex-windows-managed-deny-probe.exe");
        Directory.CreateDirectory(allowedDirectory);
        Directory.CreateDirectory(deniedRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(probeCopy)!);
        await File.WriteAllTextAsync(allowedText, "ALLOW-CONTROL", cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(deniedText, "DENIED-CONTENT", cancellationToken).ConfigureAwait(false);
        File.Copy(versionDll, allowedModule, true);
        File.Copy(versionDll, deniedModule, true);
        File.Copy(probeSource, probeCopy, true);

        // The probe variables enter the spawn env through the same cleaned
        // environment path as every other sandboxed command: the factory merges
        // them into the trusted extraEnvironment before composition.
        var probeEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CODEX_WINDOWS_ALLOWED_TEXT"]   = allowedText,
            ["CODEX_WINDOWS_DENIED_TEXT"]    = deniedText,
            ["CODEX_WINDOWS_ALLOWED_MODULE"] = allowedModule,
            ["CODEX_WINDOWS_DENIED_MODULE"]  = deniedModule
        };
        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, probeEnvironment,
                                         new PreparedProcessExecution(probeCopy, [], _workspaceRoot,
                                                                      DenyProbeTimeoutSeconds, false, null),
                                         "deny-probe", cancellationToken).ConfigureAwait(false);

        var detail = $"读根对照: {string.Join("; ", readRoots)}；denied fixture: {deniedRoot}";
        if (run.Exception is not null)
            return Result(definition, SandboxAcceptanceVerdict.Fail, Describe(run, null),
                          $"无退出码（协议失败/异常），deny-probe 未完成。{detail}", stopwatch);

        return run.Result!.ExitCode switch
        {
            0 => Result(definition, SandboxAcceptanceVerdict.Pass, Describe(run, null), detail, stopwatch),
            20 => Result(definition, SandboxAcceptanceVerdict.Fail, Describe(run, null),
                         $"退出 20：deny 路径意外可访问，隔离失效。{detail}", stopwatch),
            21 => Result(definition, SandboxAcceptanceVerdict.Fail, Describe(run, null),
                         $"退出 21：探针自身配置错误（harness fixture 问题，不代表隔离失效）。{detail}", stopwatch),
            _ => Result(definition, SandboxAcceptanceVerdict.Fail,
                        Describe(run, $"stdout: {FirstLine(run.Stdout?.Content)}"),
                        $"退出 {run.Result.ExitCode}，不符合 0/20/21 契约。{detail}", stopwatch)
        };
    }

    private async Task<SandboxAcceptanceCaseResult> WriteDeniedOutsideRootsAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("write-denied-outside-roots");
        var stopwatch  = Stopwatch.StartNew();
        var target = Path.Combine(DeniedFixtureRoot(), "outside-roots-probe.txt");
        var execution = _backendFactory(SandboxPolicyKind.WorkspaceWrite, null);
        if (execution.Policy.EffectiveWriteRoots.Any(root => Workspace.IsInside(root, target)))
            return Result(definition, SandboxAcceptanceVerdict.Fail, null,
                          $"目标（{target}）落在有效写根内，fixture 与策略冲突；写根: {string.Join("; ", execution.Policy.EffectiveWriteRoots)}",
                          stopwatch);

        return await WriteProbeAsync(definition, SandboxPolicyKind.WorkspaceWrite, target, stopwatch,
                                     cancellationToken).ConfigureAwait(false);
    }

    private async Task<SandboxAcceptanceCaseResult> ReadonlyWorkspaceWriteDeniedAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("readonly-workspace-write-denied");
        var stopwatch  = Stopwatch.StartNew();
        return await WriteProbeAsync(definition, SandboxPolicyKind.ReadOnly,
                                     Path.Combine(_workspaceRoot, "readonly-write-probe.txt"), stopwatch,
                                     cancellationToken).ConfigureAwait(false);
    }

    private async Task<SandboxAcceptanceCaseResult> MetadataProtectedAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("metadata-protected");
        var stopwatch  = Stopwatch.StartNew();
        var gitDirectory = Path.Combine(_workspaceRoot, ".git");
        Directory.CreateDirectory(gitDirectory);
        var target = Path.Combine(gitDirectory, "metadata-probe.txt");
        var execution = _backendFactory(SandboxPolicyKind.WorkspaceWrite, null);
        var denyEntry = execution.Policy.DenyWritePaths.FirstOrDefault(path => Workspace.IsInside(path, target));
        var result = await WriteProbeAsync(definition, SandboxPolicyKind.WorkspaceWrite, target, stopwatch,
                                           cancellationToken).ConfigureAwait(false);
        return result with
        {
            Detail = $"deny-write 条目: {denyEntry ?? "<缺失：策略未保护该路径>"}" +
                     (result.Detail is null ? string.Empty : $"；{result.Detail}")
        };
    }

    private async Task<SandboxAcceptanceCaseResult> WorkspaceWriteAllowedAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("workspace-write-allowed");
        var stopwatch  = Stopwatch.StartNew();
        var target = Path.Combine(_workspaceRoot, "write-allowed-probe.txt");
        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                         CmdPlan($"copy /y NUL {CmdSafePath(target)}", WriteProbeTimeoutSeconds),
                                         "workspace-write-allowed", cancellationToken).ConfigureAwait(false);
        var exists = File.Exists(target);
        var actual = Describe(run, $"目标文件已创建: {exists}");
        var passed = run.Exception is null && run.Result is { ExitCode: 0, TimedOut: false } && exists;
        return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual,
                      "正对照：失败说明 harness 或沙箱配置损坏。", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> NetworkDeniedAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("network-denied");
        var stopwatch  = Stopwatch.StartNew();
        // The resolvers are authoritative when provided: a null result means
        // "no curl / no observable address" and records INDETERMINATE.
        var curl = _limits.CurlPathResolver is { } curlResolver ? curlResolver() : SystemTool("curl.exe");
        var nonLoopback = _limits.NonLoopbackAddressResolver is { } addressResolver
            ? addressResolver()
            : ResolveNonLoopbackAddress();
        if (curl is null)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "缺少 curl.exe（System32），无法执行断网对照。", stopwatch);

        if (nonLoopback is null)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "无法解析本机非回环地址，无法执行门控断网对照。", stopwatch);

        using var listener = new TcpListenerProbe();
        var port = listener.Start();
        var loopbackRun = await CurlAsync(curl, $"http://127.0.0.1:{port}/", "network-denied-loopback",
                                          cancellationToken).ConfigureAwait(false);
        var nonLoopbackRun = await CurlAsync(curl, $"http://{nonLoopback}:{port}/", "network-denied-nonloopback",
                                             cancellationToken).ConfigureAwait(false);

        var loopbackExit     = ExitOf(loopbackRun);
        var nonLoopbackExit  = ExitOf(nonLoopbackRun);
        var curlError = FirstLine(nonLoopbackRun.Stderr?.Content);
        var actual =
            $"回环 127.0.0.1 exit={loopbackExit?.ToString() ?? "无"}（观测）；非回环 {nonLoopback} exit={nonLoopbackExit?.ToString() ?? "无"}（门控）" +
            (string.IsNullOrEmpty(curlError) ? string.Empty : $"；非回环 stderr: {curlError}");
        var verdict = nonLoopbackRun.Exception is not null || nonLoopbackExit is null
            ? SandboxAcceptanceVerdict.Fail
            : nonLoopbackExit is 7 or 28
                ? SandboxAcceptanceVerdict.Pass
                : nonLoopbackExit == 0
                    ? SandboxAcceptanceVerdict.Fail
                    : SandboxAcceptanceVerdict.Indeterminate;
        return Result(definition, verdict, actual,
                      "exit 7=连接失败、28=超时（判为被阻断）；0=连接成功并收到数据（隔离失效）；其余视为不确定。",
                      stopwatch);
    }

    private Func<CancellationToken, Task<SandboxAcceptanceCaseResult>> ExitNaturalAsync(int expectedExitCode)
    {
        return async cancellationToken =>
        {
            var definition = Definition($"exit-natural-{expectedExitCode}");
            var stopwatch  = Stopwatch.StartNew();
            var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                             CmdPlan($"exit {expectedExitCode}", ExitProbeTimeoutSeconds),
                                             $"exit-natural-{expectedExitCode}", cancellationToken)
                .ConfigureAwait(false);
            var actual = Describe(run, null);
            var passed = run.Exception is null &&
                         run.Result is { ExitCode: var code, TimedOut: false } && code == expectedExitCode;
            return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual,
                          expectedExitCode == 192 ? "192 且 timed_out=false 是自然退出；绝不按退出码判超时。" : null,
                          stopwatch);
        };
    }

    private async Task<SandboxAcceptanceCaseResult> TimeoutFiniteAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("timeout-finite");
        var stopwatch  = Stopwatch.StartNew();
        if (PowerShellPath() is not { } powershell)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "缺少 powershell.exe，无法布置长 sleeper。", stopwatch);

        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                         new PreparedProcessExecution(powershell,
                                                                      ["-NoProfile", "-NonInteractive", "-Command",
                                                                       $"Start-Sleep -Seconds {SleeperDurationSeconds}"],
                                                                      _workspaceRoot, TimeoutCaseCommandSeconds, false,
                                                                      null),
                                         "timeout-finite", cancellationToken).ConfigureAwait(false);
        var actual = Describe(run, null);
        var passed = run.Exception is null && run.Result is { TimedOut: true };
        return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual,
                      run.Result is { ExitCode: null }
                          ? "ExitCode=null（后端兜底回收路径），TimedOut 仍为 true。"
                          : "runner 超时帧契约为 exit_code=192、timed_out=true。", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> TerminateActiveAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("terminate-active");
        var stopwatch  = Stopwatch.StartNew();
        if (PowerShellPath() is not { } sleeper)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "缺少 powershell.exe，无法布置长 sleeper。", stopwatch);

        using var caseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null, SleeperPlan(sleeper),
                                       "terminate-active", caseCancellation.Token);
        await Task.Delay(_limits.TerminateCancelDelay, cancellationToken).ConfigureAwait(false);
        await caseCancellation.CancelAsync().ConfigureAwait(false);
        var run = await AwaitAsOutcomeAsync(runTask, true).ConfigureAwait(false);

        var survivors = await WaitForNoSurvivorsAsync(cancellationToken).ConfigureAwait(false);
        var actual = Describe(run, null);
        if (run.Exception is not OperationCanceledException)
            return Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                          "取消未以 OperationCanceledException 呈现（后端取消语义不符）。", stopwatch);

        return survivors.Count == 0
            ? Result(definition, SandboxAcceptanceVerdict.Pass, actual, null, stopwatch)
            : Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                     $"回收后仍有镜像残留: {SandboxSurvivorScan.Describe(survivors)}。", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> CancelStartupAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("cancel-startup");
        var stopwatch  = Stopwatch.StartNew();
        if (PowerShellPath() is not { } sleeper)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "缺少 powershell.exe，无法布置长 sleeper。", stopwatch);

        using var caseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runTask = ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null, SleeperPlan(sleeper), "cancel-startup",
                                       caseCancellation.Token);
        // Cancel immediately after the call is in flight: the backend observes
        // cancellation during its startup stages.
        await caseCancellation.CancelAsync().ConfigureAwait(false);
        var run = await AwaitAsOutcomeAsync(runTask, true).ConfigureAwait(false);

        var survivors = await WaitForNoSurvivorsAsync(cancellationToken).ConfigureAwait(false);
        var actual = Describe(run, null);
        if (run.Exception is not OperationCanceledException)
            return Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                          "启动阶段取消未以 OperationCanceledException 呈现（后端取消语义不符）。", stopwatch);

        return survivors.Count == 0
            ? Result(definition, SandboxAcceptanceVerdict.Pass, actual, null, stopwatch)
            : Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                     $"取消后仍有镜像残留: {SandboxSurvivorScan.Describe(survivors)}。", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> RunnerBrokenPipeAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("runner-broken-pipe");
        var stopwatch  = Stopwatch.StartNew();
        if (PowerShellPath() is not { } sleeper)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "缺少 powershell.exe，无法布置长 sleeper。", stopwatch);

        using var caseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var caseStart = DateTimeOffset.Now;
        var runTask = ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null, SleeperPlan(sleeper),
                                       "runner-broken-pipe", caseCancellation.Token);

        // Locate THIS case's runner by image name plus the case's start window;
        // the baseline excludes runners of earlier cases.
        var locateDeadline = DateTimeOffset.UtcNow + _limits.ProcessScanTimeout;
        Process? runner = null;
        while (runner is null && DateTimeOffset.UtcNow < locateDeadline)
        {
            foreach (var survivor in SandboxSurvivorScan.FindSurvivors(_baselineIds, [_limits.RunnerImageName],
                                                                       caseStart))
            {
                var candidate = TryGetProcessById(survivor.Id);
                if (candidate is null) continue;

                runner = candidate;
                break;
            }

            if (runner is null)
                await Task.Delay(_limits.ProcessScanInterval, cancellationToken).ConfigureAwait(false);
        }

        if (runner is null)
        {
            await caseCancellation.CancelAsync().ConfigureAwait(false);
            var cancelledRun = await AwaitAsOutcomeAsync(runTask, true).ConfigureAwait(false);
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, Describe(cancelledRun, null),
                          $"在 {(_limits.ProcessScanTimeout).TotalSeconds:0}s 内未找到本用例的 {_limits.RunnerImageName} 进程，无法执行击杀。",
                          stopwatch);
        }

        using (runner)
        {
            runner.Kill();
        }

        // Either a result without an exit code or an exception means no exit
        // code was invented; both satisfy the contract.
        var run = await AwaitAsOutcomeAsync(runTask, false).ConfigureAwait(false);
        var survivors = await WaitForNoSurvivorsAsync(cancellationToken).ConfigureAwait(false);
        var actual = Describe(run, null);
        var exitCodeInvented = run.Result?.ExitCode is not null;
        if (exitCodeInvented)
            return Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                          $"后端补造了退出码 {run.Result!.ExitCode}，违反“无 exit 帧不补退出码”契约。", stopwatch);

        return survivors.Count == 0
            ? Result(definition, SandboxAcceptanceVerdict.Pass, actual, null, stopwatch)
            : Result(definition, SandboxAcceptanceVerdict.Fail, actual,
                     $"击杀 runner 后仍有镜像残留: {SandboxSurvivorScan.Describe(survivors)}。", stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> LargeOutputAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("large-output");
        var stopwatch  = Stopwatch.StartNew();
        var content = BuildLargeOutput(_limits.LargeOutputTargetCharacters);
        var file    = Path.Combine(_workspaceRoot, "large-output.txt");
        await File.WriteAllTextAsync(file, content, cancellationToken).ConfigureAwait(false);

        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                         CmdPlan($"type {CmdSafePath(file)}", LargeOutputTimeoutSeconds), "large-output",
                                         cancellationToken).ConfigureAwait(false);
        var stdout = run.Stdout;
        var artifactExists = stdout?.ArtifactPath is { } artifactPath && File.Exists(artifactPath);
        var actual = run.Exception is null && stdout is not null
            ? $"原始 {stdout.OriginalCharacterCount} 字符；截断={stdout.Truncated}；artifact 存在={artifactExists}"
            : Describe(run, null);
        var passed = run.Exception is null && stdout is { Truncated: true } &&
                     stdout.OriginalCharacterCount == content.Length && artifactExists;
        return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual,
                      $"模型视图上限 {_limits.ModelOutputCharacterLimit} 字符（head+tail 截断）；期望原始长度 {content.Length}；完整 artifact: {stdout?.ArtifactPath ?? "<无>"}",
                      stopwatch);
    }

    private async Task<SandboxAcceptanceCaseResult> HostExitReclaimAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("host-exit-reclaim");
        var stopwatch  = Stopwatch.StartNew();
        if (_workerSpawner is null)
            return Result(definition, SandboxAcceptanceVerdict.Skipped, null,
                          "未提供 verify-worker 启动器，无法执行宿主异常退出用例。", stopwatch);

        var markerPath = Path.Combine(_artifactDirectory, "host-exit-marker.txt");
        // The parent clears a stale marker before spawning: the worker never
        // deletes caller-supplied paths, and a stale file would fake readiness
        // before the worker even started.
        File.Delete(markerPath);
        var worker = _workerSpawner(markerPath, _limits.WorkerHoldSeconds);
        if (!await WaitForFileAsync(markerPath, _limits.MarkerWaitTimeout, _limits.ProcessScanInterval,
                                    cancellationToken).ConfigureAwait(false))
        {
            var killNote = TryKill(worker);
            return Result(definition, SandboxAcceptanceVerdict.Fail, null,
                          $"worker 未在 {(_limits.MarkerWaitTimeout).TotalSeconds:0}s 内写出 marker（沙箱可能未就绪）{killNote}。",
                          stopwatch);
        }

        var note = TryKill(worker);
        var survivors = await WaitForNoSurvivorsAsync(cancellationToken).ConfigureAwait(false);
        return survivors.Count == 0
            ? Result(definition, SandboxAcceptanceVerdict.Pass, $"marker: {markerPath}", note, stopwatch)
            : Result(definition, SandboxAcceptanceVerdict.Fail, $"marker: {markerPath}",
                     $"{note}Kill 后仍有镜像幸存: {SandboxSurvivorScan.Describe(survivors)}。", stopwatch);
    }

    // ---- 共享执行管线 ----

    private sealed record SandboxRun(
        ProcessExecutionResult? Result, Exception? Exception, CapturedProcessOutput? Stdout,
        CapturedProcessOutput? Stderr);

    /// <summary>
    ///     组合一次执行并跑完：命令超时由后端按计划实施；任何异常先丢弃输出再上抛（调用方决定按
    ///     用例语义捕获）。捕获器默认是 Core 的增量 artifact 实现（head+tail 截断）。
    ///     Composes and runs one execution: the backend enforces the plan's
    ///     command timeout; any exception discards the captures first and
    ///     rethrows (callers decide what their case semantics expect). The
    ///     default capture is Core's incremental artifact implementation
    ///     (head+tail truncation).
    /// </summary>
    private async Task<SandboxRun> ExecuteOnceAsync(SandboxPolicyKind                    kind,
                                                   IReadOnlyDictionary<string, string>? environment,
                                                   PreparedProcessExecution             plan,
                                                   string                               artifactPrefix,
                                                   CancellationToken                    cancellationToken)
    {
        var execution = _backendFactory(kind, environment);
        var stdout = CreateCapture(Path.Combine(_artifactDirectory, $"{artifactPrefix}.stdout.txt"));
        var stderr = CreateCapture(Path.Combine(_artifactDirectory, $"{artifactPrefix}.stderr.txt"));
        try
        {
            var result = await execution.Backend.ExecuteAsync(plan, stdout, stderr, cancellationToken)
                                        .ConfigureAwait(false);
            return new SandboxRun(result, null, stdout.Complete(), stderr.Complete());
        }
        catch (Exception)
        {
            stdout.Discard();
            stderr.Discard();
            throw;
        }
    }

    /// <summary>
    ///     以“结果或异常皆为准出”的方式等待一次执行：仅当用例自身已发起取消时把取消折叠为
    ///     SandboxRun.Exception；总期限/调用方取消仍向上传播，由外层记 FAIL/SKIPPED 或重抛。
    ///     Awaits one execution treating a result or an exception as an
    ///     outcome: cancellation folds into SandboxRun.Exception only when the
    ///     case itself initiated it; overall-deadline and caller cancellation
    ///     still propagate for the outer loop to record or rethrow.
    /// </summary>
    private static async Task<SandboxRun> AwaitAsOutcomeAsync(Task<SandboxRun> runTask, bool caseCancelled)
    {
        try
        {
            return await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caseCancelled)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SandboxRun(null, ex, null, null);
        }
    }

    private async Task<SandboxAcceptanceCaseResult> WriteProbeAsync(AcceptanceCaseDefinition definition,
                                                                    SandboxPolicyKind        kind,
                                                                    string                   target,
                                                                    Stopwatch                stopwatch,
                                                                    CancellationToken        cancellationToken)
    {
        var run = await ExecuteOnceAsync(kind, null, CmdPlan($"copy /y NUL {CmdSafePath(target)}", WriteProbeTimeoutSeconds),
                                         definition.Name, cancellationToken).ConfigureAwait(false);
        var exists = File.Exists(target);
        var actual = Describe(run, $"目标文件已创建: {exists}");
        var denied = run.Exception is null &&
                     run.Result is { ExitCode: not null and not 0, TimedOut: false } && !exists;
        return Result(definition, denied ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail, actual, null,
                      stopwatch);
    }

    private async Task<SandboxRun> CurlAsync(string curl, string url, string artifactPrefix,
                                             CancellationToken cancellationToken)
    {
        return await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                      new PreparedProcessExecution(curl,
                                                                   ["-sS", "-o", "NUL", "--connect-timeout", "3",
                                                                    "--max-time", "5", url],
                                                                   _workspaceRoot, NetworkProbeTimeoutSeconds, false,
                                                                   null),
                                      artifactPrefix, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     长 sleeper 计划：powershell Start-Sleep。与 waitfor 不同，它不依赖网络（waitfor 走
    ///     SMB mailslot，会被断网过滤秒退），timeout-finite 用例已验证它在沙箱内可长期运行。
    ///     The long-sleeper plan: powershell Start-Sleep. Unlike waitfor it
    ///     needs no network (waitfor uses SMB mailslots and dies instantly
    ///     under the network block); the timeout-finite case proved it runs
    ///     long inside the sandbox.
    /// </summary>
    private PreparedProcessExecution SleeperPlan(string powershell)
    {
        return new PreparedProcessExecution(powershell,
                                            ["-NoProfile", "-NonInteractive", "-Command",
                                             $"Start-Sleep -Seconds {SleeperDurationSeconds}"],
                                            _workspaceRoot, SleeperDurationSeconds + 30, false, null);
    }

    private IProcessOutputCapture CreateCapture(string artifactPath)
    {
        return _captureFactory?.Invoke(artifactPath)
               ?? new ProcessOutputCapture(artifactPath, _limits.ModelOutputCharacterLimit, []);
    }

    /// <summary>
    ///     cmd /c 载荷内的裸路径约束。runner 会按 CRT 规则重组 argv：含空格/引号的路径经双层
    ///     转义变成 cmd 无法解析的 \" 形式（宿主进程与沙箱在该语义上完全一致，实测均 exit 1），
    ///     因此验收 fixture 的路径必须由 cmd 安全字符组成；否则抛出，把 fixture 问题明确暴露，
    ///     而不是被误读为隔离结果。
    ///     Constraint for bare paths inside a cmd /c payload. The runner re-joins
    ///     argv with CRT rules: a path containing whitespace or quotes becomes a
    ///     \" form cmd cannot parse (host processes and the sandbox share this
    ///     exact semantics — measured exit 1 on both), so fixture paths must
    ///     consist of cmd-safe characters; anything else throws, surfacing a
    ///     fixture problem explicitly instead of a misread isolation verdict.
    /// </summary>
    private static string CmdSafePath(string path)
    {
        if (path.All(character => char.IsLetterOrDigit(character) ||
                                  character is ':' or '\\' or '.' or '-' or '_' or '/'))
            return path;

        throw new InvalidOperationException(
            $"验收 fixture 路径含 cmd /c 载荷无法承载的字符（空格/引号等），换用无空格的验收工作区：'{path}'");
    }

    private PreparedProcessExecution CmdPlan(string commandText, int timeoutSeconds)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(comSpec) || !File.Exists(comSpec))
            throw new InvalidOperationException("ComSpec 未指向可用的 cmd.exe，无法执行 cmd 探针。");

        return new PreparedProcessExecution(comSpec, ["/d", "/s", "/c", commandText], _workspaceRoot, timeoutSeconds,
                                            false, commandText);
    }

    private static string? SystemTool(string fileName)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot)) return null;

        var path = Path.Combine(systemRoot, "System32", fileName);
        return File.Exists(path) ? path : null;
    }

    private static string? PowerShellPath()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot)) return null;

        var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveNonLoopbackAddress()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                      .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork &&
                                                 !IPAddress.IsLoopback(address))
                     ?.ToString();
        }
        catch (SocketException)
        {
            // Hostname resolution itself failed: no non-loopback address is
            // observable, which the network case records as INDETERMINATE.
            return null;
        }
    }

    private static string BuildLargeOutput(int targetCharacters)
    {
        var builder = new StringBuilder(targetCharacters + 128);
        var line    = 0;
        while (builder.Length < targetCharacters)
        {
            builder.Append('L')
                   .Append(line.ToString("D8"))
                   .Append(':')
                   .Append('x', 52)
                   .Append("\r\n");
            line++;
        }

        return builder.ToString();
    }

    private string DeniedFixtureRoot()
    {
        var trimmed = _workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name   = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent))
            throw new InvalidOperationException($"无法从验收工作区推导 denied fixture 目录：'{_workspaceRoot}'。");

        return Path.Combine(parent, name + "-denied-" + Guid.NewGuid().ToString("N")[..8]);
    }

    private async Task<List<SandboxSurvivorScan.Survivor>> WaitForNoSurvivorsAsync(CancellationToken cancellationToken)
    {
        return await SandboxSurvivorScan.WaitForNoSurvivorsAsync(_baselineIds, _limits.SurvivorImageNames,
                                                                 _runStartedLocal, _limits.ProcessScanTimeout,
                                                                 _limits.ProcessScanInterval, cancellationToken)
                                        .ConfigureAwait(false);
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout, TimeSpan interval,
                                                     CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(path);
    }

    private static string TryKill(ISandboxVerifyWorkerProcess worker)
    {
        try
        {
            worker.Kill();
            return string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return $"（Kill 时 worker 已退出：{ex.Message}）";
        }
    }

    private static Process? TryGetProcessById(int id)
    {
        try
        {
            return Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            // The survivor exited between the scan and this lookup.
            return null;
        }
    }

    private AcceptanceCaseDefinition Definition(string name)
    {
        return _caseDefinitions.FirstOrDefault(definition => definition.Name == name)
               ?? throw new InvalidOperationException($"未定义的验收用例 '{name}'。");
    }

    private SandboxAcceptanceCaseResult Result(AcceptanceCaseDefinition    definition,
                                               SandboxAcceptanceVerdict    verdict,
                                               string?                     actual,
                                               string?                     detail,
                                               Stopwatch?                  stopwatch = null)
    {
        return new SandboxAcceptanceCaseResult
        {
            Name          = definition.Name,
            CommandSummary = definition.CommandSummary,
            Expectation   = definition.Expectation,
            ActualSummary = actual,
            Verdict       = verdict,
            Duration      = (stopwatch?.Elapsed ?? TimeSpan.Zero).ToString("hh\\:mm\\:ss\\.fff"),
            Detail        = detail
        };
    }

    private static string Describe(SandboxRun run, string? extra)
    {
        var parts = new List<string>();
        if (run.Exception is not null)
            parts.Add($"exception={run.Exception.GetType().Name}: {run.Exception.Message}");

        if (run.Result is { } result)
        {
            parts.Add($"exit={result.ExitCode?.ToString() ?? "无"}");
            parts.Add($"timedOut={result.TimedOut}");
            if (result.Failure is { } failure) parts.Add($"failure={failure.Message}");
        }

        if (extra is not null) parts.Add(extra);
        return string.Join("; ", parts);
    }

    private static int? ExitOf(SandboxRun run)
    {
        return run.Exception is null ? run.Result?.ExitCode : null;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var index = text.IndexOf('\n');
        return index < 0 ? text.TrimEnd('\r') : text[..index].TrimEnd('\r');
    }

    private static IReadOnlyList<string> ValidateCaseFilter(IReadOnlyList<string>? caseFilter)
    {
        if (caseFilter is not { Count: > 0 }) return CaseNames;

        var unknown = caseFilter.Where(name => !CaseNames.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException(
                $"未知的验收用例名: {string.Join(", ", unknown)}。有效名称: {string.Join(", ", CaseNames)}。");

        return caseFilter.Distinct(StringComparer.Ordinal).ToArray();
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

    private sealed record ComponentSnapshotResult(SandboxAcceptanceComponentSnapshot Snapshot, string? MarkerError);

    private static async Task<ComponentSnapshotResult> BuildComponentSnapshotAsync(WindowsSandboxExecution execution,
                                                                                   CancellationToken cancellationToken)
    {
        uint?      markerVersion = null;
        string?    markerError   = null;
        try
        {
            var markerJson = await File.ReadAllTextAsync(execution.Components.MarkerPath, cancellationToken)
                                      .ConfigureAwait(false);
            markerVersion = JsonSerializer.Deserialize(markerJson,
                                                        WindowsSandboxJsonContext.Default.SandboxSetupMarker)?.Version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            markerError = ex.Message;
        }

        var runnerDirectory = Path.GetDirectoryName(execution.Components.RunnerExecutablePath);
        var probePath = runnerDirectory is null
            ? null
            : Path.Combine(runnerDirectory, "codex-windows-managed-deny-probe.exe");
        var snapshot = new SandboxAcceptanceComponentSnapshot
        {
            SetupExecutable   = DescribeFile(execution.Components.SetupExecutablePath),
            RunnerExecutable  = DescribeFile(execution.Components.RunnerExecutablePath),
            DenyProbeExecutable = File.Exists(probePath) ? DescribeFile(probePath!) : null,
            SandboxHome       = execution.Components.SandboxHome,
            MarkerVersion     = markerVersion,
            SetupVersion      = WindowsSandboxComponents.SetupVersion,
            IpcVersion        = WindowsSandboxComponents.IpcVersion
        };
        return new ComponentSnapshotResult(snapshot, markerError);
    }

    private static SandboxAcceptanceFileInfo DescribeFile(string path)
    {
        var info = new FileInfo(path);
        return new SandboxAcceptanceFileInfo
        {
            Path            = path,
            SizeBytes       = info.Exists ? info.Length : null,
            LastWriteTimeUtc = info.Exists ? info.LastWriteTimeUtc.ToString("O") : null
        };
    }

    private async Task<string?> TryWriteReportAsync(WindowsSandboxAcceptanceReport report,
                                                    DateTimeOffset                 startedAtUtc,
                                                    CancellationToken              cancellationToken)
    {
        try
        {
            var directory = _snapshotExecution!.Components.SandboxDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
                                     $"tinyharness-verify-{startedAtUtc.LocalDateTime:yyyyMMdd-HHmmss}.json");
            var json = JsonSerializer.Serialize(report,
                                                WindowsSandboxAcceptanceJsonContext.Default.WindowsSandboxAcceptanceReport);
            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The report path stays null; the CLI surfaces the missing report.
            return null;
        }
    }

    /// <summary>
    ///     host 侧一次性 HTTP 对照监听：绑定全部接口的随机端口，对每个连接回一个最小合法 HTTP/1.0
    ///     应答后关闭，使"连接成功并收到数据"（curl exit 0）与"被阻断"（exit 7/28）映射到无歧义的
    ///     退出码。回环与非回环连接同一个监听，因此连接失败只能来自网络隔离，而不是没有监听。
    ///     不要回裸字节：现代 curl 会按 HTTP/0.9 拒绝并 exit 1，污染退出码映射。
    ///     A host-side one-shot HTTP probe listener: it binds a random port on
    ///     all interfaces, answers every connection with a minimal valid
    ///     HTTP/1.0 response, then closes, so "connected and received data"
    ///     (curl exit 0) and "blocked" (exit 7/28) map to unambiguous exit
    ///     codes. Loopback and non-loopback connections target the same
    ///     listener, so a connection failure can only come from the network
    ///     isolation, not from a missing listener. Never answer with raw
    ///     bytes: modern curl rejects them as HTTP/0.9 with exit 1, which
    ///     pollutes the exit-code mapping.
    /// </summary>
    private sealed class TcpListenerProbe : IDisposable
    {
        private TcpListener?          _listener;
        private CancellationTokenSource? _acceptSource;
        private Task?                _acceptLoop;

        public int Start()
        {
            _listener     = new TcpListener(IPAddress.Any, 0);
            _acceptSource = new CancellationTokenSource();
            _listener.Start();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_listener, _acceptSource.Token));
            return ((IPEndPoint)_listener.LocalEndpoint).Port;
        }

        public void Stop()
        {
            _acceptSource?.Cancel();
            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // Best-effort teardown of this harness-owned listener.
            }

            _acceptSource?.Dispose();
            _acceptSource = null;
        }

        public void Dispose()
        {
            Stop();
        }

        private static async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    // The listener was stopped; the probe is over.
                    return;
                }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(
                                       "HTTP/1.0 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"u8
                                          .ToArray(),
                                       CancellationToken.None)
                                    .ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                    {
                        // The client vanished mid-handshake; keep accepting.
                    }
                }
            }
        }
    }
}

/// <summary>
///     进程存活扫描的小工具：按镜像名 + 启动时间过滤并限期轮询，供 terminate/cancel/broken-pipe/
/// host-exit 用例复用。识别局限（同名进程、PID 复用、改名）记录在报告 limitations 中。
///     A small process-survival scan utility: filters by image name plus start
///     time with a bounded polling deadline, shared by the
///     terminate/cancel/broken-pipe/host-exit cases. Its identification limits
///     (same-image processes, PID reuse, renaming) are recorded in the report
///     limitations.
/// </summary>
internal static class SandboxSurvivorScan
{
    internal readonly record struct Survivor(string Image, int Id, DateTime StartTime);

    /// <summary>
    ///     记录验收开始前已存在的目标镜像 PID；扫描把它们从“残留”中排除。
    ///     Records target-image PIDs that exist before the run; scans exclude
    ///     them from the survivor set.
    /// </summary>
    internal static HashSet<int> CaptureBaseline(IReadOnlyList<string> imageNames)
    {
        var ids = new HashSet<int>();
        foreach (var image in imageNames)
            foreach (var process in SafeGetProcessesByName(image))
            {
                ids.Add(process.Id);
                process.Dispose();
            }

        return ids;
    }

    internal static List<Survivor> FindSurvivors(HashSet<int>        baseline,
                                                 IReadOnlyList<string> imageNames,
                                                 DateTimeOffset      startedAfter)
    {
        var survivors = new List<Survivor>();
        foreach (var image in imageNames)
            foreach (var process in SafeGetProcessesByName(image))
                try
                {
                    if (!baseline.Contains(process.Id) && process.StartTime >= startedAfter.LocalDateTime)
                        survivors.Add(new Survivor(image, process.Id, process.StartTime));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // The process exited between enumeration and the StartTime query.
                }
                finally
                {
                    process.Dispose();
                }

        return survivors;
    }

    internal static async Task<List<Survivor>> WaitForNoSurvivorsAsync(HashSet<int>          baseline,
                                                                       IReadOnlyList<string> imageNames,
                                                                       DateTimeOffset        startedAfter,
                                                                       TimeSpan              timeout,
                                                                       TimeSpan              interval,
                                                                       CancellationToken     cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var survivors = FindSurvivors(baseline, imageNames, startedAfter);
            if (survivors.Count == 0 || DateTimeOffset.UtcNow >= deadline) return survivors;

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Describe(IReadOnlyList<Survivor> survivors)
    {
        return string.Join(", ", survivors.Select(survivor => $"{survivor.Image}#{survivor.Id}"));
    }

    private static Process[] SafeGetProcessesByName(string imageName)
    {
        try
        {
            return Process.GetProcessesByName(imageName);
        }
        catch (InvalidOperationException)
        {
            // Process enumeration is unavailable on this platform state; report none.
            return [];
        }
    }
}
