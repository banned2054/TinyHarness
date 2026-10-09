using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
/// M11.3 策略闭环：可信设置 → 冻结策略与环境 → 策略身份进入指纹/授权/审批展示/审计 →
/// fake model + fake backend 的完整工具调用闭环。所有测试离线运行，不触碰机器账户、ACL
/// 或防火墙。
///
/// M11.3 policy closure: trusted settings → frozen policy and environment →
/// the policy identity flowing into fingerprints, grants, approval display,
/// and audit → a complete tool-call loop over the fake model and fake backend.
/// Everything runs offline without touching machine accounts, ACLs, or the firewall.
/// </summary>
public class WindowsSandboxPolicyClosureTests
{
    private static ProcessExecutionPolicy SandboxExecutionPolicy(string? identity = null) => new()
    {
        Backend        = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName    = "windows-sandbox: workspace-write, network: restricted",
        PolicyIdentity = identity ??
                         "backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write\nnetwork=restricted",
    };

    private static ChatToolCall ShellCall(string executable, params string[] arguments)
        => new("call_1", "shell",
               new JsonObject
               {
                   ["mode"]       = "direct",
                   ["executable"] = executable,
                   ["arguments"]  = new JsonArray(arguments.Select(argument => (JsonNode?)JsonValue.Create(argument))
                                                           .ToArray()),
               }.ToJsonString());

    // ---- 指纹/授权绑定：无权限扩大、拒绝不失效 ----

    [Fact]
    public void PermissionEngine_SessionGrantDoesNotCrossExecutionPolicies()
    {
        using var dir         = new TestTempDir();
        var          hostTool  = new ShellTool(dir.Workspace);
        var          sandboxed = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var          engine    = new PermissionEngine(dir.Root);
        var          call      = ShellCall("dotnet", "test");

        engine.GrantSession(hostTool.Prepare(call));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(sandboxed.Prepare(call)));

