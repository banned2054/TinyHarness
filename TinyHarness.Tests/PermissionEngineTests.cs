using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class PermissionEngineTests
{
    private static ToolPreparation Prep(
        string capability, IReadOnlyList<string> targets, string argumentKey = "x", int argumentValue = 1) => new()
    {
        ToolName    = "apply_patch",
        CallId      = "call_1",
        Arguments   = new JsonObject { [argumentKey] = argumentValue },
        Capability  = capability,
        Summary     = "summary",
        TargetPaths = targets
    };

    [Test]
    public void ToolPreparation_SnapshotsMutableInputs()
    {
        using var dir     = new TestTempDir();
        var       a       = Path.Combine(dir.Root, "a.cs");
        var       b       = Path.Combine(dir.Root, "b.cs");
        var       args    = new JsonObject { ["line"] = 1 };
        var       targets = new List<string> { a };

        var preparation = new ToolPreparation
        {
            ToolName    = "apply_patch",
            CallId      = "call_1",
            Arguments   = args,
            Capability  = "filesystem.write",
            Summary     = "summary",
            TargetPaths = targets
        };

        args["line"] = 2;
        targets[0]   = b;

        Assert.That(preparation.Arguments["line"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(TestAssert.Single(preparation.TargetPaths), Is.EqualTo(a));
    }

    [Test]
    public void ReadOnlyInsideWorkspace_IsAllowedByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       target = Path.Combine(dir.Root, "a.cs");

        Assert.That(engine.Decide(Prep("filesystem.read", [target])), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(Prep("filesystem.list", [dir.Root])), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(Prep("filesystem.search", [dir.Root])), Is.EqualTo(PermissionDecision.Allow));
    }

    [Test]
    public void WriteInsideWorkspace_AsksByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       target = Path.Combine(dir.Root, "a.cs");

        Assert.That(engine.Decide(Prep("filesystem.write", [target])), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void UnknownCapability_AsksByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);

        Assert.That(engine.Decide(Prep("process.execute", [dir.Root])), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void NonReadOnlyBlock_DeniesSideEffectsWhileReadsStayAllowedAndGrantsCannotBypass()
    {
        using var dir     = new TestTempDir();
        var       engine  = new PermissionEngine(dir.Root);
        var       target  = Path.Combine(dir.Root, "a.cs");
        var       write   = Prep("filesystem.write", [target]);
        var       read    = Prep("filesystem.read", [target]);
        var       execute = Prep("process.execute", [target]);

        // NeedsInspection 阻断所有非只读能力。
        engine.SetNonReadOnlyBlock("run x was interrupted");
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(execute), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(read), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.NonReadOnlyBlockReason, Is.EqualTo("run x was interrupted"));
        Assert.That(engine.NonReadOnlyBlock(write), Is.EqualTo("run x was interrupted"));
        Assert.That(engine.NonReadOnlyBlock(read), Is.Null);

        // 运行期授权和单次批准均不能绕过检查阻断。
        engine.GrantSession(write);
        engine.GrantOnce(execute);
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(execute), Is.EqualTo(PermissionDecision.Deny));

        // 解除阻断后恢复正常策略，已有授权按原范围生效。
        engine.SetNonReadOnlyBlock(null);
        Assert.That(engine.NonReadOnlyBlockReason, Is.Null);
        Assert.That(engine.Decide(write), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(execute), Is.EqualTo(PermissionDecision.Allow));
    }

    [Test]
    public void WorkerMode_HardDeniesWriteProcessAndUnknownCapabilities()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root, denyNonReadOnlyCapabilities : true);

        Assert.That(engine.Decide(Prep("filesystem.read", [Path.Combine(dir.Root, "a.cs")])),
                    Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(Prep("filesystem.write", [Path.Combine(dir.Root, "a.cs")])),
                    Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(Prep("process.execute", [dir.Root])), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(Prep("filesystem.unknown", [dir.Root])), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void TargetOutsideWorkspace_IsHardDenied()
    {
        using var inside  = new TestTempDir();
        using var outside = new TestTempDir();
        var       engine  = new PermissionEngine(inside.Root);
        var       target  = Path.Combine(outside.Root, "secret.cs");

        Assert.That(engine.Decide(Prep("filesystem.read", [target])), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(Prep("filesystem.write", [target])), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SessionGrant_AllowsMatchingScopeOnly()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.GrantSession(Prep("filesystem.write", [a]));

        Assert.That(engine.Decide(Prep("filesystem.write", [a])), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(Prep("filesystem.write", [b])), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void SessionGrant_DirectoryScopeCoversDescendants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       scope  = Path.Combine(dir.Root, "src");
        var       file   = Path.Combine(scope, "deep", "a.cs");

        engine.GrantSession(Prep("filesystem.write", [scope]));

        Assert.That(engine.Decide(Prep("filesystem.write", [file])), Is.EqualTo(PermissionDecision.Allow));
    }

    [Test]
    public void SessionDeny_WinsOverSessionGrant()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        engine.GrantSession(Prep("filesystem.write", [a]));
        engine.DenySession(Prep("filesystem.write", [a]));

        // 显式拒绝和运行期拒绝优先于运行期授权。
        Assert.That(engine.Decide(Prep("filesystem.write", [a])), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SessionDeny_DoesNotAffectOtherScopes()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.DenySession(Prep("filesystem.write", [a]));

        Assert.That(engine.Decide(Prep("filesystem.write", [a])), Is.EqualTo(PermissionDecision.Deny));
        Assert.That(engine.Decide(Prep("filesystem.write", [b])), Is.EqualTo(PermissionDecision.Ask));
    }


    [TestCase(false)]
    [TestCase(true)]
    public void SessionDeny_BlocksAnyDeniedTargetInMultiFileCall(bool grantSession)
    {
        using var dir     = new TestTempDir();
        var       engine  = new PermissionEngine(dir.Root);
        var       denied  = Path.Combine(dir.Root, "protected", "a.cs");
        var       allowed = Path.Combine(dir.Root, "b.cs");
        var       call    = Prep("filesystem.write", [allowed, denied]);

        engine.DenySession(Prep("filesystem.write", [Path.Combine(dir.Root, "protected")]));
        if (grantSession)
            engine.GrantSession(Prep("filesystem.write", [dir.Root]));
        else
            engine.GrantOnce(call);

        Assert.That(engine.Decide(call), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void SessionGrant_MustCoverEveryTargetInMultiFileCall()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");
        engine.GrantSession(Prep("filesystem.write", [a]));

        Assert.That(engine.Decide(Prep("filesystem.write", [a, b])), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void AllowOnce_ApprovesExactInvocationOnly()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        var invocation = Prep("filesystem.write", [a], "line", 2);
        engine.GrantOnce(invocation);

        Assert.That(engine.Decide(invocation), Is.EqualTo(PermissionDecision.Allow));

        // 文件和能力相同但参数不同，仍是不同调用。
        Assert.That(engine.Decide(Prep("filesystem.write", [a], "line", 3)), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void AllowOnce_IsSpentOnFirstUse()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        var invocation = Prep("filesystem.write", [a], "line", 2);
        engine.GrantOnce(invocation);

        Assert.That(engine.Decide(invocation), Is.EqualTo(PermissionDecision.Allow));

        // 执行提交时消耗单次批准，后续相同调用仍须重新审批。
        Assert.That(engine.TryConsumeOnce(invocation), Is.True);
        Assert.That(engine.Decide(invocation), Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.TryConsumeOnce(invocation), Is.False); // 已消耗的批准不能再次使用。
    }

    [Test]
    public void AllowOnce_DoesNotLeakAcrossTargets()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.GrantOnce(Prep("filesystem.write", [a]));

        Assert.That(engine.Decide(Prep("filesystem.write", [a])), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(Prep("filesystem.write", [b])), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void HardDeny_BeatsOneShotApproval()
    {
        // 工作区内目标的单次批准不能授权工作区外目标。
        using var inside  = new TestTempDir();
        using var outside = new TestTempDir();
        var       engine  = new PermissionEngine(inside.Root);
        var       a       = Path.Combine(inside.Root, "a.cs");
        var       leak    = Path.Combine(outside.Root, "a.cs");

        engine.GrantOnce(Prep("filesystem.write", [a]));

        Assert.That(engine.Decide(Prep("filesystem.write", [leak])), Is.EqualTo(PermissionDecision.Deny));
    }

    [Test]
    public void ConfiguredCommandRule_AllowsOnlyExactExecutableArgumentsAndWorkingDirectory()
    {
        using var dir = new TestTempDir();
        var       src = dir.CreateDirectory("src");
        var engine =
            new PermissionEngine(dir.Root,
            [
                new CommandRule { Executable = "dotnet", Arguments = ["test", "*.csproj"], WorkingDirectory = "src" }
            ]);

        Assert.That(engine.Decide(ProcessPrep("dotnet", ["test", "TinyHarness.csproj"], src)),
                    Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(ProcessPrep("dotnet", ["build", "TinyHarness.csproj"], src)),
                    Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.Decide(ProcessPrep("git", ["test", "TinyHarness.csproj"], src)),
                    Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.Decide(ProcessPrep("dotnet", ["test", "TinyHarness.csproj"], dir.Root)),
                    Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void ProcessSessionGrant_DoesNotAuthorizeDifferentCommandInSameDirectory()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        engine.GrantSession(ProcessPrep("dotnet", ["test"], dir.Root));

        Assert.That(engine.Decide(ProcessPrep("dotnet", ["test"], dir.Root)), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(ProcessPrep("dotnet", ["build"], dir.Root)), Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.Decide(ProcessPrep("git", ["status"], dir.Root)), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void ShellCommandRule_AllowsOnlyExactShellCommandAndNeverMatchesDirectMode()
    {
        using var    dir              = new TestTempDir();
        var          shell            = OperatingSystem.IsWindows() ? "powershell" : "sh";
        const string command          = "echo one && echo two";
        var          tool             = new ShellTool(dir.Workspace);
        var          shellPreparation = tool.Prepare(ShellCall(shell, command));
        var engine = new PermissionEngine(dir.Root,
        [
            new CommandRule { Mode = "shell", Shell = shell, Command = command, WorkingDirectory = "." }
        ]);

        Assert.That(engine.Decide(shellPreparation), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(tool.Prepare(ShellCall(shell, command + " && echo three"))),
                    Is.EqualTo(PermissionDecision.Ask));

        var directEquivalent = ProcessPrep(
                                           shellPreparation.Arguments["executable"]!.GetValue<string>(),
                                           ReadArguments(shellPreparation), dir.Root);
        Assert.That(engine.Decide(directEquivalent), Is.EqualTo(PermissionDecision.Ask));
    }

    [Test]
    public void ShellSessionGrant_IsBoundToModeShellFlavorAndExactCommand()
    {
        using var dir     = new TestTempDir();
        var       shell   = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var       tool    = new ShellTool(dir.Workspace);
        var       granted = tool.Prepare(ShellCall(shell, "echo one"));
        var       engine  = new PermissionEngine(dir.Root);
        engine.GrantSession(granted);
        var child = dir.CreateDirectory("child");

        Assert.That(engine.Decide(tool.Prepare(ShellCall(shell, "echo one"))), Is.EqualTo(PermissionDecision.Allow));
        Assert.That(engine.Decide(tool.Prepare(ShellCall(shell, "echo two"))), Is.EqualTo(PermissionDecision.Ask));
        Assert.That(engine.Decide(tool.Prepare(ShellCall(shell, "echo one", child))),
                    Is.EqualTo(PermissionDecision.Ask));
    }

    private static ToolPreparation ProcessPrep(string executable, IReadOnlyList<string> arguments, string cwd)
    {
        var array = new JsonArray();
        foreach (var argument in arguments) array.Add((JsonNode?)JsonValue.Create(argument));

        var identity = new JsonObject
        {
            ["mode"]       = "direct",
            ["executable"] = executable,
            ["arguments"]  = array.DeepClone()
        }.ToJsonString();
        return new ToolPreparation
        {
            ToolName   = "shell",
            CallId     = "shell",
            Capability = "process.execute",
            Summary    = "shell",
            Arguments = new JsonObject
            {
                ["mode"]             = "direct",
                ["executable"]       = executable,
                ["arguments"]        = array,
                ["workingDirectory"] = cwd
            },
            SessionConstraint = identity,
            TargetPaths       = [cwd]
        };
    }

    private static ChatToolCall ShellCall(string shell, string command, string workingDirectory = ".") =>
        new("shell", "shell", new JsonObject
        {
            ["mode"]             = "shell",
            ["shell"]            = shell,
            ["command"]          = command,
            ["workingDirectory"] = workingDirectory
        }.ToJsonString());

    private static IReadOnlyList<string> ReadArguments(ToolPreparation preparation)
    {
        return ((JsonArray)preparation.Arguments["arguments"]!).Select(node => node!.GetValue<string>()).ToArray();
    }
}
