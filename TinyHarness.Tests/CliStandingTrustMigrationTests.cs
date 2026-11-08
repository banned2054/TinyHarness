using System.Diagnostics;
using System.Text.Json.Nodes;
using TinyHarness.Cli;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         CLI chat 常设信任迁移交互（真实 chat 入口，进程级）：配置存在未确认常设扩大设置且输入为
///         重定向流时，启动被阻断（exit 1）且不改任何设置。y/n 交互分支需要真实交互式控制台
///         （<see cref="Console" />.IsInputRedirected 为 false 才可达），进程级夹具无法覆盖，其落盘
///         语义由 <see cref="StandingTrustMigrationTests" /> 按同一记录形状固化。
///     </para>
///     <para>
///         The CLI chat standing-trust migration interaction (real chat entry, process level): with
///         unconfirmed standing expansion in the config and redirected input, startup is blocked
///         (exit 1) without changing any settings. The y/n interactive branches require a real
///         interactive console (<see cref="Console" />.IsInputRedirected must be false) and are out of
///         reach for process-level fixtures; their persistence semantics are pinned by
///         <see cref="StandingTrustMigrationTests" /> with the same record shape.
///     </para>
/// </summary>
public class CliStandingTrustMigrationTests
{
    private const int ExitTimeoutMs = 60_000;

    private const int RunTimeoutMs = 120_000;

    private static readonly string[] CompletionChunks =
    [
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
        "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n",
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
        "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n",
        "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
        "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n",
        "data: [DONE]\n\n"
    ];

    [Test]
    public async Task Chat_PendingStandingTrust_RedirectedInput_BlocksStartupWithoutChangingSettings()
    {
        var       cliDll     = LocateCliDll();
        using var configDir  = new TestTempDir();
        var       configPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
        // 常设网络信任命中（enabled）且无确认记录 = Pending；无 permissions 节，避免旧配置迁移分支。
        const string rawConfig =
            """{"settings":{"windowsSandbox":{"enabled":true,"networkPolicy":"enabled"}}}""";
        await File.WriteAllTextAsync(configPath, rawConfig);

        var start = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = $"exec \"{cliDll}\" chat",
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };
        start.Environment[UserConfigStore.DirectoryOverrideVariable] = configDir.Root;

