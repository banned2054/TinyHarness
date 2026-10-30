using System.Collections.Concurrent;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
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
///     <para>
///         Provides desktop text and history projections with redacted diagnostics, without raw protocol payloads or
///         opaque reasoning.
///     </para>
/// </summary>
public sealed record DesktopEvent(
    string                       Kind,
    string?                      RunId,
    string?                      Text            = null,
    SessionHistoryItem?          History         = null,
    DesktopWorkspaceRepairState? WorkspaceRepair = null);

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
    bool                  Reselect        = false)
{
    public string? ProjectId      { get; init; }
    public string? StoreDirectory { get; init; }
}

/// <summary>
///     <para>独立于 CLI 组装桌面宿主，复用 Core 的模型、权限、工具和唯一日志。</para>
///     <para>Composes desktop hosts independently of the CLI, reusing Core models, permissions, tools and journal.</para>
/// </summary>
public sealed class DesktopSessionFactory
{
    private readonly ICredentialStore                                       _credentials;
    private readonly Func<TinyHarnessConfig, string, IChatCompletionClient> _modelFactory;
    private readonly ConcurrentDictionary<string, string>                   _secrets = new(StringComparer.Ordinal);
    private readonly string?                                                _userConfigPath;

    /// <summary>
    ///     <para>允许离线验收注入模型与凭据存储；生产默认使用 Core 的正式客户端和系统凭据。</para>
    ///     <para>
    ///         Allows offline acceptance to inject models and credentials; production uses Core clients and system
    ///         credentials.
    ///     </para>
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
    ///     <para>用入口生命周期内全部已知密钥处理诊断。</para>
    ///     <para>Redacts diagnostics with every key known during this entry's lifetime.</para>
    /// </summary>
    public string Redact(string text) => new DisplaySecretRedactor(_secrets).Redact(text);

    /// <summary>
    ///     <para>只读解析草稿的生效模型配置；不读取密钥、不创建客户端或会话。</para>
    ///     <para>Resolves the effective draft model configuration without reading secrets or creating clients or sessions.</para>
    /// </summary>
    public Task<ConfigResolution>
        ResolveDefaultConfigAsync(string workspaceRoot, CancellationToken cancellationToken) =>
        ConfigResolver.ResolveAsync(null, cancellationToken, workspaceRoot, _userConfigPath);

    /// <summary>
    ///     <para>读取可信设置，建立新会话或严格恢复；失败释放所有已打开的资源，不发送模型请求。</para>
    ///     <para>
    ///         Reads trusted settings and creates or strictly resumes a session; failures release resources without model
    ///         calls.
    ///     </para>
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
        if (request.StoreDirectory is { } expectedDirectory &&
            !ProjectRoots.AreEqual(directory, Path.GetFullPath(expectedDirectory)))
            throw new InvalidOperationException("会话存储位置已改变，请刷新后重试。");
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

        SandboxDiagnosticLog.Event("session",
                                   $"workspace='{root}' identity='{access.Identity.FileIdentity}' allowChanges={request.AllowChanges} " +
                                   $"sandbox={(sandbox is null ? "disabled" : "enabled")} store='{directory}' user='{Environment.UserName}'");

        var permissions = new PermissionEngine(root, config.CommandRules, workspaceReadOnly : () => access.IsReadOnly);
        DesktopWorkspaceCoordinator? repair = null;
        var backend = sandbox?.Backend ?? new HostProcessBackend(secrets);
        if (sandbox is not null && user.Settings?.WindowsSandbox is { } sandboxSettings)
        {
            var grant = new ProcessWorkspaceAclGrant(
                                                     [
                                                         userPath, directory, sandbox.Components.SetupExecutablePath,
                                                         sandbox.Components.RunnerExecutablePath,
                                                         sandbox.Components.SandboxHome
                                                     ],
                                                     [root, .. sandboxSettings.AdditionalWriteRoots], sandboxSettings);
            var services = new DesktopWorkspaceRepairServices(async token =>
            {
                if (!await grant.CanChangePermissionsAsync(access.Identity, token).ConfigureAwait(false)) return false;
                await grant.PrepareWithoutElevationAsync(access.Identity, token).ConfigureAwait(false);
                return true;
            }, grant, async token =>
            {
                if (!await grant.IsReadyAsync(access.Identity, token).ConfigureAwait(false))
                    throw new InvalidOperationException("工作区沙箱权限仍未就绪。");
            }, () => grant.ValidatePreparationScope(access.Identity));
            repair = new DesktopWorkspaceCoordinator(access.Identity, services, state =>
                                                         events(new DesktopEvent("workspace-repair", null,
                                                                         WorkspaceRepair : state)));
            backend = new DesktopWorkspaceBackend(backend, repair);
        }

