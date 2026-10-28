using System.Collections.ObjectModel;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>管理会话目录与页面选择，并编排旧日志归属的自动整理；运行资源交给桌面协调层拥有。</para>
///     <para>
///         Manages the session catalog and page selection and orchestrates legacy-log ownership; the desktop
///         coordinator owns running resources.
///     </para>
/// </summary>
public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly Func<Action, ValueTask>?      _dispatch;
    private readonly DesktopSessionFactory         _factory;
    private readonly CancellationTokenSource       _lifetime    = new();
    private readonly DesktopSessionManager         _manager     = new();
    private readonly SemaphoreSlim                 _refreshGate = new(1, 1);
    private          bool                          _closing;
    private          string?                       _initializationCursor;
    private          bool                          _loadingCatalog;
    private          int                           _migrationBusy;
    private          IReadOnlyList<OrganizeIssue>? _organizeIssues;
    private          string?                       _organizeIssuesStore;
    private          CancellationTokenSource?      _organizeSource;
    private          TaskCompletionSource?         _organizeFinished;
    private          int                           _organizeGeneration;
    private          string                        _organizeNotice = string.Empty;
    private          bool                          _loadingHistory;
    private          string                        _notice = string.Empty;
    private          int                           _renameDialogVersion;
    private          ProjectViewModel?             _renameTarget;
    private          bool                          _renamingProject;
    private          string                        _projectRenameDraft = string.Empty;
    private          string?                       _projectRenameError;
    private          bool                          _projectRenameOpen;
    private          IReadOnlyList<Project>        _projects = [];
    private          SessionViewModel?             _selected;
    private          bool                          _settingsOpen;
    private          string?                       _storeDirectory;

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
        RefreshCommand = new AsyncCommand(RefreshAsync, exception => Notice = exception.Message);
        InitializeProjectsCommand = new AsyncCommand(InitializeProjectsAsync, exception => Notice = exception.Message);
        CancelInitializationCommand = new RelayCommand(() => _organizeSource?.Cancel());
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

    /// <summary>
    ///     <para>启动阶段是否正在加载历史；轻量准备与后台整理期间为真，统一加载完成后复位。</para>
    ///     <para>
    ///         Whether startup is still loading history; true during light preparation and the background round, reset
    ///         after the unified load.
    ///     </para>
    /// </summary>
    public bool LoadingHistory
    {
        get => _loadingHistory;
        private set => SetProperty(ref _loadingHistory, value);
    }

    /// <summary>
    ///     <para>历史整理摘要：成功静默；待处理、失败或异常时给出简短原因，不覆盖其他操作的错误。</para>
    ///     <para>
    ///         Organize summary: silent on success, a short reason while items stay pending or fail; it never
    ///         overwrites other operations' errors.
    ///     </para>
    /// </summary>
    public string OrganizeNotice
    {
        get => _organizeNotice;
        private set
        {
            if (!SetProperty(ref _organizeNotice, _factory.Redact(value))) return;
            // 可见性是计算属性：文本变化时一并通知，侧栏摘要的 IsVisible 绑定才能翻转。
            OnPropertyChanged(nameof(HasOrganizeNotice));
        }
    }

    public bool HasOrganizeNotice => _organizeNotice.Length > 0;

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
    ///     <para>
    ///         停止接收新操作；先取消并等待在途整理整轮退出（不只等某一批），再等待所有会话取消、清理及保存。
    ///     </para>
    ///     <para>
    ///         Stops accepting operations; cancels and awaits the whole in-flight organize round first, then every
    ///         session's cancellation, cleanup and saving.
    ///     </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _closing = true;
        await _lifetime.CancelAsync();
        // linked 取消源已随 lifetime 触发；显式取消覆盖未关联路径，随后等待迁移真正退出。
        _organizeSource?.Cancel();
        if (_organizeFinished is { } finished) await finished.Task;
        Notice = "正在关闭：等待所有会话取消、工具清理和日志保存…";
        await _manager.DisposeAsync();
    }

    /// <summary>
    ///     <para>只读加载项目、会话与历史，不触发迁移；生效 store 真正变化时取消旧整理并开始新一轮。</para>
    ///     <para>
    ///         Read-only load of projects, sessions and history that never organizes; a real store change cancels the
    ///         old round and starts a new one.
    ///     </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_closing) return;
        var changed = false;
        var store   = string.Empty;
        await _refreshGate.WaitAsync(_lifetime.Token);
        _loadingCatalog = true;
        try
        {
            (store, changed) = await PrepareAsync();
            var entries = await SessionStoreReader.ListAsync(store, _lifetime.Token);
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
                                                       ProjectRoots.AreEqual(source, store)) is { } existing)
                {
                    existing.ApplyStoredMetadata(stored.ProjectId, stored.LastActivityUtc);
                    continue;
                }

                var text  = stored.Turns.FirstOrDefault()?.UserInput;
                var title = string.IsNullOrWhiteSpace(text) ? stored.Id[..8] : text.Split('\n')[0];
                var row = new SessionViewModel(_manager, _factory, stored.WorkspaceRoot, stored.Id, store,
                                               title[..Math.Min(title.Length, 48)], _dispatch, stored.ProjectId,
                                               stored.LastActivityUtc)
                    { ModelOptions = Models, ManageModelsCommand = OpenSettingsCommand };
                // 标签来自最后选择；历史页面不解析凭据，目标缺失也保留页面。
                AddSession(row);
            }

            if (Sessions.Count == 0) NewSession();
            SelectedSession ??= Sessions.FirstOrDefault();
            await RebindUnboundDraftsAsync(store);
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

        // 锁外协调：先等被取消的旧轮退出，再按“store 真正变化”触发新一轮；本轮结束后的刷新不会再次进入这里。
        if (changed)
        {
            await StopOrganizeMigrationAsync();
            _ = StartOrganizeRound(store);
        }
    }

    /// <summary>
    ///     <para>
    ///         窗口显示后的启动序列：轻量准备不被历史扫描阻塞，后台自动整理一轮，结束后统一加载项目与历史。
    ///     </para>
    ///     <para>
    ///         Startup sequence after the window is shown: light preparation never waits for the scan, one background
    ///         organize round runs, then projects and history load once.
    ///     </para>
    /// </summary>
    public async Task StartAsync()
    {
        if (_closing) return;
        LoadingHistory = true;
        try
        {
            var (store, _) = await InitializeLightAsync();
            await StopOrganizeMigrationAsync();
            // 未能启动整轮（迁移在途或无生效 store）时直接统一加载，保证历史仍然出现。
            var round = StartOrganizeRound(store);
            if (round is null) await InitializeAsync();
            else await round;
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
        finally
        {
            LoadingHistory = false;
        }
    }

    /// <summary>
    ///     <para>用户刷新：既重读项目与历史，也重试未完成的整理；在途整轮合并，不重复启动。</para>
    ///     <para>User refresh: reloads projects and history and retries an unfinished round; a running round is merged.</para>
    /// </summary>
    public async Task RefreshAsync()
    {
        await InitializeAsync();
        // 整轮自行管理异常与取消；这里只负责触发，不等待其完成。
        _ = StartOrganizeRound(_storeDirectory);
    }

    /// <summary>
    ///     <para>启动轻量准备：配置、模型、生效 store 与项目册；不读取日志正文，不等待历史扫描。</para>
    ///     <para>
    ///         Light startup preparation: config, models, the effective store and the catalog, without reading log
    ///         bodies or waiting for a history scan.
    ///     </para>
    /// </summary>
    private async Task<(string Store, bool StoreChanged)> InitializeLightAsync()
    {
        await _refreshGate.WaitAsync(_lifetime.Token);
        _loadingCatalog = true;
        try
        {
            var (store, changed) = await PrepareAsync();
            if (_closing) return (store, changed);
            await RebindUnboundDraftsAsync(store);
            RebuildProjects();
            if (Sessions.Count == 0) NewSession();
            SelectedSession ??= Sessions.FirstOrDefault();
            return (store, changed);
        }
        finally
        {
            _loadingCatalog = false;
            _refreshGate.Release();
        }
    }

    /// <summary>
    ///     <para>读取配置与模型目录、解析生效 store 并载入项目册；不读取日志正文。</para>
    ///     <para>Loads config, models, the effective store and the catalog; log bodies are never read here.</para>
    /// </summary>
    private async Task<(string Store, bool StoreChanged)> PrepareAsync()
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
        var changed = _storeDirectory is not null && !ProjectRoots.AreEqual(_storeDirectory, directory);
        if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory))
            _initializationCursor = null;
        if (changed)
        {
            // 切换 store：取消在途迁移（调用方锁外等待退出），并清空上一 store 的整理展示残留。
            _organizeSource?.Cancel();
            ResetOrganizeDisplay();
        }
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

        return (directory, changed);
    }

    /// <summary>切换 store 后清空上一 store 的整理展示残留；新一轮结束时会重新汇总。</summary>
    private void ResetOrganizeDisplay()
    {
        _organizeIssues      = null;
        _organizeIssuesStore = null;
        OrganizeNotice       = string.Empty;
    }

    /// <summary>
    ///     <para>为仍无归属的草稿按当前目录册重新解析项目；守卫与草稿首次绑定一致，不覆盖用户已改状态。</para>
    ///     <para>
    ///         Re-resolves a project for drafts still without ownership against the current catalog; the guards match
    ///         first-time draft binding and never overwrite user changes.
    ///     </para>
    /// </summary>
    private async Task RebindUnboundDraftsAsync(string directory)
    {
        foreach (var draft in Sessions.Where(session => session.Id is null).ToArray())
        {
            if (_closing || draft is not { Id: null, ProjectId: null, CanChooseWorkspace: true }) continue;
            if (draft.StoreDirectory is { } bound && !ProjectRoots.AreEqual(bound, directory)) continue;
            var root    = draft.WorkspaceRoot;
            var project = _projects.FirstOrDefault(item =>
                item.Roots.Any(projectRoot => ProjectRoots.AreEqual(projectRoot.Path, root)));
            if (project is null) continue;
            await draft.SelectProjectAsync(project.Id, root, directory);
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
                !_projects.Any(project => project.Id == id) && !_closing && !_loadingCatalog &&
                _organizeSource is null)
                _ = InitializeAsync();
        };
        if (!_loadingCatalog) RebuildProjects();
    }

    private void RebuildProjects()
    {
        var previous = Projects.ToDictionary(group => group.Key);
        var groups   = new Dictionary<string, ProjectViewModel>();

        ProjectViewModel Group(string key, Project? project, string name, string root, string? detail = null)
        {
            if (!groups.TryGetValue(key, out var group))
            {
                var existing = previous.GetValueOrDefault(key);
                group = (existing is not null && (existing.Project is null) == (project is null) ? existing : null) ??
                        new ProjectViewModel(key, project, name, root,
                                             NewProjectSessionAsync,
                                             exception => Notice = exception.Message);
                // 本轮首次遇到该分组时清一次成员；同组逐会话追加成员时不得再次清空。
                group.Sessions.Clear();
                groups.Add(key, group);
            }

            if (project is not null) group.Update(project);
            else group.RefreshDisplay(name, detail);
            return group;
        }

        foreach (var project in _projects)
            Group(project.Id, project, project.Name, project.PrimaryRoot);

        // 未发送且未归属的草稿独立成组；纯内存投影，不创建项目、日志或宿主。
        var drafts = Sessions.Where(session => session.Id is null && session.ProjectId is null).ToList();
        if (drafts.Count > 0)
        {
            var group = Group("drafts", null, "草稿", string.Empty);
            foreach (var draft in drafts) group.Sessions.Add(draft);
        }

        // 已归属草稿与已保存会话同样进入所属项目分组；只有未归属草稿留在上面的草稿组。
        foreach (var session in Sessions.Where(session => session.Id is not null || session.ProjectId is not null)
                                       .OrderByDescending(session => session.LastActivityUtc))
        {
            var project = _projects.FirstOrDefault(project => project.Id == session.ProjectId);
            // 未归属的存储行构造即带 store，且根目录固定后不可再清空（见 SessionViewModel.WorkspaceRoot 守卫）；
            // 分组键纳入 store，两个 store 的同一执行目录不得并成一组，整理明细才能按 store 精确定位。
            string key;
            if (session.ProjectId is { } projectId)
                key = projectId;
            else if (session.StoreDirectory is { } pendingStore)
                key = PendingGroupKey(pendingStore, session.WorkspaceRoot);
            else
                key = "pending:" + ProjectRoots.Normalize(session.WorkspaceRoot);
            string name;
            string? detail = null;
            if (project is not null) name = project.Name;
            else if (session.ProjectId is not null) name = "项目信息缺失";
            else
            {
                // 旧日志未归属：整理结束时仍有待处理或失败项才标记“整理未完成”，否则只按目录展示。
                detail = OrganizeIssueFor(session.StoreDirectory, session.WorkspaceRoot);
                name   = detail is null ? session.WorkspaceLabel : "整理未完成 · " + session.WorkspaceLabel;
            }

            Group(key, project, name, session.WorkspaceRoot, detail).Sessions.Add(session);
        }

        Projects.Clear();
        foreach (var group in groups.Values.OrderByDescending(group =>
                     group.Sessions.Select(session => session.LastActivityUtc)
                          .DefaultIfEmpty(group.Project?.CreatedUtc ?? DateTimeOffset.MinValue).Max()))
            Projects.Add(group);
        RebuildSidebarRows();
    }

    /// <summary>
    ///     <para>未归属旧日志的侧栏分组键：长度前缀分隔 store 与执行目录，避免路径中合法分隔符字符造成键歧义。</para>
    ///     <para>Group key for unbound legacy rows: a length prefix separates store and root so legal path separators cannot collide.</para>
    /// </summary>
    private static string PendingGroupKey(string store, string workspaceRoot)
    {
        var normalizedStore = ProjectRoots.Normalize(store);
        return "pending:" + normalizedStore.Length + ":" + normalizedStore + ProjectRoots.Normalize(workspaceRoot);
    }

    /// <summary>
    ///     <para>按会话 store 与执行目录查询整理未完成明细；行 store 与结果所属 store 不一致时不得串用原因。</para>
    ///     <para>Looks up unfinished-organize detail by the row's store and root; a store mismatch never reuses another store's reason.</para>
    /// </summary>
    private string? OrganizeIssueFor(string? sessionStore, string workspaceRoot)
    {
        if (_organizeIssues is null || _organizeIssuesStore is not { } issueStore ||
            sessionStore is not { } row || !ProjectRoots.AreEqual(row, issueStore))
            return null;
        var normalized = ProjectRoots.Normalize(workspaceRoot);
        foreach (var issue in _organizeIssues)
            if (ProjectRoots.AreEqual(issue.Root, normalized))
                return issue.Detail;
        return null;
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
        // OpenProjectAsync 同样可能观察到配置变化：生效 store 真正变化时取消旧轮并重启整理。
        var changed = _storeDirectory is not null && !ProjectRoots.AreEqual(_storeDirectory, directory);
        if (changed)
        {
            _organizeSource?.Cancel();
            _initializationCursor = null;
            ResetOrganizeDisplay();
        }
        _storeDirectory = directory;
        _projects       = (await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token)).Projects;
        if (_closing) return;
        if (changed) await StopOrganizeMigrationAsync();
        if (draft is not { CanChooseWorkspace: true })
        {
            NewSession();
            draft = SelectedSession!;
        }

        await draft.SelectProjectAsync(project.Id, project.PrimaryRoot, directory);
        RebuildProjects();
        if (changed) _ = StartOrganizeRound(directory);
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
    ///     <para>保留的单批显式入口：与自动整轮共用单飞与游标；已有迁移在途时直接合并返回。</para>
    ///     <para>
    ///         The retained explicit single-batch entry: it shares the single-flight and cursor with the automatic
    ///         round and merges into any migration already running.
    ///     </para>
    /// </summary>
    public async Task InitializeProjectsAsync()
    {
        if (!TryBeginMigration(out var source, out var finished)) return;
        SessionProjectInitializationResult result;
        try
        {
            var directory =
                await ResolveProtectedDirectoryAsync(SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory,
                                                     source.Token);
            if (!Directory.Exists(directory)) return;
            if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory))
                _initializationCursor = null;
            _storeDirectory = directory;
            result = await SessionProjectInitializer.InitializeAsync(directory, _initializationCursor, 100,
                                                                     cancellationToken : source.Token);
            _initializationCursor = result.HasMore ? result.NextCursor : null;
        }
        finally
        {
            EndMigration(source, finished);
        }

        // 单批入口保留逐批全库刷新；整轮编排不使用该路径，避免每批重读整个 store。
        await InitializeAsync();
        Notice = $"项目初始化：{result.Outcomes.Count(item => item.Status == SessionProjectBindingStatus.Bound)} 已绑定，" +
                 $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Pending)} 待处理，" +
                 $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Failed)} 失败；"   +
                 (result.Cancelled ? "已取消，可继续。" : result.HasMore ? "仍有日志，可继续。" : "本轮扫描完成，可重试待处理项。")                +
                 string.Join("", result.Outcomes.Where(item => item.Detail is not null)
                                       .Select(item => $"\n{item.SessionId}: {item.Detail}"));
    }

    private const int OrganizeBatchSize = 100;

    /// <summary>一条整理未完成明细：按目录定位的原因摘要，仅用于侧栏展示。</summary>
    private sealed record OrganizeIssue(string Root, string Detail);

    /// <summary>占用迁移单飞槽位；已有迁移（整轮或保留的单批入口）在途时返回 false。</summary>
    private bool TryBeginMigration(out CancellationTokenSource source, out TaskCompletionSource finished)
    {
        if (_closing || Interlocked.CompareExchange(ref _migrationBusy, 1, 0) != 0)
        {
            source   = null!;
            finished = null!;
            return false;
        }

        source            = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        finished          = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _organizeSource   = source;
        _organizeFinished = finished;
        return true;
    }

    /// <summary>释放迁移槽位并发出结束信号；仍由本迁移占用时清空在途字段。</summary>
    private void EndMigration(CancellationTokenSource source, TaskCompletionSource finished)
    {
        if (ReferenceEquals(_organizeSource, source))
        {
            _organizeSource   = null;
            _organizeFinished = null;
        }

        Interlocked.Exchange(ref _migrationBusy, 0);
        finished.TrySetResult();
    }

    /// <summary>
    ///     <para>取消在途迁移并等待其退出；不得在持有刷新锁时调用，避免与迁移尾部刷新互等。</para>
    ///     <para>
    ///         Cancels the in-flight migration and awaits its exit; never call while holding the refresh gate, or the
    ///         migration's own final refresh would deadlock against it.
    ///     </para>
    /// </summary>
    private async Task StopOrganizeMigrationAsync()
    {
        if (_organizeSource is not { } source) return;
        source.Cancel();
        if (_organizeFinished is { } finished) await finished.Task;
    }

    /// <summary>
    ///     <para>启动一轮后台自动整理；已有迁移在途或无生效 store 时返回 null（合并或放弃）。</para>
    ///     <para>
    ///         Starts one background organize round; returns null when a migration is already running or no effective
    ///         store is known.
    ///     </para>
    /// </summary>
    private Task? StartOrganizeRound(string? store)
    {
        if (store is null || !TryBeginMigration(out var source, out var finished)) return null;
        _organizeGeneration++;
        var round = RunOrganizeRoundAsync(source, store, _organizeGeneration, finished);
        // 整轮可能在同步前缀内直接结束（如存储目录缺失）；仅当槽位仍由本轮占用时记录并返回任务。
        if (!ReferenceEquals(_organizeSource, source)) return null;
        return round;
    }

    /// <summary>
    ///     <para>
    ///         循环分批整理当前 store：每批复用 <see cref="SessionProjectInitializer" /> 及其返回游标，取消即停；
    ///         所有异常都在本任务内观察转换，调用方持有的返回值只用于等待结束。
    ///     </para>
    ///     <para>
    ///         Organizes the store batch by batch through <see cref="SessionProjectInitializer" /> and its returned
    ///         cursor, stopping on cancellation; every exception is observed and converted inside this task.
    ///     </para>
    /// </summary>
    private async Task RunOrganizeRoundAsync(CancellationTokenSource source, string store, int generation,
                                             TaskCompletionSource finished)
    {
        string? failureSummary = null;
        var merged = new Dictionary<string, SessionProjectBindingOutcome>(StringComparer.Ordinal);
        try
        {
            // 写入前复用与显式入口一致的完整保护校验：轻量准备只解析路径，不构成创建目录册或追加归属的授权；
            // 沙箱附加写根等边界只有这里与显式入口检查，Core 仅校验各日志自己的执行根。
            var protectedStore = await ResolveProtectedDirectoryAsync(
                SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory, source.Token);
            if (!ProjectRoots.AreEqual(protectedStore, store))
            {
                // 配置在本轮启动后变化（含 trim 后与轻量解析不一致）：不写捕获的 store，按未完成报告而非静默成功；
                // 迟到防护保证只有最新代次且 store 未变的轮次才更新共享展示，不覆盖新轮的结果。
                if (IsCurrentStore(store, generation))
                    OrganizeNotice = "历史整理未完成：会话存储解析结果与生效目录不一致，本轮未写入。";
            }
            // 空 store／缺失目录正常结束：不为扫描创建目录、项目、日志或锁文件。
            else if (Directory.Exists(store))
            {
                var cursor = (string?)null;
                while (!_closing)
                {
                    // 批内扫描与迁移放工作线程：Core 首个 await 前有同步枚举与排序，不占用 UI 线程。
                    var result = await Task.Run(() => SessionProjectInitializer.InitializeAsync(
                                                    store, cursor, OrganizeBatchSize,
                                                    cancellationToken : source.Token), source.Token);
                    // 迟到防护：store 已切换的批次不再推进共享游标。
                    if (IsCurrentStore(store, generation))
                        _initializationCursor = result.HasMore ? result.NextCursor : null;
                    foreach (var outcome in result.Outcomes) merged[outcome.SessionId] = outcome;
                    // 取消必须先于停滞防御判定：Core 取消且本批无事实时 NextCursor 保持原值，
                    // 正常取消不是停滞也不产生诊断（成功静默，取消不是异常）。
                    if (result.Cancelled) break;

                    // 防御：仍有后续但游标未推进时终止，避免无限循环。
                    if (result.HasMore && string.Equals(result.NextCursor, cursor, StringComparison.Ordinal))
                    {
                        if (IsCurrentStore(store, generation)) OrganizeNotice = "历史整理已停止：扫描游标未推进。";
                        break;
                    }

                    if (!result.HasMore) break;
                    cursor = result.NextCursor;
                }

                if (IsCurrentStore(store, generation)) PublishOrganizeOutcome(merged.Values);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消只影响写入；尾部统一加载照常执行，无需记录状态。
        }
        catch (Exception exception)
        {
            // 整轮异常只进独立摘要通道，不覆盖其他操作的错误；结束后仍按当前状态统一加载一次。
            if (IsCurrentStore(store, generation))
            {
                failureSummary = _factory.Redact(exception.Message);
                OrganizeNotice = $"历史整理未完成：{failureSummary}";
                // 异常轮按已观测事实结算明细：先清掉本 store 上一轮残留，已见批次的待处理／失败照常按目录汇总；
                // 未扫到的目录取不到按目录事实，统一加载后由下方 failureSummary 分支用摘要补标。
                _organizeIssues      = BuildOrganizeIssues(merged.Values);
                _organizeIssuesStore = _storeDirectory;
            }
        }
        finally
        {
            // 先释放单飞并发出结束信号，再做尾部统一加载；关闭因此不必等待刷新即可继续清理。
            EndMigration(source, finished);
        }

        // 取消只停止本轮写入：统一只读加载仍是整理后的必经步骤，否则侧栏停在空态；迟到防护由 _closing 与代次保持。
        if (!_closing && IsCurrentStore(store, generation))
        {
            try
            {
                await InitializeAsync();
                if (failureSummary is not null && IsCurrentStore(store, generation))
                    MarkUnboundOrganizeIssues(store, failureSummary);
            }
            catch (OperationCanceledException)
            {
                // 关闭竞态中的取消不构成错误。
            }
        }
    }

    /// <summary>迟到防护：整轮结果只在其代次仍是最新且 store 未变化时进入共享展示状态。</summary>
    private bool IsCurrentStore(string store, int generation) =>
        generation == _organizeGeneration && _storeDirectory is { } current &&
        ProjectRoots.AreEqual(current, store);

    /// <summary>汇总本轮仍未绑定的事实：成功静默；待处理与失败给出简短摘要及按目录可定位的明细。</summary>
    private void PublishOrganizeOutcome(IEnumerable<SessionProjectBindingOutcome> outcomes)
    {
        var stuck = outcomes.Where(outcome => outcome.Status is SessionProjectBindingStatus.Pending
                                                                  or SessionProjectBindingStatus.Failed).ToArray();
        if (stuck.Length == 0)
        {
            ResetOrganizeDisplay();
            return;
        }

        var pending = stuck.Count(item => item.Status == SessionProjectBindingStatus.Pending);
        OrganizeNotice       = $"历史整理未完成：{pending} 项待处理、{stuck.Length - pending} 项失败。";
        _organizeIssues      = BuildOrganizeIssues(outcomes);
        _organizeIssuesStore = _storeDirectory;
    }

    /// <summary>
    ///     <para>把仍待处理或失败的事实按目录归并为侧栏「整理未完成」明细；无法定位目录的事实只进摘要。</para>
    ///     <para>Groups pending or failed facts into per-directory unfinished details; facts without a locatable root stay in the summary only.</para>
    /// </summary>
    private IReadOnlyList<OrganizeIssue> BuildOrganizeIssues(IEnumerable<SessionProjectBindingOutcome> outcomes)
    {
        var groups = new List<(string Root, List<string> Lines)>();
        foreach (var item in outcomes)
        {
            if (item.Status is not (SessionProjectBindingStatus.Pending or SessionProjectBindingStatus.Failed))
                continue;
            // 读取失败等无法定位目录的事实只进摘要，不挂到任何目录组。
            if (string.IsNullOrWhiteSpace(item.WorkspaceRoot)) continue;
            var normalized = ProjectRoots.Normalize(item.WorkspaceRoot);
            var index      = groups.FindIndex(group => ProjectRoots.AreEqual(group.Root, normalized));
            if (index < 0)
            {
                groups.Add((normalized, []));
                index = groups.Count - 1;
            }

            var lines = groups[index].Lines;
            if (lines.Count >= 4)
            {
                if (lines[^1] != "…") lines.Add("…");
                continue;
            }

            lines.Add($"{(item.Status == SessionProjectBindingStatus.Pending ? "待处理" : "失败")}：" +
                      TruncateDetail(_factory.Redact(item.Detail ?? item.Status.ToString())));
        }

        return groups.Select(group => new OrganizeIssue(group.Root, string.Join('\n', group.Lines))).ToArray();
    }

    /// <summary>
    ///     <para>异常轮统一补标：为本轮 store 内统一加载后仍未绑定且尚无明细的旧日志目录追加本轮异常摘要作为原因。</para>
    ///     <para>
    ///         Shared marking after an exceptional round: appends the round's failure summary to this round's store's
    ///         legacy directories still unbound and without per-directory facts.
    ///     </para>
    /// </summary>
    private void MarkUnboundOrganizeIssues(string store, string detail)
    {
        var issues = _organizeIssues is { } existing ? new List<OrganizeIssue>(existing) : [];
        // 只补标本轮 store 的行：切换 store 后旧页面保留原 store 绑定，旧 store 的未绑定组不归本轮解释。
        foreach (var session in Sessions.Where(session => session.Id is not null            &&
                                                           session.ProjectId is null         &&
                                                           session.StoreDirectory is { } row &&
                                                           ProjectRoots.AreEqual(row, store)))
        {
            var normalized = ProjectRoots.Normalize(session.WorkspaceRoot);
            if (issues.Any(issue => ProjectRoots.AreEqual(issue.Root, normalized))) continue;
            issues.Add(new OrganizeIssue(normalized, detail));
        }

        _organizeIssues      = issues.ToArray();
        _organizeIssuesStore = _storeDirectory;
        RebuildProjects();
    }

    private static string TruncateDetail(string detail) =>
        detail.Length <= 200 ? detail : detail[..200] + "…";

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
