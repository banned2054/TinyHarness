using System.Collections.ObjectModel;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>管理会话目录与页面选择；运行资源交给桌面协调层拥有。</para>
///     <para>Manages the session catalog and page selection while the desktop coordinator owns running resources.</para>
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<Action, ValueTask>? _dispatch;
    private readonly DesktopSessionFactory    _factory;
    private readonly CancellationTokenSource  _lifetime    = new();
    private readonly DesktopSessionManager    _manager     = new();
    private readonly SemaphoreSlim            _refreshGate = new(1, 1);
    private          bool                     _closing;
    private          CancellationTokenSource? _initialization;
    private          string?                  _initializationCursor;
    private          TaskCompletionSource?    _initializationFinished;
    private          bool                     _loadingCatalog;
    private          string                   _notice = string.Empty;
    private          int                      _renameDialogVersion;
    private          ProjectViewModel?        _renameTarget;
    private          bool                     _renamingProject;
    private          string                   _projectRenameDraft = string.Empty;
    private          string?                  _projectRenameError;
    private          bool                     _projectRenameOpen;
    private          IReadOnlyList<Project>   _projects = [];
    private          SessionViewModel?        _selected;
    private          bool                     _settingsOpen;
    private          string?                  _storeDirectory;

    public MainWindowViewModel() : this(new DesktopSessionFactory(), UserConfigStore.DefaultFilePath())
    {
    }

    public MainWindowViewModel(DesktopSessionFactory    factory, string userConfigPath,
                               Func<Action, ValueTask>? dispatch = null)
    {
        _factory = factory;
        _dispatch = dispatch;
        UserConfigPath = userConfigPath;
        Catalog = new CatalogSettingsViewModel(UserConfigPath, InitializeAsync);
        Settings = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(UserConfigPath), Catalog);
        Settings.CloseRequested += (_, _) => SettingsOpen = false;
        NewSessionCommand = new RelayCommand(NewSession);
        SelectSessionCommand = new RelayCommand<SessionViewModel>(session => SelectedSession = session);
        ToggleProjectCommand = new RelayCommand<ProjectViewModel>(group =>
        {
            if (group is null) return;
            group.IsExpanded = !group.IsExpanded;
            RebuildSidebarRows();
        });
        OpenProjectRenameCommand =
            new RelayCommand<ProjectViewModel>(OpenProjectRename, group => group is { CanEdit: true });
        ConfirmProjectRenameCommand = new RelayCommand(() => _ = ConfirmProjectRenameAsync());
        CancelProjectRenameCommand  = new RelayCommand(CancelProjectRename);
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
        InitializeProjectsCommand = new AsyncCommand(InitializeProjectsAsync, exception => Notice = exception.Message);
        CancelInitializationCommand = new RelayCommand(() => _initialization?.Cancel());
        NewSession();
    }

    public CatalogSettingsViewModel               Catalog  { get; }
    public SettingsPanelViewModel                 Settings { get; }
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ProjectViewModel> Projects { get; } = [];
    public ObservableCollection<ModelChoice>      Models   { get; } = [];

    /// <summary>侧栏呈现行：项目组头行与成员会话行混排的铺平投影，按各组的展开态生成。</summary>
    public ObservableCollection<object> SidebarRows { get; } = [];

    public RelayCommand                   NewSessionCommand           { get; }
    public RelayCommand<SessionViewModel> SelectSessionCommand        { get; }
    public RelayCommand<ProjectViewModel> ToggleProjectCommand        { get; }
    public RelayCommand<ProjectViewModel> OpenProjectRenameCommand    { get; }
    public RelayCommand                   ConfirmProjectRenameCommand { get; }
    public RelayCommand                   CancelProjectRenameCommand  { get; }
    public RelayCommand                   OpenSettingsCommand         { get; }
    public AsyncCommand                   RefreshCommand              { get; }
    public string                         UserConfigPath              { get; }
    public AsyncCommand                   InitializeProjectsCommand   { get; }
    public RelayCommand                   CancelInitializationCommand { get; }

    public string Notice
    {
        get => _notice;
        set => SetProperty(ref _notice, _factory.Redact(value));
    }

    public bool SettingsOpen
    {
        get => _settingsOpen;
        set => SetProperty(ref _settingsOpen, value);
    }

    /// <summary>项目重命名弹窗是否打开（组头菜单打开，浅失焦或取消关闭）。</summary>
    public bool IsProjectRenameOpen
    {
        get => _projectRenameOpen;
        private set => SetProperty(ref _projectRenameOpen, value);
    }

    /// <summary>项目重命名输入草稿：弹窗打开时预填当前名称；trim 后为确认与校验依据。</summary>
    public string ProjectRenameDraftText
    {
        get => _projectRenameDraft;
        set
        {
            if (!SetProperty(ref _projectRenameDraft, value ?? string.Empty)) return;
            // 输入变化即重算本地校验；上一次确认失败的错误随之让位。
            _projectRenameError = null;
            NotifyProjectRenameValidation();
        }
    }

    /// <summary>项目重命名错误提示：目录册保存失败在弹窗内呈现，可修改后重试。</summary>
    public string? ProjectRenameErrorText => _projectRenameError;

    public bool HasProjectRenameError => _projectRenameError is not null;

    /// <summary>确认重命名是否可用：存在目标、名称非空、与当前名不同且不在途。</summary>
    public bool CanConfirmProjectRename =>
        !_renamingProject &&
        _renameTarget is not null &&
        ProjectRenameDraftText.Trim().Length > 0 &&
        ProjectRenameDraftText.Trim() != _renameTarget.Name;

    public SessionViewModel? SelectedSession
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            if (_selected is { } previous) previous.IsSelected = false;
            if (!SetProperty(ref _selected, value) || value is null) return;
            value.IsSelected = true;
            RebuildSidebarRows();
            _ = LoadSelectedHistoryAsync(value);
        }
    }

    /// <summary>
    ///     <para>停止接收新操作并等待所有会话取消、清理及保存，不因窗口关闭丢弃运行。</para>
    ///     <para>
    ///         Stops accepting operations and awaits cancellation, cleanup and saving for every session before window
    ///         shutdown.
    ///     </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _lifetime.CancelAsync();
        if (_initializationFinished is { } finished) await finished.Task;
        Notice = "正在关闭：等待所有会话取消、工具清理和日志保存…";
        await _manager.DisposeAsync();
    }

    /// <summary>
    ///     <para>读取模型目录与日志目录，不解析凭据、不打开会话或调用模型。</para>
    ///     <para>Reads configured models and journal metadata without resolving credentials, opening hosts or calling models.</para>
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_closing) return;
        await _refreshGate.WaitAsync(_lifetime.Token);
        _loadingCatalog = true;
        try
        {
            var user = await UserConfigStore.LoadAsync(UserConfigPath, _lifetime.Token);
            Models.Clear();
            foreach (var profile in user.Profiles)
            foreach (var model in profile.Models)
                Models.Add(new ModelChoice(profile.Name, model.Id, model.DisplayName, model.ReasoningEfforts,
                                           model.DefaultReasoningEffort));
            foreach (var draft in Sessions.Where(session => session.Id is null).ToArray())
                await draft.RefreshDefaultModelAsync();
            var directory = user.Settings?.SessionStoreDirectory ?? SessionStorePaths.DefaultDirectory();
            directory = Path.GetFullPath(directory);
            if (_storeDirectory != directory) _initializationCursor = null;
            _storeDirectory = directory;
            try
            {
                _projects = (await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token)).Projects;
            }
            catch (Exception exception) when (exception is ProjectStoreException or IOException
                                               or UnauthorizedAccessException)
            {
                _projects = [];
                Notice    = $"项目信息无法读取：{exception.Message}";
            }

            var entries = await SessionStoreReader.ListAsync(directory, _lifetime.Token);
            if (_closing) return;
            foreach (var entry in entries.OrderByDescending(entry => entry.Session?.LastActivityUtc))
            {
                if (entry.Session is not { } stored)
                {
                    Notice = $"部分历史无法读取：{entry.Error}";
                    continue;
                }

                // 会话身份按 store 限定；已有行仅刷新归属与活动元数据，不重建实例、宿主、草稿和历史投影。
                if (Sessions.FirstOrDefault(session => session.Id == entry.Id               &&
                                                       session.StoreDirectory is { } source &&
                                                       ProjectRoots.AreEqual(source, directory)) is { } existing)
                {
                    existing.ApplyStoredMetadata(stored.ProjectId, stored.LastActivityUtc);
                    continue;
                }

                var text  = stored.Turns.FirstOrDefault()?.UserInput;
                var title = string.IsNullOrWhiteSpace(text) ? stored.Id[..8] : text.Split('\n')[0];
                var row = new SessionViewModel(_manager, _factory, stored.WorkspaceRoot, stored.Id, directory,
                                               title[..Math.Min(title.Length, 48)], _dispatch, stored.ProjectId,
                                               stored.LastActivityUtc)
                    { ModelOptions = Models, ManageModelsCommand = OpenSettingsCommand };
                // 标签来自最后选择；历史页面不解析凭据，目标缺失也保留页面。
                AddSession(row);
            }

            if (Sessions.Count == 0) NewSession();
            SelectedSession ??= Sessions.FirstOrDefault();
            RebuildProjects();
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
            if (Sessions.Count == 0) NewSession();
        }
        finally
        {
            _loadingCatalog = false;
            _refreshGate.Release();
        }
    }

    private void NewSession()
    {
        if (_closing) return;
        // 新草稿继承所选执行目录；存储与项目绑定按当前有效配置解析，不复制旧页面可能过期的绑定。
        var session = new SessionViewModel(_manager, _factory,
                                           SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory,
                                           null, null, null, _dispatch, null, null)
            { ModelOptions = Models, ManageModelsCommand = OpenSettingsCommand };
        AddSession(session);
        SelectedSession = session;
        _               = session.RefreshDefaultModelAsync();
        _               = BindNewDraftAsync(session);
    }

    /// <summary>
    ///     <para>按当前有效存储为新草稿解析目录与项目；目录册尚无对应项目时不预写，留待首次发送时绑定。</para>
    ///     <para>
    ///         Resolves the directory and project for a new draft against the effective store; a project absent from
    ///         the catalog is left for the first send to bind.
    ///     </para>
    /// </summary>
    private async Task BindNewDraftAsync(SessionViewModel session)
    {
        try
        {
            var root      = session.WorkspaceRoot;
            var directory = await ResolveProtectedDirectoryAsync(root, _lifetime.Token);
            // 项目只取当前目录册中可靠存在的定义；草稿不创建项目或会话日志。
            var project = _storeDirectory is { } current && ProjectRoots.AreEqual(current, directory)
                ? _projects.FirstOrDefault(item =>
                    item.Roots.Any(projectRoot => ProjectRoots.AreEqual(projectRoot.Path, root)))
                : null;
            // 页面已显式选择项目、开始发送或改过执行目录时，保留其自己的绑定。
            if (_closing                                                           ||
                !session.CanChooseWorkspace                                        ||
                session is not { Id: null, ProjectId: null, StoreDirectory: null } ||
                session.WorkspaceRoot != root)
                return;
            await session.SelectProjectAsync(project?.Id, root, directory);
        }
        catch (OperationCanceledException)
        {
            // 关闭过程中的取消不构成错误。
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
    }

    private void AddSession(SessionViewModel session)
    {
        Sessions.Insert(0, session);
        session.PropertyChanged += (_, args) =>
        {
            if (!_loadingCatalog &&
                args.PropertyName is nameof(SessionViewModel.ProjectId) or nameof(SessionViewModel.LastActivityUtc))
                RebuildProjects();
            if (args.PropertyName == nameof(SessionViewModel.ProjectId) && session.ProjectId is { } id &&
                !_projects.Any(project => project.Id == id) && !_closing && !_loadingCatalog && _initialization is null)
                _ = InitializeAsync();
        };
        if (!_loadingCatalog) RebuildProjects();
    }

    private void RebuildProjects()
    {
        var previous = Projects.ToDictionary(group => group.Key);
        var groups   = new Dictionary<string, ProjectViewModel>();

        ProjectViewModel Group(string key, Project? project, string name, string root)
        {
            if (groups.TryGetValue(key, out var group)) return group;
            var existing = previous.GetValueOrDefault(key);
            group = (existing is not null && (existing.Project is null) == (project is null) ? existing : null) ??
                    new ProjectViewModel(key, project, name, root,
                                         NewProjectSessionAsync,
                                         exception => Notice = exception.Message);
            if (project is not null) group.Update(project);
            group.Sessions.Clear();
            groups.Add(key, group);
            return group;
        }

        foreach (var project in _projects)
            Group(project.Id, project, project.Name, project.PrimaryRoot);
        foreach (var session in Sessions.OrderByDescending(session => session.LastActivityUtc))
        {
            var project = _projects.FirstOrDefault(project => project.Id == session.ProjectId);
            var key     = session.ProjectId ?? "pending:" + ProjectRoots.Normalize(session.WorkspaceRoot);
            var name    = project?.Name ?? (session.ProjectId is null ? "待初始化 · " + session.WorkspaceLabel : "项目信息缺失");
            Group(key, project, name, session.WorkspaceRoot).Sessions.Add(session);
        }

        Projects.Clear();
        foreach (var group in groups.Values.OrderByDescending(group =>
                     group.Sessions.Where(session => session.Id is not null).Select(session => session.LastActivityUtc)
                          .DefaultIfEmpty(group.Project?.CreatedUtc ?? DateTimeOffset.MinValue).Max()))
            Projects.Add(group);
        RebuildSidebarRows();
    }

    /// <summary>
    ///     <para>把项目分组与成员会话铺平为侧栏呈现行；折叠组只保留组头行。</para>
    ///     <para>Flattens project groups and member sessions into sidebar rows; collapsed groups keep only their header.</para>
    /// </summary>
    private void RebuildSidebarRows()
    {
        foreach (var group in Projects) group.NotifyCurrentChanged();
        var rows = new List<object>(Projects.Count + Sessions.Count);
        foreach (var group in Projects)
        {
            rows.Add(group);
            if (group.IsExpanded) rows.AddRange(group.Sessions);
        }

        // 保留未变行与控件；刷新不 Reset，排序只移动、增删实际变化的行，避免整表重建闪动。
        var retained = new HashSet<object>(rows);
        for (var index = SidebarRows.Count - 1; index >= 0; index--)
            if (!retained.Contains(SidebarRows[index]))
                SidebarRows.RemoveAt(index);
        for (var index = 0; index < rows.Count; index++)
        {
            if (index < SidebarRows.Count && ReferenceEquals(SidebarRows[index], rows[index])) continue;
            var previousIndex = SidebarRows.IndexOf(rows[index]);
            if (previousIndex >= 0) SidebarRows.Move(previousIndex, index);
            else SidebarRows.Insert(index, rows[index]);
        }
    }

    /// <summary>
    ///     <para>显式打开目录并保存项目；只创建或切换草稿，不建立会话日志。</para>
    ///     <para>Explicitly opens a directory and saves its project, creating or moving a draft without a session log.</para>
    /// </summary>
    public async Task OpenProjectAsync(string root)
    {
        if (_closing) return;
        var draft = SelectedSession is { CanChooseWorkspace: true } selected ? selected : null;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var directory = await ResolveProtectedDirectoryAsync(root, _lifetime.Token);
        var project   = await SessionProjectBinding.GetProjectAsync(directory, root, _lifetime.Token);
        _storeDirectory = directory;
        _projects       = (await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token)).Projects;
        if (_closing) return;
        if (draft is not { CanChooseWorkspace: true })
        {
            NewSession();
            draft = SelectedSession!;
        }

        await draft.SelectProjectAsync(project.Id, project.PrimaryRoot, directory);
        RebuildProjects();
    }

    /// <summary>
    ///     <para>在指定项目内新建独立草稿，不打开宿主。</para>
    ///     <para>Creates an independent draft in the chosen project without opening a host.</para>
    /// </summary>
    public async Task NewProjectSessionAsync(ProjectViewModel group)
    {
        if (_closing || group.Project is not { } project) return;
        var directory = await ResolveProtectedDirectoryAsync(project.PrimaryRoot, _lifetime.Token);
        NewSession();
        await SelectedSession!.SelectProjectAsync(project.Id, project.PrimaryRoot, directory);
    }

    /// <summary>
    ///     <para>按显示修订号保存项目名称；冲突不覆盖其他保存。</para>
    ///     <para>Saves a project name against the displayed revision without overwriting conflicting saves.</para>
    /// </summary>
    public async Task RenameProjectAsync(ProjectViewModel group)
    {
        if (_closing || group.Project is not { } project) return;
        var directory = await ResolveProtectedDirectoryAsync(project.PrimaryRoot, _lifetime.Token);
        var renamed = await ProjectStore.RenameAsync(ProjectStore.FilePath(directory), project.Id, group.EditedName,
                                                     project.Revision, cancellationToken : _lifetime.Token);
        _projects = _projects.Select(item => item.Id == renamed.Id ? renamed : item).ToArray();
        group.Update(renamed);
    }

    /// <summary>组头「重命名」：以该组为对象打开重命名弹窗并预填当前名称。</summary>
    private void OpenProjectRename(ProjectViewModel? group)
    {
        if (group is not { CanEdit: true }) return;
        _renameTarget = group;
        // 每次打开都是新的弹窗实例：身份递增，使仍在途的上一次确认结果不落回本弹窗。
        _renameDialogVersion++;
        ProjectRenameDraftText = group.Name;
        _projectRenameError    = null;
        IsProjectRenameOpen    = true;
        // 预填与残留草稿相同时草稿 setter 不通知，开窗即补齐校验通知（确认按钮的可用态）。
        NotifyProjectRenameValidation();
    }

    /// <summary>确认重命名：按显示修订号保存；失败留在弹窗内呈现并可重试。</summary>
    private async Task ConfirmProjectRenameAsync()
    {
        if (_renameTarget is null || !CanConfirmProjectRename) return;
        var group         = _renameTarget;
        var name          = ProjectRenameDraftText.Trim();
        var dialogVersion = _renameDialogVersion;
        _renamingProject = true;
        NotifyProjectRenameValidation();
        try
        {
            group.EditedName = name;
            await RenameProjectAsync(group);
            if (_renameDialogVersion != dialogVersion) return;
            _projectRenameError = null;
            CloseProjectRename();
        }
        catch (Exception exception)
        {
            // 原弹窗已不在时错误只进窗口通知，不得写入当前弹窗（可能是另一个项目）。
            if (_renameDialogVersion != dialogVersion)
            {
                Notice = exception.Message;
                return;
            }

            _projectRenameError = _factory.Redact(exception.Message);
            NotifyProjectRenameValidation();
        }
        finally
        {
            _renamingProject = false;
            NotifyProjectRenameValidation();
        }
    }

    /// <summary>关闭并清理重命名弹窗状态（取消、确认成功与浅失焦共用）。</summary>
    private void CloseProjectRename()
    {
        IsProjectRenameOpen = false;
        _renameTarget       = null;
        _projectRenameError = null;
        NotifyProjectRenameValidation();
    }

    private void CancelProjectRename()
    {
        CloseProjectRename();
    }

    /// <summary>重命名校验相关属性的统一通知点（输入、在途与错误状态变化都会经过）。</summary>
    private void NotifyProjectRenameValidation()
    {
        OnPropertyChanged(nameof(CanConfirmProjectRename));
        OnPropertyChanged(nameof(ProjectRenameErrorText));
        OnPropertyChanged(nameof(HasProjectRenameError));
    }

    private async Task<string> ResolveProtectedDirectoryAsync(string root, CancellationToken token)
    {
        var user      = await UserConfigStore.LoadAsync(UserConfigPath, token);
        var directory = SessionStorePaths.ResolveProtected(user.Settings?.SessionStoreDirectory, root);
        if (user.Settings?.WindowsSandbox is { Enabled: true } sandbox)
            foreach (var writable in sandbox.AdditionalWriteRoots)
                SessionStorePaths.EnsureOutsideWorkspace(directory, writable, "settings.sessionStoreDirectory");
        return directory;
    }

    /// <summary>
    ///     <para>仅由显式操作初始化一批旧日志，保留取消后的结果与游标，不打开模型或工具。</para>
    ///     <para>
    ///         Initializes one batch only by explicit action, retaining outcomes and cursor after cancellation without
    ///         models or tools.
    ///     </para>
    /// </summary>
    public async Task InitializeProjectsAsync()
    {
        if (_closing || _initialization is not null) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _initialization         = source;
        _initializationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var directory =
                await ResolveProtectedDirectoryAsync(SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory,
                                                     source.Token);
            if (!Directory.Exists(directory)) return;
            if (_storeDirectory != directory) _initializationCursor = null;
            _storeDirectory = directory;
            var result = await SessionProjectInitializer.InitializeAsync(directory, _initializationCursor, 100,
                                                                         cancellationToken : source.Token);
            _initializationCursor = result.HasMore ? result.NextCursor : null;
            await InitializeAsync();
            Notice = $"项目初始化：{result.Outcomes.Count(item => item.Status == SessionProjectBindingStatus.Bound)} 已绑定，"   +
                     $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Pending)} 待处理，" +
                     $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Failed)} 失败；"   +
                     (result.Cancelled ? "已取消，可继续。" : result.HasMore ? "仍有日志，可继续。" : "本轮扫描完成，可重试待处理项。")                +
                     string.Join("", result.Outcomes.Where(item => item.Detail is not null)
                                           .Select(item => $"\n{item.SessionId}: {item.Detail}"));
        }
        finally
        {
            _initialization = null;
            _initializationFinished.TrySetResult();
        }
    }

    private static async Task LoadSelectedHistoryAsync(SessionViewModel session)
    {
        try
        {
            await session.EnsureHistoryLoadedAsync();
        }
        catch (Exception exception)
        {
            session.ShowError(exception);
        }
    }
}
