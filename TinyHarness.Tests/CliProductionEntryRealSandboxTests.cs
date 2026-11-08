using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         CLI 真实生产入口（run）× 真实 Windows 沙箱（专用探针目录+专用配置目录）：组合态
///         PermissionShellTool 的 L3 宿主申请（RS-10，批准后按宿主账户执行、拒绝不执行）、
///         networkAccess=enabled 的逐次审批与批准后联网（验收 18，负/正对照）、常设信任三态
///         端到端（Confirmed 自动放行 vs Unconfirmed 逐次审批）。审批经 stdin 行应答（非交互
///         可达）；模型为本地 mock SSE 环回；机器配置零改动（组件路径读自机器配置，专用配置
///         目录 + TINYHARNESS_USER_CONFIG_DIR 指向）。
///     </para>
///     <para>
///         显式 opt-in 测试：未设置 TINYHARNESS_REAL_SANDBOX_TESTS=1（或 true）时类内全部
///         测试 Ignore，默认 dotnet test 不触机器沙箱、不写探针目录、不联网。启用后探针根
///         必须经 TINYHARNESS_REAL_SANDBOX_PROBE 显式提供绝对路径，不设 %TEMP% 等默认值——
///         默认/共享目录有沙箱组与 capability ACE 残留的实机 ACL 污染记录
///         （Docs/Archive/windows-sandbox-verification.md）；外网代理经
///         TINYHARNESS_REAL_SANDBOX_PROXY 参数化（默认 http://127.0.0.1:7890）。
///         负向隔离探针（断言沙箱身份不可执行/不可写宿主内容的用例）运行前校验探针根 ACL
///         前提：DACL（含继承）已含沙箱账户/组或 capability SID 的允许 ACE 时视为历史污染
///         形态，用例置为 Inconclusive（探针根 ACL 前提不满足，无法验收隔离，不得计为通过）。
///     </para>
///     <para>
///         The real CLI production entry (run) against the real Windows sandbox (dedicated probe and
///         config directories): the composed PermissionShellTool's L3 host request (RS-10 — approved it
///         runs with the host account, denied it never executes), networkAccess=enabled per-call approval
///         and approved networking (acceptance 18, negative and positive controls), and the standing-trust
///         three-state end to end (Confirmed auto-allows vs Unconfirmed per call). Approvals answer over
///         stdin lines (reachable non-interactively); the model is a local mock SSE loopback; the machine
///         config stays untouched (component paths read from it, a dedicated config directory wired
///         through TINYHARNESS_USER_CONFIG_DIR).
///     </para>
///     <para>
///         Explicit opt-in: with TINYHARNESS_REAL_SANDBOX_TESTS unset (anything but 1 or true) every
///         test here ignores, so a default `dotnet test` touches no machine sandbox, writes no probe
///         directory, and performs no networking. When enabled, the probe root must be supplied
///         explicitly as an absolute path via TINYHARNESS_REAL_SANDBOX_PROBE — no %TEMP%-style default,
///         because default and shared directories carry an on-machine record of residual sandbox-group
///         and capability ACE pollution (Docs/Archive/windows-sandbox-verification.md); the external
///         proxy is parameterized through TINYHARNESS_REAL_SANDBOX_PROXY (default http://127.0.0.1:7890).
///         Before a negative isolation probe (a case asserting the sandbox identity must not execute or
///         write host content), the probe-root ACL precondition is checked: an allow ACE — explicit or
///         inherited — for the sandbox accounts/group or a capability SID matches the historical
///         pollution form and marks the case Inconclusive (the ACL precondition is unmet, isolation
///         cannot be accepted, and it must not count as a pass).
///     </para>
/// </summary>
public class CliProductionEntryRealSandboxTests
{
    private const int RunTimeoutMs = 300_000;

    private const string GateVariable      = "TINYHARNESS_REAL_SANDBOX_TESTS";
    private const string ProbeRootVariable = "TINYHARNESS_REAL_SANDBOX_PROBE";
    private const string ProxyVariable     = "TINYHARNESS_REAL_SANDBOX_PROXY";
    private const string DefaultProxyUrl   = "http://127.0.0.1:7890";

