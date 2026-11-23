using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/* 临时验收文件（本轮新增，不提交）：WP5 复验轮的就绪门真机验收载体（经 IVT 使用 internal
   BaselineReadProtectionOverride 注入计划；其余组装为生产路径）。
   场景一（冷启动）：全新 deny 夹具 + 真实 helper（无 stub）+ 真实 runner 首命令端到端，记录首次
   准备总耗时；就绪门等待经沙箱诊断事件（Debug 构建）落证。
   场景二（fail-closed 反证）：计划读根包含组 ACE 无法到达的路径（fc-missing：不存在的路径——
   实测 helper 对不存在读根 refresh exit=0 容忍且授予无从到达；"ACL 拒绝 helper 写入"的构造被
   helper 的特权授予路径绕过，见 wp5/logs/probe-console-grant-latency.log 与 snapshot 观察）→
   就绪门超预算 fail-closed：命令不启动、错误含阶段与未就绪根、聊天可继续。
   安全边界：真实 helper 的 ACL 操作只落在 WP5 夹具树；真实 profile 零触碰。 */

[TestFixture]
public sealed class Wp5ReadinessGateAcceptanceTests
{
    private const string FixtureRootEnv = "TH_W5_FIXTURE_ROOT";
    private const string SandboxHomeEnv = "TH_W5_SANDBOX_HOME";
    private const string SetupExeEnv    = "TH_W5_SETUP_EXE";
    private const string RunnerExeEnv   = "TH_W5_RUNNER_EXE";
    private const string InstanceEnv    = "TH_W5_INSTANCE_ROOT";
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
            // 构造阶段只读门禁；仅精确 "1" 激活，单独设置 TH_W5_* 夹具参数不激活。
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
    public async Task Wp5ColdStart_FirstCommandThroughRealHelperWithReadinessGate()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox is Windows-only.");
        SandboxDiagnosticLog.Initialize();
        var sessionResult = await RunSessionAsync("coldstart", unreadyRoot: null).ConfigureAwait(false);
        WriteReport("coldstart", sessionResult);
        await Task.Delay(2000).ConfigureAwait(false); // 诊断异步刷盘后拷贝。
        CopyDiagnostics("coldstart");

