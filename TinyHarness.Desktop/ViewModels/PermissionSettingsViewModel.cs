using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>编辑双审批模式的新会话默认值和独立审批模型，并显式确认旧权限配置迁移。</para>
///     <para>
///         Edits two-mode approval defaults and the independent reviewer model, with explicit legacy-config migration
///         confirmation.
///     </para>
/// </summary>
public sealed class PermissionSettingsViewModel : SettingsSectionViewModel
{
    private readonly string             _path;
    private readonly Func<Task>         _refresh;
    private          UserConfig         _baseline = new();
    private          bool               _configMigrationPending;
    private          PermissionModeRow? _default;

    private string               _migrationError = string.Empty;
    private string               _notice         = string.Empty;
    private ApprovalModelChoice? _reviewer;
    private string               _standingTrustError = string.Empty;
    private bool                 _useCurrent         = true;

    /// <summary>
    ///     <para>使用可信配置路径及保存后的刷新回调建立权限分区。</para>
    ///     <para>Creates the permission section with a trusted config path and a post-save refresh callback.</para>
    /// </summary>
    public PermissionSettingsViewModel(string path, Func<Task>? refresh = null) : base("权限与审批")
    {
        _path       = path;
        _refresh    = refresh ?? (() => Task.CompletedTask);
        SaveCommand = new AsyncCommand(SaveAsync, exception => Notice = exception.Message);
        ConfirmMigrationCommand = new AsyncCommand(ConfirmMigrationAsync,
                                                   exception => MigrationError = exception.Message);
        ConfirmStandingTrustCommand = new AsyncCommand(ConfirmStandingTrustAsync,
                                                       exception => StandingTrustError = exception.Message);
        RevokeStandingTrustCommand = new AsyncCommand(RevokeStandingTrustAsync,
                                                      exception => StandingTrustError = exception.Message);
    }

    public ObservableCollection<PermissionModeRow>   Modes                       { get; } = [];
    public ObservableCollection<ApprovalModelChoice> Models                      { get; } = [];
    public AsyncCommand                              SaveCommand                 { get; }
    public AsyncCommand                              ConfirmMigrationCommand     { get; }
    public AsyncCommand                              ConfirmStandingTrustCommand { get; }
    public AsyncCommand                              RevokeStandingTrustCommand  { get; }

    public bool ConfigMigrationPending
    {
        get => _configMigrationPending;
        private set => SetProperty(ref _configMigrationPending, value);
    }

    public string MigrationSummary => ConfigMigrationPending ? BuildMigrationSummary(_baseline) : string.Empty;

    public string MigrationError
    {
        get => _migrationError;
        private set => SetProperty(ref _migrationError, value);
    }

    /// <summary>
    ///     <para>当前基线的常设信任三态；None 表示没有可常设扩大的设置，区块整体隐藏。</para>
    ///     <para>
    ///         The baseline's three-state standing trust; None means no standing expansion exists and the whole
    ///         block stays hidden.
    ///     </para>
    /// </summary>
    public StandingTrustState StandingState =>
        StandingTrustEvaluation.Evaluate(_baseline.Settings?.WindowsSandbox, _baseline.Settings?.Permissions);

    public bool StandingTrustVisible => StandingState != StandingTrustState.None;

    /// <summary>
    ///     <para>检测到常设扩大设置但尚未显式确认（含范围修订漂移）；确认前一律逐次审批。</para>
    ///     <para>
    ///         Standing expansion detected without explicit confirmation (including scope-revision drift);
    ///         everything runs per-call approval until confirmed.
    ///     </para>
    /// </summary>
    public bool StandingTrustPending => StandingState == StandingTrustState.Unconfirmed;

    /// <summary>
    ///     <para>常设授权已确认且范围修订一致；撤销入口始终可用。</para>
    ///     <para>
    ///         The standing authorization is confirmed and the scope revision matches; the revoke entry stays
    ///         available.
    ///     </para>
    /// </summary>
    public bool StandingTrustActive => StandingState == StandingTrustState.Confirmed;