    // 沙箱组名与生产 WorkspaceSandboxAclPreparation 的发现逻辑同源；账户名复用组件默认常量。
    private const string SandboxGroupName = "TinyHarnessUsers";

    private static bool IsWindows => OperatingSystem.IsWindows();

    // ---- RS-10：L3 宿主申请（组合态 PermissionShellTool） ----

    [Test]
    public async Task Run_ComposedHostScopeRequest_ApprovalExecutesWithTheHostAccount()
    {
        // 负向隔离探针：断言宿主账户落盘、沙箱账户身份缺席，先校验探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        RequireProbeRootAclPrecondition(probeRoot);
        var stdout = await RunRealAsync("host-yes", probeRoot, HostScopeCall, approvals : ["a"]);
        var identityPath = Path.Combine(stdout.Workspace, "host-identity.txt");
        var identity = File.Exists(identityPath)
            ? await File.ReadAllTextAsync(identityPath)
            : "<missing: not created>";

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Contain("backend: HostProcess"),
                        "审批卡必须声明 L3 真实后端与无隔离声明。");
            Assert.That(stdout.All, Does.Contain("no OS sandbox isolation"));
            // whoami 落盘=宿主账户（本机 whoami 输出用户名形态）；沙箱账户名必须缺席。
            Assert.That(identity, Does.Contain(Environment.UserName),
                        "批准后 host 申请必须以宿主账户执行（whoami 落盘）。");
            Assert.That(identity, Does.Not.Contain("TinyHarnessOffline"));
            Assert.That(identity, Does.Not.Contain("TinyHarnessOnline"),
                        "host 执行不得是沙箱账户身份。");
        });
    }

    [Test]
    public async Task Run_UnapprovedHostScopeRequest_DoesNotExecute()
    {
        // 负向隔离探针：断言拒绝后无落盘，先校验探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        RequireProbeRootAclPrecondition(probeRoot);
        var stdout = await RunRealAsync("host-no", probeRoot, HostScopeCall, approvals : ["d"]);

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Contain("backend: HostProcess"));
            Assert.That(File.Exists(Path.Combine(stdout.Workspace, "host-identity.txt")), Is.False,
                        "未批准的 L3 申请不得执行。");
        });
    }

    // ---- 验收 18：networkAccess=enabled 逐次审批 + 批准后联网 ----

    [Test]
    public async Task Run_PerCallNetworkRequest_DeniedStaysUnexecuted()
    {
        // 负向隔离探针：断言拒绝后无外联结果落盘，先校验探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        RequireProbeRootAclPrecondition(probeRoot);
        var stdout = await RunRealAsync("net-no", probeRoot, NetworkCall,
                                   networkMode : NetworkMode.ProxyRestricted, approvals : ["d"]);

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Contain("[a]llow once"), "逐次审批卡必须出现。");
            Assert.That(stdout.All, Does.Contain("network: full network inside the sandbox"),
                        "fact 行必须按 F6 措辞声明网络范围。");
            Assert.That(File.Exists(Path.Combine(stdout.Workspace, "net-result.txt")), Is.False,
                        "拒绝后不得有外联结果落盘。");
        });
    }

    [Test]
    public async Task Run_PerCallNetworkRequest_ApprovedReachesExternalViaProxy()
    {
        // 正对照（联网可达性），不依赖探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        var stdout = await RunRealAsync("net-yes", probeRoot, NetworkCall,
                                   networkMode : NetworkMode.ProxyRestricted, approvals : ["a"]);
        var resultPath = Path.Combine(stdout.Workspace, "net-result.txt");
        var netResult = File.Exists(resultPath)
            ? await File.ReadAllTextAsync(resultPath)
            : "<missing: not created>";

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Contain("[a]llow once"), "restricted 常设下逐次审批必须出现。");
            Assert.That(netResult, Does.Contain("200"),
                        "批准后单次联网必须经代理到达外网（curl http_code 落盘）。");
        });
    }

    // ---- 常设信任端到端：Confirmed 自动放行 vs Unconfirmed 逐次审批 ----

    [Test]
    public async Task Run_ConfirmedStandingTrust_AutoAllowsTheNetworkedCall()
    {
        // 正对照（Confirmed 自动放行 + 联网可达性），不依赖探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        var stdout = await RunRealAsync("standing-confirmed", probeRoot, NetworkCall,
                                   networkMode : NetworkMode.StandingConfirmed);
        var netResult = await File.ReadAllTextAsync(
            Path.Combine(stdout.Workspace, "net-result.txt"));

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Not.Contain("[a]llow once"),
                        "Confirmed 常设信任下联网不得再逐次审批。");
            Assert.That(netResult, Does.Contain("200"),
                        "Confirmed 下 enabled 申请按显式持久授权自动放行并可达外网。");
        });
    }

    [Test]
    public async Task Run_UnconfirmedStandingTrust_RequiresPerCallApproval()
    {
        // 负向隔离探针：断言 Unconfirmed 下必须逐次审批且拒绝后无落盘，先校验探针根 ACL 前提。
        var probeRoot = GateRealSandboxRun();
        RequireProbeRootAclPrecondition(probeRoot);
        var stdout = await RunRealAsync("standing-unconfirmed", probeRoot, NetworkCall,
                                   networkMode : NetworkMode.StandingUnconfirmed, approvals : ["d"]);

        Assert.Multiple(() =>
        {
            Assert.That(stdout.ExitCode, Is.EqualTo(0), stdout.All);
            Assert.That(stdout.All, Does.Contain("[a]llow once"),
                        "Unconfirmed 常设信任下联网必须逐次审批。");
            // Unconfirmed 的生产标记面=run 入口 stderr 提示（审批卡渲染结构化字段，
            // 引擎逐次文案经离线 StandingTrustMigrationTests 固化）。
            Assert.That(stdout.All, Does.Contain("is not confirmed yet"),
                        "run 入口必须提示常设信任未确认。");
            Assert.That(File.Exists(Path.Combine(stdout.Workspace, "net-result.txt")), Is.False,
                        "拒绝后不得有外联结果落盘。");
        });
    }

    // ---- 公共夹具：专用配置目录 + 真实组件 + 信任激活 + mock SSE + stdin 审批 ----

    /// <summary>
    ///     <para>
    ///         类级环境门禁：未显式 opt-in 时每条测试 Ignore（默认 dotnet test 不触真实沙箱、
    ///         不写探针目录、不联网）。门禁开启后返回探针根；探针根必须显式提供绝对路径，
    ///         缺失一律 Ignore 而非回退 %TEMP%——默认/共享目录有沙箱组与 capability ACE 残留
    ///         的历史 ACL 污染记录（Docs/Archive/windows-sandbox-verification.md 的实机 ACL 核对）。
    ///     </para>
    ///     <para>
    ///         The class-level environment gate: without explicit opt-in every test here ignores
    ///         (a default `dotnet test` touches no real sandbox, writes no probe directory, and
    ///         performs no networking). When enabled, returns the probe root, which must be given
    ///         explicitly as an absolute path — a missing value ignores rather than falling back to
    ///         %TEMP%: default and shared directories have a history of residual sandbox-group and
    ///         capability ACE pollution (the on-machine ACL audit in
    ///         Docs/Archive/windows-sandbox-verification.md).
    ///     </para>
    /// </summary>
    private static string GateRealSandboxRun()
    {
        if (!IsWindows) Assert.Ignore("Windows sandbox composition requires Windows.");
        var gate = Environment.GetEnvironmentVariable(GateVariable);
        var enabled = string.Equals(gate, "1", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(gate, "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled)
            Assert.Ignore($"默认关闭的真实 Windows 沙箱入口测试：设置 {GateVariable}=1（或 true），" +
                          $"并配套 {ProbeRootVariable}（必填，绝对路径）与 {ProxyVariable}" +
                          $"（可选，默认 {DefaultProxyUrl}）后启用。");
        var probeRoot = Environment.GetEnvironmentVariable(ProbeRootVariable);
        if (string.IsNullOrWhiteSpace(probeRoot) || !Path.IsPathFullyQualified(probeRoot))
            Assert.Ignore($"{ProbeRootVariable} 必须显式提供绝对路径的探针根目录；" +
                          "不设 %TEMP% 等默认值（默认/共享目录有 ACL 污染记录，见类注释）。");
        return probeRoot!; // 非空已由上一守卫保证（Assert.Ignore 中断该分支）。
    }

    /// <summary>
    ///     <para>
    ///         负向隔离探针的 ACL 前提校验：探针根 DACL（含继承）不得已含沙箱账户/组
    ///         （TinyHarnessOffline/TinyHarnessOnline/TinyHarnessUsers）或 capability SID
    ///         （S-1-15-3- 前缀）的允许 ACE——即实机 ACL 核对记录到的历史污染形态；前提不满足时
    ///         Inconclusive，明确"无法验收隔离，不得计为通过"。未 provision 的账户不存在、
    ///         不可能构成污染，翻译失败即跳过；探针根缺失则幂等创建后再校验。
    ///     </para>
    ///     <para>
    ///         The ACL precondition for negative isolation probes: the probe-root DACL
    ///         (inherited included) must carry no allow ACE for the sandbox accounts/group
    ///         (TinyHarnessOffline/TinyHarnessOnline/TinyHarnessUsers) or capability SIDs
    ///         (S-1-15-3- prefix) — the pollution form recorded by the on-machine ACL audit.
    ///         When violated the case is inconclusive and must not count as a pass.
    ///         Un-provisioned accounts do not exist and cannot pollute, so their translation
    ///         failures are skipped; a missing probe root is created idempotently first.
    ///     </para>
    /// </summary>
    private static void RequireProbeRootAclPrecondition(string probeRoot)
    {
        // 必须已存在才能读取 DACL：探针根由运行方显式指定，幂等创建后校验。
        Directory.CreateDirectory(probeRoot);
        if (!OperatingSystem.IsWindows()) return; // 门禁已保证 Windows；此处为平台分析守卫。
        var sandboxSids = SandboxIdentitySids();
        var access = new DirectoryInfo(probeRoot).GetAccessControl(AccessControlSections.Access);
        var offending = new List<string>();
        foreach (FileSystemAccessRule rule in access.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = rule.IdentityReference.ToString();
            // 平台 API 不进 lambda（CA1416 的守卫分析不跨 lambda）；显式内层循环完成 SID 匹配。
            var isSandboxIdentity = false;
            foreach (var matched in sandboxSids)
            {
                if (!matched.Equals(rule.IdentityReference)) continue;
                isSandboxIdentity = true;
                break;
            }

            var isCapabilitySid = sid.StartsWith("S-1-15-3-", StringComparison.Ordinal);
            if (isSandboxIdentity || isCapabilitySid) offending.Add(sid);
        }

        if (offending.Count > 0)
            Assert.Inconclusive(
                $"探针根 ACL 前提不满足，无法验收隔离，不得计为通过：'{probeRoot}' 的 DACL 已含" +
                $"沙箱身份或 capability SID 的允许 ACE（{string.Join(", ", offending.Distinct())}）；" +
                "请改用未被沙箱授权或污染过的探针目录。");
    }

    private static List<SecurityIdentifier> SandboxIdentitySids()
    {
        if (!OperatingSystem.IsWindows()) return []; // 门禁已保证 Windows；此处为平台分析守卫。
        // 发现逻辑与生产同源：组/账户按 NTAccount→SID 翻译（WorkspaceSandboxAclPreparation 同形）。
        string[] names = [SandboxGroupName,
                          WindowsSandboxComponents.DefaultOfflineUsername,
                          WindowsSandboxComponents.DefaultOnlineUsername];
        var sids = new List<SecurityIdentifier>();
        foreach (var name in names)
        {
            try
            {
                sids.Add((SecurityIdentifier)new NTAccount(Environment.MachineName, name)
                             .Translate(typeof(SecurityIdentifier)));
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                // 未 provision 的身份不存在：不可能构成污染，跳过。
            }
        }

        return sids;
    }

    /// <summary>
    ///     <para>
    ///         解析外网代理端点：读 TINYHARNESS_REAL_SANDBOX_PROXY，空白时回退默认
    ///         http://127.0.0.1:7890（本机 opt-in 行为不变）；非绝对 http/https/socks5 URL
    ///         视为显式配置错误并抛出带上下文的异常。
    ///     </para>
    ///     <para>
    ///         Resolves the external proxy endpoint from TINYHARNESS_REAL_SANDBOX_PROXY, falling
    ///         back to the default http://127.0.0.1:7890 when blank (local opt-in behavior
    ///         unchanged); a non-absolute http/https/socks5 URL is an explicit configuration error
    ///         and throws with context.
    ///     </para>
    /// </summary>
    private static (string Host, int Port, string Protocol) ResolveProxyEndPoint()
    {
        var raw = Environment.GetEnvironmentVariable(ProxyVariable);
        if (string.IsNullOrWhiteSpace(raw)) raw = DefaultProxyUrl;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var proxy) ||
            proxy.Scheme is not ("http" or "https" or "socks5"))
            throw new InvalidOperationException(
                $"{ProxyVariable} 必须是绝对代理 URL（http/https/socks5）；当前值 '{raw}'，默认 {DefaultProxyUrl}。");
        return (proxy.IdnHost, proxy.Port, proxy.Scheme);
    }

    private static JsonObject? NetworkSettingsFromProxy((string Host, int Port, string Protocol)? proxy)
    {
        if (proxy is null) return null;
        return new JsonObject
        {
            ["defaultRoute"] = new JsonObject { ["mode"] = "system", ["proxyId"] = null },
            ["proxies"] = new JsonArray(new JsonObject
            {
                ["id"]       = "72f2f1d537e144caa3d93b42f30024ca",
                ["name"]     = "clash",
                ["protocol"] = proxy.Value.Protocol,
                ["host"]     = proxy.Value.Host,
                ["port"]     = proxy.Value.Port
            })
        };
    }

    private static (string Setup, string Runner) ReadMachineComponentPaths()
    {
        var machineConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                         "tinyharness", "user-config.json");
        using var document = JsonDocument.Parse(File.ReadAllText(machineConfig));
        var sandbox = document.RootElement.GetProperty("settings").GetProperty("windowsSandbox");
        return (sandbox.GetProperty("setupExecutablePath").GetString()!,
                sandbox.GetProperty("runnerExecutablePath").GetString()!);
    }

    private static ChatToolCallScript HostScopeCall(string workspace)
    {
        var outputFile = Path.Combine(workspace, "host-identity.txt");
        var arguments = new JsonObject
        {
            ["mode"]             = "direct",
            ["executable"]       = "cmd.exe",
            ["arguments"]        = new JsonArray("/d", "/s", "/c", $"whoami > {outputFile}"),
            ["executionScope"]   = "host"
        };
        return new ChatToolCallScript(arguments.ToJsonString());
    }

    private static ChatToolCallScript NetworkCall(string workspace)
    {
        var outputFile = Path.Combine(workspace, "net-result.txt");
        var arguments = new JsonObject
        {
            ["mode"]             = "direct",
            ["executable"]       = "cmd.exe",
            ["arguments"]        = new JsonArray("/d", "/s", "/c",
                                                 // GAP-4：沙箱账户无 TLS 客户端凭据，https 必然失败；探测 URL 用 http。
                                                 $"curl -s -o NUL -w %{{http_code}} http://example.com > {outputFile}"),
            ["networkAccess"]    = "enabled"
        };
        return new ChatToolCallScript(arguments.ToJsonString());
    }

    private sealed record ChatToolCallScript(string ArgumentsJson)
    {
        public string ToolCallChunks => string.Concat(
            Chunk("{\"role\":\"assistant\",\"content\":\"\"}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                  "\"function\":{\"name\":\"shell\",\"arguments\":\"\"}}]}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":" +
                  JsonSerializer.Serialize(ArgumentsJson) + "}}]}"),
            Chunk("{}", "tool_calls")) + "data: [DONE]\n\n";

        public string FinalChunks => string.Concat(
            Chunk("{\"role\":\"assistant\",\"content\":\"\"}"),
            Chunk("{\"content\":\"done\"}"),
            Chunk("{}", "stop")) + "data: [DONE]\n\n";

        private static string Chunk(string deltaJson, string? finishReason = null)
        {
            var finish = finishReason is null ? "null" : $"\"{finishReason}\"";
            return "data: {\"id\":\"chatcmpl-mock\",\"object\":\"chat.completion.chunk\",\"created\":1700000000," +
                   $"\"model\":\"mock\",\"choices\":[{{\"index\":0,\"delta\":{deltaJson},\"finish_reason\":{finish}}}]}}\n\n";
        }
    }

    /// <summary>网络/常设信任夹具模式：无网络、受限+命名代理逐次、Confirmed 常设在线、Unconfirmed 常设在线。</summary>
    private enum NetworkMode
    {
        None,
        ProxyRestricted,
        StandingConfirmed,
        StandingUnconfirmed
    }

    private static async Task<(int ExitCode, string All, string Workspace)> RunRealAsync(string caseId,
        string probeRoot, Func<string, ChatToolCallScript> scriptBuilder,
        NetworkMode networkMode = NetworkMode.None, IReadOnlyList<string>? approvals = null)
    {
        (string Host, int Port, string Protocol)? proxyEndPoint =
            networkMode != NetworkMode.None ? ResolveProxyEndPoint() : null;
        var cliDll = LocateCliDll();
        using var configDir = new TestTempDir();
        using var sessionsRoot = new TestTempDir();
        using var mockServer = new MockSseServer();
        mockServer.Start();
        var workspace = Directory.CreateDirectory(Path.Combine(probeRoot, $"ws-{caseId}-{Guid.NewGuid():N}")).FullName;
        var configPath = Path.Combine(configDir.Root, UserConfigStore.FileName);
        var trustPath = Path.Combine(configDir.Root, "workspace-trust.json");
        var (setupPath, runnerPath) = ReadMachineComponentPaths();

        var sandbox = new JsonObject
        {
            ["enabled"]         = true,
            ["setupExecutablePath"]  = setupPath,
            ["runnerExecutablePath"] = runnerPath,
            ["policy"] = "workspace-write"
        };
        var standing = networkMode is NetworkMode.StandingConfirmed or NetworkMode.StandingUnconfirmed;
        if (networkMode != NetworkMode.None)
        {
            // 网络（常设或逐次）：独立命名代理 + 不开启环回（enabled×tcp-loopback fail-closed 互斥）。
            sandbox["networkPolicy"] = standing ? "enabled" : "restricted";
            sandbox["proxy"] = new JsonObject { ["mode"] = "named", ["proxyName"] = "clash" };
            sandbox["allowTcpLoopback"] = false;
        }

        var config = new JsonObject
        {
            ["defaultProfile"] = "mock",
            ["profiles"] = new JsonArray(new JsonObject
            {
                ["name"]                      = "mock",
                ["endpoint"]                  = mockServer.BaseUrl,
                ["apiKeyEnvironmentVariable"] = "TH_REAL_ENTRY_KEY",
                ["chatApi"]                   = "chat-completions",
                ["defaultModel"]              = "mock-model",
                ["models"] = new JsonArray(new JsonObject
                {
                    ["id"] = "mock-model", ["contextWindowTokens"] = 64000
                })
            }),
            ["settings"] = new JsonObject
            {
                ["windowsSandbox"] = sandbox,
                ["sessionStoreDirectory"] = sessionsRoot.Root,
                // 命名代理必须在网络设置中注册（与 C2 代理变体同形）；端点来自 TINYHARNESS_REAL_SANDBOX_PROXY。
                ["network"] = NetworkSettingsFromProxy(proxyEndPoint)
            }
        };
        await File.WriteAllTextAsync(configPath, config.ToJsonString());
        if (networkMode == NetworkMode.StandingConfirmed)
        {
            // 常设信任 Confirmed：按生产同一评估计算范围修订并落盘确认记录。
            var loaded = await UserConfigStore.LoadAsync(configPath, CancellationToken.None)
                              .ConfigureAwait(false);
            var sandboxSettings = loaded.Settings!.WindowsSandbox!;
            var revision = StandingTrustEvaluation.ScopeRevision(sandboxSettings);
            await File.WriteAllTextAsync(configPath, PatchStandingTrust(config.ToJsonString(), revision));
        }

        // 信任激活：CLI 组合时序=trust.json 先落盘（SaveAsync）再组合；写入后重新加载确认。
        if (networkMode == NetworkMode.StandingConfirmed)
        {
            var reloaded = await UserConfigStore.LoadAsync(configPath, CancellationToken.None);
            Assert.That(StandingTrustEvaluation.Evaluate(reloaded.Settings!.WindowsSandbox,
                                                         reloaded.Settings.Permissions),
                        Is.EqualTo(StandingTrustState.Confirmed),
                        "夹具的确认记录必须使配置进入 Confirmed 态。");
        }

        var trustStore = new WorkspaceTrustStore(trustPath, [workspace]);
        await trustStore.SaveAsync(WorkspaceDirectoryIdentity.Capture(workspace), true,
                                   CancellationToken.None);

        // 模型脚本：第一个请求吐 shell 工具调用，第二个请求收尾。
        var script = scriptBuilder(workspace);
        mockServer.EnqueueRaw(script.ToolCallChunks);
        mockServer.EnqueueRaw(script.FinalChunks);

        var start = new ProcessStartInfo
        {
            FileName                   = "dotnet",
            Arguments                  = $"exec \"{cliDll}\" run \"execute the scripted tool call\"",
            RedirectStandardInput      = true,
            RedirectStandardOutput     = true,
            RedirectStandardError      = true,
            UseShellExecute            = false,
            CreateNoWindow             = true,
            WorkingDirectory           = workspace
        };
        start.Environment[UserConfigStore.DirectoryOverrideVariable] = configDir.Root;
        start.Environment["TH_REAL_ENTRY_KEY"] = "dummy-key";

        using var process = Process.Start(start)!;
        // 审批经共享 stdin 行应答：逐行写入后关闭（无审批的用例直接 EOF）。
        if (approvals is { Count: > 0 })
        {
            foreach (var answer in approvals) await process.StandardInput.WriteLineAsync(answer);
        }
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(RunTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"run did not exit within the timeout (case {caseId}).");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        // 探针工作区保留不删：工具输出落盘即取证（专用目录惯例，产物不入库）。
        return (process.ExitCode, stdout + "\n--stderr--\n" + stderr, workspace);
    }

    private static string PatchStandingTrust(string configText, string revision)
    {
        var node = JsonNode.Parse(configText)!.AsObject();
        var settings = node["settings"]!.AsObject();
        var permissions = new JsonObject
        {
            ["standingTrust"] = new JsonObject
            {
                ["confirmedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["scopeRevision"] = revision
            }
        };
        settings["permissions"] = permissions;
        return node.ToJsonString();
    }

    private static string LocateCliDll()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                                                     "..", "..", "..", ".."));
        var candidate = Path.Combine(repoRoot, "TinyHarness.Cli", "bin", "Debug", "net10.0",
                                     "TinyHarness.dll");
        Assert.That(File.Exists(candidate), Is.True,
                    $"The CLI output was not found at '{candidate}'; build TinyHarness.Cli before running this test.");
        return candidate;
    }
}
