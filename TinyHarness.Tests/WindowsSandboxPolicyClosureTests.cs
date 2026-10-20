using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         策略闭环：可信设置 → 冻结策略与环境 → 策略身份进入指纹/授权/审批展示/审计 →
///         fake model + fake backend 的完整工具调用闭环。所有测试离线运行，不触碰机器账户、ACL
///         或防火墙。
///     </para>
///     <para>
///         Policy closure: trusted settings → frozen policy and environment →
///         the policy identity flowing into fingerprints, grants, approval display,
///         and audit → a complete tool-call loop over the fake model and fake backend.
///         Everything runs offline without touching machine accounts, ACLs, or the firewall.
///     </para>
/// </summary>
public class WindowsSandboxPolicyClosureTests
{
    private static ProcessExecutionPolicy SandboxExecutionPolicy(string? identity = null) => new()
    {
        Backend     = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName = "windows-sandbox: workspace-write, network: restricted",
        PolicyIdentity = identity ??
                         "backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write\nnetwork=restricted"
    };

    private static ChatToolCall ShellCall(string executable, params string[] arguments)
    {
        return new ChatToolCall("call_1", "shell",
                                new JsonObject
                                {
                                    ["mode"]       = "direct",
                                    ["executable"] = executable,
                                    ["arguments"] =
                                        new JsonArray(arguments
                                                     .Select(argument => (JsonNode?)JsonValue.Create(argument))
                                                     .ToArray())
                                }.ToJsonString());
    }

    // ---- 指纹/授权绑定：无权限扩大、拒绝不失效 ----

