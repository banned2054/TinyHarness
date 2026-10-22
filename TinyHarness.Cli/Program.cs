using System.Text;
using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;
using TinyHarness.Cli.Prompts;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
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
        using var cts = new CancellationTokenSource();
        using var cancelRegistration = new ConsoleCancellation(cts);
        var       reader = new ExclusiveInputReader(Console.In);
        var       composition = await ComposeAgentAsync(configPath, cts.Token, true, reader).ConfigureAwait(false);
        await using var events =
            new ConsoleHostEventSink(composition.KnownSecrets, reasoningDisplay : reasoningDisplay);

        await WriteHeaderAsync("TinyHarness run", composition).ConfigureAwait(false);

        // 审批通过单一读取器接收，避免多个循环争抢 stdin。
        IApprovalProvider approver = new ConsoleApprovalProvider(reader, Console.Out, composition.KnownSecrets);
        var instructionLoader = new ProjectInstructionLoader(new Workspace(composition.Config.WorkspaceRoot));
        var initialInstructions = await instructionLoader.LoadAsync(null, [], cts.Token).ConfigureAwait(false);

        using var host = new ChatSessionHost(composition.Config.WorkspaceRoot,
                                             new SessionModelIdentity(composition.Config.Model,
                                                                      composition.Config.Endpoint,
                                                                      composition.Config.ChatApi),
                                             composition.Options,
                                             composition.Model,
                                             composition.Tools,
                                             WorkspacePrompts.Current,
                                             composition.Permissions,
                                             approver,
                                             events,
                                             sessionWriterFactory : sessionId =>
                                                 new SessionStoreWriter(composition.SessionStoreDirectory!, sessionId,
                                                                        composition.KnownSecrets),
                                             instructionLoader : instructionLoader,
                                             initialInstructions : initialInstructions,
                                             knownSecrets : composition.KnownSecrets);

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

        await Console.Out.WriteLineAsync($"final      : {TerminalDisplay.Format(result.FinalMessage, redactor)}");
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
        using var reader       = new ChatInputRouter(new ExclusiveInputReader(Console.In));
        var composition = await ComposeAgentAsync(configPath, cancellation.SessionToken, true, reader)
           .ConfigureAwait(false);
        var config = composition.Config;
        await using var events =
            new ConsoleHostEventSink(composition.KnownSecrets, reasoningDisplay : reasoningDisplay);

        // 回合、审批与检查确认必须共享同一个读取器，不能另开 stdin 循环。

        ChatSessionHost? host = null;
        Func<CancellationToken, Task<ChatSessionHost>>? hostFactory = null;
        var instructionLoader = new ProjectInstructionLoader(new Workspace(config.WorkspaceRoot));
        string? resumedNote = null;
        if (resumeSessionId is null)
        {
            hostFactory = async token =>
            {
                var initialInstructions = await instructionLoader.LoadAsync(null, [], token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return new ChatSessionHost(config.WorkspaceRoot,
                                           new SessionModelIdentity(config.Model, config.Endpoint, config.ChatApi),
                                           composition.Options,
                                           composition.Model,
                                           composition.Tools,
                                           WorkspacePrompts.Current,
                                           composition.Permissions,
                                           new ConsoleApprovalProvider(reader, Console.Out, composition.KnownSecrets),
                                           events,
                                           null,
                                           sessionId => new SessionStoreWriter(composition.SessionStoreDirectory!,
                                                                               sessionId, composition.KnownSecrets),
                                           instructionLoader,
                                           initialInstructions,
                                           composition.KnownSecrets);
            };
        }
        else
        {
            // 先取锁、严格读取并续种提交序号；恢复成功后转交写入器，否则由此处释放。
            var writer = await SessionStoreWriter.OpenExistingAsync(composition.SessionStoreDirectory!,
                                                                    resumeSessionId, composition.KnownSecrets,
                                                                    cancellation.SessionToken).ConfigureAwait(false);
            try
            {
                var restored = SessionResume.Restore(writer.LoadedSnapshot!, config.WorkspaceRoot,
                                                     new SessionModelIdentity(config.Model, config.Endpoint,
                                                                              config.ChatApi),
                                                     WorkspacePrompts.Current, composition.Options.Context);
                resumedNote = $"resumed    : {restored.Session.Turns.Count} turns from commit " +
                              $"{writer.CommitSequence} ({writer.LoadedSnapshot!.UpdatedUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss})";
                var pendingAmendments = restored.Session.Amendments.Count(item => item.CanonicalMessageIndex is null);
                if (pendingAmendments > 0)
                    resumedNote +=
                        $"\n  amendments : {pendingAmendments} saved but unapplied; inspect with session show and resubmit to continue";
                if (restored.RewoundTail)
                    resumedNote += "\n  rewound    : the last incomplete tool round was dropped from the model view";
                host = new ChatSessionHost(restored.Session, restored.Context, composition.Options, composition.Model,
                                           composition.Tools, WorkspacePrompts.Current, composition.Permissions,
                                           new ConsoleApprovalProvider(reader, Console.Out, composition.KnownSecrets),
                                           events,
                                           null,
                                           writer, restored.NeedsInspection,
                                           instructionLoader,
                                           knownSecrets : composition.KnownSecrets);
            }
            catch
            {
                writer.Dispose();
                throw;
            }
        }

        using (host)
        {
            await WriteHeaderAsync("TinyHarness chat", composition, host?.Session.Id).ConfigureAwait(false);
            if (resumedNote is not null) await Console.Out.WriteLineAsync($"  {resumedNote}");
            await
                Console.Out
                       .WriteLineAsync("  storage   : append-only JSONL session log; protocol history is saved verbatim " +
                                       "without harness encryption, so pasted secrets may be stored. Keep the session store private.");

            // 启动与后续中断确认均交给会话循环，避免保持阻断后多读一次回合输入。
            using var console = host is null
                ? new ChatSessionConsole(hostFactory!, reader, Console.Out, cancellation, composition.KnownSecrets)
                : new ChatSessionConsole(host, reader, Console.Out, cancellation, composition.KnownSecrets);
            console.WorkspaceTrustHandler = composition.Access.ChangeAsync;
            var exit         = await console.RunAsync().ConfigureAwait(false);
            var attachedHost = console.Host;

            // 只报告存在的产物；末次保存不可靠时明确警告，不以终态代替保存证据。
            if (attachedHost?.Session.Turns.Count > 0 && attachedHost.PersistencePath is { } path && File.Exists(path))
                await Console.Out.WriteLineAsync($"session    : {path}");
            if (attachedHost?.PersistenceWarning is { Length: > 0 } warning)
                await Console.Out.WriteLineAsync($"warning    : {warning}");
            if (attachedHost?.DisplayWarning is { Length: > 0 } displayWarning)
                await Console.Out.WriteLineAsync($"display    : {displayWarning}");

            return exit;
        }
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
    /// <exception cref="ConfigException">
    ///     <para>模型或 endpoint 缺失，或无法解析 API key。</para>
    ///     <para>The model or endpoint is missing, or the API key cannot be resolved.</para>
    /// </exception>
    private static async Task<AgentComposition> ComposeAgentAsync(
        string? configPath, CancellationToken cancellationToken, bool persistSessions = false, TextReader? input = null)
    {
        var resolution = await ConfigResolver.ResolveAsync(configPath, cancellationToken).ConfigureAwait(false);
        var config     = resolution.Config;

        if (string.IsNullOrWhiteSpace(config.Model))
            throw new
                ConfigException("A model must be configured. Run 'tinyharness init' (user config) or set 'model' in the config file.");

        if (string.IsNullOrWhiteSpace(config.Endpoint))
            throw new
                ConfigException("An endpoint must be configured. Run 'tinyharness init' (user config) or set 'endpoint' in the config file.");

        var apiKey = ApiKeyReader.Read(config.ApiKeyEnvironmentVariable, config.ApiKeyCredentialTarget,
                                       new WindowsCredentialStore(), resolution.ProfileName);
        if (apiKey is null)
            throw new
                ConfigException("No API key found. Run 'tinyharness auth set <profile>' or set the environment variable " +
                                $"'{config.ApiKeyEnvironmentVariable}' before running.");

        // 续聊存储仅取可信用户设置，不接受项目配置；保护校验失败即拒绝启动，不降级目录。
        var userConfig = await UserConfigStore.LoadAsync(UserConfigStore.DefaultFilePath(), cancellationToken)
                                              .ConfigureAwait(false);
        string? sessionStoreDirectory = null;
        if (persistSessions)
            sessionStoreDirectory = SessionStorePaths.ResolveProtected(userConfig.Settings?.SessionStoreDirectory,
                                                                       config.WorkspaceRoot);

        // 沙箱仅取可信用户设置，不接受项目配置；组件、路径、平台或存储保护校验失败均不回退宿主。
        var knownSecrets = SandboxCliComposition.KnownSecrets(config, apiKey);
        var settings     = userConfig.Settings?.WindowsSandbox;
        var accessState  = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(config.WorkspaceRoot));
        var permissions = new PermissionEngine(config.WorkspaceRoot, config.CommandRules,
                                               workspaceReadOnly : () => accessState.IsReadOnly);
        var trustPath = Path.Combine(Path.GetDirectoryName(UserConfigStore.DefaultFilePath())!, "workspace-trust.json");
        IReadOnlyList<string> writeRoots = [config.WorkspaceRoot, .. settings?.AdditionalWriteRoots ?? []];
        var protectedPaths = new List<string> { trustPath, UserConfigStore.DefaultFilePath() };
        if (sessionStoreDirectory is not null) protectedPaths.Add(sessionStoreDirectory);
        if (settings is { Enabled: true })
        {
            protectedPaths.Add(settings.SetupExecutablePath);
            protectedPaths.Add(settings.RunnerExecutablePath);
            protectedPaths.Add(WindowsSandboxComponents.ResolveSandboxHome(settings.SandboxHome,
                                                                           settings.AllowTcpLoopback));
        }

        WindowsSandboxExecution? sandbox = null;
        var access = new WorkspaceAccessCoordinator(accessState, new WorkspaceTrustStore(trustPath, writeRoots),
                                                    permissions, input ?? new ExclusiveInputReader(Console.In),
                                                    Console.Out, !Console.IsInputRedirected,
                                                    OperatingSystem.IsWindows() && settings is
                                                        { Enabled: true, Policy: SandboxPolicyKind.WorkspaceWrite },
                                                    new ProcessWorkspaceAclGrant(protectedPaths, writeRoots, settings),
                                                    () =>
                                                    {
                                                        sandbox = SandboxCliComposition.TryCompose(settings,
                                                            config.WorkspaceRoot, knownSecrets,
                                                            sessionStoreDirectory);
                                                        var policy = sandbox?.ExecutionPolicy ??
                                                                     ProcessExecutionPolicy.Host();
                                                        return
                                                            Task.FromResult((new ShellTool(new Workspace(config
                                                                                    .WorkspaceRoot),
                                                                                 config.DefaultToolTimeoutSeconds,
                                                                                 knownSecrets,
                                                                                 processBackend :
                                                                                 sandbox?.Backend ??
                                                                                 new
                                                                                     HostProcessBackend(knownSecrets),
                                                                                 executionPolicy : policy),
                                                                             policy));
                                                    });
        await access.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var executionPolicy = access.Policy;

        var model     = ModelClientFactory.Create(config.Model, config.Endpoint, apiKey, config.ChatApi);
        var workspace = new Workspace(config.WorkspaceRoot);
        var tools     = BuildTools(workspace, config, apiKey, executionPolicy, sandbox?.Backend, knownSecrets, access);
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

        return new AgentComposition(resolution, config, knownSecrets, sandbox, executionPolicy, model, tools,
                                    permissions, options, sessionStoreDirectory, access);
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
        var       hostConfig         = await McpWorkerHostConfiguration.ResolveAsync(cts.Token).ConfigureAwait(false);
        var model = ModelClientFactory.Create(hostConfig.Model, hostConfig.Endpoint,
                                              hostConfig.ApiKey, hostConfig.ChatApi);
        var runner = new WorkerRunner(model, hostConfig.WorkspaceRoot, hostConfig.ExecutionOptions);

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
    private static ToolRegistry BuildTools(Workspace workspace, TinyHarnessConfig config, string? apiKey = null,
                                           ProcessExecutionPolicy? executionPolicy = null,
                                           IProcessExecutionBackend? processBackend = null,
                                           IReadOnlyDictionary<string, string>? knownSecrets = null,
                                           WorkspaceAccessCoordinator? access = null)
    {
        var secrets = knownSecrets ?? SandboxCliComposition.KnownSecrets(config, apiKey);
        ITool[] tools =
        [
            new ListFilesTool(workspace), new SearchTextTool(workspace), new ReadFileTool(workspace),
            new ApplyPatchTool(workspace),
            new ShellTool(workspace, config.DefaultToolTimeoutSeconds, secrets,
                          processBackend : processBackend ?? new HostProcessBackend(secrets),
                          executionPolicy : executionPolicy)
        ];
        return new ToolRegistry(access is null
                                    ? tools
                                    : tools.Select(tool => access.State.Guard(() => tool.Definition.Name == "shell"
                                                                                  ? access.Shell
                                                                                  : tool)));
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
        var settings = await SandboxCliComposition.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        return SandboxCliComposition.TryCompose(settings, config.WorkspaceRoot, knownSecrets, sessionStoreDirectory);
    }

    /// <summary>
    ///     <para>使用脚本模型与临时工作区，离线检查读写工具闭环及审批流程。</para>
    ///     <para>
    ///         Uses a scripted model and disposable workspace to exercise
    ///         the read/write tool loop and approval flow offline.
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

        var plan        = SmokeScript.Build(ProcessProbeCommand(), fixture.Root);
        var script      = new SmokeScriptClient(plan.Steps);
        var workspace   = new Workspace(fixture.Root);
        var tools       = BuildTools(workspace, config);
        // 冒烟使用默认策略，确保副作用审批不受用户现行规则影响。
        var permissions = new PermissionEngine(fixture.Root);
        var approver    = new SmokeApprover();
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
                                                  new SessionStoreWriter(sessionDirectory, sessionId),
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
                       null, cts.Token).ConfigureAwait(false))
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

        // 脚本客户端拒绝工具闭环或内容不匹配；还需核对工具与审批次数，防止跳过补丁或进程执行。
        var passed = result.ToolExecutions == plan.ExpectedToolExecutions &&
                     approver.Prompts      == plan.ExpectedApprovalPrompts;
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
    /// </remarks>
    private sealed record AgentComposition(
        ConfigResolution                    Resolution,
        TinyHarnessConfig                   Config,
        IReadOnlyDictionary<string, string> KnownSecrets,
        WindowsSandboxExecution?            Sandbox,
        ProcessExecutionPolicy              ExecutionPolicy,
        IChatCompletionClient               Model,
        ToolRegistry                        Tools,
        PermissionEngine                    Permissions,
        AgentOptions                        Options,
        string?                             SessionStoreDirectory,
        WorkspaceAccessCoordinator          Access);

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
    ///     <para>显式启用时校验存储边界并组合沙箱；失败不回退宿主执行。</para>
    ///     <para>Validates storage boundaries and composes an explicitly enabled sandbox without host fallback on failure.</para>
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
        string?                             sessionStoreDirectory = null)
    {
        if (settings is not { Enabled: true }) return null;

        if (sessionStoreDirectory is null) return WindowsSandboxComposer.Compose(settings, workspaceRoot, knownSecrets);
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

        return WindowsSandboxComposer.Compose(settings, workspaceRoot, knownSecrets);
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
