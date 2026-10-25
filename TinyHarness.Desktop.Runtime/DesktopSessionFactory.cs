using System.Collections.Concurrent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Desktop.Runtime.Prompts;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
    ///     <para>提供桌面文本与历史投影，诊断脱敏，不暴露原始协议载荷或不透明推理。</para>
    ///     <para>Provides desktop text and history projections with redacted diagnostics, without raw protocol payloads or opaque reasoning.</para>
/// </summary>
public sealed record DesktopEvent(
    string              Kind,
    string?             RunId,
    string?             Text    = null,
    SessionHistoryItem? History = null);

/// <summary>
///     <para>一次打开请求；工作区及写入许可由人类界面显式选择，恢复不继承历史批准。</para>
///     <para>An opening request; workspace and write consent come from the human UI, and resume inherits no old approvals.</para>
/// </summary>
public sealed record DesktopSessionRequest(
    string                WorkspaceRoot,
    bool                  AllowChanges    = false,
    string?               SessionId       = null,
    string?               ProfileName     = null,
    string?               ModelId         = null,
    bool                  ShowReasoning   = false,
    ModelReasoningEffort? Effort          = null,
    bool                  EffortSpecified = false,
    bool                  Reselect        = false);

/// <summary>
///     <para>独立于 CLI 组装桌面宿主，复用 Core 的模型、权限、工具和唯一日志。</para>
///     <para>Composes desktop hosts independently of the CLI, reusing Core models, permissions, tools and journal.</para>
/// </summary>
public sealed class DesktopSessionFactory
{
    private readonly Func<TinyHarnessConfig, string, IChatCompletionClient> _modelFactory;
    private readonly ICredentialStore _credentials;
    private readonly string?          _userConfigPath;
    private readonly ConcurrentDictionary<string, string>                   _secrets = new(StringComparer.Ordinal);

    /// <summary>
    /// <para>用入口生命周期内全部已知密钥处理诊断。</para>
    /// <para>Redacts diagnostics with every key known during this entry's lifetime.</para>
    /// </summary>
    public string Redact(string text) => new DisplaySecretRedactor(_secrets).Redact(text);

    /// <summary>
    ///     <para>允许离线验收注入模型与凭据存储；生产默认使用 Core 的正式客户端和系统凭据。</para>
    ///     <para>Allows offline acceptance to inject models and credentials; production uses Core clients and system credentials.</para>
    /// </summary>
    public DesktopSessionFactory(Func<TinyHarnessConfig, string, IChatCompletionClient>? modelFactory = null,
                                 ICredentialStore? credentials = null, string? userConfigPath = null)
    {
        _modelFactory = modelFactory ?? ((config, key) =>
            ModelClientFactory.Create(config.Model, config.Endpoint, key, config.ChatApi));
        _credentials    = credentials ?? new WindowsCredentialStore();
        _userConfigPath = userConfigPath;
    }

    /// <summary>
    ///     <para>读取可信设置，建立新会话或严格恢复；失败释放所有已打开的资源，不发送模型请求。</para>
    ///     <para>Reads trusted settings and creates or strictly resumes a session; failures release resources without model calls.</para>
    /// </summary>
    public async Task<DesktopSession> OpenAsync(
        DesktopSessionRequest             request,         Func<DesktopEvent, ValueTask> events,
        Func<DesktopApproval?, ValueTask> approvalChanged, CancellationToken             cancellationToken)
    {
        var root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var userPath = _userConfigPath ?? UserConfigStore.DefaultFilePath();
        var user     = await UserConfigStore.LoadAsync(userPath, cancellationToken).ConfigureAwait(false);
        // 恢复和显式选择不依赖全局默认目标可用；项目执行策略仍按原优先级读取。
        var resolution = request.SessionId is null && request.ProfileName is null
            ? await ConfigResolver.ResolveAsync(null, cancellationToken, root, userPath).ConfigureAwait(false)
            : null;
        var projectPath = Path.Combine(root, ConfigResolver.ProjectConfigFileName);
        var config = (resolution?.Config ?? (File.Exists(projectPath)
                ? await ConfigurationLoader.LoadAsync(projectPath, cancellationToken).ConfigureAwait(false)
                : new TinyHarnessConfig
                {
                    MaxAgentSteps             = user.Settings?.MaxAgentSteps ?? 0,
                    ReservedOutputTokens      = user.Settings?.ReservedOutputTokens ?? 8000,
                    CompactionThreshold       = user.Settings?.CompactionThreshold ?? 0,
                    DefaultToolTimeoutSeconds = user.Settings?.DefaultToolTimeoutSeconds ?? 120,
                    CommandRules              = user.Settings?.CommandRules ?? new TinyHarnessConfig().CommandRules
                })) with
            {
                WorkspaceRoot = root
            };
        var directory = SessionStorePaths.ResolveProtected(user.Settings?.SessionStoreDirectory, root);
        var secrets   = _secrets;
        var redactor  = new DisplaySecretRedactor(secrets);
        var approvals = new DesktopApprovalProvider(approvalChanged, redactor.Redact);
        var sink      = new DesktopEventSink(events, secrets, request.ShowReasoning);
        var access    = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(root));
        access.SetReadOnly(!request.AllowChanges);
        WindowsSandboxExecution? sandbox = null;
        if (request.AllowChanges && user.Settings?.WindowsSandbox is { Enabled: true } settings)
        {
            // 会话存储不能落入任一可信写根；此处不进行 provisioning 或机器权限变更。
            foreach (var writeRoot in settings.AdditionalWriteRoots)
                SessionStorePaths.EnsureOutsideWorkspace(directory, writeRoot, "settings.sessionStoreDirectory");
            sandbox = WindowsSandboxComposer.Compose(settings, root, secrets);
        }

