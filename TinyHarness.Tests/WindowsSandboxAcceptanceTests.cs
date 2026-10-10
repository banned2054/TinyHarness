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
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     M11.5 隔离验收：Core 验收运行器（判定逻辑、用例过滤、总期限、报告序列化）与 CLI 入口
///     （parser、确认门、就绪拒绝、退出码）的离线测试。后端全部为脚本化 fake——不启动真实
///     setup/runner、不做 refresh、不修改机器账户/ACL/防火墙；host-exit 用例用 fake worker，
///     broken-pipe 用例用复制镜像的本地短命进程模拟 runner。
///     M11.5 isolation acceptance: offline tests of the Core acceptance runner
///     (verdict logic, case filtering, overall deadline, report serialization)
///     and the CLI entry (parser, confirmation gate, readiness refusal, exit
///     codes). Every backend is a scripted fake — no real setup/runner launch,
///     no refresh, no machine account/ACL/firewall changes; the host-exit case
///     uses a fake worker and the broken-pipe case a short-lived local process
///     with a copied image name simulating the runner.
/// </summary>
public sealed class WindowsSandboxAcceptanceTests
{
    // ---- Parser ----

    [Fact]
    public void SandboxVerify_ParsesWithoutOptions()
    {
        var options = CommandLine.Parse(["sandbox", "verify"]);

        Assert.Equal(CliCommandKind.Sandbox, options.Kind);
        Assert.Equal("verify", options.Subcommand);
        Assert.False(options.AssumeYes);
        Assert.Null(options.CaseFilter);
        Assert.Null(options.VerifyWorkspace);
    }

    [Fact]
    public void SandboxVerify_ParsesYesCaseAndWorkspace()
    {
        var options = CommandLine.Parse(["sandbox", "verify", "--yes", "--case", "deny-probe,exit-natural-23",
                                         "--workspace", "C:\\verify-ws"]);

        Assert.Equal("verify", options.Subcommand);
        Assert.True(options.AssumeYes);
        Assert.Equal(["deny-probe", "exit-natural-23"], options.CaseFilter);
        Assert.Equal("C:\\verify-ws", options.VerifyWorkspace);
    }

