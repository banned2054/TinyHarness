using System.Text;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>
///         会话内 <c>/models</c> 命令：列出本地目录、两级交互选择（模型＋强度）与直接形式
///         <c>use</c>／<c>effort</c>。选择经宿主在空闲边界原子保存，不修改全局默认，不伪造回合；
///         目录只来自可信本地配置，不在线发现、不解析无关密钥。
///     </para>
///     <para>
///         The in-session <c>/models</c> command: local catalog listing, the two-step model+effort menu, and the
///         direct <c>use</c>/<c>effort</c> forms. Selections are saved atomically through the host at an idle
///         boundary without touching global defaults or fabricating turns; the catalog comes from trusted local
///         configuration only, with no online discovery or unrelated credential resolution.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         输入输出必须与 chat 循环共用同一读取器，菜单读取不新增 <c>Console.ReadLine</c>；
///         菜单中的 <c>/cancel</c>、EOF、无效选项或异常都不部分更改会话。运行中仅允许查询。
///     </para>
///     <para>
///         Input and output share the chat loop's reader — the menu never opens its own
///         <c>Console.ReadLine</c>; <c>/cancel</c>, EOF, invalid choices, and failures inside the menu all leave
///         the session untouched. During a run only queries are allowed.
///     </para>
///     <para>
///         候选客户端由工厂在用户完成选择后才构造；被拒绝或未采纳的候选沿用组合根的进程生命周期约定，
///         不由本类释放，已采纳客户端同样随进程存活。
///     </para>
///     <para>
///         Candidate clients are constructed by the factory only after the user completes a choice; rejected or
///         unadopted candidates follow the composition root's process-lifetime convention and are never disposed
///         here, and adopted clients live with the process as well.
///     </para>
/// </remarks>
internal sealed class ChatModelSelection
{
    private const string EffortDefaultToken = "default";

    private readonly Func<CancellationToken, Task<UserConfig>> _catalogLoader;
    private readonly Func<UserProfile, string, CancellationToken, Task<IChatCompletionClient>> _clientFactory;
    private readonly bool                        _interactiveInputAvailable;
    private readonly TextReader                  _input;
    private readonly TextWriter                  _output;
    private readonly IDictionary<string, string> _secrets;

    /// <summary>
    ///     <para>用共享读取器、目录加载器与候选客户端工厂构造选择服务；secrets 在展示前合并脱敏。</para>
    ///     <para>Creates the selection service with the shared reader, catalog loader, and candidate-client factory; secrets are merged for redaction before display.</para>
    /// </summary>
    public ChatModelSelection(
        TextReader input, TextWriter output, Func<CancellationToken, Task<UserConfig>> catalogLoader,
        Func<UserProfile, string, CancellationToken, Task<IChatCompletionClient>> clientFactory,
        IDictionary<string, string>? secrets = null, bool interactiveInputAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(catalogLoader);
        ArgumentNullException.ThrowIfNull(clientFactory);
        _input                     = input;
        _output                    = output;
        _catalogLoader             = catalogLoader;
        _clientFactory             = clientFactory;
        _secrets                   = secrets ?? new Dictionary<string, string>();
        _interactiveInputAvailable = interactiveInputAvailable;
    }

    /// <summary>
    ///     <para>判断输入行是否为 /models 命令；<c>/modelsXYZ</c> 不按前缀误识别。</para>
    ///     <para>Whether the input line is a /models command; <c>/modelsXYZ</c> is not matched by prefix.</para>
    /// </summary>
    public static bool IsModelsCommand(string input) => SplitArguments(input).FirstOrDefault() == "/models";

    /// <summary>
    ///     <para>处理一条 /models 命令；空闲与运行路径共用，运行中只允许查询。</para>
    ///     <para>Handles one /models command; both idle and running paths share it, and only queries are allowed during a run.</para>
    /// </summary>
    public async Task HandleAsync(ChatSessionHost host, string input, bool duringRun,
                                  CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var arguments = SplitArguments(input);
        if (arguments.Length < 2)
        {
            if (duringRun) await WriteCurrentSelectionAsync(host, running : true, cancellationToken);
            else await RunMenuAsync(host, cancellationToken);
            return;
        }

        switch (arguments[1])
        {
            case "list" :
                await ListAsync(host, cancellationToken);
                return;
            case "use" when arguments.Length >= 3 :
                await UseAsync(host, arguments[2..], duringRun, cancellationToken);
                return;
            case "effort" when arguments.Length == 3 :
                await ChangeEffortAsync(host, arguments[2], duringRun, cancellationToken);
                return;
            default :
                await WriteAsync("Usage: /models [list | use <model-id> [--provider <profile>] [--effort <id>] | " +
                                 "effort <id|default>].", cancellationToken);
                await WriteAsync("Unknown subcommand or wrong argument count; the input was not sent to the model.",
                                 cancellationToken);
                return;
        }
    }

