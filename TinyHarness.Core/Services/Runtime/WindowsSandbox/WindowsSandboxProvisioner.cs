using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         provisioning 计划：校验后的组件、构造完成的 setup payload、base64 形态与审计文件路径。
///         由 <see cref="WindowsSandboxProvisioner.BuildPlan" /> 产生；本身无副作用，不创建任何目录或
///         文件（tempRoot 只以路径形式进入 payload）。
///     </para>
///     <para>
///         The provisioning plan: validated components, the constructed setup
///         payload, its base64 form, and the audit file path. Produced by
///         <see cref="WindowsSandboxProvisioner.BuildPlan" />; it is side-effect
///         free and creates no directory or file (the temp root enters the payload
///         as a path only).
///     </para>
/// </summary>
public sealed record WindowsSandboxProvisionPlan
{
    public required WindowsSandboxComponents Components { get; init; }

    public required WindowsSandboxComponentResolution ResolvedComponents { get; init; }

    public SandboxFileIdentity? SetupIdentity { get; init; }

    public required SandboxSetupPayload Payload { get; init; }

    /// <summary>
    ///     <para>payload JSON 的规范字节（版本固定的 source-gen 序列化）。</para>
    ///     <para>The canonical bytes of the payload JSON (version-pinned source-generated serialization).</para>
    /// </summary>
    public required byte[] PayloadJson { get; init; }

    public required string Base64Payload { get; init; }

    /// <summary>
    ///     <para>payload JSON 字节的 SHA-256（大写十六进制），进入审计记录。</para>
    ///     <para>SHA-256 of the payload JSON bytes (uppercase hex), included in the audit record.</para>
    /// </summary>
    public required string PayloadSha256 { get; init; }

    /// <summary>
    ///     <para>&lt;home&gt;\.sandbox\tinyharness-provision.jsonl；仅在提权尝试结束后由 provisioner 创建。</para>
    ///     <para>
    ///         &lt;home&gt;\.sandbox\tinyharness-provision.jsonl; created by the provisioner only after an elevation attempt
    ///         ends.
    ///     </para>
    /// </summary>
    public required string AuditPath { get; init; }

    public required string TempRoot { get; init; }

    public required IReadOnlyList<string> AdditionalWriteRoots { get; init; }
}

/// <summary>
///     <para>provisioning 的终态。</para>
///     <para>The terminal outcomes of provisioning.</para>
/// </summary>
public enum SandboxProvisionOutcome
{
    /// <summary>
    ///     <para>提权 helper 以退出码 0 结束（事后状态见 PostInspection）。</para>
    ///     <para>The elevated helper finished with exit code 0 (see PostInspection for the follow-up status).</para>
    /// </summary>
    Completed,

    /// <summary>
    ///     <para>提权 helper 以非 0 退出码结束；SetupError 携带 setup_error.json 详情。</para>
    ///     <para>The elevated helper finished with a non-zero exit code; SetupError carries the setup_error.json details.</para>
    /// </summary>
    Failed,

    /// <summary>
    ///     <para>用户在 UAC 对话框拒绝提权。</para>
    ///     <para>The user declined elevation in the UAC dialog.</para>
    /// </summary>
    Declined,

    /// <summary>
    ///     <para>超时或取消；机器状态未知，不 kill 提权进程。</para>
    ///     <para>Timeout or cancellation; the machine state is unknown and the elevated process is never killed.</para>
    /// </summary>
    NotFinished
}

