using System.Diagnostics;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         以真实子进程（TinyHarness.IndexProbe 的 catalog 模式）作为另一进程写目录册：窗口侧一次
///         CheckProjectCatalogAsync 即发现新建与改名并保持组实例复用。
///     </para>
///     <para>
///         Uses a real child process (TinyHarness.IndexProbe catalog modes) as the other process writing the
///         catalog: one CheckProjectCatalogAsync discovers the probe's create and rename and keeps reusing group
///         instances.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         探针可执行文件先复制到 %TEMP% 隔离目录再启动，避免直接运行仓库内构建产物；
///         夹具仅位于 %TEMP%，不触碰真实用户数据。
///     </para>
///     <para>
///         The probe executable is copied into an isolated %TEMP% directory before launch rather than run from the
///         repository build output; fixtures live only under %TEMP% and never touch real user data.
///     </para>
/// </remarks>
[TestFixture]
public sealed class DesktopCatalogProbeCrossProcessTests
{
    private const string ProbeExeName = "TinyHarness.IndexProbe.exe";

    private static readonly SessionModelIdentity Identity =
        new("offline", "https://offline.invalid/v1", ChatApiKind.ChatCompletions);

    /// <summary>
    ///     <para>探针子进程创建项目后，一次同步检查发现该项目并正确投影。</para>
    ///     <para>
    ///         After the probe child process creates a project, one sync check discovers it and projects it
    ///         correctly.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ProbeCreatedProjectIsDiscoveredByOneSyncCheck()
    {
        var             fixture = await CatalogProbeFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();
        Assert.That(model.Projects, Is.Empty, "前置：空 store 初始化后无项目组。");

        var (exit, output) = await RunProbeAsync(fixture.Root, "catalog-create", fixture.Store, fixture.Workspace);
        Assert.That(exit, Is.EqualTo(0), output);
        Assert.That(output, Does.Contain("created="), "探针必须报告创建结果。");

        await model.CheckProjectCatalogAsync(CancellationToken.None);

        var group = model.Projects.Single(item => item.Project is not null);
        Assert.That(ProjectRoots.AreEqual(group.Root, fixture.Workspace), Is.True,
                    "窗口必须发现探针创建的项目。");
        Assert.That(group.Name, Is.EqualTo(ProjectRoots.DefaultDisplayName(ProjectRoots.Normalize(fixture.Workspace))),
                    "组名必须与探针写入的目录册一致。");
        Assert.That(model.SidebarRows.OfType<ProjectViewModel>().ToArray(), Has.Length.EqualTo(1),
                    "不得产生重复组。");
    }

    /// <summary>
    ///     <para>探针子进程改名后，一次同步检查更新组名且复用原分组实例。</para>
    ///     <para>
    ///         After the probe child process renames, one sync check updates the group name and reuses the original
    ///         group instance.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ProbeRenameIsAppliedByOneSyncCheckWithReusedInstance()
    {
        var             fixture = await CatalogProbeFixture.CreateAsync();
        await using var model   = fixture.Window();
        await model.InitializeAsync();

        var (exit, output) = await RunProbeAsync(fixture.Root, "catalog-create", fixture.Store, fixture.Workspace);
        Assert.That(exit, Is.EqualTo(0), output);
        await model.CheckProjectCatalogAsync(CancellationToken.None);
        var group     = model.Projects.Single(item => item.CanEdit);
        var projectId = group.Project!.Id;

        var (renameExit, renameOutput) =
            await RunProbeAsync(fixture.Root, "catalog-rename", fixture.Store, projectId, "探针改名项目");
        Assert.That(renameExit, Is.EqualTo(0), renameOutput);

        await model.CheckProjectCatalogAsync(CancellationToken.None);

        Assert.That(model.Projects.Single(item => item.Key == group.Key), Is.SameAs(group),
                    "跨进程改名仍必须复用原分组实例。");
        Assert.That(group.Name, Is.EqualTo("探针改名项目"), "组名必须跟随探针写入的目录册。");
    }

    /// <summary>
    ///     <para>启动探针并等待退出，返回退出码与标准输出全文。</para>
    ///     <para>Starts the probe and waits for exit, returning the exit code and full standard output.</para>
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunProbeAsync(string root, params string[] arguments)
    {
        using var process    = StartProbeProcess(root, arguments);
        var       outputTask = process.StandardOutput.ReadToEndAsync();
        var       errorTask  = process.StandardError.ReadToEndAsync();
        using var timeout    = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw;
        }

