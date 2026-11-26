using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>编辑可信的 Windows 沙箱设置：启用、组件路径覆盖、网络策略、代理与额外读写范围。</para>
///     <para>
///         Edits the trusted Windows sandbox settings: enablement, component-path overrides, network policy,
///         proxy, and additional read/write roots.
///     </para>
/// </summary>
public sealed class WindowsSandboxSettingsViewModel : SettingsSectionViewModel
{
    private readonly string                      _appBaseDirectory;
    private readonly SettingsAutoSaveQueue       _autoSave;
    private readonly string                      _path;
    private readonly Func<Task>                  _refresh;
    private          UserConfig                  _baseline = new();
    private          bool                        _enabled;
    private          string                      _environmentText = string.Empty;
    private          bool                        _hasAutoSaveError;
    private          bool                        _hasLoaded;
    private          bool                        _isUpdating;
    private          string                      _notice = string.Empty;
    private          string?                     _proxyName;
    private          string                      _readRootsText = string.Empty;
    private          string                      _runnerEffectivePathText = string.Empty;
    private          string                      _runnerExecutablePathText = string.Empty;
    private          string                      _runnerStatusSummaryText = string.Empty;
    private          SandboxNetworkPolicyChoice? _selectedNetworkPolicy;
    private          SandboxProxyModeChoice?     _selectedProxyMode;
    private          string                      _setupEffectivePathText = string.Empty;
    private          string                      _setupExecutablePathText = string.Empty;
    private          string                      _setupStatusSummaryText = string.Empty;
    private          string                      _writeRootsText = string.Empty;

    /// <summary>
    ///     <para>使用可信配置路径、保存后的刷新回调与可选的应用基准目录建立 Windows 沙箱分区。</para>
    ///     <para>
    ///         Creates the Windows sandbox section with a trusted config path, a post-save refresh callback,
    ///         and an optional application base directory.
    ///     </para>
    /// </summary>
    public WindowsSandboxSettingsViewModel(
        string path, Func<Task>? refresh = null, string? appBaseDirectory = null) : base("Windows 沙箱")
    {
        _path             = path;
        _refresh          = refresh ?? (() => Task.CompletedTask);
        _appBaseDirectory = WindowsSandboxComponentResolver.NormalizeAppBaseDirectory(appBaseDirectory);
        _autoSave = new SettingsAutoSaveQueue(async () =>
        {
            await SaveCoreAsync();
            return true;
        }, exception =>
        {
            HasAutoSaveError = true;
            Notice           = $"沙箱设置保存失败：{exception.Message}";
        }, TimeSpan.FromMilliseconds(350));
        SaveCommand = new AsyncCommand(() =>
        {
            _autoSave.Request();
            return _autoSave.WaitAsync();
        }, exception => Notice = exception.Message);
        ResetComponentPathsCommand = new RelayCommand(ResetComponentPaths, () => HasCustomComponentPaths);
        NetworkPolicyOptions =
        [
            new SandboxNetworkPolicyChoice(SandboxNetworkPolicy.Restricted, "受限（需审批）"),
            new SandboxNetworkPolicyChoice(SandboxNetworkPolicy.Enabled, "常设允许")
        ];
        ProxyModeOptions =
        [
            new SandboxProxyModeChoice(SandboxProxyMode.FollowAppDefault, "跟随软件默认"),
            new SandboxProxyModeChoice(SandboxProxyMode.System, "系统代理"),
            new SandboxProxyModeChoice(SandboxProxyMode.Direct, "直连"),
            new SandboxProxyModeChoice(SandboxProxyMode.Named, "指定已有代理")
        ];
        UpdateComponentDiagnostics();
    }

    public string Intro =>
        "沙箱设置属于可信用户配置；改动在下次会话或下次组合沙箱时生效，不影响正在运行的命令。";

    public IReadOnlyList<SandboxNetworkPolicyChoice> NetworkPolicyOptions { get; }

    public IReadOnlyList<SandboxProxyModeChoice> ProxyModeOptions { get; }

