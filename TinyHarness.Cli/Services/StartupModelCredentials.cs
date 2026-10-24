using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>解析启动默认模型的 API key，并把“凭据不可用”统一为组合层可决策的一种结果。</para>
///     <para>Resolves the startup default model's API key, unifying "credentials unavailable" into one outcome the composition layer can decide on.</para>
/// </summary>
/// <remarks>
///     <para>
///         允许降级（恢复入口）时，环境变量缺失（返回 null）与已配置凭据存储条目缺失或不支持
///         （抛出 <see cref="ConfigException" />）都归一为 null：启动组合继续，目标绑定由恢复绑定
///         从日志最后保存的选择解析，或以未绑定宿主等待显式重选。严格模式（普通 chat／run）两种
///         情况都按启动失败抛出，错误信息保留修复指引。
///     </para>
///     <para>
///         When degradation is allowed (the resume entry), both a missing environment variable (null) and a
///         configured but missing or unsupported credential-store entry (a thrown
///         <see cref="ConfigException" />) normalize to null: the composition continues and the target binding
///         is resolved by the resume binding from the log's last saved selection, or awaited as an unbound
///         host pending explicit re-selection. In strict mode (plain chat／run) both cases reject startup with
///         fix hints preserved in the error.
///     </para>
/// </remarks>
internal static class StartupModelCredentials
{
    /// <summary>
    ///     <para>解析启动默认模型的 API key；降级允许时凭据不可用返回 null，严格模式抛 <see cref="ConfigException" />。</para>
    ///     <para>Resolves the startup default model's API key; unavailable credentials return null when degradation is allowed and throw <see cref="ConfigException" /> in strict mode.</para>
    /// </summary>
    public static string? Resolve(string           environmentVariable, string credentialTarget, string? profileName,
                                  ICredentialStore credentials,         bool   allowMissing)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        string? apiKey;
        try
        {
            apiKey = ApiKeyReader.Read(environmentVariable, credentialTarget, credentials, profileName);
        }
        catch (ConfigException) when (allowMissing)
        {
            // 已配置凭据存储条目缺失或不支持：降级组合继续，恢复入口先接管历史。
            return null;
        }

        if (apiKey is null && !allowMissing)
            throw new
                ConfigException("No API key found. Run 'tinyharness auth set <profile>' or set the environment variable " +
                                $"'{environmentVariable}' before running.");
        return apiKey;
    }
}
