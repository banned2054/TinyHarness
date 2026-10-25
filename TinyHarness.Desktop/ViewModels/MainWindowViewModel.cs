using System.Collections.ObjectModel;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>管理会话目录与页面选择；运行资源交给桌面协调层拥有。</para>
///     <para>Manages the session catalog and page selection while the desktop coordinator owns running resources.</para>
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopSessionManager _manager = new();
    private readonly DesktopSessionFactory _factory = new();
    private SessionViewModel?     _selected;
    private string _notice = string.Empty;
    private bool _settingsOpen;
    private bool _closing;

    public MainWindowViewModel()
    {
        Catalog = new CatalogSettingsViewModel(UserConfigPath, InitializeAsync);
        Settings = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(UserConfigPath), Catalog);
        Settings.CloseRequested += (_, _) => SettingsOpen = false;
        NewSessionCommand = new RelayCommand(NewSession);
        OpenSettingsCommand = new RelayCommand(() =>
        {
            SettingsOpen = !SettingsOpen;
            if (SettingsOpen)
            {
                if (SelectedSession is { } session) session.IsModelMenuOpen = false;
                _ = Settings.OpenAsync();
            }
        });
        RefreshCommand = new AsyncCommand(InitializeAsync, exception => Notice = exception.Message);
        NewSession();
    }

    public CatalogSettingsViewModel               Catalog             { get; }
    public SettingsPanelViewModel                 Settings            { get; }
    public ObservableCollection<SessionViewModel> Sessions            { get; } = [];
    public ObservableCollection<ModelChoice>      Models              { get; } = [];
    public RelayCommand                           NewSessionCommand   { get; }
    public RelayCommand                           OpenSettingsCommand { get; }
    public AsyncCommand                           RefreshCommand      { get; }
    public string                                 UserConfigPath      => UserConfigStore.DefaultFilePath();

    public string Notice
    {
        get => _notice;
        set => SetProperty(ref _notice, value);
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => SetProperty(ref _settingsOpen, value);
    }

    public SessionViewModel? SelectedSession
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
                _ = LoadSelectedHistoryAsync(value);
        }
    }

    /// <summary>
    /// <para>读取模型目录与日志目录，不解析凭据、不打开会话或调用模型。</para>
    /// <para>Reads configured models and journal metadata without resolving credentials, opening hosts or calling models.</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_closing) return;
        try
        {
            var user = await UserConfigStore.LoadAsync(UserConfigPath, CancellationToken.None);
            Models.Clear();
            foreach (var profile in user.Profiles)
            foreach (var model in profile.Models)
                Models.Add(new ModelChoice(profile.Name, model.Id, model.DisplayName, model.ReasoningEfforts,
                                           model.DefaultReasoningEffort));
            var directory = user.Settings?.SessionStoreDirectory ?? SessionStorePaths.DefaultDirectory();
            var entries   = await SessionStoreReader.ListAsync(directory, CancellationToken.None);
            if (_closing) return;
            foreach (var entry in entries.OrderByDescending(entry => entry.Session?.UpdatedUtc))
            {
                if (Sessions.Any(session => session.Id == entry.Id)) continue;
                if (entry.Session is not { } stored)
                {
                    Notice = $"部分历史无法读取：{entry.Error}";
                    continue;
                }

                var text  = stored.Turns.FirstOrDefault()?.UserInput;
                var title = string.IsNullOrWhiteSpace(text) ? stored.Id[..8] : text.Split('\n')[0];
                var row = new SessionViewModel(_manager, _factory, stored.WorkspaceRoot, stored.Id, directory,
                                               title[..Math.Min(title.Length, 48)])
                    { ModelOptions = Models, ManageModelsCommand = OpenSettingsCommand };
                // 标签来自最后选择；历史页面不解析凭据，目标缺失也保留页面。
                Sessions.Add(row);
            }

            if (Sessions.Count == 0) NewSession();
            SelectedSession ??= Sessions.FirstOrDefault();
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
            if (Sessions.Count == 0) NewSession();
        }
    }

    private void NewSession()
    {
        if (_closing) return;
        var session = new SessionViewModel(_manager, _factory,
                                           SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory)
            { ModelOptions = Models, ManageModelsCommand = OpenSettingsCommand };
        Sessions.Insert(0, session);
        SelectedSession = session;
    }

    private static async Task LoadSelectedHistoryAsync(SessionViewModel session)
    {
        try { await session.EnsureHistoryLoadedAsync(); }
        catch (Exception exception) { session.ShowError(exception); }
    }

    /// <summary>
    /// <para>停止接收新操作并等待所有会话取消、清理及保存，不因窗口关闭丢弃运行。</para>
    ///     <para>Stops accepting operations and awaits cancellation, cleanup and saving for every session before window shutdown.</para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        Notice   = "正在关闭：等待所有会话取消、工具清理和日志保存…";
        await _manager.DisposeAsync();
    }
}