        using var process = Process.Start(start)!;
        // 关闭 stdin 即非交互（重定向流）；确认入口必须在任何模型/沙箱组合前阻断。
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ExitTimeoutMs))
        {
            process.Kill(true);
            Assert.Fail("chat did not exit within the timeout; the standing-trust gate must block synchronously.");
        }

        var output = await stdoutTask + await stderrTask;
        var after  = await File.ReadAllTextAsync(configPath);

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.EqualTo(1), output);
            Assert.That(output, Does.Contain("requires explicit confirmation"));
            Assert.That(output, Does.Contain("redirected input cannot confirm"));
            Assert.That(after, Is.EqualTo(rawConfig), "阻断启动不得改动任何设置。");
        });
    }

    [Test]
    public async Task Run_PendingStandingTrust_NoticesWithoutBlocking_AndCompletesWithExitZero()
    {
        var       cliDll       = LocateCliDll();
        using var configDir    = new TestTempDir();
        using var workspace    = new TestTempDir();
        using var sessionsRoot = new TestTempDir();
        using var mockServer   = CreateMockServer();
        var       configPath   = Path.Combine(configDir.Root, UserConfigStore.FileName);
        // Pending 常设信任（enabled+无记录）+ 指向本地 mock SSE 端点的完整 profile：
        // run 不阻断，提示后按逐次审批语义继续运行到正常退出。
        var config = new JsonObject
        {
            ["defaultProfile"] = "mock",
            ["profiles"] = new JsonArray(new JsonObject
            {
                ["name"]                      = "mock",
                ["endpoint"]                  = mockServer.BaseUrl,
                ["apiKeyEnvironmentVariable"] = "TH_RUN_NOTICE_KEY",
                ["chatApi"]                   = "chat-completions",
                ["defaultModel"]              = "mock-model",
                ["models"] = new JsonArray(new JsonObject
                {
                    ["id"] = "mock-model", ["contextWindowTokens"] = 64000
                })
            }),
            ["settings"] = new JsonObject
            {
                ["windowsSandbox"] = new JsonObject
                {
                    ["enabled"] = true, ["networkPolicy"] = "enabled"
                },
                // 会话日志落在夹具目录，不写机器会话库。
                ["sessionStoreDirectory"] = sessionsRoot.Root
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        // 组合期基线读保护要求受保护路径（workspace-trust.json）已存在（覆盖内缺失 fail-closed）；
        // 预置真实信任记录但标记为不受信：run 保持只读、不组合沙箱，可到达 exit 0。
        var trustPath  = Path.Combine(configDir.Root, "workspace-trust.json");
        var trustStore = new WorkspaceTrustStore(trustPath, [workspace.Root]);
        await trustStore.SaveAsync(WorkspaceDirectoryIdentity.Capture(workspace.Root), false,
                                   CancellationToken.None);
        mockServer.EnqueueRaw(
                              "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
                              "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
                              "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
                              "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\n" +
                              "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"mock\"," +
                              "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                              "data: [DONE]\n\n");

        var start = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = $"exec \"{cliDll}\" run \"reply with a short greeting\"",
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workspace.Root
        };
        start.Environment[UserConfigStore.DirectoryOverrideVariable] = configDir.Root;
        start.Environment["TH_RUN_NOTICE_KEY"]                       = "dummy-key";

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeoutMs))
        {
            process.Kill(true);
            Assert.Fail("run did not exit within the timeout.");
        }

        var output = await stdoutTask + await stderrTask;
        var after  = await File.ReadAllTextAsync(configPath);

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.EqualTo(0), output);
            // 非阻断提示必须出现在 stderr 且含语义标记；run 不得改写配置（确认只发生在 chat）。
            Assert.That(output, Does.Contain("is not confirmed yet"));
            Assert.That(output, Does.Contain("follow per-call approval"));
            Assert.That(output, Does.Contain("'tinyharness chat'"));
            Assert.That(after, Does.Not.Contain("standingTrust"));
        });
    }

    /// <summary>每个测试独占的本地 mock SSE 端点；子进程 CLI 直连环回，无外部网络。</summary>
    /// <summary>A per-test local mock SSE endpoint; the child CLI connects over loopback only.</summary>
    private static MockSseServer CreateMockServer()
    {
        var server = new MockSseServer();
        server.Start();
        return server;
    }

    /// <summary>
    ///     <para>
    ///         B6-0 未信任工作区入口回归：沙箱 enabled、trust.json 不存在 → run 恢复基线入口行为——
    ///         模型调用可达（exit 0）、process execute fail-closed（头部声明 process execution disabled）、
    ///         stderr 明确未信任提示、配置与信任存储均未被改写。
    ///     </para>
    ///     <para>
    ///         The B6-0 untrusted-workspace entry regression: with the sandbox enabled and no trust store,
    ///         run keeps the baseline entry behavior — the model call stays reachable (exit 0), process
    ///         execution fails closed (the header declares process execution disabled), stderr names the
    ///         untrusted workspace, and neither the config nor the trust store is written.
    ///     </para>
    /// </summary>
    [Test]
    public async Task Run_UntrustedWorkspace_KeepsTheModelReachable_AndDeniesProcessExecution()
    {
        var       cliDll       = LocateCliDll();
        using var configDir    = new TestTempDir();
        using var workspace    = new TestTempDir();
        using var sessionsRoot = new TestTempDir();
        using var mockServer   = CreateMockServer();
        var       configPath   = Path.Combine(configDir.Root, UserConfigStore.FileName);
        var       trustPath    = Path.Combine(configDir.Root, "workspace-trust.json");
        var config = new JsonObject
        {
            ["defaultProfile"] = "mock",
            ["profiles"] = new JsonArray(new JsonObject
            {
                ["name"]                      = "mock",
                ["endpoint"]                  = mockServer.BaseUrl,
                ["apiKeyEnvironmentVariable"] = "TH_RUN_NOTICE_KEY",
                ["chatApi"]                   = "chat-completions",
                ["defaultModel"]              = "mock-model",
                ["models"] = new JsonArray(new JsonObject
                {
                    ["id"] = "mock-model", ["contextWindowTokens"] = 64000
                })
            }),
            ["settings"] = new JsonObject
            {
                ["windowsSandbox"]        = new JsonObject { ["enabled"] = true },
                // 会话日志落在夹具目录，不写机器会话库。
                ["sessionStoreDirectory"] = sessionsRoot.Root
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        // 一次请求取一个完整响应体：逐块入队会让首轮请求只拿到无终止符的半截流。
        mockServer.EnqueueRaw(string.Concat(CompletionChunks));

        var start = new ProcessStartInfo
        {
            FileName               = "dotnet",
            Arguments              = $"exec \"{cliDll}\" run \"reply with a short greeting\"",
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = workspace.Root
        };
        start.Environment[UserConfigStore.DirectoryOverrideVariable] = configDir.Root;
        start.Environment["TH_RUN_NOTICE_KEY"]                       = "dummy-key";

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeoutMs))
        {
            process.Kill(true);
            Assert.Fail("run did not exit within the timeout.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Multiple(() =>
        {
            Assert.That(process.ExitCode, Is.EqualTo(0), stdout + stderr);
            Assert.That(stdout, Does.Contain("mock-model"), "模型调用必须可达。");
            Assert.That(stdout, Does.Contain("workspace read-only (process execution disabled)"),
                        "未信任工作区 process execute 走沙箱不可用 fail-closed。");
            Assert.That(stderr, Does.Contain("Unknown workspace: read-only."),
                        "B′ 提供的未信任提示语义标记必须出现在 stderr。");
            Assert.That(stderr, Does.Contain("/trust to authorize it"));
            Assert.That(File.Exists(trustPath), Is.False, "未信任的非交互 run 不得创建信任存储。");
            Assert.That(File.ReadAllTextAsync(configPath).Result, Does.Not.Contain("standingTrust"),
                        "run 不得改写任何设置。");
        });
    }

    /// <summary>
    ///     <para>
    ///         B6-0 对照（既有 CLI 组合模式）：信任激活（trust.json 存在）后，CLI 生产同形输入的
    ///         基线读保护计划与沙箱组合均成功——pairing fail-closed 只在受保护路径缺失时触发。
    ///     </para>
    ///     <para>
    ///         The B6-0 control (the existing CLI composition mode): once trust is activated (the trust
    ///         store exists), the CLI-shaped baseline read-protection plan and the sandbox composition both
    ///         succeed — the pairing fail-closed triggers only when protected paths are missing.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TrustedWorkspace_ComposesBaselineReadProtectionAndSandbox()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Windows sandbox composition requires Windows.");
        using var configDir      = new TestTempDir();
        using var workspace      = new TestTempDir();
        using var storage        = new TestTempDir();
        using var home           = new TestTempDir();
        var       userConfigPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
        var       trustPath      = Path.Combine(configDir.Root, "workspace-trust.json");
        // 受保护路径（用户配置）必须存在：pairing fail-closed 校验覆盖内全部 C1 宿主密钥路径。
        await File.WriteAllTextAsync(userConfigPath,
                                     """{"settings":{"sessionStoreDirectory":"sessions"}}""");
        // 信任激活：真实信任存储先落盘（CLI 组合时序=先 SaveAsync 再组合）。
        var trustStore = new WorkspaceTrustStore(trustPath, [workspace.Root]);
        await trustStore.SaveAsync(WorkspaceDirectoryIdentity.Capture(workspace.Root), true,
                                   CancellationToken.None);
        var settings = new WindowsSandboxSettings
        {
            Enabled              = true,
            SetupExecutablePath  = home.WriteFile("setup.exe", "stub"),
            RunnerExecutablePath = home.WriteFile("runner.exe", "stub"),
            SandboxHome          = home.Root
        };

        // CLI 生产同形输入：受保护路径=trust.json+用户配置；激活后两者都存在，计划与组合均成功。
        var protection = WindowsSandboxComposer.PlanBaselineReadProtection([trustPath, userConfigPath],
                                                                           null, settings,
                                                                           static _ => []);
        var execution = SandboxCliComposition.TryCompose(settings, workspace.Root,
                                                         new Dictionary<string, string>(), storage.Root,
                                                         baselineReadProtection : protection);

        Assert.Multiple(() =>
        {
            Assert.That(protection, Is.Not.Null, "受保护路径齐备时基线读保护计划必须成功。");
            Assert.That(execution, Is.Not.Null, "信任激活后沙箱组合成功。");
            TestAssert.Contains("backend=windows-sandbox", execution!.ExecutionPolicy.PolicyIdentity,
                                StringComparison.Ordinal);
        });
    }

    /// <summary>
    ///     <para>
    ///         从测试程序集定位 CLI 产物（dotnet test 会先构建被引用项目）；
    ///         找不到时明确失败，不静默跳过。
    ///     </para>
    ///     <para>
    ///         Locates the CLI output from the test assembly (dotnet test builds referenced projects first);
    ///         a miss fails explicitly instead of skipping silently.
    ///     </para>
    /// </summary>
    private static string LocateCliDll()
    {
        // <repo>\TinyHarness.Tests\bin\Debug\net10.0 → 上溯四级到仓库根。
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                                                     "..", "..", "..", ".."));
        var candidate = Path.Combine(repoRoot, "TinyHarness.Cli", "bin", "Debug", "net10.0",
                                     "TinyHarness.dll");
        Assert.That(File.Exists(candidate), Is.True,
                    $"The CLI output was not found at '{candidate}'; build TinyHarness.Cli before running this test.");
        return candidate;
    }
}
