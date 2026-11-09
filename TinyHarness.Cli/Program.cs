using System.Collections.Concurrent;
using System.Text;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Prompts;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Mcp;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;
using TinyHarness.Core.Services.Worker;
using WindowsCredentialStore = TinyHarness.Core.Services.Runtime.WindowsCredentialStore;
using ProcessWorkspaceAclGrant = TinyHarness.Cli.Services.ProcessWorkspaceAclGrant;

namespace TinyHarness.Cli;

/// <summary>
///     <para>TinyHarness 命令行应用的组合根与入口调度器。</para>
///     <para>Composition root and entry-point dispatcher for the TinyHarness command-line application.</para>
/// </summary>
internal static class Program
{
    /// <summary>
    ///     <para>调度 CLI 命令，并将入口异常转换为诊断输出和退出码。</para>
    ///     <para>Dispatches CLI commands and converts entry-point exceptions into diagnostics and exit codes.</para>
    /// </summary>
    /// <returns>
    ///     <para>命令退出码；入口捕获的用法错误为 2，取消为 130，其他失败为 1。</para>
    ///     <para>
    ///         The command's exit code; caught usage errors return 2,
    ///         cancellation returns 130, and other failures return 1.
    ///     </para>
    /// </returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            if (ex.Usage is not { Length: > 0 } usage) return 2;
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(usage.TrimEnd());

