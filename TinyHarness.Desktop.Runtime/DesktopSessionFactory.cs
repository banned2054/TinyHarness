using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
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
///     <para>一次打开请求；新会话采用用户选择，恢复已有会话模式但不继承临时批准。</para>
///     <para>
///         An opening request; new sessions use the user's choice, while resume restores the mode without temporary
///         approvals.
///     </para>
/// </summary>
public sealed record DesktopSessionRequest(
    string                WorkspaceRoot,
    SessionApprovalMode?  PermissionMode  = null,
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

    public SessionProjectAssignmentState ProjectAssignmentState { get; init; } =
        SessionProjectAssignmentState.PendingInitialization;
}

/// <summary>
///     <para>独立于 CLI 组装桌面宿主，复用 Core 的模型、权限、工具和唯一日志。</para>
///     <para>Composes desktop hosts independently of the CLI, reusing Core models, permissions, tools and journal.</para>
/// </summary>
public sealed partial class DesktopSessionFactory
{
    private readonly ICredentialStore                                            _credentials;
    private readonly Func<TinyHarnessConfig, string, IChatCompletionClient>?     _modelFactory;
    private readonly ConditionalWeakTable<ChatSessionHost, ResolvedNetworkRoute> _networkRoutes = new();
    private readonly ConcurrentDictionary<string, string>                        _secrets = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, SessionIndexUpdater> _sessionIndexUpdaters =
        new(StringComparer.Ordinal);

