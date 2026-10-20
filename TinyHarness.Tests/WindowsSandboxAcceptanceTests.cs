using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         隔离验收：Core 验收运行器（判定逻辑、用例过滤、总期限、报告序列化）与 CLI 入口
///         （parser、确认门、就绪拒绝、退出码）的离线测试。后端全部为脚本化 fake——不启动真实
///         setup/runner、不做 refresh、不修改机器账户/ACL/防火墙；host-exit 用例用 fake worker，
///         broken-pipe 用例用复制镜像的本地短命进程模拟 runner。
///     </para>
///     <para>
///         Isolation-acceptance tests: offline tests of the Core acceptance runner
///         (verdict logic, case filtering, overall deadline, report serialization)
///         and the CLI entry (parser, confirmation gate, readiness refusal, exit
///         codes). Every backend is a scripted fake — no real setup/runner launch,
///         no refresh, no machine account/ACL/firewall changes; the host-exit case
///         uses a fake worker and the broken-pipe case a short-lived local process
///         with a copied image name simulating the runner.
///     </para>
/// </summary>
public sealed class WindowsSandboxAcceptanceTests
{
    // ---- Parser ----

    [Test]
    public void SandboxVerify_ParsesWithoutOptions()
    {
        var options = CommandLine.Parse(["sandbox", "verify"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Sandbox));
        Assert.That(options.Subcommand, Is.EqualTo("verify"));
        Assert.That(options.AssumeYes, Is.False);
        Assert.That(options.CaseFilter, Is.Null);
        Assert.That(options.VerifyWorkspace, Is.Null);
    }

    [Test]
    public void SandboxVerify_ParsesYesCaseAndWorkspace()
    {
        var options = CommandLine.Parse([
            "sandbox", "verify", "--yes", "--case", "deny-probe,exit-natural-23",
            "--workspace", "C:\\verify-ws"
        ]);

        Assert.That(options.Subcommand, Is.EqualTo("verify"));
        Assert.That(options.AssumeYes, Is.True);
        Assert.That(options.CaseFilter, Is.EqualTo(["deny-probe", "exit-natural-23"]));
        Assert.That(options.VerifyWorkspace, Is.EqualTo("C:\\verify-ws"));
    }