    public string StandingTrustSummary => StandingTrustVisible ? BuildStandingTrustSummary(_baseline) : string.Empty;

    public string StandingTrustError
    {
        get => _standingTrustError;
        private set => SetProperty(ref _standingTrustError, value);
    }

    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public PermissionModeRow? DefaultMode
    {
        get => _default;
        set => SetProperty(ref _default, value);
    }

    public bool UseCurrentModel
    {
        get => _useCurrent;
        set
        {
            if (SetProperty(ref _useCurrent, value)) OnPropertyChanged(nameof(UseFixedModel));
        }
    }

    public bool UseFixedModel => !UseCurrentModel;

    public ApprovalModelChoice? ReviewerModel
    {
        get => _reviewer;
        set => SetProperty(ref _reviewer, value);
    }

    public event Action<UserConfig>? ConfigSaved;

    /// <summary>
    ///     <para>只读加载设置与模型目录，不读取凭据或请求模型。</para>
    ///     <para>Loads settings and the model catalog without credentials or model requests.</para>
    /// </summary>
    public async Task LoadAsync()
    {
        _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        var settings = _baseline.Settings?.Permissions ?? new PermissionSettings();
        Modes.Clear();
        ConfigMigrationPending = settings.PendingMigration;
        // 旧配置只用于确定迁移默认项；权限配置确认单独完成。
        foreach (var mode in SessionApprovalModes.All)
            Modes.Add(new PermissionModeRow(mode));
        DefaultMode = Modes.Single(row => row.Mode ==
                                          (settings.PendingMigration
                                              ? LegacyPermissionSelectionMapper
                                                   .FromLegacyName(settings.LegacyDefaultMode)
                                             ?? SessionApprovalMode.HumanApproval
                                              : settings.DefaultApprovalMode));
        Models.Clear();
        foreach (var profile in _baseline.Profiles)
        foreach (var model in profile.Models)
            Models.Add(new ApprovalModelChoice(profile.Name, model.Id));
        UseCurrentModel = settings.UseCurrentModel;
        ReviewerModel = Models.FirstOrDefault(choice => choice.Profile == settings.ReviewerProfile &&
                                                        choice.Model   == settings.ReviewerModel);
        MigrationError     = string.Empty;
        StandingTrustError = string.Empty;
        OnPropertyChanged(nameof(MigrationSummary));
        OnPropertyChanged(nameof(StandingTrustVisible));
        OnPropertyChanged(nameof(StandingTrustPending));
        OnPropertyChanged(nameof(StandingTrustActive));
        OnPropertyChanged(nameof(StandingTrustSummary));
        Notice = !UseCurrentModel && ReviewerModel is null
            ? "原审批模型已不存在，请重新选择；AI 审核不可用时操作保持未执行。"
            : string.Empty;
    }

    /// <summary>
    ///     <para>同步其他设置分区保存后的基线和模型候选，不覆盖当前用户选择。</para>
    ///     <para>
    ///         Synchronizes the baseline and model choices after another section saves without replacing the current
    ///         selection.
    ///     </para>
    /// </summary>
    public void AcceptBaseline(UserConfig baseline)
    {
        var selected = ReviewerModel;
        _baseline              = baseline;
        ConfigMigrationPending = baseline.Settings?.Permissions?.PendingMigration == true;
        Models.Clear();
        foreach (var profile in baseline.Profiles)
        foreach (var model in profile.Models)
            Models.Add(new ApprovalModelChoice(profile.Name, model.Id));
        ReviewerModel = Models.FirstOrDefault(choice => choice == selected);
        OnPropertyChanged(nameof(MigrationSummary));
        OnPropertyChanged(nameof(StandingTrustVisible));
        OnPropertyChanged(nameof(StandingTrustPending));
        OnPropertyChanged(nameof(StandingTrustActive));
        OnPropertyChanged(nameof(StandingTrustSummary));
    }

