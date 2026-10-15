using System.Diagnostics;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ShellToolTests
{
    [Test]
    public void Prepare_NormalizesStructuredCommandAndBindsSessionConstraint()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory("src");
        var tool = new ShellTool(dir.Workspace, 45);

        var preparation = tool.Prepare(Call("dotnet", ["test", "--no-restore"], "src"));

        Assert.That(preparation.Capability, Is.EqualTo("process.execute"));
        Assert.That(preparation.Arguments["mode"]!.GetValue<string>(), Is.EqualTo("direct"));
        Assert.That(preparation.TargetPaths, Is.EqualTo([Path.Combine(dir.Root, "src")]));
        Assert.That(preparation.Arguments["executable"]!.GetValue<string>(), Is.EqualTo("dotnet"));
        Assert.That(preparation.Arguments["timeoutSeconds"]!.GetValue<int>(), Is.EqualTo(45));
        Assert.That(preparation.SessionConstraint, Does.Contain("dotnet"));
        Assert.That(preparation.SessionConstraint, Does.Contain("--no-restore"));
    }

    [Test]
    public void Prepare_RejectsWorkingDirectoryEscapeAndInvalidArguments()
    {
        using var dir  = new TestTempDir();
        var       tool = new ShellTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => tool.Prepare(Call("dotnet", ["test"], "..")));
        TestAssert.Throws<InvalidDataException>(() => tool.Prepare(new ChatToolCall("shell", "shell",
                                                                            """{"executable":"dotnet","arguments":[1]}""")));
        TestAssert.Throws<ArgumentOutOfRangeException>(() => tool.Prepare(new ChatToolCall("shell", "shell",
                                                                                   """{"executable":"dotnet","timeoutSeconds":0}""")));
        TestAssert.Throws<InvalidDataException>(() => tool.Prepare(new ChatToolCall("shell", "shell",
                                                                            """{"mode":"shell","shell":"powershell","command":"Get-Date","executable":"dotnet"}""")));
    }

    [Test]
    public void Prepare_ShellModeFreezesInterpreterAndMarksElevatedRisk()
    {
        using var dir = new TestTempDir();
        var tool = new ShellTool(dir.Workspace);
        var shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var command = OperatingSystem.IsWindows() ? "Write-Output one; Write-Output two" : "printf one; printf two";

        var preparation = tool.Prepare(ShellCall(shell, command));

        Assert.That(preparation.Arguments["mode"]!.GetValue<string>(), Is.EqualTo("shell"));
        Assert.That(preparation.Arguments["shell"]!.GetValue<string>(), Is.EqualTo(shell));
        Assert.That(preparation.Arguments["command"]!.GetValue<string>(), Is.EqualTo(command));
        Assert.That(preparation.RiskLevel, Is.EqualTo(ToolRiskLevel.Elevated));
        Assert.That(preparation.SessionConstraint, Does.Contain(command));
        TestAssert.DoesNotContain("Start-Process", preparation.SessionConstraint, StringComparison.Ordinal);
    }

    [Test]
    public async Task Execute_ShellModeSupportsPipelinesAndRedirection()
    {
        using var dir   = new TestTempDir();
        var       tool  = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var command = OperatingSystem.IsWindows()
            ? "'alpha' | ForEach-Object { $_.ToUpperInvariant() }; 'written' | Set-Content -NoNewline -LiteralPath shell-result.txt"
            : "printf alpha | tr '[:lower:]' '[:upper:]'; printf written > shell-result.txt";

        var result = await tool.ExecuteAsync(tool.Prepare(ShellCall(shell, command)), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(result.Stdout, Does.Contain("ALPHA"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "shell-result.txt")), Is.EqualTo("written"));
    }

    [Test]
    public async Task Execute_CmdShellPreservesQuotedCommandAndCombinationSyntax()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir       = new TestTempDir();
        var       tool      = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       nestedCmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var       command   = $"\"{nestedCmd}\" /d /c echo nested-ok & echo \"a b\" & (echo combined-ok)";

        var result = await tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), CancellationToken.None);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(result.Stdout, Does.Contain("nested-ok"));
        Assert.That(result.Stdout, Does.Contain("\"a b\""));
        Assert.That(result.Stdout, Does.Contain("combined-ok"));
        TestAssert.DoesNotContain("\\\"a b\\\"", result.Stdout, StringComparison.Ordinal);
    }

    [Test]
    public async Task Execute_DirectModePreservesSpacesQuotesAndTrailingBackslash()
    {
        if (!OperatingSystem.IsWindows()) return;

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
            "space value", "quote\"value", "C:\\path with space\\"
        };

        var result = await tool.ExecuteAsync(tool.Prepare(Call("cscript.exe", arguments)),
                                             CancellationToken.None);
        var baseline = await RunWithArgumentListAsync("cscript.exe", arguments, dir.Root);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(baseline.ExitCode, Is.EqualTo(0));
        Assert.That(result.Stdout, Is.EqualTo(baseline.Stdout));
        Assert.That(result.Stderr, Is.EqualTo(baseline.Stderr));
    }

    [Test]
    public async Task Execute_SeparatesStdoutStderrAndReturnsExitCode()
    {
        using var dir          = new TestTempDir();
        var       artifactRoot = dir.CreateDirectory("artifacts");
        var       tool         = new ShellTool(dir.Workspace, artifactRoot : artifactRoot);
        var       command      = PlatformCommand("echo stdout-message", "echo stderr-message 1>&2", "exit 7");

        var result = await tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments)),
                                             CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.ExitCode, Is.EqualTo(7));
        Assert.That(result.TimedOut, Is.False);
        Assert.That(result.Stdout, Does.Contain("stdout-message"));
        Assert.That(result.Stderr, Does.Contain("stderr-message"));
        Assert.That(result.Content, Does.Contain("Exit code: 7"));
    }

    [Test]
    public async Task Execute_TruncatesInMemoryOutputAndKeepsFullRedactedArtifact()
    {
        using var    dir          = new TestTempDir();
        const string secret       = "secret-value-12345";
        var          artifactRoot = dir.CreateDirectory("artifacts");
        var tool = new ShellTool(dir.Workspace,
                                 knownSecrets : new Dictionary<string, string> { ["TEST_SECRET"] = secret },
                                 artifactRoot : artifactRoot, outputCharacterLimit : 40);
        // 把密钥放在捕获读取器 4096 字符边界两侧，验证流式脱敏不依赖进程分块。
        var command = PlatformCommand($"echo {new string('x', 4094)}{secret}abcdefghijklmnopqrstuv");

        var result = await tool.ExecuteAsync(tool.Prepare(Call(command.Executable, command.Arguments)),
                                             CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.OutputTruncated, Is.True);
        TestAssert.NotNull(result.StdoutArtifactPath);
        Assert.That(result.Stdout, Does.Contain("[truncated"));
        TestAssert.DoesNotContain(secret, result.Content, StringComparison.Ordinal);
        var artifact = await File.ReadAllTextAsync(result.StdoutArtifactPath!);
        TestAssert.DoesNotContain(secret, artifact, StringComparison.Ordinal);
        Assert.That(artifact, Does.Contain("[REDACTED]"));
    }

    [Test]
    public async Task Execute_TimeoutTerminatesProcessTreeAndReturnsTimeoutResult()
    {
        using var dir         = new TestTempDir();
        var       tool        = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       command     = SlowCommand();
        var       preparation = tool.Prepare(Call(command.Executable, command.Arguments, timeoutSeconds : 1));
        var       watch       = Stopwatch.StartNew();

        var result = await tool.ExecuteAsync(preparation, CancellationToken.None);

        // cmd/sh 把 ping/sleep 作为子进程启动。子进程继承重定向句柄，因此提示流完成
        // 也证明整棵进程树被终止，而不只是父 shell。
        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(5), Is.True);
        Assert.That(result.TimedOut, Is.True);
        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("Timed out: true (1s)"));
    }

    [Test]
    public async Task Execute_UserCancellationTerminatesProcessAndPropagatesCancellation()
    {
        using var dir          = new TestTempDir();
        var       tool         = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       command      = SlowCommand();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var       watch        = Stopwatch.StartNew();

        await Assert.CatchAsync<OperationCanceledException>(() =>
                                                                tool.ExecuteAsync(tool.Prepare(Call(command
                                                                                              .Executable,
                                                                                           command.Arguments,
                                                                                           timeoutSeconds : 10)),
                                                                         cancellation.Token));

        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(5), Is.True);
    }

    [Test]
    public async Task Execute_CancellationAfterRootExitTerminatesBackgroundChild()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir          = new TestTempDir();
        var       tool         = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       pidPath      = Path.Combine(dir.Root, "background.pid");
        var       command      = BackgroundPowerShellCommand("background.pid");
        using var cancellation = new CancellationTokenSource();
        var       watch        = Stopwatch.StartNew();

        var execution = tool.ExecuteAsync(tool.Prepare(ShellCall("cmd", command)), cancellation.Token);
        await WaitForFileAsync(pidPath, TimeSpan.FromSeconds(5));
        watch.Restart();
        cancellation.Cancel();
        await Assert.CatchAsync<OperationCanceledException>(async () =>
                                                                await execution.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(4), Is.True, $"Cancellation took {watch.Elapsed}.");
        Assert.That(File.Exists(pidPath), Is.True, "The background child did not write its PID before cancellation.");
        var childId = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.That(WaitUntilProcessExited(childId, TimeSpan.FromSeconds(2)), Is.True,
                    $"Background child process {childId} survived cancellation.");
    }

    [Test]
    public async Task Execute_TimeoutAfterRootExitTerminatesBackgroundChild()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var dir     = new TestTempDir();
        var       tool    = new ShellTool(dir.Workspace, artifactRoot : dir.CreateDirectory("artifacts"));
        var       pidPath = Path.Combine(dir.Root, "timeout-child.pid");
        var       watch   = Stopwatch.StartNew();

        var result = await tool
                          .ExecuteAsync(tool.Prepare(ShellCall("cmd", BackgroundPowerShellCommand("timeout-child.pid"),
                                                               2)), CancellationToken.None)
                          .WaitAsync(TimeSpan.FromSeconds(7));

        Assert.That(result.TimedOut, Is.True, result.Content);
        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(6), Is.True, $"Timeout took {watch.Elapsed}.");
        Assert.That(File.Exists(pidPath), Is.True, "The background child did not write its PID before timeout.");
        var childId = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.That(WaitUntilProcessExited(childId, TimeSpan.FromSeconds(2)), Is.True,
                    $"Background child process {childId} survived timeout.");
    }

    [Test]
    public async Task Execute_CaptureFailureImmediatelyTerminatesProcessTree()
    {
        using var dir           = new TestTempDir();
        var       captureNumber = 0;

        IProcessOutputCapture CreateCapture(string path, int limit, IReadOnlyList<string> secrets) =>
            Interlocked.Increment(ref captureNumber) == 1
                ? new FailingOutputCapture()
                : new ProcessOutputCapture(path, limit, secrets);

        var tool = new ShellTool(dir.Workspace, CreateCapture, 30,
                                 dir.CreateDirectory("artifacts"));
        // 分块推送式捕获只在输出数据到达时暴露注入故障，探针必须持续输出 stdout，
        // 不能在整个超时窗口内静默运行。
        var command = OutputtingSlowCommand();
        var watch   = Stopwatch.StartNew();

        var error =
            await TestAssert.ThrowsAsync<IOException>(() => tool
                                                           .ExecuteAsync(tool.Prepare(Call(command.Executable,
                                                                                           command.Arguments)),
                                                                         CancellationToken.None)
                                                           .WaitAsync(TimeSpan.FromSeconds(5)));

        TestAssert.Contains("capturing output", error.Message, StringComparison.OrdinalIgnoreCase);
        TestAssert.Contains("injected capture failure", error.InnerException?.Message ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase);
        Assert.That(watch.Elapsed < TimeSpan.FromSeconds(4), Is.True, $"Capture failure cleanup took {watch.Elapsed}.");
    }

    private static ChatToolCall Call(string executable, IReadOnlyList<string> arguments, string workingDirectory = ".",
                                     int?   timeoutSeconds = null)
    {
        var array = new JsonArray();
        foreach (var argument in arguments) array.Add((JsonNode?)JsonValue.Create(argument));

        var args = new JsonObject
        {
            ["executable"]       = executable,
            ["arguments"]        = array,
            ["workingDirectory"] = workingDirectory
        };
        if (timeoutSeconds is not null) args["timeoutSeconds"] = timeoutSeconds.Value;

        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static ChatToolCall ShellCall(string shell, string command, int? timeoutSeconds = null)
    {
        var args = new JsonObject
        {
            ["mode"]    = "shell",
            ["shell"]   = shell,
            ["command"] = command
        };
        if (timeoutSeconds is not null) args["timeoutSeconds"] = timeoutSeconds.Value;

        return new ChatToolCall("shell-call", "shell", args.ToJsonString());
    }

    private static PlatformProcess PlatformCommand(params string[] commands)
    {
        if (OperatingSystem.IsWindows())
            return new PlatformProcess(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                                       ["/d", "/c", string.Join(" & ", commands)]);

        return new PlatformProcess("/bin/sh", ["-c", string.Join("; ", commands)]);
    }

    private static PlatformProcess SlowCommand() =>
        OperatingSystem.IsWindows()
            ? PlatformCommand("ping -n 8 127.0.0.1 >nul")
            : PlatformCommand("sleep 8");

    private static PlatformProcess OutputtingSlowCommand() =>
        OperatingSystem.IsWindows()
            ? PlatformCommand("ping -n 8 127.0.0.1")
            : PlatformCommand("ping -c 8 127.0.0.1");

    private static string BackgroundPowerShellCommand(string pidFile) =>
        "start \"\" /b powershell.exe -NoLogo -NoProfile -NonInteractive -Command " +
        $"\"$PID | Set-Content -NoNewline -LiteralPath {pidFile}; Start-Sleep -Seconds 10\"";

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (File.Exists(path)) return;

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
                if (process.HasExited) return true;
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
            FileName               = executable,
            WorkingDirectory       = workingDirectory,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("Failed to start baseline process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private sealed record PlatformProcess(string Executable, IReadOnlyList<string> Arguments);

    private sealed class FailingOutputCapture : IProcessOutputCapture
    {
        public ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("Injected capture failure."));

        public CapturedProcessOutput Complete() =>
            throw new InvalidOperationException("A failed capture cannot be completed.");

        public void Discard()
        {
        }
    }
}