    /// <summary>
    ///     <para>按 profile 分组列出本地模型并标记当前选择及其强度。</para>
    ///     <para>Lists local models grouped by profile, marking the current selection and its effort.</para>
    /// </summary>
    private async Task ListAsync(ChatSessionHost host, CancellationToken cancellationToken)
    {
        var config = await _catalogLoader(cancellationToken).ConfigureAwait(false);
        if (config.Profiles.Count == 0)
        {
            await WriteAsync("No providers are configured; run 'tinyharness provider add' first.", cancellationToken);
            return;
        }

        await WriteAsync("Models by provider (* current):", cancellationToken);
        var current = host.Session.ModelSelection;
        foreach (var profile in config.Profiles)
        {
            await
                WriteAsync($"[{profile.Name}]  {profile.Endpoint}  ({ChatApiKindParser.ToValueString(profile.ChatApi)})",
                           cancellationToken);
            if (profile.Models.Count == 0)
            {
                await WriteAsync("  (no models; run 'tinyharness model add <model-id> --context-window <tokens>')",
                                 cancellationToken);
                continue;
            }

            foreach (var model in profile.Models)
            {
                var isCurrent  = IsCurrentSelection(current, profile, model);
                var marker     = isCurrent ? "*" : " ";
                var effortText = isCurrent ? $"  effort: {EffortDisplay(current!.ReasoningEffort)}" : string.Empty;
                await WriteAsync($"{marker} {DescribeModel(profile, model)}{effortText}", cancellationToken);
            }
        }

        if (current is not null &&
            !config.Profiles.Any(profile => profile.Models.Any(model => IsCurrentSelection(current, profile, model))))
            await WriteAsync($"current model {current.Model.ModelId} is not present in the local catalog; " +
                             "selecting it again requires /models use after adding it.", cancellationToken);
    }

    /// <summary>
    ///     <para>直接形式：原子选择目标模型与强度；省略强度时采用目标模型声明的默认值。</para>
    ///     <para>Direct form: atomically selects the target model and effort; an omitted effort uses the target model's declared default.</para>
    /// </summary>
    private async Task UseAsync(ChatSessionHost host, string[] arguments, bool duringRun,
                                CancellationToken cancellationToken)
    {
        if (duringRun)
        {
            await WriteAsync("The current run must finish before the model can be changed; /models list still works.",
                             cancellationToken);
            return;
        }

        string? providerName = null;
        string? effortToken  = null;
        var     modelId      = arguments[0];
        for (var i = 1; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--provider" when i + 1 < arguments.Length :
                    providerName = arguments[++i];
                    break;
                case "--effort" when i + 1 < arguments.Length :
                    effortToken = arguments[++i];
                    break;
                default :
                    await WriteAsync($"Unknown option '{arguments[i]}'. Usage: /models use <model-id> " +
                                     "[--provider <profile>] [--effort <id>].", cancellationToken);
                    return;
            }
        }

        var config  = await _catalogLoader(cancellationToken).ConfigureAwait(false);
        var targets = ResolveTargets(config, modelId, providerName);
        if (targets.Count == 0)
        {
            await WriteAsync($"Model '{modelId}' is not configured in any profile; run 'tinyharness model add' first.",
                             cancellationToken);
            return;
        }

        if (targets.Count > 1)
        {
            var matches = string.Join(", ", targets.Select(target => $"{target.Profile.Name}/{target.Model.Id}"));
            await
                WriteAsync($"Model '{modelId}' exists in multiple profiles ({matches}); pass --provider to choose one.",
                           cancellationToken);
            return;
        }

        var (profile, model) = targets[0];
        ModelReasoningEffort? effort;
        if (effortToken is null) effort = model.DefaultReasoningEffort;
        else if (!TryResolveEffortToken(effortToken, model, out effort, out var effortError))
        {
            await WriteAsync(effortError, cancellationToken);
            return;
        }