        Assert.Multiple(() =>
        {
            Assert.That(sessionResult.Status, Is.EqualTo(AgentStatus.Completed));
            Assert.That(sessionResult.PreparationErrors, Has.Count.EqualTo(0),
                        "preparation must succeed with the real helper");
            Assert.That(sessionResult.Approvals, Has.Count.EqualTo(0), "read-only commands need no approval");
            // 正对照：读根内普通文件可读（真实沙箱账户）。
            Assert.That(sessionResult.ToolResults[0], Does.Contain("TinyHarnessTcpOff").IgnoreCase,
                        "whoami shows the offline sandbox account");
            Assert.That(sessionResult.ToolResults[1], Does.Contain("WP5-POSITIVE-NORMAL"),
                        "normal.txt (read root) is readable");
            // 反对照：固定敏感路径 OS 拒读。
            Assert.That(sessionResult.ToolResults[2], Does.Not.Contain("not-a-real-secret"), ".env is OS-denied");
            Assert.That(sessionResult.ToolResults[3], Does.Not.Contain("fake-sensitive-marker"), ".ssh is OS-denied");
        });
    }

    [Test]
    [AcceptanceOptIn]
    public async Task Wp5ReadinessGate_FailClosed_UnreadyRootNeverStartsCommand()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox is Windows-only.");
        SandboxDiagnosticLog.Initialize();
        var fixtureRoot = Path.GetFullPath(Require(FixtureRootEnv));
        var unreadyRoot = Path.Combine(fixtureRoot, "fc-missing");
        var sessionResult = await RunSessionAsync("gate-failclosed", unreadyRoot).ConfigureAwait(false);
        WriteReport("gate-failclosed", sessionResult);
        await Task.Delay(2000).ConfigureAwait(false);
        CopyDiagnostics("gate-failclosed");

        Assert.Multiple(() =>
        {
            // 就绪门在 backend preflight（ExecuteAsync 期）触发：失败以工具结果文本送达，
            // DeferredShellTool 初始化（compose 阶段）的 PreparationError 保持 null。
            Assert.That(sessionResult.PreparationErrors, Has.Count.EqualTo(1), "exactly one shell call was attempted");
            Assert.That(sessionResult.PreparationErrors[0], Is.EqualTo("<null>"),
                        "compose-stage preparation stays clean; the gate fires inside ExecuteAsync");
            Assert.That(sessionResult.ToolResults[0], Does.Contain("read-root readiness"),
                        "the tool result names the readiness stage");
            Assert.That(sessionResult.ToolResults[0], Does.Contain(unreadyRoot),
                        "the tool result lists the unready read root");
            Assert.That(sessionResult.ToolResults[0], Does.Contain("fail closed"),
                        "the tool result states the fail-closed decision");
            Assert.That(sessionResult.ToolResults[0], Does.Not.Contain("Exit code: 0"),
                        "no command output was produced (command never started)");
            Assert.That(sessionResult.Approvals, Has.Count.EqualTo(0));
            Assert.That(sessionResult.ChatContinued, Is.True, "chat remained usable after fail-closed");
        });
    }

    private sealed record SessionOutcome(AgentStatus Status, List<string> ToolResults, List<string> PreparationErrors,
                                         List<string> Approvals, List<long> StepMilliseconds, bool ChatContinued);

    /// <summary>打开会话并依序提交步骤；返回运行结果摘要供断言与报告使用。</summary>
    private static async Task<SessionOutcome> RunSessionAsync(string label, string? unreadyRoot)
    {
        var fixtureRoot  = Path.GetFullPath(Require(FixtureRootEnv));
        var sandboxHome  = Require(SandboxHomeEnv);
        var setupExe     = Require(SetupExeEnv);
        var runnerExe    = Require(RunnerExeEnv);
        var instanceRoot = Require(InstanceEnv);
        Assert.That(Directory.Exists(fixtureRoot), Is.True, "wp5 fixture root must exist");

        var workspace = Directory.CreateDirectory(Path.Combine(instanceRoot, "workspace-" + label)).FullName;
        var stateDir  = Directory.CreateDirectory(Path.Combine(instanceRoot, "state-" + label)).FullName;
        var storeDir  = Path.Combine(instanceRoot, "store-" + label);
        Directory.CreateDirectory(storeDir);
        var userPath = Path.Combine(stateDir, "user.json");
        await UserConfigStore.SaveAsync(userPath, new UserConfig
        {
            DefaultProfile = "offline",
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "offline",
                    Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                }
            ],
            Settings = new UserConfigSettings
            {
                SessionStoreDirectory = storeDir,
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true,
                    AllowTcpLoopback     = true,
                    SandboxHome          = sandboxHome,
                    SetupExecutablePath  = setupExe,
                    RunnerExecutablePath = runnerExe
                }
            }
        }, default);

        // 计划：生产顶层规划同形态（normal.txt 与 keys 为读根，固定敏感段 + 顶层 .env 精确 deny）；
        // fail-closed 场景追加组 ACE 无法到达的读根。
        var readRoots = new List<string>
        {
            Path.Combine(fixtureRoot, "normal.txt"),
            Path.Combine(fixtureRoot, "keys")
        };
        if (unreadyRoot is not null) readRoots.Add(unreadyRoot);
        var plan = new SandboxBaselineReadProtection(readRoots,
        [
            Path.Combine(fixtureRoot, ".ssh"),    Path.Combine(fixtureRoot, ".aws"),
            Path.Combine(fixtureRoot, ".docker"), Path.Combine(fixtureRoot, ".gnupg"),
            Path.Combine(fixtureRoot, ".kube"),   Path.Combine(fixtureRoot, ".codex"),
            Path.Combine(fixtureRoot, ".env")
        ]);

        var steps = unreadyRoot is null
            ? new[] { "whoami",
                      $"type {fixtureRoot}\\normal.txt",
                      $"type {fixtureRoot}\\.env",
                      $"dir {fixtureRoot}\\.ssh" }
            : new[] { "whoami" };

        var factory = new DesktopSessionFactory((_, _) => new StepModel(steps),
                                                new FixtureCredentials(), userPath)
        {
            BaselineReadProtectionOverride = _ => plan
        };
        var approvalsSeen = new List<string>();
        var stepTimes     = new List<long>();
        var session = await factory.OpenAsync(
            new DesktopSessionRequest(workspace)
            {
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => ValueTask.CompletedTask,
            approval =>
            {
                if (approval is not null)
                    approvalsSeen.Add($"id={approval.Id} tool={approval.ToolName} level={approval.Level}");
                return ValueTask.CompletedTask;
            }, default).ConfigureAwait(false);

        AgentResult?    lastRun   = null;
        var             chatContinued = false;
        await using (session)
        {
            foreach (var _ in steps)
            {
                var stopwatch = Stopwatch.StartNew();
                var run = await session.SubmitAsync("run the fixture step").WaitAsync(TimeSpan.FromSeconds(180))
                                       .ConfigureAwait(false);
                stopwatch.Stop();
                stepTimes.Add(stopwatch.ElapsedMilliseconds);
                lastRun = run.Result;
            }

            if (unreadyRoot is not null)
            {
                // 聊天可继续：fail-closed 后追加纯聊天提交。
                var chat = await session.SubmitAsync("plain chat afterwards").WaitAsync(TimeSpan.FromSeconds(60))
                                        .ConfigureAwait(false);
                chatContinued = chat.Result!.Status == AgentStatus.Completed && chat.Result.ToolExecutions == 0;
            }
        }

        Assert.That(approvalsSeen, Has.Count.EqualTo(0), "no approvals requested in either scenario");
        var persisted   = await SessionStoreReader.ReadAsync(storeDir, session.Id, default).ConfigureAwait(false);
        var toolResults = persisted?.Conversation?.Messages.Where(m => m.Role == "Tool").Select(m => m.Content)
                                   .ToList() ?? [];
        var shellCalls  = lastRun?.ToolCalls?.Where(c => c.ToolName == "shell").ToList() ?? [];
        return new SessionOutcome(lastRun?.Status ?? AgentStatus.Failed, toolResults,
                                  shellCalls.Select(c => c.PreparationError ?? "<null>").ToList(),
                                  approvalsSeen, stepTimes, chatContinued);
    }

    private static void CopyDiagnostics(string label)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "tinyharness", "logs");
            foreach (var file in Directory.GetFiles(dir, $"sandbox-diagnostic-*-p{Environment.ProcessId}.log"))
            {
                var target = Path.Combine(Require(EvidenceEnv), $"wp5-{label}-{Path.GetFileName(file)}");
                File.Copy(file, target, overwrite: true);
                TestContext.Out.WriteLine($"diagnostics copied: {target}");
            }
        }
        catch (Exception ex)
        {
            TestContext.Out.WriteLine($"diagnostics copy failed: {ex.Message}");
        }
    }

    private static void WriteReport(string label, SessionOutcome outcome)
    {
        var report = new StringBuilder();
        report.Append("label=").AppendLine(label);
        report.Append("timestampUtc=").AppendLine(DateTime.UtcNow.ToString("o"));
        report.Append("buildFlavor=").AppendLine(
#if DEBUG
            "Debug"
#else
            "Release"
#endif
        );
        report.Append("runStatus=").AppendLine(outcome.Status.ToString());
        report.Append("chatContinued=").AppendLine(outcome.ChatContinued.ToString());
        for (var i = 0; i < outcome.StepMilliseconds.Count; i++)
            report.Append("step.").Append(i).Append(" wallMs=").AppendLine(outcome.StepMilliseconds[i].ToString());
        for (var i = 0; i < outcome.ToolResults.Count; i++)
        {
            report.Append("result.").Append(i).AppendLine("=");
            report.AppendLine(outcome.ToolResults[i].ReplaceLineEndings(" | "));
        }
        for (var i = 0; i < outcome.PreparationErrors.Count; i++)
            report.Append("preparationError.").Append(i).AppendLine($"={outcome.PreparationErrors[i]}");
        File.WriteAllText(Path.Combine(Require(EvidenceEnv), $"wp5-{label}-report.txt"), report.ToString(),
                          new UTF8Encoding(false));
        TestContext.Out.Write(report.ToString());
    }

    /// <summary>脚本模型：首个请求按序注入全部 shell 调用；其后请求返回普通完成。</summary>
    private sealed class StepModel(IReadOnlyList<string> shellArguments) : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Interlocked.Increment(ref _requests) == 1)
            {
                for (var index = 0; index < shellArguments.Count; index++)
                    yield return new ChatStreamEvent
                    {
                        Kind                 = ChatStreamEventKind.ToolCallDelta,
                        ToolCallIndex        = index,
                        ToolCallId           = $"wp5-call-{index}",
                        ToolCallFunctionName = "shell",
                        ToolCallArgumentsDelta =
                            "{\"mode\":\"direct\",\"executable\":\"cmd.exe\",\"arguments\":[\"/c\"," +
                            System.Text.Json.JsonSerializer.Serialize(shellArguments[index]) + "]}"
                    };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
                yield break;
            }

            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }

    private sealed class FixtureCredentials : ICredentialStore
    {
        public bool    IsSupported                               => true;
        public string? Read(string   targetName)                 => "offline-key";
        public void    Save(string   targetName, string secret)  => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }
}