        var permissions = new PermissionEngine(root, config.CommandRules, workspaceReadOnly : () => access.IsReadOnly);
        var workspace   = new Workspace(root);
        ITool[] toolSet =
        [
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
            new ApplyPatchTool(workspace),
            new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                          processBackend : sandbox?.Backend ?? (IProcessExecutionBackend)new HostProcessBackend(secrets),
                          executionPolicy : sandbox?.ExecutionPolicy)
        ];
        var tools = new ToolRegistry(toolSet.Select(tool => access.Guard(() => tool)));
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            EnableWorkItems           = true,
            Budget                    = user.Settings?.RunBudget ?? new RunBudgetLimits(),
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens       = config.ContextWindowTokens,
                ReservedOutputTokens      = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold
            }
        };
        var identity     = new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi);
        var instructions = new ProjectInstructionLoader(workspace);

        IChatCompletionClient? model  = null;
        SessionStoreWriter?    writer = null;
        ChatSessionHost?       host   = null;
        try
        {
            if (request.SessionId is { } sessionId)
            {
                writer = await SessionStoreWriter.OpenExistingAsync(directory, sessionId, secrets, cancellationToken)
                                                 .ConfigureAwait(false);
                var snapshot = writer.LoadedSnapshot!;
                var restored = SessionResume.Restore(snapshot, root, WorkspacePrompts.Current, options.Context);
                var saved    = restored.Session.ModelSelection;
                // 旧日志允许没有选择记录；宿主接管前按初始模型定位，缺失强度保持未知。
                var savedModel  = saved?.Model ?? restored.Session.InitialModel;
                var savedEffort = saved?.ReasoningEffort;
                var matches = user
                             .Profiles.SelectMany(profile =>
                                                      profile.Models.Select(item => (Profile : profile, Model : item)))
                             .Where(item => item.Model.Id         == savedModel.ModelId  &&
                                            item.Profile.Endpoint == savedModel.Endpoint &&
                                            item.Profile.ChatApi  == savedModel.ChatApi  &&
                                            (saved?.CredentialRoute is null ||
                                             Route(item.Profile) == saved.CredentialRoute)).ToArray();
                var target = matches.Length == 1 ? matches[0] : default;
                var available = target.Profile is not null &&
                                (savedEffort is null ||
                                 target.Model.ReasoningEfforts?.Contains(savedEffort.Value) == true);
                string? reason = null;
                if (available && !request.Reselect)
                {
                    try { model = CreateClient(ConfigFor(config, target.Profile!, target.Model!), target.Profile!.Name);
                    }
                    catch (Exception ex) when (ex is Core.Exceptions.ConfigException or InvalidOperationException)
                    {
                        reason = Redact(ex.Message);
                    }
                }
                host = new ChatSessionHost(restored.Session, restored.Context,
                                           options with
                                           {
                                               Model = savedModel.ModelId,
                                               Context = saved is null ? options.Context : saved.Context,
                                               ReasoningEffort = savedEffort
                                           }, model, tools, WorkspacePrompts.Current, permissions, approvals, sink,
                                           sessionWriter : writer, needsInspection : restored.NeedsInspection,
                                           instructionLoader : instructions, knownSecrets : secrets);
                if (model is null)
                    await events(new DesktopEvent("error", null, "需要重新选择模型；历史已恢复。" + reason)).ConfigureAwait(false);
                else if (saved is null)
                {
                    // 首次绑定固化当前目录的窗口与能力；旧 run 的强度和选择归属仍保持未知。
                    var context = options.Context! with { ContextWindowTokens = target.Model!.ContextWindowTokens };
                    await host.ChangeModelAsync(new SessionModelBinding(savedModel, model, context, savedEffort,
                                                                        Route(target.Profile!), target.Profile!.Name,
                                                                        target.Model.ReasoningEfforts, ownsClient : false),
                                                cancellationToken).ConfigureAwait(false);
                }
                else
                    await host.EnsureCredentialRouteAsync(Route(target.Profile!), cancellationToken)
                              .ConfigureAwait(false);
            }
            else
            {
                config = SelectConfig(config, user, request);
                model  = CreateClient(config, request.ProfileName ?? resolution?.ProfileName);
                options = options with
                {
                    Model = config.Model,
                    Context = options.Context! with { ContextWindowTokens = config.ContextWindowTokens }
                };
                identity = new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi);
                var profile =
                    user.Profiles.SingleOrDefault(item => item.Name ==
                                                          (request.ProfileName ?? resolution?.ProfileName));
                var selected = profile?.Models.SingleOrDefault(item => item.Id == config.Model);
                options = options with
                {
                    ReasoningEffort = request.EffortSpecified ? request.Effort : selected?.DefaultReasoningEffort
                };
                if (options.ReasoningEffort is { } level && selected?.ReasoningEfforts?.Contains(level) != true)
                    throw new InvalidOperationException("所选模型未声明该推理强度。");
                var credentialRoute =
                    CredentialRoute.Describe(profile?.Name, config.ApiKeyEnvironmentVariable,
                                             config.ApiKeyCredentialTarget);
                var initial = await instructions.LoadAsync(null, [], cancellationToken).ConfigureAwait(false);
                var session = new ChatSession(Guid.NewGuid().ToString("N"), root, identity);
                var context = new ConversationContext(options.Context);
                context.Append(Core.Models.ChatCompletions.ChatMessage.System(WorkspacePrompts.Current));
                writer = new SessionStoreWriter(directory, session.Id, secrets);
                host = new ChatSessionHost(session, context, options, model, tools, WorkspacePrompts.Current,
                                           permissions, approvals, sink, sessionWriter : writer,
                                           instructionLoader : instructions, initialInstructions : initial,
                                           knownSecrets : secrets, credentialRoute : credentialRoute);
                await host.ChangeModelAsync(new SessionModelBinding(identity, model!, options.Context,
                                                                    options.ReasoningEffort,
                                                                    credentialRoute, profile?.Name,
                                                                    selected?.ReasoningEfforts, ownsClient: false),
                                            cancellationToken).ConfigureAwait(false);
            }

            return new DesktopSession(host, approvals,
                                      (profile, modelId, effort, revision, token) =>
                                          ChangeAsync(host, profile, modelId, effort, revision, token),
                                      token => ValidateCurrentAsync(host, token));
        }
        catch
        {
            // 已构造宿主拥有 writer 与客户端；未构造时两者仍归本方法释放。
            if (host is not null) host.Dispose();
            else
            {
                writer?.Dispose();
                (model as IDisposable)?.Dispose();
            }

            throw;
        }
    }

    private IChatCompletionClient CreateClient(TinyHarnessConfig config, string? profileName)
    {
        var key = ApiKeyReader.Read(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget, _credentials,
                                    profileName)
               ?? throw new InvalidOperationException("没有可用的模型凭据，请重新选择模型或配置凭据引用。");
        _secrets[Guid.NewGuid().ToString("N")] = key;
        if (config.ApiKeyEnvironmentVariable.Length > 0) _secrets[config.ApiKeyEnvironmentVariable] = key;
        return _modelFactory(config, key);
    }

    private static string Route(UserProfile profile) =>
        CredentialRoute.Describe(profile.Name, profile.ApiKeyEnvironmentVariable, profile.ApiKeyCredentialTarget);

    private static TinyHarnessConfig ConfigFor(TinyHarnessConfig config, UserProfile profile, UserProfileModel model) =>
        config with
        {
            Model = model.Id, Endpoint = profile.Endpoint, ChatApi = profile.ChatApi,
            ContextWindowTokens = model.ContextWindowTokens,
            ApiKeyCredentialTarget = profile.ApiKeyCredentialTarget,
            ApiKeyEnvironmentVariable = profile.ApiKeyEnvironmentVariable
        };

    private async Task<SessionModelSelection> ChangeAsync(ChatSessionHost host, string profileName, string modelId,
                                                          ModelReasoningEffort? effort, long revision,
                                                          CancellationToken token)
    {
        var user = await UserConfigStore.LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), token)
                                        .ConfigureAwait(false);
        var profile = user.Profiles.SingleOrDefault(item => item.Name == profileName) ??
                      throw new InvalidOperationException("供应商已不存在。");
        var model = profile.Models.SingleOrDefault(item => item.Id == modelId) ??
                    throw new InvalidOperationException("模型已不存在。");
        if (effort is { } level && model.ReasoningEfforts?.Contains(level) != true)
            throw new InvalidOperationException("模型未声明该强度。");
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("请先配置供应商 Endpoint。");
        var current = host.Session.ModelSelection!;
        var context = (current.Context ?? new ContextOptions()) with
        {
            ContextWindowTokens = model.ContextWindowTokens
        };
        if (host.HasModelBinding &&
            current.Revision == revision &&
            current.Model.ModelId == model.Id &&
            current.Model.Endpoint == profile.Endpoint &&
            current.Model.ChatApi == profile.ChatApi &&
            current.CredentialRoute == Route(profile) &&
            current.Context == context &&
            (current.ReasoningCapabilities ?? []).SequenceEqual(model.ReasoningEfforts ?? []))
            return await host.ChangeReasoningEffortAsync(effort, token, model.ReasoningEfforts,
                                                         expectedRevision: revision).ConfigureAwait(false);
        var client =
            CreateClient(ConfigFor(new TinyHarnessConfig { WorkspaceRoot = host.Session.WorkspaceRoot }, profile,
                                   model), profile.Name);
        var candidate = new SessionModelBinding(new SessionModelIdentity(model.Id, profile.Endpoint, profile.ChatApi),
                                                client,
                                                context, effort, Route(profile), profile.Name, model.ReasoningEfforts);
        try
        {
            return await host.ChangeModelAsync(candidate, token, revision).ConfigureAwait(false);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private async Task ValidateCurrentAsync(ChatSessionHost host, CancellationToken token)
    {
        var current = host.Session.ModelSelection!;
        var user = await UserConfigStore.LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), token)
                                        .ConfigureAwait(false);
        // 无目录路由的项目配置仍沿用原路径；目录绑定在发送前检查漂移，不自动转发历史。
        if (current.CredentialRoute is null) return;
        var profile = user.Profiles.SingleOrDefault(item => current.ProfileHint is { } name
                                                        ? item.Name == name
                                                        : Route(item) == current.CredentialRoute);
        if (profile is null && current.ProfileHint is null) return;
        var model = profile?.Models.SingleOrDefault(item => item.Id == current.Model.ModelId);
        if (profile is null ||
            model is null ||
            Route(profile) != current.CredentialRoute ||
            profile.Endpoint != current.Model.Endpoint ||
            profile.ChatApi != current.Model.ChatApi ||
            current.Context?.ContextWindowTokens != model.ContextWindowTokens ||
            !(current.ReasoningCapabilities ?? []).SequenceEqual(model.ReasoningEfforts ?? []))
            throw new InvalidOperationException("模型配置已变化，请显式重新选择模型后发送。");
    }

    private static TinyHarnessConfig SelectConfig(TinyHarnessConfig     config, UserConfig user,
                                                  DesktopSessionRequest request)
    {
        if (request.ProfileName is null) return config;
        var profile = user.Profiles.SingleOrDefault(profile => profile.Name == request.ProfileName)
                   ?? throw new InvalidOperationException("所选提供方已不在用户配置中。");
        var selected = profile.Models.SingleOrDefault(model => model.Id == request.ModelId)
                    ?? throw new InvalidOperationException("所选模型已不在用户配置中。");
        return ConfigFor(config, profile, selected);
    }
}