    /// <summary>
    ///     <para>可被 named 模式引用的已保存代理；此处不读取或显示凭据。</para>
    ///     <para>Saved proxies referenceable by named mode; credentials are neither read nor displayed here.</para>
    /// </summary>
    public ObservableCollection<NetworkProxyChoice> ProxyChoices { get; } = [];

    public AsyncCommand SaveCommand { get; }

    /// <summary>
    ///     <para>清空自定义组件路径覆盖，恢复使用当前安装包自带的默认组件。</para>
    ///     <para>Clears custom component-path overrides to restore the current package's bundled defaults.</para>
    /// </summary>
    public RelayCommand ResetComponentPathsCommand { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value)) QueueAutoSave();
        }
    }

    /// <summary>
    ///     <para>用户输入的 setup 组件绝对路径覆盖；留空表示使用当前安装包默认组件。</para>
    ///     <para>User-entered setup executable absolute path override; blank uses the current package default.</para>
    /// </summary>
    public string SetupExecutablePathText
    {
        get => _setupExecutablePathText;
        set
        {
            if (!SetProperty(ref _setupExecutablePathText, value)) return;
            UpdateComponentDiagnostics();
            QueueAutoSave();
        }
    }

    /// <summary>
    ///     <para>用户输入的 runner 组件绝对路径覆盖；留空表示使用当前安装包默认组件。</para>
    ///     <para>User-entered runner executable absolute path override; blank uses the current package default.</para>
    /// </summary>
    public string RunnerExecutablePathText
    {
        get => _runnerExecutablePathText;
        set
        {
            if (!SetProperty(ref _runnerExecutablePathText, value)) return;
            UpdateComponentDiagnostics();
            QueueAutoSave();
        }
    }

    public bool HasCustomComponentPaths =>
        !string.IsNullOrWhiteSpace(_setupExecutablePathText) ||
        !string.IsNullOrWhiteSpace(_runnerExecutablePathText);

    public string SetupStatusSummaryText
    {
        get => _setupStatusSummaryText;
        private set => SetProperty(ref _setupStatusSummaryText, value);
    }

    public string SetupEffectivePathText
    {
        get => _setupEffectivePathText;
        private set => SetProperty(ref _setupEffectivePathText, value);
    }

    public string RunnerStatusSummaryText
    {
        get => _runnerStatusSummaryText;
        private set => SetProperty(ref _runnerStatusSummaryText, value);
    }

    public string RunnerEffectivePathText
    {
        get => _runnerEffectivePathText;
        private set => SetProperty(ref _runnerEffectivePathText, value);
    }

    public string ComponentOverrideHint =>
        "留空时 CLI 与 Desktop 各自使用本安装包内的默认组件；填写自定义绝对路径会写入共享用户配置，对 CLI 和 Desktop 同时生效。";

    public SandboxNetworkPolicyChoice SelectedNetworkPolicy
    {
        get => _selectedNetworkPolicy ?? NetworkPolicyOptions[0];
        set
        {
            if (value is not null && SetProperty(ref _selectedNetworkPolicy, value)) QueueAutoSave();
        }
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
                QueueAutoSave();
            }
        }
    }

    public bool IsNamedProxyMode => SelectedProxyMode.Mode == SandboxProxyMode.Named;

    /// <summary>named 模式引用的已保存代理（按名称匹配）。The saved proxy referenced in named mode (matched by name).</summary>
    public NetworkProxyChoice? SelectedProxy
    {
        get => ProxyChoices.FirstOrDefault(choice => choice.Id == _proxyName);
        set
        {
            if (_isUpdating) return;
            if (SetProperty(ref _proxyName, value?.Id)) QueueAutoSave();
        }
    }

    /// <summary>额外只读根，一行一个绝对路径。Additional read roots, one absolute path per line.</summary>
    public string ReadRootsText
    {
        get => _readRootsText;
        set
        {
            if (SetProperty(ref _readRootsText, value)) QueueAutoSave();
        }
    }

    /// <summary>额外写根，一行一个绝对路径。Additional write roots, one absolute path per line.</summary>
    public string WriteRootsText
    {
        get => _writeRootsText;
        set
        {
            if (SetProperty(ref _writeRootsText, value)) QueueAutoSave();
        }
    }

    /// <summary>附加环境变量，一行一条 KEY=VALUE。Extra environment variables, one KEY=VALUE pair per line.</summary>
    public string EnvironmentText
    {
        get => _environmentText;
        set
        {
            if (SetProperty(ref _environmentText, value)) QueueAutoSave();
        }
    }

    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public bool HasAutoSaveError
    {
        get => _hasAutoSaveError;
        private set => SetProperty(ref _hasAutoSaveError, value);
    }

    public event Action<UserConfig>? ConfigSaved;

    private void QueueAutoSave()
    {
        if (!_hasLoaded || _isUpdating) return;
        HasAutoSaveError = false;
        _autoSave.Request();
    }

    internal Task WaitForAutoSaveAsync() => _autoSave.WaitAsync();

    /// <summary>
    ///     <para>只读加载可信沙箱设置；沙箱未启用时仍显示已保存字段供编辑。</para>
    ///     <para>
    ///         Loads trusted sandbox settings read-only; saved fields stay visible for editing even
    ///         when the sandbox is disabled.
    ///     </para>
    /// </summary>
    public Task LoadAsync() => _autoSave.ExecuteAsync(LoadCoreAsync);

    private async Task LoadCoreAsync()
    {
        _isUpdating = true;
        try
        {
            _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
            ApplySavedSettings(_baseline);
            _autoSave.Reset();
            HasAutoSaveError = false;
            _hasLoaded       = true;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    ///     <para>同步其他设置分区保存后的配置基线，不覆盖当前编辑。</para>
    ///     <para>Accepts a config baseline saved by another section without replacing current edits.</para>
    /// </summary>
    public void AcceptBaseline(UserConfig baseline)
    {
        _isUpdating = true;
        try
        {
            _baseline = baseline;
            if (!_autoSave.HasChanges)
            {
                ApplySavedSettings(baseline);
            }
            else
            {
                RebuildProxyChoices();
                OnPropertyChanged(nameof(SelectedProxy));
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    ///     <para>校验编辑内容并保存；失败保留编辑内容与基线。</para>
    ///     <para>Validates the edits and saves; failures retain edits and the baseline.</para>
    /// </summary>
    public Task SaveAsync() => _autoSave.SaveAsync();

    private async Task SaveCoreAsync()
    {
        HasAutoSaveError = false;
        Notice           = string.Empty;
        var setupPath   = ParseOptionalAbsolutePath(SetupExecutablePathText, "setup 组件路径");
        var runnerPath  = ParseOptionalAbsolutePath(RunnerExecutablePathText, "runner 组件路径");
        var readRoots   = ParseRoots(ReadRootsText, "额外只读根");
        var writeRoots  = ParseRoots(WriteRootsText, "额外写根");
        var environment = ParseEnvironment(EnvironmentText);
        if (SelectedProxyMode.Mode == SandboxProxyMode.Named && _proxyName is null)
            throw new InvalidOperationException("请先在网络设置中保存代理，再选择指定代理。");
        var saved = _baseline.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        var sandbox = saved with
        {
            Enabled              = Enabled,
            AllowTcpLoopback     = false,
            SetupExecutablePath  = setupPath,
            RunnerExecutablePath = runnerPath,
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
                Permissions = permissions
            }
        };
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline = candidate;
        UpdateComponentDiagnostics();
        Notice = "已保存。改动在下次会话或下次组合沙箱时生效，不影响正在运行的命令。";
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    private void ApplySavedSettings(UserConfig config)
    {
        var sandbox = config.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        _enabled                  = sandbox.Enabled;
        _setupExecutablePathText  = sandbox.SetupExecutablePath;
        _runnerExecutablePathText = sandbox.RunnerExecutablePath;
        _proxyName                = sandbox.Proxy.ProxyName;
        _readRootsText            = string.Join(Environment.NewLine, sandbox.AdditionalReadRoots);
        _writeRootsText           = string.Join(Environment.NewLine, sandbox.AdditionalWriteRoots);
        _environmentText = sandbox.ExtraEnvironment is { } environment
            ? string.Join(Environment.NewLine, environment.Select(pair => $"{pair.Key}={pair.Value}"))
            : string.Empty;
        _selectedNetworkPolicy = NetworkPolicyOptions.Single(option => option.Policy == sandbox.NetworkPolicy);
        _selectedProxyMode     = ProxyModeOptions.Single(option => option.Mode       == sandbox.Proxy.Mode);
        RebuildProxyChoices();
        UpdateComponentDiagnostics();
        OnPropertyChanged(nameof(SelectedNetworkPolicy));
        OnPropertyChanged(nameof(SelectedProxyMode));
        OnPropertyChanged(nameof(IsNamedProxyMode));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(SetupExecutablePathText));
        OnPropertyChanged(nameof(RunnerExecutablePathText));
        OnPropertyChanged(nameof(ReadRootsText));
        OnPropertyChanged(nameof(WriteRootsText));
        OnPropertyChanged(nameof(EnvironmentText));
        OnPropertyChanged(nameof(SelectedProxy));
    }

    private void ResetComponentPaths()
    {
        if (!HasCustomComponentPaths) return;

        var changed = false;
        if (!string.IsNullOrEmpty(_setupExecutablePathText))
        {
            _setupExecutablePathText = string.Empty;
            OnPropertyChanged(nameof(SetupExecutablePathText));
            changed = true;
        }

        if (!string.IsNullOrEmpty(_runnerExecutablePathText))
        {
            _runnerExecutablePathText = string.Empty;
            OnPropertyChanged(nameof(RunnerExecutablePathText));
            changed = true;
        }

        UpdateComponentDiagnostics();
        if (changed) QueueAutoSave();
    }

    private void UpdateComponentDiagnostics()
    {
        var preview = new WindowsSandboxSettings
        {
            SetupExecutablePath  = _setupExecutablePathText,
            RunnerExecutablePath = _runnerExecutablePathText
        };
        var resolution       = WindowsSandboxComponentResolver.Resolve(preview, _appBaseDirectory);
        var setupInspection  = WindowsSandboxComponentResolver.InspectComponent(resolution.Setup, _appBaseDirectory);
        var runnerInspection = WindowsSandboxComponentResolver.InspectComponent(resolution.Runner, _appBaseDirectory);

        SetupStatusSummaryText  = FormatComponentSummary(setupInspection);
        SetupEffectivePathText  = FormatEffectivePath(setupInspection);
        RunnerStatusSummaryText = FormatComponentSummary(runnerInspection);
        RunnerEffectivePathText = FormatEffectivePath(runnerInspection);

        OnPropertyChanged(nameof(HasCustomComponentPaths));
        ResetComponentPathsCommand.RaiseCanExecuteChanged();
    }

    private static string FormatComponentSummary(WindowsSandboxComponentInspection inspection)
    {
        var resolved = inspection.Resolved;
        var state = !resolved.IsValid
            ? "路径无效（需填写完整绝对路径）"
            : inspection.IsUsable
                ? "已找到"
                : inspection.Exists
                    ? "目录边界校验失败"
                    : inspection.IsReleasePackageBundled
                        ? "发布包缺件（请重新完整解压 ZIP）"
                        : "未找到文件";
        return $"当前来源：{resolved.SourceDisplayZh} · 状态：{state}";
    }

    private static string FormatEffectivePath(WindowsSandboxComponentInspection inspection) =>
        inspection.Resolved.CandidatePath is { } path
            ? $"当前 Desktop 解析路径：{path}"
            : $"当前配置值：{inspection.Resolved.ConfiguredValue ?? "<空>"}";

    private static string ParseOptionalAbsolutePath(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var trimmed = text.Trim();
        if (!Path.IsPathFullyQualified(trimmed))
            throw new InvalidOperationException($"{label}必须是完整绝对路径（或留空使用当前安装包默认组件）：{trimmed}");

        return trimmed;
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
