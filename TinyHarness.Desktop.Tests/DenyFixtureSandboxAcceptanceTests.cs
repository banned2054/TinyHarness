using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/* 临时验收文件（本轮新增，不提交）：DesktopSessionFactory 生产组装路径 + 既有测试接缝
   BaselineReadProtectionOverride（DesktopSessionFactory.Sandbox.cs:14，internal，经
   InternalsVisibleTo 进入）注入"对隔离 deny 夹具根用生产顶层规划代码得到的计划"。
   TinyHarness.Core 的 InternalsVisibleTo 只授予 TinyHarness.Tests，因此计划由载体
   （TinyHarness.Tests 临时测试，PlanBaselineReadProtectionTopLevelCore）真实规划夹具根后
   经 JSON 文件移交，本测试只做公开 record 重建（ReadRoots/DenyReadPaths 逐位置还原）。
   其余组装（DeferredShellTool 初始化回调 → WindowsSandboxComposer.Compose →
   ProcessSandboxSetupInvoker refresh → runner 经 CreateProcessWithLogonW 启动 → IPC
   spawn_request）全部走生产路径。
   这不是 Desktop 生产入口成功路径：生产入口规划根恒为 UserProfile（待验收）。 */

/// <summary>
///     <para>
///         本机入口级验收（2026-11-21 第五轮）：真实 Windows runner/helper 对 deny 夹具的
///         可读/拒读对照。注入计划来自生产顶层规划代码对夹具根的真实规划（顶层读根 +
///         精确拒读）；命令全部为无害只读（whoami/type/dir），敏感夹具内容均为假标记。
///     </para>
///     <para>
///         Local entry-level acceptance (round 5, 2026-11-21): readable/denied comparison on the
///         deny fixture through the real Windows runner/helper. The injected plan comes from the
///         production top-level planner over the fixture root (top-level read roots plus precise
///         denies); every command is harmless and read-only (whoami/type/dir), and all sensitive
///         fixture contents are fake markers.
///     </para>
/// </summary>
[TestFixture]
public sealed class DenyFixtureSandboxAcceptanceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(420);

    private const string FixtureRootEnv    = "TH_R5_DENY_FIXTURE_ROOT";
    private const string SandboxHomeEnv    = "TH_R5_SANDBOX_HOME";
    private const string SetupExeEnv       = "TH_R5_SETUP_EXE";
    private const string RunnerExeEnv      = "TH_R5_RUNNER_EXE";
    private const string InstanceRootEnv   = "TH_R5_INSTANCE_ROOT";
    private const string EvidenceDirEnv    = "TH_R5_EVIDENCE_DIR";
    private const string SessionArchiveEnv = "TH_R5_SESSION_ARCHIVE_DIR";
    private const string PlanPathEnv       = "TH_R5_DENY_PLAN_PATH";

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.That(value, Is.Not.Null.And.Not.Empty, $"missing env {name}");
        return value!;
    }

    private const string AcceptanceGateEnv = "TH_R5_ACCEPTANCE";

    /// <summary>
    ///     <para>
    ///         显式 opt-in 门禁：环境变量 TH_R5_ACCEPTANCE 精确等于 "1" 时用例才可运行；
    ///         未启用时在测试图构建阶段（先于夹具参数读取与任何副作用）标记为 Ignored。
    ///     </para>
    ///     <para>
    ///         Explicit opt-in gate: a case runs only when the environment variable
    ///         TH_R5_ACCEPTANCE equals exactly "1"; otherwise it is marked ignored at test-graph
    ///         build time, before any fixture configuration is read and before any side effect.
    ///     </para>
    /// </summary>
    private sealed class AcceptanceOptInAttribute : NUnitAttribute, IApplyToTest
    {
        private readonly string? _skipReason;

        public AcceptanceOptInAttribute()
        {
            // 构造阶段只读门禁；仅精确 "1" 激活，单独设置 TH_R5_* 夹具参数不激活。
            var gate = Environment.GetEnvironmentVariable(AcceptanceGateEnv);
            if (!string.Equals(gate, "1", StringComparison.Ordinal))
                _skipReason = "默认关闭的真实沙箱拒读验收：设置环境变量 TH_R5_ACCEPTANCE=1 显式启用。";
        }

        public void ApplyToTest(Test test)
        {
            // 与 NUnit IgnoreAttribute 一致：NotRunnable 优先级更高，不覆盖既有状态。
            if (_skipReason is null || test.RunState == RunState.NotRunnable) return;
            test.RunState = RunState.Ignored;
            test.Properties.Set(PropertyNames.SkipReason, _skipReason);
        }
    }

    [Test]
    [AcceptanceOptIn]
    public async Task DenyFixtureCommandMatrix_NoProject_ThroughRealRunner()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox is Windows-only.");
        await RunMatrixAsync(withProject: false, label: "noproj");
    }

    [Test]
    [AcceptanceOptIn]
    public async Task DenyFixtureCommandMatrix_Project_ThroughRealRunner()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("The Windows sandbox is Windows-only.");
        await RunMatrixAsync(withProject: true, label: "proj");
    }

    private async Task RunMatrixAsync(bool withProject, string label)
    {
        var fixtureRoot  = Path.GetFullPath(Require(FixtureRootEnv));
        var sandboxHome  = Require(SandboxHomeEnv);
        var setupExe     = Require(SetupExeEnv);
        var runnerExe    = Require(RunnerExeEnv);
        var instanceRoot = Require(InstanceRootEnv);
        var evidenceDir  = Require(EvidenceDirEnv);
        var archiveDir   = Require(SessionArchiveEnv);
        Assert.That(Directory.Exists(fixtureRoot), Is.True, "deny fixture root must exist");

        // 每组独立实例目录：workspace-*、state-*、store-* 均为本轮新建（归属见 RUN-MANIFEST）。
        var workspace   = Directory.CreateDirectory(Path.Combine(instanceRoot, "workspace-" + label)).FullName;
        var stateDir    = Directory.CreateDirectory(Path.Combine(instanceRoot, "state-" + label)).FullName;
        var storeDir    = Path.Combine(instanceRoot, "store-" + label);
        var userPath    = Path.Combine(stateDir, "user.json");
        var sandboxSettings = new WindowsSandboxSettings
        {
            Enabled              = true,
            AllowTcpLoopback     = true,
            SandboxHome          = sandboxHome,
            SetupExecutablePath  = setupExe,
            RunnerExecutablePath = runnerExe
        };
        Directory.CreateDirectory(storeDir);
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
                WindowsSandbox = sandboxSettings
            }
        }, default);

        var matrix = Commands(fixtureRoot);
        // 计划来自载体（TinyHarness.Tests，PlanBaselineReadProtectionTopLevelCore 对夹具根的
        // 真实规划）经 JSON 移交；此处只按公开 record 逐位置还原。
        var planPath = Require(PlanPathEnv);
        var planDocument =
            System.Text.Json.JsonSerializer.Deserialize<PlanFile>(File.ReadAllText(planPath),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("the handed-over plan file is empty");
        var plan = new SandboxBaselineReadProtection(planDocument.ReadRoots, planDocument.DenyReadPaths);

        var factory = new DesktopSessionFactory((_, _) => new MatrixModel(matrix, ApprovalProbeArguments,
                                                                      HostWhoamiArguments),
                                                new FixtureCredentials(), userPath)
        {
            BaselineReadProtectionOverride = token => plan
        };

        var approvalsSeen = new List<string>();
        DesktopSession? session = null;
        session = await factory.OpenAsync(
            new DesktopSessionRequest(workspace)
            {
                ProjectAssignmentState = withProject
                    ? SessionProjectAssignmentState.Project
                    : SessionProjectAssignmentState.NoProject
            },
            _ => ValueTask.CompletedTask,
            approval =>
            {
                if (approval is not null)
                {
                    var scope = approval.AllowedScopes.Count > 0 ? approval.AllowedScopes[0] : GrantScope.SingleOperation;
                    approvalsSeen.Add($"id={approval.Id} tool={approval.ToolName} summary={approval.Summary} " +
                                      $"policy={approval.ExecutionPolicy} level={approval.Level} scopeGranted={scope}");
                    session!.Approvals.Respond(approval.Id, ApprovalResolution.Granted(scope));
                }

                return ValueTask.CompletedTask;
            }, default);
        await using (session)
        {
            Assert.That(session.Host.Session.ProjectAssignmentState,
                        Is.EqualTo(withProject ? SessionProjectAssignmentState.Project
                                               : SessionProjectAssignmentState.NoProject));
            if (withProject) Assert.That(session.Host.Session.ProjectId, Is.Not.Null, "project id must bind");

            var run = await session.SubmitAsync("run the deny fixture command matrix").WaitAsync(Limit);

            // 会话持久化证据归档（每组新会话；归属运行目录 logs）。
            foreach (var file in Directory.GetFiles(storeDir, "*.session.jsonl"))
                File.Copy(file, Path.Combine(archiveDir, Path.GetFileName(file)), overwrite: true);

            var persisted = await SessionStoreReader.ReadAsync(storeDir, session.Id, default);
            // 工具结果按会话顺序取全部 role=Tool 消息：网络探针的启动失败文本不含
            // "Exit code:"（Failed 形态），按内容过滤会错位；结果序与注入序一致（顺序执行）。
            var toolResults = persisted?.Conversation?.Messages
                                         .Where(message => message.Role == "Tool")
                                        .Select(message => message.Content)
                                        .ToList() ?? [];

            var report = new StringBuilder();
            void Line(string key, object value) => report.Append(key).Append('=').AppendLine(value.ToString());
            Line("label", label);
            Line("withProject", withProject);
            Line("projectId", session.Host.Session.ProjectId ?? "<none>");
            Line("timestampUtc", DateTime.UtcNow.ToString("o"));
            Line("buildFlavor",
#if DEBUG
                 "Debug"
#else
                 "Release"
#endif
                );
            Line("framework", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Line("fixtureRoot", fixtureRoot);
            Line("sandboxHome", sandboxHome);
            Line("setupExe", setupExe);
            Line("runnerExe", runnerExe);
            Line("workspace", workspace);
            Line("store", storeDir);
            Line("runStatus", run.Result?.Status.ToString() ?? "<null>");
            Line("toolExecutions", run.Result?.ToolExecutions ?? -1);
            Line("toolCallCount", run.Result?.ToolCalls?.Count ?? 0);
            Line("approvalCount", approvalsSeen.Count);
            foreach (var approval in approvalsSeen) Line("approval", approval);
            Line("planSource", planPath);
            Line("planReadRoots", string.Join(";", plan.ReadRoots));
            Line("planDenyCount", plan.DenyReadPaths.Count);
            foreach (var deny in plan.DenyReadPaths) Line("planDenyPath", deny);
            foreach (var call in run.Result?.ToolCalls ?? []) Line("toolCall", $"callId={call.CallId} tool={call.ToolName} state={call.State} approval={call.Approval ?? "<null>"} preparationError={call.PreparationError ?? "<null>"}");
            for (var index = 0; index <= matrix.Count + 1; index++)
            {
                string command;
                if (index < matrix.Count) command = matrix[index];
                else if (index == matrix.Count) command = "network-probe: cmd /c ver (networkAccess=enabled; loopback 模式预期应用层拦截)";
                else command = "host-probe: cmd /c whoami (executionScope=host; Level 3 人工审批后宿主执行)";
                var content = index < toolResults.Count ? toolResults[index] : "<missing result>";
                Line("command." + index, command);
                Line("result." + index, content.ReplaceLineEndings(" | "));
            }

            var missingResult = Result(toolResults, 6);
            Line("missingLooksLikeNotFound",
                 missingResult.Contains("cannot find", StringComparison.OrdinalIgnoreCase) ||
                 missingResult.Contains("找不到", StringComparison.Ordinal));
            Line("hostProbeWhoamiNotSandboxAccount",
                 !Result(toolResults, 9).Contains("TinyHarnessTcpOff", StringComparison.OrdinalIgnoreCase));

            File.WriteAllText(Path.Combine(evidenceDir, "r5-deny-matrix-" + label + ".txt"), report.ToString(),
                              new UTF8Encoding(false));
            TestContext.Out.Write(report.ToString());

            // 判定断言：组合成功、真实执行 10 条、真实沙箱账户、正对照可读、敏感夹具拒读。
            Assert.Multiple(() =>
            {
                Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
                Assert.That(run.Result.ToolCalls!.Count, Is.EqualTo(matrix.Count + 2), "every injected call is processed");
                Assert.That(run.Result.ToolExecutions, Is.EqualTo(matrix.Count + 2), "every call executed");
                Assert.That(run.Result.ToolCalls!.All(call => call.PreparationError is null), Is.True,
                            "composition and preparation must succeed");
                // 只读命令由权限引擎 Evaluate 自动放行（approval=allow，AgentLoop.Permissions.cs
                // Allow 分支）；两个人工审批探针（网络申请、host scope）真实触发回调。
                Assert.That(approvalsSeen, Has.Count.EqualTo(2), "both probes must reach the real callback");
                Assert.That(approvalsSeen.Any(text => text.Contains("windows-sandbox", StringComparison.Ordinal)),
                            Is.True, "the network-approval must carry the windows-sandbox execution policy");
                Assert.That(run.Result.ToolCalls!.All(call => call.Approval is not null &&
                                                             (call.Approval.StartsWith("allow", StringComparison.Ordinal) ||
                                                              call.Approval.Contains("windows-sandbox", StringComparison.Ordinal))),
                            Is.True, "every call carries its real authorization outcome");
                // whoami 证明真实沙箱进程（TCP 环回离线账户）。
                Assert.That(Result(toolResults, 0), Does.Contain("TinyHarnessTcpOff").IgnoreCase,
                            "whoami inside the sandbox must show the offline sandbox account");
                // 正对照：OS 读根内普通文件可读（含普通读根内任意深度的 server.pem——首版 OS
                // 语义收敛后，嵌套敏感名不再由启动规划发现，由内置文件工具的应用层分类兜底）。
                Assert.That(Result(toolResults, 1), Does.Contain("positive control"), "normal.txt must be readable");
                Assert.That(Result(toolResults, 2), Does.Contain("positive control"), "keys\\readme.txt must be readable");
                Assert.That(Result(toolResults, 4), Does.Contain("not-a-real-key"),
                            "keys is a read root; the nested server.pem is not OS-denied by the top-level planning");
                // OS 拒读：顶层敏感命中与整目录拒读覆盖内绝不可达（文件内容不出现即 OS 拒读生效）。
                Assert.That(Result(toolResults, 3), Does.Not.Contain("not-a-real-secret"), ".env must be OS-denied");
                Assert.That(Result(toolResults, 5), Does.Not.Contain("FAKE-tinyharness"), "credential.txt must be OS-denied");
                // 缺失对照：missing-file.txt 不得泄露正对照内容（区分"找不到"与"拒读"依据记录的
                // 原文判定，中英文/码页差异不进入硬断言）。
                Assert.That(missingResult, Does.Not.Contain("positive control"), "missing-file.txt must not leak");
                // dir 敏感目录：列表不可得（credential.txt 文件名不得出现）。
                Assert.That(Result(toolResults, 7), Does.Not.Contain("credential.txt"), "dir .aws must be OS-denied");
                // 网络探针：TCP 环回模式下逐次在线授权为应用层互斥拦截（区别于 OS 拒读/文件缺失）。
                Assert.That(Result(toolResults, 8), Does.Contain("mutually exclusive"),
                            "the loopback mode must reject per-command online grants at the application layer");
                // host 探针：人工审批放行后于宿主执行，身份不是沙箱账户（真实回调 + 真实执行的对照）。
                Assert.That(Result(toolResults, 9), Does.Contain("Exit code: 0"), "the approved host probe ran");
                Assert.That(Result(toolResults, 9), Does.Not.Contain("TinyHarnessTcpOff").IgnoreCase,
                            "host whoami must not show the sandbox account");
            });
        }
    }

    private static string Result(List<string> results, int index) =>
        index < results.Count ? results[index] : "<missing result>";

    /// <summary>载体移交的计划文件形态（ReadRoots/DenyReadPaths 逐位置还原）。</summary>
    private sealed record PlanFile(
        IReadOnlyList<string> ReadRoots,
        IReadOnlyList<string> DenyReadPaths);

    private static IReadOnlyList<string> Commands(string fixtureRoot)
    {
        // 沙箱内无害只读命令（绝对路径指向夹具标识；不写任何路径）。第 9 条为审批路径探针：
        // networkAccess=enabled 使权限引擎发出真实审批请求（whoami/type/dir 的 Level-1 只读
        // 命令按 Evaluate 自动放行，不进入人工回调——见 AgentLoop.Permissions.cs 的 Allow 分支）；
        // ver 命令本身不发起任何外联，仅携带网络申请标志。
        return
        [
            "whoami",
            $"type {fixtureRoot}\\normal.txt",
            $"type {fixtureRoot}\\keys\\readme.txt",
            $"type {fixtureRoot}\\.env",
            $"type {fixtureRoot}\\keys\\server.pem",
            $"type {fixtureRoot}\\.aws\\credential.txt",
            $"type {fixtureRoot}\\missing-file.txt",
            $"dir {fixtureRoot}\\.aws"
        ];
    }

    private const string ApprovalProbeArguments =
        """{"mode":"direct","executable":"cmd.exe","arguments":["/c","ver"],"networkAccess":"enabled"}""";

    private const string HostWhoamiArguments =
        """{"mode":"direct","executable":"cmd.exe","arguments":["/c","whoami"],"executionScope":"host"}""";

    /// <summary>脚本模型：首个请求按序注入全部 shell 工具调用（末尾附两个审批探针）；此后返回普通完成。</summary>
    private sealed class MatrixModel(IReadOnlyList<string> shellArguments, string networkProbeArguments,
                                     string hostProbeArguments)
        : IChatCompletionClient
    {
        private int _requests;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
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
                        ToolCallId           = "deny-fixture-" + index,
                        ToolCallFunctionName = "shell",
                        ToolCallArgumentsDelta =
                            "{\"mode\":\"direct\",\"executable\":\"cmd.exe\",\"arguments\":[\"/c\"," +
                            System.Text.Json.JsonSerializer.Serialize(shellArguments[index]) + "]}"
                    };
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta,
                    ToolCallIndex          = shellArguments.Count,
                    ToolCallId             = "deny-fixture-probe",
                    ToolCallFunctionName   = "shell",
                    ToolCallArgumentsDelta = networkProbeArguments
                };
                yield return new ChatStreamEvent
                {
                    Kind                   = ChatStreamEventKind.ToolCallDelta,
                    ToolCallIndex          = shellArguments.Count + 1,
                    ToolCallId             = "deny-fixture-host-probe",
                    ToolCallFunctionName   = "shell",
                    ToolCallArgumentsDelta = hostProbeArguments
                };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "tool_calls" };
            }
            else
            {
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
                yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason          = "stop" };
            }
        }
    }

    private sealed class FixtureCredentials : ICredentialStore
    {
        public bool    IsSupported                              => true;
        public string? Read(string   targetName)                => "offline-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }
}
