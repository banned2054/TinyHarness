using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Core.Services.Mcp;

/// <summary>
///     只从 TinyHarness 用户配置组装 MCP worker 的可信启动参数。不会读取当前工作区里的
///     tinyharness.json，也不会使用其中的 commandRules。工作区与模型 profile 在服务启动时固定。
///     Resolves trusted MCP worker startup settings exclusively from TinyHarness user config. It never
///     reads a target workspace's tinyharness.json or adopts its commandRules. Workspace and model
///     profile are fixed for the lifetime of the server.
/// </summary>
public sealed class McpWorkerHostConfiguration
{
    private McpWorkerHostConfiguration(string model, string endpoint, string endpointType, string apiKey,
                                       string workspaceRoot, string userConfigPath,
                                       WorkerExecutionOptions executionOptions)
    {
        Model            = model;
        Endpoint         = endpoint;
        EndpointType     = endpointType;
        ApiKey           = apiKey;
        WorkspaceRoot    = workspaceRoot;
        UserConfigPath   = userConfigPath;
        ExecutionOptions = executionOptions;
    }

    public string Model { get; }

    public string Endpoint { get; }

    /// <summary>仅用于启动诊断与验收记录，不包含 endpoint URL 或凭据。The scheme only; no URL or credential.</summary>
    public string EndpointType { get; }

    /// <summary>只用于构造模型 client；调用方不得写入日志、协议响应或审计。Used only to construct the model client.</summary>
    public string ApiKey { get; }

    public string WorkspaceRoot { get; }

    public string UserConfigPath { get; }

    public WorkerExecutionOptions ExecutionOptions { get; }

    /// <summary>
    ///     从给定用户配置解析默认 profile 和其中显式选择的模型。显式参数只供离线测试/嵌入调用；
    ///     MCP CLI 入口固定使用 <see cref="UserConfigStore.DefaultFilePath"/>，忽略项目配置文件。
    ///     Resolves the default profile and its explicitly selected model from the user config. Explicit
    ///     paths are for offline tests/embedded callers; the MCP CLI always uses the standard user config
    ///     path and ignores project config files.
    /// </summary>
    public static async Task<McpWorkerHostConfiguration> ResolveAsync(
        CancellationToken cancellationToken,
        string?           userConfigPath   = null,
        string?           workingDirectory = null,
        ICredentialStore? credentialStore  = null)
    {
        var configPath    = Path.GetFullPath(userConfigPath   ?? UserConfigStore.DefaultFilePath());
        var baseDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        var userConfig    = await UserConfigStore.LoadAsync(configPath, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(userConfig.DefaultProfile))
            throw new
                ConfigException($"MCP worker requires a default provider profile in user config '{configPath}'. " +
                                "Run 'tinyharness provider use <profile>' and 'tinyharness model use <model-id>'.");

        var profile = userConfig.Profiles.FirstOrDefault(item =>
                                                             string.Equals(item.Name, userConfig.DefaultProfile,
                                                                           StringComparison.Ordinal));
        if (profile is null)
            throw new ConfigException($"User config '{configPath}' names a missing default profile. " +
                                      "Run 'tinyharness provider list' and 'tinyharness provider use <profile>'.");

        if (!Uri.TryCreate(profile.Endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme is not ("http" or "https")                           ||
            !string.IsNullOrEmpty(endpointUri.UserInfo))
            throw new
                ConfigException($"The default profile in user config '{configPath}' must declare an absolute HTTP or HTTPS endpoint without embedded credentials.");

        if (string.IsNullOrWhiteSpace(profile.DefaultModel))
            throw new ConfigException($"The default profile in user config '{configPath}' has no selected model. " +
                                      "Run 'tinyharness model use <model-id>'.");

        var selectedModel = profile.Models.FirstOrDefault(item =>
                                                              string.Equals(item.Id, profile.DefaultModel,
                                                                            StringComparison.Ordinal));
        if (selectedModel is null || selectedModel.ContextWindowTokens <= 0)
            throw new
                ConfigException($"The selected model in user config '{configPath}' has no valid explicit context window. " +
                                "Run 'tinyharness model add <model-id> --context-window <tokens>'.");

        var worker                 = userConfig.Settings?.Worker;
        var reservedOutput         = WorkerExecutionLimits.DefaultReservedOutputTokens;
        var availableContextTokens = selectedModel.ContextWindowTokens - reservedOutput;
        if (availableContextTokens < 1)
            throw new
                ConfigException("The selected model's configured context window is too small for the worker's reserved output budget.");

        var maxContextTokensPerRequest = Value(worker?.MaxContextTokensPerRequest,
                                               Math.Min(availableContextTokens, 24_000),
                                               WorkerExecutionLimits.MaxCumulativeContextTokens,
                                               "maxContextTokensPerRequest");
        if (maxContextTokensPerRequest > availableContextTokens)
            throw new
                ConfigException($"settings.worker.maxContextTokensPerRequest must not exceed the selected model's context window minus {reservedOutput} reserved output tokens.");

        var workspaceSetting = worker?.WorkspaceRoot;
        var workspaceRoot =
            Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceSetting) ? baseDirectory : workspaceSetting,
                             baseDirectory);
        if (!Directory.Exists(workspaceRoot))
            throw new
                ConfigException($"The configured MCP worker workspace does not exist or is not a directory: {workspaceRoot}");

