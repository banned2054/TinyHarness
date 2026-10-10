using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     provisioning 计划：校验后的组件、构造完成的 setup payload、base64 形态与审计文件路径。
///     由 <see cref="WindowsSandboxProvisioner.BuildPlan" /> 产生；本身无副作用，不创建任何目录或
///     文件（tempRoot 只以路径形式进入 payload）。
///     The provisioning plan: validated components, the constructed setup
///     payload, its base64 form, and the audit file path. Produced by
///     <see cref="WindowsSandboxProvisioner.BuildPlan" />; it is side-effect
///     free and creates no directory or file (the temp root enters the payload
///     as a path only).
/// </summary>
public sealed record WindowsSandboxProvisionPlan
{
    public required WindowsSandboxComponents Components { get; init; }

    public required SandboxSetupPayload Payload { get; init; }

    /// <summary>payload JSON 的规范字节（版本固定的 source-gen 序列化）。</summary>
    public required byte[] PayloadJson { get; init; }

    public required string Base64Payload { get; init; }

    /// <summary>payload JSON 字节的 SHA-256（大写十六进制），进入审计记录。</summary>
    public required string PayloadSha256 { get; init; }

    /// <summary>&lt;home&gt;\.sandbox\tinyharness-provision.jsonl；仅在提权尝试结束后由 provisioner 创建。</summary>
    public required string AuditPath { get; init; }

    public required string TempRoot { get; init; }

    public required IReadOnlyList<string> AdditionalWriteRoots { get; init; }
}

/// <summary>provisioning 的终态。</summary>
public enum SandboxProvisionOutcome
{
    /// <summary>提权 helper 以退出码 0 结束（事后状态见 PostInspection）。</summary>
    Completed,

    /// <summary>提权 helper 以非 0 退出码结束；SetupError 携带 setup_error.json 详情。</summary>
    Failed,

    /// <summary>用户在 UAC 对话框拒绝提权。</summary>
    Declined,

    /// <summary>超时或取消；机器状态未知，不 kill 提权进程。</summary>
    NotFinished
}

/// <summary>
///     provisioning 结果：终态、退出码、setup_error 描述、事后状态检查与审计写入失败说明
///     （审计尽力而为，失败不改写终态）。
///     The provisioning result: outcome, exit code, the setup_error
///     description, the post-run inspection, and an explanation when the
///     best-effort audit append failed (an audit failure never rewrites the
///     outcome).
/// </summary>
public sealed record WindowsSandboxProvisionResult
{
    public required SandboxProvisionOutcome Outcome { get; init; }

    public int? ExitCode { get; init; }

    /// <summary>Failed 时为 "code: message" 或 setup_error.json 缺失时的占位说明。</summary>
    public string? SetupError { get; init; }

    /// <summary>Completed 时对 home 的重新检查；Ready 仍可能为 false（如 runner 未放置）。</summary>
    public WindowsSandboxStatus? PostInspection { get; init; }

    public string? AuditWriteError { get; init; }
}

/// <summary>
///     显式 provisioning 的 Core 编排：校验并构造 payload（复用组合器同一份 root/环境推导）、经
///     <see cref="ISandboxSetupElevator" /> 提权执行 setup.exe、读取 setup_error.json、事后重新检查
///     状态，并把每次提权尝试追加进轻量审计 JSONL。确认门、副作用展示与退出码映射属于 CLI；
///     日常执行路径仍由 <see cref="ProcessSandboxSetupInvoker" /> 只做 refresh，本类型不放宽它。
///     Core orchestration for explicit provisioning: it validates and builds
///     the payload (reusing the composer's exact root/environment
///     derivation), runs setup.exe through <see cref="ISandboxSetupElevator" />,
///     reads setup_error.json, re-inspects the home afterward, and appends
///     every elevation attempt to a lightweight audit JSONL. The confirmation
///     gate, the side-effect presentation, and exit-code mapping belong to the
///     CLI; the execution path keeps refreshing only via
///     <see cref="ProcessSandboxSetupInvoker" />, which this type never widens.
/// </summary>
public sealed class WindowsSandboxProvisioner(ISandboxSetupElevator elevator)
{
    private const int ErrorSummaryLimit = 400;