internal sealed class DesktopEventSink(
    Func<DesktopEvent, ValueTask>       publish,
    IReadOnlyDictionary<string, string> secrets,
    bool                                includeReasoning) : IHostEventSink
{
    private readonly DisplaySecretRedactor _diagnostics = new(secrets);
    public           bool                  IncludeReasoningText => includeReasoning;

    public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        DesktopEvent? display = @event.Kind switch
        {
            HostEventKind.AssistantTextDelta when @event.Payload is string text =>
                new("text", @event.RunId, text),
            HostEventKind.AssistantReasoningDelta when @event.Payload is string text =>
                new("reasoning", @event.RunId, text),
            HostEventKind.HistoryItemCompleted when @event.Payload is SessionHistoryItem item =>
                new("history", @event.RunId, History : item),
            HostEventKind.RunStarted => new("state", @event.RunId, "运行中"),
            HostEventKind.RunControlChanged when @event.Payload is string state =>
                new("state", @event.RunId, state),
            HostEventKind.ModelRequestStarted => new("state", @event.RunId, "等待模型响应"),
            HostEventKind.ModelRequestWaiting => new("state", @event.RunId, "等待模型响应"),
            HostEventKind.ToolStarted         => new("state", @event.RunId, "工具执行中"),
            HostEventKind.RunError when @event.Payload is string error => new("error", @event.RunId,
                _diagnostics.Redact(error)),
            HostEventKind.RunFinished => new("state", @event.RunId, "正在保存"),
            _                         => null
        };
        if (display is not null) await publish(display).ConfigureAwait(false);
    }
}
