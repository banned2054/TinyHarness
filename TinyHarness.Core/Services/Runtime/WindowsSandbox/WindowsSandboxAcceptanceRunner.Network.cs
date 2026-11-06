using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

public sealed partial class WindowsSandboxAcceptanceRunner
{
    /// <summary>组合器约定的 NO_PROXY 常量值（环回目标恒不走代理）。The composer's constant NO_PROXY value (loopback targets never proxy).</summary>
    private const string ExpectedNoProxyValue = "localhost,127.0.0.1,::1";

    /// <summary>代理环境反显与受限负向对照涉及的键。The keys involved in the proxy echo and its restricted negative control.</summary>
    private static readonly IReadOnlyList<string> ProxyEnvironmentKeys =
        ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    /// <summary>
    ///     <para>
    ///         代理环境反显：在线模式断言代理键随组合注入（HTTP/HTTPS 同值并命中命名代理 host:port、
    ///         NO_PROXY 精确、ALL_PROXY 不出现）；受限模式断言全部代理键缺席。显示值只到 host:port。
    ///     </para>
    ///     <para>
    ///         The proxy environment echo: online mode asserts the proxy keys are
    ///         injected by the composition (HTTP/HTTPS equal and matching the named
    ///         proxy host:port, NO_PROXY exact, ALL_PROXY absent); restricted mode
    ///         asserts every proxy key is absent. Displayed values carry host:port only.
    ///     </para>
    /// </summary>
    private async Task<SandboxAcceptanceCaseResult> EnvProxyEchoAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("env-proxy-echo");
        var stopwatch  = Stopwatch.StartNew();
        // 前缀用 HTTP（不带下划线）：HTTP_ 只匹配 HTTP_PROXY，会漏掉 HTTPS_PROXY；
        // ALL_PROXY 与 NO_PROXY 单独列出，避免空格并入变量名；末尾 rem 抹掉
        // “变量未定义”时 set 的 errorlevel 1，使退出码只反映反显本身是否完成。
        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                         CmdPlan("set HTTP&set ALL_PROXY&set NO_PROXY&rem", ExitProbeTimeoutSeconds),
                                         "env-proxy-echo", cancellationToken).ConfigureAwait(false);

        var detail = $"期望 NO_PROXY={ExpectedNoProxyValue}；代理地址仅以 host:port 记录。";
        if (run.Exception is not null || run.Result is not { ExitCode: 0, TimedOut: false } ||
            run.Stdout is not { Truncated: false })
            return Result(definition, SandboxAcceptanceVerdict.Fail, Describe(run, FirstLine(run.Stderr?.Content)),
                          $"环境反显未完成，无法判定代理环境注入。{detail}", stopwatch);

        var variables = ParseEnvironmentEcho(run.Stdout.Content);
        var online    = _networkPolicy == SandboxNetworkPolicy.Enabled;
        var httpProxy = variables.TryGetValue("HTTP_PROXY", out var httpValue) && httpValue.Length > 0;
        var httpsProxy = variables.TryGetValue("HTTPS_PROXY", out var httpsValue) && httpsValue.Length > 0;
        var allProxy   = variables.ContainsKey("ALL_PROXY");
        var noProxy    = variables.TryGetValue("NO_PROXY", out var noProxyValue);
        var hostPort   = _limits.OnlineProxyHostPort;

        var verdicts = new List<string>();
        if (online)
        {
            var sameValue = httpProxy && httpsProxy &&
                            string.Equals(httpValue, httpsValue, StringComparison.Ordinal);
            var hostPortMatched = string.IsNullOrWhiteSpace(hostPort) ||
                                  (httpValue?.Contains(hostPort, StringComparison.Ordinal) ?? false) &&
                                  (httpsValue?.Contains(hostPort, StringComparison.Ordinal) ?? false);
            if (!httpProxy)  verdicts.Add("HTTP_PROXY 缺失或为空");
            if (!httpsProxy) verdicts.Add("HTTPS_PROXY 缺失或为空");
            if (httpProxy    && httpsProxy && !sameValue) verdicts.Add("HTTP_PROXY 与 HTTPS_PROXY 取值不一致");
            if (sameValue    && !hostPortMatched) verdicts.Add($"代理地址未命中命名代理 {hostPort}");
            if (!noProxy     || noProxyValue != ExpectedNoProxyValue)
                verdicts.Add($"NO_PROXY 缺失或不等于 {ExpectedNoProxyValue}");
            if (allProxy) verdicts.Add("ALL_PROXY 不应出现（http/https 代理只注入 HTTP/HTTPS_PROXY）");

            var passed = verdicts.Count == 0;
            return Result(definition, passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail,
                          DescribeProxyEcho(variables, hostPort),
                          $"{(passed ? "代理环境按在线组合注入。" : "代理环境注入不符。")}{detail}",
                          stopwatch);
        }

        foreach (var key in ProxyEnvironmentKeys)
            if (variables.ContainsKey(key))
                verdicts.Add($"{key} 不应出现在受限目标环境");

        var restrictedClean = verdicts.Count == 0;
        return Result(definition, restrictedClean ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail,
                      DescribeProxyEcho(variables, hostPort),
                      $"{(restrictedClean ? "受限目标环境无代理键。" : "受限目标环境出现了代理键。")}{detail}",
                      stopwatch);
    }

    /// <summary>
    ///     <para>
    ///         经配置代理的轻量只读外网请求：在线模式要求 2xx；受限模式要求失败（非零退出或无
    ///         HTTP 状态码）作为阻断证据。目标仅限
    ///         <see cref="WindowsSandboxAcceptanceLimits.OnlineExternalProbeUrl" />，失败时如实记录
    ///         stderr 关键行，不重试、不放宽。
    ///     </para>
    ///     <para>
    ///         A lightweight read-only external request through the configured proxy:
    ///         online mode requires a 2xx; restricted mode requires a failure (non-zero
    ///         exit or no HTTP status) as blocking evidence. The target stays the
    ///         <see cref="WindowsSandboxAcceptanceLimits.OnlineExternalProbeUrl" />; a
    ///         failure records the stderr key lines verbatim, without retries or
    ///         relaxed criteria.
    ///     </para>
    /// </summary>
    private async Task<SandboxAcceptanceCaseResult> NetworkExternalAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("network-external");
        var stopwatch  = Stopwatch.StartNew();
        if (SystemTool("curl.exe") is not { } curl)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "host 缺少 curl.exe（System32），无法执行外网连通探测。", stopwatch);

        var url  = _limits.OnlineExternalProbeUrl;
        var run  = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                          new PreparedProcessExecution(curl,
                                                                       ["-s", "-m", "20", "-o", "NUL", "-w",
                                                                        "%{http_code}", url],
                                                                       _workspaceRoot, 40, false, null),
                                          "network-external", cancellationToken).ConfigureAwait(false);
        var code     = TryParseHttpCode(run.Stdout?.Content);
        var reached  = run.Exception is null && run.Result is { ExitCode: 0, TimedOut: false } &&
                       code is >= 200 and <= 299;
        var online   = _networkPolicy == SandboxNetworkPolicy.Enabled;
        var passed   = online ? reached : !reached;
        var verdict  = passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail;
        var reason   = online
            ? (reached
                ? "外网请求经代理返回 2xx。"
                : $"外网请求未返回 2xx（http_code={CodeDisplay(code)}）；代理或上游不可用时如实 FAIL。")
            : (reached
                ? "受限模式下外网请求竟返回 2xx，网络阻断失效。"
                : "受限模式下外网请求未成功（阻断语义成立）。");
        return Result(definition, verdict, Describe(run, $"http_code={CodeDisplay(code)}"),
                      $"{reason} {FirstLine(run.Stderr?.Content)}；外网目标仅限轻量只读端点。".TrimEnd(),
                      stopwatch)
           with
           {
               NetworkChecks =
               [
                   new SandboxNetworkCheck
                   {
                       Target = url, ExpectedAllowed = online, Verdict = verdict,
                       Reason = reason, ExitCode = run.Result?.ExitCode, OuterTimedOut = run.Result?.TimedOut ?? false
                   }
               ]
           };
    }

    /// <summary>
    ///     <para>
    ///         Online 账户对宿主环回服务（在线代理端点）的 TCP 连通：NO_PROXY 排除环回使 curl 直连，
    ///         任何 HTTP 状态码（100-599）都判定连通；连接拒绝或超时记 FAIL 并如实呈现环回阻断语义。
    ///         仅在 enabled 矩阵出现。
    ///     </para>
    ///     <para>
    ///         The online account's TCP reachability of the host loopback service (the
    ///         online proxy endpoint): NO_PROXY excludes loopback so curl connects
    ///         directly; any HTTP status (100-599) proves reachability, while a
    ///         refusal or timeout records FAIL with the loopback-block semantics
    ///         verbatim. Appears only in the enabled matrix.
    ///     </para>
    /// </summary>
    private async Task<SandboxAcceptanceCaseResult> NetworkLoopbackHostAsync(CancellationToken cancellationToken)
    {
        var definition = Definition("network-loopback-host");
        var stopwatch  = Stopwatch.StartNew();
        var target     = _limits.HostLoopbackProbeTarget;
        if (string.IsNullOrWhiteSpace(target))
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "未提供宿主环回探测目标（在线代理端点非环回或未配置）；本用例不适用，未验证。", stopwatch);

        if (SystemTool("curl.exe") is not { } curl)
            return Result(definition, SandboxAcceptanceVerdict.Indeterminate, null,
                          "host 缺少 curl.exe（System32），无法执行宿主环回探测。", stopwatch);

        var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                         new PreparedProcessExecution(curl,
                                                                      ["-s", "-m", "10", "-o", "NUL", "-w",
                                                                       "%{http_code}", $"http://{target}"],
                                                                      _workspaceRoot, 30, false, null),
                                         "network-loopback-host", cancellationToken).ConfigureAwait(false);
        var code    = TryParseHttpCode(run.Stdout?.Content);
        var reached = run.Exception is null && run.Result is { ExitCode: 0, TimedOut: false } &&
                      code is >= 100 and <= 599;
        var verdict = reached ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail;
        var reason  = reached
            ? $"Online 账户可直连宿主环回 {target}（HTTP {CodeDisplay(code)}；环回放行语义成立）。"
            : $"Online 账户无法直连宿主环回 {target}（exit={run.Result?.ExitCode?.ToString() ?? "无"}，" +
              $"http_code={CodeDisplay(code)}）；按环回阻断/端口不可达语义如实记 FAIL，此结果本身即为环回行为的观测。";
        return Result(definition, verdict, Describe(run, $"http_code={CodeDisplay(code)}"),
                      $"{reason} NO_PROXY 恒排除环回，本请求为直连而非代理转发；仅记录 host:port。",
                      stopwatch)
           with
           {
               NetworkChecks =
               [
                   new SandboxNetworkCheck
                   {
                       Target = $"tcp [127.0.0.1] {target}", ExpectedAllowed = true, Verdict = verdict,
                       Reason = reason, ExitCode = run.Result?.ExitCode, OuterTimedOut = run.Result?.TimedOut ?? false
                   }
               ]
           };
    }

    /// <summary>解析 `set` 反显输出为键值表（大小写不敏感，后值覆盖）。Parses `set` echo output into a dictionary (case-insensitive, last value wins).</summary>
    private static Dictionary<string, string> ParseEnvironmentEcho(string content)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            var entry = line.TrimEnd('\r');
            var separator = entry.IndexOf('=');
            if (separator <= 0) continue;

            variables[entry[..separator]] = entry[(separator + 1)..];
        }

        return variables;
    }

    /// <summary>
    ///     <para>代理环境摘要：值脱敏到 host:port 或存在性标记；NO_PROXY 为常量可原样显示。</para>
    ///     <para>
    ///         The proxy environment summary: values redact to host:port or presence
    ///         markers; the constant NO_PROXY may display verbatim.
    ///     </para>
    /// </summary>
    private static string DescribeProxyEcho(Dictionary<string, string> variables, string? hostPort)
    {
        var parts = ProxyEnvironmentKeys.Select(key =>
        {
            if (!variables.TryGetValue(key, out var value)) return $"{key}=<absent>";

            return key == "NO_PROXY"       ? $"{key}={value}"
                 : string.IsNullOrWhiteSpace(hostPort) ? $"{key}=<set>"
                 : value.Contains(hostPort, StringComparison.Ordinal) ? $"{key}=<set:{hostPort}>"
                 : $"{key}=<set:mismatch>";
        });
        return string.Join("; ", parts);
    }

    /// <summary>curl -w %{http_code} 输出的三位状态码解析；其余形态返回 null。Parses the 3-digit status from curl -w %{http_code}; anything else is null.</summary>
    private static int? TryParseHttpCode(string? content)
    {
        var trimmed = content?.Trim();
        return trimmed is { Length: 3 } && int.TryParse(trimmed, out var code) ? code : null;
    }

    private static string CodeDisplay(int? code) => code?.ToString() ?? "无";

    private async Task<SandboxAcceptanceCaseResult> NetworkAsync(string name, CancellationToken cancellationToken)
    {
        var stopwatch       = Stopwatch.StartNew();
        var checks          = new List<SandboxNetworkCheck>();
        var loopbackAllowed = _snapshotExecution!.Components.AllowTcpLoopback;
        if (name == "network-dns")
        {
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            foreach (var port in new[] { 53, 853 })
            foreach (var transport in new[] { "tcp", "udp" })
                checks.Add(await CheckNetworkTargetAsync(transport, address, port, false, cancellationToken)
                              .ConfigureAwait(false));
        }
        else
        {
            var transport = name == "network-udp" ? "udp" : "tcp";
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
                checks.Add(await CheckNetworkTargetAsync(transport, address, 0,
                                                         transport == "tcp" && loopbackAllowed, cancellationToken)
                              .ConfigureAwait(false));

            var addressText = _limits.NonLoopbackAddressResolver is { } resolver
                ? resolver()
                : ResolveNonLoopbackAddress();
            if (!IPAddress.TryParse(addressText, out var nonLoopback)   || IPAddress.IsLoopback(nonLoopback) ||
                nonLoopback.AddressFamily != AddressFamily.InterNetwork || nonLoopback.Equals(IPAddress.Any))
                checks.Add(new SandboxNetworkCheck
                {
                    Target  = $"{transport} non-loopback IPv4", ExpectedAllowed = false,
                    Verdict = SandboxAcceptanceVerdict.Indeterminate, Reason    = "本机非环回 IPv4 地址不可用。"
                });
            else
                checks.Add(await CheckNetworkTargetAsync(transport, nonLoopback, 0, false, cancellationToken)
                              .ConfigureAwait(false));
        }

        var verdict = checks.Any(check => check.Verdict == SandboxAcceptanceVerdict.Fail)
            ? SandboxAcceptanceVerdict.Fail
            : checks.All(check => check.Verdict == SandboxAcceptanceVerdict.Pass)
                ? SandboxAcceptanceVerdict.Pass
                : SandboxAcceptanceVerdict.Indeterminate;
        return Result(Definition(name), verdict,
                      string.Join("; ", checks.Select(check => $"{check.Target}: {check.Verdict}")),
                      string.Join("; ", checks.Select(check => $"{check.Target}: {check.Reason}")), stopwatch)
            with
            {
                NetworkChecks = checks
            };
    }

    private async Task<SandboxNetworkCheck> CheckNetworkTargetAsync(
        string transport, IPAddress address, int port, bool expectedAllowed, CancellationToken cancellationToken)
    {
        var check = new SandboxNetworkCheck
        {
            Target  = $"{transport} [{address}]:{port}", ExpectedAllowed = expectedAllowed,
            Verdict = SandboxAcceptanceVerdict.Indeterminate, Reason     = "未执行。"
        };
        var command = _limits.NetworkProbeCommandResolver is { } resolver ? resolver() : ResolveNetworkProbeCommand();
        if (command is null) return check with { Reason = "当前可执行入口不可用，无法启动 C# 网络探针。" };
        if (_limits.NetworkTargetObserver is { } beforeBinding)
            await beforeBinding($"即将独占监听 {check.Target}（0=随机端口）；可能出现防火墙提示。", cancellationToken)
               .ConfigureAwait(false);
        var                      nonce = Guid.NewGuid().ToString("N");
        ISandboxNetworkResponder responder;
        try
        {
            responder = _limits.NetworkResponderFactory is { } factory
                ? factory(transport, address, port, nonce)
                : new SandboxNetworkResponder(transport, address, port, nonce);
        }
        catch (SocketException ex)
        {
            return check with { Reason = $"监听不可用，未验证：{ex.SocketErrorCode}/{ex.NativeErrorCode}: {ex.Message}" };
        }
        catch (PlatformNotSupportedException ex)
        {
            return check with { Reason = $"地址族不可用，未验证：{ex.Message}" };
        }

        await using (responder.ConfigureAwait(false))
        {
            port  = responder.Port;
            check = check with { Target = $"{transport} [{address}]:{port}" };
            if (_limits.NetworkTargetObserver is { } beforeExchange)
                await beforeExchange($"实际目标 {check.Target}：宿主前置 → 沙箱 → 宿主后置。", cancellationToken)
                   .ConfigureAwait(false);
            var exchange = _limits.HostNetworkExchange ?? SandboxNetworkProbe.ExchangeAsync;
            var before   = await exchange(transport, address, port, nonce, cancellationToken).ConfigureAwait(false);
            check = check with { HostBefore = before };
            if (!IsMatchingControl(before, transport, address, port, nonce))
                return check with { Reason = "宿主前置正对照没有匹配 ACK，不能解释沙箱失败。" };

            try
            {
                var arguments = command.Value.Prefix.Concat([
                    "sandbox", "verify-network-probe", transport, address.ToString(), port.ToString(), nonce
                ]).ToArray();
                var run = await ExecuteOnceAsync(SandboxPolicyKind.WorkspaceWrite, null,
                                                 new PreparedProcessExecution(command.Value.Executable, arguments,
                                                                              _workspaceRoot, 30, false, null),
                                                 $"network-{transport}-{address.AddressFamily}-{port}-{nonce}",
                                                 cancellationToken).ConfigureAwait(false);
                check = check with { ExitCode = run.Result?.ExitCode, OuterTimedOut = run.Result?.TimedOut ?? false };
                // 即使探针非零退出，也保留可解析的原始错误；退出码和外层失败仍优先门控。
                string? parseError = null;
                if (run.Stdout is { Truncated: false, Content.Length: <= 8192 })
                    try
                    {
                        check = check with
                        {
                            Sandbox = JsonSerializer.Deserialize(run.Stdout.Content,
                                                                 WindowsSandboxAcceptanceJsonContext.Default
                                                                    .SandboxNetworkObservation)
                        };
                    }
                    catch (JsonException ex)
                    {
                        parseError = ex.Message;
                    }

                if (run.Result is { ExitCode: 3, TimedOut: false, Failure: null } && parseError is null &&
                    check.Sandbox is
                    {
                        Status: "error", SocketError: "AddressFamilyNotSupported" or
                                                      "ProtocolFamilyNotSupported" or "ProtocolNotSupported"
                                                   or "AddressNotAvailable"
                    } unavailable                      &&
                    unavailable.Transport == transport && unavailable.Address == address.ToString() &&
                    unavailable.Port      == port      && unavailable.Nonce   == nonce)
                    check = check with
                    {
                        Verdict = SandboxAcceptanceVerdict.Indeterminate,
                        Reason = $"沙箱探针报告地址/协议不可用，未验证：{unavailable.SocketError}/{unavailable.NativeError}。"
                    };
                else if (run.Result is not { ExitCode : 0, TimedOut: false, Failure: null } ||
                         run.Stdout is not { Truncated: false } || run.Stdout.Content.Length > 8192)
                    check = check with
                    {
                        Verdict = SandboxAcceptanceVerdict.Fail,
                        Reason = $"探针未正常完成；{Describe(run, FirstLine(run.Stderr?.Content))}"
                    };
                else if (parseError is not null)
                    check = check with { Verdict = SandboxAcceptanceVerdict.Fail, Reason = $"探针 JSON 无效：{parseError}" };
                else check = EvaluateNetworkObservation(check, transport, address, port, nonce);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                check = check with
                {
                    Verdict = SandboxAcceptanceVerdict.Fail, Reason = $"探针异常：{ex.GetType().Name}: {ex.Message}"
                };
            }

            var after = await exchange(transport, address, port, nonce, cancellationToken).ConfigureAwait(false);
            check = check with { HostAfter = after };
            if (!IsMatchingControl(after, transport, address, port, nonce) || responder.Failure is not null)
                return check with
                {
                    Verdict = check.Verdict == SandboxAcceptanceVerdict.Fail
                        ? SandboxAcceptanceVerdict.Fail
                        : SandboxAcceptanceVerdict.Indeterminate,
                    Reason = $"{check.Reason}；宿主后置正对照/监听失效，阻断未验证：{responder.Failure}"
                };
            return check;
        }
    }

    private static SandboxNetworkCheck EvaluateNetworkObservation(
        SandboxNetworkCheck check, string transport, IPAddress address, int port, string nonce)
    {
        var observed = check.Sandbox;
        if (observed is null || observed.Transport != transport || observed.Address != address.ToString() ||
            observed.Port                          != port || observed.Nonce != nonce ||
            observed.Status is not ("matched" or "wait-expired" or "socket-denied") ||
            (observed.Status == "matched"      && (!observed.Sent || (transport == "tcp" && !observed.Connected))) ||
            (observed.Status == "wait-expired" && transport == "udp" && !observed.Sent) ||
            observed is
            {
                Status: "socket-denied", SocketError: not ("AccessDenied"
                                                        or "ConnectionRefused"
                                                        or "TimedOut"
                                                        or "HostUnreachable"
                                                        or "NetworkUnreachable"
                                                        or "ConnectionReset")
            })
            return check with { Verdict = SandboxAcceptanceVerdict.Fail, Reason = "探针输出无效、nonce/目标不符或探针内部错误；不构成阻断证据。" };

        var reached = observed.Status == "matched" || (transport == "tcp" && observed.Connected);
        var passed  = check.ExpectedAllowed ? observed.Status == "matched" : !reached;
        return check with
        {
            Verdict = passed ? SandboxAcceptanceVerdict.Pass : SandboxAcceptanceVerdict.Fail,
            Reason = check.ExpectedAllowed
                ? $"TCP 环回要求匹配 ACK；实际 {observed.Status}。"
                : $"要求阻断；实际 {observed.Status}，connected={observed.Connected}，sent={observed.Sent}；仅限当前本机路径，不能唯一归因 WFP。"
        };
    }

    private static bool IsMatchingControl(SandboxNetworkObservation observation, string transport,
                                          IPAddress                 address,     int    port, string nonce) =>
        observation.Status    == "matched"          &&
        observation.Transport == transport          &&
        observation.Address   == address.ToString() &&
        observation.Port      == port               &&
        observation.Nonce     == nonce              &&
        observation.Sent                            &&
        (transport != "tcp" || observation.Connected);

    private static (string Executable, IReadOnlyList<string> Prefix)? ResolveNetworkProbeCommand()
    {
        var executable = Environment.ProcessPath;
        if (executable is null || !File.Exists(executable)) return null;
        return (executable, string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                                          StringComparison.OrdinalIgnoreCase)
                    ? [Environment.GetCommandLineArgs()[0]]
                    : []);
    }

    private static string? ResolveNonLoopbackAddress() => NetworkInterface.GetAllNetworkInterfaces()
                                                                          .Where(adapter => adapter.OperationalStatus ==
                                                                                        OperationalStatus.Up)
                                                                          .SelectMany(adapter => adapter
                                                                                       .GetIPProperties()
                                                                                       .UnicastAddresses)
                                                                          .Select(info => info.Address)
                                                                          .FirstOrDefault(address =>
                                                                                        address.AddressFamily ==
                                                                                        AddressFamily.InterNetwork &&
                                                                                        !IPAddress.IsLoopback(address))
                                                                         ?.ToString();
}