    [Test]
    public void PermissionEngine_SessionGrantDoesNotCrossExecutionPolicies()
    {
        using var dir       = new TestTempDir();
        var       hostTool  = new ShellTool(dir.Workspace);
        var       sandboxed = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var       engine    = new PermissionEngine(dir.Root);
        var       call      = ShellCall("dotnet", "test");

        engine.GrantSession(hostTool.Prepare(call));
        Assert.That(engine.Decide(sandboxed.Prepare(call)), Is.EqualTo(PermissionDecision.Ask));

        engine.GrantOnce(hostTool.Prepare(call));
        Assert.That(engine.Decide(sandboxed.Prepare(call)), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void PermissionEngine_SessionDenySurvivesExecutionPolicyChange()
    {
        using var dir       = new TestTempDir();
        var       hostTool  = new ShellTool(dir.Workspace);
        var       sandboxed = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var       engine    = new PermissionEngine(dir.Root);
        var       call      = ShellCall("dotnet", "test");

        engine.DenySession(hostTool.Prepare(call));
        Assert.That(engine.Decide(sandboxed.Prepare(call)), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void PermissionEngine_GrantWithinSamePolicyStillCoversRepeatedCalls()
    {
        using var dir    = new TestTempDir();
        var       tool   = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var       engine = new PermissionEngine(dir.Root);
        var       call   = ShellCall("dotnet", "test");

        engine.GrantSession(tool.Prepare(call));
        Assert.That(engine.Decide(tool.Prepare(call)), Is.EqualTo(PermissionDecision.Allow));

        // 不同的沙箱策略身份（如新增写根）不得继承既有授权。
        engine.GrantSession(tool.Prepare(call));
        var widened = new ShellTool(dir.Workspace,
                                    executionPolicy :
                                    SandboxExecutionPolicy("backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write\nextra-root"));
        Assert.That(engine.Decide(widened.Prepare(call)), Is.EqualTo(PermissionDecision.Ask));
    }

    // ---- Prepare 展示与冻结 ----

    [Test]
    public void ShellTool_PrepareBindsPolicyToSummaryGrantConstraintAndAuditIdentity()
    {
        using var dir    = new TestTempDir();
        var       policy = SandboxExecutionPolicy();
        var       tool   = new ShellTool(dir.Workspace, executionPolicy : policy);

        var preparation = tool.Prepare(ShellCall("dotnet", "test"));

        TestAssert.Contains(policy.DisplayName, preparation.Summary, StringComparison.Ordinal);
        Assert.That(preparation.ExecutionPolicy, Is.EqualTo(policy.PolicyIdentity));
        TestAssert.NotNull(preparation.SessionGrantConstraint);
        TestAssert.Contains(preparation.SessionConstraint!, preparation.SessionGrantConstraint!,
                            StringComparison.Ordinal);
        TestAssert.DoesNotContain("windows-sandbox", preparation.SessionConstraint!, StringComparison.Ordinal);
        // 命令身份必须保持可解析且不含策略：拒绝规则按它匹配。
        var identity = JsonNode.Parse(preparation.SessionConstraint!)!.AsObject();
        Assert.That(identity["executable"]!.GetValue<string>(), Is.EqualTo("dotnet"));
    }

    [Test]
    public void ShellTool_PrepareWithoutPolicyKeepsLegacyShape()
    {
        using var dir  = new TestTempDir();
        var       tool = new ShellTool(dir.Workspace);

        var preparation = tool.Prepare(ShellCall("dotnet", "test"));

        Assert.That(preparation.SessionGrantConstraint, Is.Null);
        Assert.That(preparation.ExecutionPolicy, Is.Null);
        TestAssert.DoesNotContain("[", preparation.Summary, StringComparison.Ordinal);
    }

    // ---- fake model 完整闭环（验收门槛）----

    [Test]
    public async Task AgentLoop_ShellUnderSandboxPolicy_PassesPolicyIdentityToRecorder()
    {
        using var dir     = new TestTempDir();
        var       policy  = SandboxExecutionPolicy();
        var       backend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["ok"] };
        var       client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("shell",
                                               new JsonObject
                                               {
                                                   ["mode"]       = "direct",
                                                   ["executable"] = "dotnet",
                                                   ["arguments"]  = new JsonArray("--version")
                                               }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("version checked"));

        var approver = new RecordingApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        var tools = new ToolRegistry([
            new ShellTool(dir.Workspace, 30,
                          processBackend : backend, executionPolicy : policy)
        ]);
        var recorder = new PolicyCaptureRecorder();
        var loop = new AgentLoop(client, tools, new AgentOptions
                                 {
                                     Model                     = "test-model",
                                     MaxAgentSteps             = 5,
                                     DefaultToolTimeoutSeconds = 30
                                 },
                                 new PermissionEngine(dir.Root), approver, recorder);

        var result = await loop.RunAsync("sys", "check the version", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(approver.Prompts, Is.EqualTo(1));
        // 审批提示必须展示命令将运行的真实隔离环境。
        TestAssert.Contains("windows-sandbox", approver.SeenSummaries.Single(), StringComparison.Ordinal);
        // 批准的计划与执行的计划完全一致。
        TestAssert.NotNull(backend.ReceivedExecution);
        Assert.That(backend.ReceivedExecution!.Executable, Is.EqualTo("dotnet"));
        Assert.That(backend.ReceivedExecution.WorkingDirectory, Is.EqualTo(dir.Root));

        TestAssert.Contains("backend=windows-sandbox", TestAssert.Single(recorder.PreparedPolicies),
                            StringComparison.Ordinal);
        var permission = TestAssert.Single(recorder.PermissionRecords);
        Assert.That(permission.Decision, Is.EqualTo(PermissionDecision.Ask));
        Assert.That(permission.Outcome, Is.EqualTo("allow once"));
        TestAssert.Contains("backend=windows-sandbox", permission.ExecutionPolicy, StringComparison.Ordinal);
        var toolResult = TestAssert.Single(recorder.ResultRecords);
        Assert.That(toolResult.Succeeded, Is.True);
        TestAssert.Contains("backend=windows-sandbox", toolResult.ExecutionPolicy, StringComparison.Ordinal);
    }

    // ---- 目标环境治理 ----

    [Test]
    public void SandboxTargetEnvironment_BuildUsesAllowlistAndRedirectsTemp()
    {
        using var dir      = new TestTempDir();
        var       tempRoot = Path.Combine(dir.Root, "sandbox-tmp");
        Directory.CreateDirectory(tempRoot);

        var environment = SandboxTargetEnvironment.Build(tempRoot);

        Assert.That(environment["TEMP"], Is.EqualTo(tempRoot));
        Assert.That(environment["TMP"], Is.EqualTo(tempRoot));
        Assert.That(environment["USERPROFILE"], Is.EqualTo(tempRoot));
        Assert.That(environment["HOME"], Is.EqualTo(tempRoot));
        Assert.That(environment["DOTNET_CLI_HOME"], Is.EqualTo(tempRoot));
        Assert.That(environment["APPDATA"], Is.EqualTo(Path.Combine(tempRoot, "AppData", "Roaming")));
        Assert.That(environment["LOCALAPPDATA"], Is.EqualTo(Path.Combine(tempRoot, "AppData", "Local")));
        // 来自宿主的只有固定 allowlist 加重定向。
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
            "PROCESSOR_ARCHITECTURE", "ProgramFiles", "ProgramFiles(x86)", "ProgramData",
            "TEMP", "TMP", "USERPROFILE", "HOME", "DOTNET_CLI_HOME", "APPDATA", "LOCALAPPDATA"
        };
        TestAssert.All(environment.Keys, name => Assert.That(allowed, Does.Contain(name)));
        if (Environment.GetEnvironmentVariable("PATH") is { } hostPath)
            Assert.That(environment["PATH"], Is.EqualTo(hostPath));
        foreach (var name in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramData" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } hostValue)
                Assert.That(environment[name], Is.EqualTo(hostValue));
    }

    [Test]
    public void SandboxTargetEnvironment_ExtraVariablesApplyAndSecretsNeverLeak()
    {
        using var dir      = new TestTempDir();
        var       tempRoot = Path.Combine(dir.Root, "sandbox-tmp");
        Directory.CreateDirectory(tempRoot);

        var environment = SandboxTargetEnvironment.Build(tempRoot,
                                                         new Dictionary<string, string>
                                                         {
                                                             ["NUGET_PACKAGES"] = "X:\\nuget",
                                                             ["SECRET_KEY"]     = "trusted-value"
                                                         },
                                                         new Dictionary<string, string>
                                                             { ["SECRET_KEY"] = "api-key-value" });

        Assert.That(environment["NUGET_PACKAGES"], Is.EqualTo("X:\\nuget"));
        Assert.That(environment.ContainsKey("SECRET_KEY"), Is.False);
    }

    [Test]
    public void SandboxTargetEnvironment_RejectsRelativeTempRoot()
    {
        TestAssert.Throws<ArgumentException>(() => SandboxTargetEnvironment.Build("relative-tmp"));
    }

    // ---- 策略解析：额外写根 ----

    [Test]
    public void SandboxPolicyResolver_WorkspaceWriteMergesAdditionalWriteRoots()
    {
        using var dir   = new TestTempDir();
        using var extra = new TestTempDir();
        var       env   = new Dictionary<string, string> { ["TEMP"] = Path.Combine(dir.Root, "t") };
        Directory.CreateDirectory(env["TEMP"]);

        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
                                                   [extra.Root, dir.Root]);

        Assert.That(policy.EffectiveWriteRoots, Does.Contain(extra.Root));
        TestAssert.Single(policy.EffectiveWriteRoots,
                          root => string.Equals(root, dir.Root, StringComparison.OrdinalIgnoreCase));
        var writeEntries = policy.SpawnProfile.FileSystem.Entries
                                 .Where(entry => entry is { Access: "write", Path.ExplicitPath: not null })
                                 .Select(entry => entry.Path.ExplicitPath)
                                 .ToList();
        Assert.That(writeEntries, Does.Contain(extra.Root));
    }

    [Test]
    public void SandboxPolicyResolver_ReadOnlyGrantsOnlyDeclaredWriteRoots()
    {
        using var dir   = new TestTempDir();
        using var extra = new TestTempDir();
        var       env   = new Dictionary<string, string>();

        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, dir.Root, env, [extra.Root]);

        Assert.That(policy.EffectiveWriteRoots, Is.EqualTo([extra.Root]));
        Assert.That(policy.SpawnProfile.FileSystem.Entries
                          .Where(entry => entry.Access == "write")
                          .Select(entry => entry.Path.ExplicitPath)
                          .ToList(), Is.EqualTo([extra.Root]));
    }

    [Test]
    public void SandboxPolicyResolver_RejectsRelativeAdditionalWriteRoot()
    {
        using var dir = new TestTempDir();
        TestAssert.Throws<ArgumentException>(() => SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                      dir.Root,
                                                      new Dictionary<string, string>(),
                                                      ["relative-root"]));
    }

    // ---- 组合器：fail closed、身份稳定 ----

    [Test]
    public void WindowsSandboxComposer_ComposesFrozenPolicyEnvironmentAndIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home       = new TestTempDir();
        var       setupPath  = home.WriteFile("setup.exe", "stub");
        var       runnerPath = home.WriteFile("runner.exe", "stub");
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = setupPath,
            RunnerExecutablePath = runnerPath,
            SandboxHome          = home.Root
        };
        using var workspace = new TestTempDir();

        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root);

        var expectedTemp = Path.Combine(home.Root, "tmp");
        Assert.That(Directory.Exists(expectedTemp), Is.True);
        Assert.That(composed.TargetEnvironment["TEMP"], Is.EqualTo(expectedTemp));
        Assert.That(composed.Policy.TempWriteRoots, Is.EqualTo([expectedTemp]));
        Assert.That(composed.Policy.EffectiveWriteRoots, Is.EqualTo([workspace.Root, expectedTemp]));
        foreach (var name in new[] { "USERPROFILE", "HOME", "DOTNET_CLI_HOME", "APPDATA", "LOCALAPPDATA" })
        {
            Assert.That(Workspace.IsInside(expectedTemp, composed.TargetEnvironment[name]), Is.True);
            Assert.That(Workspace.IsInside(workspace.Root, composed.TargetEnvironment[name]), Is.False);
        }

        Assert.That(composed.Policy.WorkspaceRoot, Is.EqualTo(workspace.Root));
        TestAssert.Contains("workspace-write", composed.ExecutionPolicy.DisplayName, StringComparison.Ordinal);
        Assert.That(composed.ExecutionPolicy.PolicyIdentity,
                    Does.StartWith("backend=windows-sandbox").Using(StringComparison.Ordinal));
        TestAssert.Contains("network=restricted", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);

        var again = WindowsSandboxComposer.Compose(settings, workspace.Root);
        Assert.That(again.ExecutionPolicy.PolicyIdentity, Is.EqualTo(composed.ExecutionPolicy.PolicyIdentity));

        var overridden = WindowsSandboxComposer.Compose(settings with
        {
            ExtraEnvironment = new Dictionary<string, string> { ["APPDATA"] = Path.Combine(expectedTemp, "custom") }
        }, workspace.Root);
        Assert.That(overridden.ExecutionPolicy.PolicyIdentity, Is.Not.EqualTo(composed.ExecutionPolicy.PolicyIdentity));
        Assert.That(overridden.Policy.EffectiveWriteRoots, Is.EqualTo(composed.Policy.EffectiveWriteRoots));

        var filtered = WindowsSandboxComposer.Compose(settings with
        {
            ExtraEnvironment = new Dictionary<string, string> { ["SYNTHETIC_SECRET"] = "test-only" }
        }, workspace.Root, new Dictionary<string, string> { ["synthetic_secret"] = "test-only" });
        Assert.That(filtered.ExecutionPolicy.PolicyIdentity, Is.EqualTo(composed.ExecutionPolicy.PolicyIdentity));
    }

    [Test]
    public void WindowsSandboxComposer_IdentityBindsAdditionalWriteRoots()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        using var extra     = new TestTempDir();
        var baseSettings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        var withoutRoot = WindowsSandboxComposer.Compose(baseSettings, workspace.Root).ExecutionPolicy.PolicyIdentity;
        var withRoot = WindowsSandboxComposer.Compose(baseSettings with { AdditionalWriteRoots = [extra.Root] },
                                                      workspace.Root)
                                             .ExecutionPolicy.PolicyIdentity;
        Assert.That(withRoot, Is.Not.EqualTo(withoutRoot));
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedWhenComponentsAreMissing()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = Path.Combine(home.Root, "missing-runner.exe"),
            SandboxHome          = home.Root
        };

        var error =
            TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer
                                                            .Compose(settings, workspace.Root));
        TestAssert.Contains("refusing to fall back", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedOnRelativeComponentPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = "relative\\setup.exe",
            RunnerExecutablePath = "C:\\absolute\\runner.exe",
            SandboxHome          = "C:\\absolute\\home"
        };

        TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedWhenTempRootIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
            SandboxTempRoot      = Path.Combine(workspace.Root, "tmp")
        };

        TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedWhenSandboxHomeIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = workspace.CreateDirectory("sandbox-home")
        };

        var error =
            TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer
                                                            .Compose(settings, workspace.Root));
        TestAssert.Contains("sandbox home", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedWhenComponentExecutableIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = workspace.WriteFile("tools\\setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        var error =
            TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer
                                                            .Compose(settings, workspace.Root));
        TestAssert.Contains("setup executable", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void WindowsSandboxComposer_FailsClosedWhenTrustedPathLiesInsideAdditionalWriteRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
            AdditionalWriteRoots = [home.Root]
        };

        // 把沙箱 home 声明为附加写根会使所有可信路径可被篡改；错误信息必须点名命中的写根。
        var error =
            TestAssert.Throws<InvalidOperationException>(() => WindowsSandboxComposer
                                                            .Compose(settings, workspace.Root));
        TestAssert.Contains(home.Root, error.Message, StringComparison.Ordinal);
    }

    // ---- 可信设置的存储边界 ----

    [Test]
    public async Task UserConfigStore_RoundTripsWindowsSandboxSettings()
    {
        using var dir  = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        var config = new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = new WindowsSandboxSettings
                {
                    Enabled              = true,
                    SetupExecutablePath  = "C:\\sb\\setup.exe",
                    RunnerExecutablePath = "C:\\sb\\runner.exe",
                    SandboxHome          = "C:\\sb-home",
                    Policy               = SandboxPolicyKind.ReadOnly,
                    SandboxTempRoot      = "C:\\sb-home\\tmp",
                    AdditionalWriteRoots = ["C:\\cache\\nuget"],
                    ExtraEnvironment     = new Dictionary<string, string> { ["NUGET_PACKAGES"] = "C:\\cache\\nuget" }
                }
            }
        };

        await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

        var sandbox = loaded.Settings!.WindowsSandbox!;
        Assert.That(sandbox.Enabled, Is.True);
        Assert.That(sandbox.SetupExecutablePath, Is.EqualTo("C:\\sb\\setup.exe"));
        Assert.That(sandbox.RunnerExecutablePath, Is.EqualTo("C:\\sb\\runner.exe"));
        Assert.That(sandbox.SandboxHome, Is.EqualTo("C:\\sb-home"));
        Assert.That(sandbox.Policy, Is.EqualTo(SandboxPolicyKind.ReadOnly));
        Assert.That(sandbox.SandboxTempRoot, Is.EqualTo("C:\\sb-home\\tmp"));
        Assert.That(sandbox.AdditionalWriteRoots, Is.EqualTo(["C:\\cache\\nuget"]));
        Assert.That(sandbox.ExtraEnvironment!["NUGET_PACKAGES"], Is.EqualTo("C:\\cache\\nuget"));
    }

    [Test]
    public async Task UserConfigStore_AbsentWindowsSandboxYieldsNullAndInvalidValuesFail()
    {
        using var dir  = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        await File.WriteAllTextAsync(path, """{"settings":{}}""");
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.That(loaded.Settings!.WindowsSandbox, Is.Null);

        var invalid = Path.Combine(dir.Root, "invalid.json");
        await File.WriteAllTextAsync(invalid,
                                     """{"settings":{"windowsSandbox":{"enabled":true,"policy":"unrestricted"}}}""");
        await TestAssert.ThrowsAsync<InvalidDataException>(() => UserConfigStore
                                                              .LoadAsync(invalid, CancellationToken.None));
    }

    private sealed class PolicyCaptureRecorder : IRunRecorder
    {
        public List<string?> PreparedPolicies { get; } = [];

        public List<(PermissionDecision Decision, string Outcome, string? ExecutionPolicy)> PermissionRecords { get; }
            = [];

        public List<(bool Succeeded, string? ExecutionPolicy)> ResultRecords { get; } = [];

        public Task StartAsync(string systemPrompt, string userInput, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordPreparedAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            PreparedPolicies.Add(preparation.ExecutionPolicy);
            return Task.CompletedTask;
        }

        public Task RecordPermissionAsync(ToolPreparation   preparation, PermissionDecision decision, string outcome,
                                          CancellationToken cancellationToken)
        {
            PermissionRecords.Add((decision, outcome, preparation.ExecutionPolicy));
            return Task.CompletedTask;
        }

        public Task RecordResultAsync(
            ToolPreparation preparation, ToolResult result, CancellationToken cancellationToken)
        {
            ResultRecords.Add((result.Succeeded, preparation.ExecutionPolicy));
            return Task.CompletedTask;
        }

        public Task RecordCompactionAsync(ContextChange change, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RecordModelUsageAsync(
            int? inputTokens, int? outputTokens, string? finishReason, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task CompleteAsync(AgentResult       result, IReadOnlyList<ChatMessage> messages, StructuredState state,
                                  CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    ///     <para>记录审批摘要并按脚本回复的审批器。</para>
    ///     <para>An approver that records approval summaries and replies from a script.</para>
    /// </summary>
    private sealed class RecordingApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public List<string> SeenSummaries { get; } = [];

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Prompts++;
            SeenSummaries.Add(preparation.Summary);
            if (_actions.Count == 0) throw new InvalidOperationException("No scripted approval action left.");

            return Task.FromResult(_actions.Dequeue());
        }

        public void Enqueue(ApprovalAction action)
        {
            _actions.Enqueue(action);
        }
    }
}