    [Fact]
    public void SandboxVerify_UnknownArgument_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--bogus"]));
    }

    [Fact]
    public void SandboxVerify_CaseWithoutValue_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case"]));
    }

    [Fact]
    public void SandboxVerify_CaseWithOnlySeparators_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case", ","]));
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--case", " "]));
    }

    [Fact]
    public void SandboxVerify_WorkspaceWithoutValue_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--workspace"]));
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify", "--workspace", " "]));
    }

    [Fact]
    public void SandboxVerifyWorker_ParsesAllRequiredOptions()
    {
        var options = CommandLine.Parse(["sandbox", "verify-worker", "--marker", "C:\\m.txt", "--hold-seconds", "30",
                                         "--workspace", "C:\\ws"]);

        Assert.Equal("verify-worker", options.Subcommand);
        Assert.Equal("C:\\m.txt", options.VerifyMarkerPath);
        Assert.Equal(30, options.VerifyHoldSeconds);
        Assert.Equal("C:\\ws", options.VerifyWorkspace);
    }

    [Fact]
    public void SandboxVerifyWorker_MissingRequiredOptions_AreUsageErrors()
    {
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify-worker"]));
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["sandbox", "verify-worker", "--marker", "C:\\m.txt"]));
        Assert.Throws<CliUsageException>(() =>
                                             CommandLine.Parse(["sandbox", "verify-worker", "--marker", "C:\\m",
                                                                "--hold-seconds", "5"]));
    }

    [Fact]
    public void SandboxVerifyWorker_InvalidHoldSeconds_IsAUsageError()
    {
        Assert.Throws<CliUsageException>(() =>
                                             CommandLine.Parse(["sandbox", "verify-worker", "--hold-seconds", "0"]));
        Assert.Throws<CliUsageException>(() =>
                                             CommandLine.Parse(["sandbox", "verify-worker", "--hold-seconds", "x"]));
    }

    [Fact]
    public void SandboxVerifyWorker_MissingOptionValueAndUnknownArgument_AreUsageErrors()
    {
        Assert.Throws<CliUsageException>(() =>
                                             CommandLine.Parse(["sandbox", "verify-worker", "--marker", "C:\\m",
                                                                "--hold-seconds", "5", "--workspace"]));
        Assert.Throws<CliUsageException>(() =>
                                             CommandLine.Parse(["sandbox", "verify-worker", "--extra", "1"]));
    }

    // ---- 运行器：全矩阵与逐例判定 ----

    [Fact]
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
            // Long sleepers are powershell Start-Sleep with a 300s+ command
            // timeout; timeout-finite uses the same executable with an 8s
            // timeout and keeps going through the happy script's timed-out
            // verdict.
            if (Path.GetFileName(plan.Executable) != "powershell.exe" || plan.TimeoutSeconds < 300)
                return HappyScriptAsync(plan, ct);

            // Fixed case order: terminate-active and cancel-startup observe
            // cancellation; runner-broken-pipe gets the killable dummy runner.
            var index = ++sleeperCount;
            return index <= 2
                ? WaitUntilCancelledAsync(ct)
                : SimulateExternalRunnerKillAsync(workspace.Root,
                                                  new ScriptedOutcome(Failure: new ProcessExecutionFailure(
                                                                          "The runner pipe closed before the exit frame; no command exit code is available.")));
        });

        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root,
                                                        (marker, _) => new FakeWorker(marker, true),
                                                        limits : TestLimits());
        var report = await runner.RunAsync(null, CancellationToken.None);

        Assert.All(report.Cases,
                   result => Assert.True(result.Verdict == SandboxAcceptanceVerdict.Pass,
                                          $"{result.Name}: {result.Verdict} {result.Detail}"));
        Assert.Equal(WindowsSandboxAcceptanceRunner.CaseNames.Count, report.Cases.Count);
        Assert.Contains("read-only", report.PolicyKinds);
        Assert.Contains("workspace-write", report.PolicyKinds);
        Assert.NotEmpty(report.Limitations);
        Assert.Equal(WindowsSandboxComponents.DefaultOfflineUsername, report.OfflineAccountName);

        // The report lands in <home>\.sandbox\tinyharness-verify-*.json.
        Assert.NotNull(report.ReportPath);
        Assert.True(File.Exists(report.ReportPath));
        Assert.StartsWith(home.Components.SandboxDirectory, report.ReportPath);
        Assert.Contains("tinyharness-verify-", report.ReportPath);

        // The deny-probe variables reached the factory through the cleaned
        // environment seam (extraEnvironment), never a hand-built env.
        var probeRequest = Assert.Single(factory.Requests, request => request.ExtraEnvironment is { Count: 4 });
        Assert.Contains("CODEX_WINDOWS_ALLOWED_TEXT", probeRequest.ExtraEnvironment!.Keys);
        Assert.Contains("CODEX_WINDOWS_DENIED_MODULE", probeRequest.ExtraEnvironment.Keys);
    }

    [Fact]
    public async Task CaseFilter_RunsOnlyTheSelectedCase()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));
        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());
        var report = await runner.RunAsync(["exit-natural-23"], CancellationToken.None);

        var result = Assert.Single(report.Cases);
        Assert.Equal("exit-natural-23", result.Name);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, result.Verdict);
        Assert.All(factory.Requests, request => Assert.Equal(SandboxPolicyKind.WorkspaceWrite, request.Kind));
    }

    [Fact]
    public async Task UnknownCaseName_ThrowsArgumentException()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));
        var runner  = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(["no-such-case"], CancellationToken.None));
    }

    [Fact]
    public async Task OverallDeadline_InterruptsCurrentCaseAndSkipsTheRest()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root, StallThenHappyScript);
        var limits  = TestLimits() with { OverallDeadline = TimeSpan.FromMilliseconds(150) };
        var runner  = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : limits);
        var report  = await runner.RunAsync(null, CancellationToken.None);

        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "identity-whoami"));
        Assert.Contains(report.Cases, result => result.Verdict == SandboxAcceptanceVerdict.Skipped);
        Assert.Contains("总期限", CaseDetail(report, "identity-whoami")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExitSemantics_ExitCodeAloneNeverMeansTimeout()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root, (plan, ct) =>
        {
            var command = LastArgument(plan);
            if (command.StartsWith("exit 192", StringComparison.Ordinal))
                // A lying backend that reports 192 WITH timed_out=true for a
                // natural exit must not fool the verdict.
                return Task.FromResult(new ScriptedOutcome(192, TimedOut : true));

            if (Path.GetFileName(plan.Executable) == "powershell.exe")
                // The timeout case expects TimedOut=true; a plain success fails.
                return Task.FromResult(new ScriptedOutcome(0));

            return HappyScriptAsync(plan, ct);
        });
        var runner = new WindowsSandboxAcceptanceRunner(factory.Invoke, workspace.Root, limits : TestLimits());
        var report = await runner.RunAsync(["exit-natural-23", "exit-natural-192", "timeout-finite"],
                                          CancellationToken.None);

        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "exit-natural-23"));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "exit-natural-192"));
        Assert.Contains("绝不", CaseDetail(report, "exit-natural-192")!, StringComparison.Ordinal);
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "timeout-finite"));
    }

    [Fact]
    public async Task DenyProbe_VerdictsFollowTheExitCodeContract()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        await File.WriteAllTextAsync(Path.Combine(home.Components.SandboxHome,
                                                  "codex-windows-managed-deny-probe.exe"), "MZ");

        // 0 = PASS; 20 = isolation breach FAIL; throw = no-exit-code FAIL.
        var report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(0)));
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "deny-probe"));

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(20)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "deny-probe"));
        Assert.Contains("隔离失效", CaseDetail(report, "deny-probe")!, StringComparison.Ordinal);

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(21)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "deny-probe"));
        Assert.Contains("fixture", CaseDetail(report, "deny-probe")!, StringComparison.Ordinal);

        report = await RunSingleCaseAsync(home, "deny-probe", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(Throw : new InvalidOperationException("pipe broke"))));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "deny-probe"));
    }

    [Fact]
    public async Task WriteProbes_AllowedAndDeniedJudgements()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();

        // Happy path: allowed write creates the file with exit 0; all deny
        // targets fail without creating anything.
        var report = await RunSingleCaseAsync(home, "workspace-write-allowed", HappyScriptAsync);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "workspace-write-allowed"));
        report = await RunSingleCaseAsync(home, "readonly-workspace-write-denied", HappyScriptAsync);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "readonly-workspace-write-denied"));
        report = await RunSingleCaseAsync(home, "write-denied-outside-roots", HappyScriptAsync);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "write-denied-outside-roots"));
        report = await RunSingleCaseAsync(home, "metadata-protected", HappyScriptAsync);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "metadata-protected"));
        Assert.Contains("deny-write", CaseDetail(report, "metadata-protected")!, StringComparison.Ordinal);

        // A write that "succeeds" (exit 0) but leaves no file is a broken
        // positive control.
        report = await RunSingleCaseAsync(home, "workspace-write-allowed", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(0)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "workspace-write-allowed"));

        // An outside-root write that creates the file means isolation failed.
        report = await RunSingleCaseAsync(home, "write-denied-outside-roots", (plan, ct) =>
        {
            var target = QuotedPath(LastArgument(plan));
            File.WriteAllText(target, string.Empty);
            return Task.FromResult(new ScriptedOutcome(0));
        });
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "write-denied-outside-roots"));
    }

    [Fact]
    public async Task NetworkCase_VerdictTriad()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();

        var report = await RunSingleCaseAsync(home, "network-denied", (plan, ct) =>
            Task.FromResult(Path.GetFileName(plan.Executable) == "curl.exe" && !LastArgument(plan)
                                                                                                .Contains("127.0.0.1",
                                                                                                          StringComparison
                                                                                                              .Ordinal)
                ? new ScriptedOutcome(7)
                : new ScriptedOutcome(0)));
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "network-denied"));

        report = await RunSingleCaseAsync(home, "network-denied", (plan, ct) =>
            Task.FromResult(new ScriptedOutcome(0)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "network-denied"));

        var limits = TestLimits() with { NonLoopbackAddressResolver = () => null };
        report = await RunSingleCaseAsync(home, "network-denied", HappyScriptAsync, limits);
        Assert.Equal(SandboxAcceptanceVerdict.Indeterminate, VerdictOf(report, "network-denied"));

        limits  = TestLimits() with { CurlPathResolver = () => null };
        report = await RunSingleCaseAsync(home, "network-denied", HappyScriptAsync, limits);
        Assert.Equal(SandboxAcceptanceVerdict.Indeterminate, VerdictOf(report, "network-denied"));
    }

    [Fact]
    public async Task LargeOutput_TruncatesModelViewAndKeepsArtifact()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var limits  = TestLimits();
        var runner  = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root,
                                        (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, true), limits : limits);
        var report = await runner.RunAsync(["large-output"], CancellationToken.None);
        var result = report.Cases.Single(caseResult => caseResult.Name == "large-output");

        Assert.Equal(SandboxAcceptanceVerdict.Pass, result.Verdict);
        Assert.Contains("截断", result.Detail, StringComparison.Ordinal);
        var artifactLine = result.Detail!.Split("完整 artifact: ")[^1];
        Assert.True(File.Exists(artifactLine), $"artifact should exist: {artifactLine}");
        var generated = Path.Combine(workspace.Root, "large-output.txt");
        Assert.True(File.Exists(generated));
        Assert.Equal(new FileInfo(generated).Length, new FileInfo(artifactLine).Length);
        Assert.True(new FileInfo(artifactLine).Length >= limits.LargeOutputTargetCharacters);

        // Tiny output that never crosses the budget must not count as truncated.
        var tiny = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root, (plan, ct) =>
                Task.FromResult(new ScriptedOutcome(0, StdoutChunks: ["small"]))).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, true), limits : limits);
        report = await tiny.RunAsync(["large-output"], CancellationToken.None);
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "large-output"));
    }

    [Fact]
    public async Task TerminateAndCancel_CancellationOutcomeIsJudgedStrictly()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        var report = await RunSingleCaseAsync(home, "terminate-active",
                                              (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "terminate-active"));

        report = await RunSingleCaseAsync(home, "cancel-startup",
                                          (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "cancel-startup"));

        // A backend that swallows the cancellation and returns a result fails
        // the case: cancellation must surface as OperationCanceledException.
        report = await RunSingleCaseAsync(home, "terminate-active",
                                          (plan, ct) => Task.FromResult(new ScriptedOutcome(1)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "terminate-active"));
        Assert.Contains("OperationCanceledException", CaseDetail(report, "terminate-active")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenPipe_NoExitCodeMayBeInvented()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var report = await RunSingleCaseAsync(home, "runner-broken-pipe", (plan, ct) =>
            SimulateExternalRunnerKillAsync(workspace.Root,
                                            new ScriptedOutcome(Failure: new ProcessExecutionFailure(
                                                                    "The runner pipe closed before the exit frame."))));
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "runner-broken-pipe"));

        // The kill succeeded but the backend invented exit code 5 — a contract
        // violation the acceptance must catch.
        report = await RunSingleCaseAsync(home, "runner-broken-pipe", (plan, ct) =>
            SimulateExternalRunnerKillAsync(workspace.Root, new ScriptedOutcome(5)));
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "runner-broken-pipe"));
        Assert.Contains("补造", CaseDetail(report, "runner-broken-pipe")!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokenPipe_WithoutALocatableRunner_IsIndeterminate()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        // A plain sleeper without any runner image: the locate window expires.
        var report = await RunSingleCaseAsync(home, "runner-broken-pipe",
                                              (plan, ct) => WaitUntilCancelledAsync(ct));
        Assert.Equal(SandboxAcceptanceVerdict.Indeterminate, VerdictOf(report, "runner-broken-pipe"));
    }

    [Fact]
    public async Task HostExit_MarkerTimeoutFailsWhileReadyMarkerPasses()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();

        // The worker never writes the marker: the bounded wait fails the case.
        var runner = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root,
                                        (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, false), limits : TestLimits());
        var report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);
        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "host-exit-reclaim"));
        Assert.Contains("marker", CaseDetail(report, "host-exit-reclaim")!, StringComparison.Ordinal);

        // A worker that writes the marker and dies on Kill leaves no survivors.
        runner = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root,
                                        (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, true), limits : TestLimits());
        report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);
        Assert.Equal(SandboxAcceptanceVerdict.Pass, VerdictOf(report, "host-exit-reclaim"));
    }

    [Fact]
    public async Task SurvivorsAfterCancellation_FailTheCase()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var limits  = TestLimits() with { SurvivorImageNames = ["cmd"] };
        var runner  = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root, (plan, ct) =>
            {
                if (Path.GetFileName(plan.Executable) != "powershell.exe") return HappyScriptAsync(plan, ct);

                // Leave a same-image process behind: the survivor scan must
                // catch it after the cancellation.
                var comSpec = Environment.GetEnvironmentVariable("ComSpec")!;
                var process = Process.Start(new ProcessStartInfo(comSpec, "/c ping -n 20 -w 1000 127.0.0.1")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true
                });
                return WaitUntilCancelledAsync(ct);
            }).Invoke, workspace.Root, limits : limits);
        var report = await runner.RunAsync(["terminate-active"], CancellationToken.None);

        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "terminate-active"));
        Assert.Contains("残留", CaseDetail(report, "terminate-active")!, StringComparison.Ordinal);
    }

    // ---- 报告序列化 ----

    [Fact]
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
                SetupExecutable  = new SandboxAcceptanceFileInfo { Path = @"C:\sb\setup.exe", SizeBytes = 17 },
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
        Assert.Contains("\"verdict\": \"Pass\"", json, StringComparison.Ordinal);
        Assert.Contains("\"markerVersion\": 5", json, StringComparison.Ordinal);
        Assert.Contains("\"setupVersion\": 5", json, StringComparison.Ordinal);
        Assert.Contains("\"ipcVersion\": 6", json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        var back = JsonSerializer.Deserialize(json,
                                              WindowsSandboxAcceptanceJsonContext.Default.WindowsSandboxAcceptanceReport);
        Assert.NotNull(back);
        Assert.Equal(report.TimestampUtc, back.TimestampUtc);
        Assert.Equal(report.OfflineAccountName, back.OfflineAccountName);
        Assert.Equal(report.PolicyKinds, back.PolicyKinds);
        Assert.Equal(report.WorkspaceRoot, back.WorkspaceRoot);
        Assert.Equal(report.Components, back.Components);
        var result = Assert.Single(back.Cases);
        Assert.Equal(report.Cases[0], result);
        Assert.Equal(report.Limitations, back.Limitations);
    }

    // ---- CLI：确认门、就绪与退出码 ----

    [Fact]
    public async Task Verify_UnconfiguredSettings_RefusesWithoutCallingTheFactory()
    {
        using var fixture  = new CliFixture();
        using var home     = new SandboxTestHome();
        using var workspace = new TestTempDir();
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.Equal(1, result);
        Assert.Empty(factory.Requests);
        Assert.Contains("settings.windowsSandbox", fixture.Io.Output);
    }

    [Fact]
    public async Task Verify_NotEnabled_RefusesWithoutCallingTheFactory()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, new UserConfig
        {
            Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, enabled : false) }
        }, CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.Equal(1, result);
        Assert.Empty(factory.Requests);
        Assert.Contains("enabled", fixture.Io.Output);
    }

    [Fact]
    public async Task Verify_NotReady_SuggestsProvisioningAndRefuses()
    {
        using var home      = new SandboxTestHome();
        await File.WriteAllTextAsync(home.Components.MarkerPath, "{\"version\":4}");
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home), CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify", "--yes"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.Equal(1, result);
        Assert.Empty(factory.Requests);
        Assert.Contains("sandbox provision", fixture.Io.Output);
    }

    [Fact]
    public async Task Verify_NonInteractiveWithoutYes_Refuses()
    {
        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.Equal(1, result);
        Assert.Empty(factory.Requests);
        Assert.Contains("--yes", fixture.Io.Output);
    }

    [Fact]
    public async Task Verify_InteractiveDecline_ExitsZeroWithoutRunning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture(["n"], interactive : true);
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(fixture.CreateContext(),
                                                             CommandLine.Parse(["sandbox", "verify"]),
                                                             CancellationToken.None, factory.Invoke);

        Assert.Equal(0, result);
        Assert.Empty(factory.Requests);
        Assert.Contains("已取消", fixture.Io.Output);
    }

    [Fact]
    public async Task Verify_WithYesAndCaseFilter_RunsAcceptanceAndWritesReport()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => HappyScriptAsync(plan, ct));

        var result = await SandboxVerifyCommand.ExecuteAsync(
            fixture.CreateContext(),
            CommandLine.Parse(["sandbox", "verify", "--yes", "--case", "exit-natural-23",
                               "--workspace", workspace.Root]),
            CancellationToken.None, factory.Invoke, (marker, _) => new FakeWorker(marker, true));

        Assert.Equal(0, result);
        Assert.Contains("exit-natural-23", fixture.Io.Output);
        Assert.Contains("PASS 1", fixture.Io.Output);
        Assert.Contains(workspace.Root, fixture.Io.Output);
        var reportFiles = Directory.GetFiles(home.Components.SandboxDirectory, "tinyharness-verify-*.json");
        var reportPath  = Assert.Single(reportFiles);
        var json        = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("\"offlineAccountName\": \"TinyHarnessOffline\"", json);
        Assert.Contains("limitations", json);
    }

    [Fact]
    public async Task Verify_AnyFailureOrIndeterminate_ExitsFour()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        using var fixture   = new CliFixture();
        await UserConfigStore.SaveAsync(fixture.UserConfigPath, SandboxConfig(home, enabled : true),
                                        CancellationToken.None);
        var factory = new RecordingBackendFactory(home.Components, workspace.Root,
                                                  (plan, ct) => Task.FromResult(new ScriptedOutcome(1)));

        var result = await SandboxVerifyCommand.ExecuteAsync(
            fixture.CreateContext(),
            CommandLine.Parse(["sandbox", "verify", "--yes", "--case", "identity-whoami",
                               "--workspace", workspace.Root]),
            CancellationToken.None, factory.Invoke, (marker, _) => new FakeWorker(marker, true));

        Assert.Equal(4, result);
        Assert.Contains("FAIL 1", fixture.Io.Output);
    }

    // ---- host-exit marker 清理与 verify-worker 的 marker 边界 ----

    [Fact]
    public async Task HostExit_StaleMarkerIsClearedByTheParentBeforeSpawning()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new SandboxTestHome();
        using var workspace = new TestTempDir();
        Directory.CreateDirectory(Path.Combine(workspace.Root, "artifacts"));
        var staleMarker = Path.Combine(workspace.Root, "artifacts", "host-exit-marker.txt");
        await File.WriteAllTextAsync(staleMarker, "stale");
        // A worker that never writes: without the parent-side cleanup the stale
        // marker would fake readiness instantly and the case would PASS.
        var runner = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root,
                                        (plan, ct) => HappyScriptAsync(plan, ct)).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, writeMarker : false), limits : TestLimits());

        var report = await runner.RunAsync(["host-exit-reclaim"], CancellationToken.None);

        Assert.Equal(SandboxAcceptanceVerdict.Fail, VerdictOf(report, "host-exit-reclaim"));
        Assert.Contains("marker", CaseDetail(report, "host-exit-reclaim")!, StringComparison.Ordinal);
        Assert.False(File.Exists(staleMarker), "the stale marker should have been cleared by the parent");
    }

    [Fact]
    public async Task VerifyWorker_MarkerOutsideTheWorkspaceArtifacts_IsRefused()
    {
        using var directory = new TestTempDir();
        using var fixture   = new CliFixture();
        var outsideMarker = Path.Combine(directory.Root, "outside", "marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(outsideMarker)!);
        await File.WriteAllTextAsync(outsideMarker, "keep");

        var result = await SandboxVerifyCommand.ExecuteWorkerAsync(
            fixture.CreateContext(),
            CommandLine.Parse(["sandbox", "verify-worker", "--marker", outsideMarker,
                               "--hold-seconds", "30", "--workspace", Path.Combine(directory.Root, "ws")]),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.True(File.Exists(outsideMarker));
        Assert.Contains("artifacts", fixture.Io.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyWorker_DoesNotTouchTheMarkerWhenRefusingEarly()
    {
        using var directory = new TestTempDir();
        using var fixture   = new CliFixture();
        var workspace = Path.Combine(directory.Root, "ws");
        var marker    = Path.Combine(workspace, "artifacts", "host-exit-marker.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        await File.WriteAllTextAsync(marker, "keep");

        // No user config saved: the worker must refuse on the settings check
        // and leave the marker untouched (the old code deleted it up front).
        var result = await SandboxVerifyCommand.ExecuteWorkerAsync(
            fixture.CreateContext(),
            CommandLine.Parse(["sandbox", "verify-worker", "--marker", marker,
                               "--hold-seconds", "30", "--workspace", workspace]),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.True(File.Exists(marker));
        Assert.Contains("settings.windowsSandbox", fixture.Io.Error, StringComparison.Ordinal);
    }

    // ---- CLI 侧输出捕获 ----

    [Fact]
    public async Task VerifyOutputCapture_TruncatesAndKeepsTheArtifact()
    {
        using var directory = new TestTempDir();
        var artifactPath = Path.Combine(directory.Root, "out.txt");
        var capture = new VerifyOutputCapture(artifactPath, 100);

        foreach (var chunk in new[] { new string('a', 60), new string('b', 60), new string('c', 60) })
            await capture.AppendAsync(chunk.AsMemory(), CancellationToken.None);

        var output = capture.Complete();
        Assert.True(output.Truncated);
        Assert.Equal(180, output.OriginalCharacterCount);
        Assert.Equal(artifactPath, output.ArtifactPath);
        Assert.True(File.Exists(artifactPath));
        Assert.StartsWith(new string('a', 50), output.Content, StringComparison.Ordinal);
        Assert.EndsWith(new string('c', 50), output.Content, StringComparison.Ordinal);
        Assert.Contains("[truncated 80 chars", output.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyOutputCapture_DiscardDeletesTheArtifact()
    {
        using var directory = new TestTempDir();
        var artifactPath = Path.Combine(directory.Root, "out.txt");
        var capture = new VerifyOutputCapture(artifactPath, 100);

        await capture.AppendAsync("hello".AsMemory(), CancellationToken.None);
        capture.Discard();

        Assert.False(File.Exists(artifactPath));
    }

    // ---- 脚本与 fixture ----

    private sealed record ScriptedOutcome(
        int?                     ExitCode     = null,
        bool                     TimedOut     = false,
        ProcessExecutionFailure? Failure      = null,
        IReadOnlyList<string>?   StdoutChunks = null,
        Exception?               Throw        = null);

    /// <summary>
    ///     按计划脚本化的 fake 后端：把结果/异常原样呈给运行器，不做任何真实进程操作。
    ///     A scripted fake backend: hands the scripted result or exception to
    ///     the runner without any real process activity.
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
    ///     录制式工厂 fake：记录每次 (策略种类, 附加环境) 请求并返回脚本化后端组合。策略用真实的
    ///     SandboxPolicyResolver 对验收工作区解析，使读根/写根判定与生产语义一致。
    ///     A recording factory fake: it records every (policy kind, extra
    ///     environment) request and returns a scripted backend composition. The
    ///     policy is resolved with the real SandboxPolicyResolver against the
    ///     acceptance workspace so read-root/write-root verdicts match
    ///     production semantics.
    /// </summary>
    private sealed class RecordingBackendFactory(
        WindowsSandboxComponents                                              components,
        string                                                                workspaceRoot,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script)
    {
        public List<(SandboxPolicyKind Kind, IReadOnlyDictionary<string, string>? ExtraEnvironment)> Requests
        {
            get;
        } = [];

        public WindowsSandboxExecution Invoke(SandboxPolicyKind                    kind,
                                              IReadOnlyDictionary<string, string>? extraEnvironment)
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
    ///     fake verify-worker：按标志立即写 marker（或永不写）；Kill 仅记录。
    ///     A fake verify-worker: it writes the marker immediately (or never),
    ///     and Kill only records the call.
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

    /// <summary>
    ///     在一次性验收工作区里跑单个用例：每次调用使用全新工作区，避免上一个用例的残留文件
    ///     影响文件存在性判定。
    ///     Runs one case in a disposable acceptance workspace: every call gets a
    ///     fresh workspace so leftover files from a previous case never affect
    ///     the file-existence verdicts.
    /// </summary>
    private static async Task<WindowsSandboxAcceptanceReport> RunSingleCaseAsync(
        SandboxTestHome home, string caseName,
        Func<PreparedProcessExecution, CancellationToken, Task<ScriptedOutcome>> script,
        WindowsSandboxAcceptanceLimits? limits = null)
    {
        using var workspace = new TestTempDir();
        var runner = new WindowsSandboxAcceptanceRunner(
            new RecordingBackendFactory(home.Components, workspace.Root, script).Invoke,
            workspace.Root, (marker, _) => new FakeWorker(marker, true), limits : limits ?? TestLimits());
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
    ///     满足全矩阵的脚本：whoami 返回 offline 账户、probe 退出 0、curl 回环成功/非回环被拒、
    ///     cmd exit/type/copy 按目标语义应答。
    ///     The full-matrix happy script: whoami prints the offline account, the
    ///     probe exits 0, curl succeeds on loopback and is blocked otherwise,
    ///     and cmd exit/type/copy answer per target semantics.
    /// </summary>
    private static Task<ScriptedOutcome> HappyScriptAsync(PreparedProcessExecution plan, CancellationToken ct)
    {
        switch (Path.GetFileName(plan.Executable))
        {
            case "whoami.exe" :
                return Task.FromResult(new ScriptedOutcome(
                                           0,
                                           StdoutChunks :
                                           [$"test-machine\\{WindowsSandboxComponents.DefaultOfflineUsername}\n"]));
            case "codex-windows-managed-deny-probe.exe" :
                return Task.FromResult(new ScriptedOutcome(0));
            case "curl.exe" :
                return Task.FromResult(LastArgument(plan).Contains("127.0.0.1", StringComparison.Ordinal)
                                           ? new ScriptedOutcome(0)
                                           : new ScriptedOutcome(7));
            case "powershell.exe" :
                // The timeout case: the runner reports its timeout verdict.
                return Task.FromResult(new ScriptedOutcome(192, TimedOut : true));
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
            var chunks = new List<string>();
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
    ///     在 workspaceRoot 下复制一份 cmd.exe 并以 runner 镜像名启动，等它被验收运行器击杀后返回
    ///     afterKill——模拟“按镜像名定位本用例 runner 并外部击杀”。
    ///     Copies cmd.exe under workspaceRoot, starts it under the runner's
    ///     image name, waits for the acceptance runner to kill it, then returns
    ///     afterKill — simulating "locate this case's runner by image name and
    ///     kill it externally".
    /// </summary>
    private static async Task<ScriptedOutcome> SimulateExternalRunnerKillAsync(string       workspaceRoot,
                                                                               ScriptedOutcome afterKill)
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

    private static string LastArgument(PreparedProcessExecution plan)
    {
        return plan.Arguments.Count > 0 ? plan.Arguments[^1] : string.Empty;
    }

    private static string QuotedPath(string command)
    {
        // The acceptance fixtures embed bare (quote-free) paths; a quoted form
        // is still understood for hand-written commands.
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

    private static WindowsSandboxSettings SandboxSettings(SandboxTestHome home, bool enabled = true)
    {
        return new WindowsSandboxSettings
        {
            Enabled              = enabled,
            SetupExecutablePath  = home.Components.SetupExecutablePath,
            RunnerExecutablePath = home.Components.RunnerExecutablePath,
            SandboxHome          = home.Components.SandboxHome
        };
    }

    private static UserConfig SandboxConfig(SandboxTestHome home, bool enabled = true)
    {
        return new UserConfig { Settings = new UserConfigSettings { WindowsSandbox = SandboxSettings(home, enabled) } };
    }

    private sealed class CliFixture(IReadOnlyList<string>? input = null) : IDisposable
    {
        private readonly FakeCliConsole _io = new(input);

        public CliFixture(IReadOnlyList<string> input, bool interactive) : this(input)
        {
            _io.Interactive = interactive;
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"tinyharness-verify-{Guid.NewGuid():N}");

        public string UserConfigPath { get; } = Path.Combine(Path.GetTempPath(),
                                                             $"tinyharness-verify-config-{Guid.NewGuid():N}",
                                                             "user-config.json");

        public FakeCliConsole Io => _io;

        public CommandContext CreateContext()
        {
            return new CommandContext
            {
                Io               = _io,
                Credentials      = new FakeCredentialStore(),
                UserConfigPath   = UserConfigPath,
                WorkingDirectory = Root
            };
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);

            var configDirectory = Path.GetDirectoryName(UserConfigPath);
            if (Directory.Exists(configDirectory)) Directory.Delete(configDirectory, true);
        }
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

        public Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
        }

        public Task<string?> ReadHiddenLineAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_input.Count > 0 ? _input.Dequeue() : null);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool IsSupported => false;

        public void Save(string targetName, string secret)
        {
            _entries[targetName] = secret;
        }

        public string? Read(string targetName)
        {
            return _entries.GetValueOrDefault(targetName);
        }

        public bool Delete(string targetName)
        {
            return _entries.Remove(targetName);
        }
    }
}
