using System.Collections.ObjectModel;
using System.Globalization;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>编辑可信全局网络默认值与复用代理；代理认证值只写入系统凭据存储。</para>
///     <para>
///         Edits the trusted global network default and reusable proxies; proxy authentication secrets go only to the
///         system credential store.
///     </para>
/// </summary>
public sealed class NetworkSettingsViewModel : SettingsSectionViewModel
{
    private readonly SettingsAutoSaveQueue _autoSave;
    private readonly ICredentialStore      _credentials;
    private readonly string                _path;
    private readonly Func<Task>            _refresh;
    private          UserConfig            _baseline = new();
    private          bool                  _clearSavedCredentials;
    private          string                _defaultMode = "system";
    private          string?               _defaultProxyId;
    private          string?               _editingCredentialTarget;
    private          string?               _editingProxyError;
    private          string?               _editingProxyId;
    private          bool                  _hasDefaultRouteSaveError;
    private          bool                  _hasLoaded;
    private          bool                  _isAddingProxy;
    private          bool                  _isEditingProxy;
    private          bool                  _isUpdating;
    private          string                _notice        = string.Empty;
    private          string                _proxyHost     = string.Empty;
    private          string                _proxyName     = string.Empty;
    private          string                _proxyPassword = string.Empty;
    private          string                _proxyPort     = string.Empty;
    private          string                _proxyUsername = string.Empty;
    private          DefaultNetworkChoice? _selectedDefaultMode;
    private          NetworkProxyChoice?   _selectedDefaultProxy;
    private          ProxyProtocolChoice?  _selectedProxyProtocol;
    private          string                _testTarget = "https://api.openai.com/v1/models";

    public NetworkSettingsViewModel(string path, Func<Task>? refresh = null, ICredentialStore? credentials = null)
        : base("网络")
    {
        _path        = path;
        _refresh     = refresh     ?? (() => Task.CompletedTask);
        _credentials = credentials ?? new WindowsCredentialStore();
        DefaultModeOptions =
        [
            new DefaultNetworkChoice("system", "使用系统代理"),
            new DefaultNetworkChoice("direct", "直连"),
            new DefaultNetworkChoice("proxy", "指定代理")
        ];
        ProxyProtocolOptions =
        [
            new ProxyProtocolChoice(ProxyProtocol.Http, "HTTP"),
            new ProxyProtocolChoice(ProxyProtocol.Https, "HTTPS"),
            new ProxyProtocolChoice(ProxyProtocol.Socks5, "SOCKS5")
        ];
        _autoSave = new SettingsAutoSaveQueue(AutoSaveDefaultRouteAsync, exception =>
        {
            HasDefaultRouteSaveError = true;
            Notice                   = $"默认网络保存失败：{exception.Message}";
        });
        SaveDefaultRouteCommand = new AsyncCommand(() =>
        {
            _autoSave.Request();
            return _autoSave.WaitAsync();
        }, exception => Notice = exception.Message);
        AddProxyCommand        = new RelayCommand(OpenAddProxy, () => !IsEditingProxy);
        SaveProxyCommand       = new AsyncCommand(SaveProxyAsync, exception => ProxyError = exception.Message);
        CancelProxyEditCommand = new RelayCommand(CancelProxyEdit);
    }

    public string Intro => "仅影响模型请求与模型目录，不改变系统或 Agent 工具网络。";

    public IReadOnlyList<DefaultNetworkChoice> DefaultModeOptions { get; }

    public IReadOnlyList<ProxyProtocolChoice> ProxyProtocolOptions { get; }

    public ObservableCollection<NetworkProxyRowViewModel> Proxies { get; } = [];

    public AsyncCommand SaveDefaultRouteCommand { get; }

    public RelayCommand AddProxyCommand { get; }

    public AsyncCommand SaveProxyCommand { get; }

    public RelayCommand CancelProxyEditCommand { get; }

    public bool IsDefaultProxyMode => _defaultMode == "proxy";