            return 2;
        }
        catch (ConfigException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return ex.IsUsageError ? 2 : 1;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Run cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // 将启动边界异常转换为 CLI 诊断，避免 Windows 弹出 CLR 错误对话框。
            await Console.Error.WriteLineAsync($"TinyHarness failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> DispatchAsync(string[] args)
    {
        if (args is ["sandbox", "verify-network-probe", ..])
        {
            using var probeCancellation = new CancellationTokenSource();
            using var registration      = new ConsoleCancellation(probeCancellation);
            return await SandboxNetworkProbe.RunCommandAsync(args[2..], Console.Out, probeCancellation.Token)
                                            .ConfigureAwait(false);
        }

        if (args.FirstOrDefault() == ProcessWorkspaceAclGrant.HelperArgument)
        {
            if (args.Length != 2) throw new CliUsageException("The workspace ACL helper requires exactly one payload.");
            return await ProcessWorkspaceAclGrant.ExecuteHelperAsync(args[1]).ConfigureAwait(false);
        }

        ProjectInstructionDiagnosticLog.Initialize();
        var options = CommandLine.Parse(args);
        switch (options.Kind)
        {
            case CliCommandKind.Help :
                await Console.Out.WriteLineAsync(HelpText.For(options.HelpTopic).TrimEnd());
                return 0;

            case CliCommandKind.ProcessSmokeChild :
                await Console.Out.WriteLineAsync("process stdout");
                await Console.Error.WriteLineAsync("process stderr");
                return 0;

            case CliCommandKind.Smoke :
                return await RunSmokeAsync(options.ConfigPath);

            case CliCommandKind.Mcp :
                return await RunMcpServerAsync();

            case CliCommandKind.Run :
                return await RunAsync(options.Prompt!, options.ConfigPath, options.ReasoningDisplay);

            case CliCommandKind.Chat :
                return await RunChatAsync(options.ConfigPath, options.ResumeSessionId, options.ReasoningDisplay);

            default :
                return await RunManagementCommandAsync(options).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     <para>执行管理命令；绝不把输入发送给模型。</para>
    ///     <para>Executes management commands without sending their input to a model.</para>
    /// </summary>
    private static async Task<int> RunManagementCommandAsync(CliOptions options)
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var context = new CommandContext
        {
            Io          = new ConsoleCliIo(),
            Credentials = new WindowsCredentialStore()
        };

        return options.Kind switch
        {
            CliCommandKind.Init => await InitCommand.ExecuteAsync(context, cts.Token).ConfigureAwait(false),
            CliCommandKind.Config => await ConfigCommand.ExecuteAsync(context, options, cts.Token)
                                                        .ConfigureAwait(false),
            CliCommandKind.Provider => await ProviderCommand.ExecuteAsync(context, options, cts.Token)
                                                            .ConfigureAwait(false),
            CliCommandKind.Auth  => await AuthCommand.ExecuteAsync(context, options, cts.Token).ConfigureAwait(false),
            CliCommandKind.Model => await ModelCommand.ExecuteAsync(context, options, cts.Token).ConfigureAwait(false),
            CliCommandKind.Doctor => await DoctorCommand.ExecuteAsync(context, options, cts.Token)
                                                        .ConfigureAwait(false),
            CliCommandKind.Sandbox => await SandboxCommand.ExecuteAsync(context, options, cts.Token)
                                                          .ConfigureAwait(false),
            CliCommandKind.Session => await SessionCommand.ExecuteAsync(context, options, cts.Token)
                                                          .ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unhandled command '{options.Kind}'.")
        };
    }

    /// <summary>
    ///     <para>使用共享执行栈运行单次 Agent 任务，输出事件流与终态摘要。</para>
    ///     <para>Runs one agent task with the shared execution stack and prints events and a terminal summary.</para>
    /// </summary>
    /// <remarks>
    ///     <para>配置与工具由 <see cref="ComposeAgentAsync" /> 组合；审批通过单一输入读取器接收。</para>
    ///     <para>
    ///         <see cref="ComposeAgentAsync" /> composes configuration and tools;
    ///         approvals use a single input reader.
    ///     </para>
    /// </remarks>
    private static async Task<int> RunAsync(string prompt, string? configPath, ReasoningDisplayMode reasoningDisplay)
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var       reader             = new ExclusiveInputReader(Console.In);
        var migrationConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cts.Token)
                                                   .ConfigureAwait(false);
        if (migrationConfig.Settings?.Permissions is { PendingMigration: true })
        {
            await WritePermissionMigrationSummaryAsync(migrationConfig, Console.Error,
                                                       "Legacy permission configuration requires confirmation.", true)
               .ConfigureAwait(false);
            await Console.Error.WriteLineAsync(
                                               "Configuration migration is pending. Run 'tinyharness chat' with interactive input to review and confirm it.");
            return 1;
        }

        // 常设信任未确认不阻断 run：联网与附加写根写按逐次审批运行，提示确认入口。
        await WriteStandingTrustNoticeAsync(migrationConfig, Console.Error).ConfigureAwait(false);

        var composition = await ComposeAgentAsync(configPath, cts.Token, true, reader).ConfigureAwait(false);
        await using var events =
            new ConsoleHostEventSink(composition.KnownSecrets, reasoningDisplay : reasoningDisplay);

        await WriteHeaderAsync("TinyHarness run", composition).ConfigureAwait(false);

        // 审批通过单一读取器接收，避免多个循环争抢 stdin。
        ChatSessionHost? activeHost = null;
        var approver = CliApprovalRouting.Create(
                                                 new ConsoleApprovalProvider(reader, Console.Out,
                                                                             composition.KnownSecrets,
                                                                             !Console.IsInputRedirected),
                                                 () => activeHost ??
                                                       throw new
                                                           InvalidOperationException("The session host is not ready."),
                                                 composition.Permissions,
                                                 token => CreateReviewerAsync(activeHost ??
                                                                              throw new
                                                                                  InvalidOperationException("The session host is not ready."),
                                                                              composition.KnownSecrets, token));
        var instructionLoader   = new ProjectInstructionLoader(new Workspace(composition.Config.WorkspaceRoot));
        var initialInstructions = await instructionLoader.LoadAsync(null, [], cts.Token).ConfigureAwait(false);

        var project = await SessionProjectBinding.GetProjectAsync(composition.SessionStoreDirectory!,
                                                                  composition.Config.WorkspaceRoot, cts.Token)
                                                 .ConfigureAwait(false);
        var session = new ChatSession(Guid.NewGuid().ToString("N"), composition.Config.WorkspaceRoot,
                                      new SessionModelIdentity(composition.Config.Model, composition.Config.Endpoint,
                                                               composition.Config.ChatApi), null, project.Id);
        var context = new ConversationContext(composition.Options.Context);
        context.Append(ChatMessage.System(WorkspacePrompts.Current));
        using var host = new ChatSessionHost(session, context,
                                             composition.Options,
                                             composition.Model!,
                                             composition.Tools,
                                             WorkspacePrompts.Current,
                                             composition.Permissions,
                                             approver,
                                             events,
                                             sessionWriter : new SessionStoreWriter(composition.SessionStoreDirectory!,
                                                 session.Id, composition.KnownSecrets,
                                                 composition.SessionIndexUpdater is { } runUpdater
                                                     ? runUpdater.HandleBatchCommittedAsync
                                                     : null),
                                             instructionLoader : instructionLoader,
                                             initialInstructions : initialInstructions,
                                             knownSecrets : composition.KnownSecrets,
                                             credentialRoute : composition.CredentialRoute,
                                             modelTimeouts : (composition.Model as MicrosoftAiChatClient)
                                           ?.EffectiveTimeouts,
                                             compositionGuard : composition.CompositionGuard);
        activeHost = host;

        await host.ChangeApprovalModeAsync(composition.DefaultApprovalMode, cts.Token).ConfigureAwait(false);

        var run      = await host.SubmitTurnAsync(prompt, cts.Token).ConfigureAwait(false);
        var result   = run.Result!;
        var redactor = new DisplaySecretRedactor(composition.KnownSecrets);

        await Console.Out.WriteLineAsync();
        await Console.Out.WriteLineAsync($"status     : {result.Status}");
        await Console.Out.WriteLineAsync($"steps      : {result.Steps}");
        await Console.Out.WriteLineAsync($"toolExecs  : {result.ToolExecutions}");
        if (result.Budget is { } budget)
            await
                Console.Out
                       .WriteLineAsync($"budget     : {budget.ModelRequests} requests; {budget.AccountedTokens:N0} " +
                                       $"{(budget.HasEstimatedTokens ? "estimated/mixed" : "reported")} tokens; {budget.ElapsedMilliseconds} ms; limit={budget.LimitReason ?? "none"}");
        if (result.InputTokens is not null || result.OutputTokens is not null)
        {
            var inputTokens  = (result.InputTokens  ?? 0).ToString("N0");
            var outputTokens = (result.OutputTokens ?? 0).ToString("N0");
            await Console.Out
                         .WriteLineAsync($"usage      : {inputTokens} in / {outputTokens} out tokens (server-reported; absent requests not counted)");
        }

        await Console.Out.WriteLineAsync($"final      : {TerminalDisplay.Format(result.FinalMessage)}");
        await Console.Out.WriteLineAsync("storage    : append-only JSONL session log");
        if (host.PersistencePath is { } persistencePath && File.Exists(persistencePath))
            await Console.Out.WriteLineAsync($"session    : {persistencePath}").ConfigureAwait(false);
        if (host.PersistenceWarning is { Length: > 0 } warning)
            await Console.Out.WriteLineAsync($"warning    : {TerminalDisplay.Format(warning, redactor)}")
                         .ConfigureAwait(false);
        if (host.DisplayWarning is { Length: > 0 } displayWarning)
            await Console.Out.WriteLineAsync($"display    : {TerminalDisplay.Format(displayWarning, redactor)}")
                         .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.Error))
            await Console.Out.WriteLineAsync($"error      : {TerminalDisplay.Format(result.Error, redactor)}");

        return result.Status switch
        {
            AgentStatus.Completed => 0,
            AgentStatus.Cancelled => 130,
            _                     => 1
        };
    }

    /// <summary>
    ///     <para>使用与 run 共享的执行栈，创建或恢复交互式多回合会话。</para>
    ///     <para>Creates or resumes an interactive multi-turn session using the execution stack shared with run.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         回合、审批与检查确认共享一个 <see cref="ExclusiveInputReader" />；
    ///         <see cref="ChatCancellation" /> 区分活动回合取消与空闲会话退出。
    ///         终态回合追加到受保护 JSONL 会话日志供离线查看；保存失败必须报告，终态不代表保存成功。
    ///     </para>
    ///     <para>
    ///         Turns, approvals, and inspection confirmation share one <see cref="ExclusiveInputReader" />;
    ///         <see cref="ChatCancellation" /> distinguishes active-turn cancellation from idle-session exit.
    ///         Terminal turns are appended to the protected JSONL session log for offline inspection;
    ///         save failures must be reported, and a terminal state does not prove persistence.
    ///     </para>
    ///     <para>
    ///         恢复时先取锁并严格读取、核对审计，再校验工作区、模型、协议身份与 opaque 完整性；
    ///         校验失败由入口报告退出码 1，不降级放行。会话宿主持有并释放存储写入器。
    ///     </para>
    ///     <para>
    ///         Resume takes the lock, strictly reads and reconciles the audit, then validates
    ///         workspace, model, protocol identity, and opaque integrity. Validation failures
    ///         reach the entry point as exit code 1 without fallback. The session host owns and disposes the store writer.
    ///     </para>
    /// </remarks>
    private static async Task<int> RunChatAsync(
        string? configPath, string? resumeSessionId, ReasoningDisplayMode reasoningDisplay)
    {
        using var cancellation = new ChatCancellation();
        using var consoleInput = new ChatConsoleInputReader(Console.In, cancellation,
                                                            OperatingSystem.IsWindows() && !Console.IsInputRedirected);
        using var reader = new ChatInputRouter(new ExclusiveInputReader(consoleInput));
        var migrationBaseline = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(),
                                                                cancellation.SessionToken).ConfigureAwait(false);
        if (!await ConfirmConfigurationMigrationAsync(migrationBaseline, reader, cancellation.SessionToken)
               .ConfigureAwait(false))
            return 1;
        if (!await ConfirmStandingTrustMigrationAsync(reader, cancellation.SessionToken).ConfigureAwait(false))
            return 1;

        var composition = await ComposeAgentAsync(configPath, cancellation.SessionToken, true, reader,
                                                  true)
           .ConfigureAwait(false);
        var config           = composition.Config;
        var selectionSecrets = (ConcurrentDictionary<string, string>)composition.KnownSecrets;
        await using var events =
            new ConsoleHostEventSink(composition.KnownSecrets, reasoningDisplay : reasoningDisplay);

        // 回合、审批与检查确认必须共享同一个读取器，不能另开 stdin 循环。

        var instructionLoader = new ProjectInstructionLoader(new Workspace(config.WorkspaceRoot));
        var identity          = new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi);

        ChatModelSelection? draftSelector = null;

        async Task<ChatSessionHost> CreateHostAsync(CancellationToken token)
        {
            var               instructions = await instructionLoader.LoadAsync(null, [], token).ConfigureAwait(false);
            var               draft = draftSelector?.DraftTarget;
            var               selectedOptions = composition.Options;
            var               selectedIdentity = identity;
            var               selectedClient = composition.Model;
            var               selectedRoute = composition.CredentialRoute;
            UserProfile?      initialProfile = null;
            UserProfileModel? initialModel = null;
            if (composition.Resolution.ProfileName is { } profileName)
            {
                var user = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), token)
                                                .ConfigureAwait(false);
                initialProfile  = user.Profiles.SingleOrDefault(item => item.Name        == profileName);
                initialModel    = initialProfile?.Models.SingleOrDefault(item => item.Id == selectedIdentity.ModelId);
                selectedOptions = selectedOptions with { ReasoningEffort = initialModel?.DefaultReasoningEffort };
            }

            if (draft is not null)
            {
                var catalog = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), token)
                                                   .ConfigureAwait(false);
                var provider = catalog.Profiles.SingleOrDefault(item => item.Name == draft.Profile.Name)
                            ?? throw new InvalidOperationException("Draft provider was removed; select again.");
                var model = provider.Models.SingleOrDefault(item => item.Id == draft.Model.Id)
                         ?? throw new InvalidOperationException("Draft model was removed; select again.");
                if (provider.Endpoint                  != draft.Profile.Endpoint                  ||
                    provider.ChatApi                   != draft.Profile.ChatApi                   ||
                    provider.ApiKeyCredentialTarget    != draft.Profile.ApiKeyCredentialTarget    ||
                    provider.ApiKeyEnvironmentVariable != draft.Profile.ApiKeyEnvironmentVariable ||
                    model.ContextWindowTokens          != draft.Model.ContextWindowTokens         ||
                    !(model.ReasoningEfforts ?? []).SequenceEqual(draft.Model.ReasoningEfforts ?? []))
                    throw new InvalidOperationException("Draft configuration changed; select again before sending.");
                selectedOptions = selectedOptions with
                {
                    Model = model.Id,
                    ReasoningEffort = draft.Effort,
                    Context = (selectedOptions.Context ?? new ContextOptions()) with
                    {
                        ContextWindowTokens = model.ContextWindowTokens
                    }
                };
                selectedIdentity = new SessionModelIdentity(model.Id, provider.Endpoint, provider.ChatApi);
                selectedRoute = CredentialRoute.Describe(provider.Name, provider.ApiKeyEnvironmentVariable,
                                                         provider.ApiKeyCredentialTarget);
            }

            if (selectedClient is null && draft is null)
                throw new InvalidOperationException("Select a configured model with /models use before sending.");

            token.ThrowIfCancellationRequested();
            var project = await SessionProjectBinding.GetProjectAsync(composition.SessionStoreDirectory!,
                                                                      config.WorkspaceRoot,
                                                                      token).ConfigureAwait(false);
            var session = new ChatSession(Guid.NewGuid().ToString("N"), config.WorkspaceRoot, selectedIdentity,
                                          null, project.Id);
            var context = new ConversationContext(selectedOptions.Context);
            context.Append(ChatMessage.System(WorkspacePrompts.Current));
            var writer =
                new SessionStoreWriter(composition.SessionStoreDirectory!, session.Id, composition.KnownSecrets,
                                       composition.SessionIndexUpdater is { } creationUpdater
                                           ? creationUpdater.HandleBatchCommittedAsync
                                           : null);
            ChatSessionHost? createdHost = null;
            var              newStack    = CreateChatExecutionStack(composition);
            var newApprover = CliApprovalRouting.Create(
                                                        new ConsoleApprovalProvider(reader, Console.Out,
                                                            composition.KnownSecrets,
                                                            !Console.IsInputRedirected),
                                                        () => createdHost ??
                                                              throw new
                                                                  InvalidOperationException("The session host is not ready."),
                                                        newStack.Permissions,
                                                        token => CreateReviewerAsync(
                                                         createdHost ??
                                                         throw new
                                                             InvalidOperationException("The session host is not ready."),
                                                         composition.KnownSecrets, token));
            try
            {
                if (draft is not null)
                    selectedClient = await CreateSelectionClientAsync(draft.Profile, draft.Model.Id, token)
                       .ConfigureAwait(false);
                // 首次普通输入才执行本工厂：/new 只进入未发送空白态，不创建会话或写入端；
                // 项目规则仍由首回合边界可靠提交。
                // 启动客户端归组合根按进程生命周期持有（后续宿主共用），宿主以借用方式绑定。
                createdHost = new ChatSessionHost(session, context, selectedOptions, selectedClient!, newStack.Tools,
                                                  WorkspacePrompts.Current, newStack.Permissions,
                                                  newApprover,
                                                  events, sessionWriter : writer, instructionLoader : instructionLoader,
                                                  initialInstructions : instructions,
                                                  knownSecrets : composition.KnownSecrets,
                                                  credentialRoute : selectedRoute,
                                                  ownsModelClient : draft is not null,
                                                  modelTimeouts : (selectedClient as MicrosoftAiChatClient)
                                                ?.EffectiveTimeouts,
                                                  compositionGuard : composition.CompositionGuard);
                await createdHost.ChangeModelAsync(new SessionModelBinding(selectedIdentity, selectedClient!,
                                                                           selectedOptions.Context,
                                                                           selectedOptions.ReasoningEffort,
                                                                           selectedRoute,
                                                                           draft?.Profile.Name ?? initialProfile?.Name,
                                                                           draft?.Model.ReasoningEfforts ??
                                                                           initialModel?.ReasoningEfforts, false,
                                                                           (selectedClient as MicrosoftAiChatClient)
                                                                         ?.NetworkRouteIdentity,
                                                                           (selectedClient as MicrosoftAiChatClient)
                                                                         ?.EffectiveTimeouts),
                                                   token).ConfigureAwait(false);
                await createdHost.ChangeApprovalModeAsync(composition.DefaultApprovalMode, token)
                                 .ConfigureAwait(false);
                await composition.Access.BindSessionAsync(newStack.Permissions, token).ConfigureAwait(false);
                return createdHost;
            }
            catch
            {
                if (createdHost is not null)
                {
                    createdHost.Dispose();
                }
                else
                {
                    writer.Dispose();
                    if (!ReferenceEquals(selectedClient, composition.Model))
                        (selectedClient as IDisposable)?.Dispose();
                }

                throw;
            }
        }

        async Task<ChatSessionHost> ResumeHostAsync(string sessionId, CancellationToken token)
        {
            var writer = await SessionStoreWriter.OpenExistingAsync(composition.SessionStoreDirectory!, sessionId,
                                                                    composition.KnownSecrets, token,
                                                                    composition.SessionIndexUpdater is { } resumeUpdater
                                                                        ? resumeUpdater.HandleBatchCommittedAsync
                                                                        : null)
                                                 .ConfigureAwait(false);
            ChatSessionHost?                  host     = null;
            ChatResumeBinding.ResolvedTarget? resolved = null;
            try
            {
                var snapshot = writer.LoadedSnapshot!;
                // 先恢复历史事实：验证日志、完整历史与选择链，不以目标模型可用性为前提；
                // 历史模型已删除不构成日志损坏，也不解析其凭据或构造其客户端。
                // 会话沿用保存的系统提示；应用升级不改变历史指令，当前权限由执行引擎独立约束。
                var systemPrompt = snapshot.Conversation?.Messages.FirstOrDefault()?.Content ??
                                   WorkspacePrompts.Current;
                var restored = SessionResume.Restore(snapshot, config.WorkspaceRoot, systemPrompt,
                                                     composition.Options.Context);
                await SessionProjectBinding.BindRestoredAsync(composition.SessionStoreDirectory!, restored.Session,
                                                              writer, token).ConfigureAwait(false);
                var resumeStack = CreateChatExecutionStack(composition);
                var resumeApprover = CliApprovalRouting.Create(
                                                               new ConsoleApprovalProvider(reader, Console.Out,
                                                                   composition.KnownSecrets,
                                                                   !Console.IsInputRedirected),
                                                               () => host ??
                                                                     throw new
                                                                         InvalidOperationException("The session host is not ready."),
                                                               resumeStack.Permissions,
                                                               approvalToken => CreateReviewerAsync(
                                                                host ??
                                                                throw new
                                                                    InvalidOperationException("The session host is not ready."),
                                                                composition.KnownSecrets, approvalToken));

                // 再解析可执行目标：默认最后已提交选择；失效、歧义或能力不再支持时不悄悄改用全局默认。
                var outcome =
                    await ChatResumeBinding.ResolveAsync(snapshot, identity, composition.Options,
                                                         composition.Model, composition.CredentialRoute,
                                                         t =>
                                                             UserConfigStore
                                                                .LoadAsync(UserConfigStore.DefaultFilePath(), t),
                                                         CreateSelectionClientAsync, token).ConfigureAwait(false);
                await Console.Out.WriteLineAsync($"  resumed   : {restored.Session.Turns.Count} turns from commit " +
                                                 $"{writer.CommitSequence} ({snapshot.UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss})");
                if (restored.RewoundTail)
                    await
                        Console.Out
                               .WriteLineAsync("  rewound    : the last incomplete tool round was dropped from the model view");
                if (outcome.Target is not { } target)
                {
                    // 目标不可解析：以未绑定宿主接管已恢复历史与写入端，等待用户显式选择后再发送。
                    host = new ChatSessionHost(restored.Session, restored.Context, composition.Options,
                                               null, resumeStack.Tools, systemPrompt,
                                               resumeStack.Permissions,
                                               resumeApprover,
                                               events, sessionWriter : writer,
                                               needsInspection : restored.NeedsInspection,
                                               instructionLoader : instructionLoader,
                                               knownSecrets : composition.KnownSecrets,
                                               compositionGuard : composition.CompositionGuard);
                    await Console.Out.WriteLineAsync("  model     : " + outcome.ReselectionReason);
                    await Console.Out.WriteLineAsync(
                                                     "  model     : the session history stays resumable; select a model with " +
                                                     "'/models use <model-id> [--provider <profile>] [--effort <id>]' before sending.");
                    await composition.Access.BindSessionAsync(resumeStack.Permissions, token).ConfigureAwait(false);
                    return host;
                }

                resolved = target;
                // 启动客户端归组合根进程生命周期持有（借用）；目录解析构造的新客户端随宿主转交释放。
                host = new ChatSessionHost(restored.Session, restored.Context, target.Options, target.Client,
                                           resumeStack.Tools, systemPrompt, resumeStack.Permissions,
                                           resumeApprover,
                                           events, sessionWriter : writer,
                                           needsInspection : restored.NeedsInspection,
                                           instructionLoader : instructionLoader,
                                           knownSecrets : composition.KnownSecrets,
                                           ownsModelClient : !ReferenceEquals(target.Client, composition.Model),
                                           modelTimeouts : (target.Client as MicrosoftAiChatClient)
                                         ?.EffectiveTimeouts,
                                           compositionGuard : composition.CompositionGuard);

                // 恢复不经过会话内切换入口：把本次实际使用的客户端路由交给 Core 核对；
                // 不一致时按选择事务采纳当前路由并开启新 epoch，隔离旧路由产生的 opaque 推理。
                var recordedRoute = host.Session.ModelSelection!;
                var alignedRoute = await host.EnsureCredentialRouteAsync(target.CredentialRoute, token)
                                             .ConfigureAwait(false);
                if (target.Client is MicrosoftAiChatClient { NetworkRouteIdentity: { } networkRouteIdentity })
                    await host.EnsureNetworkRouteIdentityAsync(networkRouteIdentity, token).ConfigureAwait(false);
                if (target.RefreshedContext is { } refreshed)
                {
                    // 目标模型当前窗口与保存选择不同：使用前按选择事务追加新快照，不显示旧修订却发送新配置。
                    var current = host.Session.ModelSelection!;
                    await host.ChangeModelAsync(new SessionModelBinding(current.Model, target.Client, refreshed,
                                                                        current.ReasoningEffort,
                                                                        target.CredentialRoute, target.ProfileHint,
                                                                        target.CurrentCapabilities,
                                                                        false,
                                                                        (target.Client as MicrosoftAiChatClient)
                                                                      ?.NetworkRouteIdentity,
                                                                        (target.Client as MicrosoftAiChatClient)
                                                                      ?.EffectiveTimeouts),
                                                token, current.Revision).ConfigureAwait(false);
                    await Console.Out.WriteLineAsync(
                                                     $"  window    : the model's current context window ({refreshed.ContextWindowTokens:N0} tokens) " +
                                                     "was saved as a new selection revision");
                }

                if (target.SavedSelection is { } saved &&
                    (saved.Model           != identity                            ||
                     saved.ReasoningEffort != composition.Options.ReasoningEffort ||
                     saved.Context         != composition.Options.Context))
                {
                    var effort = saved.ReasoningEffort is { } level
                        ? ModelReasoningEffortParser.ToValueString(level)
                        : "default (service decides)";
                    await Console.Out.WriteLineAsync($"  selection : model {saved.Model.ModelId}, effort {effort} " +
                                                     "(restored from the saved session)");
                }

                // 降级组合没有启动路由可比；绑定成功时说明实际恢复到的路由来源。
                if (composition.CredentialRoute is { } startupRoute)
                {
                    if (!string.Equals(target.CredentialRoute, startupRoute, StringComparison.Ordinal))
                        await
                            Console.Out
                                   .WriteLineAsync("  route     : resumed with the session's saved credential route " +
                                                   $"'{target.CredentialRoute}' (startup default: "                   +
                                                   $"{startupRoute})");
                }
                else
                {
                    await
                        Console.Out.WriteLineAsync("  route     : resumed with the session's saved credential route " +
                                                   $"'{target.CredentialRoute}' (no startup credentials)");
                }

                if (alignedRoute.Revision != recordedRoute.Revision)
                    await Console.Out.WriteLineAsync("  route     : the credential route changed since this session " +
                                                     "was saved; the previous route's reasoning stays isolated from " +
                                                     "new requests");
                await composition.Access.BindSessionAsync(resumeStack.Permissions, token).ConfigureAwait(false);
                return host;
            }
            catch
            {
                // 已构造宿主拥有 writer 与已转交客户端；未构造时仅释放本方法构造的候选客户端与 writer。
                if (host is not null)
                {
                    host.Dispose();
                }
                else
                {
                    writer.Dispose();
                    if (resolved is not null && !ReferenceEquals(resolved.Client, composition.Model))
                        (resolved.Client as IDisposable)?.Dispose();
                }

                throw;
            }
        }

        using var console = new ChatSessionConsole(CreateHostAsync, reader, Console.Out, cancellation,
                                                   composition.KnownSecrets, ResumeHostAsync,
                                                   composition.SessionStoreDirectory,
                                                   resumeSessionId is null
                                                       ? null
                                                       : await ResumeHostAsync(resumeSessionId,
                                                                               cancellation.SessionToken),
                                                   !Console.IsInputRedirected);

        // 普通正文保留原文；所有诊断与审计出口共享本次进程使用过的密钥集合。

        async Task<IChatCompletionClient> CreateSelectionClientAsync(UserProfile       profile, string modelId,
                                                                     CancellationToken token)
        {
            var credentials = new WindowsCredentialStore();
            var apiKey = ApiKeyReader.Read(profile.ApiKeyEnvironmentVariable, profile.ApiKeyCredentialTarget,
                                           credentials, profile.Name);
            if (apiKey is null)
                throw new
                    ConfigException($"No API key found for provider '{profile.Name}'. Run 'tinyharness auth set {profile.Name}' or set the environment variable '{profile.ApiKeyEnvironmentVariable}' before switching.");

            selectionSecrets[Guid.NewGuid().ToString("N")] = apiKey;
            if (profile.ApiKeyEnvironmentVariable.Length > 0)
                selectionSecrets[profile.ApiKeyEnvironmentVariable] = apiKey;
            var user = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), token)
                                            .ConfigureAwait(false);
            return ModelClientFactory.Create(modelId, profile.Endpoint, apiKey, profile.ChatApi,
                                             user.Settings?.Network, profile.Network, credentials,
                                             user.Settings?.ModelTimeouts ?? ModelTimeouts.Default);
        }

        console.ModelSelection = draftSelector =
            new ChatModelSelection(reader, Console.Out,
                                   token => UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), token),
                                   CreateSelectionClientAsync, selectionSecrets, !Console.IsInputRedirected);
        draftSelector.ManageCatalog = token => CatalogWizard.MenuAsync(new CommandContext
        {
            Io          = new ChatCatalogConsole(reader, Console.Out),
            Credentials = new WindowsCredentialStore()
        }, token);
        await WriteHeaderAsync("TinyHarness chat", composition, console.Host?.Session.Id).ConfigureAwait(false);
        await
            Console.Out.WriteLineAsync("  storage   : append-only JSONL session log; protocol history is saved verbatim " +
                                       "without harness encryption, so pasted secrets may be stored. Keep the session store private.");

        console.WorkspaceTrustHandler = composition.Access.ChangeAsync;
        console.PermissionMigrationSummaryWriter = async token =>
        {
            var currentConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), token)
                                                     .ConfigureAwait(false);
            await WritePermissionMigrationSummaryAsync(
                                                       currentConfig, Console.Out,
                                                       "Current trusted sandbox/network settings (preserved independently):",
                                                       false)
               .ConfigureAwait(false);
        };
        var exit         = await console.RunAsync().ConfigureAwait(false);
        var attachedHost = console.Host;

        // 只报告存在的产物；末次保存不可靠时明确警告，不以终态代替保存证据。
        if (attachedHost?.PersistencePath is { } path && File.Exists(path))
            await Console.Out.WriteLineAsync($"session    : {path}");
        if (attachedHost?.PersistenceWarning is { Length: > 0 } warning)
            await Console.Out.WriteLineAsync($"warning    : {warning}");
        if (attachedHost?.DisplayWarning is { Length: > 0 } displayWarning)
            await Console.Out.WriteLineAsync($"display    : {displayWarning}");

        return exit;
    }

    /// <summary>
    ///     <para>确认旧用户权限配置迁移；仅在成功写入新形状后返回继续启动。</para>
    ///     <para>Confirms legacy user-permission migration and continues startup only after the new shape is saved.</para>
    /// </summary>
    private static async Task<bool> ConfirmConfigurationMigrationAsync(
        UserConfig baseline, TextReader input, CancellationToken cancellationToken)
    {
        if (baseline.Settings?.Permissions is not { PendingMigration: true }) return true;
        if (Console.IsInputRedirected)
        {
            await Console.Error.WriteLineAsync(
                                               "Permission configuration migration is pending; redirected input cannot confirm it. No settings were changed.");
            return false;
        }

        await WritePermissionMigrationSummaryAsync(baseline, Console.Out,
                                                   "Legacy permission configuration requires confirmation.", true)
           .ConfigureAwait(false);
        await Console.Out.WriteAsync("Rewrite the legacy permission settings and continue? [y/N] ");
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        var answer = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (answer is not ("y" or "Y" or "yes" or "YES"))
        {
            await Console.Out.WriteLineAsync("Configuration migration was not confirmed; no settings were changed.");
            return false;
        }

        var permissions = baseline.Settings.Permissions;
        var candidate = baseline with
        {
            Settings = baseline.Settings with
            {
                Permissions = permissions with
                {
                    LegacyDefaultMode = null,
                    LegacyComposerModes = null,
                    PendingMigration = false
                }
            }
        };
        var path = UserConfigStore.DefaultFilePath();
        await UserConfigStore.SaveAsync(path, candidate, baseline, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync("Legacy permission settings migrated successfully.").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     <para>
    ///         确认常设信任迁移：交互展示授权范围与有效期，y 落盘确认记录后继续启动；
    ///         非交互或拒绝即阻断启动，不改任何设置。
    ///     </para>
    ///     <para>
    ///         Confirms the standing-trust migration: interactive input shows the authorized scope and validity,
    ///         and 'y' persists the confirmation record before startup continues; non-interactive input or a
    ///         decline blocks startup without changing any settings.
    ///     </para>
    /// </summary>
    private static async Task<bool> ConfirmStandingTrustMigrationAsync(TextReader        input,
                                                                       CancellationToken cancellationToken)
    {
        var baseline = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cancellationToken)
                                            .ConfigureAwait(false);
        var sandbox = baseline.Settings?.WindowsSandbox;
        if (!StandingTrustEvaluation.HasStandingExpansion(sandbox)) return true;
        if (StandingTrustEvaluation.Evaluate(sandbox, baseline.Settings?.Permissions) ==
            StandingTrustState.Confirmed)
            return true;

        if (Console.IsInputRedirected)
        {
            await Console.Error.WriteLineAsync(
                                               "Standing sandbox trust (networkPolicy=enabled and/or additional write roots) requires explicit confirmation; " +
                                               "redirected input cannot confirm it. Startup is blocked; no settings were changed. " +
                                               "Run 'tinyharness chat' with interactive input to review and confirm it.");
            return false;
        }

        await WriteStandingTrustSummaryAsync(baseline, Console.Out).ConfigureAwait(false);
        await Console.Out
                     .WriteAsync("Confirm these standing settings as persistent authorization and continue? [y/N] ");
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        var answer = (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (answer is not ("y" or "Y" or "yes" or "YES"))
        {
            await Console.Out.WriteLineAsync(
                                             "Standing-trust confirmation was not given; no settings were changed and startup is blocked. " +
                                             "Re-run 'tinyharness chat' to confirm, or 'tinyharness run' for per-call approvals.");
            return false;
        }

        var permissions = (baseline.Settings?.Permissions ?? new PermissionSettings()) with
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                      StandingTrustEvaluation.ScopeRevision(sandbox!))
        };
        var candidate = baseline with
        {
            Settings = (baseline.Settings ?? new UserConfigSettings()) with { Permissions = permissions }
        };
        await UserConfigStore.SaveAsync(UserConfigStore.DefaultFilePath(), candidate, baseline, cancellationToken)
                             .ConfigureAwait(false);
        await Console.Out.WriteLineAsync("Standing trust confirmed; the settings now run as explicit persistent " +
                                         "authorization until revoked.")
                     .ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     <para>展示常设信任的授权范围、敏感位置提示、有效期与当前状态；不输出代理地址或凭据。</para>
    ///     <para>
    ///         Shows the standing-trust scope, the sensitive-location hint, the validity, and the current state;
    ///         proxy addresses and credentials are never printed.
    ///     </para>
    /// </summary>
    private static async Task WriteStandingTrustSummaryAsync(UserConfig user, TextWriter output)
    {
        var sandbox = user.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        await output.WriteLineAsync("Standing sandbox trust requires explicit confirmation.");
        await output.WriteLineAsync(
                                    "  scope    : networkPolicy=enabled — full sandbox network: through the configured proxy when one is set " +
                                    "(a broken proxy fails fast without direct fallback); direct full network when no proxy is configured");
        await output.WriteLineAsync("  write roots: " + FormatPaths(sandbox.AdditionalWriteRoots));
        // 附加读根随确认注入 OS 读面（profile 外真实可读），确认展示如实列出（M2）。
        await output.WriteLineAsync("  read roots : " + FormatPaths(sandbox.AdditionalReadRoots) +
                                    " — injected with the confirmed authorization (outside-profile roots become OS-readable)");
        await output.WriteLineAsync(
                                    "  note     : persistent roots may contain sensitive locations; until confirmation, " +
                                    "networking and additional-write-root writes follow per-call approval");
        await output.WriteLineAsync("  validity : persistent until revoked (clearing the settings revokes it)");
        await output.WriteLineAsync("  state    : awaiting confirmation");

        static string FormatPaths(IReadOnlyList<string> paths) =>
            paths is { Count: > 0 } ? string.Join(", ", paths) : "(none)";
    }

    /// <summary>
    ///     <para>非交互 run 入口的常设信任提示：未确认时联网与附加写根写按逐次审批运行。</para>
    ///     <para>
    ///         The non-interactive run notice for standing trust: until confirmation, networking and
    ///         additional-write-root writes follow per-call approval.
    ///     </para>
    /// </summary>
    private static async Task WriteStandingTrustNoticeAsync(UserConfig user, TextWriter output)
    {
        var sandbox = user.Settings?.WindowsSandbox;
        if (!StandingTrustEvaluation.HasStandingExpansion(sandbox)) return;
        if (StandingTrustEvaluation.Evaluate(sandbox, user.Settings?.Permissions) ==
            StandingTrustState.Confirmed)
            return;

        await output.WriteLineAsync(
                                    "note       : standing sandbox trust (networkPolicy=enabled and/or additional write roots) is not confirmed yet; " +
                                    "networking and additional-write-root writes follow per-call approval. " +
                                    "Run 'tinyharness chat' with interactive input to review and confirm it.");
    }

    /// <summary>
    ///     <para>展示旧权限迁移结果与保持独立的可信沙箱和网络设置，不输出代理地址或凭据。</para>
    ///     <para>
    ///         Shows the permission migration result and independent trusted sandbox/network settings without proxy
    ///         addresses or credentials.
    ///     </para>
    /// </summary>
    internal static async Task WritePermissionMigrationSummaryAsync(
        UserConfig user, TextWriter output, string heading, bool showLegacyMode)
    {
        var oldMode = user.Settings?.Permissions?.LegacyDefaultMode ?? "unknown";
        var mappedMode = user.Settings?.Permissions?.DefaultApprovalMode.ToString() ??
                         SessionApprovalMode.HumanApproval.ToString();
        await output.WriteLineAsync(heading);
        if (showLegacyMode)
            await output.WriteLineAsync($"  old default mode: {oldMode} -> {mappedMode} (migration mapping only)");
        await
            output.WriteLineAsync("  new baseline    : workspace read/write and sandbox Shell; add-on capabilities require approval");
        await
            output.WriteLineAsync("  removed         : FullAccess; old grants and commandRules do not become permissions");

        var sandbox       = user.Settings?.WindowsSandbox;
        var networkPolicy = sandbox?.NetworkPolicy ?? SandboxNetworkPolicy.Restricted;
        var standingAccess = networkPolicy == SandboxNetworkPolicy.Enabled
            ? "all sandbox commands retain standing full network access"
            : "external sandbox networking remains restricted; per-operation approval may grant it";
        await output.WriteLineAsync($"  networkPolicy   : {networkPolicy} — {standingAccess}");
        await output.WriteLineAsync("  additional read roots : " + FormatPaths(sandbox?.AdditionalReadRoots));
        await output.WriteLineAsync("  additional write roots: " + FormatPaths(sandbox?.AdditionalWriteRoots));

        var proxy = sandbox?.Proxy;
        var proxyDescription = proxy is null
            ? "FollowAppDefault"
            : proxy.Mode == SandboxProxyMode.Named
                ? $"{proxy.Mode} ({TerminalDisplay.Format(proxy.ProxyName ?? "unnamed")})"
                : proxy.Mode.ToString();
        await output.WriteLineAsync($"  sandbox proxy   : {proxyDescription} — retained independently");
        var network = user.Settings?.Network;
        if (network is not null)
        {
            var route = network.DefaultRoute;
            await output.WriteLineAsync($"  app network route: {route.Mode}" +
                                        (route.ProxyId is { Length: > 0 } proxyId
                                            ? $" (proxy id {TerminalDisplay.Format(proxyId)})"
                                            : string.Empty) + "; retained independently");
            var proxyNames = network.Proxies.Select(item => TerminalDisplay.Format(item.Name)).ToArray();
            await
                output.WriteLineAsync($"  app proxy entries: {(proxyNames.Length == 0 ? "(none)" : string.Join(", ", proxyNames))} — retained");
        }

        await
            output.WriteLineAsync("  only settings.permissions will be rewritten; all other user settings are preserved.");

        static string FormatPaths(IReadOnlyList<string>? paths) =>
            paths is { Count: > 0 } ? string.Join(", ", paths.Select(path => TerminalDisplay.Format(path))) : "(none)";
    }

    /// <summary>
    ///     <para>按可信配置解析 AI 审核模型；缺失或歧义时失败关闭，且不输出凭据。</para>
    ///     <para>
    ///         Resolves the AI reviewer from trusted configuration; missing or ambiguous targets fail closed without
    ///         exposing credentials.
    ///     </para>
    /// </summary>
    internal static async Task<(IChatCompletionClient Client, string Model)> CreateReviewerAsync(
        ChatSessionHost host, IReadOnlyDictionary<string, string> knownSecrets, CancellationToken cancellationToken,
        string?         userConfigPath = null, ICredentialStore? credentialStore = null)
    {
        var path     = userConfigPath ?? UserConfigStore.DefaultFilePath();
        var user     = await UserConfigStore.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        var settings = user.Settings?.Permissions ?? new PermissionSettings();
        settings.Validate();

        var target = ResolveReviewerTarget(user.Profiles, settings, host.Session.Model, host.Session.ModelSelection);

        if (target is not { } selected)
            throw new InvalidOperationException("The configured AI reviewer provider/model is missing or ambiguous.");

        var credentials = credentialStore ?? new WindowsCredentialStore();
        var apiKey = StartupModelCredentials.Resolve(selected.Profile.ApiKeyEnvironmentVariable,
                                                     selected.Profile.ApiKeyCredentialTarget,
                                                     selected.Profile.Name, credentials, false)
                  ?? throw new ConfigException("The configured AI reviewer has no API key.");
        if (knownSecrets is ConcurrentDictionary<string, string> secrets)
        {
            secrets[Guid.NewGuid().ToString("N")] = apiKey;
            if (!string.IsNullOrWhiteSpace(selected.Profile.ApiKeyEnvironmentVariable))
                secrets[selected.Profile.ApiKeyEnvironmentVariable] = apiKey;
        }

        return (ModelClientFactory.Create(selected.Model.Id, selected.Profile.Endpoint, apiKey,
                                          selected.Profile.ChatApi, user.Settings?.Network,
                                          selected.Profile.Network, credentials,
                                          user.Settings?.ModelTimeouts ?? ModelTimeouts.Default), selected.Model.Id);
    }

    /// <summary>
    ///     <para>
    ///         从可信目录解析 AI 审核目标；“使用当前模型”同时核对模型、endpoint 与协议身份，
    ///         与当前会话绑定的目标不一致时返回 null，由调用方失败关闭。
    ///     </para>
    ///     <para>
    ///         Resolves the AI reviewer target from the trusted catalog; "use current model" also matches the
    ///         endpoint and protocol identity, returning null on a mismatch so the caller fails closed.
    ///     </para>
    /// </summary>
    internal static (UserProfile Profile, UserProfileModel Model)? ResolveReviewerTarget(
        IReadOnlyList<UserProfile> profiles, PermissionSettings settings, SessionModelIdentity sessionModel,
        SessionModelSelection?     savedSelection)
    {
        if (!settings.UseCurrentModel)
        {
            var profile = profiles.SingleOrDefault(item => item.Name      == settings.ReviewerProfile);
            var model   = profile?.Models.SingleOrDefault(item => item.Id == settings.ReviewerModel);
            return profile is not null && model is not null ? (profile, model) : null;
        }

        var profileName = savedSelection?.ProfileHint;
        if (profileName is null && savedSelection?.CredentialRoute is { } route)
        {
            var separator                  = route.IndexOf('|');
            if (separator > 0) profileName = route[..separator];
        }

        if (profileName is not null)
        {
            var profile = profiles.SingleOrDefault(item => item.Name      == profileName);
            var model   = profile?.Models.SingleOrDefault(item => item.Id == sessionModel.ModelId);
            if (profile is null                                                                   || model is null ||
                !string.Equals(profile.Endpoint, sessionModel.Endpoint, StringComparison.Ordinal) ||
                profile.ChatApi != sessionModel.ChatApi)
                return null;

            return (profile, model);
        }

        var matches = profiles.SelectMany(profile => profile.Models.Select(model => (Profile : profile, Model : model)))
                              .Where(item => item.Model.Id         == sessionModel.ModelId  &&
                                             item.Profile.Endpoint == sessionModel.Endpoint &&
                                             item.Profile.ChatApi  == sessionModel.ChatApi)
                              .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>
    ///     <para>解析配置与凭据，组合 run 和 chat 共享的模型、工具、权限与执行策略。</para>
    ///     <para>
    ///         Resolves configuration and credentials and composes the model, tools,
    ///         permissions, and policy shared by run and chat.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>审批器与事件展示由入口组装；chat 审批器必须与回合输入共享读取器。</para>
    ///     <para>Entry points compose approvals and event display; chat approvals must share the turn-input reader.</para>
    /// </remarks>
    /// <param name="persistSessions">
    ///     <para>是否从可信用户设置解析并校验受保护会话存储目录。</para>
    ///     <para>Whether to resolve and validate the protected session-store directory from trusted user settings.</para>
    /// </param>
    /// <param name="input">
    ///     <para>工作区信任确认等宿主交互共用的输入读取器；为 null 时使用控制台标准输入。</para>
    ///     <para>
    ///         The shared reader for host interactions such as workspace-trust consent;
    ///         null falls back to the console standard input.
    ///     </para>
    /// </param>
    /// <param name="allowMissingModelCredentials">
    ///     <para>
    ///         供交互入口使用：启动默认模型或凭据缺失时不拒绝启动，改为组合出无客户端、无路由的降级组合，
    ///         由恢复绑定从日志最后保存的选择解析目标，或以未绑定宿主等待显式重选。
    ///     </para>
    ///     <para>
    ///         For interactive entries: a missing startup model or credential no longer rejects
    ///         startup — the composition degrades to no client and no route, letting the resume binding resolve
    ///         the target from the log's last saved selection or wait for explicit re-selection unbound.
    ///     </para>
    /// </param>
    /// <exception cref="ConfigException">
    ///     <para>模型或 endpoint 缺失，或无法解析 API key（未允许降级时）。</para>
    ///     <para>The model or endpoint is missing, or the API key cannot be resolved (when degradation is not allowed).</para>
    /// </exception>
    private static async Task<AgentComposition> ComposeAgentAsync(
        string? configPath, CancellationToken cancellationToken, bool persistSessions = false, TextReader? input = null,
        bool    allowMissingModelCredentials = false)
    {
        var resolution = await ConfigResolver.ResolveAsync(configPath, cancellationToken).ConfigureAwait(false);
        var config     = resolution.Config;

        if (!allowMissingModelCredentials && string.IsNullOrWhiteSpace(config.Model))
            throw new
                ConfigException("A model must be configured. Run 'tinyharness init' (user config) or set 'model' in the config file.");

        if (!allowMissingModelCredentials && string.IsNullOrWhiteSpace(config.Endpoint))
            throw new
                ConfigException("An endpoint must be configured. Run 'tinyharness init' (user config) or set 'endpoint' in the config file.");

        // 启动凭据解析统一经组合入口：降级允许时环境变量缺失与凭据存储条目缺失都归一为无客户端组合。
        var apiKey = string.IsNullOrWhiteSpace(config.Model) || string.IsNullOrWhiteSpace(config.Endpoint)
            ? null
            : StartupModelCredentials.Resolve(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                              resolution.ProfileName, new WindowsCredentialStore(),
                                              allowMissingModelCredentials);

        // 续聊存储仅取可信用户设置，不接受项目配置；保护校验失败即拒绝启动，不降级目录。
        var userConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cancellationToken)
                                              .ConfigureAwait(false);
        var defaultApprovalMode = userConfig.Settings?.Permissions?.DefaultApprovalMode ??
                                  SessionApprovalMode.HumanApproval;
        var networkProfile = resolution.ProfileName is null
            ? null
            : userConfig.Profiles.SingleOrDefault(profile => profile.Name == resolution.ProfileName)
           ?? throw new
                  ConfigException("The selected provider changed while the model client was being prepared; reload configuration and retry.");
        string? sessionStoreDirectory = null;
        if (persistSessions)
            sessionStoreDirectory = SessionStorePaths.ResolveProtected(userConfig.Settings?.SessionStoreDirectory,
                                                                       config.WorkspaceRoot);

        // 沙箱仅取可信用户设置，不接受项目配置；组件、路径、平台或存储保护校验失败均不回退宿主。
        var knownSecrets =
            new ConcurrentDictionary<string, string>(SandboxCliComposition.KnownSecrets(config, apiKey),
                                                     StringComparer.Ordinal);
        var settings = userConfig.Settings?.WindowsSandbox;
        var userConfigPath = UserConfigStore.DefaultFilePath();
        // 常设信任三态只来自可信用户设置与显式确认记录；未确认时组合与引擎都按逐次审批消费。
        var standingTrust = StandingTrustEvaluation.Evaluate(settings, userConfig.Settings?.Permissions);
        // 组合实际消费的设置决定常设网络是否启用：与传给沙箱组合器的 EffectiveSandboxSettings
        // 同源同值；常设网络 restricted 时网络申请走逐次审批，写根确认不得复用为网络免审批。
        var compositionSettings = StandingTrustEvaluation.EffectiveSandboxSettings(settings, standingTrust);
        var standingNetworkEnabled = compositionSettings?.NetworkPolicy == SandboxNetworkPolicy.Enabled;
        var accessState = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(config.WorkspaceRoot));
        var trustPath = Path.Combine(Path.GetDirectoryName(userConfigPath)!, "workspace-trust.json");
        IReadOnlyList<string> writeRoots = [config.WorkspaceRoot, .. settings?.AdditionalWriteRoots ?? []];
        // C1 权限系统实例（实际路径）：组合期写根重叠校验与 grant 校验共用，底层不重新猜默认路径。
        var permissionSystemPaths = new List<string> { trustPath, userConfigPath };
        if (sessionStoreDirectory is not null) permissionSystemPaths.Add(sessionStoreDirectory);
        // OS ACL 保护集保持整 home 不收缩（与组合期完整性保护同层）；应用层 C1 分类集把 home
        // 收缩为敏感子路径（凭据目录、deny 簿记、授权映射与执行组件目录），默认 tmp/tools/
        // nuget-cache 运行时根物理位于 home 下，不得被 C1 遮蔽。
        var protectedPaths        = new List<string>(permissionSystemPaths);
        var permissionSystemRoots = new List<string>(permissionSystemPaths);
        if (settings is { Enabled: true })
        {
            var sandboxHome = WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome,
                                                                          settings.AllowTcpLoopback);
            var components = new WindowsSandboxComponents
            {
                SetupExecutablePath  = settings.SetupExecutablePath,
                RunnerExecutablePath = settings.RunnerExecutablePath,
                SandboxHome          = sandboxHome,
                AllowTcpLoopback     = settings.AllowTcpLoopback
            };
            protectedPaths.Add(settings.SetupExecutablePath);
            protectedPaths.Add(settings.RunnerExecutablePath);
            protectedPaths.Add(sandboxHome);
            permissionSystemRoots.Add(settings.SetupExecutablePath);
            permissionSystemRoots.Add(settings.RunnerExecutablePath);
            permissionSystemRoots.AddRange(components.ProtectedHomeSubpaths);
        }

        // 文件工具与 Shell 的统一资源访问策略：权限系统保护路径与可信附加根集中分类；
        // 分类结果只作为注记进入准备计划，授权判定仍属于权限引擎。
        // 派生运行时写根待沙箱实际组合后注入（M3）：此处不预注入，未组合（未信任/组合失败）
        // 时 harness 基础设施目录分类回落 OutsideWorkspace → L2 Ask，fail 方向不变；
        // 组合前分类集保持整 home（与 OS ACL 层一致），组合后按细化集重建。
        var resourcePolicy = new ResourceAccessPolicy(config.WorkspaceRoot,
                                                      settings?.AdditionalReadRoots,
                                                      settings?.AdditionalWriteRoots,
                                                      permissionSystemRoots : protectedPaths,
                                                      standingTrust : standingTrust);

        // 活动授权登记表：引擎与 Shell 工具共享同一实例，派发复核与快照聚合看到同一状态。
        // sandboxExecutionAvailable 由组合结果决定：沙箱未组合即 Deny 进程执行，不回退宿主。
        var grants = new GrantRegistry();
        // 常设信任只来自可信用户设置与显式确认记录：Confirmed 之外联网与附加写根写一律逐次审批。
        var permissions = new PermissionEngine(config.WorkspaceRoot,
                                               workspaceReadOnly : () => accessState.IsReadOnly,
                                               standingTrust : standingTrust,
                                               sandboxExecutionAvailable : false,
                                               standingNetworkEnabled : standingNetworkEnabled,
                                               resourcePolicy : resourcePolicy,
                                               grantRegistry : grants,
                                               frozenScopeReader : FrozenCapabilityScopeReader.FromPreparation);

        WindowsSandboxExecution? sandbox = null;
        var access = new WorkspaceAccessCoordinator(accessState, new WorkspaceTrustStore(trustPath, writeRoots),
                                                    permissions, input ?? new ExclusiveInputReader(Console.In),
                                                    Console.Out, !Console.IsInputRedirected,
                                                    OperatingSystem.IsWindows() && settings is
                                                        { Enabled: true, Policy: SandboxPolicyKind.WorkspaceWrite },
                                                    new ProcessWorkspaceAclGrant(protectedPaths, writeRoots, settings),
                                                    activeGrants =>
                                                    {
                                                        // 组合门禁：基线读保护计划与沙箱同点组合（信任激活
                                                        // 之后）。trust.json 由信任激活先行创建，此时
                                                        // pairing fail-closed 校验的 C1 宿主密钥路径必然
                                                        // 存在；未信任工作区不进入本回调，组合保持为空
                                                        // （process.execute 走沙箱不可用 fail-closed）。
                                                        var baselineReadProtection =
                                                            WindowsSandboxComposer.PlanBaselineReadProtection(
                                                             [trustPath, UserConfigStore.DefaultFilePath()],
                                                             sessionStoreDirectory,
                                                             settings);
                                                        sandbox =
                                                            SandboxCliComposition.TryCompose(settings,
                                                                config.WorkspaceRoot, knownSecrets,
                                                                sessionStoreDirectory,
                                                                userConfig.Settings?.Network,
                                                                baselineReadProtection,
                                                                standingTrust,
                                                                permissionSystemPaths);
                                                        var policy = sandbox?.ExecutionPolicy ??
                                                                     ProcessExecutionPolicy.Host();
                                                        // 与 BuildTools 的 shell 槽位同构：沙箱已组合时注册
                                                        // PermissionShellTool（workspace 路由沙箱、host 显
                                                        // 式 L3），未组合回退裸 ShellTool 形态。
                                                        ITool workspaceShell;
                                                        if (sandbox is { } composed)
                                                        {
                                                            var restricted =
                                                                new ShellTool(new Workspace(config.WorkspaceRoot, true),
                                                                              config.DefaultToolTimeoutSeconds,
                                                                              knownSecrets,
                                                                              processBackend : composed.Backend,
                                                                              executionPolicy :
                                                                              composed.ExecutionPolicy,
                                                                              activeCapabilities : activeGrants,
                                                                              grantContext :
                                                                              new SandboxGrantContext(
                                                                               composed.Policy,
                                                                               composed.Components,
                                                                               permissionSystemPaths));
                                                            var hostShell =
                                                                new ShellTool(new Workspace(config.WorkspaceRoot, true),
                                                                              config.DefaultToolTimeoutSeconds,
                                                                              knownSecrets,
                                                                              executionPolicy :
                                                                              ProcessExecutionPolicy.Host());
                                                            workspaceShell =
                                                                new PermissionShellTool(restricted, hostShell);
                                                        }
                                                        else
                                                        {
                                                            workspaceShell =
                                                                new ShellTool(new Workspace(config.WorkspaceRoot, true),
                                                                              config.DefaultToolTimeoutSeconds,
                                                                              knownSecrets,
                                                                              processBackend :
                                                                              new HostProcessBackend(knownSecrets),
                                                                              executionPolicy : policy);
                                                        }

                                                        return Task.FromResult((workspaceShell, policy));
                                                    });
        await access.InitializeAsync(cancellationToken).ConfigureAwait(false);
        // M3：派生运行时写根仅在沙箱实际组合后注入资源策略（与 DesktopSessionFactory 同构），
        // 消除 OS 基线可写与应用层审批的错位；未组合（未信任/组合失败）时 harness 目录分类
        // 回落 OutsideWorkspace → L2 Ask，fail 方向不变。InitializeAsync 的信任激活已完成
        // 沙箱组合，此处按组合结果重建策略供引擎与工具集合共用；应用层 C1 分类集以细化后的
        // home 敏感子路径为准，运行时根不再被整 home 遮蔽。
        if (sandbox is not null && settings is { } composedSettings)
            resourcePolicy = new ResourceAccessPolicy(config.WorkspaceRoot,
                                                      composedSettings.AdditionalReadRoots,
                                                      composedSettings.AdditionalWriteRoots,
                                                      permissionSystemRoots : permissionSystemRoots,
                                                      sandboxRuntimeRoots :
                                                      WindowsSandboxComposer.DeriveRuntimeWriteRoots(
                                                       composedSettings),
                                                      standingTrust : standingTrust);
        permissions = new PermissionEngine(config.WorkspaceRoot,
                                           workspaceReadOnly : () => accessState.IsReadOnly,
                                           standingTrust : standingTrust,
                                           sandboxExecutionAvailable : sandbox is not null,
                                           standingNetworkEnabled : standingNetworkEnabled,
                                           resourcePolicy : resourcePolicy,
                                           grantRegistry : grants,
                                           frozenScopeReader : FrozenCapabilityScopeReader.FromPreparation);
        await access.BindSessionAsync(permissions, cancellationToken).ConfigureAwait(false);
        var executionPolicy = access.Policy;

        // 降级组合（恢复入口且凭据缺失）没有客户端与路由；其余执行栈照常组装。
        var model = apiKey is null
            ? null
            : ModelClientFactory.Create(config.Model, config.Endpoint, apiKey, config.ChatApi,
                                        userConfig.Settings?.Network, networkProfile?.Network,
                                        new WindowsCredentialStore(),
                                        userConfig.Settings?.ModelTimeouts ?? ModelTimeouts.Default);
        // 与 Desktop 同构：文件工具与 shell 槽位共用允许外部路径的 workspace。allowExternalPaths
        // 只放开 Prepare 期的词法越界拒绝，让越界读/敏感读/越界写到达权限引擎的 L1 广泛读基线
        // 与 L2 审批；实际授权仍由引擎与沙箱 OS 边界决定，不扩大 shell 工作目录以外的边界。
        // Same shape as Desktop: the file tools and the shell slot share an external-path-enabled
        // workspace. allowExternalPaths only removes Prepare's lexical escape refusal so
        // out-of-workspace reads, sensitive reads, and out-of-workspace writes reach the engine's
        // L1 broad-read baseline and L2 approval; authorization still belongs to the engine and
        // the sandbox OS boundary, and no boundary beyond that is widened.
        var workspace = new Workspace(config.WorkspaceRoot, true);
        var tools = BuildTools(workspace, config, apiKey, executionPolicy, sandbox?.Backend, knownSecrets, access,
                               resourcePolicy, grants, sandbox);
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            Budget                    = userConfig.Settings?.RunBudget ?? new RunBudgetLimits(),
            EnableWorkItems           = true,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens       = config.ContextWindowTokens,
                ReservedOutputTokens      = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold
            }
        };

        // 会话索引更新器随组合共享：writer 每次可靠提交后增量推进列表索引，故障不阻断提交。
        // The session-index updater is shared through the composition: writers advance the list index incrementally
        // after every durable commit, and index faults never block commits.
        var sessionIndexUpdater = sessionStoreDirectory is null
            ? null
            : new SessionIndexUpdater(new SessionIndexStore(sessionStoreDirectory));

        // 组合授权指纹守卫：捕获组合实际消费的可信用户配置指纹，并注入同一函数的取值器——run 与
        // chat 的宿主在每次发送/派发前重读该配置重算，失配或取值失败即撤销授权并闩死会话。
        // 权限、常设信任与沙箱边界始终来自该可信配置，项目配置不参与指纹。
        IReadOnlyList<string> baselineProtectedPaths = [trustPath, userConfigPath];
        var compositionGuard = new PermissionCompositionGuard(
                                                              PermissionCompositionFingerprint.Compute(settings,
                                                                  userConfig.Settings?.Permissions,
                                                                  userConfig.Settings?.Network,
                                                                  config.WorkspaceRoot,
                                                                  sandbox is not null, baselineProtectedPaths,
                                                                  sessionStoreDirectory is null
                                                                      ? null
                                                                      : userConfig.Settings
                                                                        ?.SessionStoreDirectory),
                                                              PermissionCompositionFingerprint
                                                                 .CreateEvaluator(userConfigPath, config.WorkspaceRoot,
                                                                      sandbox is not null, baselineProtectedPaths,
                                                                      sessionStoreDirectory is not null));

        return new AgentComposition(resolution, config,
                                    apiKey is null
                                        ? null
                                        : CredentialRoute.Describe(resolution.ProfileName,
                                                                   config.ApiKeyEnvironmentVariable,
                                                                   config.ApiKeyCredentialTarget),
                                    knownSecrets, sandbox, settings, permissionSystemPaths, permissionSystemRoots,
                                    standingNetworkEnabled, executionPolicy, model, tools,
                                    permissions, defaultApprovalMode, options,
                                    sessionStoreDirectory, access, sessionIndexUpdater, standingTrust,
                                    compositionGuard);
    }

    /// <summary>
    ///     <para>打印配置、模型、工作区、沙箱与工具头部；提供会话 ID 时一并显示。</para>
    ///     <para>
    ///         Prints the configuration, model, workspace, sandbox, and tool header;
    ///         includes the session ID when supplied.
    ///     </para>
    /// </summary>
    private static async Task WriteHeaderAsync(string title, AgentComposition composition, string? sessionId = null)
    {
        var resolution = composition.Resolution;
        var config     = composition.Config;

        await Console.Out.WriteLineAsync(title);
        if (sessionId is not null) await Console.Out.WriteLineAsync($"  session   : {sessionId}");

        await Console.Out.WriteLineAsync($"  config    : {resolution.Source}" +
                                         (resolution.SourcePath is null
                                             ? string.Empty
                                             : $" ({resolution.SourcePath})"));
        await Console.Out.WriteLineAsync($"  model     : {config.Model}");
        await Console.Out.WriteLineAsync($"  endpoint  : {config.Endpoint}");
        await Console.Out.WriteLineAsync($"  chat api  : {ChatApiKindParser.ToValueString(config.ChatApi)}");
        await Console.Out.WriteLineAsync($"  workspace : {config.WorkspaceRoot}");
        await Console.Out.WriteLineAsync(composition.Sandbox is null
                                             ? $"  sandbox   : {composition.ExecutionPolicy.DisplayName}"
                                             : $"  sandbox   : {composition.ExecutionPolicy.DisplayName}; home: {composition.Sandbox.Components.SandboxHome}");
        if (composition.Sandbox is null && composition.SandboxSettings is { Enabled: true })
            await Console.Out.WriteLineAsync(
                                             "  note      : the sandbox is enabled in settings but was not composed (workspace not trusted or composition failed); " +
                                             "the shell runs as the bare host form, process execution is denied fail-closed, and no host fallback is implied.");
        await
            Console.Out.WriteLineAsync("  tools     : list_files, search_text, read_file, apply_patch, shell, work_items");
    }

    /// <summary>
    ///     <para>运行本机 stdio MCP 服务端；stdout 仅承载协议，诊断写入 stderr。</para>
    ///     <para>Runs the local stdio MCP server; stdout carries only protocol messages and stderr carries diagnostics.</para>
    /// </summary>
    /// <remarks>
    ///     <para>stdin 关闭或 Ctrl+C 均正常退出，不向协议输出写入取消提示。</para>
    ///     <para>Closed stdin and Ctrl+C both exit cleanly without writing cancellation notices to protocol output.</para>
    /// </remarks>
    private static async Task<int> RunMcpServerAsync()
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var       credentials        = new WindowsCredentialStore();
        var hostConfig = await McpWorkerHostConfiguration.ResolveAsync(cts.Token, credentialStore : credentials)
                                                         .ConfigureAwait(false);
        var model = ModelClientFactory.Create(hostConfig.Model, hostConfig.Endpoint,
                                              hostConfig.ApiKey, hostConfig.ChatApi,
                                              hostConfig.Network, hostConfig.ProviderNetwork, credentials,
                                              hostConfig.ModelTimeouts);
        using var ownedModel = model as IDisposable;
        var       runner     = new WorkerRunner(model, hostConfig.WorkspaceRoot, hostConfig.ExecutionOptions);

        // 启动摘要不含凭据；stderr 提醒用户获准读取的文件内容会发送到模型 endpoint。
        await Console.Error.WriteLineAsync($"TinyHarness MCP worker ready: model={hostConfig.Model}; "        +
                                           $"endpointType={hostConfig.EndpointType}; "                        +
                                           $"chatApi={ChatApiKindParser.ToValueString(hostConfig.ChatApi)}; " +
                                           $"workspace={hostConfig.WorkspaceRoot}");
        await Console.Error.WriteLineAsync("Read-permitted file contents are sent to the selected model endpoint; " +
                                           "the worker cannot modify files or run commands.");

        // MCP stdio 要求 UTF-8；直接包装字节流并禁用 BOM，避开 Windows Console 的 OEM/ANSI 代码页。
        using var input = new StreamReader(Console.OpenStandardInput(),
                                           new UTF8Encoding(false),
                                           false);
        await using var output = new StreamWriter(Console.OpenStandardOutput(),
                                                  new UTF8Encoding(false));
        var server = new McpStdioServer(input, output, runner.RunAsync);

        try
        {
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>
    ///     <para>把完整的只读与写入工具集合绑定到同一个工作区。</para>
    ///     <para>Binds the complete read/write tool set to one workspace.</para>
    /// </summary>
    /// <param name="sandbox">
    ///     <para>
    ///         已组合的沙箱执行要素；非空时 shell 槽位按 Desktop 组装形态注册
    ///         <see cref="PermissionShellTool" />（workspace 路由沙箱、host 路由显式 L3），
    ///         null 时回退裸 ShellTool 形态并由入口如实提示。
    ///     </para>
    ///     <para>
    ///         The composed sandbox execution; when present the shell slot registers a
    ///         <see cref="PermissionShellTool" /> in the Desktop assembly shape (workspace routes to the
    ///         sandbox, host is the explicit L3 request), and when absent it falls back to the bare ShellTool
    ///         form with an honest entry-point notice.
    ///     </para>
    /// </param>
    private static ToolRegistry BuildTools(Workspace workspace, TinyHarnessConfig config, string? apiKey = null,
                                           ProcessExecutionPolicy? executionPolicy = null,
                                           IProcessExecutionBackend? processBackend = null,
                                           IReadOnlyDictionary<string, string>? knownSecrets = null,
                                           WorkspaceAccessCoordinator? access = null,
                                           ResourceAccessPolicy? resourcePolicy = null,
                                           IActiveCapabilitySource? activeCapabilities = null,
                                           WindowsSandboxExecution? sandbox = null,
                                           IReadOnlyList<string>? permissionSystemPaths = null)
    {
        var secrets = knownSecrets ?? SandboxCliComposition.KnownSecrets(config, apiKey);
        // 沙箱已组合：shell 槽位与 Desktop 同构（workspace 沙箱路由 + host 显式 L3 申请）；
        // 未组合：裸 ShellTool 形态，进程执行由引擎的沙箱不可用 fail-closed 拒绝，不回退宿主。
        ITool shell = sandbox is { } composed
            ? new PermissionShellTool(
                                      new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                                                    processBackend : composed.Backend,
                                                    executionPolicy : composed.ExecutionPolicy,
                                                    activeCapabilities : activeCapabilities,
                                                    grantContext : new SandboxGrantContext(composed.Policy,
                                                        composed.Components, permissionSystemPaths)),
                                      new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                                                    executionPolicy : ProcessExecutionPolicy.Host()))
            : new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                            processBackend : processBackend ?? new HostProcessBackend(secrets),
                            executionPolicy : executionPolicy,
                            // 快照源仅在沙箱身份就绪时接入：host/无身份形态下 source 会触发
                            // fail-closed 组合错误，而这里语义是"沙箱不可用"而非组合错误。
                            activeCapabilities :
                            executionPolicy?.Backend == ProcessExecutionBackendKind.WindowsSandbox
                                ? activeCapabilities
                                : null);
        ITool[] tools =
        [
            new ListFilesTool(workspace, resourcePolicy : resourcePolicy),
            new SearchTextTool(workspace, resourcePolicy : resourcePolicy),
            new ReadFileTool(workspace, resourcePolicy : resourcePolicy),
            new ApplyPatchTool(workspace, resourcePolicy),
            shell
        ];
        return new ToolRegistry(access is null
                                    ? tools
                                    : tools.Select(tool => access.State.Guard(() => tool)));
    }

    /// <summary>
    ///     <para>为单个 chat 宿主构造隔离的授权登记表、资源策略与工具集合。</para>
    ///     <para>Builds an isolated grant registry, resource policy, and tool set for one chat host.</para>
    /// </summary>
    internal static ChatExecutionStack CreateChatExecutionStack(AgentComposition composition)
    {
        var grants   = new GrantRegistry();
        var settings = composition.SandboxSettings;
        // 派生运行时写根仅在沙箱已组合时回注，消除 OS 基线可写与应用层审批的错位；
        // 应用层 C1 分类集取细化后的 home 敏感子路径（PermissionSystemRoots），运行时根不被遮蔽。
        var resourcePolicy = new ResourceAccessPolicy(composition.Config.WorkspaceRoot,
                                                      settings?.AdditionalReadRoots,
                                                      settings?.AdditionalWriteRoots,
                                                      permissionSystemRoots : composition.PermissionSystemRoots,
                                                      sandboxRuntimeRoots : composition.Sandbox is not null
                                                          ? WindowsSandboxComposer.DeriveRuntimeWriteRoots(settings)
                                                          : null,
                                                      standingTrust : composition.StandingTrust);
        // 常设网络免审批只对应"常设网络已启用 + Confirmed"；restricted 时网络申请恒逐次审批。
        var permissions = new PermissionEngine(composition.Config.WorkspaceRoot,
                                               workspaceReadOnly : () => composition.Access.State.IsReadOnly,
                                               standingTrust : composition.StandingTrust,
                                               sandboxExecutionAvailable : composition.Sandbox is not null,
                                               standingNetworkEnabled : composition.StandingNetworkEnabled,
                                               resourcePolicy : resourcePolicy,
                                               grantRegistry : grants,
                                               frozenScopeReader : FrozenCapabilityScopeReader.FromPreparation);
        // 与 Desktop 同构（DesktopSessionFactory 同一形态）：外部路径在 Prepare 放开词法拒绝，
        // 越界读/敏感读/越界写到达引擎的 L1 基线与 L2 审批，授权仍由引擎与沙箱 OS 边界决定。
        // Same shape as Desktop (DesktopSessionFactory): external paths remove Prepare's lexical
        // refusal so out-of-workspace reads, sensitive reads, and writes reach the engine's L1
        // baseline and L2 approval; authorization still belongs to the engine and the sandbox OS
        // boundary.
        var tools = BuildTools(new Workspace(composition.Config.WorkspaceRoot, true), composition.Config,
                               knownSecrets : composition.KnownSecrets,
                               executionPolicy : composition.ExecutionPolicy,
                               processBackend : composition.Sandbox?.Backend,
                               access : composition.Access,
                               resourcePolicy : resourcePolicy,
                               activeCapabilities : grants,
                               sandbox : composition.Sandbox,
                               permissionSystemPaths : composition.PermissionSystemPaths);
        return new ChatExecutionStack(permissions, tools, grants, resourcePolicy);
    }

    /// <summary>
    ///     <para>通过共享的 <see cref="SandboxCliComposition" /> 从可信用户设置组合 Windows 沙箱。</para>
    ///     <para>
    ///         Composes the Windows sandbox from trusted user settings through the shared
    ///         <see cref="SandboxCliComposition" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>显式启用后的组合失败向上传播，由入口报告失败；绝不回退宿主执行。</para>
    ///     <para>
    ///         Composition failures after explicit enablement propagate to the entry point; host execution is never a
    ///         fallback.
    ///     </para>
    /// </remarks>
    /// <returns>
    ///     <para>启用时返回沙箱执行要素；未启用时返回 null，使用明确标注无 OS 隔离的宿主执行。</para>
    ///     <para>
    ///         Sandbox execution components when enabled; otherwise null for host execution
    ///         explicitly marked as having no OS isolation.
    ///     </para>
    /// </returns>
    private static async Task<WindowsSandboxExecution?> TryComposeWindowsSandboxAsync(
        TinyHarnessConfig config, IReadOnlyDictionary<string, string> knownSecrets, CancellationToken cancellationToken,
        string?           sessionStoreDirectory = null)
    {
        var userConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cancellationToken)
                                              .ConfigureAwait(false);
        var standingTrust = StandingTrustEvaluation.Evaluate(userConfig.Settings?.WindowsSandbox,
                                                             userConfig.Settings?.Permissions);
        return SandboxCliComposition.TryCompose(userConfig.Settings?.WindowsSandbox, config.WorkspaceRoot,
                                                knownSecrets, sessionStoreDirectory,
                                                userConfig.Settings?.Network, null, standingTrust);
    }

    /// <summary>
    ///     <para>使用脚本模型与临时工作区，离线检查读写工具闭环、审批流程与会话索引读回。</para>
    ///     <para>
    ///         Uses a scripted model and disposable workspace to exercise the read/write tool loop,
    ///         approval flow, and session-index read-back offline.
    ///     </para>
    /// </summary>
    private static async Task<int> RunSmokeAsync(string? configPath)
    {
        using var cts                = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var       resolution         = await ConfigResolver.ResolveAsync(configPath, cts.Token).ConfigureAwait(false);
        var       config             = resolution.Config;

        await Console.Out.WriteLineAsync("TinyHarness smoke");
        await Console.Out.WriteLineAsync($"  model        : {config.Model}");

        if (string.IsNullOrWhiteSpace(config.Model))
        {
            await Console.Error
                         .WriteLineAsync("A model must be configured. Run 'tinyharness init' (user config) or set 'model' in the config file.");
            return 1;
        }

        using var fixture = await SmokeWorkspace.CreateAsync(cts.Token).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"  fixtureWs    : {fixture.Root}");

        var plan           = SmokeScript.Build(ProcessProbeCommand(), fixture.Root);
        var script         = new SmokeScriptClient(plan.Steps);
        var workspace      = new Workspace(fixture.Root);
        var resourcePolicy = new ResourceAccessPolicy(fixture.Root);
        // 离线 smoke 与生产的显式差异（B7 声明）：shell 槽位同样注册 PermissionShellTool
        // （与生产同构的 workspace/host 路由形态），但 restricted 是脚本后端的裸 ShellTool、
        // 引擎按 sandboxExecutionAvailable:true 组合——smoke 不证明机器沙箱可用，也不携带
        // 沙箱 grant 上下文；生产路径的 restricted 由真实沙箱组合要素构建。
        var tools = new ToolRegistry(BuildTools(workspace, config, resourcePolicy : resourcePolicy).Values
                                        .Select(tool => tool is ShellTool shell
                                                    ? new PermissionShellTool(shell,
                                                                              new ShellTool(workspace,
                                                                                  config.DefaultToolTimeoutSeconds,
                                                                                  executionPolicy :
                                                                                  ProcessExecutionPolicy.Host()))
                                                    : tool));
        // 固定脚本显式申请 Host，覆盖单次审批；这是离线测试组合，不证明机器沙箱可用。
        var permissions = new PermissionEngine(fixture.Root, sandboxExecutionAvailable : true,
                                               resourcePolicy : resourcePolicy);
        var approver = new SmokeApprover();
        var options = new AgentOptions
        {
            Model                     = config.Model,
            MaxAgentSteps             = config.MaxAgentSteps,
            DefaultToolTimeoutSeconds = config.DefaultToolTimeoutSeconds,
            Context = new ContextOptions
            {
                ContextWindowTokens       = config.ContextWindowTokens,
                ReservedOutputTokens      = config.ReservedOutputTokens,
                CompactionThresholdTokens = config.CompactionThreshold
            }
        };

        var sessionDirectory = Path.Combine(Path.GetTempPath(), $"tinyharness-smoke-session-{Guid.NewGuid():N}");
        // 与 run/chat 相同接入会话索引更新器；提交通知的失败会被 writer 静默吞掉（含缺 SQLite 原生库的
        // DllNotFoundException），索引是否真正可用只能靠下方对索引行的读回验证暴露。
        var sessionIndexUpdater = new SessionIndexUpdater(new SessionIndexStore(sessionDirectory));
        const string systemPrompt = WorkspacePrompts.Current;
        AgentResult result;
        string? persistenceWarning;
        string? sessionId;
        string? sessionLogPath;
        var instructionLoader = new ProjectInstructionLoader(workspace);
        var initialInstructions = await instructionLoader.LoadAsync(null, [], cts.Token).ConfigureAwait(false);
        using (var host = new ChatSessionHost(fixture.Root,
                                              new SessionModelIdentity(config.Model,
                                                                       config.Endpoint ?? "offline-smoke://local",
                                                                       config.ChatApi),
                                              options,
                                              script,
                                              tools,
                                              systemPrompt,
                                              permissions,
                                              approver,
                                              sessionWriterFactory : sessionId =>
                                                  new SessionStoreWriter(sessionDirectory, sessionId, null,
                                                                         sessionIndexUpdater.HandleBatchCommittedAsync),
                                              instructionLoader : instructionLoader,
                                              initialInstructions : initialInstructions))
        {
            result = (await host.SubmitTurnAsync("smoke: inspect the fixture workspace and apply one patch.",
                                                 cts.Token).ConfigureAwait(false)).Result!;
            persistenceWarning = host.PersistenceWarning;
            sessionId          = host.Session.Id;
            sessionLogPath     = host.PersistencePath;
        }

        await Console.Out.WriteLineAsync($"  status       : {result.Status}");
        await Console.Out.WriteLineAsync($"  steps        : {result.Steps}");
        await Console.Out.WriteLineAsync($"  toolExecs    : {result.ToolExecutions}");
        await Console.Out.WriteLineAsync($"  finalMessage : {result.FinalMessage}");
        if (persistenceWarning is { Length: > 0 })
            await Console.Out.WriteLineAsync($"  warning      : {persistenceWarning}");
        if (!string.IsNullOrWhiteSpace(result.Error))
            await Console.Out.WriteLineAsync($"  error        : {result.Error}");

        if (result.Status != AgentStatus.Completed ||
            persistenceWarning is not null         ||
            sessionId is null                      ||
            sessionLogPath is null)
        {
            TryDeleteSmokeDirectory(sessionDirectory);
            return 1;
        }

        try
        {
            var logFiles = Directory.Exists(sessionDirectory)
                ? Directory.GetFiles(sessionDirectory, "*" + SessionStoreWriter.LogFileSuffix)
                : [];
            if (logFiles.Length != 1 ||
                !string.Equals(Path.GetFullPath(logFiles[0]),
                               Path.GetFullPath(sessionLogPath),
                               StringComparison.OrdinalIgnoreCase))
                throw new SessionStoreException("Smoke expected one authoritative session JSONL file.");

            var restoredSnapshot = await SessionStoreReader.ReadAsync(sessionDirectory, sessionId, cts.Token)
                                                           .ConfigureAwait(false)
                                ?? throw new SessionStoreException("Smoke session log could not be read back.");
            var modelIdentity = new SessionModelIdentity(config.Model, config.Endpoint ?? "offline-smoke://local",
                                                         config.ChatApi);
            var restoration = SessionResume.Restore(restoredSnapshot, fixture.Root, modelIdentity, systemPrompt,
                                                    options.Context);
            if (restoration.Context.BuildModelView()[0].Content != WorkspacePrompts.Current)
                throw new SessionStoreException("Smoke did not retain the current workspace behavior prompt.");
            var historyReader = new SessionHistoryReader();
            var historyPage = await historyReader.ReadPageAsync(sessionDirectory, sessionId,
                                                                SessionHistoryReader.MaxPageItems, null, cts.Token)
                                                 .ConfigureAwait(false);
            if (historyPage.Warning is { Length: > 0 } firstPageWarning)
                throw new SessionStoreException($"Smoke history read was incomplete: {firstPageWarning}");
            var history = historyPage.Items.ToList();
            while (historyPage.NextCursor is { } cursor)
            {
                historyPage = await historyReader.ReadPageAsync(sessionDirectory, sessionId,
                                                                SessionHistoryReader.MaxPageItems, cursor, cts.Token)
                                                 .ConfigureAwait(false);
                if (historyPage.Warning is { Length: > 0 } pageWarning)
                    throw new SessionStoreException($"Smoke history read was incomplete: {pageWarning}");
                history.AddRange(historyPage.Items);
            }

            var toolItems = history.Where(item => item.Kind == SessionHistoryKind.Tool).ToArray();
            var confirmedDiff = toolItems.SelectMany(item => item.PatchChanges ?? [])
                                         .Any(change => change.State == PatchChange.ConfirmedState &&
                                                        !string.IsNullOrWhiteSpace(change.Diff));
            if (restoration.Session.Id != sessionId                   ||
                toolItems.Length       != plan.ExpectedToolExecutions ||
                !confirmedDiff)
                throw new
                    SessionStoreException("Smoke journal restore/history did not contain the expected tools and confirmed patch diff.");

            var legacyArtifacts = Directory.GetFiles(sessionDirectory, "*.session.json")
                                           .Concat(Directory.GetFiles(sessionDirectory, "*.audit.jsonl")).ToArray();
            if (legacyArtifacts.Length != 0)
                throw new
                    SessionStoreException("Smoke found a legacy snapshot or separate audit sidecar beside the journal.");

            if (restoration.Context.ProjectInstructions?.Revision != initialInstructions.Revision)
                throw new SessionStoreException("Smoke did not restore the initial project instruction sources.");
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"),
                                         "Smoke refreshed project rules: preserve confirmed file facts.\n", cts.Token)
                      .ConfigureAwait(false);
            using (var resumedWriter = await SessionStoreWriter.OpenExistingAsync(sessionDirectory, sessionId,
                       null, cts.Token, sessionIndexUpdater.HandleBatchCommittedAsync).ConfigureAwait(false))
            using (var refreshedHost = new ChatSessionHost(restoration.Session, restoration.Context, options,
                                                           new SmokeScriptClient([new SmokeStep()]), tools,
                                                           systemPrompt, permissions, approver,
                                                           sessionWriter : resumedWriter,
                                                           instructionLoader : instructionLoader))
            {
                var refreshRun = await refreshedHost
                                      .SubmitTurnAsync("smoke: verify refreshed project instructions.", cts.Token)
                                      .ConfigureAwait(false);
                if (refreshRun.Result?.Status != AgentStatus.Completed ||
                    refreshedHost.PersistenceWarning is not null       ||
                    restoration.Context.ProjectInstructions?.Revision == initialInstructions.Revision)
                    throw new SessionStoreException("Smoke did not reliably refresh resumed project instructions.");
            }

            var refreshedSnapshot = await SessionStoreReader.ReadAsync(sessionDirectory, sessionId, cts.Token)
                                                            .ConfigureAwait(false)
                                 ?? throw new SessionStoreException("Smoke refreshed log could not be read.");
            var refreshedRestore = SessionResume.Restore(refreshedSnapshot, fixture.Root, modelIdentity, systemPrompt,
                                                         options.Context);
            if (refreshedRestore.Context.ProjectInstructions?.Revision !=
                restoration.Context.ProjectInstructions?.Revision)
                throw new SessionStoreException("Smoke refreshed instruction sources did not survive another restore.");
            await
                Console.Out
                       .WriteLineAsync("  instructions : verified initial load, update and resumed source integrity");

            await
                Console.Out.WriteLineAsync("  session log  : verified read, restore, history and confirmed patch diff");
            await Console.Out.WriteLineAsync("  prompt       : verified current behavior prompt after journal restore");
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or SessionStoreException
                                      or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync($"  session log verification failed: {ex.Message}");
            TryDeleteSmokeDirectory(sessionDirectory);
            return 1;
        }

        // 索引行读回是发布产物的硬门槛：原生库缺失或 schema 建库失败时，writer 只会静默记录，
        // 这里必须把缺口转成 smoke 失败，而不是留给列表投影降级成全量扫描。
        // 原生加载失败可能以任意异常类型出现（如包裹 DllNotFoundException 的 TypeInitializationException），
        // 除取消外一律按 smoke 失败处理。
        try
        {
            var indexRow = await new SessionIndexStore(sessionDirectory).GetRowAsync(sessionId, cts.Token)
                                                                        .ConfigureAwait(false);
            if (indexRow is null         ||
                indexRow.Summary is null ||
                !string.Equals(indexRow.Id, sessionId, StringComparison.OrdinalIgnoreCase))
                throw new SessionStoreException("Smoke session was not reflected in the SQLite index.");
            await Console.Out.WriteLineAsync("  session index: verified SQLite summary row for the smoke session");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Console.Error.WriteLineAsync($"  session index verification failed: {ex.Message}");
            TryDeleteSmokeDirectory(sessionDirectory);
            return 1;
        }

        // 脚本客户端拒绝工具闭环或内容不匹配；还需核对工具与审批次数，防止跳过补丁或进程执行。
        var passed = result.ToolExecutions == plan.ExpectedToolExecutions &&
                     approver.Prompts      == plan.ExpectedApprovalPrompts;
        if (!passed)
            await Console.Error.WriteLineAsync(
                                               $"Smoke expected {plan.ExpectedToolExecutions} tool executions and {plan.ExpectedApprovalPrompts} approvals; " +
                                               $"observed {result.ToolExecutions} executions and {approver.Prompts} approvals.");
        if (passed)
        {
            await M15OfflineSmoke.RunAsync(fixture.Root, sessionDirectory,
                                           new SessionModelIdentity(config.Model,
                                                                    config.Endpoint ?? "offline-smoke://local",
                                                                    config.ChatApi), cts.Token)
                                 .ConfigureAwait(false);
            await
                Console.Out
                       .WriteLineAsync("  m15          : control/work/budget/restore passed (separate offline session)");
            await ModelResponseOfflineSmoke.RunAsync(fixture.Root, sessionDirectory,
                                                     new SessionModelIdentity(config.Model,
                                                                              config.Endpoint ??
                                                                              "offline-smoke://local",
                                                                              config.ChatApi), cts.Token)
                                           .ConfigureAwait(false);
            await Console.Out.WriteLineAsync(
                                             "  responses    : empty/termination/failure/restore/continue/timing/cache/cancel passed (offline)");
        }

        TryDeleteSmokeDirectory(sessionDirectory);
        return passed ? 0 : 1;
    }

    private static void TryDeleteSmokeDirectory(string path)
    {
        var target         = Path.GetFullPath(path);
        var expectedPrefix = Path.GetFullPath(Path.GetTempPath());
        if (!target.StartsWith(expectedPrefix, OperatingSystem.IsWindows()
                                   ? StringComparison.OrdinalIgnoreCase
                                   : StringComparison.Ordinal) ||
            !Path.GetFileName(target)
                 .StartsWith("tinyharness-smoke-session-",
                             StringComparison.Ordinal))
            throw new InvalidOperationException($"Refusing to delete unexpected smoke session directory '{target}'.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    /// <summary>
    ///     <para>构造当前 CLI 隐藏 probe 命令的结构化调用，兼容依赖框架与原生入口。</para>
    ///     <para>Builds a structured invocation of this CLI's hidden probe command for framework-dependent and native hosts.</para>
    /// </summary>
    private static ProcessProbe ProcessProbeCommand()
    {
        var executable = Environment.ProcessPath
                      ?? throw new InvalidOperationException("Cannot determine the current process executable.");
        var entryAssembly = Environment.GetCommandLineArgs()[0];
        var isDotnetHost = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet",
                                         StringComparison.OrdinalIgnoreCase);
        return isDotnetHost
            ? new ProcessProbe(executable, [entryAssembly, "process-smoke-child"])
            : new ProcessProbe(executable, ["process-smoke-child"]);
    }

    /// <summary>
    ///     <para>run 与 chat 共享的执行栈组合结果，不含审批器与事件展示。</para>
    ///     <para>The execution-stack composition shared by run and chat, excluding approvals and event display.</para>
    /// </summary>
    /// <remarks>
    ///     <para>chat 携带受保护会话存储目录；未启用持久化时该目录为 null。</para>
    ///     <para>Chat carries the protected session-store directory; it is null when persistence is disabled.</para>
    ///     <para>
    ///         PermissionSystemPaths 是 C1 实例路径（组合期写根与 grant 校验用）；PermissionSystemRoots
    ///         是应用层分类集（home 已收缩为敏感子路径，运行时根不被遮蔽）；两者语义不同不得混用。
    ///     </para>
    ///     <para>
    ///         PermissionSystemPaths are the C1 instance paths (for composition-time write-root and grant
    ///         validation); PermissionSystemRoots is the application-layer classification set (the home
    ///         refined to its sensitive subpaths so runtime roots stay reachable); the two semantics must
    ///         never be mixed.
    ///     </para>
    /// </remarks>
    internal sealed record AgentComposition(
        ConfigResolution                    Resolution,
        TinyHarnessConfig                   Config,
        string?                             CredentialRoute,
        IReadOnlyDictionary<string, string> KnownSecrets,
        WindowsSandboxExecution?            Sandbox,
        WindowsSandboxSettings?             SandboxSettings,
        IReadOnlyList<string>               PermissionSystemPaths,
        IReadOnlyList<string>               PermissionSystemRoots,
        bool                                StandingNetworkEnabled,
        ProcessExecutionPolicy              ExecutionPolicy,
        IChatCompletionClient?              Model,
        ToolRegistry                        Tools,
        PermissionEngine                    Permissions,
        SessionApprovalMode                 DefaultApprovalMode,
        AgentOptions                        Options,
        string?                             SessionStoreDirectory,
        WorkspaceAccessCoordinator          Access,
        SessionIndexUpdater?                SessionIndexUpdater,
        StandingTrustState                  StandingTrust    = StandingTrustState.None,
        PermissionCompositionGuard?         CompositionGuard = null);

    internal sealed record ChatExecutionStack(
        PermissionEngine     Permissions,
        ToolRegistry         Tools,
        GrantRegistry        Grants,
        ResourceAccessPolicy ResourcePolicy);

    /// <summary>
    ///     <para>将 Ctrl+C 转换为任务取消；释放时移除全局事件处理器。</para>
    ///     <para>Converts Ctrl+C into task cancellation and detaches the global handler on disposal.</para>
    /// </summary>
    private sealed class ConsoleCancellation : IDisposable
    {
        private readonly ConsoleCancelEventHandler _handler;
        private readonly CancellationTokenSource   _source;

        public ConsoleCancellation(CancellationTokenSource source)
        {
            _source                =  source;
            _handler               =  OnCancel;
            Console.CancelKeyPress += _handler;
        }

        public void Dispose()
        {
            Console.CancelKeyPress -= _handler;
        }

        private void OnCancel(object? sender, ConsoleCancelEventArgs args)
        {
            args.Cancel = true;
            _source.Cancel();
        }
    }
}