    private readonly string? _userConfigPath;

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
        _modelFactory   = modelFactory;
        _credentials    = credentials ?? new WindowsCredentialStore();
        _userConfigPath = userConfigPath;
    }

    /// <summary>
    ///     <para>用入口生命周期内全部已知密钥处理诊断。</para>
    ///     <para>Redacts diagnostics with every key known during this entry's lifetime.</para>
    /// </summary>
    public string Redact(string text) => new DisplaySecretRedactor(_secrets).Redact(text);

    /// <summary>
    ///     <para>只读解析草稿的生效模型配置（仅来自全局用户配置，不读取工作目录配置）；不读取密钥、不创建客户端或会话。</para>
    ///     <para>
    ///         Resolves the effective draft model configuration from the global user config only (never a working
    ///         directory config), without reading secrets or creating clients or sessions.
    ///     </para>
    /// </summary>
    public Task<ConfigResolution>
        ResolveDefaultConfigAsync(CancellationToken cancellationToken) =>
        ConfigResolver.ResolveAsync(null, cancellationToken, _userConfigPath);

    /// <summary>
    ///     <para>只读加载可信用户配置供迁移说明使用；不读取凭据、不创建宿主或模型客户端。</para>
    ///     <para>
    ///         Loads trusted user configuration for migration notices without reading credentials or creating hosts or model
    ///         clients.
    ///     </para>
    /// </summary>
    public Task<UserConfig> LoadUserConfigAsync(CancellationToken cancellationToken) =>
        UserConfigStore.LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), cancellationToken);

    /// <summary>
    ///     <para>读取可信设置，建立新会话或严格恢复；失败释放所有已打开的资源，不发送模型请求。</para>
    ///     <para>
    ///         Reads trusted settings and creates or strictly resumes a session; failures release resources without model
    ///         calls.
    ///     </para>
    ///     <para>恢复按最后选择绑定当前目录目标，已保存强度失效时改用模型默认档；目标缺失时保持未绑定并等待显式选择，不在对话中追加错误。</para>
    ///     <para>
    ///         Resume binds the last selection against the current catalog and adopts the model's default effort when
    ///         the saved one is unsupported; a missing target stays unbound and awaits explicit selection without
    ///         appending errors to the conversation.
    ///     </para>
    /// </summary>
    public async Task<DesktopSession> OpenAsync(
        DesktopSessionRequest             request,         Func<DesktopEvent, ValueTask> events,
        Func<DesktopApproval?, ValueTask> approvalChanged, CancellationToken             cancellationToken)
    {
        if (!Enum.IsDefined(request.ProjectAssignmentState))
            throw new ArgumentOutOfRangeException(nameof(request), "The project-assignment state is invalid.");
        var root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var userPath = _userConfigPath ?? UserConfigStore.DefaultFilePath();
        var user     = await UserConfigStore.LoadAsync(userPath, cancellationToken).ConfigureAwait(false);
        EnsurePermissionConfigConfirmed(user);
        // 常设信任三态只来自可信用户设置与显式确认记录；未确认时组合与引擎都按逐次审批消费。
        var sandboxSettings        = user.Settings?.WindowsSandbox;
        var standingTrust          = StandingTrustEvaluation.Evaluate(sandboxSettings, user.Settings?.Permissions);
        // 组合实际消费的设置决定常设网络是否启用：与传给沙箱组合器的 EffectiveSandboxSettings
        // 同源同值；常设网络 restricted 时网络申请走逐次审批，写根确认不得复用为网络免审批。
        var compositionSettings    = StandingTrustEvaluation.EffectiveSandboxSettings(sandboxSettings, standingTrust);
        var standingNetworkEnabled = compositionSettings?.NetworkPolicy == SandboxNetworkPolicy.Enabled;
        // 恢复和显式选择不依赖全局默认目标可用；配置一律来自可信用户设置，不读取工作目录的 tinyharness.json。
        var resolution = request.SessionId is null && request.ProfileName is null
            ? await ConfigResolver.ResolveAsync(null, cancellationToken, userPath).ConfigureAwait(false)
            : null;
        var config = (resolution?.Config ?? new TinyHarnessConfig
            {
                MaxAgentSteps        = user.Settings?.MaxAgentSteps        ?? 0,
                ReservedOutputTokens = user.Settings?.ReservedOutputTokens ?? 8000,
                CompactionThreshold  = user.Settings?.CompactionThreshold  ?? 0,
                DefaultToolTimeoutSeconds =
                    user.Settings is { } userSettings ? userSettings.DefaultToolTimeoutSeconds : 120,
                CommandRules = user.Settings?.CommandRules ?? new TinyHarnessConfig().CommandRules
            }) with
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
        // 用户模式由权限引擎审批；硬只读仍专用于未信任工作区和 worker。
        access.SetReadOnly(false);
        var                       sandboxConfigured = sandboxSettings is { Enabled: true };
        WindowsSandboxComponents? sandboxComponents = null;
        // 共享的 CLI workspace-trust.json（B7 两端超集）：OS deny 仅在文件已存在时配对
        // （helper 会把缺失条目物化为目录，绝不能让信任文件被物化）；应用层保护按路径形态恒生效。
        var                       trustPath = Path.Combine(Path.GetDirectoryName(userPath)!, "workspace-trust.json");
        // C1 权限系统实例（实际路径）：组合期写根重叠校验与 grant 校验共用，底层不重新猜默认路径。
        var                       permissionSystemPaths = new List<string> { userPath, trustPath, directory };
        // 基线广泛读保护的 C1 宿主密钥路径：组合与授权指纹共用同一列表，守卫重算同一 deny 集。
        var                       baselineProtectedPaths = new List<string> { userPath };
        if (sandboxSettings is { Enabled: true } settings)
        {
            // 只派生应用层保护路径；组件检查、目录创建与敏感名扫描延迟到首次沙箱工具准备。
            if (File.Exists(trustPath)) baselineProtectedPaths.Add(trustPath);
            sandboxComponents = new WindowsSandboxComponents
            {
                SetupExecutablePath  = settings.SetupExecutablePath.Trim(),
                RunnerExecutablePath = settings.RunnerExecutablePath.Trim(),
                SandboxHome =
                    WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome, settings.AllowTcpLoopback),
                AllowTcpLoopback = settings.AllowTcpLoopback
            };
        }

        SandboxDiagnosticLog.Event("session",
                                   $"workspace='{root}' identity='{access.Identity.FileIdentity}' requestedMode={request.PermissionMode} " +
                                   $"sandbox={(sandboxConfigured ? "deferred" : "disabled")} store='{directory}' user='{Environment.UserName}'");

        // 活动授权登记表：引擎与 Shell 工具共享同一实例，派发复核与快照聚合看到同一状态。
        var grants                = new GrantRegistry();
        // 文件工具与 Shell 的统一资源访问策略：权限系统保护路径（用户配置、共享 trust.json、
        // 会话存储与沙箱组件）与可信附加根集中分类；保护根集合与 CLI 取超集（B7），
        // trust.json 尚未由 CLI 创建时按路径形态保护（将来落盘即受保护）。
        // 沙箱 home 收缩为敏感子路径（凭据目录、deny 簿记、授权映射与执行组件目录）：默认
        // tmp/tools/nuget-cache 运行时根物理位于 home 下，整 home 入 C1 会把运行时根恒判保护类；
        // 组合期完整性保护仍以整 home 为准（另一层，不受此影响）。
        var permissionSystemRoots = new List<string> { userPath, trustPath, directory };
        if (sandboxComponents is not null)
        {
            permissionSystemRoots.Add(sandboxComponents.SetupExecutablePath);
            permissionSystemRoots.Add(sandboxComponents.RunnerExecutablePath);
            permissionSystemRoots.AddRange(sandboxComponents.ProtectedHomeSubpaths);
        }

        // 应用层分类只消费可信配置；沙箱工具在冻结计划前必须完成同一配置的实际组合。
        var resourcePolicy = new ResourceAccessPolicy(root,
                                                      user.Settings?.WindowsSandbox?.AdditionalReadRoots,
                                                      user.Settings?.WindowsSandbox?.AdditionalWriteRoots,
                                                      permissionSystemRoots : permissionSystemRoots,
                                                      sandboxRuntimeRoots : sandboxConfigured
                                                          ? WindowsSandboxComposer.DeriveRuntimeWriteRoots(
                                                           user.Settings?.WindowsSandbox)
                                                          : null,
                                                      standingTrust : standingTrust);
        // 组合授权指纹守卫：捕获组合实际消费的可信配置指纹，并注入同一函数的取值器——
        // 每次发送/派发前重读该配置重算，失配或取值失败即撤销授权并闩死会话（快照失效机制，
        // 独立于审批模式切换与 NeedsInspection）。
        var compositionGuard = new PermissionCompositionGuard(
                                                              PermissionCompositionFingerprint.Compute(sandboxSettings,
                                                                  user.Settings?.Permissions,
                                                                  user.Settings?.Network, root,
                                                                  sandboxConfigured,
                                                                  baselineProtectedPaths,
                                                                  user.Settings?.SessionStoreDirectory),
                                                              PermissionCompositionFingerprint.CreateEvaluator(userPath,
                                                                  root, sandboxConfigured,
                                                                  baselineProtectedPaths,
                                                                  true));
        // 常设信任只来自可信用户设置与显式确认记录：Confirmed 之外联网与附加写根写一律逐次审批；
        // 常设网络 restricted 时网络申请恒逐次审批，写根确认不复用为网络免审批。
        // 启用沙箱时准备阶段先组合成功才产出可授权的冻结计划；禁用时拒绝沙箱进程执行。
        var permissions = new PermissionEngine(root,
                                               workspaceReadOnly : () => access.IsReadOnly,
                                               standingTrust : standingTrust,
                                               sandboxExecutionAvailable : sandboxConfigured,
                                               standingNetworkEnabled : standingNetworkEnabled,
                                               resourcePolicy : resourcePolicy,
                                               grantRegistry : grants,
                                               frozenScopeReader : FrozenCapabilityScopeReader.FromPreparation);
        DesktopWorkspaceCoordinator? repair = null;
        if (sandboxComponents is not null && sandboxSettings is { } aclSandboxSettings)
        {
            var grant = new ProcessWorkspaceAclGrant(
                                                     [
                                                         userPath, directory, sandboxComponents.SetupExecutablePath,
                                                         sandboxComponents.RunnerExecutablePath,
                                                         sandboxComponents.SandboxHome
                                                     ],
                                                     [root, .. aclSandboxSettings.AdditionalWriteRoots],
                                                     aclSandboxSettings);
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
        }

        var              workspace      = new Workspace(root, true);
        ChatSessionHost? permissionHost = null;
        var routedApprovals = new DesktopAutoApprovalProvider(approvals, () => permissions.ApprovalMode,
                                                              token => CreateReviewerAsync(permissionHost!, token),
                                                              () => ReviewContext(permissionHost!, permissions),
                                                              text => events(new DesktopEvent("state", null, text)),
                                                              () => TimeSpan.FromSeconds(permissionHost!
                                                                 .EffectiveModelTimeouts
                                                                 .ApprovalTimeoutSeconds));
        // 定义可立即发给模型；真正的沙箱、能力快照和执行身份在首次 workspace Shell 准备时一起冻结。
        ITool restrictedShell = sandboxConfigured
            ? new DeferredShellTool(new ShellTool(workspace).Definition, token =>
            {
                foreach (var writeRoot in sandboxSettings!.AdditionalWriteRoots)
                    SessionStorePaths.EnsureOutsideWorkspace(directory, writeRoot, "settings.sessionStoreDirectory");
                var baseline = BaselineReadProtectionOverride is { } planner
                    ? planner(token)
                    : WindowsSandboxComposer.PlanBaselineReadProtection(baselineProtectedPaths, directory,
                                                                        sandboxSettings, null, token);
                token.ThrowIfCancellationRequested();
                var sandbox = WindowsSandboxComposer.Compose(compositionSettings ?? sandboxSettings, root, secrets,
                                                             user.Settings?.Network, baseline, permissionSystemPaths);
                token.ThrowIfCancellationRequested();
                var backend = repair is null ? sandbox.Backend : new DesktopWorkspaceBackend(sandbox.Backend, repair);
                return CreateWorkspaceShellTool(workspace, config.DefaultToolTimeoutSeconds, backend, sandbox, grants,
                                                permissionSystemPaths);
            })
            : CreateWorkspaceShellTool(workspace, config.DefaultToolTimeoutSeconds, new HostProcessBackend(secrets),
                                       null, grants, permissionSystemPaths);
        var hostShell = new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                                      executionPolicy : ProcessExecutionPolicy.Host());
        ITool[] toolSet =
        [
            new ListFilesTool(workspace, resourcePolicy : resourcePolicy),
            new SearchTextTool(workspace, resourcePolicy : resourcePolicy),
            new ReadFileTool(workspace, resourcePolicy : resourcePolicy),
            new ApplyPatchTool(workspace, resourcePolicy),
            // 路由只按 executionScope：host 是显式 Level 3 申请，权限模式不再参与工具路由。
            new PermissionShellTool(restrictedShell, hostShell)
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
        var instructions = new ProjectInstructionLoader(new Workspace(root));

        IChatCompletionClient? model  = null;
        SessionStoreWriter?    writer = null;
        ChatSessionHost?       host   = null;
        try
        {
            if (request.SessionId is { } sessionId)
            {
                writer = await SessionStoreWriter.OpenExistingAsync(directory, sessionId, secrets, cancellationToken,
                                                                    SessionIndexUpdaterFor(directory)
                                                                       .HandleBatchCommittedAsync)
                                                 .ConfigureAwait(false);
                var snapshot = writer.LoadedSnapshot!;
                // 会话沿用保存的系统提示；应用升级不改变历史指令，当前权限由执行引擎独立约束。
                var systemPrompt = snapshot.Conversation?.Messages.FirstOrDefault()?.Content ??
                                   WorkspacePrompts.Current;
                var restored = SessionResume.Restore(snapshot, root, systemPrompt, options.Context);
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
                                             resolved.Profile, user.Settings?.Network,
                                             user.Settings?.ModelTimeouts);
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
                                           }, model, tools, systemPrompt, permissions, routedApprovals,
                                           sink,
                                           sessionWriter : writer, needsInspection : restored.NeedsInspection,
                                           instructionLoader : instructions, knownSecrets : secrets,
                                           modelTimeouts : user.Settings?.ModelTimeouts,
                                           compositionGuard : compositionGuard);
                // 目标缺失或显式重选时保持未绑定，历史仍可浏览；缺失由选择器状态提示，不向对话追加错误。
                if (!request.Reselect && target is not null)
                {
                    if (model is null)
                        await events(new DesktopEvent("error", null, "模型配置暂不可用，请检查配置后重试。" + reason))
                           .ConfigureAwait(false);
                    else
                        try
                        {
                            await ChangeAsync(host, target.Value.Profile, target.Value.Model,
                                              CurrentEffort(savedEffort, target.Value.Model),
                                              host.Session.ModelSelection!.Revision, cancellationToken, model,
                                              user.Settings?.Network, user.Settings?.ModelTimeouts)
                               .ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is ConfigException or InvalidOperationException)
                        {
                            await events(new DesktopEvent("error", null,
                                                          "模型配置暂不可用，请检查配置后重试。" + Redact(ex.Message)))
                               .ConfigureAwait(false);
                        }
                }
            }
            else
            {
                config = SelectConfig(config, user, request);
                var profileName = request.ProfileName ?? resolution?.ProfileName;
                var profile     = user.Profiles.SingleOrDefault(item => item.Name == profileName);
                model = CreateClient(config, profile, user.Settings?.Network, user.Settings?.ModelTimeouts);
                options = options with
                {
                    Model = config.Model,
                    Context = options.Context! with { ContextWindowTokens = config.ContextWindowTokens }
                };
                identity = new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi);
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
                var      networkRoute = NetworkRouteResolver.Resolve(user.Settings?.Network, profile?.Network);
                var      initial      = await instructions.LoadAsync(null, [], cancellationToken).ConfigureAwait(false);
                Project? project      = null;
                if (request.ProjectAssignmentState != SessionProjectAssignmentState.NoProject)
                {
                    project = await SessionProjectBinding.GetProjectAsync(directory, root, cancellationToken)
                                                         .ConfigureAwait(false);
                    if (request.ProjectId is { } expectedProject && expectedProject != project.Id)
                        throw new InvalidOperationException("项目归属已改变，请刷新项目后重试。");
                }
                else if (request.ProjectId is not null)
                {
                    throw new InvalidOperationException("无项目会话不能同时携带项目 ID。");
                }

                var assignmentState = project is null
                    ? SessionProjectAssignmentState.NoProject
                    : SessionProjectAssignmentState.Project;
                var session = new ChatSession(Guid.NewGuid().ToString("N"), root, identity, null, project?.Id,
                                              assignmentState);
                var context = new ConversationContext(options.Context);
                context.Append(ChatMessage.System(WorkspacePrompts.Current));
                writer = new SessionStoreWriter(directory, session.Id, secrets,
                                                SessionIndexUpdaterFor(directory).HandleBatchCommittedAsync);
                host = new ChatSessionHost(session, context, options, model, tools, WorkspacePrompts.Current,
                                           permissions, routedApprovals, sink, sessionWriter : writer,
                                           instructionLoader : instructions, initialInstructions : initial,
                                           knownSecrets : secrets, credentialRoute : credentialRoute,
                                           modelTimeouts : user.Settings?.ModelTimeouts,
                                           compositionGuard : compositionGuard);
                await host.ChangeModelAsync(new SessionModelBinding(identity, model!, options.Context,
                                                                    options.ReasoningEffort,
                                                                    credentialRoute, profile?.Name,
                                                                    selected?.ReasoningEfforts, false,
                                                                    NetworkRouteResolver.BindingIdentity(networkRoute),
                                                                    user.Settings?.ModelTimeouts),
                                            cancellationToken).ConfigureAwait(false);
                _networkRoutes.Add(host, networkRoute);
            }

            permissionHost = host;
            if (host.Session.PermissionMigrationPending)
            {
                await host.ConfirmPermissionMigrationAsync(SessionApprovalMode.HumanApproval, cancellationToken)
                          .ConfigureAwait(false);
            }
            else if (host.Session.PermissionSelectionV2 is null && host.Session.PermissionSelection is null)
            {
                // 全新会话采用请求或可信默认；旧会话自动迁移为人工审批，已有 /2 选择保持不变。
                var requested = request.PermissionMode ??
                                user.Settings?.Permissions?.DefaultApprovalMode ??
                                SessionApprovalMode.HumanApproval;
                await host.ChangeApprovalModeAsync(requested, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    ///     <para>按生产组合结果构建工作区 Shell；活动能力快照、沙箱身份上下文与 C1 实例路径成对注入。</para>
    ///     <para>
    ///         Builds the workspace Shell from production composition, injecting the active-capability
    ///         snapshot, the sandbox identity context, and the C1 instance paths together.
    ///     </para>
    /// </summary>
    /// <param name="permissionSystemPaths">
    ///     <para>
    ///         C1 权限系统实例路径（用户配置、共享信任文件与会话存储目录）；grant 写根与之双向重叠
    ///         在 Prepare 期 fail-closed。
    ///     </para>
    ///     <para>
    ///         The C1 permission-system instance paths (the user config, the shared trust file, and the
    ///         session-store directory); a grant write root overlapping one bidirectionally fails closed
    ///         at Prepare.
    ///     </para>
    /// </param>
    internal ShellTool CreateWorkspaceShellTool(Workspace                workspace, int? timeoutSeconds,
                                                IProcessExecutionBackend backend,
                                                WindowsSandboxExecution? sandbox, GrantRegistry grants,
                                                IReadOnlyList<string>    permissionSystemPaths)
    {
        var grantContext = sandbox is null
            ? null
            : new SandboxGrantContext(sandbox.Policy, sandbox.Components, permissionSystemPaths);
        return new ShellTool(workspace, timeoutSeconds, _secrets,
                             processBackend : backend,
                             executionPolicy : sandbox?.ExecutionPolicy,
                             // 快照源仅在沙箱组合成功时接入：无身份形态下 source 会触发
                             // fail-closed 组合错误，而这里语义是"沙箱不可用"而非组合错误。
                             activeCapabilities : sandbox is null ? null : grants,
                             grantContext : grantContext);
    }

    /// <summary>
    ///     <para>
    ///         按已解析的会话存储目录取共享索引更新器：writer 每次可靠提交后增量推进列表索引，
    ///         索引故障不改变提交结果，由后台对账追平。
    ///     </para>
    ///     <para>
    ///         Returns the shared index updater for the resolved session-store directory: writers advance the list
    ///         index incrementally after every durable commit, and index faults never change commit outcomes —
    ///         background reconciliation catches up.
    ///     </para>
    /// </summary>
    private SessionIndexUpdater SessionIndexUpdaterFor(string directory) =>
        _sessionIndexUpdaters.GetOrAdd(directory,
                                       static resolved => new SessionIndexUpdater(new SessionIndexStore(resolved)));

    private IChatCompletionClient CreateClient(TinyHarnessConfig config, UserProfile? profile,
                                               NetworkSettings?  networkSettings,
                                               ModelTimeouts?    modelTimeouts = null)
    {
        var key = ApiKeyReader.Read(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget, _credentials,
                                    profile?.Name)
               ?? throw new InvalidOperationException("没有可用的模型凭据，请重新选择模型或配置凭据引用。");
        _secrets[Guid.NewGuid().ToString("N")] = key;
        if (config.ApiKeyEnvironmentVariable.Length > 0) _secrets[config.ApiKeyEnvironmentVariable] = key;
        return _modelFactory is null
            ? ModelClientFactory.Create(config.Model, config.Endpoint, key, config.ChatApi,
                                        networkSettings, profile?.Network, _credentials,
                                        modelTimeouts ?? ModelTimeouts.Default)
            : _modelFactory(config, key);
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
        return await ChangeAsync(host, profile, model, effort, revision, token,
                                 networkSettings : user.Settings?.Network,
                                 modelTimeouts : user.Settings?.ModelTimeouts).ConfigureAwait(false);
    }

    private async Task<SessionModelSelection> ChangeAsync(
        ChatSessionHost  host, UserProfile profile, UserProfileModel model, ModelReasoningEffort? effort,
        long             revision, CancellationToken token, IChatCompletionClient? preparedClient = null,
        NetworkSettings? networkSettings = null, ModelTimeouts? modelTimeouts = null)
    {
        if (effort is { } level && model.ReasoningEfforts?.Contains(level) != true)
            throw new InvalidOperationException("模型未声明该强度。");
        if (string.IsNullOrWhiteSpace(profile.Endpoint)) throw new InvalidOperationException("请先配置供应商 Endpoint。");
        var current              = host.Session.ModelSelection!;
        var networkRoute         = NetworkRouteResolver.Resolve(networkSettings, profile.Network);
        var networkRouteIdentity = NetworkRouteResolver.BindingIdentity(networkRoute);
        var context = (current.Context ?? new ContextOptions()) with
        {
            ContextWindowTokens = model.ContextWindowTokens
        };
        // 本次组合实际采用的超时策略：与当前绑定不同时禁止快速复用，必须构造携带新策略的客户端。
        var effectiveTimeouts = modelTimeouts ?? ModelTimeouts.Default;
        if (host.HasModelBinding &&
            current.Revision            == revision &&
            current.Model.ModelId       == model.Id &&
            current.Model.Endpoint      == profile.Endpoint &&
            current.Model.ChatApi       == profile.ChatApi &&
            current.CredentialRoute     == Route(profile) &&
            current.ProfileHint         == profile.Name &&
            current.Context             == context &&
            host.EffectiveModelTimeouts == effectiveTimeouts &&
            (current.ReasoningCapabilities ?? []).SequenceEqual(model.ReasoningEfforts ?? []) &&
            _networkRoutes.TryGetValue(host, out var previousRoute) && previousRoute == networkRoute)
            return await host.ChangeReasoningEffortAsync(effort, token, model.ReasoningEfforts,
                                                         revision).ConfigureAwait(false);
        var client = preparedClient ??
                     CreateClient(ConfigFor(new TinyHarnessConfig { WorkspaceRoot = host.Session.WorkspaceRoot },
                                            profile, model), profile, networkSettings, modelTimeouts);
        var candidate = new SessionModelBinding(new SessionModelIdentity(model.Id, profile.Endpoint, profile.ChatApi),
                                                client,
                                                context, effort, Route(profile), profile.Name, model.ReasoningEfforts,
                                                preparedClient is null, networkRouteIdentity, effectiveTimeouts);
        try
        {
            var selection = await host.ChangeModelAsync(candidate, token, revision).ConfigureAwait(false);
            _networkRoutes.Remove(host);
            _networkRoutes.Add(host, networkRoute);
            return selection;
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
        var user = await UserConfigStore.LoadAsync(_userConfigPath ?? UserConfigStore.DefaultFilePath(), token)
                                        .ConfigureAwait(false);
        EnsurePermissionConfigConfirmed(user);
        var current = host.Session.ModelSelection!;
        // 无目录归属的项目配置仍沿用原路径，不擅自绑定目录中的同名模型。
        if (current.CredentialRoute is null) return;
        if (current.ProfileHint is null && current.CredentialRoute.StartsWith('|')) return;
        var target = FindTarget(user, current, current.Model) ??
                     throw new InvalidOperationException("当前供应商或模型已不存在，请重新选择模型后发送。");
        await ChangeAsync(host, target.Profile, target.Model, CurrentEffort(current.ReasoningEffort, target.Model),
                          current.Revision, token, networkSettings : user.Settings?.Network,
                          modelTimeouts : user.Settings?.ModelTimeouts).ConfigureAwait(false);
    }

    private static void EnsurePermissionConfigConfirmed(UserConfig user)
    {
        if (user.Settings?.Permissions?.PendingMigration == true)
            throw new InvalidOperationException("旧权限配置尚未确认迁移，请先在设置中确认权限配置迁移。");
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