        await ApplyAsync(host, profile, model, effort, cancellationToken);
    }

    /// <summary>
    ///     <para>仅调整当前模型的强度；default 表示不发送强度参数。</para>
    ///     <para>Adjusts only the current model's effort; default sends no effort parameter.</para>
    /// </summary>
    private async Task ChangeEffortAsync(ChatSessionHost   host, string effortToken, bool duringRun,
                                         CancellationToken cancellationToken)
    {
        if (duringRun)
        {
            await WriteAsync("The current run must finish before the effort can be changed; /models list still works.",
                             cancellationToken);
            return;
        }

        var current = host.Session.ModelSelection!;
        var targets = ResolveTargets(await _catalogLoader(cancellationToken).ConfigureAwait(false),
                                     current.Model.ModelId, null,
                                     profile => string.Equals(current.Model.Endpoint, profile.Endpoint,
                                                              StringComparison.Ordinal) &&
                                                current.Model.ChatApi == profile.ChatApi);
        if (targets.Count != 1)
        {
            await WriteAsync(targets.Count == 0
                                 ? "The current model is not present in the local catalog; use /models use <model-id> to select another model."
                                 : "The current model is ambiguous across profiles; use /models use <model-id> --provider <profile> instead.",
                             cancellationToken);
            return;
        }

        if (!TryResolveEffortToken(effortToken, targets[0].Model, out var effort, out var effortError))
        {
            await WriteAsync(effortError, cancellationToken);
            return;
        }

        var selection = await host.ChangeReasoningEffortAsync(effort, cancellationToken).ConfigureAwait(false);
        await WriteAsync($"model      : {targets[0].Profile.Name} / {selection.Model.ModelId}", cancellationToken);
        await WriteAsync($"effort     : {EffortDisplay(selection.ReasoningEffort)}", cancellationToken);
        if (selection.Revision == current.Revision)
            await WriteAsync("The selection already matched; nothing was changed.", cancellationToken);
    }

    /// <summary>
    ///     <para>两级交互菜单：先选模型，再选该模型支持的强度，一次性应用；取消或无效输入不部分更改。</para>
    ///     <para>The two-step menu: pick a model, then one of its supported efforts, and apply once; cancellation or invalid input never applies partially.</para>
    /// </summary>
    private async Task RunMenuAsync(ChatSessionHost host, CancellationToken cancellationToken)
    {
        if (!_interactiveInputAvailable)
        {
            await WriteAsync("Interactive model selection needs a terminal; use the direct form " +
                             "'/models use <model-id> --provider <profile> [--effort <id>]'.", cancellationToken);
            return;
        }

        var config  = await _catalogLoader(cancellationToken).ConfigureAwait(false);
        var catalog = config.Profiles.SelectMany(profile => profile.Models.Select(model => (profile, model))).ToList();
        if (catalog.Count == 0)
        {
            await
                WriteAsync("No models are configured; run 'tinyharness model add <model-id> --context-window <tokens>'.",
                           cancellationToken);
            return;
        }

        await WriteAsync("Models by provider (* current):", cancellationToken);
        var current = host.Session.ModelSelection;
        for (var i = 0; i < catalog.Count; i++)
        {
            var (profile, model) = catalog[i];
            var marker = IsCurrentSelection(current, profile, model) ? "*" : " ";
            await WriteAsync($"{marker} {i + 1}) {DescribeModel(profile, model)}", cancellationToken);
        }

        await WriteAsync("Select a model by number (/cancel to abort):", cancellationToken);
        var modelIndex = await ReadMenuChoiceAsync(catalog.Count, cancellationToken);
        if (modelIndex is null) return;
        var (chosenProfile, chosenModel) = catalog[modelIndex.Value];

        ModelReasoningEffort? effort = null;
        if (chosenModel.ReasoningEfforts is { Count: > 0 } efforts)
        {
            await WriteAsync($"Reasoning effort for {chosenModel.Id}:", cancellationToken);
            await WriteAsync("  1) default (decided by the service)", cancellationToken);
            for (var i = 0; i < efforts.Count; i++)
            {
                var level = efforts[i];
                var note  = level == chosenModel.DefaultReasoningEffort ? " (model default)" : string.Empty;
                var mark = IsCurrentSelection(current, chosenProfile, chosenModel) &&
                           current!.ReasoningEffort == level
                    ? "*"
                    : " ";
                await WriteAsync($" {mark} {i + 2}) {ModelReasoningEffortParser.ToValueString(level)}{note}",
                                 cancellationToken);
            }

            await WriteAsync("Select an effort by number (/cancel to abort):", cancellationToken);
            var effortIndex = await ReadMenuChoiceAsync(efforts.Count + 1, cancellationToken);
            if (effortIndex is null) return;
            effort = effortIndex == 0 ? null : efforts[effortIndex.Value - 1];
        }
        else
        {
            effort = null;
            await WriteAsync("This model declares no reasoning efforts; using the default (decided by the service).",
                             cancellationToken);
        }

        await ApplyAsync(host, chosenProfile, chosenModel, effort, cancellationToken);
    }

    /// <summary>
    ///     <para>解析目标档位标识；未知标识或超出模型声明能力时本地拒绝并说明原因。</para>
    ///     <para>Resolves an effort token; unknown ids or levels outside the model's declared capability are rejected locally with the reason.</para>
    /// </summary>
    private static bool TryResolveEffortToken(string                    token,  UserProfileModel model,
                                              out ModelReasoningEffort? effort, out string       error)
    {
        if (token == EffortDefaultToken)
        {
            effort = null;
            error  = string.Empty;
            return true;
        }

        if (!ModelReasoningEffortParser.TryParse(token, out var level))
        {
            effort = null;
            error = $"Unknown effort '{token}'; expected {EffortDefaultToken} or one of: " +
                    $"{ModelReasoningEffortParser.AcceptedValuesText}.";
            return false;
        }

        if (model.ReasoningEfforts is not { Count: > 0 } efforts || !efforts.Contains(level))
        {
            var declared = model.ReasoningEfforts is { Count: > 0 } declaredEfforts
                ? string.Join(", ", declaredEfforts.Select(ModelReasoningEffortParser.ToValueString))
                : "none declared";
            effort = null;
            error = $"Model '{model.Id}' does not declare effort '{ModelReasoningEffortParser.ToValueString(level)}' " +
                    $"(declared: {declared}); the selection was not changed.";
            return false;
        }

        effort = level;
        error  = string.Empty;
        return true;
    }

    /// <summary>
    ///     <para>构造候选绑定并经宿主原子保存；打印最终模型与强度。</para>
    ///     <para>Builds the candidate binding and saves it atomically through the host, then prints the final model and effort.</para>
    /// </summary>
    private async Task ApplyAsync(
        ChatSessionHost host, UserProfile profile, UserProfileModel model,
        ModelReasoningEffort? effort, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(profile.Endpoint))
        {
            await
                WriteAsync($"Provider '{profile.Name}' has no endpoint configured yet; finish its setup before selecting its models.",
                           cancellationToken);
            return;
        }

        var current = host.Session.ModelSelection!;
        var client  = await _clientFactory(profile, model.Id, cancellationToken).ConfigureAwait(false);
        var context = current.Context is { } existing
            ? existing with { ContextWindowTokens = model.ContextWindowTokens }
            : new ContextOptions { ContextWindowTokens = model.ContextWindowTokens };
        var selection =
            await host.ChangeModelAsync(new SessionModelIdentity(model.Id, profile.Endpoint, profile.ChatApi),
                                        client, context, effort, cancellationToken,
                                        credentialRoute : CredentialRoute.Describe(profile.Name,
                                                                                  profile.ApiKeyEnvironmentVariable,
                                                                                  profile.ApiKeyCredentialTarget),
                                        expectedRevision : current.Revision).ConfigureAwait(false);

        await WriteAsync($"model      : {profile.Name} / {selection.Model.ModelId}" +
                         (string.IsNullOrWhiteSpace(model.DisplayName) ? string.Empty : $" ({model.DisplayName})"),
                         cancellationToken);
        await WriteAsync($"effort     : {EffortDisplay(selection.ReasoningEffort)}", cancellationToken);
        await WriteAsync($"window     : {model.ContextWindowTokens:N0} tokens", cancellationToken);
        if (selection.Revision == current.Revision)
            await WriteAsync("The selection already matched; nothing was changed.", cancellationToken);
    }

    /// <summary>
    ///     <para>读取一个菜单编号（0 基）；取消、EOF、取消令牌或无效输入都返回 null 且不产生部分更改。</para>
    ///     <para>Reads one menu number (0-based); cancellation, EOF, the cancellation token, or invalid input all return null without partial changes.</para>
    /// </summary>
    private async Task<int?> ReadMenuChoiceAsync(int choiceCount, CancellationToken cancellationToken)
    {
        string? line;
        try
        {
            line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 会话取消或空闲 Ctrl+C：退出菜单，选择保持不变。
            return null;
        }

        if (line is null)
        {
            await WriteAsync("Model selection cancelled (end of input); nothing was changed.", cancellationToken);
            return null;
        }

        var text = line.Trim();
        if (text.Length == 0) return null;
        if (text is "/cancel")
        {
            await WriteAsync("Model selection cancelled; nothing was changed.", cancellationToken);
            return null;
        }

        if (!int.TryParse(text, out var choice) || choice < 1 || choice > choiceCount)
        {
            await WriteAsync($"Invalid choice '{text}'; model selection cancelled without changes.", cancellationToken);
            return null;
        }

        return choice - 1;
    }

    private async Task WriteCurrentSelectionAsync(ChatSessionHost   host, bool running,
                                                  CancellationToken cancellationToken)
    {
        var current = host.Session.ModelSelection!;
        await WriteAsync($"current model: {current.Model.ModelId}  effort: {EffortDisplay(current.ReasoningEffort)}",
                         cancellationToken);
        if (running)
            await WriteAsync("The current run must finish before the model can be changed; /models list still works.",
                             cancellationToken);
    }

    internal static List<(UserProfile Profile, UserProfileModel Model)> ResolveTargets(
        UserConfig config, string modelId, string? providerName,
        Func<UserProfile, bool>? profileFilter = null)
    {
        var profiles = config.Profiles.Where(profile => profileFilter is null || profileFilter(profile));
        if (providerName is not null)
            profiles = profiles.Where(profile => string.Equals(profile.Name, providerName, StringComparison.Ordinal));

        return profiles.SelectMany(profile => profile.Models
                                                     .Where(model => string.Equals(model.Id, modelId,
                                                                                   StringComparison.Ordinal))
                                                     .Select(model => (profile, model)))
                       .ToList();
    }

    private static bool IsCurrentSelection(
        SessionModelSelection? current, UserProfile profile, UserProfileModel model) =>
        current is not null &&
        string.Equals(current.Model.ModelId, model.Id, StringComparison.Ordinal) &&
        string.Equals(current.Model.Endpoint, profile.Endpoint, StringComparison.Ordinal) &&
        current.Model.ChatApi == profile.ChatApi;

    private static string DescribeModel(UserProfile profile, UserProfileModel model)
    {
        var display = string.IsNullOrWhiteSpace(model.DisplayName) ? string.Empty : $" ({model.DisplayName})";
        var efforts = model.ReasoningEfforts is { Count: > 0 } declared
            ? "  efforts: " + string.Join(", ", declared.Select(ModelReasoningEffortParser.ToValueString))
            : string.Empty;
        return $"{profile.Name} / {model.Id}{display}  {model.ContextWindowTokens:N0} tokens{efforts}";
    }

    private static string EffortDisplay(ModelReasoningEffort? effort) =>
        effort is { } level ? ModelReasoningEffortParser.ToValueString(level) : "default (service decides)";

    private async Task WriteAsync(string text, CancellationToken cancellationToken)
    {
        // 展示前合并当前秘密集合，包含切换中解析出的新密钥。
        await _output.WriteLineAsync(new DisplaySecretRedactor(_secrets.ToDictionary(pair => pair.Key,
                                                                                  pair => pair.Value)).Redact(text));
        await _output.FlushAsync(cancellationToken);
    }

    /// <summary>
    ///     <para>按空白拆分参数，双引号内的空白保留；用于含空格的模型或供应商名称。</para>
    ///     <para>Splits arguments on whitespace while preserving double-quoted segments; used for model or provider names containing spaces.</para>
    /// </summary>
    private static string[] SplitArguments(string input)
    {
        var parts   = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        foreach (var ch in input)
            if (ch == '"') inQuote = !inQuote;
            else if (char.IsWhiteSpace(ch) && !inQuote)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else current.Append(ch);

        if (current.Length > 0) parts.Add(current.ToString());
        return [.. parts];
    }
}
