using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/* 临时验收文件（本轮新增，不提交）：WP5 复验轮"反方向"收口验收载体。经 InternalsVisibleTo 调用
   真实共享助手 SandboxDenyReadBookkeeping.Merge 构造 CLI 式端 payload（Desktop 专属条目在簿记），
   以真实 helper refresh 后验证 Desktop 专属条目不被撤销（ACE 层 + 沙箱读探针）——证明共享并集
   实现覆盖两端；修复前对照见 wp3b/logs/probe-console-alternation.log R5（desktop-store 被撤销）。
   安全边界：真实 helper 的 ACL 操作只落在 WP5 夹具树；真实 profile 零触碰。 */

[TestFixture]
public sealed class Wp5BookkeepingUnionAcceptanceTests
{
    private const string FixtureRootEnv = "TH_W5_FIXTURE_ROOT";
    private const string SandboxHomeEnv = "TH_W5_SANDBOX_HOME";
    private const string SetupExeEnv    = "TH_W5_SETUP_EXE";
    private const string RunnerExeEnv   = "TH_W5_RUNNER_EXE";
    private const string EvidenceEnv    = "TH_W5_EVIDENCE_DIR";
    private const string GateEnv        = "TH_W5_ACCEPTANCE";

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.That(value, Is.Not.Null.And.Not.Empty, $"missing env {name}");
        return value!;
    }

    private sealed class AcceptanceOptInAttribute : NUnitAttribute, IApplyToTest
    {
        private readonly string? _skipReason;

        public AcceptanceOptInAttribute()
        {
            var gate = Environment.GetEnvironmentVariable(GateEnv);
            if (!string.Equals(gate, "1", StringComparison.Ordinal))
                _skipReason = "默认关闭的 WP5 真机复验：设置环境变量 TH_W5_ACCEPTANCE=1 显式启用。";
        }

        public void ApplyToTest(Test test)
        {
            if (_skipReason is null || test.RunState == RunState.NotRunnable) return;
            test.RunState = RunState.Ignored;
            test.Properties.Set(PropertyNames.SkipReason, _skipReason);
        }
    }

    [Test]
    [AcceptanceOptIn]
    public async Task Wp5ReverseDirection_RealMergeUnionKeepsDesktopEntriesAcrossCliStyleRefresh()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox is Windows-only.");
        var fixtureRoot = Path.GetFullPath(Require(FixtureRootEnv));
        var sandboxHome = Require(SandboxHomeEnv);
        var evidenceDir = Require(EvidenceEnv);
        var altNormal   = Path.Combine(fixtureRoot, "alt", "normal");
        var altWide     = Path.Combine(fixtureRoot, "alt", "wide");
        var altCliA     = Path.Combine(fixtureRoot, "alt", "cli-a");
        var altCliB     = Path.Combine(fixtureRoot, "alt", "cli-b");
        var altCliC     = Path.Combine(fixtureRoot, "alt", "cli-c");
        var altStore    = Path.Combine(fixtureRoot, "alt", "desktop-store");
        var workspace   = Path.Combine(fixtureRoot, "alt-workspace");
        var tempRoot    = Path.Combine(fixtureRoot, "alt-temp");
        var stateFile   = Path.Combine(sandboxHome, ".sandbox", "deny_read_acl_state.json");

        var components = new WindowsSandboxComponents
        {
            AllowTcpLoopback     = true,
            SetupExecutablePath  = Require(SetupExeEnv),
            RunnerExecutablePath = Require(RunnerExeEnv),
            SandboxHome          = sandboxHome
        };
        var readRoots = new[] { altNormal, altWide };
        AssertEphemeralFixture(new[] { altNormal, altWide, altCliA, altCliB, altCliC, altStore, workspace, tempRoot, sandboxHome },
                               fixtureRoot);

        // 种子：真实 helper refresh 只带 Desktop 专属条目 → 簿记含 desktop-store（模拟另一端已写入）。
        await RefreshAsync(components, Payload(components, workspace, readRoots, [altStore]), "seed desktop-only deny")
             .ConfigureAwait(false);
        await WaitForDenyAsync(altStore, expectedPresent: true, "seed desktop-store applied").ConfigureAwait(false);

        // CLI 式端：语义集只有自身扫描命中；真实共享助手并集后必须并入簿记里的 Desktop 专属条目。
        var semantic  = new[] { altCliA, altCliB, altCliC };
        var (merged, entries, mergedCount) =
            SandboxDenyReadBookkeeping.Merge("wp5 reverse-direction", semantic, readRoots, sandboxHome);
        TestContext.Out.WriteLine($"merge: bookkeepingEntries={entries} bookkeepingMerged={mergedCount} " +
                                  $"merged=[{string.Join(';', merged)}]");

        var report = new StringBuilder();
        report.Append("timestampUtc=").AppendLine(DateTime.UtcNow.ToString("o"));
        report.Append("bookkeepingEntries=").AppendLine(entries.ToString());
        report.Append("bookkeepingMerged=").AppendLine(mergedCount.ToString());
        report.Append("mergedPayload=").AppendLine(string.Join(";", merged));
        report.Append("bookkeeping=").AppendLine(
            File.Exists(stateFile) ? File.ReadAllText(stateFile).ReplaceLineEndings(" | ") : "<missing>");

        Assert.Multiple(() =>
        {
            Assert.That(entries, Is.GreaterThanOrEqualTo(1), "the bookkeeping carries the desktop-only entry");
            Assert.That(merged.Any(p => string.Equals(p, altStore, StringComparison.OrdinalIgnoreCase)), Is.True,
                        "the real shared Merge folds the desktop-only entry into the CLI-style payload");
        });

        // 用合并后的 payload 经真实 helper refresh：Desktop 专属条目不被撤销（ACE 层）。
        await RefreshAsync(components, Payload(components, workspace, readRoots, merged), "refresh with real-merge payload")
             .ConfigureAwait(false);
        await WaitForDenyAsync(altStore, expectedPresent: true, "desktop-store kept after CLI-style refresh").ConfigureAwait(false);
        await WaitForDenyAsync(altCliA, expectedPresent: true, "cli-a applied").ConfigureAwait(false);
        await WaitForDenyAsync(altCliB, expectedPresent: true, "cli-b applied").ConfigureAwait(false);
        report.Append("storeAclAfter=").AppendLine(await IcaclsOneLineAsync(altStore).ConfigureAwait(false));

        // 沙箱读探针：desktop-store 在合并 payload 的协调端读边界内不可达（exit 1）。
        var probeResult = await ProbeSandboxReadAsync(components, workspace, tempRoot, readRoots, merged,
                                                      Path.Combine(altStore, "d.txt")).ConfigureAwait(false);
        report.Append("probeExitCode=").AppendLine(probeResult.ExitCode?.ToString() ?? "<null>");
        report.Append("probeStdout=").AppendLine((probeResult.Stdout ?? "").ReplaceLineEndings(" | "));
        File.WriteAllText(Path.Combine(evidenceDir, "wp5-union-reverse-report.txt"), report.ToString(),
                          new UTF8Encoding(false));
        TestContext.Out.Write(report.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(probeResult.ExitCode, Is.Not.EqualTo(0), "desktop-store stays OS-denied (not revoked)");
            Assert.That(probeResult.Stdout, Does.Not.Contain("ALT-STORE"), "the denied file content never leaks");
        });
    }

    private static void AssertEphemeralFixture(IReadOnlyList<string> paths, string fixtureRoot)
    {
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            Assert.That(full.StartsWith(fixtureRoot, StringComparison.OrdinalIgnoreCase), Is.True,
                        $"path escapes the wp5 fixture: {full}");
            Assert.That(full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                        StringComparison.OrdinalIgnoreCase), Is.False,
                        $"path touches the real profile: {full}");
        }
    }

    private static SandboxSetupPayload Payload(WindowsSandboxComponents components, string workspace,
                                               IReadOnlyList<string> readRoots, IReadOnlyList<string> deny) => new()
    {
        AllowTcpLoopback        = true,
        OfflineUsername         = components.OfflineUsername,
        OnlineUsername          = components.OnlineUsername,
        SandboxHome             = components.SandboxHome,
        CommandWorkingDirectory = workspace,
        ReadRoots               = readRoots,
        WriteRoots              = [],
        DenyReadPaths           = deny,
        DenyWritePaths          = [],
        RealUser                = Environment.UserName,
        RefreshOnly             = true
    };

    private static async Task RefreshAsync(WindowsSandboxComponents components, SandboxSetupPayload payload, string label)
    {
        await new ProcessSandboxSetupInvoker(components).RefreshAsync(payload, CancellationToken.None)
                                                        .ConfigureAwait(false);
        // deny 的下发/撤销由后台 read-ACL helper 异步落地，随后轮询前先等一拍。
        await Task.Delay(2500).ConfigureAwait(false);
        TestContext.Out.WriteLine($"refresh {label}: OK deny=[{string.Join(';', payload.DenyReadPaths)}]");
    }

    private static async Task<(int? ExitCode, string? Stdout)> ProbeSandboxReadAsync(
        WindowsSandboxComponents components, string workspace, string tempRoot,
        IReadOnlyList<string> readRoots, IReadOnlyList<string> deny, string target)
    {
        var policy  = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, workspace,
                                                    SandboxTargetEnvironment.Build(tempRoot),
                                                    additionalWriteRoots: [], allowTcpLoopback: true,
                                                    sandboxHome: components.SandboxHome,
                                                    baselineReadRoots: readRoots,
                                                    baselineDenyReadPaths: deny);
        var backend = new WindowsSandboxBackend(components, policy, SandboxTargetEnvironment.Build(tempRoot));
        var stdout  = new StringBuilderCapture();
        var stderr  = new StringBuilderCapture();
        var execution = new PreparedProcessExecution("cmd.exe", ["/c", $"type {target}"], workspace, 60, false, null);
        var result = await backend.ExecuteAsync(execution, stdout, stderr, CancellationToken.None).ConfigureAwait(false);
        return (result.ExitCode, stdout.Complete().Content);
    }

    private static async Task WaitForDenyAsync(string path, bool expectedPresent, string label)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var acl      = await IcaclsOneLineAsync(path).ConfigureAwait(false);
            var presents = acl.Contains("(DENY)", StringComparison.OrdinalIgnoreCase);
            if (presents == expectedPresent)
            {
                TestContext.Out.WriteLine($"deny-state {label}: confirmed (present={presents})");
                return;
            }
            await Task.Delay(1000).ConfigureAwait(false);
        }
        Assert.Fail($"{label}: deny ACE did not reach the expected state within 90s");
    }

    private static async Task<string> IcaclsOneLineAsync(string path)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("cmd.exe", $"/c icacls \"{path}\"")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        return $"exit={process.ExitCode} {stdout.ToString().Trim().ReplaceLineEndings(" | ")}";
    }

    private sealed class StringBuilderCapture : IProcessOutputCapture
    {
        private readonly StringBuilder _builder = new();
        public ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken)
        {
            _builder.Append(chunk.Span);
            return ValueTask.CompletedTask;
        }
        public CapturedProcessOutput Complete() => new(_builder.ToString(), false, _builder.Length, null);
        public void Discard() => _builder.Clear();
    }
}
