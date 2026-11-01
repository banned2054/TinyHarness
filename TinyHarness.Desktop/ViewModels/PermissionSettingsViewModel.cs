using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>编辑七种模式的 Composer 可见性、新会话默认值和独立审批模型。</para>
///     <para>Edits composer visibility, new-session defaults, and the independent reviewer model for seven modes.</para>
/// </summary>
public sealed class PermissionSettingsViewModel : SettingsSectionViewModel
{
    private readonly string               _path;
    private readonly Func<Task>           _refresh;
    private          UserConfig           _baseline = new();
    private          PermissionModeRow?   _default;
    private          string               _notice = string.Empty;
    private          ApprovalModelChoice? _reviewer;
    private          bool                 _useCurrent = true;

    /// <summary>
    ///     <para>使用可信配置路径及保存后的刷新回调建立权限分区。</para>
    ///     <para>Creates the permission section with a trusted config path and a post-save refresh callback.</para>
    /// </summary>
    public PermissionSettingsViewModel(string path, Func<Task>? refresh = null) : base("权限与审批")
    {
        _path       = path;
        _refresh    = refresh ?? (() => Task.CompletedTask);
        SaveCommand = new AsyncCommand(SaveAsync, exception => Notice = exception.Message);
    }

    public ObservableCollection<PermissionModeRow>   Modes       { get; } = [];
    public ObservableCollection<ApprovalModelChoice> Models      { get; } = [];
    public AsyncCommand                              SaveCommand { get; }

    public string Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public PermissionModeRow? DefaultMode
    {
        get => _default;
        set
        {
            if (SetProperty(ref _default, value) && value is not null) value.ShowInComposer = true;
        }
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

    /// <summary>
    ///     <para>只读加载设置与模型目录，不读取凭据或请求模型。</para>
    ///     <para>Loads settings and the model catalog without credentials or model requests.</para>
    /// </summary>
    public async Task LoadAsync()
    {
        _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        var settings = _baseline.Settings?.Permissions ?? new PermissionSettings();
        Modes.Clear();
        foreach (var mode in SessionPermissionModes.All)
            Modes.Add(new PermissionModeRow(mode) { ShowInComposer = settings.ComposerModes.Contains(mode) });
        DefaultMode = Modes.Single(row => row.Mode == settings.DefaultMode);
        Models.Clear();
        foreach (var profile in _baseline.Profiles)
        foreach (var model in profile.Models)
            Models.Add(new ApprovalModelChoice(profile.Name, model.Id));
        UseCurrentModel = settings.UseCurrentModel;
        ReviewerModel = Models.FirstOrDefault(choice => choice.Profile == settings.ReviewerProfile &&
                                                        choice.Model   == settings.ReviewerModel);
        Notice = !UseCurrentModel && ReviewerModel is null
            ? "原审批模型已不存在，请重新选择；此时 AI 审批会转交用户。"
            : string.Empty;
    }

    /// <summary>
    ///     <para>带基线校验保存；默认模式始终可见，失败保留编辑内容。</para>
    ///     <para>Saves with baseline checking, keeping the default visible and retaining edits on failure.</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (DefaultMode is null) throw new InvalidOperationException("请选择默认权限模式。");
        DefaultMode.ShowInComposer = true;
        var settings = new PermissionSettings
        {
            DefaultMode     = DefaultMode.Mode,
            ComposerModes   = Modes.Where(row => row.ShowInComposer).Select(row => row.Mode).ToArray(),
            UseCurrentModel = UseCurrentModel,
            ReviewerProfile = ReviewerModel?.Profile,
            ReviewerModel   = ReviewerModel?.Model
        };
        settings.Validate();
        var candidate = _baseline with
        {
            Settings = (_baseline.Settings ?? new UserConfigSettings()) with { Permissions = settings }
        };
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline = candidate;
        Notice    = "已保存。默认模式用于新会话，已有会话保留自己的模式。";
        await _refresh();
    }
}

/// <summary>
///     <para>一个模式的说明与 Composer 可见性编辑项。</para>
///     <para>A mode description and its editable composer visibility.</para>
/// </summary>
public sealed class PermissionModeRow(SessionPermissionMode mode) : ObservableObject
{
    private bool                  _show;
    public  SessionPermissionMode Mode        { get; } = mode;
    public  string                Label       => Mode.Label();
    public  string                Description => Mode.Description();

    public bool ShowInComposer
    {
        get => _show;
        set => SetProperty(ref _show, value);
    }
}

/// <summary>
///     <para>审批模型的供应商与 API ID，不包含凭据。</para>
///     <para>A reviewer provider and API ID without credentials.</para>
/// </summary>
public sealed record ApprovalModelChoice(string Profile, string Model)
{
    public string Label => $"{Profile} / {Model}";
}