/// <summary>
///     <para>
///         provisioning 结果：终态、退出码、setup_error 描述、事后状态检查与审计写入失败说明
///         （审计尽力而为，失败不改写终态）。
///     </para>
///     <para>
///         The provisioning result: outcome, exit code, the setup_error
///         description, the post-run inspection, and an explanation when the
///         best-effort audit append failed (an audit failure never rewrites the
///         outcome).
///     </para>
/// </summary>
public sealed record WindowsSandboxProvisionResult
{
    public required SandboxProvisionOutcome Outcome { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>
    ///     <para>Failed 时为 "code: message" 或 setup_error.json 缺失时的占位说明。</para>
    ///     <para>On Failed, a "code: message" string or a placeholder when setup_error.json is missing.</para>
    /// </summary>
    public string? SetupError { get; init; }

    /// <summary>
    ///     <para>Completed 时对 home 的重新检查；Ready 仍可能为 false（如 runner 未放置）。</para>
    ///     <para>
    ///         The post-run re-inspection of the home on Completed; Ready can still be false (e.g. the runner was not
    ///         placed).
    ///     </para>
    /// </summary>
    public WindowsSandboxStatus? PostInspection { get; init; }

    public string? AuditWriteError { get; init; }
}

/// <summary>
///     <para>
///         显式 provisioning 的 Core 编排：校验并构造 payload（复用组合器同一份 root/环境推导）、经
///         <see cref="ISandboxSetupElevator" /> 提权执行 setup.exe、读取 setup_error.json、事后重新检查
///         状态，并把每次提权尝试追加进轻量审计 JSONL。确认门、副作用展示与退出码映射属于 CLI；
///         日常执行路径仍由 <see cref="ProcessSandboxSetupInvoker" /> 只做 refresh，本类型不放宽它。
///     </para>
///     <para>
///         Core orchestration for explicit provisioning: it validates and builds
///         the payload (reusing the composer's exact root/environment
///         derivation), runs setup.exe through <see cref="ISandboxSetupElevator" />,
///         reads setup_error.json, re-inspects the home afterward, and appends
///         every elevation attempt to a lightweight audit JSONL. The confirmation
///         gate, the side-effect presentation, and exit-code mapping belong to the
///         CLI; the execution path keeps refreshing only via
///         <see cref="ProcessSandboxSetupInvoker" />, which this type never widens.
///     </para>
/// </summary>
public sealed class WindowsSandboxProvisioner(
    ISandboxSetupElevator        elevator,
    ISandboxDenyReadRefreshGate? denyReadRefreshGate = null,
    TimeSpan?                    denyReadGateTimeout = null)
{
    private const    int                         ErrorSummaryLimit    = 400;
    private readonly ISandboxDenyReadRefreshGate _denyReadRefreshGate = denyReadRefreshGate ?? new GlobalDenyReadRefreshGate();
    private readonly TimeSpan                    _denyReadGateTimeout = denyReadGateTimeout ?? GlobalDenyReadRefreshGate.DefaultAcquireTimeout;

    /// <summary>
    ///     <para>
    ///         构造 provisioning 计划：路径校验、root/环境推导、payload 构造与 base64 长度检查。
    ///         无副作用。设置非法（路径缺失/非绝对、额外写根相对）或 payload base64 超过 UAC 位置
    ///         参数上限时抛带上下文的异常——UAC 启动（UseShellExecute=true）无法使用环境分块通道。
    ///     </para>
    ///     <para>
    ///         Builds the provisioning plan: path validation, root/environment
    ///         derivation, payload construction, and the base64 length check. No
    ///         side effects. Invalid settings (missing/non-absolute paths, relative
    ///         additional write roots) or a base64 payload past the UAC positional
    ///         argument limit throw contextual exceptions — the UAC launch
    ///         (UseShellExecute=true) cannot use the chunked environment channel.
    ///     </para>
    /// </summary>
    public WindowsSandboxProvisionPlan BuildPlan(
        WindowsSandboxSettings settings,
        string                 commandWorkingDirectory,
        string                 realUser,
        string?                appBaseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(realUser);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                                                    "Windows sandbox provisioning is available on Windows only.");

        var plan = WindowsSandboxPolicyPlanner.Plan(settings, commandWorkingDirectory,
                                                    appBaseDirectory : appBaseDirectory);
        IReadOnlyList<string> effectiveWriteRoots = SandboxIsolationPolicy.Deduplicate(
        [
            commandWorkingDirectory,
            .. plan.Policy.EffectiveWriteRoots,
            .. plan.Policy.TempWriteRoots,
            plan.TempRoot,
            .. plan.AdditionalWriteRoots
        ]);
        // 提权前与运行时共用同一套组件与实际写根集合校验：setup 与 runner 均须存在、合法、随包不越界且落在所有写根之外。
        var setupIdentity = WindowsSandboxComponentResolver.ValidateExecutableForUse(
            plan.ResolvedComponents.Setup, plan.ResolvedComponents.AppBaseDirectory, effectiveWriteRoots);
        WindowsSandboxComponentResolver.ValidateExecutableForUse(
            plan.ResolvedComponents.Runner, plan.ResolvedComponents.AppBaseDirectory, effectiveWriteRoots);
        // 若该 home 已由另一端登记了保护路径（例如另一套安装包组件），provisioning 写根同样不得覆盖它们。
        WindowsSandboxComposer.ValidateWriteRootsAgainstRegisteredProtectedPaths(effectiveWriteRoots,
                                                                                 plan.SandboxHome,
                                                                                 "provisioning write root");

        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = plan.SetupExecutablePath,
            RunnerExecutablePath = plan.RunnerExecutablePath,
            AllowTcpLoopback     = settings.AllowTcpLoopback,
            SandboxHome          = plan.SandboxHome
        };
        EnsureHomeCarriesNoLegacyCodexState(components);
        EnsureHomeModeMatches(components);

