using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>使用模拟后端验证冻结计划传递、输出组装和终态映射，不启动真实进程。</para>
///     <para>
///         Uses a fake backend to verify frozen-plan delivery, output assembly and terminal mapping without starting
///         processes.
///     </para>
/// </summary>
public class ShellToolBackendTests
{
    [Test]
    public async Task Execute_SendsFrozenShellModePlanToBackend()
    {
        using var dir     = new TestTempDir();
        var       shell   = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var       command = "Write-Output probe";
        var       fake    = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool    = new ShellTool(dir.Workspace, 45, processBackend : fake);

        var preparation = tool.Prepare(ShellCall(shell, command));
        var result      = await tool.ExecuteAsync(preparation, CancellationToken.None);

        var received = TestAssert.TypeOf<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(received.Executable, Is.EqualTo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh"));
        Assert.That(received.Arguments, Is.EqualTo(OperatingSystem.IsWindows()
                                                       ? new[]
                                                       {
                                                           "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                                                           command
                                                       }
                                                       : new[] { "-c", command }));
        Assert.That(received.WorkingDirectory, Is.EqualTo(dir.Root));
        Assert.That(received.TimeoutSeconds, Is.EqualTo(45));
        Assert.That(received.WorkspaceExecutable, Is.False);
        Assert.That(received.RawCmdCommand, Is.Null);
        // 后端收到的计划必须与已审批的冻结视图一致。
        Assert.That(received.Executable, Is.EqualTo(preparation.Arguments["executable"]!.GetValue<string>()));
        Assert.That(received.TimeoutSeconds, Is.EqualTo(preparation.Arguments["timeoutSeconds"]!.GetValue<int>()));
    }

    [Test]
    public async Task Execute_SendsFrozenDirectModePlanToBackend()
    {
        using var dir        = new TestTempDir();
        var       executable = dir.WriteFile("local-tool.cmd", "rem stub");
        var       fake       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool       = new ShellTool(dir.Workspace, processBackend : fake);

        var preparation = tool.Prepare(Call(executable, ["--flag value", "plain"]));
        await tool.ExecuteAsync(preparation, CancellationToken.None);

        var received = TestAssert.TypeOf<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.That(received.Executable, Is.EqualTo(executable));
        Assert.That(received.Arguments, Is.EqualTo(["--flag value", "plain"]));
        Assert.That(received.WorkingDirectory, Is.EqualTo(dir.Root));
        Assert.That(received.WorkspaceExecutable, Is.True);
        Assert.That(received.RawCmdCommand, Is.Null);
    }

    [Test]
    public async Task Execute_SendsRawCmdTailToBackendForWindowsCmdShell()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var    dir     = new TestTempDir();
        const string command = "echo quoted \"a b\" & echo combined";
        var          fake    = new FakeProcessExecutionBackend { ExitCode = 0 };
        var          tool    = new ShellTool(dir.Workspace, processBackend : fake);

        await tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), CancellationToken.None);

        var received = TestAssert.TypeOf<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.That(received.Executable, Is.EqualTo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"));
        Assert.That(received.Arguments, Is.EqualTo(["/d", "/s", "/c", command]));
        Assert.That(received.RawCmdCommand, Is.EqualTo(command));
    }

    [Test]
    public async Task Execute_SeparatesBackendStdoutAndStderrStreams()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["out-part-1 ", "out-part-2"],
            StderrChunks = ["err-part-1 ", "err-part-2"],
            ExitCode     = 0
        };
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"),
                                 processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(result.Stdout, Is.EqualTo("out-part-1 out-part-2"));
        Assert.That(result.Stderr, Is.EqualTo("err-part-1 err-part-2"));
        TestAssert.DoesNotContain("err-part", result.Stdout, StringComparison.Ordinal);
        TestAssert.DoesNotContain("out-part", result.Stderr, StringComparison.Ordinal);
    }

    [Test]
    public async Task Execute_MapsBackendZeroExitCodeToSucceeded()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(result.ExitCode, Is.EqualTo(0));
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Content, Does.Contain("Exit code: 0"));
    }

    [Test]
    public async Task Execute_MapsBackendNonZeroExitCodeToFailedResult()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend { ExitCode = 7 };
        var       tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ExitCode, Is.EqualTo(7));
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Content, Does.Contain("Exit code: 7"));
    }

    [Test]
    public async Task Execute_KeepsOutputAndFailsOnBackendTimeout()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["partial-stdout"],
            StderrChunks = ["partial-stderr"],
            ExitCode     = 1,
            TimedOut     = true
        };
        var tool = new ShellTool(dir.Workspace, 9, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.Stdout, Does.Contain("partial-stdout"));
        Assert.That(result.Stderr, Does.Contain("partial-stderr"));
        Assert.That(result.Content, Does.Contain("Timed out: true (9s)"));
    }

    [Test]
    public async Task Execute_PropagatesBackendCancellation()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new OperationCanceledException()
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                tool.ExecuteAsync(tool.Prepare(Call("dotnet",
                                                                                           ["test"])),
                                                                         CancellationToken.None));
    }

    [Test]
    public async Task Execute_FailsWithoutExitCodeOnBackendFailure()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            StderrChunks = ["partial-stderr"],
            Failure      = new ProcessExecutionFailure("sandbox runner handshake failed")
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Content, Does.Contain("Exit code: unavailable"));
        Assert.That(result.Content, Does.Contain("sandbox runner handshake failed"));
        Assert.That(result.Content, Does.Contain("partial-stderr" + Environment.NewLine +
                                                 "Failure: sandbox runner handshake failed"));
    }

    [Test]
    public async Task Execute_MapsBackendStartFailureToFailedResult()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new ProcessExecutionStartException("executable was not found",
                                                                new IOException("no such file"))
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.Content, Does.Contain("Failed to start executable"));
    }

    [Test]
    public async Task Execute_PropagatesUnknownProcessCleanupWithoutFabricatingAResult()
    {
        using var dir     = new TestTempDir();
        var       failure = new ToolExecutionUncertainException("runner could not be reclaimed");
        var       backend = new FakeProcessExecutionBackend { ThrowOnExecute = failure };
        var       tool    = new ShellTool(dir.Workspace, processBackend : backend);

        var exception = await TestAssert.ThrowsAsync<ToolExecutionUncertainException>(() =>
                     tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None));

        Assert.That(exception, Is.SameAs(failure));
    }

    [Test]
    public async Task Execute_RedactsSecretAcrossBackendChunkBoundary()
    {
        using var    dir    = new TestTempDir();
        const string secret = "chunk-boundary-secret";
        var fake = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["prefix-" + secret[..10], secret[10..] + "-suffix"],
            ExitCode     = 0
        };
        var tool = new ShellTool(dir.Workspace,
                                 knownSecrets : new Dictionary<string, string> { ["TEST_SECRET"] = secret },
                                 artifactRoot : dir.CreateDirectory("artifacts"), processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(result.Stdout, Does.Contain("[REDACTED]"));
        TestAssert.DoesNotContain(secret, result.Stdout, StringComparison.Ordinal);
        TestAssert.DoesNotContain(secret, result.Content, StringComparison.Ordinal);
    }

    private static ChatToolCall Call(string executable, IReadOnlyList<string> arguments,
                                     string workingDirectory = ".")
    {
        var array = new JsonArray();
        foreach (var argument in arguments) array.Add((JsonNode?)JsonValue.Create(argument));

        var args = new JsonObject
        {
            ["executable"]       = executable,
            ["arguments"]        = array,
            ["workingDirectory"] = workingDirectory
        };
        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static ChatToolCall ShellCall(string shell, string command)
    {
        var args = new JsonObject
        {
            ["mode"]    = "shell",
            ["shell"]   = shell,
            ["command"] = command
        };
        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }
}