    /// <summary>
    ///     <para>带基线校验保存；默认模式始终可见，失败保留编辑内容。</para>
    ///     <para>Saves with baseline checking, keeping the default visible and retaining edits on failure.</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (DefaultMode is null) throw new InvalidOperationException("请选择默认权限模式。");
        var existing = _baseline.Settings?.Permissions ?? new PermissionSettings();
        var settings = ConfigMigrationPending
            ? existing with
            {
                UseCurrentModel = UseCurrentModel,
                ReviewerProfile = ReviewerModel?.Profile,
                ReviewerModel = ReviewerModel?.Model,
                PendingMigration = true
            }
            : new PermissionSettings
            {
                // 普通保存不解禁也不撤销：常设信任确认记录原样保留。
                StandingTrustConfirmation = existing.StandingTrustConfirmation,
                DefaultApprovalMode       = DefaultMode.Mode,
                UseCurrentModel           = UseCurrentModel,
                ReviewerProfile           = ReviewerModel?.Profile,
                ReviewerModel             = ReviewerModel?.Model
            };
        settings.Validate();
        var candidate = _baseline with
        {
            Settings = (_baseline.Settings ?? new UserConfigSettings()) with { Permissions = settings }
        };
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline              = candidate;
        ConfigMigrationPending = settings.PendingMigration;
        OnPropertyChanged(nameof(MigrationSummary));
        Notice = ConfigMigrationPending
            ? "其他权限设置已保存；旧配置仍待单独确认迁移。"
            : "已保存。默认模式用于新会话，已有会话保留自己的模式。";
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    /// <summary>
    ///     <para>显式确认配置迁移；保留其他配置字段并基于加载基线保存，成功后才清除待确认态。</para>
    ///     <para>
    ///         Explicitly confirms config migration, preserving other fields and saving against the loaded baseline; pending
    ///         state clears only after success.
    ///     </para>
    /// </summary>
    public async Task ConfirmMigrationAsync()
    {
        if (!ConfigMigrationPending) throw new InvalidOperationException("当前没有待确认的权限配置迁移。");
        if (DefaultMode is null) throw new InvalidOperationException("请选择迁移后的默认审批模式。");
        var current = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        if (!string.Equals(BuildMigrationSummary(_baseline), BuildMigrationSummary(current), StringComparison.Ordinal))
        {
            AcceptBaseline(current);
            throw new InvalidOperationException("可信沙箱配置已改变，请检查更新后的迁移说明并再次确认。");
        }

        var settings = new PermissionSettings
        {
            // 迁移确认只重写旧形状字段；常设信任确认记录原样保留。
            StandingTrustConfirmation = current.Settings?.Permissions?.StandingTrustConfirmation,
            DefaultApprovalMode       = DefaultMode.Mode,
            UseCurrentModel           = UseCurrentModel,
            ReviewerProfile           = ReviewerModel?.Profile,
            ReviewerModel             = ReviewerModel?.Model
        };
        settings.Validate();
        var candidate = _baseline with
        {
            Settings = (_baseline.Settings ?? new UserConfigSettings()) with { Permissions = settings }
        };
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline              = candidate;
        ConfigMigrationPending = false;
        MigrationError         = string.Empty;
        OnPropertyChanged(nameof(MigrationSummary));
        Notice = "权限配置迁移已确认；新会话默认与沙箱设置已保留。";
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    /// <summary>
    ///     <para>
    ///         显式确认常设授权：核对基线后落盘确认记录（时间与范围修订），成功后才进入持久授权态。
    ///     </para>
    ///     <para>
    ///         Explicitly confirms the standing authorization: after a baseline check it persists the confirmation
    ///         record (time and scope revision); the persistent-authorization state starts only after success.
    ///     </para>
    /// </summary>
    public async Task ConfirmStandingTrustAsync()
    {
        if (StandingState != StandingTrustState.Unconfirmed)
            throw new InvalidOperationException("当前没有待确认的常设信任。");
        var current = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        if (!string.Equals(BuildStandingTrustSummary(_baseline), BuildStandingTrustSummary(current),
                           StringComparison.Ordinal))
        {
            AcceptBaseline(current);
            throw new InvalidOperationException("可信沙箱配置已改变，请检查更新后的常设授权说明并再次确认。");
        }

        var sandbox = current.Settings?.WindowsSandbox ??
                      throw new InvalidOperationException("可信沙箱设置不存在，无法确认常设授权。");
        var settings = (current.Settings?.Permissions ?? new PermissionSettings()) with
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                      StandingTrustEvaluation.ScopeRevision(sandbox))
        };
        settings.Validate();
        var candidate = current with
        {
            Settings = (current.Settings ?? new UserConfigSettings()) with { Permissions = settings }
        };
        await UserConfigStore.SaveAsync(_path, candidate, current, CancellationToken.None);
        _baseline          = candidate;
        StandingTrustError = string.Empty;
        Notice             = "常设授权已确认；联网与附加写根按显式持久授权运行，撤销入口随时可用。";
        OnPropertyChanged(nameof(StandingTrustVisible));
        OnPropertyChanged(nameof(StandingTrustPending));
        OnPropertyChanged(nameof(StandingTrustActive));
        OnPropertyChanged(nameof(StandingTrustSummary));
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    /// <summary>
    ///     <para>
    ///         撤销常设授权：网络策略回到 restricted 并清空附加写根，同时删除确认记录；附加读根保持原值。
    ///     </para>
    ///     <para>
    ///         Revokes the standing authorization: the network policy returns to restricted and the additional
    ///         write roots are cleared together with the confirmation record; additional read roots stay unchanged.
    ///     </para>
    /// </summary>
    public async Task RevokeStandingTrustAsync()
    {
        if (StandingState == StandingTrustState.None)
            throw new InvalidOperationException("当前没有可撤销的常设授权。");
        var current = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        var sandbox = current.Settings?.WindowsSandbox ??
                      throw new InvalidOperationException("可信沙箱设置不存在，无法撤销常设授权。");
        var revokedSandbox = sandbox with
        {
            NetworkPolicy = SandboxNetworkPolicy.Restricted,
            AdditionalWriteRoots = []
        };
        var settings = (current.Settings?.Permissions ?? new PermissionSettings()) with
        {
            StandingTrustConfirmation = null
        };
        settings.Validate();
        var candidate = current with
        {
            Settings = (current.Settings ?? new UserConfigSettings()) with
            {
                WindowsSandbox = revokedSandbox,
                Permissions = settings
            }
        };
        await UserConfigStore.SaveAsync(_path, candidate, current, CancellationToken.None);
        _baseline          = candidate;
        StandingTrustError = string.Empty;
        Notice             = "常设授权已撤销：网络策略回到 restricted，附加写根已清空。";
        OnPropertyChanged(nameof(StandingTrustVisible));
        OnPropertyChanged(nameof(StandingTrustPending));
        OnPropertyChanged(nameof(StandingTrustActive));
        OnPropertyChanged(nameof(StandingTrustSummary));
        ConfigSaved?.Invoke(candidate);
        await _refresh();
    }

