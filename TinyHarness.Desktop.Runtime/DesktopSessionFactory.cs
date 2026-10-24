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
/// <para>提供桌面使用的脱敏事件，不将原始协议载荷或不透明推理暴露给视图。</para>
/// <para>Provides redacted desktop events without exposing raw protocol payloads or opaque reasoning to views.</para>
/// </summary>
public sealed record DesktopEvent(string Kind, string? RunId, string? Text = null,
                                  SessionHistoryItem? History = null);

/// <summary>
/// <para>一次打开请求；工作区及写入许可由人类界面显式选择，恢复不继承历史批准。</para>
/// <para>An opening request; workspace and write consent come from the human UI, and resume inherits no old approvals.</para>
/// </summary>
public sealed record DesktopSessionRequest(string WorkspaceRoot, bool AllowChanges = false,
                                           string? SessionId = null, string? ProfileName = null,
                                           string? ModelId = null, bool ShowReasoning = false);

/// <summary>
/// <para>独立于 CLI 组装桌面宿主，复用 Core 的模型、权限、工具和唯一日志。</para>
/// <para>Composes desktop hosts independently of the CLI, reusing Core models, permissions, tools and journal.</para>
/// </summary>
public sealed class DesktopSessionFactory
{
    private readonly Func<TinyHarnessConfig, string, IChatCompletionClient> _modelFactory;
    private readonly ICredentialStore _credentials;
    private readonly string? _userConfigPath;

    /// <summary>
    /// <para>允许离线验收注入模型与凭据存储；生产默认使用 Core 的正式客户端和系统凭据。</para>
    /// <para>Allows offline acceptance to inject models and credentials; production uses Core clients and system credentials.</para>
    /// </summary>
    public DesktopSessionFactory(Func<TinyHarnessConfig, string, IChatCompletionClient>? modelFactory = null,
                                 ICredentialStore? credentials = null, string? userConfigPath = null)
    {
        _modelFactory = modelFactory ?? ((config, key) => ModelClientFactory.Create(config.Model, config.Endpoint, key, config.ChatApi));
        _credentials = credentials ?? new WindowsCredentialStore();
        _userConfigPath = userConfigPath;
    }

    /// <summary>
    /// <para>读取可信设置，建立新会话或严格恢复；失败释放所有已打开的资源，不发送模型请求。</para>
    /// <para>Reads trusted settings and creates or strictly resumes a session; failures release resources without model calls.</para>
    /// </summary>
    public async Task<DesktopSession> OpenAsync(DesktopSessionRequest request,
                                                Func<DesktopEvent, ValueTask> events,
                                                Func<DesktopApproval?, ValueTask> approvalChanged,
                                                CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.WorkspaceRoot);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var resolution = await ConfigResolver.ResolveAsync(null, cancellationToken, root, _userConfigPath).ConfigureAwait(false);
        var user = await UserConfigStore.LoadAsync(resolution.UserConfigPath, cancellationToken).ConfigureAwait(false);
        var config = SelectConfig(resolution.Config with { WorkspaceRoot = root }, user, request);
        var credentialRoute = CredentialRoute.Describe(request.ProfileName ?? resolution.ProfileName,
                                                       config.ApiKeyEnvironmentVariable,
                                                       config.ApiKeyCredentialTarget);
        var directory = SessionStorePaths.ResolveProtected(user.Settings?.SessionStoreDirectory, root);
        var key = ApiKeyReader.Read(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                     _credentials, request.ProfileName ?? resolution.ProfileName)
                 ?? throw new InvalidOperationException("没有可用的模型凭据，请在用户配置中设置凭据引用或环境变量。");
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal) { ["model-api-key"] = key };
        var redactor = new DisplaySecretRedactor(secrets);
        var approvals = new DesktopApprovalProvider(approvalChanged, redactor.Redact);
        var sink = new DesktopEventSink(events, secrets, request.ShowReasoning);
        var access = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(root));
        access.SetReadOnly(!request.AllowChanges);
        WindowsSandboxExecution? sandbox = null;
        if (request.AllowChanges && user.Settings?.WindowsSandbox is { Enabled: true } settings)
        {
            // 会话存储不能落入任一可信写根；此处不进行 provisioning 或机器权限变更。
            foreach (var writeRoot in settings.AdditionalWriteRoots)
                SessionStorePaths.EnsureOutsideWorkspace(directory, writeRoot, "settings.sessionStoreDirectory");
            sandbox = WindowsSandboxComposer.Compose(settings, root, secrets);
        }
        var permissions = new PermissionEngine(root, config.CommandRules, workspaceReadOnly: () => access.IsReadOnly);
        var workspace = new Workspace(root);
        ITool[] toolSet =
        [
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
            new ApplyPatchTool(workspace),
            new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                          processBackend: sandbox?.Backend ?? (IProcessExecutionBackend)new HostProcessBackend(secrets),
                          executionPolicy: sandbox?.ExecutionPolicy)
        ];
        var tools = new ToolRegistry(toolSet.Select(tool => access.Guard(() => tool)));
        var options = new AgentOptions
        {
            Model = config.Model, MaxAgentSteps = config.MaxAgentSteps, EnableWorkItems = true,
            Budget = user.Settings?.RunBudget ?? new RunBudgetLimits(),
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens = config.ContextWindowTokens, ReservedOutputTokens = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold
            }
        };
        var identity = new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi);
        var instructions = new ProjectInstructionLoader(workspace);
        IChatCompletionClient? model = null;
        SessionStoreWriter? writer = null;
        try
        {
            model = _modelFactory(config, key);
            ChatSessionHost host;
            if (request.SessionId is { } sessionId)
            {
                writer = await SessionStoreWriter.OpenExistingAsync(directory, sessionId, secrets, cancellationToken)
                                                  .ConfigureAwait(false);
                var restored = SessionResume.Restore(writer.LoadedSnapshot!, root, identity, WorkspacePrompts.Current,
                                                      options.Context);
                host = new ChatSessionHost(restored.Session, restored.Context, options, model, tools,
                                            WorkspacePrompts.Current, permissions, approvals, sink,
                                            sessionWriter: writer, needsInspection: restored.NeedsInspection,
                                            instructionLoader: instructions, knownSecrets: secrets);
                // 恢复不经过会话内切换入口：核对当前凭据路由，不一致时按选择事务采纳并隔离旧路由推理。
                await host.EnsureCredentialRouteAsync(credentialRoute, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var initial = await instructions.LoadAsync(null, [], cancellationToken).ConfigureAwait(false);
                var session = new ChatSession(Guid.NewGuid().ToString("N"), root, identity);
                var context = new ConversationContext(options.Context);
                context.Append(Core.Models.ChatCompletions.ChatMessage.System(WorkspacePrompts.Current));
                writer = new SessionStoreWriter(directory, session.Id, secrets);
                await writer.CommitAsync(session, context, cancellationToken).ConfigureAwait(false);
                host = new ChatSessionHost(session, context, options, model, tools, WorkspacePrompts.Current,
                                            permissions, approvals, sink, sessionWriter: writer,
                                            instructionLoader: instructions, initialInstructions: initial,
                                            knownSecrets: secrets, credentialRoute: credentialRoute);
            }
            return new DesktopSession(host, model, approvals);
        }
        catch
        {
            writer?.Dispose();
            (model as IDisposable)?.Dispose();
            throw;
        }
    }

    private static TinyHarnessConfig SelectConfig(TinyHarnessConfig config, UserConfig user, DesktopSessionRequest request)
    {
        if (request.ProfileName is null) return config;
        var profile = user.Profiles.SingleOrDefault(profile => profile.Name == request.ProfileName)
                      ?? throw new InvalidOperationException("所选提供方已不在用户配置中。");
        var selected = profile.Models.SingleOrDefault(model => model.Id == request.ModelId)
                       ?? throw new InvalidOperationException("所选模型已不在用户配置中。");
        return config with
        {
            Model = selected.Id, Endpoint = profile.Endpoint, ChatApi = profile.ChatApi,
            ContextWindowTokens = selected.ContextWindowTokens,
            ApiKeyCredentialTarget = profile.ApiKeyCredentialTarget,
            ApiKeyEnvironmentVariable = profile.ApiKeyEnvironmentVariable
        };
    }
}

