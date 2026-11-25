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
using IWorkspaceAclGrant = TinyHarness.Cli.Services.IWorkspaceAclGrant;
using ProcessWorkspaceAclGrant = TinyHarness.Cli.Services.ProcessWorkspaceAclGrant;
using WorkspaceGrantOutcome = TinyHarness.Cli.Services.WorkspaceGrantOutcome;
using WorkspaceGrantChannel = TinyHarness.Cli.Services.WorkspaceGrantChannel;
using WorkspaceGrantFailureException = TinyHarness.Core.Services.Runtime.WorkspaceGrantFailureException;

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
        using var dir   = new TestTempDir();
        var       state = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var       inner = new CountingTool(capability, dir.Root);
        var       tool  = state.Guard(() => inner);
        var       plan  = tool.Prepare(new ChatToolCall("id", "probe", "{}"));
        var permissions = new PermissionEngine(dir.Root, workspaceReadOnly : () => state.IsReadOnly,
                                               resourcePolicy : new TestResourcePolicy(dir.Root));
        permissions.Grants.Add(GrantScope.SingleOperation, plan);
        permissions.Grants.Add(GrantScope.Turn, plan);
        Assert.That(permissions.Evaluate(plan).Decision, Is.EqualTo(PermissionDecision.Deny));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(plan, CancellationToken.None));
        Assert.That(inner.Executions, Is.Zero);
        state.SetReadOnly(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(plan, CancellationToken.None));
        var fresh = tool.Prepare(new ChatToolCall("id2", "probe", "{}"));
        Assert.That(permissions.Grants.Peek(fresh), Is.Null);
        Assert.That(permissions.Evaluate(fresh).Decision,
                    Is.EqualTo(capability == "future.unknown" ? PermissionDecision.Ask : PermissionDecision.Allow));
        Assert.That((await tool.ExecuteAsync(fresh, CancellationToken.None)).Succeeded, Is.True);
    }

    [Test]
    public void ReadOnly_CommandRuleCannotEnableShell()
    {
        using var dir   = new TestTempDir();
        var       shell = new ShellTool(dir.Workspace);
        var plan = shell.Prepare(new ChatToolCall("id", "shell",
                                                  "{\"mode\":\"direct\",\"executable\":\"dotnet\",\"arguments\":[\"test\"]}"));
        // 只读工作区硬限制独立于命令规则（引擎不再消费 commandRules）。
        var permissions = new PermissionEngine(dir.Root, workspaceReadOnly : () => true);
        Assert.That(permissions.Evaluate(plan).Decision, Is.EqualTo(PermissionDecision.Deny));
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
        Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        Assert.That(fixture.Grant.IsReadyCalls, Is.Zero);
        Assert.That(fixture.Grant.CanChangeCalls, Is.Zero);
        Assert.That(fixture.Grant.PrepareCalls, Is.Zero);
        Assert.That(File.Exists(fixture.TrustPath), Is.False);
        Assert.That(await fixture.Input.ReadLineAsync(), Is.EqualTo("t"));
    }

    [Test]
    public async Task BindSessionAsync_ActivatesShellWithTheBoundPermissionRegistry()
    {
        using var fixture = new Fixture(true, string.Empty);
        fixture.State.SetReadOnly(false);
        var sessionPermissions = new PermissionEngine(
                                                      fixture.Workspace.Root,
                                                      workspaceReadOnly : () => fixture.State.IsReadOnly,
                                                      resourcePolicy : new TestResourcePolicy(fixture.Workspace.Root));

        await fixture.Coordinator.BindSessionAsync(sessionPermissions, CancellationToken.None);

        Assert.That(fixture.ActivatedGrants, Is.SameAs(sessionPermissions.Grants));
        Assert.That(fixture.ActivatedGrants, Is.Not.SameAs(fixture.Permissions.Grants));
    }

    [Test]
    public async Task Startup_NewAndPersistedTrust_ActivateWithoutAclChecksOrUac_AndReadOnlyToolsWork()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Workspace.WriteFile("notes.txt", "hello-readonly");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;

        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.State.IsReadOnly, Is.False);
            Assert.That(fixture.Activations, Is.EqualTo(1));
            Assert.That(fixture.Grant.IsReadyCalls, Is.Zero);
            Assert.That(fixture.Grant.CanChangeCalls, Is.Zero);
            Assert.That(fixture.Grant.PrepareCalls, Is.Zero);
            Assert.That(fixture.Grant.ScopeCalls, Is.Zero);
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
            Assert.That(fixture.Output.ToString(), Does.Not.Contain("[a] repair workspace ACL"));
        });

        var readTool = fixture.State.Guard(() => new ReadFileTool(fixture.Workspace.Workspace));
        var readPlan = readTool.Prepare(new ChatToolCall("r1", "read_file", "{\"path\":\"notes.txt\"}"));
        var readRes  = await readTool.ExecuteAsync(readPlan, CancellationToken.None);
        Assert.That(readRes.Content, Does.Contain("hello-readonly"));

        using var second = new Fixture(true, string.Empty, sharedWorkspace : fixture.Workspace,
                                       sharedTrustPath : fixture.TrustPath);
        second.Grant.Ready                = false;
        second.Grant.CanChangePermissions = false;
        await second.Coordinator.InitializeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(second.State.IsReadOnly, Is.False);
            Assert.That(second.Activations, Is.EqualTo(1));
            Assert.That(second.Grant.IsReadyCalls, Is.Zero);
            Assert.That(second.Grant.CanChangeCalls, Is.Zero);
            Assert.That(second.Grant.PrepareCalls, Is.Zero);
            Assert.That(second.Grant.ScopeCalls, Is.Zero);
            Assert.That(second.Grant.GrantCalls, Is.Zero);
        });
    }

    [Test]
    public async Task TrustDowngrade_RevokesWriteAccessAndRejectsOldPlan()
    {
        using var fixture = new Fixture(true, "t\nr\n");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.False);
        var guarded = fixture.State.Guard(() => new CountingTool("filesystem.write", fixture.Workspace.Root));
        var plan    = guarded.Prepare(new ChatToolCall("id", "probe", "{}"));
        fixture.Permissions.Grants.Add(GrantScope.SingleOperation, plan);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
            Assert.That(fixture.State.IsReadOnly, Is.True);
            Assert.That(fixture.Permissions.Evaluate(plan).Decision, Is.EqualTo(PermissionDecision.Deny));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => guarded.ExecuteAsync(plan, CancellationToken.None));
    }

    [Test]
    public async Task TrustPrompt_ReadOnlyState_DoesNotExposeRepairOption()
    {
        using var fixture = new Fixture(true, "r\na\n");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Output.ToString(), Does.Not.Contain("[a] repair workspace ACL"));
            Assert.That(fixture.Output.ToString(), Does.Contain("Unrecognized choice; access was not changed."));
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
            Assert.That(fixture.Grant.PrepareCalls, Is.Zero);
        });
    }

    [Test]
    public async Task WorkspaceWriteShell_NormalPrivilegePreparable_PreparesOnce_ReusesCache_ReverifiesOnPolicyChange()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = true;
        fixture.Grant.PrepareSetsReady     = true;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.Multiple(() =>
        {
            Assert.That(inner.Executions, Is.EqualTo(1));
            Assert.That(fixture.Grant.IsReadyCalls, Is.EqualTo(2));
            Assert.That(fixture.Grant.ScopeCalls, Is.EqualTo(1));
            Assert.That(fixture.Grant.PrepareCalls, Is.EqualTo(1));
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        });

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.Multiple(() =>
        {
            Assert.That(inner.Executions, Is.EqualTo(2));
            Assert.That(fixture.Grant.IsReadyCalls, Is.EqualTo(2));
            Assert.That(fixture.Grant.PrepareCalls, Is.EqualTo(1));
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        });

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root, networkAccessEnabled : true);
        Assert.Multiple(() =>
        {
            Assert.That(inner.Executions, Is.EqualTo(3));
            Assert.That(fixture.Grant.IsReadyCalls, Is.EqualTo(3));
            Assert.That(fixture.Grant.PrepareCalls, Is.EqualTo(1));
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        });
    }

    [Test]
    public async Task WorkspaceWriteShell_DirectoryReplaced_RejectsExecutionWithoutStartingBackend()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows file identity check.");
        using var parent        = new TestTempDir();
        var       workspacePath = parent.CreateDirectory("ws");
        using var storage       = new TestTempDir();
        var       trustPath     = Path.Combine(storage.Root, "trust.json");
        var       state         = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(workspacePath));
        var       permissions   = new PermissionEngine(workspacePath, workspaceReadOnly : () => state.IsReadOnly);
        var       grant         = new FakeGrant { Ready = true };
        using var input         = new StringReader("t\n");
        using var output        = new StringWriter();
        using var coordinator = new WorkspaceAccessCoordinator(state, new WorkspaceTrustStore(trustPath, [workspacePath]),
                                                             permissions, input, output, true, true, grant,
                                                             _ => Task.FromResult((
                                                                 new ShellTool(new Workspace(workspacePath)) as ITool,
                                                                 ProcessExecutionPolicy.Host())));
        await coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = coordinator.WrapSandboxBackend(inner);
        await ExecuteSampleAsync(wrapped, workspacePath);
        Assert.That(inner.Executions, Is.EqualTo(1));

        Directory.Move(workspacePath, workspacePath + "-moved");
        Directory.CreateDirectory(workspacePath);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ExecuteSampleAsync(wrapped, workspacePath));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.InnerException, Is.TypeOf<InvalidDataException>());
            Assert.That(ex.Message, Does.Contain(workspacePath));
            Assert.That(ex.Message, Does.Contain("identity-validation"));
            Assert.That(ex.Message, Does.Contain("No command was started"));
            Assert.That(inner.Executions, Is.EqualTo(1));
            Assert.That(grant.GrantCalls, Is.Zero);
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task WorkspaceWriteShell_NormalPrivilegeFails_ReportsDirectoryAndStage_WithoutStartingCommandOrUac(
        bool interactive)
    {
        using var fixture = new Fixture(interactive, interactive ? "t\n" : string.Empty);
        if (!interactive)
        {
            var store = new WorkspaceTrustStore(fixture.TrustPath, [fixture.Workspace.Root]);
            await store.SaveAsync(fixture.State.Identity, true, CancellationToken.None);
        }

        fixture.Grant.Ready            = false;
        fixture.Grant.PrepareException = new UnauthorizedAccessException("normal-privilege denied");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ExecuteSampleAsync(wrapped,
                                                                                            fixture.Workspace.Root));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(fixture.Workspace.Root));
            Assert.That(ex.Message, Does.Contain("normal-privilege-preparation"));
            Assert.That(ex.Message, Does.Contain("normal-privilege denied"));
            Assert.That(ex.Message, Does.Contain("No command was started"));
            Assert.That(ex.Message, Does.Contain("/trust"));
            if (!interactive) Assert.That(ex.Message, Does.Contain("Non-interactive"));
            Assert.That(inner.Executions, Is.Zero);
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        });
    }

    [Test]
    public async Task WorkspaceWriteShell_CorruptCapSidJsonException_ReportsErrorWithoutUacOrCommandStart()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Grant.IsReadyException = new JsonException("corrupt cap_sid");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ExecuteSampleAsync(wrapped,
                                                                                            fixture.Workspace.Root));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.InnerException, Is.TypeOf<JsonException>());
            Assert.That(ex.Message, Does.Contain(fixture.Workspace.Root));
            Assert.That(ex.Message, Does.Contain("readiness-check"));
            Assert.That(ex.Message, Does.Contain("corrupt cap_sid"));
            Assert.That(ex.Message, Does.Contain("No command was started"));
            Assert.That(inner.Executions, Is.Zero);
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
        });
    }

    [Test]
    public async Task WorkspaceWriteShell_PostRefreshAclFailure_ClearsCacheAndRecordsPendingRepairForTrust()
    {
        using var fixture = new Fixture(true, "t\nr\n");
        fixture.Grant.Ready = true;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var problem = new WorkspaceAclProblem(fixture.Workspace.Root,
                                              Path.Combine(fixture.Workspace.Root, "sub"),
                                              WorkspaceAclProblemCategory.MissingInheritedAccess,
                                              [], false, null);
        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.That(fixture.Grant.IsReadyCalls, Is.EqualTo(1));

        inner.ExceptionToThrow = new WorkspaceAclPreparationException(problem);
        await Assert.ThrowsAsync<WorkspaceAclPreparationException>(() =>
                                                                       ExecuteSampleAsync(wrapped,
                                                                                          fixture.Workspace.Root));

        // 缓存必须已失效：下一次执行会重新调用 IsReadyAsync，而不是复用旧准备态。
        inner.ExceptionToThrow = null;
        fixture.Grant.Ready    = true;
        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.That(fixture.Grant.IsReadyCalls, Is.EqualTo(2));

        // 再次触发刷新后 ACL 失败，验证 /trust 提示包含待修复目录与阶段。
        inner.ExceptionToThrow = new WorkspaceAclPreparationException(problem);
        await Assert.ThrowsAsync<WorkspaceAclPreparationException>(() =>
                                                                       ExecuteSampleAsync(wrapped,
                                                                                          fixture.Workspace.Root));

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Output.ToString(), Does.Contain("post-refresh-validation"));
            Assert.That(fixture.Output.ToString(), Does.Contain(fixture.Workspace.Root));
            Assert.That(fixture.Output.ToString(), Does.Contain("[a] repair workspace ACL"));
        });
    }

    [Test]
    public async Task TrustRepair_NormalPrivilegeSucceeds_VerifiesAndAllowsRetryWithoutReplay()
    {
        using var fixture = new Fixture(true, "t\na\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = true;
        fixture.Grant.PrepareException     = new UnauthorizedAccessException("transient permission lock");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                ExecuteSampleAsync(wrapped, fixture.Workspace.Root));
        Assert.That(inner.Executions, Is.Zero);

        fixture.Grant.PrepareException = null;
        fixture.Grant.PrepareSetsReady = true;

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
            Assert.That(inner.Executions, Is.Zero);
            Assert.That(fixture.Output.ToString(), Does.Contain("normal-privilege-preparation"));
            Assert.That(fixture.Output.ToString(), Does.Contain("completed without elevation and was verified"));
            Assert.That(fixture.Output.ToString(), Does.Contain("No command was replayed"));
        });

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.That(inner.Executions, Is.EqualTo(1));
    }

    [Test]
    public async Task TrustRepair_ElevationDeclinedByUser_DoesNotCallGrantAsync_AndKeepsSessionUsable()
    {
        using var fixture = new Fixture(true, "t\na\nn\nr\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.Zero);
            Assert.That(fixture.State.IsReadOnly, Is.False);
            Assert.That(fixture.Output.ToString(), Does.Contain("READ_CONTROL | WRITE_DAC"));
            Assert.That(fixture.Output.ToString(), Does.Contain("Request Windows UAC for this directory? [y/N]:"));
            Assert.That(fixture.Output.ToString(), Does.Contain("Directory authorization declined"));
        });

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
    }

    [Test]
    public async Task TrustRepair_ElevationCompletedAndVerified_AllowsSubsequentCommandRetry()
    {
        using var fixture = new Fixture(true, "t\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        fixture.Grant.Outcome              = WorkspaceGrantOutcome.Completed;
        fixture.Grant.GrantSetsReady       = true;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Grant.IsReadyCalls, Is.GreaterThanOrEqualTo(2));
            Assert.That(inner.Executions, Is.Zero);
            Assert.That(fixture.Output.ToString(),
                        Does.Contain("Directory authorization completed and workspace ACLs were verified"));
            Assert.That(fixture.Output.ToString(), Does.Contain("No command was replayed"));
        });

        await ExecuteSampleAsync(wrapped, fixture.Workspace.Root);
        Assert.That(inner.Executions, Is.EqualTo(1));
    }

    [Test]
    public async Task TrustRepair_ElevationCompletedButNotReady_DoesNotMarkCacheReady()
    {
        using var fixture = new Fixture(true, "t\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        fixture.Grant.Outcome              = WorkspaceGrantOutcome.Completed;
        fixture.Grant.GrantSetsReady       = false;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Output.ToString(), Does.Contain("verification is still incomplete"));
        });

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);
        fixture.Grant.PrepareSetsReady = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                ExecuteSampleAsync(wrapped, fixture.Workspace.Root));
        Assert.That(fixture.Grant.PrepareCalls, Is.EqualTo(1));
        Assert.That(inner.Executions, Is.Zero);
    }

    [TestCase(1)]
    [TestCase(2)]
    public async Task TrustRepair_DeclinedOrFailedOutcome_AllowsSubsequentTrustRetry(int outcomeValue)
    {
        var outcome = (WorkspaceGrantOutcome)outcomeValue;
        using var fixture = new Fixture(true, "t\na\ny\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        fixture.Grant.Outcome              = outcome;
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Output.ToString(), Does.Contain($"Directory authorization: {outcome}"));
        });

        fixture.Grant.Outcome        = WorkspaceGrantOutcome.Completed;
        fixture.Grant.GrantSetsReady = true;
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(2));
            Assert.That(fixture.Output.ToString(), Does.Contain("workspace ACLs were verified"));
        });
    }

    [Test]
    public async Task TrustRepair_GrantFailureException_AllowsSubsequentTrustRetry()
    {
        using var fixture = new Fixture(true, "t\na\ny\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        fixture.Grant.GrantException       = new WorkspaceGrantFailureException("helper exited with error");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Output.ToString(), Does.Contain("helper exited with error"));
        });

        fixture.Grant.GrantException = null;
        fixture.Grant.Outcome        = WorkspaceGrantOutcome.Completed;
        fixture.Grant.GrantSetsReady = true;
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(2));
        Assert.That(fixture.Output.ToString(), Does.Contain("workspace ACLs were verified"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task TrustRepair_DidNotFinishOrInterruptedDuringGrant_LocksUncertainState(bool cancelDuringGrant)
    {
        using var fixture = new Fixture(true, "t\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        if (cancelDuringGrant)
            fixture.Grant.GrantException = new OperationCanceledException("cancelled mid-grant");
        else
            fixture.Grant.Outcome = WorkspaceGrantOutcome.DidNotFinish;

        await fixture.Coordinator.InitializeAsync(CancellationToken.None);
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ExecuteSampleAsync(wrapped,
                                                                                            fixture.Workspace.Root));
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Output.ToString(), Does.Contain("Directory authorization may still be running"));
            Assert.That(ex!.Message, Does.Contain("may still be running"));
            Assert.That(inner.Executions, Is.Zero);
        });
    }

    [Test]
    public async Task TrustRepair_CancelledBeforeGrantStarts_DoesNotLockUncertainState()
    {
        using var fixture = new Fixture(true, "t\na\na\ny\n");
        fixture.Grant.Ready                = false;
        fixture.Grant.CanChangePermissions = false;
        fixture.Grant.IsReadyException     = new OperationCanceledException("cancelled pre-check");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.That(fixture.Output.ToString(), Does.Contain("Workspace ACL repair was cancelled"));

        fixture.Grant.IsReadyException = null;
        fixture.Grant.Outcome          = WorkspaceGrantOutcome.Completed;
        fixture.Grant.GrantSetsReady   = true;
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(fixture.Grant.GrantCalls, Is.EqualTo(1));
            Assert.That(fixture.Output.ToString(), Does.Contain("workspace ACLs were verified"));
        });
    }

    [Test]
    public async Task WorkspaceWriteShell_ScopeValidationFails_ReportsStageWithoutStartingCommand()
    {
        using var fixture = new Fixture(true, "t\n");
        fixture.Grant.Ready          = false;
        fixture.Grant.ScopeException = new InvalidDataException("executable inside write root");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        var inner   = new CountingProcessBackend();
        var wrapped = fixture.Coordinator.WrapSandboxBackend(inner);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                         ExecuteSampleAsync(wrapped,
                                                                                            fixture.Workspace.Root));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.InnerException, Is.TypeOf<InvalidDataException>());
            Assert.That(ex.Message, Does.Contain("scope-validation"));
            Assert.That(ex.Message, Does.Contain("executable inside write root"));
            Assert.That(ex.Message, Does.Contain("No command was started"));
            Assert.That(inner.Executions, Is.Zero);
            Assert.That(fixture.Grant.PrepareCalls, Is.Zero);
        });
    }

    [Test]
    public async Task TrustRepair_CorruptCapSidJsonException_ReportsErrorWithoutCrashingLoop()
    {
        using var fixture = new Fixture(true, "t\na\na\nr\n");
        fixture.Grant.IsReadyException = new JsonException("corrupt cap_sid");
        await fixture.Coordinator.InitializeAsync(CancellationToken.None);

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(fixture.Output.ToString(), Does.Contain("Workspace ACL repair failed"));
            Assert.That(fixture.Output.ToString(), Does.Contain(fixture.Workspace.Root));
            Assert.That(fixture.Output.ToString(), Does.Contain("corrupt cap_sid"));
        });

        fixture.Grant.IsReadyException              = null;
        fixture.Grant.Ready                         = false;
        fixture.Grant.CanChangePermissionsException = new JsonException("corrupt cap_sid in probe");
        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.That(fixture.Output.ToString(), Does.Contain("corrupt cap_sid in probe"));

        await fixture.Coordinator.ChangeAsync(CancellationToken.None);
        Assert.That(fixture.State.IsReadOnly, Is.True);
    }

    [Test]
    public async Task ActivationFailure_DoesNotEnablePatching()
    {
        using var fixture = new Fixture(true, "t\n");
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
        var permissions = new PermissionEngine(dir.Root, workspaceReadOnly : () => state.IsReadOnly,
                                               resourcePolicy : new TestResourcePolicy(dir.Root));
        var read  = new ReadFileTool(dir.Workspace);
        var patch = new ApplyPatchTool(dir.Workspace);
        var model = new FakeChatClient();
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
        // 可信工作区的基线写入已自动放行；不可信工作区由硬限制拒绝，两者都不再询问。
        Assert.That(approver.Calls, Is.EqualTo(0));
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

    private static Task<ProcessExecutionResult> ExecuteSampleAsync(
        IProcessExecutionBackend backend, string workingDirectory, bool networkAccessEnabled = false)
    {
        var execution = new PreparedProcessExecution("cmd.exe", ["/c", "echo", "ok"], workingDirectory, 10, false,
                                                     null, networkAccessEnabled);
        return backend.ExecuteAsync(execution, NoopOutputCapture.Instance, NoopOutputCapture.Instance,
                                    CancellationToken.None);
    }

    private sealed class NoopOutputCapture : IProcessOutputCapture
    {
        public static readonly NoopOutputCapture Instance = new();

        public ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public CapturedProcessOutput Complete() => new(string.Empty, false, 0, null);

        public void Discard()
        {
        }
    }

    private sealed class CountingProcessBackend : IProcessExecutionBackend
    {
        public int        Executions;
        public Exception? ExceptionToThrow;

        public Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                         IProcessOutputCapture    stdoutCapture,
                                                         IProcessOutputCapture    stderrCapture,
                                                         CancellationToken        cancellationToken)
        {
            if (ExceptionToThrow is not null) return Task.FromException<ProcessExecutionResult>(ExceptionToThrow);
            Executions++;
            return Task.FromResult(new ProcessExecutionResult(0, false, null));
        }
    }

    private sealed class AllowOnceApprover : IApprovalProvider
    {
        public int Calls;

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApprovalResolution.Granted(GrantScope.SingleOperation));
        }
    }

    private sealed class FakeGrant : IWorkspaceAclGrant
    {
        public int                   CanChangeCalls;
        public bool                  CanChangePermissions;
        public Exception?            CanChangePermissionsException;
        public int                   GrantCalls;
        public Exception?            GrantException;
        public bool                  GrantSetsReady = true;
        public int                   IsReadyCalls;
        public Exception?            IsReadyException;
        public WorkspaceGrantOutcome Outcome = WorkspaceGrantOutcome.Completed;
        public int                   PrepareCalls;
        public Exception?            PrepareException;
        public bool                  PrepareSetsReady = true;
        public bool                  Ready;
        public int                   ScopeCalls;
        public Exception?            ScopeException;

        public Task<bool> CanChangePermissionsAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
        {
            CanChangeCalls++;
            return CanChangePermissionsException is not null
                ? Task.FromException<bool>(CanChangePermissionsException)
                : Task.FromResult(CanChangePermissions);
        }

        public Task PrepareWithoutElevationAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            if (PrepareException is not null) return Task.FromException(PrepareException);
            if (PrepareSetsReady) Ready = true;
            return Task.CompletedTask;
        }

        public Task<bool> IsReadyAsync(WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
        {
            IsReadyCalls++;
            return IsReadyException is not null
                ? Task.FromException<bool>(IsReadyException)
                : Task.FromResult(Ready);
        }

        public void ValidatePreparationScope(WorkspaceDirectoryIdentity identity)
        {
            ScopeCalls++;
            if (ScopeException is not null) throw ScopeException;
        }

        public Task<WorkspaceGrantOutcome> GrantAsync(
            WorkspaceDirectoryIdentity identity, CancellationToken cancellationToken)
        {
            GrantCalls++;
            if (GrantException is not null) return Task.FromException<WorkspaceGrantOutcome>(GrantException);
            if (Outcome == WorkspaceGrantOutcome.Completed && GrantSetsReady) Ready = true;
            return Task.FromResult(Outcome);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly  FakeGrant    Grant = new();
        public readonly  StringReader Input;
        public readonly  StringWriter Output = new();
        public readonly  TestTempDir  Workspace;
        private readonly bool         _ownsWorkspace;
        private readonly TestTempDir? _storage;
        public           int          Activations;
        public           bool         FailActivation;

        public Fixture(bool         interactive, string input, TestTempDir? sharedWorkspace = null,
                       string?      sharedTrustPath = null)
        {
            Input          = new StringReader(input);
            _ownsWorkspace = sharedWorkspace is null;
            Workspace      = sharedWorkspace ?? new TestTempDir();
            if (sharedTrustPath is null)
            {
                _storage  = new TestTempDir();
                TrustPath = Path.Combine(_storage.Root, "trust.json");
            }
            else
            {
                TrustPath = sharedTrustPath;
            }

            State = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(Workspace.Root));
            Permissions = new PermissionEngine(Workspace.Root, workspaceReadOnly : () => State.IsReadOnly,
                                               resourcePolicy : new TestResourcePolicy(Workspace.Root));
            Coordinator = new WorkspaceAccessCoordinator(State, new WorkspaceTrustStore(TrustPath, [Workspace.Root]),
                                                         Permissions, Input, Output, interactive, true, Grant,
                                                         grants =>
                                                         {
                                                             Activations++;
                                                             ActivatedGrants = grants;
                                                             if (FailActivation)
                                                                 throw new
                                                                     InvalidOperationException("fake activation failure");
                                                             return Task.FromResult((new ShellTool(Workspace.Workspace)
                                                                        as ITool,
                                                                    ProcessExecutionPolicy.Host()));
                                                         });
        }

        public GrantRegistry? ActivatedGrants { get; private set; }

        public WorkspaceAccessState       State       { get; }
        public PermissionEngine           Permissions { get; }
        public WorkspaceAccessCoordinator Coordinator { get; }
        public string                     TrustPath   { get; }

        public void Dispose()
        {
            Coordinator.Dispose();
            Input.Dispose();
            Output.Dispose();
            if (_ownsWorkspace) Workspace.Dispose();
            _storage?.Dispose();
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