    /// <summary>
    ///     <para>
    ///         构建常设授权区块说明：范围（网络与写根列表）、敏感位置提示、有效期与当前状态；
    ///         确认前的基线核对也依赖同一文本。
    ///     </para>
    ///     <para>
    ///         Builds the standing-authorization block text: scope (network and write-root list), the
    ///         sensitive-location hint, the validity, and the current state; the pre-confirmation baseline check
    ///         relies on the same text.
    ///     </para>
    /// </summary>
    internal static string BuildStandingTrustSummary(UserConfig config)
    {
        var sandbox = config.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        var state = StandingTrustEvaluation.Evaluate(config.Settings?.WindowsSandbox, config.Settings?.Permissions);
        var confirmation = config.Settings?.Permissions?.StandingTrustConfirmation;
        static string Roots(IReadOnlyList<string> roots) => roots.Count == 0 ? "无" : string.Join("；", roots);
        return "授权范围：常设网络=enabled——已配置代理时经该代理且坏代理快速失败不回退直连；未配置代理时为直连全网。\n"      +
               $"附加写根：{Roots(sandbox.AdditionalWriteRoots)}。\n"                 +
               $"附加读根：{Roots(sandbox.AdditionalReadRoots)}（确认后随持久授权注入沙箱 OS 读面，" +
               "profile 外的根真实可读；应用层文件工具读不受影响）。\n"                               +
               "注意：持久根可能包含敏感位置；确认前联网与附加写根写按逐次审批执行。\n"                           +
               "有效期：持久至撤销（撤销=回到 restricted 并清空附加写根，读根不再注入）。\n"                  +
               state switch
               {
                   StandingTrustState.Confirmed =>
                       $"当前状态：已确认，按显式持久授权运行（确认时间 {confirmation?.ConfirmedUtc:yyyy-MM-dd HH:mm 'UTC'}；" +
                       "设置页与审批展示可见）。",
                   StandingTrustState.Unconfirmed when confirmation is not null =>
                       "当前状态：设置范围与确认记录不一致，需重新确认（当前联网与附加写根写按逐次审批执行）。",
                   _ => "当前状态：待确认（当前联网与附加写根写按逐次审批执行）。"
               };
    }