        return (process.ExitCode, await outputTask.ConfigureAwait(false) +
                                  await errorTask.ConfigureAwait(false));
    }

    private static Process StartProbeProcess(string root, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName               = CopyProbeExecutable(root),
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            WorkingDirectory       = root
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Probe process failed to start.");
    }

    /// <summary>
    ///     <para>把探针产物整体复制到隔离目录并返回可执行文件路径；缺失时以断言失败报告。</para>
    ///     <para>
    ///         Copies the whole probe output into the isolated directory and returns the executable path; a
    ///         missing build fails the assertion.
    ///     </para>
    /// </summary>
    private static string CopyProbeExecutable(string root)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TinyHarness.slnx")))
            current = current.Parent;
        Assert.That(current, Is.Not.Null, "未找到仓库根 TinyHarness.slnx。");
        var probeBin = Path.Combine(current!.FullName, "TinyHarness.IndexProbe", "bin");
        Assert.That(Directory.Exists(probeBin), Is.True, "TinyHarness.IndexProbe 尚未构建（缺少 bin 输出）。");
        var probeDirectory = ResolveProbeOutputDirectory(probeBin);

        var target = Path.Combine(root, "probe");
        Directory.CreateDirectory(target);
        // 整树复制：原生 e_sqlite3 位于 runtimes/ 子树，平铺复制会丢掉原生加载路径。
        foreach (var file in Directory.GetFiles(probeDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(probeDirectory, file);
            if (file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || relative.Contains("browser-wasm"))
                continue;
            var targetPath = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, true);
        }

        return Path.Combine(target, ProbeExeName);
    }

    /// <summary>
    ///     <para>优先选取与测试进程相同配置的探针输出目录；未命中时回退为最新修改时间的产物目录。</para>
    ///     <para>
    ///         Prefers the probe output directory matching the test process's own configuration, falling back to
    ///         the most recently modified one when that misses.
    ///     </para>
    /// </summary>
    private static string ResolveProbeOutputDirectory(string probeBin)
    {
        // 测试自身输出形如 ...\bin\{配置}\{目标框架}\；同配置探针产物优先，Debug/Release 并存时不选错。
        var segments = AppContext.BaseDirectory.Split(Path.DirectorySeparatorChar,
                                                      Path.AltDirectorySeparatorChar);
        var binIndex = Array.LastIndexOf(segments, "bin");
        if (binIndex >= 0 && binIndex + 2 < segments.Length)
        {
            var sameConfiguration = Path.Combine(probeBin, segments[binIndex + 1], segments[binIndex + 2],
                                                 ProbeExeName);
            if (File.Exists(sameConfiguration))
                return Path.GetDirectoryName(sameConfiguration)!;
        }

        var newest = Directory.EnumerateFiles(probeBin, ProbeExeName, SearchOption.AllDirectories)
                              .Where(path =>
                                         !path
                                            .Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}"))
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();
        Assert.That(newest, Is.Not.Null, "TinyHarness.IndexProbe bin 中没有探针可执行文件。");
        return Path.GetDirectoryName(newest)!;
    }

    private sealed class CatalogProbeFixture
    {
        public required string      Root        { get; init; }
        public required string      Workspace   { get; init; }
        public required string      Store       { get; init; }
        public required string      Config      { get; init; }
        public          Credentials Credentials { get; } = new();
        public          int         Models      { get; private set; }

        public MainWindowViewModel Window() => new(new DesktopSessionFactory((_, _) =>
        {
            Models++;
            return new Model();
        }, Credentials, Config), Config, action =>
        {
            action();
            return ValueTask.CompletedTask;
        });

        public static async Task<CatalogProbeFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-catalog-probe-" + Guid.NewGuid().ToString("N"));
            var fixture = new CatalogProbeFixture
            {
                Root      = root,
                Workspace = Path.Combine(root, "work-a"),
                Store     = Path.Combine(root, "sessions"),
                Config    = Path.Combine(root, "user.json")
            };
            Directory.CreateDirectory(fixture.Workspace);
            Directory.CreateDirectory(fixture.Store);
            await UserConfigStore.SaveAsync(fixture.Config, new UserConfig
            {
                DefaultProfile = "offline", Profiles =
                [
                    new UserProfile
                    {
                        Name         = "offline", Endpoint = Identity.Endpoint, ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models       = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = fixture.Store }
            }, CancellationToken.None);
            return fixture;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool IsSupported => true;

        public string? Read(string targetName) => "offline-fixture-key";

        public void Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool Delete(string targetName) => throw new NotSupportedException();
    }

    private sealed class Model : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                     [EnumeratorCancellation]
                                                                     CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatStreamEvent
                { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "offline answer" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}
