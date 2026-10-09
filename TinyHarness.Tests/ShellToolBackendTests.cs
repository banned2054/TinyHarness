using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
/// ShellTool 与 IProcessExecutionBackend 之间契约的映射测试：使用 fake backend
/// 验证冻结计划的传递、输出块到 ToolResult 的组装，以及终态映射，不启动真实进程。
/// </summary>
public class ShellToolBackendTests
{
    [Fact]
    public async Task Execute_SendsFrozenShellModePlanToBackend()
    {
        using var dir    = new TestTempDir();
        var       shell  = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var       command = "Write-Output probe";
        var       fake    = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool    = new ShellTool(dir.Workspace, defaultTimeoutSeconds : 45, processBackend : fake);

        var preparation = tool.Prepare(ShellCall(shell, command));
        var result      = await tool.ExecuteAsync(preparation, CancellationToken.None);

        var received = Assert.IsType<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.True(result.Succeeded, result.Content);
        Assert.Equal(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh", received.Executable);
        Assert.Equal(OperatingSystem.IsWindows()
                         ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command }
                         : new[] { "-c", command },
                     received.Arguments);
        Assert.Equal(dir.Root, received.WorkingDirectory);
        Assert.Equal(45, received.TimeoutSeconds);
        Assert.False(received.WorkspaceExecutable);
        Assert.Null(received.RawCmdCommand);
        // The backend plan and the approved preparation must stay the same frozen view.
        Assert.Equal(preparation.Arguments["executable"]!.GetValue<string>(), received.Executable);
        Assert.Equal(preparation.Arguments["timeoutSeconds"]!.GetValue<int>(), received.TimeoutSeconds);
    }

    [Fact]
    public async Task Execute_SendsFrozenDirectModePlanToBackend()
    {
        using var dir        = new TestTempDir();
        var       executable = dir.WriteFile("local-tool.cmd", "rem stub");
        var       fake       = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool       = new ShellTool(dir.Workspace, processBackend : fake);

        var preparation = tool.Prepare(Call(executable, ["--flag value", "plain"]));
        await tool.ExecuteAsync(preparation, CancellationToken.None);

        var received = Assert.IsType<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.Equal(executable, received.Executable);
        Assert.Equal(["--flag value", "plain"], received.Arguments);
        Assert.Equal(dir.Root, received.WorkingDirectory);
        Assert.True(received.WorkspaceExecutable);
        Assert.Null(received.RawCmdCommand);
    }

    [Fact]
    public async Task Execute_SendsRawCmdTailToBackendForWindowsCmdShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir     = new TestTempDir();
        const string command = "echo quoted \"a b\" & echo combined";
        var       fake   = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool   = new ShellTool(dir.Workspace, processBackend : fake);

        await tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), CancellationToken.None);

        var received = Assert.IsType<PreparedProcessExecution>(fake.ReceivedExecution);
        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", received.Executable);
        Assert.Equal(["/d", "/s", "/c", command], received.Arguments);
        Assert.Equal(command, received.RawCmdCommand);
    }

    [Fact]
    public async Task Execute_SeparatesBackendStdoutAndStderrStreams()
    {
        using var dir = new TestTempDir();
        var fake = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["out-part-1 ", "out-part-2"],
            StderrChunks = ["err-part-1 ", "err-part-2"],
            ExitCode     = 0,
        };
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"),
                                 processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal("out-part-1 out-part-2", result.Stdout);
        Assert.Equal("err-part-1 err-part-2", result.Stderr);
        Assert.DoesNotContain("err-part", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("out-part", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_MapsBackendZeroExitCodeToSucceeded()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend { ExitCode = 0 };
        var       tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("Exit code: 0", result.Content);
    }

    [Fact]
    public async Task Execute_MapsBackendNonZeroExitCodeToFailedResult()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend { ExitCode = 7 };
        var       tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("Exit code: 7", result.Content);
    }

    [Fact]
    public async Task Execute_KeepsOutputAndFailsOnBackendTimeout()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["partial-stdout"],
            StderrChunks = ["partial-stderr"],
            ExitCode     = 1,
            TimedOut     = true,
        };
        var tool = new ShellTool(dir.Workspace, defaultTimeoutSeconds : 9, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Contains("partial-stdout", result.Stdout);
        Assert.Contains("partial-stderr", result.Stderr);
        Assert.Contains("Timed out: true (9s)", result.Content);
    }

    [Fact]
    public async Task Execute_PropagatesBackendCancellation()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new OperationCanceledException(),
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                                                                    tool.ExecuteAsync(tool.Prepare(Call("dotnet",
                                                                               ["test"])),
                                                                        CancellationToken.None));
    }

    [Fact]
    public async Task Execute_FailsWithoutExitCodeOnBackendFailure()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend
        {
            StderrChunks = ["partial-stderr"],
            Failure      = new ProcessExecutionFailure("sandbox runner handshake failed"),
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("Exit code: unavailable", result.Content);
        Assert.Contains("sandbox runner handshake failed", result.Content);
        Assert.Contains("partial-stderr" + Environment.NewLine +
                        "Failure: sandbox runner handshake failed", result.Content);
    }

    [Fact]
    public async Task Execute_MapsBackendStartFailureToFailedResult()
    {
        using var dir  = new TestTempDir();
        var       fake = new FakeProcessExecutionBackend
        {
            ThrowOnExecute = new ProcessExecutionStartException("executable was not found",
                                                                new IOException("no such file")),
        };
        var tool = new ShellTool(dir.Workspace, processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.ExitCode);
        Assert.Contains("Failed to start executable", result.Content);
    }

    [Fact]
    public async Task Execute_RedactsSecretAcrossBackendChunkBoundary()
    {
        using var    dir     = new TestTempDir();
        const string secret  = "chunk-boundary-secret";
        var          fake    = new FakeProcessExecutionBackend
        {
            StdoutChunks = ["prefix-" + secret[..10], secret[10..] + "-suffix"],
            ExitCode     = 0,
        };
        var tool = new ShellTool(dir.Workspace,
                                 knownSecrets : new Dictionary<string, string> { ["TEST_SECRET"] = secret },
                                 artifactRoot : dir.CreateDirectory("artifacts"), processBackend : fake);

        var result = await tool.ExecuteAsync(tool.Prepare(Call("dotnet", ["test"])), CancellationToken.None);

        Assert.True(result.Succeeded, result.Content);
        Assert.Contains("[REDACTED]", result.Stdout);
        Assert.DoesNotContain(secret, result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Content, StringComparison.Ordinal);
    }

    private static ChatToolCall Call(string executable, IReadOnlyList<string> arguments,
                                     string workingDirectory = ".")
    {
        var array = new JsonArray();
        foreach (var argument in arguments)
        {
            array.Add((JsonNode?)JsonValue.Create(argument));
        }

        var args = new JsonObject
        {
            ["executable"]       = executable,
            ["arguments"]        = array,
            ["workingDirectory"] = workingDirectory,
        };
        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static ChatToolCall ShellCall(string shell, string command)
    {
        var args = new JsonObject
        {
            ["mode"]    = "shell",
            ["shell"]   = shell,
            ["command"] = command,
        };
        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }
}