    [Test]
    public void SandboxVerify_UnknownArgument_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--bogus"]));
    }

    [Test]
    public void SandboxVerify_CaseWithoutValue_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case"]));
    }

    [Test]
    public void SandboxVerify_CaseWithOnlySeparators_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case", ","]));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case", " "]));
    }

    [Test]
    public void SandboxVerify_WorkspaceWithoutValue_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--workspace"]));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--workspace", " "]));
    }

    [Test]
    public void SandboxVerifyWorker_ParsesAllRequiredOptions()
    {
        var options = CommandLine.Parse([
            "sandbox", "verify-worker", "--marker", "C:\\m.txt", "--hold-seconds", "30",
            "--workspace", "C:\\ws"
        ]);

        Assert.That(options.Subcommand, Is.EqualTo("verify-worker"));
        Assert.That(options.VerifyMarkerPath, Is.EqualTo("C:\\m.txt"));
        Assert.That(options.VerifyHoldSeconds, Is.EqualTo(30));
        Assert.That(options.VerifyWorkspace, Is.EqualTo("C:\\ws"));
    }

    [Test]
    public void SandboxVerifyWorker_MissingRequiredOptions_AreUsageErrors()
    {
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify-worker"]));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse([
            "sandbox", "verify-worker", "--marker", "C:\\m.txt"
        ]));
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse([
                                                     "sandbox", "verify-worker", "--marker", "C:\\m",
                                                     "--hold-seconds", "5"
                                                 ]));
    }

    [Test]
    public void SandboxVerifyWorker_InvalidHoldSeconds_IsAUsageError()
    {
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse([
                                                     "sandbox", "verify-worker", "--hold-seconds", "0"
                                                 ]));
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse([
                                                     "sandbox", "verify-worker", "--hold-seconds", "x"
                                                 ]));
    }

    [Test]
    public void SandboxVerifyWorker_MissingOptionValueAndUnknownArgument_AreUsageErrors()
    {
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse([
                                                     "sandbox", "verify-worker", "--marker", "C:\\m",
                                                     "--hold-seconds", "5", "--workspace"
                                                 ]));
        TestAssert.Throws<CliUsageException>(() =>
                                                 CommandLine.Parse(["sandbox", "verify-worker", "--extra", "1"]));
    }

    // ---- 运行器：全矩阵与逐例判定 ----

    [Test]
    public async Task FullMatrix_WithScriptedBackend_AllCasesPassAndReportIsWritten()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        await File.WriteAllTextAsync(Path.Combine(home.Components.SandboxHome,
                                                  "codex-windows-managed-deny-probe.exe"), "MZ");
        var sleeperCount = 0;
        var factory = new RecordingBackendFactory(home.Components, workspace.Root, (plan, ct) =>
        {
            // 长睡眠用例是 powershell Start-Sleep 加 300 秒以上的命令超时；timeout-finite 用
            // 同一可执行文件加 8 秒超时，并继续走 happy 脚本的超时判定。
            if (Path.GetFileName(plan.Executable) != "powershell.exe" || plan.TimeoutSeconds < 300)
                return HappyScriptAsync(plan, ct);

            // 固定用例顺序：terminate-active 与 cancel-startup 观察取消；runner-broken-pipe
            // 使用可击杀的傀儡 runner。
            var index = ++sleeperCount;
            return index <= 2
                ? WaitUntilCancelledAsync(ct)
                : SimulateExternalRunnerKillAsync(workspace.Root,
                                                  new ScriptedOutcome(Failure : new ProcessExecutionFailure(
                                                                           "The runner pipe closed before the exit frame; no command exit code is available.")));
        });

        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root,
                                                        (marker, _) => new FakeWorker(marker, true),
                                                        limits : TestLimits());
        var report = await runner.RunAsync(null, CancellationToken.None);

        TestAssert.All(report.Cases,
                       result => Assert.That(result.Verdict == SandboxAcceptanceVerdict.Pass, Is.True,
                                             $"{result.Name}: {result.Verdict} {result.Detail}"));
        Assert.That(report.Cases.Count, Is.EqualTo(WindowsSandboxAcceptanceRunner.CaseNames.Count));
        Assert.That(report.PolicyKinds, Does.Contain("read-only"));
        Assert.That(report.PolicyKinds, Does.Contain("workspace-write"));
        Assert.That(report.Limitations, Is.Not.Empty);
        Assert.That(report.OfflineAccountName, Is.EqualTo(WindowsSandboxComponents.DefaultOfflineUsername));

        // 报告落在 <home>\.sandbox\tinyharness-verify-*.json。
        TestAssert.NotNull(report.ReportPath);
        Assert.That(File.Exists(report.ReportPath), Is.True);
        Assert.That(report.ReportPath, Does.StartWith(home.Components.SandboxDirectory));
        Assert.That(report.ReportPath, Does.Contain("tinyharness-verify-"));

        // deny-probe 变量经清理后的环境接缝（extraEnvironment）到达工厂，绝不走手工构造的 env。
        var probeRequest = TestAssert.Single(factory.Requests, request => request.ExtraEnvironment is { Count: 4 });
        Assert.That(probeRequest.ExtraEnvironment!.Keys, Does.Contain("CODEX_WINDOWS_ALLOWED_TEXT"));
        Assert.That(probeRequest.ExtraEnvironment.Keys, Does.Contain("CODEX_WINDOWS_DENIED_MODULE"));
    }

    [Test]
    public async Task CaseFilter_RunsOnlyTheSelectedCase()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));
        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());
        var report = await runner.RunAsync(["exit-natural-23"], CancellationToken.None);

        var result = TestAssert.Single(report.Cases);
        Assert.That(result.Name, Is.EqualTo("exit-natural-23"));
        Assert.That(result.Verdict, Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        TestAssert.All(factory.Requests,
                       request => Assert.That(request.Kind, Is.EqualTo(SandboxPolicyKind.WorkspaceWrite)));
    }

    [Test]
    public async Task UnknownCaseName_ThrowsArgumentException()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));
        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());

        await TestAssert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(["no-such-case"],
                                                                              CancellationToken.None));
    }

    [Test]
    public async Task OverallDeadline_InterruptsCurrentCaseAndSkipsTheRest()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       factory   = new RecordingBackendFactory(home.Components, workspace.Root, StallThenHappyScript);
        var       limits    = TestLimits() with { OverallDeadline = TimeSpan.FromMilliseconds(150) };
        var       runner    = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : limits);
        var       report    = await runner.RunAsync(null, CancellationToken.None);

        Assert.That(VerdictOf(report, "identity-whoami"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.ContainsAll(result => result.Verdict == SandboxAcceptanceVerdict.Skipped, report.Cases);
        TestAssert.Contains("总期限", CaseDetail(report, "identity-whoami")!, StringComparison.Ordinal);
    }

    [Test]
    public async Task ExitSemantics_ExitCodeAloneNeverMeansTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root, (plan, ct) =>
        {
            var command = LastArgument(plan);
            if (command.StartsWith("exit 192", StringComparison.Ordinal))
                // 自然退出却上报 192 且 timed_out=true 的撒谎后端不得骗过判定。
                return Task.FromResult(new ScriptedOutcome(192, true));

            if (Path.GetFileName(plan.Executable) == "powershell.exe")
                // 超时用例期望 TimedOut=true；普通成功反而失败。
                return Task.FromResult(new ScriptedOutcome(0));

            return HappyScriptAsync(plan, ct);
        });
        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());
        var report = await runner.RunAsync(["exit-natural-23", "exit-natural-192", "timeout-finite"],
                                           CancellationToken.None);

        Assert.That(VerdictOf(report, "exit-natural-23"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        Assert.That(VerdictOf(report, "exit-natural-192"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("绝不", CaseDetail(report, "exit-natural-192")!, StringComparison.Ordinal);
        Assert.That(VerdictOf(report, "timeout-finite"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task DenyProbe_VerdictsFollowTheExitCodeContract()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        await File.WriteAllTextAsync(Path.Combine(home.Components.SandboxHome,
                                                  "codex-windows-managed-deny-probe.exe"), "MZ");

        // 0 = PASS；20 = 隔离失效 FAIL；抛异常 = 无退出码 FAIL。
        var report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
                                                  Task.FromResult(new ScriptedOutcome(0)));
        Assert.That(VerdictOf(report, "deny-probe"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
                                              Task.FromResult(new ScriptedOutcome(20)));
        Assert.That(VerdictOf(report, "deny-probe"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("隔离失效", CaseDetail(report, "deny-probe")!, StringComparison.Ordinal);

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
                                              Task.FromResult(new ScriptedOutcome(21)));
        Assert.That(VerdictOf(report, "deny-probe"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("fixture", CaseDetail(report, "deny-probe")!, StringComparison.Ordinal);

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
                                              Task.FromResult(new ScriptedOutcome(Throw :
                                                                       new InvalidOperationException("pipe broke"))));
        Assert.That(VerdictOf(report, "deny-probe"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task WriteProbes_AllowedAndDeniedJudgements()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();

        // happy 路径：允许的写入以 exit 0 建立文件；所有 deny 目标失败且不产生任何文件。
        var report = await RunSingleCaseAsync(home, "workspace-write-allowed", HappyScriptAsync);
        Assert.That(VerdictOf(report, "workspace-write-allowed"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        report = await RunSingleCaseAsync(home, "readonly-workspace-write-denied", HappyScriptAsync);
        Assert.That(VerdictOf(report, "readonly-workspace-write-denied"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        report = await RunSingleCaseAsync(home, "write-denied-outside-roots", HappyScriptAsync);
        Assert.That(VerdictOf(report, "write-denied-outside-roots"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        report = await RunSingleCaseAsync(home, "metadata-protected", HappyScriptAsync);
        Assert.That(VerdictOf(report, "metadata-protected"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        TestAssert.Contains("deny-write", CaseDetail(report, "metadata-protected")!, StringComparison.Ordinal);

        // 写入"成功"（exit 0）却没留下文件属于失效的正对照。
        report = await RunSingleCaseAsync(home, "workspace-write-allowed", (plan, ct) =>
                                              Task.FromResult(new ScriptedOutcome(0)));
        Assert.That(VerdictOf(report, "workspace-write-allowed"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));

        // 写根外写入建出了文件即隔离失效。
        report = await RunSingleCaseAsync(home, "write-denied-outside-roots", (plan, ct) =>
        {
            var target = QuotedPath(LastArgument(plan));
            File.WriteAllText(target, string.Empty);
            return Task.FromResult(new ScriptedOutcome(0));
        });
        Assert.That(VerdictOf(report, "write-denied-outside-roots"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task NetworkCase_VerdictTriad()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();

        var report = await RunSingleCaseAsync(home, "network-denied", (plan, ct) =>
                                                  Task.FromResult(Path.GetFileName(plan.Executable) == "curl.exe" &&
                                                                  !LastArgument(plan)
                                                                     .Contains("127.0.0.1",
                                                                               StringComparison
                                                                                  .Ordinal)
                                                                      ? new ScriptedOutcome(7)
                                                                      : new ScriptedOutcome(0)));
        Assert.That(VerdictOf(report, "network-denied"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        report = await RunSingleCaseAsync(home, "network-denied", (plan, ct) =>
                                              Task.FromResult(new ScriptedOutcome(0)));
        Assert.That(VerdictOf(report, "network-denied"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));

        var limits = TestLimits() with { NonLoopbackAddressResolver = () => null };
        report = await RunSingleCaseAsync(home, "network-denied", HappyScriptAsync, limits);
        Assert.That(VerdictOf(report, "network-denied"), Is.EqualTo(SandboxAcceptanceVerdict.Indeterminate));

        limits = TestLimits() with { CurlPathResolver = () => null };
        report = await RunSingleCaseAsync(home, "network-denied", HappyScriptAsync, limits);
        Assert.That(VerdictOf(report, "network-denied"), Is.EqualTo(SandboxAcceptanceVerdict.Indeterminate));
    }

    [Test]
    public async Task LargeOutput_TruncatesModelViewAndKeepsArtifact()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       limits    = TestLimits();
        var runner = new WindowsSandboxAcceptanceRunner(
                                                        new RecordingBackendFactory(home.Components, workspace.Root,
                                                                 (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
                                                        workspace.Root, (marker, _) => new FakeWorker(marker, true),
                                                        limits : limits);
        var report = await runner.RunAsync(["large-output"], CancellationToken.None);
        var result = report.Cases.Single(caseResult => caseResult.Name == "large-output");

        Assert.That(result.Verdict, Is.EqualTo(SandboxAcceptanceVerdict.Pass));
        TestAssert.Contains("截断", result.Detail, StringComparison.Ordinal);
        var artifactLine = result.Detail!.Split("完整 artifact: ")[^1];
        Assert.That(File.Exists(artifactLine), Is.True, $"artifact should exist: {artifactLine}");
        var generated = Path.Combine(workspace.Root, "large-output.txt");
        Assert.That(File.Exists(generated), Is.True);
        Assert.That(new FileInfo(artifactLine).Length, Is.EqualTo(new FileInfo(generated).Length));
        Assert.That(new FileInfo(artifactLine).Length >= limits.LargeOutputTargetCharacters, Is.True);

        // 从未超过预算的极小输出不得计为截断。
        var tiny = new WindowsSandboxAcceptanceRunner(
                                                      new RecordingBackendFactory(home.Components, workspace.Root,
                                                               (plan, ct) =>
                                                                   Task.FromResult(new ScriptedOutcome(0, StdoutChunks :
                                                                                     ["small"]))).Invoke,
                                                      workspace.Root, (marker, _) => new FakeWorker(marker, true),
                                                      limits : limits);
        report = await tiny.RunAsync(["large-output"], CancellationToken.None);
        Assert.That(VerdictOf(report, "large-output"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
    }

    [Test]
    public async Task TerminateAndCancel_CancellationOutcomeIsJudgedStrictly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        var report = await RunSingleCaseAsync(home, "terminate-active",
                                              (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.That(VerdictOf(report, "terminate-active"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        report = await RunSingleCaseAsync(home, "cancel-startup",
                                          (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.That(VerdictOf(report, "cancel-startup"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        // 吞掉取消并返回结果的后端判 FAIL：取消必须以 OperationCanceledException 呈现。
        report = await RunSingleCaseAsync(home, "terminate-active",
                                          (plan, ct) => Task.FromResult(new ScriptedOutcome(1)));
        Assert.That(VerdictOf(report, "terminate-active"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("OperationCanceledException", CaseDetail(report, "terminate-active")!,
                            StringComparison.Ordinal);
    }

    [Test]
    public async Task BrokenPipe_NoExitCodeMayBeInvented()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var report = await RunSingleCaseAsync(home, "runner-broken-pipe", (plan, ct) =>
                                                  SimulateExternalRunnerKillAsync(workspace.Root,
                                                           new ScriptedOutcome(Failure : new ProcessExecutionFailure(
                                                                                    "The runner pipe closed before the exit frame."))));
        Assert.That(VerdictOf(report, "runner-broken-pipe"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));

        // 击杀成功但后端编造退出码 5——验收必须抓住这种契约违反。
        report = await RunSingleCaseAsync(home, "runner-broken-pipe", (plan, ct) =>
                                              SimulateExternalRunnerKillAsync(workspace.Root, new ScriptedOutcome(5)));
        Assert.That(VerdictOf(report, "runner-broken-pipe"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("补造", CaseDetail(report, "runner-broken-pipe")!, StringComparison.Ordinal);
    }

    [Test]
    public async Task BrokenPipe_WithoutALocatableRunner_IsIndeterminate()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home = new SandboxTestHome();
        // 不带 runner 镜像名的普通睡眠进程：定位窗口超时。
        var report = await RunSingleCaseAsync(home, "runner-broken-pipe",
                                              (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.That(VerdictOf(report, "runner-broken-pipe"), Is.EqualTo(SandboxAcceptanceVerdict.Indeterminate));
    }

    [Test]
    public async Task HostExit_MarkerTimeoutFailsWhileReadyMarkerPasses()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();

        // worker 永不写 marker：有界等待判用例失败。
        var runner = new WindowsSandboxAcceptanceRunner(
                                                        new RecordingBackendFactory(home.Components, workspace.Root,
                                                                 (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
                                                        workspace.Root, (marker, _) => new FakeWorker(marker, false),
                                                        limits : TestLimits());
        var report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);
        Assert.That(VerdictOf(report, "host-exit-reclaim"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("marker", CaseDetail(report, "host-exit-reclaim")!, StringComparison.Ordinal);

        // 写 marker 后随 Kill 退出的 worker 不留存活进程。
        runner = new WindowsSandboxAcceptanceRunner(
                                                    new RecordingBackendFactory(home.Components, workspace.Root,
                                                                                    (plan, ct) =>
                                                                                        HappyScriptAsync(plan, ct))
                                                       .Invoke,
                                                    workspace.Root, (marker, _) => new FakeWorker(marker, true),
                                                    limits : TestLimits());
        report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);
        Assert.That(VerdictOf(report, "host-exit-reclaim"), Is.EqualTo(SandboxAcceptanceVerdict.Pass));
    }

    [Test]
    public async Task SurvivorsAfterCancellation_FailTheCase()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var       limits    = TestLimits() with { SurvivorImageNames = ["cmd"] };
        var runner = new WindowsSandboxAcceptanceRunner(
                                                        new RecordingBackendFactory(home.Components, workspace.Root,
                                                                 (plan, ct) =>
                                                                 {
                                                                     if (Path.GetFileName(plan.Executable) !=
                                                                         "powershell.exe")
                                                                         return HappyScriptAsync(plan, ct);

                                                                     // 留下同名镜像进程：幸存者扫描必须在取消后抓住它。
                                                                     var comSpec =
                                                                         Environment.GetEnvironmentVariable("ComSpec")!;
                                                                     var process =
                                                                         Process.Start(new ProcessStartInfo(comSpec,
                                                                                           "/c ping -n 20 -w 1000 127.0.0.1")
                                                                                  {
                                                                                      UseShellExecute = false,
                                                                                      CreateNoWindow  = true
                                                                                  });
                                                                     return WaitUntilCancelledAsync(ct);
                                                                 }).Invoke, workspace.Root, limits : limits);
        var report = await runner.RunAsync(["terminate-active"], CancellationToken.None);

        Assert.That(VerdictOf(report, "terminate-active"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("残留", CaseDetail(report, "terminate-active")!, StringComparison.Ordinal);
    }

    // ---- 报告序列化 ----

    [Test]
    public void AcceptanceReport_RoundTripsThroughSourceGeneration()
    {
        var report = new WindowsSandboxAcceptanceReport
        {
            TimestampUtc       = "2026-10-10T00:00:00Z",
            MachineName        = "TEST-MACHINE",
            OsVersion          = "Microsoft Windows NT 10.0.26300.0",
            OfflineAccountName = "TinyHarnessOffline",
            PolicyKinds        = ["read-only", "workspace-write"],
            WorkspaceRoot      = @"C:\verify-ws",
            Components = new SandboxAcceptanceComponentSnapshot
            {
                SetupExecutable  = new SandboxAcceptanceFileInfo { Path = @"C:\sb\setup.exe", SizeBytes  = 17 },
                RunnerExecutable = new SandboxAcceptanceFileInfo { Path = @"C:\sb\runner.exe", SizeBytes = 8 },
                SandboxHome      = @"C:\sb-home",
                MarkerVersion    = 5,
                SetupVersion     = WindowsSandboxComponents.SetupVersion,
                IpcVersion       = WindowsSandboxComponents.IpcVersion
            },
            Cases =
            [
                new SandboxAcceptanceCaseResult
                {
                    Name           = "exit-natural-23",
                    CommandSummary = "cmd /c exit 23",
                    Expectation    = "ExitCode=23 且 TimedOut=false",
                    ActualSummary  = "exit=23; timedOut=False",
                    Verdict        = SandboxAcceptanceVerdict.Pass,
                    Duration       = "00:00:01.500"
                }
            ],
            Limitations = ["局限一"]
        };

        var json = JsonSerializer.Serialize(report,
                                            WindowsSandboxAcceptanceJsonContext.Default.WindowsSandboxAcceptanceReport);
        TestAssert.Contains("\"verdict\": \"Pass\"", json, StringComparison.Ordinal);
        TestAssert.Contains("\"markerVersion\": 5", json, StringComparison.Ordinal);
        TestAssert.Contains("\"setupVersion\": 5", json, StringComparison.Ordinal);
        TestAssert.Contains("\"ipcVersion\": 6", json, StringComparison.Ordinal);
        TestAssert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        var back = JsonSerializer.Deserialize(json,
                                              WindowsSandboxAcceptanceJsonContext.Default
                                                 .WindowsSandboxAcceptanceReport);
        TestAssert.NotNull(back);
        Assert.That(back.TimestampUtc, Is.EqualTo(report.TimestampUtc));
        Assert.That(back.OfflineAccountName, Is.EqualTo(report.OfflineAccountName));
        Assert.That(back.PolicyKinds, Is.EqualTo(report.PolicyKinds));
        Assert.That(back.WorkspaceRoot, Is.EqualTo(report.WorkspaceRoot));
        Assert.That(back.Components, Is.EqualTo(report.Components));
        var result = TestAssert.Single(back.Cases);
        Assert.That(result, Is.EqualTo(report.Cases[0]));
        Assert.That(back.Limitations, Is.EqualTo(report.Limitations));
    }

    // ---- CLI：确认门、就绪与退出码 ----

    [Test]
    public async Task Verify_UnconfiguredSettings_RefusesWithoutCallingTheFactory()
    {
        using var fixture   = new CliFixture();
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(factory.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("settings.windowsSandbox"));
    }

    [Test]
    public async Task Verify_NotEnabled_RefusesWithoutCallingTheFactory()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, false) }
        }, CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(factory.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("enabled"));
    }

    [Test]
    public async Task Verify_NotReady_SuggestsProvisioningAndRefuses()
    {
        using var home = new SandboxTestHome();
        await File.WriteAllTextAsync(home.Components.MarkerPath, "{\"version\":4}");
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(factory.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("sandbox provision"));
    }

    [Test]
    public async Task Verify_NonInteractiveWithoutYes_Refuses()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(factory.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("--yes"));
    }

    [Test]
    public async Task Verify_InteractiveDecline_ExitsZeroWithoutRunning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture(["n"], true);
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.That(result, Is.EqualTo(0));
        Assert.That(factory.Requests, Is.Empty);
        Assert.That(fixture.Io.Output, Does.Contain("已取消"));
    }

    [Test]
    public async Task Verify_WithYesAndCaseFilter_RunsAcceptanceAndWritesReport()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(
                                                             fixture.CreateContext(),
                                                             CommandLine.Parse([
                                                                 "sandbox", "verify", "--yes",
                                                                 "--case", "exit-natural-23",
                                                                 "--workspace", workspace.Root
                                                             ]),
                                                             CancellationToken.None, factory.Invoke,
                                                             (marker, _) => new FakeWorker(marker, true));

        Assert.That(result, Is.EqualTo(0));
        Assert.That(fixture.Io.Output, Does.Contain("exit-natural-23"));
        Assert.That(fixture.Io.Output, Does.Contain("PASS 1"));
        Assert.That(fixture.Io.Output, Does.Contain(workspace.Root));
        var reportFiles = Directory.GetFiles(home.Components.SandboxDirectory, "tinyharness-verify-*.json");
        var reportPath  = TestAssert.Single(reportFiles);
        var json        = await File.ReadAllTextAsync(reportPath);
        Assert.That(json, Does.Contain("\"offlineAccountName\": \"TinyHarnessOffline\""));
        Assert.That(json, Does.Contain("limitations"));
    }

    [Test]
    public async Task Verify_AnyFailureOrIndeterminate_ExitsFour()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => Task.FromResult(new ScriptedOutcome(1)));

        var result = await SandboxVerifyCommand.ExecuteAsync(
                                                             fixture.CreateContext(),
                                                             CommandLine.Parse([
                                                                 "sandbox", "verify", "--yes",
                                                                 "--case", "identity-whoami",
                                                                 "--workspace", workspace.Root
                                                             ]),
                                                             CancellationToken.None, factory.Invoke,
                                                             (marker, _) => new FakeWorker(marker, true));

        Assert.That(result, Is.EqualTo(4));
        Assert.That(fixture.Io.Output, Does.Contain("FAIL 1"));
    }

    // ---- host-exit marker 清理与 verify-worker 的 marker 边界 ----

    [Test]
    public async Task HostExit_StaleMarkerIsClearedByTheParentBeforeSpawning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "artifacts"));
        var staleMarker = Path.Combine(workspace.Root, "artifacts", "host-exit-marker.txt");
        await File.WriteAllTextAsync(staleMarker, "stale");
        // worker 永不写 marker：父侧必须先清理陈旧 marker，否则它会立即伪造就绪并让用例
        // 错误通过。
        var runner = new WindowsSandboxAcceptanceRunner(new RecordingBackendFactory(home.Components, workspace.Root,
                                                                 (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
                                                        workspace.Root, (marker, _) => new FakeWorker(marker, false),
                                                        limits : TestLimits());

        var report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);

        Assert.That(VerdictOf(report, "host-exit-reclaim"), Is.EqualTo(SandboxAcceptanceVerdict.Fail));
        TestAssert.Contains("marker", CaseDetail(report, "host-exit-reclaim")!, StringComparison.Ordinal);
        Assert.That(File.Exists(staleMarker), Is.False, "the stale marker should have been cleared by the parent");
    }

    [Test]
    public async Task VerifyWorker_MarkerOutsideTheWorkspaceArtifacts_IsRefused()
    {
        using var directory     = new TestTempDir();
        using var fixture       = new CliFixture();
        var       outsideMarker = Path.Combine(directory.Root, "outside", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideMarker)!);
        await File.WriteAllTextAsync(outsideMarker, "keep");

        var result = await SandboxVerifyCommand.ExecuteWorkerAsync(fixture.CreateContext(),
                                                                   CommandLine.Parse([
                                                                       "sandbox", "verify-worker", "--marker",
                                                                       outsideMarker,
                                                                       "--hold-seconds", "30", "--workspace",
                                                                       Path.Combine(directory.Root, "ws")
                                                                   ]),
                                                                   CancellationToken.None);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(outsideMarker), Is.True);
        TestAssert.Contains("artifacts", fixture.Io.Error, StringComparison.Ordinal);
    }

    [Test]
    public async Task VerifyWorker_DoesNotTouchTheMarkerWhenRefusingEarly()
    {
        using var directory = new TestTempDir();
        using var fixture   = new CliFixture();
        var       workspace = Path.Combine(directory.Root, "ws");
        var       marker    = Path.Combine(workspace, "artifacts", "host-exit-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "keep");

        // 未保存用户配置：worker 必须在设置检查处拒绝，且不触碰 marker。
        var result = await SandboxVerifyCommand.ExecuteWorkerAsync(fixture.CreateContext(),
                                                                   CommandLine.Parse([
                                                                       "sandbox", "verify-worker", "--marker",
                                                                       marker,
                                                                       "--hold-seconds", "30", "--workspace",
                                                                       workspace
                                                                   ]),
                                                                   CancellationToken.None);

        Assert.That(result, Is.EqualTo(1));
        Assert.That(File.Exists(marker), Is.True);
        TestAssert.Contains("settings.windowsSandbox", fixture.Io.Error, StringComparison.Ordinal);
    }

    // ---- CLI 侧输出捕获 ----

    [Test]
    public async Task VerifyOutputCapture_TruncatesAndKeepsTheArtifact()
    {
        using var directory    = new TestTempDir();
        var       artifactPath = Path.Combine(directory.Root, "out.txt");
        var       capture      = new VerifyOutputCapture(artifactPath, 100);

        foreach (var chunk in new[] { new string('a', 60), new string('b', 60), new string('c', 60) })
            await capture.AppendAsync(chunk.AsMemory(), CancellationToken.None);

        var output = capture.Complete();
        Assert.That(output.Truncated, Is.True);
        Assert.That(output.OriginalCharacterCount, Is.EqualTo(180));
        Assert.That(output.ArtifactPath, Is.EqualTo(artifactPath));
        Assert.That(File.Exists(artifactPath), Is.True);
        Assert.That(output.Content, Does.StartWith(new string('a', 50)).Using(StringComparison.Ordinal));
        Assert.That(output.Content, Does.EndWith(new string('c', 50)).Using(StringComparison.Ordinal));
        TestAssert.Contains("[truncated 80 chars", output.Content, StringComparison.Ordinal);
    }

    [Test]
    public async Task VerifyOutputCapture_DiscardDeletesTheArtifact()
    {
        using var directory    = new TestTempDir();
        var       artifactPath = Path.Combine(directory.Root, "out.txt");
        var       capture      = new VerifyOutputCapture(artifactPath, 100);

        await capture.AppendAsync("hello".AsMemory(), CancellationToken.None);
        capture.Discard();

        Assert.That(File.Exists(artifactPath), Is.False);
    }

    /// <summary>
    ///     <para>
    ///         在一次性验收工作区里跑单个用例：每次调用使用全新工作区，避免上一个用例的残留文件
    ///         影响文件存在性判定。
    ///     </para>
    ///     <para>
    ///         Runs one case in a disposable acceptance workspace: every call gets a
    ///         fresh workspace so leftover files from a previous case never affect
    ///         the file-existence verdicts.
    ///     </para>
    /// </summary>
    private static async Task<WindowsSandboxAcceptanceReport> RunSingleCaseAsync(
        SandboxTestHome                                                          home, string caseName,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script,
        WindowsSandboxAcceptanceLimits?                                          limits = null)
    {
        using var workspace = new TestTempDir();
        var runner = new WindowsSandboxAcceptanceRunner(new RecordingBackendFactory(home.Components, workspace.Root,
                                                                 script).Invoke,
                                                        workspace.Root, (marker, _) => new FakeWorker(marker, true),
                                                        limits : limits ?? TestLimits());
        return await runner.RunAsync([caseName], CancellationToken.None);
    }

    private static WindowsSandboxAcceptanceLimits TestLimits()
    {
        return new WindowsSandboxAcceptanceLimits
        {
            OverallDeadline             = TimeSpan.FromSeconds(60),
            ProcessScanTimeout          = TimeSpan.FromSeconds(2),
            ProcessScanInterval         = TimeSpan.FromMilliseconds(20),
            MarkerWaitTimeout           = TimeSpan.FromSeconds(1),
            TerminateCancelDelay        = TimeSpan.FromMilliseconds(60),
            WorkerHoldSeconds           = 10,
            LargeOutputTargetCharacters = 60_000,
            ModelOutputCharacterLimit   = 4_096,
            NonLoopbackAddressResolver  = () => "10.255.255.1",
            CurlPathResolver            = () => "C:\\fake\\curl.exe"
        };
    }

    /// <summary>
    ///     <para>
    ///         满足全矩阵的脚本：whoami 返回 offline 账户、probe 退出 0、curl 回环成功/非回环被拒、
    ///         cmd exit/type/copy 按目标语义应答。
    ///     </para>
    ///     <para>
    ///         The full-matrix happy script: whoami prints the offline account, the
    ///         probe exits 0, curl succeeds on loopback and is blocked otherwise,
    ///         and cmd exit/type/copy answer per target semantics.
    ///     </para>
    /// </summary>
    private static Task<ScriptedOutcome> HappyScriptAsync(PreparedProcessExecution plan, CancellationToken ct)
    {
        switch (Path.GetFileName(plan.Executable))
        {
            case "whoami.exe" :
                return Task.FromResult(new ScriptedOutcome(0, StdoutChunks :
                [
                    $"test-machine\\{WindowsSandboxComponents.DefaultOfflineUsername}\n"
                ]));
            case "codex-windows-managed-deny-probe.exe" :
                return Task.FromResult(new ScriptedOutcome(0));
            case "curl.exe" :
                return Task.FromResult(LastArgument(plan).Contains("127.0.0.1", StringComparison.Ordinal)
                                           ? new ScriptedOutcome(0)
                                           : new ScriptedOutcome(7));
            case "powershell.exe" :
                // 超时用例：runner 上报其超时判定。
                return Task.FromResult(new ScriptedOutcome(192, true));
            case "cmd.exe" :
                return CmdScriptAsync(plan);
            default :
                return Task.FromResult(new ScriptedOutcome(1));
        }
    }

    private static Task<ScriptedOutcome> StallThenHappyScript(PreparedProcessExecution plan, CancellationToken ct)
    {
        if (Path.GetFileName(plan.Executable) != "whoami.exe") return HappyScriptAsync(plan, ct);

        return StallAsync(ct);

        static async Task<ScriptedOutcome> StallAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken);
            return new ScriptedOutcome(0);
        }
    }

    private static async Task<ScriptedOutcome> WaitUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new ScriptedOutcome(0);
    }

    private static async Task<ScriptedOutcome> CmdScriptAsync(PreparedProcessExecution plan)
    {
        var command = LastArgument(plan);
        if (command.StartsWith("exit 23", StringComparison.Ordinal)) return new ScriptedOutcome(23);

        if (command.StartsWith("exit 192", StringComparison.Ordinal)) return new ScriptedOutcome(192);

        if (command.StartsWith("type ", StringComparison.Ordinal))
        {
            var content = await File.ReadAllTextAsync(QuotedPath(command)).ConfigureAwait(false);
            var chunks  = new List<string>();
            for (var offset = 0; offset < content.Length; offset += 8192)
                chunks.Add(content.Substring(offset, Math.Min(8192, content.Length - offset)));

            return new ScriptedOutcome(0, StdoutChunks : chunks);
        }

        if (command.StartsWith("copy ", StringComparison.Ordinal))
        {
            var target = QuotedPath(command);
            if (command.Contains("write-allowed-probe", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(target, string.Empty).ConfigureAwait(false);
                return new ScriptedOutcome(0);
            }

            return new ScriptedOutcome(1);
        }

        return new ScriptedOutcome(1);
    }

    /// <summary>
    ///     <para>
    ///         在 workspaceRoot 下复制一份 cmd.exe 并以 runner 镜像名启动，等它被验收运行器击杀后返回
    ///         afterKill——模拟“按镜像名定位本用例 runner 并外部击杀”。
    ///     </para>
    ///     <para>
    ///         Copies cmd.exe under workspaceRoot, starts it under the runner's
    ///         image name, waits for the acceptance runner to kill it, then returns
    ///         afterKill — simulating "locate this case's runner by image name and
    ///         kill it externally".
    ///     </para>
    /// </summary>
    private static async Task<ScriptedOutcome> SimulateExternalRunnerKillAsync(
        string workspaceRoot, ScriptedOutcome afterKill)
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec")
                   ?? throw new InvalidOperationException("ComSpec is required for the broken-pipe simulation.");
        var directory = Path.Combine(workspaceRoot, "bp-sim");
        Directory.CreateDirectory(directory);
        var image = Path.Combine(directory, "codex-command-runner.exe");
        File.Copy(comSpec, image, true);
        using var process = Process.Start(new ProcessStartInfo(image, "/c ping -n 60 -w 1000 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow  = true
        });
        while (!process!.HasExited) await Task.Delay(20).ConfigureAwait(false);

        return afterKill;
    }

    private static string LastArgument(PreparedProcessExecution plan) =>
        plan.Arguments.Count > 0 ? plan.Arguments[^1] : string.Empty;

    private static string QuotedPath(string command)
    {
        // 验收 fixture 嵌入裸路径（无引号）；手写命令的带引号形式仍可解析。
        var parts = command.Split('"');
        return parts.Length > 1
            ? parts[1]
            : command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
    }

    private static SandboxAcceptanceVerdict VerdictOf(WindowsSandboxAcceptanceReport report, string caseName)
    {
        return report.Cases.Single(result => result.Name == caseName).Verdict;
    }

    private static string? CaseDetail(WindowsSandboxAcceptanceReport report, string caseName)
    {
        return report.Cases.Single(result => result.Name == caseName).Detail;
    }

    private static WindowsSandboxSettings SandboxSettings(SandboxTestHome home, bool enabled = true) => new()
    {
        Enabled              = enabled,
        SetupExecutablePath  = home.Components.SetupExecutablePath,
        RunnerExecutablePath = home.Components.RunnerExecutablePath,
        SandboxHome          = home.Components.SandboxHome
    };

    private static UserConfig SandboxConfig(SandboxTestHome home, bool enabled = true) => new()
        { Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, enabled) } };

    // ---- 脚本与 fixture ----

    private sealed record ScriptedOutcome(
        int?                     ExitCode     = null,
        bool                     TimedOut     = false,
        ProcessExecutionFailure? Failure      = null,
        IReadOnlyList<string>?   StdoutChunks = null,
        Exception?               Throw        = null);

    /// <summary>
    ///     <para>按计划脚本化的 fake 后端：把结果/异常原样呈给运行器，不做任何真实进程操作。</para>
    ///     <para>
    ///         A scripted fake backend: hands the scripted result or exception to
    ///         the runner without any real process activity.
    ///     </para>
    /// </summary>
    private sealed class ScriptedBackend(
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script) : IProcessExecutionBackend
    {
        public async Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                               IProcessOutputCapture    stdoutCapture,
                                                               IProcessOutputCapture    stderrCapture,
                                                               CancellationToken        cancellationToken)
        {
            var outcome = await script(execution, cancellationToken).ConfigureAwait(false);
            foreach (var chunk in outcome.StdoutChunks ?? [])
                await stdoutCapture.AppendAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);

            await stdoutCapture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            await stderrCapture.AppendAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            if (outcome.Throw is not null) throw outcome.Throw;

            return new ProcessExecutionResult(outcome.ExitCode, outcome.TimedOut, outcome.Failure);
        }
    }

    /// <summary>
    ///     <para>
    ///         录制式工厂 fake：记录每次 (策略种类, 附加环境) 请求并返回脚本化后端组合。策略用真实的
    ///         SandboxPolicyResolver 对验收工作区解析，使读根/写根判定与生产语义一致。
    ///     </para>
    ///     <para>
    ///         A recording factory fake: it records every (policy kind, extra
    ///         environment) request and returns a scripted backend composition. The
    ///         policy is resolved with the real SandboxPolicyResolver against the
    ///         acceptance workspace so read-root/write-root verdicts match
    ///         production semantics.
    ///     </para>
    /// </summary>
    private sealed class RecordingBackendFactory(
        WindowsSandboxComponents                                                 components,
        string                                                                   workspaceRoot,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script)
    {
        public List<(SandboxPolicyKind Kind, IReadOnlyDictionary<string, string>? ExtraEnvironment)> Requests { get; } =
            [];

        public WindowsSandboxExecution Invoke(
            SandboxPolicyKind kind, IReadOnlyDictionary<string, string>? extraEnvironment)
        {
            Requests.Add((kind, extraEnvironment));
            var tempRoot = Path.Combine(components.SandboxHome, "tmp");
            var targetEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TEMP"] = tempRoot,
                ["TMP"]  = tempRoot
            };
            var policy = SandboxPolicyResolver.Resolve(kind, workspaceRoot, targetEnvironment);
            return new WindowsSandboxExecution(components, policy, targetEnvironment,
                                               ProcessExecutionPolicy.Host(), new ScriptedBackend(script));
        }
    }

    /// <summary>
    ///     <para>fake verify-worker：按标志立即写 marker（或永不写）；Kill 仅记录。</para>
    ///     <para>
    ///         A fake verify-worker: it writes the marker immediately (or never),
    ///         and Kill only records the call.
    ///     </para>
    /// </summary>
    private sealed class FakeWorker : ISandboxVerifyWorkerProcess
    {
        public FakeWorker(string markerPath, bool writeMarker)
        {
            if (writeMarker) File.WriteAllText(markerPath, DateTimeOffset.UtcNow.ToString("O"));
        }

        public bool Killed { get; private set; }

        public void Kill()
        {
            Killed = true;
        }
    }

    private sealed class CliFixture(IReadOnlyList<string>? input = null) : IDisposable
    {
        public CliFixture(IReadOnlyList<string> input, bool interactive) : this(input) => Io.Interactive = interactive;

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tinyharness-verify-{Guid.NewGuid():N}");

        public string UserConfigPath { get; } = Path.Combine(Path.GetTempPath(),
                                                             $"tinyharness-verify-config-{Guid.NewGuid():N}",
                                                             "user-config.json");

        public FakeCliConsole Io { get; } = new(input);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);

            var configDirectory = Path.GetDirectoryName(UserConfigPath);
            if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true);
        }

        public CommandContext CreateContext() => new()
        {
            Io               = Io,
            Credentials      = new FakeCredentialStore(),
            UserConfigPath   = UserConfigPath,
            WorkingDirectory = Root
        };
    }

    private sealed class FakeCliConsole(IReadOnlyList<string>? input) : ICliConsole
    {
        private readonly StringBuilder  _error  = new();
        private readonly Queue<string?> _input  = new(input ?? []);
        private readonly StringBuilder  _output = new();

        public bool Interactive { get; set; }

        public string Output => _output.ToString();

        public string Error => _error.ToString();

        public bool IsInteractive => Interactive;

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            _output.Append(text);
            return Task.CompletedTask;
        }

        public Task WriteLineAsync(string text, CancellationToken cancellationToken)
        {
            _output.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task WriteErrorLineAsync(string text, CancellationToken cancellationToken)
        {
            _error.AppendLine(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
            _entries[targetName] = secret;
        }

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