    internal static string BuildMigrationSummary(UserConfig config)
    {
        var sandbox = config.Settings?.WindowsSandbox ?? new WindowsSandboxSettings();
        var network = sandbox.NetworkPolicy switch
        {
            SandboxNetworkPolicy.Enabled    => "Enabled（所有沙箱命令常设全网，无需逐次网络审批）",
            SandboxNetworkPolicy.Restricted => "Restricted（外部网络受限，单次联网仍需审批）",
            _                               => $"未知值：{sandbox.NetworkPolicy}"
        };
        static string Roots(IReadOnlyList<string> roots) => roots.Count == 0 ? "无" : string.Join("；", roots);
        var proxy = sandbox.Proxy.Mode == SandboxProxyMode.Named
            ? $"Named（{sandbox.Proxy.ProxyName ?? "未命名"}）"
            : sandbox.Proxy.Mode.ToString();
        return "新权限基线：工作区内读写和沙箱 Shell 自动允许；附加权限按人工审批或 AI 审核处理。"                                           +
               "FullAccess 已删除，不会自动取得 Host 执行；旧临时授权和 commandRules 免审批规则不继承。\n"                            +
               $"以下可信沙箱配置保持原值：常设网络策略 {network}；"                                                          +
               $"附加只读根：{Roots(sandbox.AdditionalReadRoots)}；附加写入根：{Roots(sandbox.AdditionalWriteRoots)}；" +
               $"子进程代理模式：{proxy}（代理地址和凭据不显示）。";
    }
}

/// <summary>
///     <para>一个审批模式的说明。</para>
///     <para>An approval mode description.</para>
/// </summary>
public sealed class PermissionModeRow(SessionApprovalMode mode) : ObservableObject
{
    public SessionApprovalMode Mode        { get; } = mode;
    public string              Label       => Mode.Label();
    public string              Description => Mode.Description();
}

/// <summary>
///     <para>审批模型的供应商与 API ID，不包含凭据。</para>
///     <para>A reviewer provider and API ID without credentials.</para>
/// </summary>
public sealed record ApprovalModelChoice(string Profile, string Model)
{
    public string Label => $"{Profile} / {Model}";
}
