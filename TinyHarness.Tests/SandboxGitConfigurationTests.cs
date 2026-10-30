using System.Diagnostics;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>验证沙箱 Git 的精确工作区信任、配置合并和真实 Git 所有者检查。</para>
///     <para>Verifies exact workspace trust, configuration merging, and real Git ownership checks.</para>
/// </summary>
public class SandboxGitConfigurationTests
{
    [Test]
    public async Task Plan_GitTrustsOnlyWorkspaceEvenWhenExistingConfigurationTrustsEverything()
    {
        using var directory = new TestTempDir();
        var       workspace = directory.CreateDirectory("workspace with spaces");
        var       outside   = directory.CreateDirectory("outside");
        Assert.That((await RunGit(workspace, null, "init")).ExitCode, Is.Zero);
        Assert.That((await RunGit(outside, null, "init")).ExitCode, Is.Zero);
        var environment = Plan(directory.Root, workspace, new Dictionary<string, string>
        {
            ["git_config_count"]   = "2",
            ["git_config_key_0"]   = "safe.directory",
            ["git_config_value_0"] = "*",
            ["git_config_key_1"]   = "core.abbrev",
            ["git_config_value_1"] = "9"
        });

        var config = await RunGit(workspace, environment, "config", "--get", "core.abbrev");
        Assert.That(config.ExitCode, Is.Zero, config.Text);
        Assert.That(config.Text.Trim(), Is.EqualTo("9"));
        foreach (var arguments in new[] { new[] { "status", "--porcelain" }, new[] { "diff", "--stat" } })
        {
            var result = await RunGit(workspace, environment, arguments);
            Assert.That(result.ExitCode, Is.Zero, result.Text);
        }

        var rejected = await RunGit(outside, environment, "status", "--porcelain");
        Assert.That(rejected.ExitCode, Is.Not.Zero, rejected.Text);
        Assert.That(rejected.Text, Does.Contain("dubious ownership"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0")]
    public void Plan_EmptyConfigurationAddsOnlyExactWorkspace(string? count)
    {
        using var directory = new TestTempDir();
        var       extras = new Dictionary<string, string>();
        if (count is not null) extras["GIT_CONFIG_COUNT"] = count;
        var environment = Plan(directory.Root, directory.Root + Path.DirectorySeparatorChar, extras);
        Assert.That(environment["GIT_CONFIG_COUNT"], Is.EqualTo("2"));
        Assert.That(environment["GIT_CONFIG_VALUE_0"], Is.Empty);
        Assert.That(environment["GIT_CONFIG_VALUE_1"], Is.EqualTo(directory.Root.Replace('\\', '/')));
        Assert.That(extras.ContainsKey("GIT_CONFIG_KEY_0"), Is.False);
    }

    [TestCase("-1")]
    [TestCase("abc")]
    [TestCase("2147483647")]
    [TestCase("1025")]
    [TestCase("1")]
    public void Plan_RejectsMalformedOrIncompleteConfiguration(string count)
    {
        using var directory = new TestTempDir();
        Assert.Throws<InvalidOperationException>(() => Plan(directory.Root, directory.Root,
                                                            new Dictionary<string, string>
                                                                { ["GIT_CONFIG_COUNT"] = count }));
    }

    private static IReadOnlyDictionary<string, string> Plan(
        string root, string workspace, IReadOnlyDictionary<string, string> extras) =>
        WindowsSandboxPolicyPlanner.Plan(new WindowsSandboxSettings
        {
            SetupExecutablePath  = Path.Combine(root, "setup.exe"),
            RunnerExecutablePath = Path.Combine(root, "runner.exe"),
            SandboxHome          = Path.Combine(root, "home"),
            ExtraEnvironment     = extras
        }, workspace).TargetEnvironment;

    private static async Task<(int ExitCode, string Text)> RunGit(
        string workspace, IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory       = workspace,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        if (environment is not null)
        {
            start.Environment.Clear();
            foreach (var (key, value) in environment) start.Environment[key] = value;
            // Git 自带的测试开关强制触发所有者检查，避免依赖真实账户或修改 fixture Owner。
            start.Environment["GIT_TEST_ASSUME_DIFFERENT_OWNER"] = "1";
        }

        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var       output  = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var       error   = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