/// <summary>
///     <para>为 run、chat 与 sandbox verify 共享可信设置、存储保护和已知密钥的沙箱组合规则。</para>
///     <para>
///         Shares sandbox composition rules for trusted settings, storage protection,
///         and known secrets across run, chat, and sandbox verify.
///     </para>
/// </summary>
internal static class SandboxCliComposition
{
    /// <summary>
    ///     <para>从固定默认位置的用户配置加载 settings.windowsSandbox。</para>
    ///     <para>Loads settings.windowsSandbox from user configuration at the fixed default location.</para>
    /// </summary>
    public static async Task<WindowsSandboxSettings?> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var userConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cancellationToken)
                                              .ConfigureAwait(false);
        return userConfig.Settings?.WindowsSandbox;
    }

    /// <summary>
    ///     <para>显式启用时校验存储边界并组合沙箱；失败不回退宿主执行。networkSettings 传入组合器供沙箱代理解析。</para>
    ///     <para>
    ///         Validates storage boundaries and composes an explicitly enabled sandbox without host fallback on
    ///         failure; networkSettings feeds the composer for sandbox proxy resolution.
    ///     </para>
    /// </summary>
    /// <returns>
    ///     <para>启用时返回沙箱执行要素；未启用时返回 null。</para>
    ///     <para>Sandbox execution components when enabled; otherwise null.</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">
    ///     <para>
    ///         启用沙箱时，实际使用的受保护会话存储目录位于工作区或附加写根内；
    ///         这些位置会允许沙箱命令篡改会话日志。
    ///     </para>
    ///     <para>
    ///         With the sandbox enabled, the active protected session store lies inside the workspace
    ///         or an additional write root; these locations would let sandboxed commands tamper with session logs.
    ///     </para>
    /// </exception>
    public static WindowsSandboxExecution? TryCompose(
        WindowsSandboxSettings?             settings,
        string                              workspaceRoot,
        IReadOnlyDictionary<string, string> knownSecrets,
        string?                             sessionStoreDirectory  = null,
        NetworkSettings?                    networkSettings        = null,
        SandboxBaselineReadProtection?      baselineReadProtection = null,
        StandingTrustState                  standingTrust          = StandingTrustState.None,
        IReadOnlyList<string>?              permissionSystemPaths  = null)
    {
        if (settings is not { Enabled: true }) return null;

        // 存储保护校验按用户配置的原始写根集合执行：未确认的常设写根虽不进 OS 基线，
        // 仍可经 grant 通道临时生效，不得吞掉受保护会话存储。
        var compositionSettings = StandingTrustEvaluation.EffectiveSandboxSettings(settings, standingTrust) ??
                                  settings;
        if (sessionStoreDirectory is null)
            return WindowsSandboxComposer.Compose(compositionSettings, workspaceRoot, knownSecrets, networkSettings,
                                                  baselineReadProtection, permissionSystemPaths);
        var fullStoreDirectory = Path.GetFullPath(sessionStoreDirectory);
        var offendingRoot = settings.AdditionalWriteRoots?
                                    .Select(Path.GetFullPath)
                                    .FirstOrDefault(root => Workspace.IsInside(root, fullStoreDirectory));
        if (Workspace.IsInside(workspaceRoot, fullStoreDirectory) || offendingRoot is not null)
            throw new
                InvalidOperationException($"The protected session store directory '{fullStoreDirectory}' lies inside " +
                                          $"a sandbox-writable root '{offendingRoot ?? workspaceRoot}'; "              +
                                          "sandboxed commands could tamper with resumable session snapshots. "         +
                                          "Move it outside the workspace and every additional write root "             +
                                          "(settings.sessionStoreDirectory) before enabling the Windows sandbox.");

        return WindowsSandboxComposer.Compose(compositionSettings, workspaceRoot, knownSecrets, networkSettings,
                                              baselineReadProtection, permissionSystemPaths);
    }

    /// <summary>
    ///     <para>为子进程环境清理与输出脱敏提供已知密钥，不记录密钥值。</para>
    ///     <para>Supplies known secrets for child-environment removal and output redaction without logging their values.</para>
    /// </summary>
    /// <remarks>
    ///     <para>凭据存储目标名仅作映射键，不创建子进程环境变量；密钥值仍参与脱敏。</para>
    ///     <para>
    ///         A credential-store target name is only a mapping key and creates no child environment variable;
    ///         its secret value still participates in redaction.
    ///     </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, string> KnownSecrets(TinyHarnessConfig config, string? apiKey)
    {
        var name = !string.IsNullOrWhiteSpace(config.ApiKeyEnvironmentVariable)
            ? config.ApiKeyEnvironmentVariable
            : config.ApiKeyCredentialTarget;
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(apiKey))
            return new Dictionary<string, string>(StringComparer.Ordinal);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [name] = apiKey
        };
    }
}
