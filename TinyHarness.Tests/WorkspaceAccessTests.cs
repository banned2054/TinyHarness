using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证信任、只读及授权失败边界；不启动 UAC 或修改 ACL。</para>
///     <para>Verifies consent, read-only and grant failure boundaries offline without UAC or ACL changes.</para>
/// </summary>
public class WorkspaceAccessTests
{
    [TestCase("filesystem.write")]
    [TestCase("process.execute")]
    [TestCase("future.unknown")]
    public async Task ReadOnly_RejectsGrantsAndDirectExecution_ThenInvalidatesOldPlans(string capability)
    {
        using var dir         = new TestTempDir();
        var       state       = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var       inner       = new CountingTool(capability, dir.Root);
        var       tool        = state.Guard(() => inner);
        var       plan        = tool.Prepare(new ChatToolCall("id", "probe", "{}"));
        var       permissions = new PermissionEngine(dir.Root, workspaceReadOnly : () => state.IsReadOnly);
        permissions.GrantOnce(plan);
        permissions.GrantSession(plan);
        Assert.That(permissions.Decide(plan), Is.EqualTo(PermissionDecision.Deny));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(plan, CancellationToken.None));
        Assert.That(inner.Executions, Is.Zero);
        state.SetReadOnly(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(plan, CancellationToken.None));
        var fresh = tool.Prepare(new ChatToolCall("id2", "probe", "{}"));
        Assert.That(permissions.Decide(fresh), Is.EqualTo(PermissionDecision.Ask));
        Assert.That((await tool.ExecuteAsync(fresh, CancellationToken.None)).Succeeded, Is.True);
    }

    [Test]
    public void ReadOnly_CommandRuleCannotEnableShell()
    {
        using var dir   = new TestTempDir();
        var       shell = new ShellTool(dir.Workspace);
        var plan = shell.Prepare(new ChatToolCall("id", "shell",
                                                  "{\"mode\":\"direct\",\"executable\":\"dotnet\",\"arguments\":[\"test\"]}"));
        var permissions =
            new PermissionEngine(dir.Root, [new CommandRule { Executable = "dotnet", Arguments = ["test"] }],
                                 workspaceReadOnly : () => true);
        Assert.That(permissions.Decide(plan), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public async Task ReadOnly_ReadFileStillWorks()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("input.txt", "unchanged");
        var state = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var tool  = state.Guard(() => new ReadFileTool(dir.Workspace));
        var plan  = tool.Prepare(new ChatToolCall("read", "read_file", "{\"path\":\"input.txt\"}"));
        Assert.That((await tool.ExecuteAsync(plan, CancellationToken.None)).Content, Does.Contain("unchanged"));
    }

    [Test]
    public async Task TrustStore_ExactIdentityNoParentInheritance_PreservesOtherRecords()
    {
        using var dir      = new TestTempDir();
        using var storage  = new TestTempDir();
        var       child    = WorkspaceDirectoryIdentity.Capture(dir.CreateDirectory("child"));
        var       identity = WorkspaceDirectoryIdentity.Capture(dir.Root);
        var       store    = new WorkspaceTrustStore(Path.Combine(storage.Root, "trust.json"), [dir.Root]);
        Assert.That(await store.ReadAsync(identity, CancellationToken.None), Is.Null);
        await store.SaveAsync(identity, true, CancellationToken.None);
        Assert.That(await store.ReadAsync(child, CancellationToken.None), Is.Null);
        await store.SaveAsync(child, false, CancellationToken.None);
        Assert.That(await store.ReadAsync(identity, CancellationToken.None), Is.True);
        Assert.That(await store.ReadAsync(child, CancellationToken.None), Is.False);
        Assert.That(await store.ReadAsync(identity with { FileIdentity = "replacement" }, CancellationToken.None),
                    Is.Null);
    }

    [Test]
    public async Task TrustStore_RejectsWritableStorageAndInvalidDocument()
    {
        using var dir      = new TestTempDir();
        using var storage  = new TestTempDir();
        var       identity = WorkspaceDirectoryIdentity.Capture(dir.Root);
        var       invalid  = new WorkspaceTrustStore(dir.WriteFile("trust.json", "{}"), [dir.Root]);
        await Assert.CatchAsync(() => invalid.ReadAsync(identity, CancellationToken.None));
        var path  = storage.WriteFile("trust.json", "{\"version\":999,\"workspaces\":{}}");
        var store = new WorkspaceTrustStore(path, [dir.Root]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(identity, true, CancellationToken.None));
        Assert.That(File.ReadAllText(path), Does.Contain("999"));
    }

    [Test]
    public async Task UnknownNoninteractive_DoesNotReadInputPersistOrActivate()
    {
        using var fixture = new Fixture(false, "t\ny\n");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
        Assert.That(fixture.Activations, Is.Zero);
        Assert.That(fixture.Grant.Calls, Is.Zero);
        Assert.That(File.Exists(fixture.TrustPath), Is.False);
        Assert.That(await fixture.Input.ReadLineAsync(), Is.EqualTo("t"));
    }

    [Test]
    public async Task TrustConfirmation_PersistsButRechecksReadiness()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Grant.Ready = true;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.False);
        fixture.Grant.Ready = false;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
        Assert.That(fixture.Activations, Is.EqualTo(1));
        Assert.That(fixture.Output.ToString(), Does.Contain("WRITE_DAC"));
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task GrantFailure_StaysReadOnlyWithoutActivationOrAutomaticRetry(int resultValue)
    {
        using var fixture = new Fixture(true, "t\ny\nt\ny\n");
        fixture.Grant.Outcome = (WorkspaceGrantOutcome)resultValue;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
        Assert.That(fixture.Activations, Is.Zero);
        Assert.That(fixture.Grant.Calls, Is.EqualTo(1));
        if ((WorkspaceGrantOutcome)resultValue == WorkspaceGrantOutcome.DidNotFinish)
        {
            await fixture.Coordinator.ChangeAsync(CancellationToken.None);
            Assert.That(fixture.Grant.Calls, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task GrantSuccess_RechecksBeforeActivation_AndDowngradeRejectsOldPlan()
    {
        using var fixture = new Fixture(true, "t\ny\nr\n");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.Grant.Calls, Is.EqualTo(1));
        Assert.That(fixture.State.IsReadOnly, Is.False);
        var guarded = fixture.State.Guard(() => new CountingTool("filesystem.write", fixture.Workspace.Root));
        var plan    = guarded.Prepare(new ChatToolCall("id", "probe", "{}"));
        fixture.Permissions.GrantOnce(plan);
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
        Assert.That(fixture.Permissions.Decide(plan), Is.EqualTo(PermissionDecision.Deny));
        await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.ExecuteAsync(plan, CancellationToken.None));
    }

    [Test]
    public async Task ActivationFailure_DoesNotEnablePatching()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Grant.Ready    = true;
        fixture.FailActivation = true;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
        Assert.That(fixture.Output.ToString(), Does.Contain("activation failed"));
    }

    [Test]
    public void DirectoryIdentity_RejectsReplacement()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows file identity check.");
        using var dir      = new TestTempDir();
        var       path     = dir.CreateDirectory("workspace");
        var       identity = WorkspaceDirectoryIdentity.Capture(path);
        Directory.Move(path, path + "-old");
        Directory.CreateDirectory(path);
        Assert.Throws<InvalidDataException>(identity.EnsureUnchanged);
    }

    [Test]
    public void AuthorizationPayload_IsSourceGenerated_AndProtectsExecutablePath()
    {
        using var dir      = new TestTempDir();
        var       identity = WorkspaceDirectoryIdentity.Capture(dir.Root);
        var request =
            new WorkspaceAclRequest(identity, "S-1-5-21-1-2-3-1001", 1, 123, [Path.Combine(dir.Root, "app.exe")]);
        var json   = JsonSerializer.Serialize(request, WorkspaceAclJsonContext.Default.WorkspaceAclRequest);
        var loaded = JsonSerializer.Deserialize(json, WorkspaceAclJsonContext.Default.WorkspaceAclRequest)!;
        Assert.That(loaded.Directory, Is.EqualTo(identity));
        Assert.That(loaded.CallerSid, Is.EqualTo(request.CallerSid));
        Assert.Throws<InvalidDataException>(() => ProcessWorkspaceAclGrant.ValidateProtectedPaths(identity,
                                                     loaded.ProtectedPaths));
    }

    [Test]
    public void SetupPayload_SkipsMissingMetadata_ProtectsExistingDirectoryAndFile()
    {
        using var workspace = new TestTempDir();
        using var home      = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, workspace.Root,
                                                   new Dictionary<string, string>());
        Assert.That(policy.CreateSetupPayload(home.Components, workspace.Root, "user", true).DenyWritePaths, Is.Empty);
        var git     = workspace.WriteFile(".git", "gitdir: elsewhere");
        var agents  = workspace.CreateDirectory(".agents");
        var payload = policy.CreateSetupPayload(home.Components, workspace.Root, "user", true);
        Assert.That(payload.DenyWritePaths, Is.EquivalentTo(new[] { git, agents }));
        Assert.That(Directory.Exists(Path.Combine(workspace.Root, ".codex")), Is.False);
        Assert.That(File.ReadAllText(git), Is.EqualTo("gitdir: elsewhere"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task GuardedTools_PreserveInstructionCoverageAndApprovalFlow(bool trusted)
    {
        using var dir  = new TestTempDir();
        var       file = dir.WriteFile("input.txt", "old\n");
        dir.WriteFile("AGENTS.md", "Keep unrelated files unchanged.");
        var state = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        state.SetReadOnly(!trusted);
        var permissions = new PermissionEngine(dir.Root, workspaceReadOnly : () => state.IsReadOnly);
        var read        = new ReadFileTool(dir.Workspace);
        var patch       = new ApplyPatchTool(dir.Workspace);
        var model       = new FakeChatClient();
        model.Enqueue(FakeChatClient.ToolCall("read_file", "{\"path\":\"input.txt\"}"));
        model.Enqueue(FakeChatClient.ToolCall("apply_patch", new JsonObject
        {
            ["patch"] = "--- a/input.txt\n+++ b/input.txt\n@@ -1,1 +1,1 @@\n-old\n+new\n"
        }.ToJsonString()));
        model.Enqueue(FakeChatClient.Text("finished"));
        var loader       = new ProjectInstructionLoader(dir.Workspace);
        var instructions = await loader.LoadAsync(null, [], CancellationToken.None);
        var approver     = new AllowOnceApprover();
        using var host = new ChatSessionHost(dir.Root,
                                             new SessionModelIdentity("test", "https://example.invalid/v1",
                                                                      ChatApiKind.ChatCompletions),
                                             new AgentOptions
                                                 { Model = "test", MaxAgentSteps = 5, DefaultToolTimeoutSeconds = 30 },
                                             model,
                                             new ToolRegistry([state.Guard(() => read), state.Guard(() => patch)]),
                                             "test system", permissions,
                                             approver, instructionLoader : loader, initialInstructions : instructions);
        var run = await host.SubmitTurnAsync("Read and update the file.", CancellationToken.None);
        Assert.That(run.Result!.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(await File.ReadAllTextAsync(file), Is.EqualTo(trusted ? "new\n" : "old\n"));
        Assert.That(approver.Calls, Is.EqualTo(trusted ? 1 : 0));
        Assert.That(run.Result.ToolExecutions, Is.EqualTo(trusted ? 2 : 1));
    }

    [Test]
    public async Task TrustCommand_DoesNotCreateSessionOrModelRequest()
    {
        var       changes      = 0;
        using var cancellation = new ChatCancellation(false);
        using var console =
            new ChatSessionConsole(_ => throw new InvalidOperationException("Unexpected session creation"),
                                   new StringReader("/trust\n"), new StringWriter(), cancellation)
            {
                WorkspaceTrustHandler = _ =>
                {
                    changes++;
                    return Task.CompletedTask;
                }
            };
        Assert.That(await console.RunAsync(), Is.Zero);
        Assert.That(changes, Is.EqualTo(1));
        Assert.That(console.TryGetSession(out _), Is.False);
    }

    [Test]
    public async Task GrantProtocol_BoundsFramesAndRoundTripsWithoutReflection()
    {
        using var stream   = new MemoryStream();
        var       response = new WorkspaceAclResponse(false, "access denied");
        await WorkspaceGrantChannel.WriteAsync(stream, response, WorkspaceAclJsonContext.Default.WorkspaceAclResponse,
                                               CancellationToken.None);
        stream.Position = 0;
        Assert.That(await WorkspaceGrantChannel.ReadAsync(stream, WorkspaceAclJsonContext.Default.WorkspaceAclResponse,
                                                          CancellationToken.None), Is.EqualTo(response));
        using var oversized = new MemoryStream([255, 255, 255, 127]);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceGrantChannel.ReadAsync(oversized,
                                                                WorkspaceAclJsonContext.Default.WorkspaceAclResponse,
                                                                CancellationToken.None));
        using var truncated = new MemoryStream("d\0\0\0{"u8.ToArray()
                                              );
        await Assert.ThrowsAsync<EndOfStreamException>(() => WorkspaceGrantChannel.ReadAsync(truncated,
                                                                WorkspaceAclJsonContext.Default.WorkspaceAclResponse,
                                                                CancellationToken.None));
        Assert.That(WorkspaceGrantChannel.IsValidName("other-pipe"), Is.False);
        Assert.That(WorkspaceGrantChannel.IsValidName("tinyharness-workspace-acl-" + Guid.NewGuid().ToString("N")),
                    Is.True);
    }

    private sealed class AllowOnceApprover : IApprovalProvider
    {
        public int Calls;

        public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApprovalAction.AllowOnce);
        }
    }

    private sealed class FakeGrant : IWorkspaceAclGrant
    {
        public int                   Calls;
        public WorkspaceGrantOutcome Outcome = WorkspaceGrantOutcome.Completed;
        public bool                  Ready;

        public Task<bool> CanChangePermissionsAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken) => Task.FromResult(Ready);

        public Task<WorkspaceGrantOutcome> GrantAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
        {
            Calls++;
            if (Outcome == WorkspaceGrantOutcome.Completed) Ready = true;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestTempDir  _storage = new();
        public readonly  FakeGrant    Grant    = new();
        public readonly  StringReader Input;
        public readonly  StringWriter Output    = new();
        public readonly  TestTempDir  Workspace = new();
        public           int          Activations;
        public           bool         FailActivation;

        public Fixture(bool interactive, string input)
        {
            Input       = new StringReader(input);
            State       = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(Workspace.Root));
            Permissions = new PermissionEngine(Workspace.Root, workspaceReadOnly : () => State.IsReadOnly);
            Coordinator = new WorkspaceAccessCoordinator(State, new WorkspaceTrustStore(TrustPath, [Workspace.Root]),
                                                         Permissions, Input, Output, interactive, true, Grant, () =>
                                                         {
                                                             Activations++;
                                                             if (FailActivation)
                                                                 throw new
                                                                     InvalidOperationException("fake activation failure");
                                                             return Task.FromResult((new ShellTool(Workspace.Workspace),
                                                                          ProcessExecutionPolicy.Host()));
                                                         });
        }

        public WorkspaceAccessState       State       { get; }
        public PermissionEngine           Permissions { get; }
        public WorkspaceAccessCoordinator Coordinator { get; }
        public string                     TrustPath   => Path.Combine(_storage.Root, "trust.json");

        public void Dispose()
        {
            Input.Dispose();
            Output.Dispose();
            Workspace.Dispose();
            _storage.Dispose();
        }
    }

    private sealed class CountingTool(string capability, string root) : ITool
    {
        public int Executions;

        public ToolDefinition Definition =>
            new() { Name = "probe", Description = "test", Parameters = new JsonObject() };

        public ToolPreparation Prepare(ChatToolCall call) => new()
        {
            ToolName    = "probe",
            CallId      = call.Id,
            Arguments   = new JsonObject(),
            Capability  = capability,
            Summary     = "probe",
            TargetPaths = [root]
        };

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(new ToolResult { Succeeded = true });
        }
    }
}