        engine.GrantOnce(hostTool.Prepare(call));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(sandboxed.Prepare(call)));
    }

    [Fact]
    public void PermissionEngine_SessionDenySurvivesExecutionPolicyChange()
    {
        using var dir         = new TestTempDir();
        var          hostTool  = new ShellTool(dir.Workspace);
        var          sandboxed = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var          engine    = new PermissionEngine(dir.Root);
        var          call      = ShellCall("dotnet", "test");

        engine.DenySession(hostTool.Prepare(call));
        Assert.Equal(PermissionDecision.Deny, engine.Decide(sandboxed.Prepare(call)));
    }

    [Fact]
    public void PermissionEngine_GrantWithinSamePolicyStillCoversRepeatedCalls()
    {
        using var dir     = new TestTempDir();
        var          tool    = new ShellTool(dir.Workspace, executionPolicy : SandboxExecutionPolicy());
        var          engine  = new PermissionEngine(dir.Root);
        var          call    = ShellCall("dotnet", "test");

        engine.GrantSession(tool.Prepare(call));
        Assert.Equal(PermissionDecision.Allow, engine.Decide(tool.Prepare(call)));

        // A different sandbox policy identity (e.g. an added write root) must
        // not inherit the approval.
        engine.GrantSession(tool.Prepare(call));
        var widened = new ShellTool(dir.Workspace,
                                    executionPolicy : SandboxExecutionPolicy("backend=windows-sandbox\npolicyVersion=1\nkind=workspace-write\nextra-root"));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(widened.Prepare(call)));
    }

    // ---- Prepare 展示与冻结 ----

    [Fact]
    public void ShellTool_PrepareBindsPolicyToSummaryGrantConstraintAndAuditIdentity()
    {
        using var dir  = new TestTempDir();
        var          policy = SandboxExecutionPolicy();
        var          tool   = new ShellTool(dir.Workspace, executionPolicy : policy);

        var preparation = tool.Prepare(ShellCall("dotnet", "test"));

        Assert.Contains(policy.DisplayName, preparation.Summary, StringComparison.Ordinal);
        Assert.Equal(policy.PolicyIdentity, preparation.ExecutionPolicy);
        Assert.NotNull(preparation.SessionGrantConstraint);
        Assert.Contains(preparation.SessionConstraint!, preparation.SessionGrantConstraint!,
                        StringComparison.Ordinal);
        Assert.DoesNotContain("windows-sandbox", preparation.SessionConstraint!, StringComparison.Ordinal);
        // Command identity must stay parseable and policy-free: denies match it.
        var identity = JsonNode.Parse(preparation.SessionConstraint!)!.AsObject();
        Assert.Equal("dotnet", identity["executable"]!.GetValue<string>());
    }

    [Fact]
    public void ShellTool_PrepareWithoutPolicyKeepsLegacyShape()
    {
        using var dir = new TestTempDir();
        var          tool = new ShellTool(dir.Workspace);

        var preparation = tool.Prepare(ShellCall("dotnet", "test"));

        Assert.Null(preparation.SessionGrantConstraint);
        Assert.Null(preparation.ExecutionPolicy);
        Assert.DoesNotContain("[", preparation.Summary, StringComparison.Ordinal);
    }

    // ---- fake model 完整闭环（M11.3 验收门槛）----

    [Fact]
    public async Task AgentLoop_ShellUnderSandboxPolicy_ApprovesExecutesAndAuditsPolicyIdentity()
    {
        using var dir     = new TestTempDir();
        using var runsDir = new TestTempDir();
        var          policy  = SandboxExecutionPolicy();
        var          backend = new FakeProcessExecutionBackend { ExitCode = 0, StdoutChunks = ["ok"] };
        var          client  = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("shell",
                                               new JsonObject
                                               {
                                                   ["mode"]       = "direct",
                                                   ["executable"] = "dotnet",
                                                   ["arguments"]  = new JsonArray("--version"),
                                               }.ToJsonString()));
        client.Enqueue(FakeChatClient.Text("version checked"));

        var approver = new RecordingApprover();
        approver.Enqueue(ApprovalAction.AllowOnce);
        var tools = new ToolRegistry([new ShellTool(dir.Workspace, defaultTimeoutSeconds : 30,
                                                    processBackend : backend, executionPolicy : policy)]);
        var recorder = new FileRunRecorder(runsDir.Root);
        var loop = new AgentLoop(client, tools, new AgentOptions
                       {
                           Model                     = "test-model",
                           MaxAgentSteps             = 5,
                           DefaultToolTimeoutSeconds = 30,
                       },
                       new PermissionEngine(dir.Root), approver, recorder);

        var result = await loop.RunAsync("sys", "check the version", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(1, approver.Prompts);
        // The approval prompt must show the real isolation the command will run under.
        Assert.Contains("windows-sandbox", approver.SeenSummaries.Single(), StringComparison.Ordinal);
        // The approved plan is exactly the plan executed.
        Assert.NotNull(backend.ReceivedExecution);
        Assert.Equal("dotnet", backend.ReceivedExecution!.Executable);
        Assert.Equal(dir.Root, backend.ReceivedExecution.WorkingDirectory);

        var records = ReadAuditRecords(recorder.AuditPath);
        var prepared = Assert.Single(records, record => record!["kind"]!.GetValue<string>() == "tool.prepared")!;
        Assert.Contains("backend=windows-sandbox", prepared["executionPolicy"]!.GetValue<string>(),
                        StringComparison.Ordinal);
        var permission = Assert.Single(records, record => record!["kind"]!.GetValue<string>() == "tool.permission")!;
        Assert.Equal("Ask", permission["decision"]!.GetValue<string>());
        Assert.Equal("allow once", permission["outcome"]!.GetValue<string>());
        Assert.Contains("backend=windows-sandbox", permission["executionPolicy"]!.GetValue<string>(),
                        StringComparison.Ordinal);
        var toolResult = Assert.Single(records, record => record!["kind"]!.GetValue<string>() == "tool.result")!;
        Assert.True(toolResult["succeeded"]!.GetValue<bool>());
        Assert.Contains("backend=windows-sandbox", toolResult["executionPolicy"]!.GetValue<string>(),
                        StringComparison.Ordinal);
    }

    // ---- 目标环境治理 ----

    [Fact]
    public void SandboxTargetEnvironment_BuildUsesAllowlistAndRedirectsTemp()
    {
        using var dir      = new TestTempDir();
        var          tempRoot  = Path.Combine(dir.Root, "sandbox-tmp");
        Directory.CreateDirectory(tempRoot);

        var environment = SandboxTargetEnvironment.Build(tempRoot);

        Assert.Equal(tempRoot, environment["TEMP"]);
        Assert.Equal(tempRoot, environment["TMP"]);
        Assert.Equal(tempRoot, environment["USERPROFILE"]);
        Assert.Equal(tempRoot, environment["HOME"]);
        // Only the fixed allowlist plus redirects may come from the host.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PATH", "PATHEXT", "SystemRoot", "SystemDrive", "ComSpec", "OS", "NUMBER_OF_PROCESSORS",
            "PROCESSOR_ARCHITECTURE", "TEMP", "TMP", "USERPROFILE", "HOME",
        };
        Assert.All(environment.Keys, name => Assert.Contains(name, allowed));
        if (Environment.GetEnvironmentVariable("PATH") is { } hostPath)
        {
            Assert.Equal(hostPath, environment["PATH"]);
        }
    }

    [Fact]
    public void SandboxTargetEnvironment_ExtraVariablesApplyAndSecretsNeverLeak()
    {
        using var dir      = new TestTempDir();
        var          tempRoot  = Path.Combine(dir.Root, "sandbox-tmp");
        Directory.CreateDirectory(tempRoot);

        var environment = SandboxTargetEnvironment.Build(
            tempRoot,
            new Dictionary<string, string> { ["NUGET_PACKAGES"] = "X:\\nuget", ["SECRET_KEY"] = "trusted-value" },
            new Dictionary<string, string> { ["SECRET_KEY"] = "api-key-value" });

        Assert.Equal("X:\\nuget", environment["NUGET_PACKAGES"]);
        Assert.False(environment.ContainsKey("SECRET_KEY"));
    }

    [Fact]
    public void SandboxTargetEnvironment_RejectsRelativeTempRoot()
    {
        Assert.Throws<ArgumentException>(() => SandboxTargetEnvironment.Build("relative-tmp"));
    }

    // ---- 策略解析：额外写根 ----

    [Fact]
    public void SandboxPolicyResolver_WorkspaceWriteMergesAdditionalWriteRoots()
    {
        using var dir     = new TestTempDir();
        using var extra   = new TestTempDir();
        var          env     = new Dictionary<string, string> { ["TEMP"] = Path.Combine(dir.Root, "t") };
        Directory.CreateDirectory(env["TEMP"]);

        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root, env,
                                                   [extra.Root, dir.Root]);

        Assert.Contains(extra.Root, policy.EffectiveWriteRoots);
        Assert.Single(policy.EffectiveWriteRoots,
                      root => string.Equals(root, dir.Root, StringComparison.OrdinalIgnoreCase));
        var writeEntries = policy.SpawnProfile.FileSystem.Entries
                                 .Where(entry => entry.Access == "write" && entry.Path.ExplicitPath is not null)
                                 .Select(entry => entry.Path.ExplicitPath)
                                 .ToList();
        Assert.Contains(extra.Root, writeEntries);
    }

    [Fact]
    public void SandboxPolicyResolver_ReadOnlyGrantsOnlyDeclaredWriteRoots()
    {
        using var dir   = new TestTempDir();
        using var extra = new TestTempDir();
        var env = new Dictionary<string, string>();

        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.ReadOnly, dir.Root, env, [extra.Root]);

        Assert.Equal([extra.Root], policy.EffectiveWriteRoots);
        Assert.Equal([extra.Root],
                     policy.SpawnProfile.FileSystem.Entries
                           .Where(entry => entry.Access == "write")
                           .Select(entry => entry.Path.ExplicitPath)
                           .ToList());
    }

    [Fact]
    public void SandboxPolicyResolver_RejectsRelativeAdditionalWriteRoot()
    {
        using var dir = new TestTempDir();
        Assert.Throws<ArgumentException>(
            () => SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root,
                                                new Dictionary<string, string>(), ["relative-root"]));
    }

    // ---- 组合器：fail closed、身份稳定 ----

    [Fact]
    public void WindowsSandboxComposer_ComposesFrozenPolicyEnvironmentAndIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home = new TestTempDir();
        var setupPath   = home.WriteFile("setup.exe", "stub");
        var runnerPath  = home.WriteFile("runner.exe", "stub");
        var settings = new WindowsSandboxSettings
        {
            Enabled             = true,
            SetupExecutablePath = setupPath,
            RunnerExecutablePath = runnerPath,
            SandboxHome         = home.Root,
        };
        using var workspace = new TestTempDir();

        var composed = WindowsSandboxComposer.Compose(settings, workspace.Root);

        var expectedTemp = Path.Combine(home.Root, "tmp");
        Assert.True(Directory.Exists(expectedTemp));
        Assert.Equal(expectedTemp, composed.TargetEnvironment["TEMP"]);
        Assert.Equal(workspace.Root, composed.Policy.WorkspaceRoot);
        Assert.Contains("workspace-write", composed.ExecutionPolicy.DisplayName, StringComparison.Ordinal);
        Assert.StartsWith("backend=windows-sandbox", composed.ExecutionPolicy.PolicyIdentity,
                          StringComparison.Ordinal);
        Assert.Contains("network=restricted", composed.ExecutionPolicy.PolicyIdentity, StringComparison.Ordinal);

        var again = WindowsSandboxComposer.Compose(settings, workspace.Root);
        Assert.Equal(composed.ExecutionPolicy.PolicyIdentity, again.ExecutionPolicy.PolicyIdentity);
    }

    [Fact]
    public void WindowsSandboxComposer_IdentityBindsAdditionalWriteRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home     = new TestTempDir();
        using var workspace = new TestTempDir();
        using var extra    = new TestTempDir();
        var baseSettings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
        };

        var withoutRoot = WindowsSandboxComposer.Compose(baseSettings, workspace.Root).ExecutionPolicy.PolicyIdentity;
        var withRoot = WindowsSandboxComposer.Compose(baseSettings with { AdditionalWriteRoots = [extra.Root] },
                                                      workspace.Root)
                                             .ExecutionPolicy.PolicyIdentity;
        Assert.NotEqual(withoutRoot, withRoot);
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedWhenComponentsAreMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home     = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = Path.Combine(home.Root, "missing-runner.exe"),
            SandboxHome          = home.Root,
        };

        var error = Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
        Assert.Contains("refusing to fall back", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedOnRelativeComponentPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = "relative\\setup.exe",
            RunnerExecutablePath = "C:\\absolute\\runner.exe",
            SandboxHome          = "C:\\absolute\\home",
        };

        Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedWhenTempRootIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home     = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
            SandboxTempRoot      = Path.Combine(workspace.Root, "tmp"),
        };

        Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedWhenSandboxHomeIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = workspace.CreateDirectory("sandbox-home"),
        };

        var error = Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
        Assert.Contains("sandbox home", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedWhenComponentExecutableIsInsideWorkspace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = workspace.WriteFile("tools\\setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
        };

        var error = Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
        Assert.Contains("setup executable", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsSandboxComposer_FailsClosedWhenTrustedPathLiesInsideAdditionalWriteRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root,
            AdditionalWriteRoots = [home.Root],
        };

        // Declaring the sandbox home as an additional write root makes every
        // trusted path tamperable; the message must name the hit write root.
        var error = Assert.Throws<InvalidOperationException>(() => WindowsSandboxComposer.Compose(settings, workspace.Root));
        Assert.Contains(home.Root, error.Message, StringComparison.Ordinal);
    }

    // ---- 可信设置的存储边界 ----

    [Fact]
    public async Task UserConfigStore_RoundTripsWindowsSandboxSettings()
    {
        using var dir  = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        var       config = new UserConfig
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
                    ExtraEnvironment     = new Dictionary<string, string> { ["NUGET_PACKAGES"] = "C:\\cache\\nuget" },
                },
            },
        };

        await UserConfigStore.SaveAsync(path, config, CancellationToken.None);
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

        var sandbox = loaded.Settings!.WindowsSandbox!;
        Assert.True(sandbox.Enabled);
        Assert.Equal("C:\\sb\\setup.exe", sandbox.SetupExecutablePath);
        Assert.Equal("C:\\sb\\runner.exe", sandbox.RunnerExecutablePath);
        Assert.Equal("C:\\sb-home", sandbox.SandboxHome);
        Assert.Equal(SandboxPolicyKind.ReadOnly, sandbox.Policy);
        Assert.Equal("C:\\sb-home\\tmp", sandbox.SandboxTempRoot);
        Assert.Equal(["C:\\cache\\nuget"], sandbox.AdditionalWriteRoots);
        Assert.Equal("C:\\cache\\nuget", sandbox.ExtraEnvironment!["NUGET_PACKAGES"]);
    }

    [Fact]
    public async Task UserConfigStore_AbsentWindowsSandboxYieldsNullAndInvalidValuesFail()
    {
        using var dir = new TestTempDir();
        var       path = Path.Combine(dir.Root, "user-config.json");
        await File.WriteAllTextAsync(path, """{"settings":{}}""");
        var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
        Assert.Null(loaded.Settings!.WindowsSandbox);

        var invalid = Path.Combine(dir.Root, "invalid.json");
        await File.WriteAllTextAsync(invalid,
                                     """{"settings":{"windowsSandbox":{"enabled":true,"policy":"unrestricted"}}}""");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => UserConfigStore.LoadAsync(invalid, CancellationToken.None));
    }

    /// <summary>记录审批摘要并按脚本回复的审批器。</summary>
    private sealed class RecordingApprover : IApprovalProvider
    {
        private readonly Queue<ApprovalAction> _actions = new();

        public int Prompts { get; private set; }

        public List<string> SeenSummaries { get; } = [];

        public void Enqueue(ApprovalAction action) => _actions.Enqueue(action);

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Prompts++;
            SeenSummaries.Add(preparation.Summary);
            if (_actions.Count == 0)
            {
                throw new InvalidOperationException("No scripted approval action left.");
            }

            return Task.FromResult(_actions.Dequeue());
        }
    }

    private static List<JsonObject?> ReadAuditRecords(string auditPath)
        => File.ReadAllLines(auditPath)
               .Where(line => !string.IsNullOrWhiteSpace(line))
               .Select(line => JsonNode.Parse(line) as JsonObject)
               .ToList();
}