        var workspace = new Workspace(root);
        ITool[] toolSet =
        [
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
            new ApplyPatchTool(workspace),
            new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                          processBackend : backend,
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
                await SessionProjectBinding.BindRestoredAsync(directory, restored.Session, writer, cancellationToken)
                                           .ConfigureAwait(false);
                var     saved       = restored.Session.ModelSelection;
                // 旧日志允许没有选择记录；宿主接管前按初始模型定位，缺失强度保持未知。
                var     savedModel  = saved?.Model ?? restored.Session.InitialModel;
                var     savedEffort = saved?.ReasoningEffort;
                var     target      = FindTarget(user, saved, savedModel);
                string? reason      = null;
                if (!request.Reselect && target is { } resolved)
                    try
                    {
                        model = CreateClient(ConfigFor(config, resolved.Profile, resolved.Model),
                                             resolved.Profile.Name);
                    }
                    catch (Exception ex) when (ex is ConfigException or InvalidOperationException)
                    {
                        reason = Redact(ex.Message);
                    }

                // 客户端按当前目录构造，接管后先提交配置刷新；提交前不发送任何历史。
                host = new ChatSessionHost(restored.Session, restored.Context,
                                           options with
                                           {
                                               Model = savedModel.ModelId,
                                               Context = saved is null ? options.Context : saved.Context,
                                               ReasoningEffort = savedEffort
                                           }, model, tools, WorkspacePrompts.Current, permissions, approvals, sink,
                                           sessionWriter : writer, needsInspection : restored.NeedsInspection,
                                           instructionLoader : instructions, knownSecrets : secrets);
                if (request.Reselect || target is null)
                    await events(new DesktopEvent("error", null, "需要重新选择模型；历史已恢复。"))
                       .ConfigureAwait(false);
                else if (model is null)
                    await events(new DesktopEvent("error", null, "模型配置暂不可用，请检查配置后重试。" + reason))
                       .ConfigureAwait(false);
                else
                    try
                    {
                        await ChangeAsync(host, target.Value.Profile, target.Value.Model,
                                          CurrentEffort(savedEffort, target.Value.Model),
                                          host.Session.ModelSelection!.Revision, cancellationToken, model)
                           .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is ConfigException or InvalidOperationException)
                    {
                        await events(new DesktopEvent("error", null, "模型配置暂不可用，请检查配置后重试。" + Redact(ex.Message)))
                           .ConfigureAwait(false);
                    }
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
                var project = await SessionProjectBinding.GetProjectAsync(directory, root, cancellationToken)
                                                         .ConfigureAwait(false);
                if (request.ProjectId is { } expectedProject && expectedProject != project.Id)
                    throw new InvalidOperationException("项目归属已改变，请刷新项目后重试。");
                var session = new ChatSession(Guid.NewGuid().ToString("N"), root, identity, null, project.Id);
                var context = new ConversationContext(options.Context);
                context.Append(ChatMessage.System(WorkspacePrompts.Current));
                writer = new SessionStoreWriter(directory, session.Id, secrets);
                host = new ChatSessionHost(session, context, options, model, tools, WorkspacePrompts.Current,
                                           permissions, approvals, sink, sessionWriter : writer,
                                           instructionLoader : instructions, initialInstructions : initial,
                                           knownSecrets : secrets, credentialRoute : credentialRoute);
                await host.ChangeModelAsync(new SessionModelBinding(identity, model!, options.Context,
                                                                    options.ReasoningEffort,
                                                                    credentialRoute, profile?.Name,
                                                                    selected?.ReasoningEfforts, false),
                                            cancellationToken).ConfigureAwait(false);
            }

