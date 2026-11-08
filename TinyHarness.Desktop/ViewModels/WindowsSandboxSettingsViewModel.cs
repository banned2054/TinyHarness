using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>编辑可信的 Windows 沙箱设置：启用、网络策略、环回、代理与额外读写范围。</para>
///     <para>
///         Edits the trusted Windows sandbox settings: enablement, network policy, loopback,
///         proxy, and additional read/write roots.
///     </para>
/// </summary>
public sealed class WindowsSandboxSettingsViewModel : SettingsSectionViewModel
{
    private readonly string     _path;
    private readonly Func<Task> _refresh;
    private          UserConfig _baseline = new();
    private          bool       _allowTcpLoopback;
    private          bool       _enabled;
    private          string     _environmentText = string.Empty;
    private          string     _notice          = string.Empty;
    private          string?    _proxyName;
    private          string     _readRootsText  = string.Empty;
    private          SandboxProxyModeChoice? _selectedProxyMode;
    private          SandboxNetworkPolicyChoice? _selectedNetworkPolicy;
    private          string     _writeRootsText = string.Empty;

    /// <summary>
    ///     <para>使用可信配置路径及保存后的刷新回调建立 Windows 沙箱分区。</para>
    ///     <para>Creates the Windows sandbox section with a trusted config path and a post-save refresh callback.</para>
    /// </summary>
    public WindowsSandboxSettingsViewModel(string path, Func<Task>? refresh = null) : base("Windows 沙箱")
    {
        _path       = path;
        _refresh    = refresh ?? (() => Task.CompletedTask);
        SaveCommand = new AsyncCommand(SaveAsync, exception => Notice = exception.Message);
        NetworkPolicyOptions =
        [
            new SandboxNetworkPolicyChoice(SandboxNetworkPolicy.Restricted, "受限（每次联网申请需审批）"),
            new SandboxNetworkPolicyChoice(SandboxNetworkPolicy.Enabled, "常设允许（不再逐次询问联网申请）")
        ];
        ProxyModeOptions =
        [
            new SandboxProxyModeChoice(SandboxProxyMode.FollowAppDefault, "跟随软件默认"),
            new SandboxProxyModeChoice(SandboxProxyMode.System, "系统代理"),
            new SandboxProxyModeChoice(SandboxProxyMode.Direct, "直连"),
            new SandboxProxyModeChoice(SandboxProxyMode.Named, "指定已有代理")
        ];
    }

    public string Intro =>
        "沙箱设置属于可信用户配置；改动在下次会话或下次组合沙箱时生效，不影响正在运行的命令。";

    public IReadOnlyList<SandboxNetworkPolicyChoice> NetworkPolicyOptions { get; }

    public IReadOnlyList<SandboxProxyModeChoice> ProxyModeOptions { get; }

    /// <summary>可被 named 模式引用的已保存代理；凭据只保存在系统凭据存储，此处不显示。Saved proxies referenceable by named mode; credentials stay in the system credential store and are never shown.</summary>
    public ObservableCollection<NetworkProxyChoice> ProxyChoices { get; } = [];

    public AsyncCommand SaveCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public SandboxNetworkPolicyChoice SelectedNetworkPolicy
    {
        get => _selectedNetworkPolicy ?? NetworkPolicyOptions[0];
        set
        {
            if (value is not null) SetProperty(ref _selectedNetworkPolicy, value);
        }
    }

    public bool AllowTcpLoopback
    {
        get => _allowTcpLoopback;
        set => SetProperty(ref _allowTcpLoopback, value);
    }

    public SandboxProxyModeChoice SelectedProxyMode
    {
        get => _selectedProxyMode ?? ProxyModeOptions[0];
        set
        {
            if (value is null) return;
            if (SetProperty(ref _selectedProxyMode, value))
            {
                OnPropertyChanged(nameof(IsNamedProxyMode));
                OnPropertyChanged(nameof(SelectedProxy));
            }
        }
    }

    public bool IsNamedProxyMode => SelectedProxyMode.Mode == SandboxProxyMode.Named;

    /// <summary>named 模式引用的已保存代理（按名称匹配）。The saved proxy referenced in named mode (matched by name).</summary>
    public NetworkProxyChoice? SelectedProxy
    {
        get => ProxyChoices.FirstOrDefault(choice => choice.Id == _proxyName);
        set => SetProperty(ref _proxyName, value?.Id);
    }

    /// <summary>额外只读根，一行一个绝对路径。Additional read roots, one absolute path per line.</summary>
    public string ReadRootsText
    {
        get => _readRootsText;
        set => SetProperty(ref _readRootsText, value);
    }

    /// <summary>额外写根，一行一个绝对路径。Additional write roots, one absolute path per line.</summary>
    public string WriteRootsText
    {
        get => _writeRootsText;
        set => SetProperty(ref _writeRootsText, value);
    }

    /// <summary>附加环境变量，一行一条 KEY=VALUE。Extra environment variables, one KEY=VALUE pair per line.</summary>
    public string EnvironmentText
    {
        get => _environmentText;
        set => SetProperty(ref _environmentText, value);
    }

    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public event Action<UserConfig>? ConfigSaved;

    /// <summary>
    ///     <para>只读加载可信沙箱设置；沙箱未启用时仍显示已保存字段供编辑。</para>
    ///     <para>
    ///         Loads trusted sandbox settings read-only; saved fields stay visible for editing even
    ///         when the sandbox is disabled.
    ///     </para>
    /// </summary>
    public async Task LoadAsync()
    {
        _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        ApplySavedSettings(_baseline);
    }