    public DefaultNetworkChoice? SelectedDefaultMode
    {
        get => _selectedDefaultMode ?? DefaultModeOptions.First(option => option.Value == _defaultMode);
        set
        {
            if (value is null || _defaultMode == value.Value) return;
            _defaultMode         = value.Value;
            _selectedDefaultMode = value;
            if (_defaultMode == "proxy" && _defaultProxyId is null)
                _defaultProxyId = ProxyChoices.FirstOrDefault()?.Id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDefaultProxyMode));
            OnPropertyChanged(nameof(SelectedDefaultProxy));
            OnPropertyChanged(nameof(CanSaveDefaultRoute));
            QueueDefaultRouteSave();
        }
    }

    public IReadOnlyList<NetworkProxyChoice> ProxyChoices =>
        (_baseline.Settings?.Network?.Proxies ?? []).Select(NetworkProxyDisplay.ToChoice).ToArray();

    public NetworkProxyChoice? SelectedDefaultProxy
    {
        get => ProxyChoices.FirstOrDefault(option => option.Id == _defaultProxyId);
        set
        {
            if (value is null || _defaultProxyId == value.Id) return;
            _defaultProxyId       = value.Id;
            _selectedDefaultProxy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSaveDefaultRoute));
            QueueDefaultRouteSave();
        }
    }

    public bool CanSaveDefaultRoute =>
        _defaultMode != "proxy" || ProxyChoices.Any(option => option.Id == _defaultProxyId);

    public string TestTarget
    {
        get => _testTarget;
        set => SetProperty(ref _testTarget, value);
    }

    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;

    public bool HasDefaultRouteSaveError
    {
        get => _hasDefaultRouteSaveError;
        private set => SetProperty(ref _hasDefaultRouteSaveError, value);
    }

    public bool IsAddingProxy
    {
        get => _isAddingProxy;
        private set
        {
            if (!SetProperty(ref _isAddingProxy, value)) return;
            OnPropertyChanged(nameof(IsProxyEditorOpen));
            OnPropertyChanged(nameof(ProxyEditorTitle));
        }
    }

    public bool IsEditingProxy
    {
        get => _isEditingProxy;
        private set
        {
            if (!SetProperty(ref _isEditingProxy, value)) return;
            OnPropertyChanged(nameof(IsProxyEditorOpen));
            OnPropertyChanged(nameof(ProxyEditorTitle));
            AddProxyCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSaveProxy));
        }
    }

    public bool IsProxyEditorOpen => IsAddingProxy || IsEditingProxy;

    public string ProxyEditorTitle => IsAddingProxy ? "添加代理" : "编辑代理";

    public string ProxyName
    {
        get => _proxyName;
        set
        {
            if (SetProperty(ref _proxyName, value)) NotifyProxyValidationChanged();
        }
    }

    public string ProxyHost
    {
        get => _proxyHost;
        set
        {
            if (SetProperty(ref _proxyHost, value)) NotifyProxyValidationChanged();
        }
    }

    public string ProxyPort
    {
        get => _proxyPort;
        set
        {
            if (SetProperty(ref _proxyPort, value)) NotifyProxyValidationChanged();
        }
    }

    public string ProxyUsername
    {
        get => _proxyUsername;
        set
        {
            if (SetProperty(ref _proxyUsername, value)) NotifyProxyValidationChanged();
        }
    }

    public string ProxyPassword
    {
        get => _proxyPassword;
        set
        {
            if (SetProperty(ref _proxyPassword, value)) NotifyProxyValidationChanged();
        }
    }

    public ProxyProtocolChoice? SelectedProxyProtocol
    {
        get => _selectedProxyProtocol ?? ProxyProtocolOptions[0];
        set
        {
            if (value is null || _selectedProxyProtocol == value) return;
            _selectedProxyProtocol = value;
            OnPropertyChanged();
            NotifyProxyValidationChanged();
        }
    }

    public bool HasSavedCredentials => !string.IsNullOrWhiteSpace(_editingCredentialTarget);

    public bool ClearSavedCredentials
    {
        get => _clearSavedCredentials;
        set
        {
            if (SetProperty(ref _clearSavedCredentials, value)) NotifyProxyValidationChanged();
        }
    }

    public string? ProxyError
    {
        get => _editingProxyError;
        private set
        {
            if (SetProperty(ref _editingProxyError, value)) OnPropertyChanged(nameof(HasProxyError));
        }
    }

    public bool HasProxyError => !string.IsNullOrWhiteSpace(ProxyError);

    public string? FirstProxyValidationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProxyName)) return "请填写代理名称。";
            if (string.IsNullOrWhiteSpace(ProxyHost)) return "请填写代理主机。";
            if (!int.TryParse(ProxyPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
                port is < 1 or > 65535) return "端口必须是 1 到 65535 之间的整数。";
            if (string.IsNullOrEmpty(ProxyUsername) != string.IsNullOrEmpty(ProxyPassword))
                return "代理用户名和密码必须同时填写。";
            if (IsEditingProxy && HasSavedCredentials && ClearSavedCredentials &&
                (ProxyUsername.Length > 0 || ProxyPassword.Length > 0))
                return "清除已有凭据时请留空用户名和密码，或取消清除后输入新凭据。";
            if (!IsEditingProxy && Proxies.Any(row => string.Equals(row.Source.Name, ProxyName.Trim(),
                                                                    StringComparison.OrdinalIgnoreCase)))
                return $"代理名称已存在：{ProxyName.Trim()}";
            if (IsEditingProxy && Proxies.Any(row => row.Source.Id != _editingProxyId &&
                                                     string.Equals(row.Source.Name, ProxyName.Trim(),
                                                                   StringComparison.OrdinalIgnoreCase)))
                return $"代理名称已存在：{ProxyName.Trim()}";
            return null;
        }
    }

    public bool CanSaveProxy => IsProxyEditorOpen && FirstProxyValidationError is null;

    public bool HasProxies => Proxies.Count > 0;

    public event Action<UserConfig>? ConfigSaved;

    /// <summary>
    ///     <para>读取可信网络设置并重建代理行。</para>
    ///     <para>Loads trusted network settings and rebuilds the proxy rows.</para>
    /// </summary>
    public Task LoadAsync() => _autoSave.ExecuteAsync(LoadCoreAsync);

    private async Task LoadCoreAsync()
    {
        _isUpdating = true;
        try
        {
            _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
            ApplySavedSettings(_baseline);
            CancelProxyEdit();
            RebuildRows();
            _autoSave.Reset();
            HasDefaultRouteSaveError = false;
            _hasLoaded               = true;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    ///     <para>接收其他设置分区保存后的配置基线。</para>
    ///     <para>Accepts a config baseline saved by another settings section.</para>
    /// </summary>
    public void AcceptBaseline(UserConfig baseline)
    {
        _isUpdating = true;
        try
        {
            _baseline = baseline;
            if (!_autoSave.HasChanges) ApplySavedSettings(baseline);
            OnPropertyChanged(nameof(ProxyChoices));
            OnPropertyChanged(nameof(SelectedDefaultProxy));
            OnPropertyChanged(nameof(CanSaveDefaultRoute));
            RebuildRows();
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    ///     <para>只为用户更改默认路由排队保存，不保存加载或同步过程中的临时选择。</para>
    ///     <para>Queues user changes to the default route without saving transient loading or synchronization values.</para>
    /// </summary>
    private void QueueDefaultRouteSave()
    {
        if (!_hasLoaded || _isUpdating) return;
        HasDefaultRouteSaveError = false;
        _autoSave.Request();
    }

    internal Task WaitForAutoSaveAsync() => _autoSave.WaitAsync();

    private async Task<bool> AutoSaveDefaultRouteAsync()
    {
        HasDefaultRouteSaveError = false;
        if (!CanSaveDefaultRoute)
        {
            Notice = "请选择一个已保存的代理作为全局默认。";
            return false;
        }

        await SaveDefaultRouteAsync();
        return true;
    }

    private void ApplySavedSettings(UserConfig config)
    {
        var route = config.Settings?.Network?.DefaultRoute ?? new NetworkRoute();
        _defaultMode          = NetworkModeParser.ToValueString(route.Mode);
        _defaultProxyId       = route.ProxyId;
        _selectedDefaultMode  = DefaultModeOptions.First(option => option.Value == _defaultMode);
        _selectedDefaultProxy = null;
        OnPropertyChanged(nameof(SelectedDefaultMode));
        OnPropertyChanged(nameof(IsDefaultProxyMode));
        OnPropertyChanged(nameof(SelectedDefaultProxy));
        OnPropertyChanged(nameof(CanSaveDefaultRoute));
    }

    private NetworkRoute BuildDefaultRoute()
    {
        if (_defaultMode == "proxy")
        {
            if (!ProxyChoices.Any(option => option.Id == _defaultProxyId))
                throw new InvalidDataException("请选择一个已保存的代理作为全局默认。");
            return new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = _defaultProxyId };
        }

        return _defaultMode == "direct"
            ? new NetworkRoute { Mode = NetworkMode.Direct }
            : new NetworkRoute { Mode = NetworkMode.System };
    }

    private async Task SaveDefaultRouteAsync()
    {
        Notice = string.Empty;
        var network   = CurrentNetwork() with { DefaultRoute = BuildDefaultRoute() };
        var candidate = WithNetwork(_baseline, network);
        await SaveConfigAsync(candidate);
        Notice = "全局默认网络方式已保存；新请求会在安全的运行边界采用新路由。";
    }

    private void OpenAddProxy()
    {
        ResetProxyEditor();
        IsAddingProxy = true;
        ProxyError    = null;
    }

    private void OpenEditProxy(NetworkProxyRowViewModel row)
    {
        ResetProxyEditor();
        _editingProxyId          = row.Source.Id;
        _editingCredentialTarget = row.Source.CredentialTarget;
        ProxyName                = row.Source.Name;
        ProxyHost                = row.Source.Host;
        ProxyPort                = row.Source.Port.ToString(CultureInfo.InvariantCulture);
        _selectedProxyProtocol   = ProxyProtocolOptions.Single(option => option.Protocol == row.Source.Protocol);
        IsEditingProxy           = true;
        OnPropertyChanged(nameof(SelectedProxyProtocol));
        OnPropertyChanged(nameof(HasSavedCredentials));
        ProxyError = null;
    }

    private Task SaveProxyAsync() => _autoSave.ExecuteAsync(SaveProxyCoreAsync);

    private async Task SaveProxyCoreAsync()
    {
        if (!CanSaveProxy) return;
        Notice = string.Empty;
        var     network             = CurrentNetwork();
        var     id                  = _editingProxyId ?? Guid.NewGuid().ToString("N");
        var     old                 = network.Proxies.SingleOrDefault(proxy => proxy.Id == id);
        string? newCredentialTarget = null;
        string? credentialTarget    = null;
        try
        {
            if (ProxyUsername.Length > 0 || ProxyPassword.Length > 0)
            {
                if (!_credentials.IsSupported)
                    throw new PlatformNotSupportedException("当前系统不支持安全保存代理认证，请移除认证或改用受支持的系统凭据存储。");
                newCredentialTarget = $"TinyHarness:NetworkProxy:{id}:{Guid.NewGuid():N}";
                _credentials.Save(newCredentialTarget, ProxyCredentialCodec.Encode(ProxyUsername, ProxyPassword));
            }

            credentialTarget = newCredentialTarget ?? (ClearSavedCredentials ? null : _editingCredentialTarget);
            var proxy = new ProxyDefinition
            {
                Id               = id,
                Name             = ProxyName.Trim(),
                Host             = ProxyHost.Trim(),
                Port             = int.Parse(ProxyPort, NumberStyles.None, CultureInfo.InvariantCulture),
                Protocol         = SelectedProxyProtocol!.Protocol,
                CredentialTarget = credentialTarget
            };
            var proxies = old is null
                ? network.Proxies.Append(proxy).ToArray()
                : network.Proxies.Select(item => item.Id == id ? proxy : item).ToArray();
            var candidate = WithNetwork(_baseline, network with
            {
                Proxies = proxies,
                DefaultRoute = BuildDefaultRouteFor(proxies)
            });
            await SaveConfigAsync(candidate);
        }
        catch
        {
            if (newCredentialTarget is not null)
                try
                {
                    _credentials.Delete(newCredentialTarget);
                }
                catch (Exception)
                {
                }

            throw;
        }

        if (!string.IsNullOrWhiteSpace(old?.CredentialTarget) && old.CredentialTarget != credentialTarget)
            try
            {
                _credentials.Delete(old.CredentialTarget);
            }
            catch (Exception)
            {
                Notice = "代理已保存，但旧认证凭据未能从系统凭据存储清除。";
            }

        CancelProxyEdit();
        if (!HasNotice) Notice = "代理配置已保存。";
    }

    private NetworkRoute BuildDefaultRouteFor(IReadOnlyList<ProxyDefinition> proxies)
    {
        if (_defaultMode != "proxy") return BuildDefaultRoute();
        if (!proxies.Any(proxy => proxy.Id == _defaultProxyId))
            throw new InvalidDataException("请选择一个已保存的代理作为全局默认。");
        return new NetworkRoute { Mode = NetworkMode.Proxy, ProxyId = _defaultProxyId };
    }

    private Task DeleteProxyAsync(NetworkProxyRowViewModel row) =>
        _autoSave.ExecuteAsync(() => DeleteProxyCoreAsync(row));

    private async Task DeleteProxyCoreAsync(NetworkProxyRowViewModel row)
    {
        Notice = string.Empty;
        var references = ReferencesTo(row.Source.Id);
        if (references.Count > 0)
        {
            Notice = $"无法删除代理“{row.Source.Name}”；仍被{string.Join("、", references)}引用。";
            return;
        }

        var network = CurrentNetwork();
        var proxies = network.Proxies.Where(proxy => proxy.Id != row.Source.Id).ToArray();
        var nextRoute = _defaultMode == "proxy" && _defaultProxyId == row.Source.Id
            ? new NetworkRoute()
            : BuildDefaultRouteFor(proxies);
        var candidate = WithNetwork(_baseline, network with { Proxies = proxies, DefaultRoute = nextRoute });
        await SaveConfigAsync(candidate);
        if (!string.IsNullOrWhiteSpace(row.Source.CredentialTarget))
            try
            {
                _credentials.Delete(row.Source.CredentialTarget);
            }
            catch (Exception)
            {
                Notice = $"代理“{row.Source.Name}”已删除，但认证凭据未能从系统凭据存储清除。";
                return;
            }

        Notice = $"代理“{row.Source.Name}”已删除。";
    }

    private IReadOnlyList<string> ReferencesTo(string id)
    {
        var references = new List<string>();
        if (_baseline.Settings?.Network?.DefaultRoute is { Mode: NetworkMode.Proxy } route && route.ProxyId == id)
            references.Add("全局默认网络");
        references.AddRange(_baseline.Profiles.Where(profile => profile.Network is
                                                                    { Mode: NetworkMode.Proxy } providerRoute &&
                                                                providerRoute.ProxyId == id)
                                     .Select(profile => $"Provider {profile.Name}"));
        return references;
    }

    private async Task TestProxyAsync(NetworkProxyRowViewModel row)
    {
        row.IsTesting = true;
        try
        {
            var result = await ProxyConnectivityTester.TestAsync(row.Source, TestTarget, _credentials,
                                                                 CancellationToken.None);
            if (result.HttpsResponseReceived && result.TargetResponseTime is { } responseTime)
                row.TestStatus = $"连接成功，延迟 {Math.Ceiling(responseTime.TotalMilliseconds):0} ms";
            else
                row.TestStatus = result.Error ?? "连接失败。";
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                                       PlatformNotSupportedException)
        {
            row.TestStatus = exception.Message;
        }
        finally
        {
            row.IsTesting = false;
        }
    }

    private NetworkSettings CurrentNetwork() => _baseline.Settings?.Network ?? new NetworkSettings();

    private static UserConfig WithNetwork(UserConfig config, NetworkSettings network) => config with
    {
        Settings = (config.Settings ?? new UserConfigSettings()) with { Network = network }
    };

    private async Task SaveConfigAsync(UserConfig candidate)
    {
        UserConfigValidator.Validate(candidate);
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline   = candidate;
        _isUpdating = true;
        try
        {
            if (!_autoSave.HasChanges) ApplySavedSettings(candidate);
            RebuildRows();
            OnPropertyChanged(nameof(ProxyChoices));
        }
        finally
        {
            _isUpdating = false;
        }

        if (ConfigSaved is { } handlers)
            foreach (Action<UserConfig> handler in handlers.GetInvocationList())
                try
                {
                    handler(candidate);
                }
                catch (Exception)
                {
                    Notice = "网络设置已保存，但其他设置分区未能同步配置基线；重新打开设置可重新加载。";
                }

        try
        {
            await _refresh();
        }
        catch
        {
            Notice = "网络设置已保存，但会话目录刷新失败；请手动刷新。";
        }
    }

    private void NotifyProxyValidationChanged()
    {
        OnPropertyChanged(nameof(FirstProxyValidationError));
        OnPropertyChanged(nameof(CanSaveProxy));
    }

    private void RebuildRows()
    {
        Proxies.Clear();
        foreach (var proxy in CurrentNetwork().Proxies)
        {
            var references = ReferencesTo(proxy.Id);
            Proxies.Add(new NetworkProxyRowViewModel(proxy, references,
                                                     () =>
                                                         OpenEditProxy(Proxies.First(row => row.Source.Id == proxy.Id)),
                                                     row => TestProxyAsync(row), row => DeleteProxyAsync(row)));
        }

        OnPropertyChanged(nameof(HasProxies));
        OnPropertyChanged(nameof(ProxyChoices));
        OnPropertyChanged(nameof(CanSaveDefaultRoute));
    }

    private void ResetProxyEditor()
    {
        _editingProxyId          = null;
        _editingCredentialTarget = null;
        _proxyName               = string.Empty;
        _proxyHost               = string.Empty;
        _proxyPort               = string.Empty;
        _proxyUsername           = string.Empty;
        _proxyPassword           = string.Empty;
        _selectedProxyProtocol   = ProxyProtocolOptions[0];
        _clearSavedCredentials   = false;
        OnPropertyChanged(nameof(ProxyName));
        OnPropertyChanged(nameof(ProxyHost));
        OnPropertyChanged(nameof(ProxyPort));
        OnPropertyChanged(nameof(ProxyUsername));
        OnPropertyChanged(nameof(ProxyPassword));
        OnPropertyChanged(nameof(SelectedProxyProtocol));
        OnPropertyChanged(nameof(HasSavedCredentials));
        OnPropertyChanged(nameof(ClearSavedCredentials));
        OnPropertyChanged(nameof(FirstProxyValidationError));
    }

    private void CancelProxyEdit()
    {
        ResetProxyEditor();
        IsAddingProxy  = false;
        IsEditingProxy = false;
        ProxyError     = null;
    }
}

/// <summary>
///     <para>代理协议选择项。</para>
///     <para>A proxy protocol selector option.</para>
/// </summary>
public sealed record ProxyProtocolChoice(ProxyProtocol Protocol, string Label);

/// <summary>
///     <para>网络设置页中的已保存代理行及操作状态。</para>
///     <para>A saved-proxy row and its operation state in network settings.</para>
/// </summary>
public sealed class NetworkProxyRowViewModel : ObservableObject
{
    private readonly Func<Task> _delete;
    private readonly Action     _edit;
    private readonly Func<Task> _test;
    private          bool       _isTesting;
    private          string     _testStatus = string.Empty;

    internal NetworkProxyRowViewModel(ProxyDefinition source, IReadOnlyList<string> references, Action edit,
                                      Func<NetworkProxyRowViewModel, Task> test,
                                      Func<NetworkProxyRowViewModel, Task> delete)
    {
        Source        = source;
        References    = references;
        _edit         = edit;
        _test         = () => test(this);
        _delete       = () => delete(this);
        EditCommand   = new RelayCommand(_edit, () => !IsTesting);
        TestCommand   = new AsyncCommand(_test, exception => TestStatus   = exception.Message);
        DeleteCommand = new AsyncCommand(_delete, exception => TestStatus = exception.Message);
    }

    public ProxyDefinition Source { get; }

    public string Name => Source.Name;

    public string Details => $"{ProxyProtocolParser.ToValueString(Source.Protocol).ToUpperInvariant()} · " +
                             $"{(Source.Host.Contains(':') ? $"[{Source.Host}]" : Source.Host)}:{Source.Port}";

    public IReadOnlyList<string> References { get; }

    public bool IsReferenced => References.Count > 0;

    public string ReferenceText => IsReferenced ? $"仍被{string.Join("、", References)}引用，无法删除。" : string.Empty;

    public RelayCommand EditCommand { get; }

    public AsyncCommand TestCommand { get; }

    public AsyncCommand DeleteCommand { get; }

    public bool IsTesting
    {
        get => _isTesting;
        internal set
        {
            if (!SetProperty(ref _isTesting, value)) return;
            OnPropertyChanged(nameof(TestButtonText));
            EditCommand.RaiseCanExecuteChanged();
        }
    }

    public string TestButtonText => IsTesting ? "测试中…" : "测试连接";

    public string TestStatus
    {
        get => _testStatus;
        internal set => SetProperty(ref _testStatus, value);
    }
}
