using System.Diagnostics;
using TinyHarness.Cli.Commands;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         以隔离子进程的真实工作目录覆盖"工作目录中的 tinyharness.json 不参与配置解析、
///         诊断命令不向工作区写入"的边界；进程工作目录是全局状态，无法在测试进程内安全模拟。
///     </para>
///     <para>
///         Covers the boundaries that a tinyharness.json in the working directory never participates in config
///         resolution and that diagnostic commands write nothing, using isolated subprocesses with a real working
///         directory; the process working directory is global state that cannot be simulated safely in-process.
///     </para>
/// </summary>
public sealed class ConfigWorkingDirectorySubprocessTests
{
    [Test]
    public void ConfigShow_WithValidProjectFileInRealWorkingDirectory_UsesUserConfigOnly()
    {
        using var fixture =
            new SubprocessFixture("""{"endpoint":"https://project.invalid/v1","model":"project-model"}""");
        var result = RunCli(fixture.Workspace, fixture.UserConfigDirectory, "config", "show");
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        Assert.That(result.Output, Does.Contain("user-model"));
        Assert.That(result.Output, Does.Contain("https://work.invalid/v1"));
        Assert.That(result.Output, Does.Contain("user config"));
        Assert.That(result.Output, Does.Not.Contain("project-model"));
        // 工作区文件集合在命令前后保持不变。
        Assert.That(result.FilesAfter, Is.EqualTo(result.FilesBefore));
    }

    [Test]
    public void ConfigShow_WithMalformedProjectFileInRealWorkingDirectory_StillInitializes()
    {
        using var fixture = new SubprocessFixture("{ not valid json");
        var       result  = RunCli(fixture.Workspace, fixture.UserConfigDirectory, "config", "show");
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        Assert.That(result.Output, Does.Contain("user-model"));
        Assert.That(result.FilesAfter, Is.EqualTo(result.FilesBefore));
    }

    [Test]
    public void Doctor_InRealWorkingDirectory_CreatesNothing()
    {
        using var fixture =
            new SubprocessFixture("""{"endpoint":"https://project.invalid/v1","model":"project-model"}""");
        var result = RunCli(fixture.Workspace, fixture.UserConfigDirectory, "doctor");
        Assert.That(result.ExitCode, Is.Zero, result.Error);
        // 默认 sessionDirectory 指向工作区内，doctor 只报告存在性，不创建该目录。
        Assert.That(result.Output, Does.Contain("user-model"));
        Assert.That(result.Output, Does.Contain("is not present; doctor creates nothing"));
        Assert.That(result.FilesAfter, Is.EqualTo(result.FilesBefore));
    }

    /// <summary>
    ///     <para>在指定工作目录运行 CLI 子进程；用户配置目录经环境变量隔离。</para>
    ///     <para>
    ///         Runs the CLI subprocess with the given working directory; the user config directory is isolated via
    ///         environment.
    ///     </para>
    /// </summary>
    private static SubprocessResult RunCli(
        string workspace, string userConfigDirectory, params string[] arguments)
    {
        var before = SnapshotFiles(workspace);
        var start = new ProcessStartInfo
        {
            FileName               = DotnetHostPath(),
            UseShellExecute        = false,
            WorkingDirectory       = workspace,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(typeof(ConfigCommand).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[UserConfigStore.DirectoryOverrideVariable] = userConfigDirectory;

        using var process = Process.Start(start)!;
        var       stdout  = process.StandardOutput.ReadToEndAsync();
        var       stderr  = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(true);
            Assert.Fail("CLI subprocess timed out.");
        }

        return new SubprocessResult(process.ExitCode, stdout.Result, stderr.Result,
                                    SnapshotFiles(workspace), before);
    }

    /// <summary>
    ///     <para>解析 dotnet 主机路径；`dotnet test` 注入 DOTNET_HOST_PATH，缺失时回退 PATH。</para>
    ///     <para>Resolves the dotnet host path; `dotnet test` injects DOTNET_HOST_PATH, PATH is the fallback.</para>
    /// </summary>
    private static string DotnetHostPath() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : "dotnet";

    /// <summary>
    ///     <para>递归枚举目录内文件的相对路径集合，用于前后一致性比较。</para>
    ///     <para>Enumerates files recursively as relative paths for before/after comparison.</para>
    /// </summary>
    private static IReadOnlyList<string> SnapshotFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                       .Select(path => Path.GetRelativePath(root, path))
                       .OrderBy(path => path, StringComparer.Ordinal)
                       .ToArray()
            : [];

    private sealed record SubprocessResult(
        int                   ExitCode,
        string                Output,
        string                Error,
        IReadOnlyList<string> FilesAfter,
        IReadOnlyList<string> FilesBefore);

    /// <summary>
    ///     <para>独立临时目录夹具：工作区（可含待测项目文件）与隔离的用户配置目录。</para>
    ///     <para>Isolated temp fixture: a workspace (optionally containing the project file) and a user config directory.</para>
    /// </summary>
    private sealed class SubprocessFixture : IDisposable
    {
        public SubprocessFixture(string? projectFileContent)
        {
            Root                = Path.Combine(Path.GetTempPath(), $"tinyharness-cwd-{Guid.NewGuid():N}");
            Workspace           = Path.Combine(Root, "ws");
            UserConfigDirectory = Path.Combine(Root, "user");
            Directory.CreateDirectory(Workspace);
            Directory.CreateDirectory(UserConfigDirectory);
            if (projectFileContent is not null)
                File.WriteAllText(Path.Combine(Workspace, "tinyharness.json"), projectFileContent);

            UserConfigStore
               .SaveAsync(Path.Combine(UserConfigDirectory, UserConfigStore.FileName),
                          new UserConfig
                          {
                              DefaultProfile = "work",
                              Profiles =
                              [
                                  new UserProfile
                                  {
                                      Name         = "work",
                                      Endpoint     = "https://work.invalid/v1",
                                      DefaultModel = "user-model",
                                      Models =
                                      [
                                          new UserProfileModel { Id = "user-model", ContextWindowTokens = 64_000 }
                                      ]
                                  }
                              ]
                          },
                          CancellationToken.None).GetAwaiter().GetResult();
        }

        public string Root { get; }

        public string Workspace { get; }

        public string UserConfigDirectory { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
