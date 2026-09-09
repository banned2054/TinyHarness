using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Configuration;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class PermissionEngineTests
{
    private static ToolPreparation Prep(string capability, IReadOnlyList<string> targets, string argumentKey = "x",
                                        int    argumentValue = 1) => new()
    {
        ToolName    = "apply_patch",
        CallId      = "call_1",
        Arguments   = new JsonObject { [argumentKey] = argumentValue },
        Capability  = capability,
        Summary     = "summary",
        TargetPaths = targets,
    };

    [Fact]
    public void ToolPreparation_SnapshotsMutableInputs()
    {
        using var dir     = new TestTempDir();
        var       a       = Path.Combine(dir.Root, "a.cs");
        var       b       = Path.Combine(dir.Root, "b.cs");
        var       args    = new JsonObject { ["line"] = 1 };
        var       targets = new List<string> { a };

        var preparation = new ToolPreparation
        {
            ToolName   = "apply_patch", CallId       = "call_1", Arguments    = args,
            Capability = "filesystem.write", Summary = "summary", TargetPaths = targets,
        };

        args["line"] = 2;
        targets[0]   = b;

        Assert.Equal(1, preparation.Arguments["line"]!.GetValue<int>());
        Assert.Equal(a, Assert.Single(preparation.TargetPaths));
    }

    [Fact]
    public void ReadOnlyInsideWorkspace_IsAllowedByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       target = Path.Combine(dir.Root, "a.cs");

        Assert.Equal(PermissionDecision.Allow,
                     engine.Decide(Prep("filesystem.read", [target])));
        Assert.Equal(PermissionDecision.Allow,
                     engine.Decide(Prep("filesystem.list", [dir.Root])));
        Assert.Equal(PermissionDecision.Allow,
                     engine.Decide(Prep("filesystem.search", [dir.Root])));
    }

    [Fact]
    public void WriteInsideWorkspace_AsksByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       target = Path.Combine(dir.Root, "a.cs");

        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("filesystem.write", [target])));
    }

    [Fact]
    public void UnknownCapability_AsksByDefault()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);

        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("process.execute", [dir.Root])));
    }

    [Fact]
    public void TargetOutsideWorkspace_IsHardDenied()
    {
        using var inside  = new TestTempDir();
        using var outside = new TestTempDir();
        var       engine  = new PermissionEngine(inside.Root);
        var       target  = Path.Combine(outside.Root, "secret.cs");

        Assert.Equal(PermissionDecision.Deny, engine.Decide(Prep("filesystem.read", [target])));
        Assert.Equal(PermissionDecision.Deny, engine.Decide(Prep("filesystem.write", [target])));
    }

    [Fact]
    public void SessionGrant_AllowsMatchingScopeOnly()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.GrantSession(Prep("filesystem.write", [a]));

        Assert.Equal(PermissionDecision.Allow, engine.Decide(Prep("filesystem.write", [a])));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("filesystem.write", [b])));
    }

    [Fact]
    public void SessionGrant_DirectoryScopeCoversDescendants()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       scope  = Path.Combine(dir.Root, "src");
        var       file   = Path.Combine(scope, "deep", "a.cs");

        engine.GrantSession(Prep("filesystem.write", [scope]));

        Assert.Equal(PermissionDecision.Allow, engine.Decide(Prep("filesystem.write", [file])));
    }

    [Fact]
    public void SessionDeny_WinsOverSessionGrant()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        engine.GrantSession(Prep("filesystem.write", [a]));
        engine.DenySession(Prep("filesystem.write", [a]));

        // Priority: explicit/session deny > session grant (PLAN §10).
        Assert.Equal(PermissionDecision.Deny, engine.Decide(Prep("filesystem.write", [a])));
    }

    [Fact]
    public void SessionDeny_DoesNotAffectOtherScopes()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.DenySession(Prep("filesystem.write", [a]));

        Assert.Equal(PermissionDecision.Deny, engine.Decide(Prep("filesystem.write", [a])));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("filesystem.write", [b])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionDeny_BlocksAnyDeniedTargetInMultiFileCall(bool grantSession)
    {
        using var dir     = new TestTempDir();
        var       engine  = new PermissionEngine(dir.Root);
        var       denied  = Path.Combine(dir.Root, "protected", "a.cs");
        var       allowed = Path.Combine(dir.Root, "b.cs");
        var       call    = Prep("filesystem.write", [allowed, denied]);

        engine.DenySession(Prep("filesystem.write", [Path.Combine(dir.Root, "protected")]));
        if (grantSession)
        {
            engine.GrantSession(Prep("filesystem.write", [dir.Root]));
        }
        else
        {
            engine.GrantOnce(call);
        }

        Assert.Equal(PermissionDecision.Deny, engine.Decide(call));
    }

    [Fact]
    public void SessionGrant_MustCoverEveryTargetInMultiFileCall()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");
        engine.GrantSession(Prep("filesystem.write", [a]));

        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("filesystem.write", [a, b])));
    }

    [Fact]
    public void AllowOnce_ApprovesExactInvocationOnly()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        var invocation = Prep("filesystem.write", [a], "line", 2);
        engine.GrantOnce(invocation);

        Assert.Equal(PermissionDecision.Allow, engine.Decide(invocation));

        // Same file and capability but different arguments: a different invocation.
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(Prep("filesystem.write", [a], "line", 3)));
    }

    [Fact]
    public void AllowOnce_IsSpentOnFirstUse()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");

        var invocation = Prep("filesystem.write", [a], "line", 2);
        engine.GrantOnce(invocation);

        Assert.Equal(PermissionDecision.Allow, engine.Decide(invocation));

        // Spending it at execution commit makes an identical later invocation
        // ask again instead of riding the one-shot for the whole session.
        Assert.True(engine.TryConsumeOnce(invocation));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(invocation));
        Assert.False(engine.TryConsumeOnce(invocation)); // nothing left to spend
    }

    [Fact]
    public void AllowOnce_DoesNotLeakAcrossTargets()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        var       a      = Path.Combine(dir.Root, "a.cs");
        var       b      = Path.Combine(dir.Root, "b.cs");

        engine.GrantOnce(Prep("filesystem.write", [a]));

        Assert.Equal(PermissionDecision.Allow, engine.Decide(Prep("filesystem.write", [a])));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(Prep("filesystem.write", [b])));
    }

    [Fact]
    public void HardDeny_BeatsOneShotApproval()
    {
        // A one-shot approval for an inside target must not authorize the same
        // capability for a target outside the workspace.
        using var inside  = new TestTempDir();
        using var outside = new TestTempDir();
        var       engine  = new PermissionEngine(inside.Root);
        var       a       = Path.Combine(inside.Root, "a.cs");
        var       leak    = Path.Combine(outside.Root, "a.cs");

        engine.GrantOnce(Prep("filesystem.write", [a]));

        Assert.Equal(PermissionDecision.Deny, engine.Decide(Prep("filesystem.write", [leak])));
    }

    [Fact]
    public void ConfiguredCommandRule_AllowsOnlyExactExecutableArgumentsAndWorkingDirectory()
    {
        using var dir = new TestTempDir();
        var       src = dir.CreateDirectory("src");
        var engine =
            new PermissionEngine(dir.Root,
            [
                new CommandRule { Executable = "dotnet", Arguments = ["test", "*.csproj"], WorkingDirectory = "src", },
            ]);

        Assert.Equal(PermissionDecision.Allow,
                     engine.Decide(ProcessPrep("dotnet", ["test", "TinyHarness.csproj"], src)));
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(ProcessPrep("dotnet", ["build", "TinyHarness.csproj"], src)));
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(ProcessPrep("git", ["test", "TinyHarness.csproj"], src)));
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(ProcessPrep("dotnet", ["test", "TinyHarness.csproj"], dir.Root)));
    }

    [Fact]
    public void ProcessSessionGrant_DoesNotAuthorizeDifferentCommandInSameDirectory()
    {
        using var dir    = new TestTempDir();
        var       engine = new PermissionEngine(dir.Root);
        engine.GrantSession(ProcessPrep("dotnet", ["test"], dir.Root));

        Assert.Equal(PermissionDecision.Allow, engine.Decide(ProcessPrep("dotnet", ["test"], dir.Root)));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(ProcessPrep("dotnet", ["build"], dir.Root)));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(ProcessPrep("git", ["status"], dir.Root)));
    }

    [Fact]
    public void ShellCommandRule_AllowsOnlyExactShellCommandAndNeverMatchesDirectMode()
    {
        using var dir = new TestTempDir();
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        const string command = "echo one && echo two";
        var tool = new ShellTool(dir.Workspace);
        var shellPreparation = tool.Prepare(ShellCall(shell, command));
        var engine = new PermissionEngine(dir.Root,
        [
            new CommandRule { Mode = "shell", Shell = shell, Command = command, WorkingDirectory = ".", },
        ]);

        Assert.Equal(PermissionDecision.Allow, engine.Decide(shellPreparation));
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(tool.Prepare(ShellCall(shell, command + " && echo three"))));

        var directEquivalent = ProcessPrep(
            shellPreparation.Arguments["executable"]!.GetValue<string>(),
            ReadArguments(shellPreparation), dir.Root);
        Assert.Equal(PermissionDecision.Ask, engine.Decide(directEquivalent));
    }

    [Fact]
    public void ShellSessionGrant_IsBoundToModeShellFlavorAndExactCommand()
    {
        using var dir = new TestTempDir();
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var tool = new ShellTool(dir.Workspace);
        var granted = tool.Prepare(ShellCall(shell, "echo one"));
        var engine = new PermissionEngine(dir.Root);
        engine.GrantSession(granted);
        var child = dir.CreateDirectory("child");

        Assert.Equal(PermissionDecision.Allow, engine.Decide(tool.Prepare(ShellCall(shell, "echo one"))));
        Assert.Equal(PermissionDecision.Ask, engine.Decide(tool.Prepare(ShellCall(shell, "echo two"))));
        Assert.Equal(PermissionDecision.Ask,
                     engine.Decide(tool.Prepare(ShellCall(shell, "echo one", child))));
    }

    private static ToolPreparation ProcessPrep(string executable, IReadOnlyList<string> arguments, string cwd)
    {
        var array = new JsonArray();
        foreach (var argument in arguments)
        {
            array.Add((JsonNode?)JsonValue.Create(argument));
        }

        var identity = new JsonObject
        {
            ["mode"]       = "direct",
            ["executable"] = executable,
            ["arguments"]  = array.DeepClone(),
        }.ToJsonString();
        return new ToolPreparation
        {
            ToolName = "shell", CallId = "shell", Capability = "process.execute", Summary = "shell",
            Arguments = new JsonObject
            {
                ["mode"] = "direct", ["executable"] = executable, ["arguments"] = array,
                ["workingDirectory"] = cwd,
            },
            SessionConstraint = identity,
            TargetPaths       = [cwd],
        };
    }

    private static ChatToolCall ShellCall(string shell, string command, string workingDirectory = ".")
        => new("shell", "shell", new JsonObject
        {
            ["mode"] = "shell", ["shell"] = shell, ["command"] = command,
            ["workingDirectory"] = workingDirectory,
        }.ToJsonString());

    private static IReadOnlyList<string> ReadArguments(ToolPreparation preparation)
        => ((JsonArray)preparation.Arguments["arguments"]!).Select(node => node!.GetValue<string>()).ToArray();
}
