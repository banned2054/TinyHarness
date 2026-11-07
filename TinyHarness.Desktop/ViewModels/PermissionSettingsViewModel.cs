using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

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
    private bool                 _useCurrent = true;

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
    }

    public ObservableCollection<PermissionModeRow>   Modes                   { get; } = [];
    public ObservableCollection<ApprovalModelChoice> Models                  { get; } = [];
    public AsyncCommand                              SaveCommand             { get; }
    public AsyncCommand                              ConfirmMigrationCommand { get; }

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
        MigrationError = string.Empty;
        OnPropertyChanged(nameof(MigrationSummary));
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
                DefaultApprovalMode = DefaultMode.Mode,
                UseCurrentModel     = UseCurrentModel,
                ReviewerProfile     = ReviewerModel?.Profile,
                ReviewerModel       = ReviewerModel?.Model
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
            DefaultApprovalMode = DefaultMode.Mode,
            UseCurrentModel     = UseCurrentModel,
            ReviewerProfile     = ReviewerModel?.Profile,
            ReviewerModel       = ReviewerModel?.Model
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
