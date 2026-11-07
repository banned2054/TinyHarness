using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     ShellTool 的 R-identity 冻结链路测试：活动快照在 Prepare 解析为 grant 范围并追加进
///     策略身份，scope 随冻结计划走；空快照回落基线身份，错误组合 fail closed。
///     Tests for the ShellTool R-identity freeze: the active snapshot resolves into a grant
///     scope at Prepare and joins the policy identity, the scope travels with the frozen plan,
///     an empty snapshot falls back to the baseline identity, and wrong compositions fail closed.
/// </summary>
public class ShellToolGrantFreezeTests
{
    private const string BaselineIdentity =
        "backend=windows-sandbox\npolicyVersion=6\nkind=workspace-write";

    [Test]
    public void Prepare_WithActiveSnapshot_FreezesScopeAndExtendsPolicyIdentity()
    {
        using var home = new SandboxTestHome();
        var grantRoot = Directory.CreateDirectory(Path.Combine(home.Components.SandboxHome,
                                                               "grant-root")).FullName;
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });
        var source = new FakeCapabilitySource(new ActiveCapabilitySnapshot([], [grantRoot], []));
        var tool = new ShellTool(new Workspace(home.Components.SandboxHome), processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : SandboxPolicy(),
                                 activeCapabilities : source,
                                 grantContext : new SandboxGrantContext(policy, home.Components));

        var preparation = tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" }));

        // 冻结身份如实携带 grant 段：审批文本因此展示生效范围。
        Assert.That(preparation.ExecutionPolicy, Does.Contain("grantWriteRoots="));
        Assert.That(preparation.ExecutionPolicy,
                    Does.Contain(SandboxCapabilitySidStore.CanonicalRootKey(grantRoot)));
        Assert.That(preparation.SessionGrantConstraint, Does.Contain("grantWriteRoots="));

        var plan = TestAssert.TypeOf<PreparedProcessExecution>(preparation.ExecutionPlan);
        Assert.That(plan.GrantScope is { } scope && scope.WriteRoots.Contains(grantRoot), Is.True);
    }

    [Test]
    public void Prepare_WithEmptySnapshot_KeepsBaselineIdentityAndEmptyFrozenScope()
    {
        using var home = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });
        var source = new FakeCapabilitySource(new ActiveCapabilitySnapshot([], [], []));
        var tool = new ShellTool(new Workspace(home.Components.SandboxHome), processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : SandboxPolicy(),
                                 activeCapabilities : source,
                                 grantContext : new SandboxGrantContext(policy, home.Components));

        var preparation = tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" }));

        // 撤销（空快照）即回基线身份；GrantScope 恒存——空范围是有效冻结事实，
        // 派发复核据此区分"冻结时无 grant"与"计划未携带冻结范围"。
        Assert.That(preparation.ExecutionPolicy, Is.EqualTo(SandboxPolicy().PolicyIdentity));
        Assert.That(preparation.ExecutionPolicy, Does.Not.Contain("grant"));
        var plan = TestAssert.TypeOf<PreparedProcessExecution>(preparation.ExecutionPlan);
        Assert.That(plan.GrantScope is { } scope && scope.IsEmpty, Is.True);
    }

    [Test]
    public void Prepare_ActiveSourceWithoutGrantContext_FailsClosed()
    {
        using var home = new SandboxTestHome();
        var policy = SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite,
                                                   home.Components.SandboxHome,
                                                   new Dictionary<string, string>
                                                   {
                                                       ["TEMP"] = home.Components.SandboxHome
                                                   });
        var tool = new ShellTool(new Workspace(home.Components.SandboxHome), processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : SandboxPolicy(),
                                 activeCapabilities : new FakeCapabilitySource(new ActiveCapabilitySnapshot([], [], [])));

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                   tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" })));
        Assert.That(ex.Message, Does.Contain("sandbox grant context"));
    }

    [Test]
    public void Prepare_ActiveSourceWithHostPolicy_FailsClosed()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, processBackend : new FakeProcessExecutionBackend(),
                                 executionPolicy : ProcessExecutionPolicy.Host(),
                                 activeCapabilities : new FakeCapabilitySource(new ActiveCapabilitySnapshot([], [], [])),
                                 grantContext : new SandboxGrantContext(
                                     SandboxPolicyResolver.Resolve(SandboxPolicyKind.WorkspaceWrite, dir.Root,
                                                                   new Dictionary<string, string>
                                                                   {
                                                                       ["TEMP"] = dir.Root
                                                                   }),
                                     new SandboxTestHome().Components));

        var ex = TestAssert.Throws<InvalidOperationException>(() =>
                   tool.Prepare(Call("shell", new { mode = "direct", executable = "cmd.exe" })));
        Assert.That(ex.Message, Does.Contain("Windows sandbox execution policy"));
    }

    /// <summary>构造带沙箱身份的执行策略。 Builds an execution policy carrying the sandbox identity.</summary>
    private static ProcessExecutionPolicy SandboxPolicy() => new()
    {
        Backend        = ProcessExecutionBackendKind.WindowsSandbox,
        DisplayName    = "windows-sandbox: workspace-write",
        PolicyIdentity = BaselineIdentity
    };

    /// <summary>测试用活动快照来源：按构造返回固定快照。 Fixed-snapshot source for tests.</summary>
    private sealed class FakeCapabilitySource(ActiveCapabilitySnapshot snapshot) : IActiveCapabilitySource
    {
        public ActiveCapabilitySnapshot Snapshot() => snapshot;
    }

    /// <summary>以匿名对象序列化构造工具调用参数。 Builds tool-call arguments from an anonymous object.</summary>
    private static ChatToolCall Call(string functionName, object arguments) =>
        new(Guid.NewGuid().ToString("N"), functionName, System.Text.Json.JsonSerializer.Serialize(arguments));
}
