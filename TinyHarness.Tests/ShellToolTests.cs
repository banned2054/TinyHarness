using System.Diagnostics;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Runtime;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class ShellToolTests
{
    [Fact]
    public void Prepare_NormalizesStructuredCommandAndBindsSessionConstraint()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory("src");
        var tool = new ShellTool(dir.Workspace, defaultTimeoutSeconds : 45);

        var preparation = tool.Prepare(Call("dotnet", ["test", "--no-restore"], "src"));

        Assert.Equal("process.execute", preparation.Capability);
        Assert.Equal("direct", preparation.Arguments["mode"]!.GetValue<string>());
        Assert.Equal([Path.Combine(dir.Root, "src")], preparation.TargetPaths);
        Assert.Equal("dotnet", preparation.Arguments["executable"]!.GetValue<string>());
        Assert.Equal(45, preparation.Arguments["timeoutSeconds"]!.GetValue<int>());
        Assert.Contains("dotnet", preparation.SessionConstraint);
        Assert.Contains("--no-restore", preparation.SessionConstraint);
    }

    [Fact]
    public void Prepare_RejectsWorkingDirectoryEscapeAndInvalidArguments()
    {
        using var dir  = new TestTempDir();
        var       tool = new ShellTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => tool.Prepare(Call("dotnet", ["test"], "..")));
        Assert.Throws<InvalidDataException>(() => tool.Prepare(new ChatToolCall(
            "shell", "shell", """{"executable":"dotnet","arguments":[1]}""")));
        Assert.Throws<ArgumentOutOfRangeException>(() => tool.Prepare(new ChatToolCall(
            "shell", "shell", """{"executable":"dotnet","timeoutSeconds":0}""")));
        Assert.Throws<InvalidDataException>(() => tool.Prepare(new ChatToolCall(
            "shell", "shell", """{"mode":"shell","shell":"powershell","command":"Get-Date","executable":"dotnet"}""")));
    }

    [Fact]
    public void Prepare_ShellModeFreezesInterpreterAndMarksElevatedRisk()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace);
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var command = OperatingSystem.IsWindows() ? "Write-Output one; Write-Output two" : "printf one; printf two";

        var preparation = tool.Prepare(ShellCall(shell, command));

        Assert.Equal("shell", preparation.Arguments["mode"]!.GetValue<string>());
        Assert.Equal(shell, preparation.Arguments["shell"]!.GetValue<string>());
        Assert.Equal(command, preparation.Arguments["command"]!.GetValue<string>());
        Assert.Equal(ToolRiskLevel.Elevated, preparation.RiskLevel);
        Assert.Contains(command, preparation.SessionConstraint);
        Assert.DoesNotContain("Start-Process", preparation.SessionConstraint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_ShellModeSupportsPipelinesAndRedirection()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var command = OperatingSystem.IsWindows()
            ? "'alpha' | ForEach-Object { $_.ToUpperInvariant() }; 'written' | Set-Content -NoNewline -LiteralPath shell-result.txt"
            : "printf alpha | tr '[:lower:]' '[:upper:]'; printf written > shell-result.txt";

        var result = await tool.ExecuteAsync(tool.Prepare(ShellCall(shell, command)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Content);
        Assert.Contains("ALPHA", result.Stdout);
        Assert.Equal("written", await File.ReadAllTextAsync(Path.Combine(dir.Root, "shell-result.txt")));
    }

    [Fact]
    public async Task Execute_CmdShellPreservesQuotedCommandAndCombinationSyntax()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var nestedCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var command = $"\"{nestedCmd}\" /d /c echo nested-ok & echo \"a b\" & (echo combined-ok)";

        var result = await tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), CancellationToken.None);

        Assert.True(result.Succeeded, result.Content);
        Assert.Contains("nested-ok", result.Stdout);
        Assert.Contains("\"a b\"", result.Stdout);
        Assert.Contains("combined-ok", result.Stdout);
        Assert.DoesNotContain("\\\"a b\\\"", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_DirectModePreservesSpacesQuotesAndTrailingBackslash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TestTempDir();
        var scriptPath = dir.WriteFile("show-arguments.js", """
            for (var index = 0; index < WScript.Arguments.length; index++) {
                WScript.StdOut.WriteLine("<" + WScript.Arguments(index) + ">");
            }
            """);
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var arguments = new[]
        {
            "//nologo", scriptPath,
            "space value", "quote\"value", "C:\\path with space\\",
        };

        var result = await tool.ExecuteAsync(tool.Prepare(Call("cscript.exe", arguments)),
                                             CancellationToken.None);
        var baseline = await RunWithArgumentListAsync("cscript.exe", arguments, dir.Root);

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal(0, baseline.ExitCode);
        Assert.Equal(baseline.Stdout, result.Stdout);
        Assert.Equal(baseline.Stderr, result.Stderr);
    }

    [Fact]
    public async Task Execute_SeparatesStdoutStderrAndReturnsExitCode()
    {
        using var dir = new TestTempDir();
        var artifactRoot = dir.CreateDirectory("artifacts");
        var tool = new ShellTool(dir.Workspace, artifactRoot : artifactRoot);
        var command = PlatformCommand("echo stdout-message", "echo stderr-message 1>&2", "exit 7");

        var result = await tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments)),
                                             CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("stdout-message", result.Stdout);
        Assert.Contains("stderr-message", result.Stderr);
        Assert.Contains("Exit code: 7", result.Content);
    }

    [Fact]
    public async Task Execute_TruncatesInMemoryOutputAndKeepsFullRedactedArtifact()
    {
        using var dir = new TestTempDir();
        const string secret = "secret-value-12345";
        var artifactRoot = dir.CreateDirectory("artifacts");
        var tool = new ShellTool(dir.Workspace,
                                 knownSecrets : new Dictionary<string, string> { ["TEST_SECRET"] = secret },
                                 artifactRoot : artifactRoot, outputCharacterLimit : 40);
        // Put the secret across the capture reader's 4096-character boundary to
        // verify streaming redaction does not depend on process chunking.
        var command = PlatformCommand($"echo {new string('x', 4094)}{secret}abcdefghijklmnopqrstuv");

        var result = await tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments)),
                                             CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.OutputTruncated);
        Assert.NotNull(result.StdoutArtifactPath);
        Assert.Contains("[truncated", result.Stdout);
        Assert.DoesNotContain(secret, result.Content, StringComparison.Ordinal);
        var artifact = await File.ReadAllTextAsync(result.StdoutArtifactPath!);
        Assert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", artifact);
    }

    [Fact]
    public async Task Execute_TimeoutTerminatesProcessTreeAndReturnsTimeoutResult()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var command = SlowCommand();
        var preparation = tool.Prepare(Call(command.Executable, command.Arguments, timeoutSeconds : 1));
        var watch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(preparation, CancellationToken.None);

        // cmd/sh launches ping/sleep as a descendant. Because that descendant
        // inherits the redirected handles, prompt stream completion also proves
        // the process tree was terminated instead of only the parent shell.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Contains("Timed out: true (1s)", result.Content);
    }

    [Fact]
    public async Task Execute_UserCancellationTerminatesProcessAndPropagatesCancellation()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var command = SlowCommand();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments, timeoutSeconds : 10)),
                              cancellation.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Execute_CancellationAfterRootExitTerminatesBackgroundChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var pidPath = Path.Combine(dir.Root, "background.pid");
        var command = BackgroundPowerShellCommand("background.pid");
        using var cancellation = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();

        var execution = tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), cancellation.Token);
        await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(5));
        watch.Restart();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Cancellation took {watch.Elapsed}.");
        Assert.True(File.Exists(pidPath), "The background child did not write its PID before cancellation.");
        var childId = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.True(WaitUntilProcessExited(childId, TimeSpan.FromSeconds(2)),
                    $"Background child process {childId} survived cancellation.");
    }

    [Fact]
    public async Task Execute_TimeoutAfterRootExitTerminatesBackgroundChild()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var pidPath = Path.Combine(dir.Root, "timeout-child.pid");
        var watch = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(
            tool.Prepare(ShellCall("cmd", BackgroundPowerShellCommand("timeout-child.pid"), timeoutSeconds : 2)),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(7));

        Assert.True(result.TimedOut, result.Content);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6), $"Timeout took {watch.Elapsed}.");
        Assert.True(File.Exists(pidPath), "The background child did not write its PID before timeout.");
        var childId = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.True(WaitUntilProcessExited(childId, TimeSpan.FromSeconds(2)),
                    $"Background child process {childId} survived timeout.");
    }

    [Fact]
    public async Task Execute_CaptureFailureImmediatelyTerminatesProcessTree()
    {
        using var dir = new TestTempDir();
        var captureNumber = 0;
        IProcessOutputCapture CreateCapture(string path, int limit, IReadOnlyList<string> secrets)
            => Interlocked.Increment(ref captureNumber) == 1
                ? new FailingOutputCapture()
                : new ProcessOutputCapture(path, limit, secrets);
        var tool = new ShellTool(dir.Workspace, CreateCapture, defaultTimeoutSeconds : 30,
                                 artifactRoot : dir.CreateDirectory("artifacts"));
        var command = SlowCommand();
        var watch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments)),
                              CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("capturing output", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("injected capture failure", error.InnerException?.Message ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Capture failure cleanup took {watch.Elapsed}.");
    }

    private static ChatToolCall Call(string executable, IReadOnlyList<string> arguments,
                                     string workingDirectory = ".", int? timeoutSeconds = null)
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
        if (timeoutSeconds is not null)
        {
            args["timeoutSeconds"] = timeoutSeconds.Value;
        }

        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static ChatToolCall ShellCall(string shell, string command, int? timeoutSeconds = null)
    {
        var args = new JsonObject
        {
            ["mode"] = "shell", ["shell"] = shell, ["command"] = command,
        };
        if (timeoutSeconds is not null)
        {
            args["timeoutSeconds"] = timeoutSeconds.Value;
        }

        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static PlatformProcess PlatformCommand(params string[] commands)
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformProcess(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                       ["/d", "/c", string.Join(" & ", commands)]);
        }

        return new PlatformProcess("/bin/sh", ["-c", string.Join("; ", commands)]);
    }

    private static PlatformProcess SlowCommand()
        => OperatingSystem.IsWindows()
            ? PlatformCommand("ping -n 8 127.0.0.1 >nul")
            : PlatformCommand("sleep 8");

    private static string BackgroundPowerShellCommand(string pidFile)
        => "start \"\" /b powershell.exe -NoLogo -NoProfile -NonInteractive -Command " +
           $"\"$PID | Set-Content -NoNewline -LiteralPath {pidFile}; Start-Sleep -Seconds 10\"";

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for background child marker '{path}'.");
    }

    private static bool WaitUntilProcessExited(int processId, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWithArgumentListAsync(
        string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start baseline process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record PlatformProcess(string Executable, IReadOnlyList<string> Arguments);

    private sealed class FailingOutputCapture : IProcessOutputCapture
    {
        public Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
            => Task.FromException(new IOException("Injected capture failure."));

        public CapturedProcessOutput Complete()
            => throw new InvalidOperationException("A failed capture cannot be completed.");

        public void Discard()
        {
        }
    }
}
