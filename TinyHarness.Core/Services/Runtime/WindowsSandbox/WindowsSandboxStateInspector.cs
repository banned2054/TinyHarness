using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     沙箱就绪状态检查：setup/runner 组件存在、setup marker 完成且版本一致、cap_sid 可解析、
///     凭据文件可解析。聚合全部问题返回，不做任何修复；执行路径据此 fail closed。
///     Sandbox readiness check: setup/runner components exist, the setup marker
///     is complete with a matching version, cap_sid parses, and the credentials
///     file parses. All problems are aggregated; nothing is repaired, and the
///     execution path fails closed on the result.
/// </summary>
public sealed class WindowsSandboxStateInspector(WindowsSandboxComponents components)
{
    public WindowsSandboxComponents Components { get; } = components;

    public async Task<WindowsSandboxStatus> InspectAsync(CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        if (!File.Exists(Components.SetupExecutablePath))
            problems.Add($"Setup executable not found: {Components.SetupExecutablePath}");

        if (!File.Exists(Components.RunnerExecutablePath))
            problems.Add($"Runner executable not found: {Components.RunnerExecutablePath}");

        await InspectMarkerAsync(problems, cancellationToken).ConfigureAwait(false);
        await InspectCapabilitySidsAsync(problems, cancellationToken).ConfigureAwait(false);
        await InspectUsersFileAsync(problems, cancellationToken).ConfigureAwait(false);
        return problems.Count == 0
            ? WindowsSandboxStatus.Ok()
            : WindowsSandboxStatus.Blocked(problems);
    }

    private async Task InspectMarkerAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (!File.Exists(Components.MarkerPath))
        {
            problems.Add($"Sandbox is not provisioned (missing setup marker: {Components.MarkerPath}).");
            return;
        }

        try
        {
            if (new FileInfo(Components.MarkerPath).Length == 0)
            {
                // The helper creates the marker with CREATE_NEW and writes the JSON
                // only after success, so an empty file means unfinished setup.
                problems.Add("Sandbox setup marker exists but is empty; provisioning did not complete.");
                return;
            }

            var content = await File.ReadAllTextAsync(Components.MarkerPath, cancellationToken).ConfigureAwait(false);
            var marker  = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxSetupMarker);
            if (marker?.Version != WindowsSandboxComponents.SetupVersion)
                problems.Add(
                             $"Sandbox setup marker version {marker?.Version.ToString() ?? "unknown"} does not match the required {WindowsSandboxComponents.SetupVersion}.");
        }
        catch (JsonException ex)
        {
            problems.Add($"Sandbox setup marker is not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Sandbox setup marker could not be read at {Components.MarkerPath}: {ex.Message}");
        }
    }

    private async Task InspectCapabilitySidsAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (!File.Exists(Components.CapabilitySidPath))
        {
            problems.Add($"Capability SID table not found: {Components.CapabilitySidPath}");
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(Components.CapabilitySidPath, cancellationToken)
                                    .ConfigureAwait(false);
            var capabilitySids = JsonSerializer.Deserialize(content,
                                                            WindowsSandboxJsonContext.Default.SandboxCapabilitySids);
            if (string.IsNullOrEmpty(capabilitySids?.ReadOnly))
                problems.Add("Capability SID table is missing its readonly SID.");
        }
        catch (JsonException ex)
        {
            problems.Add($"Capability SID table is not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Capability SID table could not be read at {Components.CapabilitySidPath}: {ex.Message}");
        }
    }

    private async Task InspectUsersFileAsync(List<string> problems, CancellationToken cancellationToken)
    {
        if (!File.Exists(Components.UsersFilePath))
        {
            problems.Add($"Sandbox account credentials not found: {Components.UsersFilePath}");
            return;
        }

        try
        {
            var content = await File.ReadAllTextAsync(Components.UsersFilePath, cancellationToken)
                                    .ConfigureAwait(false);
            var users = JsonSerializer.Deserialize(content, WindowsSandboxJsonContext.Default.SandboxUsersFile);
            if (users?.Version != SandboxUsersFile.RequiredVersion)
            {
                problems.Add($"Sandbox account credentials version {users?.Version.ToString() ?? "unknown"} " +
                             $"does not match the required {SandboxUsersFile.RequiredVersion}.");
                return;
            }

            if (string.IsNullOrEmpty(users?.Offline?.Username) || string.IsNullOrEmpty(users.Offline.ProtectedPassword))
                problems.Add("Sandbox account credentials are missing the offline account entry.");
        }
        catch (JsonException ex)
        {
            problems.Add($"Sandbox account credentials are not valid JSON: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"Sandbox account credentials could not be read at {Components.UsersFilePath}: {ex.Message}");
        }
    }
}