    /// <summary>
    ///     构造 provisioning 计划：路径校验、root/环境推导、payload 构造与 base64 长度检查。
    ///     无副作用。设置非法（路径缺失/非绝对、额外写根相对）或 payload base64 超过 UAC 位置
    ///     参数上限时抛带上下文的异常——UAC 启动（UseShellExecute=true）无法使用环境分块通道。
    ///     Builds the provisioning plan: path validation, root/environment
    ///     derivation, payload construction, and the base64 length check. No
    ///     side effects. Invalid settings (missing/non-absolute paths, relative
    ///     additional write roots) or a base64 payload past the UAC positional
    ///     argument limit throw contextual exceptions — the UAC launch
    ///     (UseShellExecute=true) cannot use the chunked environment channel.
    /// </summary>
    public WindowsSandboxProvisionPlan BuildPlan(WindowsSandboxSettings settings,
                                                 string               commandWorkingDirectory,
                                                 string               realUser)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandWorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(realUser);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                                                    "Windows sandbox provisioning is available on Windows only.");

        var plan = WindowsSandboxPolicyPlanner.Plan(settings, commandWorkingDirectory);
        var components = new WindowsSandboxComponents
        {
            SetupExecutablePath  = plan.SetupExecutablePath,
            RunnerExecutablePath = plan.RunnerExecutablePath,
            SandboxHome          = plan.SandboxHome
        };
        EnsureHomeCarriesNoLegacyCodexState(components);

        // Provisioning uses the default "full" mode with refresh_only=false;
        // proxy ports stay empty under the fixed offline policy.
        var payload = plan.Policy.CreateSetupPayload(components, commandWorkingDirectory, realUser, refreshOnly : false);
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload,
                                                              WindowsSandboxJsonContext.Default.SandboxSetupPayload);
        var base64Payload = Convert.ToBase64String(payloadJson);
        if (base64Payload.Length > ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit)
            throw new InvalidOperationException(
                                                $"The provisioning payload is {base64Payload.Length} base64 characters, past the " +
                                                $"{ProcessSandboxSetupInvoker.PayloadArgumentCharacterLimit} argument limit of the UAC launch path " +
                                                "(the elevated helper cannot use the chunked environment channel). " +
                                                "Reduce settings.windowsSandbox.additionalWriteRoots and provision again.");

        return new WindowsSandboxProvisionPlan
        {
            Components           = components,
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
    ///     拒绝向仍带旧 Codex 用户名的 home provisioning：该 home 属于 Codex 发布版，继续写入会
    ///     覆写它的 setup marker/凭据并让两个安装争用同一目录。只读取已有 marker/users 文件，
    ///     解析失败按"无 legacy"处理（Inspector 会另行报告损坏状态）。无副作用。
    ///     Refuses provisioning into a home that still carries legacy Codex
    ///     usernames: that home belongs to a Codex release, and writing to it
    ///     would overwrite its setup marker/credentials and pit the two installs
    ///     against each other over one directory. Only existing marker/users
    ///     files are read; parse failures count as "not legacy" (the inspector
    ///     reports the corruption separately). No side effects.
    /// </summary>
    private static void EnsureHomeCarriesNoLegacyCodexState(WindowsSandboxComponents components)
    {
        if (ReadAccountNames(components.MarkerPath, WindowsSandboxJsonContext.Default.SandboxSetupMarker,
                             marker => marker?.OfflineUsername, marker => marker?.OnlineUsername)
            is { } legacyMarkerName)
            throw new InvalidOperationException(
                $"The sandbox home '{components.SandboxHome}' still names the legacy Codex account '{legacyMarkerName}' in its setup marker; it belongs to a Codex release. " +
                "Refusing to provision into it — point settings.windowsSandbox.sandboxHome at an independent TinyHarness sandbox home instead.");

        if (ReadAccountNames(components.UsersFilePath, WindowsSandboxJsonContext.Default.SandboxUsersFile,
                             users => users?.Offline?.Username, users => users?.Online?.Username)
            is { } legacyUsersName)
            throw new InvalidOperationException(
                $"The sandbox home '{components.SandboxHome}' still names the legacy Codex account '{legacyUsersName}' in its account credentials; it belongs to a Codex release. " +
                "Refusing to provision into it — point settings.windowsSandbox.sandboxHome at an independent TinyHarness sandbox home instead.");
    }

    /// <summary>
    ///     读取一个 JSON 文件并提取 offline/online 用户名，任一为 legacy Codex 名时返回它；文件不存在、
    ///     JSON 损坏或无 legacy 名时返回 null。
    ///     Reads a JSON file and extracts the offline/online usernames, returning
    ///     either one when it is a legacy Codex name; a missing file, broken
    ///     JSON, or no legacy name yields null.
    /// </summary>
    private static string? ReadAccountNames<T>(string path, JsonTypeInfo<T> jsonContext,
                                               Func<T?, string?> offlineSelector, Func<T?, string?> onlineSelector)
    {
        try
        {
            if (!File.Exists(path)) return null;

            var parsed    = JsonSerializer.Deserialize(File.ReadAllText(path), jsonContext);
            var offline   = offlineSelector(parsed);
            var online    = onlineSelector(parsed);
            return WindowsSandboxComponents.IsLegacyCodexAccountName(offline) ? offline
                       : WindowsSandboxComponents.IsLegacyCodexAccountName(online) ? online
                       : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     提权执行计划并汇总结果。每次调用都对应一次提权尝试并追加一行审计（含拒绝、失败、
    ///     超时/取消）。Completed 后重新 InspectAsync；NotFinished 不 kill 提权进程，状态未知。
    ///     Runs the plan elevated and summarizes the outcome. Every call is one
    ///     elevation attempt and appends one audit line (declines, failures,
    ///     and timeouts/cancellations included). Completed re-runs
    ///     InspectAsync; NotFinished never kills the elevated process — the
    ///     machine state is unknown.
    /// </summary>
    public async Task<WindowsSandboxProvisionResult> ProvisionAsync(WindowsSandboxProvisionPlan plan,
                                                                    CancellationToken        cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var elevation = await elevator.ElevateAsync(plan.Components, plan.Base64Payload, cancellationToken)
                                      .ConfigureAwait(false);

        WindowsSandboxProvisionResult result;
        string  outcome;
        string? errorSummary = null;
        switch (elevation.Outcome)
        {
            case SandboxSetupElevationOutcome.DeclinedByUser :
                outcome = "declined";
                result = new WindowsSandboxProvisionResult { Outcome = SandboxProvisionOutcome.Declined };
                break;

            case SandboxSetupElevationOutcome.DidNotFinish :
                outcome = "not-finished";
                result = new WindowsSandboxProvisionResult { Outcome = SandboxProvisionOutcome.NotFinished };
                break;

            case SandboxSetupElevationOutcome.Completed when elevation.ExitCode != 0 :
                outcome     = "failed";
                // The helper may fail while the caller is already cancelling (a raced
                // Ctrl+C); this bounded read deliberately ignores that so the failure
                // still reaches the audit line below.
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
                throw new InvalidOperationException($"Unhandled sandbox setup elevation outcome '{elevation.Outcome}'.");
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
    ///     尽力而为的审计追加：目录不存在则创建；写失败返回说明文本而不抛出、不改写命令结果。
    ///     Best-effort audit append: missing directories are created; a write
    ///     failure returns an explanation instead of throwing and never
    ///     rewrites the command result.
    /// </summary>
    private static async Task<string?> TryAppendAuditAsync(WindowsSandboxProvisionPlan plan,
                                                           string                      outcome,
                                                           int?                        exitCode,
                                                           string?                     error)
    {
        var record = new SandboxProvisionAuditRecord
        {
            TimestampUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Outcome      = outcome,
            ExitCode     = exitCode,
            Mode         = plan.Payload.Mode,
            RefreshOnly  = plan.Payload.RefreshOnly,
            PayloadSha256 = plan.PayloadSha256,
            Error        = error is null ? null : Truncate(error)
        };
        try
        {
            var directory = Path.GetDirectoryName(plan.AuditPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var line = JsonSerializer.Serialize(record, WindowsSandboxJsonContext.Default.SandboxProvisionAuditRecord);
            // The audit line is promised for every elevation attempt — declines,
            // failures, timeouts, and cancels included — so the append deliberately
            // ignores caller cancellation.
            await File.AppendAllTextAsync(plan.AuditPath, line + Environment.NewLine)
                      .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"could not append the provisioning audit record to '{plan.AuditPath}': {ex.Message}";
        }
    }

    private static string Truncate(string text)
    {
        return text.Length <= ErrorSummaryLimit ? text : text[..ErrorSummaryLimit] + "…";
    }
}