        var options = new WorkerExecutionOptions
        {
            Model = profile.DefaultModel,

            RunTimeout = TimeSpan.FromSeconds(Value(worker?.RunTimeoutSeconds,
                                                    WorkerExecutionLimits.DefaultRunTimeoutSeconds,
                                                    WorkerExecutionLimits.MaxRunTimeoutSeconds, "runTimeoutSeconds")),

            MaxAgentSteps = Value(worker?.MaxAgentSteps, WorkerExecutionLimits.DefaultMaxAgentSteps,
                                  WorkerExecutionLimits.MaxAgentSteps, "maxAgentSteps"),
            DefaultToolTimeoutSeconds = Value(worker?.DefaultToolTimeoutSeconds,
                                              WorkerExecutionLimits.DefaultToolTimeoutSeconds,
                                              WorkerExecutionLimits.MaxToolTimeoutSeconds, "defaultToolTimeoutSeconds"),
            MaxTaskPackageCharacters = Value(worker?.MaxTaskPackageCharacters,
                                             WorkerExecutionLimits.DefaultMaxTaskPackageCharacters,
                                             WorkerExecutionLimits.MaxTaskPackageCharacters,
                                             "maxTaskPackageCharacters"),
            MaxToolCalls = Value(worker?.MaxToolCalls, WorkerExecutionLimits.DefaultMaxToolCalls,
                                 WorkerExecutionLimits.MaxToolCalls, "maxToolCalls"),
            MaxToolOutputCharacters = Value(worker?.MaxToolOutputCharacters,
                                            WorkerExecutionLimits.DefaultMaxToolOutputCharacters,
                                            WorkerExecutionLimits.MaxToolOutputCharacters, "maxToolOutputCharacters"),
            MaxContextTokensPerRequest = maxContextTokensPerRequest,
            MaxCumulativeContextTokens = Value(worker?.MaxCumulativeContextTokens,
                                               Math.Min(WorkerExecutionLimits.DefaultMaxCumulativeContextTokens,
                                                        availableContextTokens),
                                               WorkerExecutionLimits.MaxCumulativeContextTokens,
                                               "maxCumulativeContextTokens"),
            MaxModelResponseCharacters = Value(worker?.MaxModelResponseCharacters,
                                               WorkerExecutionLimits.DefaultMaxModelResponseCharacters,
                                               WorkerExecutionLimits.MaxModelResponseCharacters,
                                               "maxModelResponseCharacters"),
        };

        var store = credentialStore ?? new WindowsCredentialStore();
        var apiKey = ApiKeyReader.Read(profile.ApiKeyEnvironmentVariable, profile.ApiKeyCredentialTarget, store,
                                       profile.Name);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new
                ConfigException($"No API key is available for the default profile in user config '{configPath}'. " +
                                $"Run 'tinyharness auth set {profile.Name}' or configure its API key environment variable.");

        return new McpWorkerHostConfiguration(profile.DefaultModel, profile.Endpoint,
                                              endpointUri.Scheme.ToUpperInvariant(), apiKey, workspaceRoot, configPath,
                                              options);
    }

    private static int Value(int? configured, int defaultValue, int maximum, string field)
    {
        var value = configured ?? defaultValue;
        if (value < 1 || value > maximum)
            throw new ConfigException($"settings.worker.{field} must be between 1 and {maximum}.");

        return value;
    }
}