        // 显式 provisioning 使用 full 模式；两个网络模式均不开放代理端口。
        var payload = plan.Policy.CreateSetupPayload(components, commandWorkingDirectory, realUser, false);
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload,
                                                              WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        var base64Payload = Convert.ToBase64String(payloadJson);
        if (base64Payload.Length > ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit)
            throw new
                InvalidOperationException($"The provisioning payload is {base64Payload.Length} base64 characters, past the " +
                                          $"{ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit} argument limit of the UAC launch path " +
                                          "(the elevated helper cannot use the chunked environment channel). " +
                                          "Reduce settings.windowsSandbox.additionalWriteRoots and provision again.");

        return new WindowsSandboxProvisionPlan
        {
            Components           = components,
            ResolvedComponents   = plan.ResolvedComponents,
            SetupIdentity        = setupIdentity,
            Payload              = payload,
            PayloadJson          = payloadJson,
            Base64Payload        = base64Payload,
            PayloadSha256        = Convert.ToHexString(SHA256.HashData(payloadJson)),
            AuditPath            = Path.Combine(components.SandboxDirectory, "tinyharness-provision.jsonl"),
            TempRoot             = plan.TempRoot,
            AdditionalWriteRoots = plan.AdditionalWriteRoots
        };
    }

    /// <summary>
    ///     <para>
    ///         拒绝向仍带旧 Codex 用户名的 home provisioning：该 home 属于 Codex 发布版，继续写入会
    ///         覆写它的 setup marker/凭据并让两个安装争用同一目录。只读取已有 marker/users 文件，
    ///         解析失败按"无 legacy"处理（Inspector 会另行报告损坏状态）。无副作用。
    ///     </para>
    ///     <para>
    ///         Refuses provisioning into a home that still carries legacy Codex
    ///         usernames: that home belongs to a Codex release, and writing to it
    ///         would overwrite its setup marker/credentials and pit the two installs
    ///         against each other over one directory. Only existing marker/users
    ///         files are read; parse failures count as "not legacy" (the inspector
    ///         reports the corruption separately). No side effects.
    ///     </para>
    /// </summary>
    private static void EnsureHomeCarriesNoLegacyCodexState(WindowsSandboxComponents components)
    {
        if (ReadAccountNames(components.MarkerPath, WindowsSandboxJsonContext.Default.SandboxSetupMarker,
                             marker => marker?.OfflineUsername, marker => marker?.OnlineUsername)
            is { } legacyMarkerName)
            throw new
                InvalidOperationException($"The sandbox home '{components.SandboxHome}' still names the legacy Codex account '{legacyMarkerName}' in its setup marker; it belongs to a Codex release. " +
                                          "Refusing to provision into it — point settings.windowsSandbox.sandboxHome at an independent TinyHarness sandbox home instead.");

        if (ReadAccountNames(components.UsersFilePath, WindowsSandboxJsonContext.Default.SandboxUsersFile,
                             users => users?.Offline?.Username, users => users?.Online?.Username)
            is { } legacyUsersName)
            throw new
                InvalidOperationException($"The sandbox home '{components.SandboxHome}' still names the legacy Codex account '{legacyUsersName}' in its account credentials; it belongs to a Codex release. " +
                                          "Refusing to provision into it — point settings.windowsSandbox.sandboxHome at an independent TinyHarness sandbox home instead.");
    }

    /// <summary>
    ///     <para>提权前拒绝跨网络模式复用 home，避免覆盖另一模式的凭据。</para>
    ///     <para>Rejects home reuse across network modes before elevation to preserve the other mode's credentials.</para>
    /// </summary>
    private static void EnsureHomeModeMatches(WindowsSandboxComponents components)
    {
        try
        {
            InspectHomeMode(components);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new
                InvalidOperationException($"Cannot inspect the existing sandbox home '{components.SandboxHome}' before provisioning; no elevation was attempted.",
                                          ex);
        }
    }

    private static void InspectHomeMode(WindowsSandboxComponents components)
    {
        if (File.Exists(components.MarkerPath))
        {
            var marker = JsonSerializer.Deserialize(File.ReadAllText(components.MarkerPath),
                                                    WindowsSandboxJsonContext.Default.SandboxSetupMarker);
            if (marker is null || marker.AllowTcpLoopback != components.AllowTcpLoopback ||
                !string.Equals(marker.OfflineUsername, components.OfflineUsername,
                               StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(marker.OnlineUsername, components.OnlineUsername, StringComparison.OrdinalIgnoreCase))
                throw new
                    InvalidOperationException("The sandbox home belongs to another network mode or account pair; select a separate sandbox home.");
        }

        if (!File.Exists(components.UsersFilePath)) return;
        var users = JsonSerializer.Deserialize(File.ReadAllText(components.UsersFilePath),
                                               WindowsSandboxJsonContext.Default.SandboxUsersFile);
        if (users is null ||
            !string.Equals(users.Offline?.Username, components.OfflineUsername,
                           StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(users.Online?.Username, components.OnlineUsername, StringComparison.OrdinalIgnoreCase))
            throw new
                InvalidOperationException("The sandbox credentials belong to another account pair; select a separate sandbox home.");
    }

    /// <summary>
    ///     <para>
    ///         读取一个 JSON 文件并提取 offline/online 用户名，任一为 legacy Codex 名时返回它；文件不存在、
    ///         JSON 损坏或无 legacy 名时返回 null。
    ///     </para>
    ///     <para>
    ///         Reads a JSON file and extracts the offline/online usernames, returning
    ///         either one when it is a legacy Codex name; a missing file, broken
    ///         JSON, or no legacy name yields null.
    ///     </para>
    /// </summary>
    private static string? ReadAccountNames<T>(
        string path, JsonTypeInfo<T> jsonContext, Func<T?, string?> offlineSelector, Func<T?, string?> onlineSelector)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var parsed  = JsonSerializer.Deserialize(File.ReadAllText(path), jsonContext);
            var offline = offlineSelector(parsed);
            var online  = onlineSelector(parsed);
            return WindowsSandboxComponents.IsLegacyCodexAccountName(offline) ? offline
                : WindowsSandboxComponents.IsLegacyCodexAccountName(online)   ? online
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     <para>
    ///         提权执行计划并汇总结果。无论 <c>SandboxHome</c> 是已存在还是首次初始化尚未创建，均在按
    ///         home 规范键控的拒读刷新门内串行执行最终身份复核、共享保护清单复核与提权写 ACL，使之与
    ///         另一端的组件登记（<see cref="WindowsSandboxComposer.Compose" />）使用同一同步边界。正常结束
    ///         （Completed / Failed / Declined）立即清理持久化租约并释放门；NotFinished 不 kill 提权进程，
    ///         通过 <see cref="ISandboxDenyReadRefreshGateHandle.PersistUntilProcessExits" /> 持久化绑定实际
    ///         写入者进程身份，使即便 CLI 进程立即退出、互斥体被内核标记为已放弃，保护仍持续到 OS 确认
    ///         实际写入者退出。每次调用都追加一行审计，Completed 后重新 InspectAsync。
    ///     </para>
    ///     <para>
    ///         Runs the plan elevated and summarizes the outcome. Holds the canonical home-keyed deny-read
    ///         refresh gate across the final identity check, the shared protected-path check, and elevated
    ///         ACL writes for both existing homes and first-time provisioning (before the home directory
    ///         exists), sharing the same synchronization boundary with peer component registration
    ///         (<see cref="WindowsSandboxComposer.Compose" />). Completed / Failed / Declined clear the
    ///         durable lease and release the gate immediately; NotFinished never kills the elevated process
    ///         and durably binds protection to the actual writer process through
    ///         <see cref="ISandboxDenyReadRefreshGateHandle.PersistUntilProcessExits" /> so protection
    ///         survives even after the CLI exits and the mutex becomes abandoned, until the OS confirms the
    ///         writer has exited. Every call appends one audit line, and Completed re-runs InspectAsync.
    ///     </para>
    /// </summary>
    public async Task<WindowsSandboxProvisionResult> ProvisionAsync(
        WindowsSandboxProvisionPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 首次 provisioning（home 尚未创建）与已有 home 均使用同一规范 home 键获取跨进程刷新门，
        // 避免 helper 创建 home 后、ACL 尚未写完期间另一端并发登记组件。
        ISandboxDenyReadRefreshGateHandle? gate =
            await _denyReadRefreshGate
                  .TryAcquireForProvisioningAsync(plan.Components.SandboxHome, _denyReadGateTimeout, cancellationToken)
                  .ConfigureAwait(false)
            ?? throw new
                InvalidOperationException($"The deny-read refresh gate for sandbox home '{plan.Components.SandboxHome}' " +
                                          $"was not acquired within {(int)_denyReadGateTimeout.TotalSeconds}s; " +
                                          "refusing to start provisioning while another operation holds the home gate.");

        SandboxSetupElevationResult elevation;
        try
        {
            // 提权前重新核对计划里的 setup 文件身份与共享保护清单，发现替换或冲突即停止，绝不发起 UAC。
            if (plan.SetupIdentity is not null)
                WindowsSandboxComponentResolver.VerifyFileIdentityUnchanged(plan.Components.SetupExecutablePath,
                                                                            plan.SetupIdentity,
                                                                            "Windows sandbox setup executable");
            WindowsSandboxComposer.ValidateWriteRootsAgainstRegisteredProtectedPaths(plan.Payload.WriteRoots,
                                                                                     plan.Components.SandboxHome,
                                                                                     "provisioning write root");

            // 启动提权 helper 前先建立持久化预启动租约：若落盘失败则不发起 UAC；若启动后 CLI 退出或
            // 后续租约更新失败，已落盘的预启动租约仍能持续阻断另一端。
            gate.BeginProvisioning(plan.Components.SetupExecutablePath);

            elevation = await elevator.ElevateAsync(plan.Components,
                                                    plan.Base64Payload,
                                                    identity => gate.BeginExternalWriter(identity,
                                                        plan.Components.SetupExecutablePath),
                                                    cancellationToken)
                                      .ConfigureAwait(false);
            if (elevation.Outcome == SandboxSetupElevationOutcome.DidNotFinish)
            {
                // helper 退出未确认（超时或取消，可能仍在后台修改 ACL）：先断开 finally 的 Dispose 路径，
                // 再转移所有权并尽力更新持久化租约（更新失败保留既有租约），确保 CLI 退出后保护不中断。
                var handedOffGate = gate;
                gate = null;
                handedOffGate.PersistUntilProcessExits(elevation.ProcessIdentity,
                                                       elevation.ExitConfirmation ?? Task.FromResult(false),
                                                       plan.Components.SetupExecutablePath);
            }
        }
        finally
        {
            // 仅在确认 helper 未启动（前置校验失败/预启动租约失败/Declined/启动前异常）或已确认退出
            // （Completed/Failed）时经 Dispose 清理租约并释放门；DidNotFinish 已置空 gate。
            gate?.Dispose();
        }

        WindowsSandboxProvisionResult result;

        string  outcome;
        string? errorSummary = null;
        switch (elevation.Outcome)
        {
            case SandboxSetupElevationOutcome.DeclinedByUser :
                outcome = "declined";
                result  = new WindowsSandboxProvisionResult { Outcome = SandboxProvisionOutcome.Declined };
                break;

            case SandboxSetupElevationOutcome.DidNotFinish :
                outcome = "not-finished";
                result  = new WindowsSandboxProvisionResult { Outcome = SandboxProvisionOutcome.NotFinished };
                break;

            case SandboxSetupElevationOutcome.Completed when elevation.ExitCode != 0 :
                outcome = "failed";
                // helper 可能在调用方已取消（竞态 Ctrl+C）时失败；此处有界读取刻意忽略取消，让失败仍进入下方的审计行。
                errorSummary = await SandboxSetupErrorReader.DescribeAsync(plan.Components.SetupErrorPath)
                                                            .ConfigureAwait(false);
                result = new WindowsSandboxProvisionResult
                {
                    Outcome = SandboxProvisionOutcome.Failed, ExitCode = elevation.ExitCode, SetupError = errorSummary
                };
                break;

            case SandboxSetupElevationOutcome.Completed :
                outcome = "completed";
                result = new WindowsSandboxProvisionResult
                {
                    Outcome = SandboxProvisionOutcome.Completed, ExitCode = elevation.ExitCode
                };
                break;

            default :
                throw
                    new InvalidOperationException($"Unhandled sandbox setup elevation outcome '{elevation.Outcome}'.");
        }

        var auditError = await TryAppendAuditAsync(plan, outcome, elevation.ExitCode, errorSummary)
           .ConfigureAwait(false);
        result = result with { AuditWriteError = auditError };

        if (result.Outcome == SandboxProvisionOutcome.Completed)
            result = result with
            {
                PostInspection = await new WindowsSandboxStateInspector(plan.Components)
                                      .InspectAsync(cancellationToken)
                                      .ConfigureAwait(false)
            };

        return result;
    }

    /// <summary>
    ///     <para>
    ///         尽力而为的审计追加：目录不存在则创建；写失败返回说明文本而不抛出、不改写命令结果。
    ///     </para>
    ///     <para>
    ///         Best-effort audit append: missing directories are created; a write
    ///         failure returns an explanation instead of throwing and never
    ///         rewrites the command result.
    ///     </para>
    /// </summary>
    private static async Task<string?> TryAppendAuditAsync(
        WindowsSandboxProvisionPlan plan, string outcome, int? exitCode, string? error)
    {
        var record = new SandboxProvisionAuditRecord
        {
            TimestampUtc  = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Outcome       = outcome,
            ExitCode      = exitCode,
            Mode          = plan.Payload.Mode,
            RefreshOnly   = plan.Payload.RefreshOnly,
            PayloadSha256 = plan.PayloadSha256,
            Error         = error is null ? null : Truncate(error)
        };
        try
        {
            var directory = Path.GetDirectoryName(plan.AuditPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(record, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);
            // 每次提权尝试都承诺写入审计行——拒绝、失败、超时与取消在内——因此追加刻意忽略调用方取消。
            await File.AppendAllTextAsync(plan.AuditPath, line + Environment.NewLine)
                      .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not append the provisioning audit record to '{plan.AuditPath}': {ex.Message}";
        }
    }

    private static string Truncate(string text) =>
        text.Length <= ErrorSummaryLimit ? text : text[..ErrorSummaryLimit] + "…";
}