internal sealed class DesktopEventSink(Func<DesktopEvent, ValueTask> publish,
                                       IReadOnlyDictionary<string, string> secrets,
                                       bool includeReasoning) : IHostEventSink
{
    private readonly DisplaySecretRedactor _text = new(secrets);
    private readonly DisplaySecretRedactor _reasoning = new(secrets);
    public bool IncludeReasoningText => includeReasoning;

    public async ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        // 条目落位即本次模型流的终点：先冲刷 text 与 reasoning 的脱敏残留，再发布正式条目替换流式气泡。
        if (@event.Kind == HostEventKind.HistoryItemCompleted && @event.Payload is SessionHistoryItem
            { Kind: SessionHistoryKind.Process or SessionHistoryKind.Final })
        {
            await publish(new DesktopEvent("text", @event.RunId, _text.Flush())).ConfigureAwait(false);
            await publish(new DesktopEvent("reasoning", @event.RunId, _reasoning.Flush())).ConfigureAwait(false);
        }
        DesktopEvent? display = @event.Kind switch
        {
            HostEventKind.AssistantTextDelta when @event.Payload is string text =>
                new("text", @event.RunId, _text.Push(text)),
            HostEventKind.AssistantReasoningDelta when @event.Payload is string text =>
                new("reasoning", @event.RunId, _reasoning.Push(text)),
            HostEventKind.HistoryItemCompleted when @event.Payload is SessionHistoryItem item =>
                new("history", @event.RunId, History: item),
            HostEventKind.RunStarted => new("state", @event.RunId, "运行中"),
            HostEventKind.RunControlChanged when @event.Payload is string state => new("state", @event.RunId, state),
            HostEventKind.ModelRequestStarted => new("state", @event.RunId, "等待模型响应"),
            HostEventKind.ModelRequestWaiting => new("state", @event.RunId, "等待模型响应"),
            HostEventKind.ToolStarted => new("state", @event.RunId, "工具执行中"),
            HostEventKind.RunError when @event.Payload is string error => new("error", @event.RunId, _text.Redact(error)),
            HostEventKind.RunFinished => new("state", @event.RunId, "正在保存"),
            _ => null
        };
        if (@event.Kind == HostEventKind.RunFinished)
        {
            await publish(new DesktopEvent("text", @event.RunId, _text.Flush())).ConfigureAwait(false);
            await publish(new DesktopEvent("reasoning", @event.RunId, _reasoning.Flush())).ConfigureAwait(false);
        }
        if (display is not null) await publish(display).ConfigureAwait(false);
    }
}