    /// <summary>
    ///     <para>同步其他设置分区保存后的配置基线，不覆盖当前编辑。</para>
    ///     <para>Accepts a config baseline saved by another section without replacing current edits.</para>
    /// </summary>
    public void AcceptBaseline(UserConfig baseline)
    {
        _baseline = baseline;
        ApplySavedSettings(baseline);
    }

    /// <summary>
    ///     <para>校验编辑内容并保存；失败保留编辑内容与基线。</para>
    ///     <para>Validates the edits and saves; failures retain edits and the baseline.</para>
    /// </summary>
    public async Task SaveAsync()
    {
        Notice          = string.Empty;
        var readRoots   = ParseRoots(ReadRootsText, "额外只读根");
        var writeRoots  = ParseRoots(WriteRootsText, "额外写根");
        var environment = ParseEnvironment(EnvironmentText);
        if (SelectedProxyMode.Mode == SandboxProxyMode.Named && _proxyName is null)
            throw new InvalidOperationException("请先在网络设置中保存代理，再选择指定代理。");
        var saved = _baseline.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        var sandbox = saved with
        {
            Enabled              = Enabled,
            AllowTcpLoopback     = AllowTcpLoopback,
            NetworkPolicy        = SelectedNetworkPolicy.Policy,
            Proxy = SelectedProxyMode.Mode == SandboxProxyMode.Named
                ? new SandboxProxyPolicy { Mode = SandboxProxyMode.Named, ProxyName = _proxyName }
                : new SandboxProxyPolicy { Mode = SelectedProxyMode.Mode },
            AdditionalReadRoots  = readRoots,
            AdditionalWriteRoots = writeRoots,
            ExtraEnvironment     = environment
        };
        // 清除扩大设置（回 restricted/写根清空）自然解除常设授权门禁：确认记录随保存一并删除。
        // 原配置没有 permissions 节时保持缺席，不新增默认块。
        var permissions = _baseline.Settings?.Permissions is { } existingPermissions
            ? existingPermissions with
            {
                StandingTrustConfirmation =
                    StandingTrustEvaluation.HasStandingExpansion(sandbox)
                        ? existingPermissions.StandingTrustConfirmation
                        : null
            }
            : null;
        var candidate = _baseline with
        {
            Settings = (_baseline.Settings ?? new UserConfigSettings()) with
            {
                WindowsSandbox = sandbox,
                Permissions    = permissions
            }
        };
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline = candidate;
        Notice    = "已保存。改动在下次会话或下次组合沙箱时生效，不影响正在运行的命令。";
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    private void ApplySavedSettings(UserConfig config)
    {
        var sandbox        = config.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        _enabled           = sandbox.Enabled;
        _allowTcpLoopback  = sandbox.AllowTcpLoopback;
        _proxyName         = sandbox.Proxy.ProxyName;
        _readRootsText     = string.Join(Environment.NewLine, sandbox.AdditionalReadRoots);
        _writeRootsText    = string.Join(Environment.NewLine, sandbox.AdditionalWriteRoots);
        _environmentText = sandbox.ExtraEnvironment is { } environment
            ? string.Join(Environment.NewLine, environment.Select(pair => $"{pair.Key}={pair.Value}"))
            : string.Empty;
        _selectedNetworkPolicy = NetworkPolicyOptions.Single(option => option.Policy == sandbox.NetworkPolicy);
        _selectedProxyMode     = ProxyModeOptions.Single(option => option.Mode == sandbox.Proxy.Mode);
        RebuildProxyChoices();
        OnPropertyChanged(nameof(SelectedNetworkPolicy));
        OnPropertyChanged(nameof(SelectedProxyMode));
        OnPropertyChanged(nameof(IsNamedProxyMode));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(AllowTcpLoopback));
        OnPropertyChanged(nameof(ReadRootsText));
        OnPropertyChanged(nameof(WriteRootsText));
        OnPropertyChanged(nameof(EnvironmentText));
        OnPropertyChanged(nameof(SelectedProxy));
    }

    private void RebuildProxyChoices()
    {
        ProxyChoices.Clear();
        foreach (var proxy in _baseline.Settings?.Network?.Proxies ?? [])
            ProxyChoices.Add(new NetworkProxyChoice(proxy.Name, $"{proxy.Name}（{proxy.Host}:{proxy.Port}）"));
    }

    private static IReadOnlyList<string> ParseRoots(string text, string label)
    {
        var roots = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(root => root.Length > 0)
                        .ToArray();
        foreach (var root in roots.Where(root => !Path.IsPathRooted(root)))
            throw new InvalidOperationException($"{label}必须是绝对路径：{root}");
        return roots;
    }

    private static IReadOnlyDictionary<string, string>? ParseEnvironment(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(line => line.Length > 0)
                        .ToArray();
        if (lines.Length == 0) return null;
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidOperationException($"环境变量必须是 KEY=VALUE 格式：{line}");
            var name = line[..separator].Trim();
            if (name.Length == 0) throw new InvalidOperationException($"环境变量名不能为空：{line}");
            environment[name] = line[(separator + 1)..].Trim();
        }

        return environment;
    }
}

/// <summary>沙箱网络策略选择项。A sandbox network policy selector option.</summary>
public sealed record SandboxNetworkPolicyChoice(SandboxNetworkPolicy Policy, string Label)
{
    public override string ToString() => Label;
}

/// <summary>沙箱代理模式选择项。A sandbox proxy mode selector option.</summary>
public sealed record SandboxProxyModeChoice(SandboxProxyMode Mode, string Label)
{
    public override string ToString() => Label;
}
