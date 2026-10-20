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
