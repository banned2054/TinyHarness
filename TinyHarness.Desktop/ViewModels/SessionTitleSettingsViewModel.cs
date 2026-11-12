using System.Collections.ObjectModel;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>通用页的自动命名模型设置；自动保存到独立配置，不与审批模型共用选择状态。</para>
///     <para>Automatic-title model settings on the general page, saved independently from reviewer selections.</para>
/// </summary>
public sealed class SessionTitleSettingsViewModel : ObservableObject
{
    private readonly SettingsAutoSaveQueue _autoSave;
    private readonly string                _path;
    private          UserConfig            _baseline = new();
    private          bool                  _hasAutoSaveError;
    private          bool                  _loaded;
    private          TitleModelChoice?     _model;
    private          string                _notice = string.Empty;
    private          bool                  _updating;
    private          bool                  _useCurrentModel = true;

    /// <summary>
    ///     <para>使用可信用户配置路径建立命名设置。</para>
    ///     <para>Creates title settings with the trusted user-config path.</para>
    /// </summary>
    public SessionTitleSettingsViewModel(string path)
    {
        _path = path;
        _autoSave = new SettingsAutoSaveQueue(SaveCoreAsync, exception =>
        {
            HasAutoSaveError = true;
            Notice           = $"自动保存失败：{exception.Message}";
        });
        SaveCommand = new AsyncCommand(_autoSave.SaveAsync, exception =>
        {
            HasAutoSaveError = true;
            Notice           = $"自动保存失败：{exception.Message}";
        });
    }

    public ObservableCollection<TitleModelChoice> Models      { get; } = [];
    public AsyncCommand                           SaveCommand { get; }

    public bool UseCurrentModel
    {
        get => _useCurrentModel;
        set
        {
            if (!SetProperty(ref _useCurrentModel, value)) return;
            OnPropertyChanged(nameof(UseFixedModel));
            QueueSave();
        }
    }

    public bool UseFixedModel => !UseCurrentModel;

    public TitleModelChoice? SelectedModel
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value)) QueueSave();
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

    /// <summary>
    ///     <para>只读加载模型目录和命名设置，不请求模型或读取凭据。</para>
    ///     <para>Loads the catalog and title settings without model calls or credential reads.</para>
    /// </summary>
    public Task LoadAsync() => _autoSave.ExecuteAsync(async () =>
    {
        _updating = true;
        try
        {
            _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
            var settings = _baseline.Settings?.SessionTitles ?? new SessionTitleSettings();
            RebuildModels();
            UseCurrentModel = settings.UseCurrentModel;
            SelectedModel = Models.FirstOrDefault(item => item.Profile == settings.Profile &&
                                                          item.Model   == settings.Model);
            Notice = !UseCurrentModel && SelectedModel is null
                ? "原命名模型已不存在，请重新选择；生成失败时继续使用首条输入摘要。"
                : string.Empty;
            HasAutoSaveError = false;
            _loaded          = true;
            _autoSave.Reset();
        }
        finally
        {
            _updating = false;
        }
    });

    /// <summary>
    ///     <para>同步其他设置的已保存基线和模型目录，保留本区尚未提交的选择。</para>
    ///     <para>Syncs saved baselines and catalog changes from other sections while retaining local selections.</para>
    /// </summary>
    public void AcceptBaseline(UserConfig baseline)
    {
        _updating = true;
        try
        {
            var selected = SelectedModel;
            _baseline = baseline;
            RebuildModels();
            SelectedModel = Models.FirstOrDefault(item => item == selected);
        }
        finally
        {
            _updating = false;
        }
    }

    private void RebuildModels()
    {
        Models.Clear();
        foreach (var profile in _baseline.Profiles)
        foreach (var model in profile.Models)
            Models.Add(new TitleModelChoice(profile.Name, model.Id));
    }

    private void QueueSave()
    {
        if (_loaded && !_updating) _autoSave.Request();
    }

    private async Task<bool> SaveCoreAsync()
    {
        HasAutoSaveError = false;
        if (!UseCurrentModel && SelectedModel is null)
        {
            Notice = "请选择命名使用的模型，选择后自动保存。";
            return false;
        }

        var settings = new SessionTitleSettings
        {
            UseCurrentModel = UseCurrentModel,
            Profile         = SelectedModel?.Profile,
            Model           = SelectedModel?.Model
        };
        settings.Validate();
        var candidate = _baseline with
        {
            Settings = (_baseline.Settings ?? new UserConfigSettings()) with { SessionTitles = settings }
        };
        Notice = "正在保存…";
        await UserConfigStore.SaveAsync(_path, candidate, _baseline, CancellationToken.None);
        _baseline = candidate;
        Notice    = string.Empty;
        ConfigSaved?.Invoke(candidate);
        return true;
    }

    /// <summary>
    ///     <para>等待命名设置的自动保存，供离线验证使用。</para>
    ///     <para>Waits for title-settings autosave for offline verification.</para>
    /// </summary>
    internal Task WaitForAutoSaveAsync() => _autoSave.WaitAsync();
}

/// <summary>
///     <para>命名模型的供应商与模型标识，仅用于命名设置。</para>
///     <para>A provider/model identity used only by title settings.</para>
/// </summary>
public sealed record TitleModelChoice(string Profile, string Model)
{
    public string Label => $"{Profile} / {Model}";
}