            return new DesktopSession(host, approvals,
                                      (profile, modelId, effort, revision, token) =>
                                          ChangeAsync(host, profile, modelId, effort, revision, token),
                                      token => RefreshCurrentAsync(host, token), repair);
        }
        catch
        {
            // 已构造宿主拥有 writer 与客户端；未构造时两者仍归本方法释放。
            if (host is not null)
            {
                host.Dispose();
            }
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
        return await ChangeAsync(host, profile, model, effort, revision, token).ConfigureAwait(false);
    }

    private async Task<SessionModelSelection> ChangeAsync(
        ChatSessionHost host,     UserProfile       profile, UserProfileModel       model, ModelReasoningEffort? effort,
        long            revision, CancellationToken token,   IChatCompletionClient? preparedClient = null)
    {
        if (effort is { } level && model.ReasoningEfforts?.Contains(level) != true)
            throw new InvalidOperationException("模型未声明该强度。");
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("请先配置供应商 Endpoint。");
        var current = host.Session.ModelSelection!;
        var context = (current.Context ?? new ContextOptions()) with
        {
            ContextWindowTokens = model.ContextWindowTokens
        };
        if (host.HasModelBinding                        &&
            current.Revision        == revision         &&
            current.Model.ModelId   == model.Id         &&
            current.Model.Endpoint  == profile.Endpoint &&
            current.Model.ChatApi   == profile.ChatApi  &&
            current.CredentialRoute == Route(profile)   &&
            current.ProfileHint     == profile.Name     &&
            current.Context         == context          &&
            (current.ReasoningCapabilities ?? []).SequenceEqual(model.ReasoningEfforts ?? []))
            return await host.ChangeReasoningEffortAsync(effort, token, model.ReasoningEfforts,
                                                         revision).ConfigureAwait(false);
        var client = preparedClient ??
                     CreateClient(ConfigFor(new TinyHarnessConfig { WorkspaceRoot = host.Session.WorkspaceRoot },
                                            profile, model), profile.Name);
        var candidate = new SessionModelBinding(new SessionModelIdentity(model.Id, profile.Endpoint, profile.ChatApi),
                                                client,
                                                context, effort, Route(profile), profile.Name, model.ReasoningEfforts,
                                                preparedClient is null);
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

    /// <summary>
    ///     <para>发送前按原供应商和模型刷新绑定；目标缺失时保留草稿并要求重选，刷新失败不发送。</para>
    ///     <para>
    ///         Refreshes the binding for the same provider and model before sending; missing targets require re-selection
    ///         and failed refreshes prevent sending.
    ///     </para>
    /// </summary>
    private async Task RefreshCurrentAsync(ChatSessionHost host, CancellationToken token)
    {
        var current = host.Session.ModelSelection!;
        var user = await UserConfigStore.LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), token)
                                        .ConfigureAwait(false);
        // 无目录归属的项目配置仍沿用原路径，不擅自绑定目录中的同名模型。
        if (current.CredentialRoute is null) return;
        if (current.ProfileHint is null && current.CredentialRoute.StartsWith('|')) return;
        var target = FindTarget(user, current, current.Model) ??
                     throw new InvalidOperationException("当前供应商或模型已不存在，请重新选择模型后发送。");
        await ChangeAsync(host, target.Profile, target.Model, CurrentEffort(current.ReasoningEffort, target.Model),
                          current.Revision, token).ConfigureAwait(false);
    }

    private static ModelReasoningEffort? CurrentEffort(ModelReasoningEffort? saved, UserProfileModel model) =>
        saved is null || model.ReasoningEfforts?.Contains(saved.Value) == true ? saved : model.DefaultReasoningEffort;

    /// <summary>
    ///     <para>按供应商名和模型 ID 定位当前目录项；无供应商信息的旧日志仅允许唯一身份匹配。</para>
    ///     <para>
    ///         Locates the current catalog entry by provider name and model ID; legacy logs without a provider require a
    ///         unique identity match.
    ///     </para>
    /// </summary>
    private static (UserProfile Profile, UserProfileModel Model)? FindTarget(
        UserConfig user, SessionModelSelection? saved, SessionModelIdentity identity)
    {
        var name = saved?.ProfileHint;
        if (name is null && saved?.CredentialRoute is { } route)
        {
            var separator           = route.IndexOf('|');
            if (separator > 0) name = route[..separator];
        }

        if (name is not null)
        {
            var profile = user.Profiles.SingleOrDefault(item => item.Name == name);
            var model   = profile?.Models.SingleOrDefault(item => item.Id == identity.ModelId);
            return profile is not null && model is not null ? (profile, model) : null;
        }

        var matches = user
                     .Profiles.SelectMany(profile => profile.Models.Select(model => (Profile : profile, Model : model)))
                     .Where(item => item.Model.Id         == identity.ModelId  &&
                                    item.Profile.Endpoint == identity.Endpoint &&
                                    item.Profile.ChatApi  == identity.ChatApi).ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
        var display = @event.Kind switch
        {
            HostEventKind.AssistantTextDelta when @event.Payload is string text =>
                new DesktopEvent("text", @event.RunId, text),
            HostEventKind.AssistantReasoningDelta when @event.Payload is string text =>
                new DesktopEvent("reasoning", @event.RunId, text),
            HostEventKind.HistoryItemCompleted when @event.Payload is SessionHistoryItem item =>
                new DesktopEvent("history", @event.RunId, History : item),
            HostEventKind.RunStarted => new DesktopEvent("state", @event.RunId, "运行中"),
            HostEventKind.RunControlChanged when @event.Payload is string state =>
                new DesktopEvent("state", @event.RunId, state),
            HostEventKind.ModelRequestStarted => new DesktopEvent("state", @event.RunId, "等待模型响应"),
            HostEventKind.ModelRequestWaiting => new DesktopEvent("state", @event.RunId, "等待模型响应"),
            HostEventKind.ToolStarted         => new DesktopEvent("state", @event.RunId, "工具执行中"),
            HostEventKind.RunError when @event.Payload is string error => new DesktopEvent("error", @event.RunId,
                     _diagnostics.Redact(error)),
            HostEventKind.RunFinished => new DesktopEvent("state", @event.RunId, "正在保存"),
            _                         => null
        };
        if (display is not null) await publish(display).ConfigureAwait(false);
    }
}
