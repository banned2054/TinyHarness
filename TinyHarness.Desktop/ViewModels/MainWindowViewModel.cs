using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Project;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Models;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.Services;

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
    private const           int                      OrganizeBatchSize      = 100;
    private const           int                      ListRowBatchSize       = 200;
    private const           int                      IndexRefreshIntervalMs = 15000;
    private const           int                      ProjectSyncIntervalMs  = IndexRefreshIntervalMs;
    private static readonly SidebarCategoryViewModel PinnedCategory         = new("pinned", "置顶");
    private static readonly SidebarCategoryViewModel ProjectsCategory       = new("projects", "项目");
    private static readonly SidebarCategoryViewModel RecentCategory         = new("recent", "最近");
    private static readonly SidebarCategoryViewModel SessionsCategory       = new("sessions", "会话");
    private static readonly SidebarCategoryViewModel ArchivedCategory       = new("archived", "已归档");

    private static readonly SidebarCategoryViewModel ArchivedEmptyCategory = new("archived-empty", "暂无已归档会话",
        false);

    private static readonly SidebarCategoryViewModel ArchiveUnavailableCategory =
        new("archive-unavailable", "归档状态暂不可用", false);

    private static readonly SidebarCategoryViewModel[] CollapsibleCategories =
    [
        PinnedCategory, ProjectsCategory, RecentCategory, SessionsCategory, ArchivedCategory
    ];

    private readonly HashSet<string> _archiveStateUnavailableStores = new(
                                                                          OperatingSystem.IsWindows()
                                                                              ? StringComparer.OrdinalIgnoreCase
                                                                              : StringComparer.Ordinal);

    private readonly Dictionary<string, SessionStoreArchives> _archiveStatesByStore =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    // 置顶/归档 ID 的按 store 懒构建查询缓存：快照写入点整体 Clear，下一次查询按当前快照重建。
    private readonly Dictionary<string, HashSet<string>> _archivedSessionIdsByStore =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ProjectCatalogMonitor _catalogMonitor = new();

    private readonly Func<Action, ValueTask>?        _dispatch;
    private readonly DesktopSessionFactory           _factory;
    private readonly List<SessionViewModel>          _failedDrafts         = [];
    private readonly CancellationTokenSource         _lifetime             = new();
    private readonly object                          _listProjectionGate   = new();
    private readonly DesktopSessionManager           _manager              = new();
    private readonly ConcurrentQueue<SessionSummary> _pendingSummaryRows   = new();
    private readonly SemaphoreSlim                   _pinStateMutationGate = new(1, 1);

    private readonly HashSet<string> _pinStateUnavailableStores = new(
                                                                      OperatingSystem.IsWindows()
                                                                          ? StringComparer.OrdinalIgnoreCase
                                                                          : StringComparer.Ordinal);

    private readonly Dictionary<string, SessionStorePins> _pinStatesByStore =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly Dictionary<string, HashSet<string>> _pinnedSessionIdsByStore =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly HashSet<string> _projectReloadRetried       = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim   _refreshGate                = new(1, 1);
    private readonly SemaphoreSlim   _sidebarPreferencesLoadGate = new(1, 1);
    private readonly string          _sidebarPreferencesPath     = string.Empty;

    private readonly SemaphoreSlim _sidebarPreferencesSaveGate = new(1, 1);

    // 上次下发的项目菜单数组：内容逐引用相同时复用实例，让 SessionViewModel.ProjectOptions 的引用相等短路跳过整条菜单重建链。
    private IReadOnlyList<ProjectViewModel>?                  _cachedProjectOptions;
    private bool                                              _closing;
    private TaskCompletionSource?                             _indexRefreshFinished;
    private int                                               _indexRefreshGeneration;
    private CancellationTokenSource?                          _indexRefreshSource;
    private string?                                           _initializationCursor;
    private (SessionListProjection Projection, string Store)? _listProjection;
    private bool                                              _loadingCatalog;
    private bool                                              _loadingHistory;
    private int                                               _migrationBusy;
    private SessionViewModel?                                 _newConversation;
    private string                                            _notice = string.Empty;
    private TaskCompletionSource?                             _organizeFinished;
    private int                                               _organizeGeneration;
    private IReadOnlyList<OrganizeIssue>?                     _organizeIssues;
    private string?                                           _organizeIssuesStore;
    private string                                            _organizeNotice = string.Empty;
    private CancellationTokenSource?                          _organizeSource;
    private bool?                                             _pendingProjectDeletionPromptPreference;
    private PermissionSettings                                _permissionSettings = new();
    private int                                               _projectDeletionDialogVersion;
    private string?                                           _projectDeletionErrorText;
    private bool                                              _projectDeletionOpen;
    private ProjectViewModel?                                 _projectDeletionTarget;
    private string                                            _projectRenameDraft = string.Empty;

    private bool _projectRenameOpen;

    // 退避计数跨线程访问：同步循环线程写、UI 线程复位，按 int/bool 写原子性容忍最坏丢失一次计数，不引入同步原语。
    private int                      _projectSyncConsecutiveFailures;
    private bool                     _projectSyncFailureNoticed;
    private TaskCompletionSource?    _projectSyncFinished;
    private CancellationTokenSource? _projectSyncSource;
    private IReadOnlyList<Project>   _projects = [];
    private int                      _renameDialogVersion;
    private ProjectViewModel?        _renameTarget;
    private bool                     _renamingProject;
    private SessionViewModel?        _selected;
    private bool                     _settingsOpen;
    private Task?                    _shutdown;
    private SidebarPreferences       _sidebarPreferences = new();
    private bool                     _sidebarPreferencesSaveAllowed;
    private bool                     _sidebarSettingsMenuOpen;
    private string?                  _storeDirectory;
    private int                      _storeRevision;
    private int                      _summaryRowPumpBusy;

    public MainWindowViewModel() : this(new DesktopSessionFactory(), UserConfigStore.DefaultFilePath())
    {
    }

    public MainWindowViewModel(DesktopSessionFactory    factory, string userConfigPath,
                               Func<Action, ValueTask>? dispatch = null)
    {
        _factory                = factory;
        _dispatch               = dispatch;
        UserConfigPath          = userConfigPath;
        _sidebarPreferencesPath = SidebarPreferencesStore.FilePathForUserConfig(userConfigPath);
        Catalog                 = new CatalogSettingsViewModel(UserConfigPath, InitializeAsync);
        Network                 = new NetworkSettingsViewModel(UserConfigPath, InitializeAsync);
        Settings = new SettingsPanelViewModel(new GeneralSettingsSectionViewModel(UserConfigPath), Catalog,
                                              new PermissionSettingsViewModel(UserConfigPath, InitializeAsync),
                                              Network,
                                              new WindowsSandboxSettingsViewModel(UserConfigPath, InitializeAsync));
        Settings.General.ProjectDeletionPromptChanged += OnProjectDeletionPromptChanged;
        Settings.CloseRequested += (_, _) => SettingsOpen = false;
        NewSessionCommand = new RelayCommand(NewConversation);
        SelectSessionCommand = new RelayCommand<SessionViewModel>(session => SelectedSession = session);
        ToggleSidebarSettingsMenuCommand = new RelayCommand(ToggleSidebarSettingsMenu);
        SelectSessionSidebarModeCommand = new RelayCommand(() => SelectSidebarMode(false));
        SelectProjectSidebarModeCommand = new RelayCommand(() => SelectSidebarMode(true));
        SelectManualProjectOrderCommand = new RelayCommand(() => SelectProjectSort(false));
        SelectRecentProjectOrderCommand = new RelayCommand(() => SelectProjectSort(true));
        ToggleProjectPinCommand = new RelayCommand<ProjectViewModel>(ToggleProjectPin);
        ToggleSidebarCategoryCommand = new RelayCommand<SidebarCategoryViewModel>(ToggleSidebarCategory);
        ToggleSessionPinCommand = new RelayCommand<SessionViewModel>(ToggleSessionPin);
        ToggleSessionArchiveCommand = new RelayCommand<SessionViewModel>(ToggleSessionArchive);
        ToggleArchiveViewCommand = new RelayCommand(ToggleArchiveView);
        ToggleProjectCommand = new RelayCommand<ProjectViewModel>(ToggleProjectGroup);
        ShowMoreSessionsCommand = new RelayCommand<ProjectViewModel>(group =>
        {
            if (group is null             ||
                !Projects.Contains(group) ||
                group.VisibleSessionCount >= group.Sessions.Count)
                return;
            group.VisibleSessionCount += ProjectViewModel.SessionPageSize;
            RebuildSidebarRows();
        });
        ReturnToFailedDraftCommand = new RelayCommand(() =>
        {
            // 轮转访问：进入最早一份未在当前查看的失败草稿，并将其移到队尾，反复进入不互相挡住。
            var draft = _failedDrafts.FirstOrDefault(item => item is { Id: null } &&
                                                             !ReferenceEquals(item, SelectedSession));
            if (draft is null) return;
            _failedDrafts.Remove(draft);
            _failedDrafts.Add(draft);
            SelectedSession = draft;
        });
        OpenProjectRenameCommand =
            new RelayCommand<ProjectViewModel>(OpenProjectRename, group => group is { CanEdit: true });
        ConfirmProjectRenameCommand = new RelayCommand(() => _ = ConfirmProjectRenameAsync());
        CancelProjectRenameCommand  = new RelayCommand(CancelProjectRename);
        OpenProjectDeletionCommand = new RelayCommand<ProjectViewModel>(OpenProjectDeletion,
                                                                        group => group is { CanShowActions: true });
        ConfirmProjectDeletionCommand = new RelayCommand(() => _ = ConfirmProjectDeletionAsync());
        CancelProjectDeletionCommand  = new RelayCommand(CancelProjectDeletion);
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
        SelectedSession = ConversationDraft();
    }

    public CatalogSettingsViewModel               Catalog  { get; }
    public NetworkSettingsViewModel               Network  { get; }
    public SettingsPanelViewModel                 Settings { get; }
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ProjectViewModel> Projects { get; } = [];
    public ObservableCollection<ModelChoice>      Models   { get; } = [];

    /// <summary>
    ///     <para>侧栏铺平投影：按各组展开态与可见数量混排组头、会话和展开入口。</para>
    ///     <para>
    ///         Flattened sidebar projection of headers, sessions and show-more entries by group expansion and visible count.
    ///     </para>
    /// </summary>
    public ObservableCollection<object> SidebarRows { get; } = [];

    public bool IsProjectMode => _sidebarPreferences.IsProjectMode;

    public bool IsManualProjectOrder => !_sidebarPreferences.SortProjectsByActivity;

    public bool SidebarPreferencesReady { get; private set; }

    public bool IsArchiveView { get; private set; }

    public string ArchiveViewButtonName => IsArchiveView ? "返回会话列表" : "查看已归档会话";

    public bool IsSidebarSettingsMenuOpen
    {
        get => _sidebarSettingsMenuOpen;
        set => SetProperty(ref _sidebarSettingsMenuOpen, value);
    }

    public RelayCommand                   NewSessionCommand                { get; }
    public RelayCommand<SessionViewModel> SelectSessionCommand             { get; }
    public RelayCommand                   ToggleSidebarSettingsMenuCommand { get; }
    public RelayCommand                   SelectSessionSidebarModeCommand  { get; }
    public RelayCommand                   SelectProjectSidebarModeCommand  { get; }
    public RelayCommand                   SelectManualProjectOrderCommand  { get; }
    public RelayCommand                   SelectRecentProjectOrderCommand  { get; }
    public RelayCommand<ProjectViewModel> ToggleProjectPinCommand          { get; }

    /// <summary>
    ///     <para>切换固定侧栏分类展开状态的命令。</para>
    ///     <para>The command that toggles a fixed sidebar category's expansion.</para>
    /// </summary>
    public RelayCommand<SidebarCategoryViewModel> ToggleSidebarCategoryCommand { get; }

    public RelayCommand<SessionViewModel> ToggleSessionPinCommand     { get; }
    public RelayCommand<SessionViewModel> ToggleSessionArchiveCommand { get; }
    public RelayCommand                   ToggleArchiveViewCommand    { get; }
    public RelayCommand<ProjectViewModel> ToggleProjectCommand        { get; }
    public RelayCommand<ProjectViewModel> ShowMoreSessionsCommand     { get; }
    public RelayCommand<ProjectViewModel> OpenProjectRenameCommand    { get; }
    public RelayCommand                   ConfirmProjectRenameCommand { get; }
    public RelayCommand                   CancelProjectRenameCommand  { get; }

    /// <summary>
    ///     <para>打开项目删除确认弹窗。</para>
    ///     <para>Opens the project-deletion confirmation popup.</para>
    /// </summary>
    public RelayCommand<ProjectViewModel> OpenProjectDeletionCommand { get; }

    /// <summary>
    ///     <para>确认并执行或继续项目删除。</para>
    ///     <para>Confirms and executes or resumes project deletion.</para>
    /// </summary>
    public RelayCommand ConfirmProjectDeletionCommand { get; }

    /// <summary>
    ///     <para>关闭项目删除确认弹窗。</para>
    ///     <para>Closes the project-deletion confirmation popup.</para>
    /// </summary>
    public RelayCommand CancelProjectDeletionCommand { get; }

    public RelayCommand OpenSettingsCommand         { get; }
    public AsyncCommand RefreshCommand              { get; }
    public string       UserConfigPath              { get; }
    public AsyncCommand InitializeProjectsCommand   { get; }
    public RelayCommand CancelInitializationCommand { get; }
    public RelayCommand ReturnToFailedDraftCommand  { get; }

    /// <summary>
    ///     <para>当前选中页是否为未发送的空白新对话页；驱动顶部「新对话」按钮的激活态。</para>
    ///     <para>Whether the selected page is the unsent blank new-conversation page; it drives the top button's active state.</para>
    /// </summary>
    public bool IsNewConversationSelected => SelectedSession is { Id: null };

    /// <summary>
    ///     <para>
    ///         是否存在可恢复的失败首发草稿：页面尚无真实 ID、不在当前选中页，经侧栏入口逐份轮转返回。
    ///     </para>
    ///     <para>
    ///         Whether recoverable failed first-send drafts exist: they have no real Id, are not the selected page, and
    ///         are visited in rotation through the sidebar entry.
    ///     </para>
    /// </summary>
    public bool HasFailedDraft =>
        _failedDrafts.Any(item => item is { Id: null } && !ReferenceEquals(item, SelectedSession));

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
            ProjectRenameErrorText = null;
            NotifyProjectRenameValidation();
        }
    }

    /// <summary>项目重命名错误提示：目录册保存失败在弹窗内呈现，可修改后重试。</summary>
    public string? ProjectRenameErrorText { get; private set; }

    public bool HasProjectRenameError => ProjectRenameErrorText is not null;

    /// <summary>确认重命名是否可用：存在目标、名称非空、与当前名不同且不在途。</summary>
    public bool CanConfirmProjectRename =>
        !_renamingProject                        &&
        _renameTarget is not null                &&
        ProjectRenameDraftText.Trim().Length > 0 &&
        ProjectRenameDraftText.Trim()        != _renameTarget.Name;

    /// <summary>
    ///     <para>项目删除确认弹窗是否打开。</para>
    ///     <para>Whether the project-deletion confirmation popup is open.</para>
    /// </summary>
    public bool IsProjectDeletionOpen
    {
        get => _projectDeletionOpen;
        private set => SetProperty(ref _projectDeletionOpen, value);
    }

    /// <summary>
    ///     <para>待删除项目的显示名称。</para>
    ///     <para>The display name of the project awaiting deletion.</para>
    /// </summary>
    public string ProjectDeletionTargetName => _projectDeletionTarget?.Name ?? string.Empty;

    /// <summary>
    ///     <para>说明项目记录、会话历史与工作目录文件的删除边界。</para>
    ///     <para>Explains what deletion changes and which session and workspace data it preserves.</para>
    /// </summary>
    public string ProjectDeletionPrompt => _projectDeletionTarget?.IsDeleting == true
        ? "删除已开始。继续将重试未完成的会话解绑；会话历史和工作目录中的文件都会保留。"
        : "这会移除 TinyHarness 中的项目记录，并将关联会话转为“无项目”。会话历史和工作目录中的文件都会保留。";

    /// <summary>
    ///     <para>返回确认按钮当前显示的删除或继续操作文本。</para>
    ///     <para>Returns the confirmation button text for a new or resumed deletion.</para>
    /// </summary>
    public string ProjectDeletionButtonText => _projectDeletionTarget?.IsDeleting == true ? "继续删除" : "删除项目";

    /// <summary>
    ///     <para>项目删除错误；失败后保留弹窗与可重试的删除状态。</para>
    ///     <para>The project-deletion error; failures keep the popup open with resumable state.</para>
    /// </summary>
    public string? ProjectDeletionErrorText
    {
        get => _projectDeletionErrorText;
        private set
        {
            if (!SetProperty(ref _projectDeletionErrorText, value)) return;
            OnPropertyChanged(nameof(HasProjectDeletionError));
        }
    }

    /// <summary>
    ///     <para>项目删除错误是否可见。</para>
    ///     <para>Whether a project-deletion error is visible.</para>
    /// </summary>
    public bool HasProjectDeletionError => ProjectDeletionErrorText is not null;

    /// <summary>
    ///     <para>确认按钮是否可用。</para>
    ///     <para>Whether the confirmation action is available.</para>
    /// </summary>
    public bool CanConfirmProjectDeletion =>
        !IsProjectDeletionRunning && _projectDeletionTarget?.Project is not null;

    /// <summary>
    ///     <para>项目删除操作是否正在执行。</para>
    ///     <para>Whether a project-deletion operation is in progress.</para>
    /// </summary>
    public bool IsProjectDeletionRunning { get; private set; }

    public SessionViewModel? SelectedSession
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            var previous                                            = _selected;
            if (previous is { } previousRow) previousRow.IsSelected = false;
            if (!SetProperty(ref _selected, value) || value is null) return;
            value.IsSelected = true;
            OnPropertyChanged(nameof(IsNewConversationSelected));
            OnPropertyChanged(nameof(HasFailedDraft));
            // 赋值路径全部非 null（构造、??=、显式草稿、行选择）；null 赋值已在上一行提前返回，不会到达本通知。
            NotifySelectionHighlight(previous, value);
            _ = LoadSelectedHistoryAsync(value);
        }
    }

    /// <summary>
    ///     <para>受保护目录解析的内部测试注入点；生产不设置。</para>
    ///     <para>Internal test seam for protected-directory resolution; production leaves it unset.</para>
    /// </summary>
    internal Func<string, CancellationToken, Task<string>>? ResolveDirectoryOverride { get; set; }

    /// <summary>
    ///     <para>目录册同步循环的完成观察缝；未启动时为 null，供测试等待循环退出。</para>
    ///     <para>
    ///         Observation seam for the catalog sync loop's completion; null when never started, letting tests
    ///         await loop exit.
    ///     </para>
    /// </summary>
    internal Task? ProjectSyncLoopCompletion => _projectSyncFinished?.Task;

    /// <summary>
    ///     <para>
    ///         停止接收新操作；先取消并等待在途整理整轮退出（不只等某一批），再等待所有会话取消、清理及保存。
    ///     </para>
    ///     <para>
    ///         Stops accepting operations; cancels and awaits the whole in-flight organize round first, then every
    ///         session's cancellation, cleanup and saving.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>各阶段失败不跳过其余阶段；重复调用等待同一次关闭及其汇总结果。</para>
    ///     <para>A failed stage does not skip later stages; repeated calls await the same shutdown and aggregate outcome.</para>
    /// </remarks>
    public ValueTask DisposeAsync() => new(_shutdown ??= ShutdownAsync());

    private async Task ShutdownAsync()
    {
        _closing = true;
        List<Exception> errors = [];
        try
        {
            await FlushSidebarPreferencesAsync();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _lifetime.CancelAsync();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            _indexRefreshSource?.Cancel();
            if (_indexRefreshFinished is { } refreshFinished) await refreshFinished.Task;
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            _projectSyncSource?.Cancel();
            if (_projectSyncFinished is { } syncFinished) await syncFinished.Task;
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        // linked 取消源已随 lifetime 触发；显式取消覆盖未关联路径，随后等待迁移真正退出。
        try
        {
            _organizeSource?.Cancel();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            if (_organizeFinished is { } finished) await finished.Task;
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            Notice = "正在关闭：等待所有会话取消、工具清理和日志保存…";
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            await _manager.DisposeAsync();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        try
        {
            // 宿主结束后仍等页面冲刷尾部，不能让定时 UI 更新越过窗口消息循环的退出。
            await Task.WhenAll(Sessions.Concat(_failedDrafts).Append(_newConversation)
                                       .OfType<SessionViewModel>().Distinct().Select(page => page.Completion));
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }

        if (errors.Count > 0) throw new AggregateException("关闭清理发生错误。", errors);
    }

    /// <summary>
    ///     <para>只读加载项目与会话列表，不触发迁移；生效 store 真正变化时取消旧整理并开始新一轮。</para>
    ///     <para>
    ///         Read-only load of projects and the session list that never organizes; a real store change cancels the
    ///         old round and starts a new one.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         成功与失败路径都在末尾确保项目目录册跨进程同步循环在运行，目录册损坏时监听仍然建立，
    ///         修复后的内容恢复能被发现。
    ///     </para>
    ///     <para>
    ///         Both success and failure paths ensure the cross-process catalog sync loop is running at the end, so
    ///         monitoring survives a damaged catalog and a recovered file is still discovered.
    ///     </para>
    /// </remarks>
    public async Task InitializeAsync()
    {
        if (_closing) return;
        await EnsureSidebarPreferencesLoadedAsync(_lifetime.Token);
        var draftTargets = CaptureDraftTargets();
        var changed      = false;
        var store        = string.Empty;
        await _refreshGate.WaitAsync(_lifetime.Token);
        _loadingCatalog = true;
        try
        {
            (store, changed) = await PrepareAsync();
            if (_closing) return;
            await LoadSessionRowsAsync(store);
            SelectedSession ??= ConversationDraft();
            await RebindUnboundDraftsAsync(store, draftTargets);
            RebuildProjects();
        }
        catch (Exception exception)
        {
            Notice          =   exception.Message;
            SelectedSession ??= ConversationDraft();
        }
        finally
        {
            _loadingCatalog = false;
            _refreshGate.Release();
            // 本次目录册快照可能早于加载期间首发建立的项目；补查兜底，避免被加载 guard 跳过的补载永久丢失。
            ReloadCatalogForUnknownProjectsAfterLoad();
        }

        // 锁外协调：先等被取消的旧轮退出，再按“store 真正变化”触发新一轮；本轮结束后的刷新不会再次进入这里。
        if (changed)
        {
            await StopOrganizeMigrationAsync();
            _ = StartOrganizeRound(store);
        }

        // 成功与失败路径都到达：目录册损坏时同步循环仍建立，修复后的内容恢复能被发现。
        StartProjectCatalogSyncLoop();
    }

    /// <summary>
    ///     <para>
    ///         索引刷新循环发现变化后的轻量应用路径：持刷新门重读当前 store 的置顶、归档与会话行，
    ///         随后重建项目分组；生效 store 已切换时不做轻量应用，释放门后回退完整初始化，
    ///         保持 store 切换的取消与重启语义。
    ///     </para>
    ///     <para>
    ///         The lightweight apply path when the index refresh loop detects a change: re-reads the current
    ///         store's pin, archive and session rows under the refresh gate, then rebuilds project groups; when
    ///         the effective store has switched, it skips the lightweight apply, releases the gate, and falls back
    ///         to full initialization to preserve store-switch cancellation and restart semantics.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         绝不持刷新门调用 <see cref="InitializeAsync" />：后者等待同一把门，持门调用会互等死锁。
    ///         门内的置顶与归档准备包含多次 await；OpenProjectAsync 与 InitializeProjectsAsync 不持刷新门，
    ///         可能在此期间切换生效 store，应用前因此复检一次，不一致时同样释放门后回退完整初始化。
    ///     </para>
    ///     <para>
    ///         Never call <see cref="InitializeAsync" /> while holding the refresh gate: it waits on the same gate
    ///         and holding it here would deadlock. Pin and archive preparation inside the gate awaits multiple times;
    ///         OpenProjectAsync and InitializeProjectsAsync do not take the refresh gate and may switch the effective
    ///         store meanwhile, so the store is re-checked before applying, falling back to full initialization after
    ///         releasing the gate on mismatch.
    ///     </para>
    /// </remarks>
    internal async Task RefreshSessionListAsync(string store)
    {
        if (_closing) return;
        await _refreshGate.WaitAsync(_lifetime.Token);
        if (_storeDirectory is not { } current || !ProjectRoots.AreEqual(current, store))
        {
            // store 已切换：轻量路径只服务当前 store，释放门后走完整初始化恢复切换语义。
            _refreshGate.Release();
            await InitializeAsync();
            return;
        }

        // 置顶与归档每轮照常重读：外部进程对状态文件的修改保持可见，与完整初始化的有意决策一致。
        _loadingCatalog = true;
        var storeChanged = false;
        try
        {
            await PreparePinStateAsync(store, _lifetime.Token);
            await PrepareArchiveStateAsync(store, _lifetime.Token);
            if (_closing) return;
            if (_storeDirectory is not { } currentStore || !ProjectRoots.AreEqual(currentStore, store))
            {
                // 第二道复检：上述 await 期间生效 store 可能已被不持刷新门的调用切换，避免旧 store 行被应用。
                // 与入口校验同样回退完整初始化，但不能持门调用，置标记后在 finally 释放门之后执行。
                storeChanged = true;
                return;
            }

            await LoadSessionRowsAsync(store);
            RebuildProjects();
        }
        catch (Exception exception)
        {
            Notice          =   exception.Message;
            SelectedSession ??= ConversationDraft();
        }
        finally
        {
            _loadingCatalog = false;
            _refreshGate.Release();
            // 与完整初始化相同的补查：加载期间首发建立的项目可能不在本轮目录册快照内。
            ReloadCatalogForUnknownProjectsAfterLoad();
        }

        if (storeChanged) await InitializeAsync();
    }

    /// <summary>
    ///     <para>
    ///         窗口显示后的启动序列：轻量准备不被历史扫描阻塞，列表先按索引摘要或降级扫描分批呈现，
    ///         索引对账与归属整理随后后台完成；首屏等待链不再包含整理本体，返回前仅等待整轮收尾。
    ///     </para>
    ///     <para>
    ///         Startup sequence after the window is shown: light preparation never waits for the scan, the list first
    ///         renders in batches from index summaries or the degraded scan, then reconciliation and organizing finish
    ///         in the background; the first-screen wait no longer contains the organize round, and the method only
    ///         awaits the round's completion before returning.
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
            // 首屏先行：列表不等待整理本体；在途整轮已合并时列表同样已就绪，直接结束启动。
            await InitializeAsync();
            var round = StartOrganizeRound(store);
            if (round is not null) await round;
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
        await EnsureSidebarPreferencesLoadedAsync(_lifetime.Token);
        var draftTargets = CaptureDraftTargets();
        await _refreshGate.WaitAsync(_lifetime.Token);
        _loadingCatalog = true;
        try
        {
            var (store, changed) = await PrepareAsync();
            if (_closing) return (store, changed);
            await RebindUnboundDraftsAsync(store, draftTargets);
            RebuildProjects();
            SelectedSession ??= ConversationDraft();
            return (store, changed);
        }
        finally
        {
            _loadingCatalog = false;
            _refreshGate.Release();
        }
    }

    /// <summary>
    ///     <para>
    ///         按 store 取共享列表投影；生效目录变化时在门内整体重建，并发调用不会读到半更新状态，
    ///         投影本身不持有可变状态以外的锁。
    ///     </para>
    ///     <para>
    ///         Returns the shared list projection for a store, rebuilt as a whole under a gate when the effective
    ///         directory changes, so concurrent callers never observe a half-updated binding; the projection holds no
    ///         mutable state beyond its own gate.
    ///     </para>
    /// </summary>
    private SessionListProjection ProjectionFor(string storeDirectory)
    {
        lock (_listProjectionGate)
        {
            if (_listProjection is { } current && ProjectRoots.AreEqual(current.Store, storeDirectory))
                return current.Projection;
            var projection = new SessionListProjection(storeDirectory);
            _listProjection = (projection, storeDirectory);
            return projection;
        }
    }

    /// <summary>
    ///     <para>
    ///         加载当前 store 的会话行：索引可用时查询摘要与失败行；索引缺失或不可用时降级为不落库的
    ///         严格摘要扫描，保持列表轻量呈现且不阻塞首屏。
    ///     </para>
    ///     <para>
    ///         Loads the session rows for the current store: summary and failed-row queries while the index is
    ///         available; a degraded, non-persisted strict summary scan when it is missing or unusable, keeping the
    ///         list lightweight and the first screen unblocked.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         调用方持有刷新门；本方法不触发迁移，也不改写索引与权威来源。Sqlite 的异步 API 仍是同步阻塞，
    ///         索引查询与降级扫描放工作线程执行，仅结果应用回到调用方上下文。
    ///     </para>
    ///     <para>
    ///         The caller holds the refresh gate; this never organizes and never rewrites the index or sources.
    ///         Sqlite's async APIs still block synchronously, so index queries and the degraded scan run on a worker
    ///         thread and only the result application returns to the caller's context.
    ///     </para>
    /// </remarks>
    private async Task LoadSessionRowsAsync(string store)
    {
        var projection = ProjectionFor(store);
        var (entries, degradedReason) = await Task.Run(() => LoadSessionEntriesAsync(projection), _lifetime.Token)
                                                  .ConfigureAwait(true);
        if (_closing) return;
        if (degradedReason is not null)
            Notice = $"会话索引不可用，已按摘要直接读取：{degradedReason}";
        await ApplySessionSummariesAsync(store, entries).ConfigureAwait(true);
    }

    /// <summary>
    ///     <para>在工作线程解析列表条目：按索引可用性走查询或降级摘要扫描，并带回不可用原因。</para>
    ///     <para>
    ///         Resolves list entries on a worker thread: index queries or the degraded scan by availability, carrying
    ///         the unusable reason back to the caller.
    ///     </para>
    /// </summary>
    private async Task<(IReadOnlyList<SessionListScanEntry> Entries, string? DegradedReason)>
        LoadSessionEntriesAsync(SessionListProjection projection)
    {
        var availability = await projection.GetAvailabilityAsync(_lifetime.Token).ConfigureAwait(false);
        switch (availability.Status)
        {
            case SessionIndexAvailabilityStatus.Available :
                var summaries = await projection.ListSummariesAsync(new SessionSummaryQuery(), _lifetime.Token)
                                                .ConfigureAwait(false);
                var rows = await projection.ListRowsAsync(_lifetime.Token).ConfigureAwait(false);
                var entries = summaries.Select(summary => new SessionListScanEntry(summary.Id, summary, null))
                                       .Concat(rows.Where(row => row.State == SessionIndexEntryState.Failed)
                                                   .Select(row => new SessionListScanEntry(row.Id, null, row.Error)))
                                       .ToArray();
                return (entries, null);
            case SessionIndexAvailabilityStatus.Missing :
            case SessionIndexAvailabilityStatus.NeedsSchema :
                // 索引尚未建立：严格摘要扫描直接呈现，后台对账建库后自动切换查询路径。
                // The index does not exist yet: render from the strict summary scan, and switch to queries once
                // background reconciliation builds the database.
                return (await projection.ScanSummariesAsync(_lifetime.Token).ConfigureAwait(false), null);
            default :
                // 索引不可用：降级摘要扫描保持列表可见，原因带回 UI 层呈现，不清空或降级索引文件。
                return (await projection.ScanSummariesAsync(_lifetime.Token).ConfigureAwait(false),
                        availability.Detail ?? availability.Status.ToString());
        }
    }

    /// <summary>
    ///     <para>按 store 一次建立会话行索引：ID 到行的 Ordinal 映射，替代条目循环中的逐行线性扫描。</para>
    ///     <para>
    ///         Builds one session-row index for a store: an Ordinal id-to-row map replacing the per-entry linear
    ///         scan of the session collection.
    ///     </para>
    /// </summary>
    private Dictionary<string, SessionViewModel> BuildSessionIndexForStore(string store)
    {
        var rowsById = new Dictionary<string, SessionViewModel>(StringComparer.Ordinal);
        foreach (var session in Sessions)
            if (session.Id is { } id                 &&
                session.StoreDirectory is { } source &&
                ProjectRoots.AreEqual(source, store))
                rowsById.TryAdd(id, session);
        return rowsById;
    }

    /// <summary>
    ///     <para>
    ///         把摘要条目应用到侧栏：已有行只刷新归属、活动与权限元数据；新行分批创建并让出调度，
    ///         不因大量行重建实例、宿主、草稿和历史投影。
    ///     </para>
    ///     <para>
    ///         Applies summary entries to the sidebar: existing rows refresh ownership, activity, and permission
    ///         metadata only; new rows are created in yielding batches without rebuilding instances, hosts, drafts,
    ///         or history projections.
    ///     </para>
    /// </summary>
    private async Task ApplySessionSummariesAsync(string store, IReadOnlyList<SessionListScanEntry> entries)
    {
        var failures      = 0;
        var batchPosition = 0;
        // 条目 ID 来自索引或逐文件扫描，批内唯一：一次建索引后按 ID 命中，消除 O(N²) 扫描。
        var rowsById      = BuildSessionIndexForStore(store);
        foreach (var entry in entries)
        {
            if (_closing) return;
            if (entry.Summary is not { } summary)
            {
                // 与旧列表契约一致：单项失败不阻止其余条目，原因进入窗口提示。
                failures++;
                Notice = failures == 1
                    ? $"部分历史无法读取：{entry.Error}"
                    : $"部分历史无法读取（{failures} 项）：{entry.Error}";
                continue;
            }

            if (rowsById.TryGetValue(summary.Id, out var existing)) ApplySummaryMetadata(existing, summary);
            else AddSession(CreateSessionRow(store, summary));

            batchPosition++;
            if (batchPosition % ListRowBatchSize == 0) await Task.Yield();
        }
    }

    /// <summary>
    ///     <para>按摘要构造正式会话行：标题与权限标签来自摘要，归档与置顶随后按权威来源刷新。</para>
    ///     <para>
    ///         Builds a formal session row from the summary; the title and permission label come from the summary,
    ///         and archive and pin states refresh from their authoritative stores afterwards.
    ///     </para>
    /// </summary>
    private SessionViewModel CreateSessionRow(string store, SessionSummary summary)
    {
        var row = new SessionViewModel(_manager, _factory, summary.WorkspaceRoot, summary.Id, store,
                                       summary.Title, _dispatch, summary.ProjectId, summary.LastActivityUtc,
                                       summary.ProjectAssignmentState)
        {
            ModelOptions        = Models,
            ManageModelsCommand = OpenSettingsCommand,
            // 缓存为空时与原实现同样得到当前可选项目数组；加载尾部的 RebuildProjects 会统一刷新到正式实例。
            ProjectOptions      = _cachedProjectOptions ?? Projects.Where(group => group.CanEdit).ToArray()
        };
        // 标签来自最后选择；历史页面不解析凭据，目标缺失也保留页面。
        row.ApplyPermissionSettings(_permissionSettings);
        row.ApplyStoredPermissionMode(summary.PermissionMode);
        row.SetArchived(IsSessionArchived(row));
        return row;
    }

    /// <summary>
    ///     <para>把摘要元数据刷新到已有行；活动只前进不回退，不替换实例、宿主、草稿和历史投影。</para>
    ///     <para>
    ///         Refreshes an existing row from summary metadata; activity only moves forward, and instances, hosts,
    ///         drafts, and history projections are never replaced.
    ///     </para>
    /// </summary>
    private void ApplySummaryMetadata(SessionViewModel row, SessionSummary summary)
    {
        row.ApplyStoredMetadata(summary.ProjectId, summary.ProjectAssignmentState, summary.LastActivityUtc);
        row.SetArchived(IsSessionArchived(row));
        row.ApplyStoredPermissionMode(summary.PermissionMode);
    }

    /// <summary>
    ///     <para>
    ///         接收对账逐条提交的摘要并入队渐进上屏；回调内只入队，调度工作交给独立泵，
    ///         不拖慢对账串行门内的扫描。
    ///     </para>
    ///     <para>
    ///         Receives reconciliation-committed summaries for progressive rendering; the callback only enqueues and
    ///         an independent pump schedules the work so the reconciler's serial gate never waits on UI work.
    ///     </para>
    /// </summary>
    private void EnqueueSummaryRow(SessionSummary summary)
    {
        _pendingSummaryRows.Enqueue(summary);
        if (Interlocked.CompareExchange(ref _summaryRowPumpBusy, 1, 0) != 0) return;
        _ = PumpSummaryRowsAsync();
    }

    /// <summary>
    ///     <para>
    ///         排水渐进队列：每次取一批，在刷新门与 UI 调度保护下应用并在批尾统一重建项目分组，批间让出调度。
    ///     </para>
    ///     <para>
    ///         Drains the progressive queue one batch at a time, applying entries under the refresh gate and UI
    ///         dispatch with project groups rebuilt once per batch, yielding between batches.
    ///     </para>
    /// </summary>
    private async Task PumpSummaryRowsAsync()
    {
        try
        {
            while (_pendingSummaryRows.TryDequeue(out var first))
            {
                // 一次取整批：批内共享一次刷新门与 UI 调度，避免逐行重建分组的 O(N²) 开销。
                var batch = new List<SessionSummary>(ListRowBatchSize) { first };
                while (batch.Count < ListRowBatchSize && _pendingSummaryRows.TryDequeue(out var next))
                    batch.Add(next);
                await ApplyCommittedSummaryBatchAsync(batch).ConfigureAwait(true);
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭取消后队列残留由进程退出回收，不构成错误。
        }
        catch (Exception exception)
        {
            // 泵自身故障只记诊断：busy 已在 finally 复位，下一次入队会重启排水。
            Trace.TraceError("Session summary row pump failed: {0}", exception.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _summaryRowPumpBusy, 0);
        }

        // 入队与排水并发交错时补一次排水，保证队尾不滞留。
        if (!_pendingSummaryRows.IsEmpty &&
            Interlocked.CompareExchange(ref _summaryRowPumpBusy, 1, 0) == 0)
            _ = PumpSummaryRowsAsync();
    }

    /// <summary>
    ///     <para>持刷新门与 UI 调度应用一批渐进摘要；项目分组在批尾统一重建一次。</para>
    ///     <para>
    ///         Applies one progressive-summary batch under the refresh gate and UI dispatch; project groups rebuild
    ///         once at the batch tail.
    ///     </para>
    /// </summary>
    private async Task ApplyCommittedSummaryBatchAsync(IReadOnlyList<SessionSummary> batch)
    {
        // 与统一加载互斥：共享刷新门避免并发改动会话集合。
        await _refreshGate.WaitAsync(_lifetime.Token).ConfigureAwait(true);
        try
        {
            if (_closing) return;
            await InvokeOnUiAsync(() =>
            {
                if (_closing) return Task.CompletedTask;
                // 整批共用一次按当前 store 建立的行索引；_storeDirectory 在同步循环内不会变化。
                if (_storeDirectory is { } store)
                {
                    var rowsByStore = BuildSessionIndexForStore(store);
                    foreach (var summary in batch) ApplyCommittedSummaryRow(summary, rowsByStore);
                }

                // 批尾统一重建：统一加载进行中由其末尾重建，否则渐进行立即反映到项目分组。
                if (!_loadingCatalog) RebuildProjects();
                return Task.CompletedTask;
            }).ConfigureAwait(true);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private void ApplyCommittedSummaryRow(SessionSummary summary, Dictionary<string, SessionViewModel> rowsByStore)
    {
        if (_storeDirectory is not { } store) return;
        if (rowsByStore.TryGetValue(summary.Id, out var existing))
        {
            ApplySummaryMetadata(existing, summary);
            return;
        }

        var row = CreateSessionRow(store, summary);
        AddSession(row);
        // 批内新增行登记进索引：同批重复摘要仍命中既有行刷新，保持与逐行扫描一致的语义。
        rowsByStore[summary.Id] = row;
    }

    /// <summary>
    ///     <para>把操作调度到 UI 上下文启动执行；注入调度器缺失时回退 Avalonia UI 线程。</para>
    ///     <para>
    ///         Starts an operation on the UI context; falls back to the Avalonia UI thread when no dispatcher is
    ///         injected.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         调度器只接受同步动作，操作按 fire-and-forget 启动，本方法不等待其完成；操作异常被观察并记录，
    ///         不向调度器传播。依赖操作结果的调用方应自行等待。
    ///     </para>
    ///     <para>
    ///         The dispatcher accepts synchronous actions only, so the operation starts fire-and-forget and this
    ///         method never awaits its completion; operation faults are observed and logged instead of escaping into
    ///         the dispatcher. Await results yourself when they matter.
    ///     </para>
    /// </remarks>
    private async Task InvokeOnUiAsync(Func<Task> operation)
    {
        if (_dispatch is { } dispatch)
        {
            await dispatch(() => { _ = ObserveDispatchedAsync(operation()); }).ConfigureAwait(true);
            return;
        }

        // DispatcherOperation 可等待但不支持 ConfigureAwait；排队后由 UI 线程执行。
        await Dispatcher.UIThread.InvokeAsync(() => { _ = ObserveDispatchedAsync(operation()); });
    }

    /// <summary>
    ///     <para>观察已调度操作的完成：取消静默，其余异常记诊断，不向调度器传播。</para>
    ///     <para>
    ///         Observes a dispatched operation to completion: cancellation stays silent and other faults are logged,
    ///         never escaping into the dispatcher.
    ///     </para>
    /// </summary>
    private static async Task ObserveDispatchedAsync(Task operation)
    {
        try
        {
            await operation.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 关闭取消由调用方生命周期处理，不构成调度错误。
        }
        catch (Exception exception)
        {
            Trace.TraceError("UI-dispatched operation failed: {0}", exception.Message);
        }
    }

    /// <summary>
    ///     <para>为归属整理执行一轮索引对账；失败给出可见原因并返回 false，回退整轮扫描。</para>
    ///     <para>
    ///         Runs one reconciliation round for organizing; failures surface with a visible reason and return false
    ///         to fall back to the full organize scan.
    ///     </para>
    /// </summary>
    private async Task<bool> ReconcileForOrganizeAsync(string store, int generation, CancellationToken token)
    {
        try
        {
            var report = await RunIndexReconcileAsync(store, token).ConfigureAwait(true);
            return report.Availability.Status == SessionIndexAvailabilityStatus.Available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException
                                           or UnauthorizedAccessException)
        {
            // 索引故障不阻断归属维护：回退整轮扫描，缺口由下一轮对账追平。
            if (IsCurrentStore(store, generation))
                OrganizeNotice = $"会话索引维护未完成，已回退完整整理：{_factory.Redact(exception.Message)}";
            return false;
        }
    }

    /// <summary>
    ///     <para>
    ///         对当前 store 执行一轮带渐进回调的索引对账；I/O 在工作线程执行，不依赖调用方上下文。
    ///     </para>
    ///     <para>
    ///         Runs one index reconciliation round with the progressive callback for the given store; I/O stays on a
    ///         worker thread regardless of the caller's context.
    ///     </para>
    /// </summary>
    private Task<SessionIndexReconcileReport> RunIndexReconcileAsync(string store, CancellationToken token)
    {
        var projection = ProjectionFor(store);
        // Sqlite 的异步 API 仍是同步阻塞：对账放工作线程，UI 调用链不承担索引与日志 I/O。
        return Task.Run(() => projection.ReconcileAsync(
                                                        new SessionIndexReconcileOptions
                                                        {
                                                            SummaryCommitted = (summary, _) =>
                                                            {
                                                                EnqueueSummaryRow(summary);
                                                                return Task.CompletedTask;
                                                            }
                                                        }, token),
                        token);
    }

    /// <summary>
    ///     <para>
    ///         从索引挑出确需归属整理的会话 ID；行读取失败时返回 null，调用方回退既有整轮扫描。
    ///     </para>
    ///     <para>
    ///         Picks the session ids that still need organizing from the index; a row-read failure returns null and
    ///         the caller falls back to the full organize scan.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> CollectPendingSessionIdsAsync(
        string store, CancellationToken token)
    {
        try
        {
            // 行读取含同步 Sqlite 查询：放工作线程，不依赖整理轮的执行上下文。
            var rows = await Task.Run(() => ProjectionFor(store).ListRowsAsync(token), token).ConfigureAwait(true);
            return rows.Where(row => row.State == SessionIndexEntryState.Current &&
                                     row.Summary?.ProjectAssignmentState ==
                                     SessionProjectAssignmentState.PendingInitialization)
                       .Select(row => row.Id)
                       .OrderBy(id => id, StringComparer.Ordinal)
                       .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException
                                           or UnauthorizedAccessException)
        {
            Trace.TraceError("Session index pending collection for '{0}' failed: {1}", store, exception.Message);
            return null;
        }
    }

    /// <summary>
    ///     <para>尽力把已提交来源事实追平到索引；失败只留诊断，不改变调用方结果。</para>
    ///     <para>
    ///         Best-effort catch-up of committed source facts into the index; failures stay diagnostic and never
    ///         change the caller's outcome.
    ///     </para>
    /// </summary>
    private async Task TryReconcileIndexAsync(string storeDirectory)
    {
        try
        {
            await RunIndexReconcileAsync(storeDirectory, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 关闭取消：已提交事实由下次启动的对账追平。
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException
                                           or UnauthorizedAccessException)
        {
            Trace.TraceError("Session index reconciliation for '{0}' failed: {1}", storeDirectory,
                             exception.Message);
        }
    }

    /// <summary>
    ///     <para>启动索引刷新循环：周期对账加 data_version 提示，发现变化后重查列表；旧循环先停。</para>
    ///     <para>
    ///         Starts the index refresh loop: periodic reconciliation plus the data_version hint requeries the list on
    ///         change; a previous loop stops first.
    ///     </para>
    /// </summary>
    private void StartIndexRefreshLoop(string store)
    {
        _indexRefreshSource?.Cancel();
        var source   = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _indexRefreshSource   = source;
        _indexRefreshFinished = finished;
        _indexRefreshGeneration++;
        _ = RunIndexRefreshLoopAsync(store, _indexRefreshGeneration, source, finished);
    }

    /// <summary>
    ///     <para>
    ///         刷新循环体：常驻监测连接读取 data_version，周期对账覆盖“来源已提交、索引未更新”窗口；
    ///         发现变化后在 UI 上下文走轻量路径重读当前 store 的列表（store 已切换时回退完整初始化），
    ///         索引失效即停循环，由手动刷新或新一轮重启。
    ///     </para>
    ///     <para>
    ///         The loop body: a standing monitor connection reads data_version while periodic reconciliation covers
    ///         the committed-source-but-unindexed window; changes apply the lightweight list refresh for the
    ///         current store on the UI context (falling back to full initialization when the store has switched),
    ///         and an unusable index stops the loop until manual refresh or a new round restarts it.
    ///     </para>
    /// </summary>
    private async Task RunIndexRefreshLoopAsync(string store, int generation, CancellationTokenSource source,
                                                TaskCompletionSource finished)
    {
        try
        {
            var             projection  = ProjectionFor(store);
            await using var watcher     = await projection.OpenVersionWatcherAsync(source.Token).ConfigureAwait(false);
            var             lastVersion = await watcher.ReadAsync(source.Token).ConfigureAwait(false);
            while (!_closing && IsCurrentIndexRefresh(store, generation))
            {
                await Task.Delay(IndexRefreshIntervalMs, source.Token).ConfigureAwait(false);
                var report = await projection.ReconcileAsync(SessionIndexReconcileOptions.Default, source.Token)
                                             .ConfigureAwait(false);
                var version = await watcher.ReadAsync(source.Token).ConfigureAwait(false);
                var changed = version             != lastVersion || report.Rebuilt > 0 || report.Removed > 0 ||
                              report.MarkedFailed > 0;
                lastVersion = version;
                // 变化只在 UI 上下文做轻量应用：不重读配置与目录册，重读仅限置顶、归档与会话行。
                if (!changed || _closing || !IsCurrentIndexRefresh(store, generation)) continue;
                await InvokeOnUiAsync(() => RefreshSessionListAsync(store)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭或 store 切换的取消不构成错误。
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException
                                           or UnauthorizedAccessException)
        {
            // 索引失效即停循环：避免每周期空转，手动刷新或下一轮启动会重启。
            Trace.TraceError("Session index refresh loop for '{0}' stopped: {1}", store, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_indexRefreshSource, source))
            {
                _indexRefreshSource   = null;
                _indexRefreshFinished = null;
            }

            finished.TrySetResult();
        }
    }

    /// <summary>刷新循环迟到防护：结果只在代次仍是最新且 store 未变化时应用。</summary>
    private bool IsCurrentIndexRefresh(string store, int generation) =>
        generation == _indexRefreshGeneration && _storeDirectory is { } current &&
        ProjectRoots.AreEqual(current, store);

    /// <summary>
    ///     <para>启动项目目录册跨进程同步循环：常驻单例、幂等启动，随窗口关闭或生命周期取消。</para>
    ///     <para>
    ///         Starts the cross-process project-catalog sync loop: a single resident loop that is idempotent to
    ///         start and cancels with window shutdown or the lifetime.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         循环与 store 解耦：store 切换不重启循环，每轮检查都读取当前生效 store；基线一致性由切换路径上
    ///         的显式 <see cref="ProjectCatalogMonitor.Reset" /> 保证。
    ///     </para>
    ///     <para>
    ///         The loop decouples from the store: a switch never restarts it and every round reads the effective
    ///         store; baseline consistency is guaranteed by explicit <see cref="ProjectCatalogMonitor.Reset" />
    ///         on switch paths.
    ///     </para>
    /// </remarks>
    internal void StartProjectCatalogSyncLoop()
    {
        if (_closing || _storeDirectory is null || _projectSyncSource is not null) return;
        var source   = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _projectSyncSource   = source;
        _projectSyncFinished = finished;
        _                    = RunProjectCatalogSyncLoopAsync(source, finished);
    }

    /// <summary>
    ///     <para>
    ///         同步循环体：周期检查目录册，连续失败按 1/2/4/8 倍退避；意外异常不退出循环（独立故障边界），
    ///         关闭或生命周期取消时静默退出。
    ///     </para>
    ///     <para>
    ///         The loop body: periodic catalog checks with 1/2/4/8 backoff on consecutive failures; unexpected
    ///         faults never exit the loop (an independent failure boundary), while shutdown or lifetime
    ///         cancellation exits silently.
    ///     </para>
    /// </summary>
    private async Task RunProjectCatalogSyncLoopAsync(CancellationTokenSource source,
                                                      TaskCompletionSource    finished)
    {
        try
        {
            while (!_closing)
            {
                // 连续失败按 1/2/4/8 倍退避，损坏或权限问题不再高频空转；恢复后计数在应用时归零。
                var delayMs = ProjectSyncIntervalMs * Math.Min(1 << Math.Min(_projectSyncConsecutiveFailures, 3), 8);
                await Task.Delay(delayMs, source.Token).ConfigureAwait(false);
                try
                {
                    await CheckProjectCatalogAsync(source.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // 意外异常与目录册失败同样退避重试，循环不退出，保持独立故障边界。
                    Trace.TraceError("Project catalog sync check failed: {0}", exception.Message);
                    OnProjectSyncCheckFailed();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 关闭或生命周期取消不构成错误。
        }
        finally
        {
            if (ReferenceEquals(_projectSyncSource, source))
            {
                _projectSyncSource   = null;
                _projectSyncFinished = null;
            }

            finished.TrySetResult();
        }
    }

    /// <summary>
    ///     <para>
    ///         执行一次目录册变化检查并按结果分派：未变直接返回（此前报过失败则复位失败状态），失败计入退避，
    ///         已加载则调度到 UI 线程应用。
    ///     </para>
    ///     <para>
    ///         Runs one catalog change check and dispatches by outcome: unchanged returns directly (resetting the
    ///         failure state when a failure was noticed before), failure counts toward backoff, and a loaded
    ///         catalog is dispatched to the UI thread for applying.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>同步循环与测试共用本方法；取消以 <see cref="OperationCanceledException" /> 原样传播。</para>
    ///     <para>Shared by the sync loop and tests; cancellation propagates as <see cref="OperationCanceledException" />.</para>
    /// </remarks>
    internal async Task CheckProjectCatalogAsync(CancellationToken cancellationToken)
    {
        var store = _storeDirectory;
        if (store is null) return;
        var result = await _catalogMonitor.CheckAsync(store, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case ProjectCatalogCheckStatus.Unchanged :
                // stat 瞬时失败后文件恢复原值时故障已不成立：复位退避与提示标志，避免失败提示永驻、退避保持峰值。
                if (_projectSyncFailureNoticed) ResetProjectSyncFailureState();
                return;
            case ProjectCatalogCheckStatus.Failed :
                // Failed 不改基线：下一轮 stat 仍与基线不同，检查自然重试。
                OnProjectSyncCheckFailed();
                return;
            case ProjectCatalogCheckStatus.Loaded :
                await InvokeOnUiAsync(() =>
                {
                    ApplyProjectCatalogSync(store, result);
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    ///     <para>
    ///         在 UI 线程应用一次已加载的目录册：内容与内存一致时只确认基线，否则替换项目列表并重建分组。
    ///     </para>
    ///     <para>
    ///         Applies one loaded catalog on the UI thread: content matching memory only confirms the baseline,
    ///         otherwise the project list is replaced and the groups rebuild.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         关闭或 store 已切换的迟到结果，以及统一加载或项目删除进行中的结果，直接丢弃且不确认基线，
    ///         由下一轮检查收敛。
    ///     </para>
    ///     <para>
    ///         Late results after shutdown or a store switch, and results arriving while unified loading or
    ///         project deletion runs, are discarded without confirming the baseline; the next check converges.
    ///     </para>
    /// </remarks>
    private void ApplyProjectCatalogSync(string store, ProjectCatalogCheckResult result)
    {
        // 迟到防护：store 已切换或正在关闭时，旧轮结果直接丢弃，不确认基线。
        if (_closing        || !SameSidebarStore(store, _storeDirectory)) return;
        // 让位防护：统一加载或项目删除进行中不抢占；不确认基线，下一轮检查重试收敛。
        if (_loadingCatalog || IsProjectDeletionRunning) return;
        // 文件可读即视为恢复：即使本轮内容未应用，退避与失败提示也归位。
        ResetProjectSyncFailureState();
        var catalog   = result.Catalog!;
        var projects  = catalog.Projects;
        var unchanged = projects.Count == _projects.Count;
        for (var i = 0; unchanged && i < projects.Count; i++)
            unchanged = projects[i] is { } project && project.ContentEquals(_projects[i]);
        if (unchanged)
        {
            // 内容未变（本地手改回写、元数据抖动、重复事件）：只确认基线，不重建侧栏。
            _catalogMonitor.Confirm(result.FileState);
            return;
        }

        _projects = catalog.Projects;
        _catalogMonitor.Confirm(result.FileState);
        RebuildProjects();
    }

    /// <summary>
    ///     <para>复位目录册同步失败状态：退避计数清零，下次失败重新提示；不清空共享的 Notice 文本。</para>
    ///     <para>
    ///         Resets the catalog-sync failure state: the backoff counter clears and the next failure notifies
    ///         again; the shared Notice text is never cleared here.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>Notice 是共享单字符串，主动清空会覆盖其他流程的提示，复位只涉及标志与计数。</para>
    ///     <para>
    ///         Notice is one shared string and clearing it would overwrite other flows' messages, so only the
    ///         flag and counter reset.
    ///     </para>
    /// </remarks>
    private void ResetProjectSyncFailureState()
    {
        _projectSyncConsecutiveFailures = 0;
        _projectSyncFailureNoticed      = false;
    }

    /// <summary>
    ///     <para>记录一次同步检查失败：累计退避计数，并去重地把失败提示写入 Notice。</para>
    ///     <para>
    ///         Records one failed sync check: accumulates the backoff count and writes a deduplicated failure
    ///         notice.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>可能在 UI 线程外调用；提示经 UI 调度设置，持续故障不随每个周期重复提示。</para>
    ///     <para>
    ///         May be called off the UI thread; the notice is set through UI dispatch, and a persistent fault is
    ///         not re-reported every cycle.
    ///     </para>
    /// </remarks>
    private void OnProjectSyncCheckFailed()
    {
        // 失败计数驱动同步循环的退避节奏；上限与倍增封顶一致，计数不无限增长。
        _projectSyncConsecutiveFailures = Math.Min(_projectSyncConsecutiveFailures + 1, 4);
        if (_projectSyncFailureNoticed) return;
        // 同一持续故障只提示一次；Notice 涉及 PropertyChanged，须在 UI 线程设置。
        _projectSyncFailureNoticed = true;
        _ = InvokeOnUiAsync(() =>
        {
            Notice = "项目目录自动同步失败，将继续自动重试。";
            return Task.CompletedTask;
        });
    }

    /// <summary>
    ///     <para>读取配置与模型目录、解析生效 store 并载入项目册；不读取日志正文。</para>
    ///     <para>Loads config, models, the effective store and the catalog; log bodies are never read here.</para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         同 store 的项目册读取失败保留最后一次成功加载的快照，不清空侧栏；本轮已切换 store 时旧快照
    ///         属于上一 store，仍清空侧栏。目录册缺失等合法空态仍按 LoadAsync 返回值正常应用。
    ///     </para>
    ///     <para>
    ///         A failed catalog read within the same store keeps the last successfully loaded snapshot instead of
    ///         clearing the sidebar; when the store actually switched this round, the old snapshot belongs to the
    ///         previous store and the sidebar still clears. Legal empty states such as a missing catalog still
    ///         apply through the LoadAsync result.
    ///     </para>
    /// </remarks>
    private async Task<(string Store, bool StoreChanged)> PrepareAsync()
    {
        var revision = _storeRevision;
        var user     = await UserConfigStore.LoadAsync(UserConfigPath, _lifetime.Token);
        _permissionSettings = user.Settings?.Permissions ?? new PermissionSettings();
        foreach (var page in Sessions.Concat(_failedDrafts).Append(_newConversation).Where(page => page is not null)
                                     .Distinct())
            page!.ApplyPermissionSettings(_permissionSettings);
        if (revision != _storeRevision && _storeDirectory is { } current) return (current, false);
        Models.Clear();
        foreach (var profile in user.Profiles)
        foreach (var model in profile.Models)
            Models.Add(new ModelChoice(profile.Name, model.Id, model.DisplayName, model.ReasoningEfforts,
                                       model.DefaultReasoningEffort));
        var directory = user.Settings?.SessionStoreDirectory ?? SessionStorePaths.DefaultDirectory();
        directory = Path.GetFullPath(directory);
        var changed = _storeDirectory is not null && !ProjectRoots.AreEqual(_storeDirectory, directory);
        if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory))
            _initializationCursor = null;
        if (changed)
        {
            // 切换 store：取消在途迁移（调用方锁外等待退出），清空上一 store 的整理展示残留并作废目录册同步基线。
            _organizeSource?.Cancel();
            ResetOrganizeDisplay();
            _catalogMonitor.Reset();
            // 上一 store 的失败提示随切换作废：新 store 的首个故障须重新提示。
            ResetProjectSyncFailureState();
        }

        if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory)) _storeRevision++;
        _storeDirectory = directory;
        revision        = _storeRevision;
        try
        {
            var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token);
            if (revision != _storeRevision) return (_storeDirectory!, false);
            _projects = catalog.Projects;
        }
        catch (Exception exception) when (exception is ProjectStoreException or IOException
                                           or UnauthorizedAccessException)
        {
            // 同 store 瞬时读取失败保留最后成功快照；本轮已切换 store 时旧快照属于上一 store，仍清空避免跨 store 展示。
            if (revision != _storeRevision) return (_storeDirectory!, false);
            if (changed) _projects = [];
            Notice                     = $"项目信息无法读取：{exception.Message}";
            // 具体原因已提示：置位同步失败标志，同一持续故障不被循环改写成泛化的同步失败提示。
            _projectSyncFailureNoticed = true;
        }

        await PreparePinStateAsync(directory, _lifetime.Token);
        await PrepareArchiveStateAsync(directory, _lifetime.Token);

        return (directory, changed);
    }

    /// <summary>
    ///     <para>读取当前 store 的 Core 置顶状态；首次读取时迁移旧 Desktop 置顶列表。</para>
    ///     <para>Loads Core pin state for the current store and migrates legacy Desktop pin lists on first access.</para>
    /// </summary>
    private async Task PreparePinStateAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var storeKey = PinStoreKey(storeDirectory);
        await _pinStateMutationGate.WaitAsync(cancellationToken);
        try
        {
            var protectedStoreDirectory = await ResolveProtectedDirectoryAsync(
                                                                               SelectedSession?.WorkspaceRoot ??
                                                                               Environment.CurrentDirectory,
                                                                               cancellationToken);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its pin state was loaded.");

            storeKey = PinStoreKey(protectedStoreDirectory);
            var legacy = StoreSidebarPreferences(storeKey);
            var pins = await SessionStorePinStore.LoadOrMigrateAsync(storeKey,
                                                                     legacy.LegacyPinnedProjectOrder,
                                                                     legacy.LegacyPinnedSessionIds,
                                                                     cancellationToken);
            _pinStatesByStore[storeKey] = pins;
            _pinStateUnavailableStores.Remove(storeKey);

            if (legacy.LegacyPinnedProjectOrder.Count == 0 && legacy.LegacyPinnedSessionIds.Count == 0) return;

            UpdateStoreSidebarPreferences(storeKey, preferences => preferences with
            {
                LegacyPinnedProjectOrder = [],
                LegacyPinnedSessionIds = []
            });
            QueueSidebarPreferencesSave();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionStorePinException or SessionStoreException
                                           or IOException or UnauthorizedAccessException)
        {
            _pinStatesByStore.Remove(storeKey);
            _pinStateUnavailableStores.Add(storeKey);
            Notice = $"置顶信息无法读取或迁移：{exception.Message}";
        }
        finally
        {
            // 成功写入与失败移除都可能改快照：在 finally 整体失效两个 ID 集合缓存，未变化的轮次重建结果不变。
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            _pinStateMutationGate.Release();
        }
    }

    private async Task PrepareArchiveStateAsync(string storeDirectory, CancellationToken cancellationToken)
    {
        var storeKey = PinStoreKey(storeDirectory);
        await _pinStateMutationGate.WaitAsync(cancellationToken);
        try
        {
            var protectedStoreDirectory = await ResolveProtectedDirectoryAsync(
                                                                               SelectedSession?.WorkspaceRoot ??
                                                                               Environment.CurrentDirectory,
                                                                               cancellationToken);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its archive state was loaded.");

            storeKey = PinStoreKey(protectedStoreDirectory);
            var archives = await SessionStoreArchiveStore.LoadAsync(storeKey, cancellationToken);
            _archiveStatesByStore[storeKey] = archives;
            _archiveStateUnavailableStores.Remove(storeKey);
            foreach (var session in Sessions.Where(session => session.StoreDirectory is { } source &&
                                                              ProjectRoots.AreEqual(source, storeKey)))
                session.SetArchived(archives.ArchivedSessionIds.Contains(session.Id, StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SessionStoreArchiveException or SessionStoreException
                                           or IOException or UnauthorizedAccessException)
        {
            _archiveStateUnavailableStores.Add(storeKey);
            Notice = $"归档信息无法读取，会话归档状态未知：{exception.Message}";
        }
        finally
        {
            // 成功写入与失败移除都可能改快照：在 finally 整体失效两个 ID 集合缓存，未变化的轮次重建结果不变。
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            _pinStateMutationGate.Release();
        }
    }

    /// <summary>切换 store 后清空上一 store 的整理展示残留；新一轮结束时会重新汇总。</summary>
    private void ResetOrganizeDisplay()
    {
        _organizeIssues      = null;
        _organizeIssuesStore = null;
        OrganizeNotice       = string.Empty;
    }

    /// <summary>
    ///     <para>在配置 I/O 前捕获可自动绑定的草稿与意图代际；显式目标解析在途的页面不参与。</para>
    ///     <para>
    ///         Captures drafts and intent revisions before config I/O; pages with pending explicit target resolutions are
    ///         excluded.
    ///     </para>
    /// </summary>
    private List<(SessionViewModel Page, int Revision)> CaptureDraftTargets()
    {
        var pages = new List<SessionViewModel>(_failedDrafts);
        if (_newConversation is { } draft) pages.Add(draft);
        if (SelectedSession is { Id: null } selected) pages.Add(selected);
        return pages.Distinct().Where(page => page.CanChooseWorkspace && !page.TargetIntentPending)
                    .Select(page => (page, page.TargetIntentRevision)).ToList();
    }

    /// <summary>
    ///     <para>仅为意图未变且未绑定当前 store 的草稿重解析项目；不沿用旧 store 的项目 ID。</para>
    ///     <para>
    ///         Re-resolves projects only for unchanged drafts not bound to the current store; old-store project IDs are
    ///         never reused.
    ///     </para>
    /// </summary>
    private async Task RebindUnboundDraftsAsync(
        string directory, IReadOnlyList<(SessionViewModel Page, int Revision)> targets)
    {
        foreach (var (draft, revision) in targets)
        {
            if (!CanApplyTarget(draft, revision) || !IsApplicableStore(directory, _storeRevision)) continue;
            if (draft.StoreDirectory is { } bound && ProjectRoots.AreEqual(bound, directory))
            {
                await draft.RefreshDefaultModelAsync();
                continue;
            }

            // 自动操作在配置读取前捕获代际；只有期间没有用户新意图时才登记实际重绑。
            draft.CaptureTargetIntent();
            var root = draft.WorkspaceRoot;
            var project = draft.ProjectAssignmentState == SessionProjectAssignmentState.NoProject
                ? null
                : _projects.FirstOrDefault(item =>
                                               item.Roots.Any(projectRoot =>
                                                                  ProjectRoots.AreEqual(projectRoot.Path, root)));
            await draft.SelectProjectAsync(project?.Id, root, directory, false);
        }
    }

    private bool CanApplyTarget(SessionViewModel page, int revision) =>
        !_closing && !IsProjectDeletionRunning && page.CanChooseWorkspace && page.TargetIntentRevision == revision;

    private bool IsApplicableStore(string directory, int revision) =>
        _storeDirectory is { } current
            ? ProjectRoots.AreEqual(current, directory)
            : revision == _storeRevision;

    /// <summary>
    ///     <para>顶部「新对话」：进入同一份未发送的空白页；当前空白页在途或已提升时才另起下一份。</para>
    ///     <para>
    ///         Top "new conversation": enters the same unsent blank page; a fresh one starts only while the current page is
    ///         sending or established.
    ///     </para>
    /// </summary>
    private void NewConversation()
    {
        if (_closing) return;
        SelectedSession = ConversationDraft();
    }

    /// <summary>
    ///     <para>返回可进入的空白新对话页：优先复用未发送的当前空白页，在途或已提升时新建下一份并接管空白槽。</para>
    ///     <para>
    ///         Returns the enterable blank page: reuses the unsent current one, or starts the next page taking over the
    ///         blank slot when it is sending or established.
    ///     </para>
    /// </summary>
    private SessionViewModel ConversationDraft()
    {
        if (_newConversation is { } page && page.Id is null && !page.IsRunning) return page;
        return CreateConversationDraft();
    }

    /// <summary>
    ///     <para>新建下一份空白页；root 继承上一份空白页的目标，不继承刚浏览的历史会话目录。</para>
    ///     <para>
    ///         Creates the next blank page; the root follows the previous blank page's target, never the just-visited
    ///         history session's directory.
    ///     </para>
    /// </summary>
    private SessionViewModel CreateConversationDraft()
    {
        var root = _newConversation?.WorkspaceRoot ?? Environment.CurrentDirectory;
        var session = new SessionViewModel(_manager, _factory, root, null, null, null, _dispatch, null, null,
                                           SessionProjectAssignmentState.NoProject)
        {
            ModelOptions        = Models,
            ManageModelsCommand = OpenSettingsCommand,
            // 与 CreateSessionRow 同一纪律：优先复用缓存分发实例；空缓存等价于原来的空数组，RebuildProjects 尾部统一兜底。
            ProjectOptions      = _cachedProjectOptions ?? Projects.Where(group => group.CanEdit).ToArray()
        };
        session.SelectProjectCommand = new RelayCommand<ProjectViewModel?>(
                                                                           group => _ =
                                                                               SelectConversationProjectAsync(session,
                                                                                   group),
                                                                           group => session.CanChooseWorkspace &&
                                                                               (group is null || group.CanEdit));
        session.Established += OnConversationEstablished;
        session.ApplyPermissionSettings(_permissionSettings);
        session.FirstSendFailed += OnConversationFirstSendFailed;
        _newConversation        =  session;
        _                       =  session.RefreshDefaultModelAsync();
        _                       =  BindNewDraftAsync(session);
        return session;
    }

    /// <summary>
    ///     <para>空白页首发取得真实 SessionId：登记为正式会话行（仅一次）；迟到登记不抢选中、不清其他页输入。</para>
    ///     <para>
    ///         A blank page gains a real SessionId on its first send: register the formal row exactly once; late
    ///         registration never steals selection or clears other pages' input.
    ///     </para>
    /// </summary>
    private void OnConversationEstablished(SessionViewModel session)
    {
        if (_closing || session.Id is null || Sessions.Contains(session)) return;
        PrioritizeSessionInManualOrder(session);
        AddSession(session);
        if (_failedDrafts.RemoveAll(item => ReferenceEquals(item, session)) > 0)
            OnPropertyChanged(nameof(HasFailedDraft));
        OnPropertyChanged(nameof(IsNewConversationSelected));
    }

    /// <summary>
    ///     <para>
    ///         首发在取得真实 ID 前失败：保留文字与预选。仅回收未被选中且无用户内容的空白槽，
    ///         其他被换出的失败页进入去重恢复列表；不改变选中或覆盖下一份草稿。
    ///     </para>
    ///     <para>
    ///         Retains text and preselections when opening fails before a real Id. Only an unselected pristine slot is
    ///         reclaimed; other displaced failed pages enter the deduplicated recovery list without changing selection
    ///         or overwriting the next draft.
    ///     </para>
    /// </summary>
    private void OnConversationFirstSendFailed(SessionViewModel session)
    {
        if (_closing || ReferenceEquals(_newConversation, session) || ReferenceEquals(SelectedSession, session)) return;
        // 下一份仍无任何用户预选（无文字、无显式项目／模型／权限；自动绑定可重解析）且未被用户正在查看：
        // 无损换回失败页；正被选中的空白页不得被回收顶替。
        if (!ReferenceEquals(SelectedSession, _newConversation) &&
            _newConversation is { Id: null, Draft.Length: 0, HasUserPreselection: false })
        {
            _newConversation = session;
            return;
        }

        // 下一份被选中或已有内容／预选：不覆盖、不抢导航、不伪装正式侧栏行，保留仍有内容的失败草稿供显式恢复。
        RegisterFailedDraft(session);
    }

    /// <summary>
    ///     <para>登记一份仍有内容或显式预选的失败草稿；重复登记与已提升页面被忽略。</para>
    ///     <para>
    ///         Registers a failed draft that still holds content or explicit preselections; duplicates and established pages
    ///         are ignored.
    ///     </para>
    /// </summary>
    private void RegisterFailedDraft(SessionViewModel session)
    {
        // 无文字也无显式预选的失败页没有可丢失的用户输入，不进入恢复列表。
        if (session.Id is not null || (session.Draft.Length == 0 && !session.HasUserPreselection)) return;
        if (_failedDrafts.Any(item => ReferenceEquals(item, session))) return;
        _failedDrafts.Add(session);
        OnPropertyChanged(nameof(HasFailedDraft));
    }

    /// <summary>
    ///     <para>空白页项目菜单选择：按页身份切换该页目标；在途或已建立的页面目标冻结，迟到解析不改写。</para>
    ///     <para>
    ///         Blank-page project menu selection: retargets that page by identity; in-flight or established targets are
    ///         frozen and late resolutions never rewrite them.
    ///     </para>
    /// </summary>
    internal async Task SelectConversationProjectAsync(SessionViewModel page, ProjectViewModel? group)
    {
        if (_closing || IsProjectDeletionRunning || !page.CanChooseWorkspace) return;
        var revision = page.BeginTargetIntent();
        try
        {
            if (group?.Project is { } project)
            {
                await ApplyProjectChoiceAsync(page, project.PrimaryRoot, revision);
                return;
            }

            var storeRevision = _storeRevision;
            var directory     = await ResolveProtectedDirectoryAsync(page.WorkspaceRoot, _lifetime.Token);
            if (!CanApplyTarget(page, revision) || !IsApplicableStore(directory, storeRevision)) return;
            await page.SelectProjectAsync(null, page.WorkspaceRoot, directory);
        }
        catch (OperationCanceledException)
        {
            // 关闭过程中的取消不构成错误。
        }
        catch (Exception exception)
        {
            Notice = exception.Message;
        }
        finally
        {
            page.CompleteTargetIntent(revision);
        }
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
            var revision      = session.TargetIntentRevision;
            var storeRevision = _storeRevision;
            var root          = session.WorkspaceRoot;
            var directory     = await ResolveProtectedDirectoryAsync(root, _lifetime.Token);
            if (!CanApplyTarget(session, revision)           ||
                !IsApplicableStore(directory, storeRevision) ||
                session.StoreDirectory is not null           ||
                session.WorkspaceRoot != root)
                return;
            var projectId = session.ProjectAssignmentState == SessionProjectAssignmentState.Project
                ? session.ProjectId
                : null;
            session.CaptureTargetIntent();
            await session.SelectProjectAsync(projectId, root, directory, false);
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
            if (!_loadingCatalog && args.PropertyName == nameof(SessionViewModel.LastActivityUtc))
                PrioritizeSessionInManualOrder(session);
            if (!_loadingCatalog &&
                args.PropertyName is nameof(SessionViewModel.ProjectId) or
                                     nameof(SessionViewModel.ProjectAssignmentState) or
                                     nameof(SessionViewModel.LastActivityUtc) or
                                     nameof(SessionViewModel.IsRunning))
                RebuildProjects();
            if (args.PropertyName == nameof(SessionViewModel.ProjectId)) ReloadCatalogForUnknownProject(session);
        };
        if (!_loadingCatalog) RebuildProjects();
        // 首发登记时 ProjectId 通知早于上方订阅到达（OpenRuntimeAsync 先改属性再触发 Established），登记本身也补查一次。
        ReloadCatalogForUnknownProject(session);
    }

    /// <summary>
    ///     <para>
    ///         行所属项目不在当前目录册时按 store 只读补载一次；行不属于当前 store、加载中或整理整轮在途时不触发，
    ///         普通历史行加载与整轮尾部统一刷新不叠加额外刷新，同一项目 ID 至多重试一次。
    ///     </para>
    ///     <para>
    ///         Reloads the catalog read-only once when the row's project is unknown in its own store; rows outside the
    ///         current store, loading states and running organize rounds never trigger it, and each project ID retries at
    ///         most once.
    ///     </para>
    /// </summary>
    private void ReloadCatalogForUnknownProject(SessionViewModel session)
    {
        if (_closing || _loadingCatalog || _organizeSource is not null) return;
        if (session.ProjectId is not { } id            ||
            _storeDirectory is not { } current         ||
            session.StoreDirectory is not { } rowStore ||
            !ProjectRoots.AreEqual(rowStore, current)  ||
            _projects.Any(project => project.Id == id))
            return;
        if (!_projectReloadRetried.Add(id)) return;
        _ = InitializeAsync();
    }

    /// <summary>
    ///     <para>
    ///         统一加载结束后的补查：加载期间首发建立的项目可能不在本次目录册快照内；
    ///         仅对当前 store 内仍未知的项目 ID 触发一次只读补载，每个 ID 至多重试一次。
    ///     </para>
    ///     <para>
    ///         Post-load reconciliation: a project established during the load may miss its catalog snapshot; at most one
    ///         read-only reload runs per still-unknown project ID within the current store.
    ///     </para>
    /// </summary>
    private void ReloadCatalogForUnknownProjectsAfterLoad()
    {
        if (_closing || _organizeSource is not null || _storeDirectory is not { } current) return;
        var reload = false;
        foreach (var session in Sessions)
        {
            if (session.ProjectId is not { } id            ||
                session.StoreDirectory is not { } rowStore ||
                !ProjectRoots.AreEqual(rowStore, current)  ||
                _projects.Any(project => project.Id == id) ||
                !_projectReloadRetried.Add(id))
                continue;
            reload = true;
        }

        if (reload) _ = InitializeAsync();
    }

    /// <summary>
    ///     <para>首次准备侧栏前读取桌面偏好；损坏的偏好只提示并回退默认值，不阻止会话加载。</para>
    ///     <para>
    ///         Loads desktop preferences before the sidebar is first prepared; damaged preferences fall back to defaults
    ///         without blocking sessions.
    ///     </para>
    /// </summary>
    private async Task EnsureSidebarPreferencesLoadedAsync(CancellationToken cancellationToken)
    {
        if (SidebarPreferencesReady) return;
        await _sidebarPreferencesLoadGate.WaitAsync(cancellationToken);
        try
        {
            if (SidebarPreferencesReady) return;
            try
            {
                _sidebarPreferences = await SidebarPreferencesStore.LoadAsync(_sidebarPreferencesPath,
                                                                              cancellationToken);
                _sidebarPreferencesSaveAllowed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                Notice                         = $"侧栏设置无法读取，已使用默认设置：{exception.Message}";
                _sidebarPreferencesSaveAllowed = false;
            }

            if (_pendingProjectDeletionPromptPreference is { } showPrompt)
                _sidebarPreferences = _sidebarPreferences with { ShowProjectDeletionPrompt = showPrompt };

            var savePromptPreference = _pendingProjectDeletionPromptPreference.HasValue &&
                                       _sidebarPreferencesSaveAllowed;
            _pendingProjectDeletionPromptPreference = null;
            Settings.General.ApplyProjectDeletionPromptPreference(_sidebarPreferences.ShowProjectDeletionPrompt);

            RestoreSidebarCategoryExpansion();
            SidebarPreferencesReady = true;
            NotifySidebarPreferenceProperties();
            if (savePromptPreference) QueueSidebarPreferencesSave();
        }
        finally
        {
            _sidebarPreferencesLoadGate.Release();
        }
    }

    /// <summary>
    ///     <para>同步通用设置中的删除提示开关，并保存到桌面侧栏偏好。</para>
    ///     <para>Synchronizes the general deletion-prompt setting and saves it to desktop sidebar preferences.</para>
    /// </summary>
    private void OnProjectDeletionPromptChanged(bool showPrompt)
    {
        _sidebarPreferences = _sidebarPreferences with { ShowProjectDeletionPrompt = showPrompt };
        if (!SidebarPreferencesReady)
        {
            _pendingProjectDeletionPromptPreference = showPrompt;
            return;
        }

        QueueSidebarPreferencesSave();
    }

    /// <summary>
    ///     <para>按已保存的分类键恢复固定分类展开状态；未保存的分类保持默认展开。</para>
    ///     <para>Restores fixed category expansion from saved keys; categories without saved keys remain expanded.</para>
    /// </summary>
    private void RestoreSidebarCategoryExpansion()
    {
        var collapsed = _sidebarPreferences.CollapsedCategoryKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var category in CollapsibleCategories) category.IsExpanded = !collapsed.Contains(category.Key);
    }

    /// <summary>
    ///     <para>切换侧栏列表设置面板的展开状态。</para>
    ///     <para>Toggles whether the sidebar list-settings panel is open.</para>
    /// </summary>
    private void ToggleSidebarSettingsMenu()
    {
        IsSidebarSettingsMenuOpen = !IsSidebarSettingsMenuOpen;
    }

    /// <summary>
    ///     <para>选择纯会话或项目分类显示，并保留会话与项目各自的置顶状态。</para>
    ///     <para>Selects flat sessions or project grouping while retaining session and project pin states.</para>
    /// </summary>
    private void SelectSidebarMode(bool isProjectMode)
    {
        IsSidebarSettingsMenuOpen = false;
        if (IsProjectMode == isProjectMode) return;

        _sidebarPreferences = _sidebarPreferences with { IsProjectMode = isProjectMode };
        NotifySidebarPreferenceProperties();
        foreach (var group in Projects)
            group.SetSidebarState(group.IsPinned, IsProjectMode && IsManualProjectOrder);
        RebuildSidebarRows();
        QueueSidebarPreferencesSave(true);
    }

    /// <summary>
    ///     <para>选择按最近活动或手动顺序排列项目。</para>
    ///     <para>Selects recent-activity or manual ordering for projects.</para>
    /// </summary>
    private void SelectProjectSort(bool sortByActivity)
    {
        IsSidebarSettingsMenuOpen = false;
        if (_sidebarPreferences.SortProjectsByActivity == sortByActivity) return;

        _sidebarPreferences = _sidebarPreferences with
        {
            SortProjectsByActivity = sortByActivity
        };
        NotifySidebarPreferenceProperties();
        RebuildProjects();
        QueueSidebarPreferencesSave(true);
    }

    /// <summary>
    ///     <para>切换项目或展示组的展开状态，并按所属 store 保存折叠键。</para>
    ///     <para>Toggles a project or display group's expansion and saves its collapse key by owning store.</para>
    /// </summary>
    private void ToggleProjectGroup(ProjectViewModel? group)
    {
        if (group is null || !SidebarPreferencesReady ||
            (IsProjectDeletionRunning && ReferenceEquals(group, _projectDeletionTarget)))
            return;

        group.IsExpanded = !group.IsExpanded;
        if (SidebarStoreForGroup(group) is { } storeDirectory)
        {
            var key = SidebarGroupExpansionKey(group.Key);
            UpdateStoreSidebarPreferences(storeDirectory, preferences =>
            {
                var collapsed = preferences.CollapsedGroupKeys.ToHashSet(StringComparer.Ordinal);
                var changed   = group.IsExpanded ? collapsed.Remove(key) : collapsed.Add(key);
                return changed
                    ? preferences with
                    {
                        CollapsedGroupKeys = collapsed.OrderBy(item => item, StringComparer.Ordinal).ToArray()
                    }
                    : preferences;
            });
            QueueSidebarPreferencesSave(true);
        }

        RebuildSidebarRows();
    }

    /// <summary>
    ///     <para>切换固定侧栏分类的展开状态并保存分类键。</para>
    ///     <para>Toggles a fixed sidebar category and saves its category key.</para>
    /// </summary>
    private void ToggleSidebarCategory(SidebarCategoryViewModel? category)
    {
        if (category is not { CanCollapse: true } || !SidebarPreferencesReady ||
            !CollapsibleCategories.Contains(category))
            return;

        category.IsExpanded = !category.IsExpanded;
        var collapsed = _sidebarPreferences.CollapsedCategoryKeys.ToHashSet(StringComparer.Ordinal);
        var changed   = category.IsExpanded ? collapsed.Remove(category.Key) : collapsed.Add(category.Key);
        if (changed)
        {
            _sidebarPreferences = _sidebarPreferences with
            {
                CollapsedCategoryKeys = collapsed.OrderBy(item => item, StringComparer.Ordinal).ToArray()
            };
            QueueSidebarPreferencesSave(true);
        }

        RebuildSidebarRows();
    }

    /// <summary>
    ///     <para>将项目移入或移出置顶分类；新置顶项目插入置顶分类顶部。</para>
    ///     <para>Moves a project into or out of the pinned category; newly pinned projects enter at its top.</para>
    /// </summary>
    private void ToggleProjectPin(ProjectViewModel? group)
    {
        if (IsProjectDeletionRunning         || group is not { CanEdit: true, Project: { } project } ||
            _storeDirectory is not { } store ||
            !Projects.Contains(group)        ||
            !CanUsePinState(store))
            return;

        _ = SetProjectPinAsync(project.Id, store, !group.IsPinned);
    }

    /// <summary>
    ///     <para>按项目头行的拖放位置调整同一分类内的手动顺序。</para>
    ///     <para>Applies a drop position to the manual order within one project category.</para>
    /// </summary>
    public void MoveProject(ProjectViewModel source, ProjectViewModel target, bool insertAfter)
    {
        if (IsProjectDeletionRunning   || !IsProjectMode             || !IsManualProjectOrder || source == target ||
            !source.CanReorder         || !target.CanReorder         || source.IsPinned != target.IsPinned ||
            !Projects.Contains(source) || !Projects.Contains(target) || _storeDirectory is not { } store)
            return;

        if (source.IsPinned)
        {
            if (CanUsePinState(store)) _ = MovePinnedProjectAsync(store, source.Key, target.Key, insertAfter);
            return;
        }

        var moved = false;
        UpdateStoreSidebarPreferences(store, preferences =>
        {
            var order       = preferences.ManualProjectOrder.ToList();
            var sourceIndex = order.IndexOf(source.Key);
            var targetIndex = order.IndexOf(target.Key);
            if (sourceIndex < 0 || targetIndex < 0) return preferences;

            order.RemoveAt(sourceIndex);
            var insertIndex = targetIndex + (insertAfter ? 1 : 0);
            if (sourceIndex < insertIndex) insertIndex--;
            order.Insert(Math.Clamp(insertIndex, 0, order.Count), source.Key);
            moved = true;
            return preferences with { ManualProjectOrder = order };
        });
        if (!moved) return;

        RebuildProjects();
        QueueSidebarPreferencesSave(true);
    }

    /// <summary>
    ///     <para>检查两个空闲会话是否能在当前侧栏分类中重排。</para>
    ///     <para>Checks whether two idle sessions can be reordered within their current sidebar category.</para>
    /// </summary>
    internal bool CanMoveSession(SessionViewModel source, SessionViewModel target)
    {
        if (ReferenceEquals(source, target)              || !source.CanReorder || !target.CanReorder ||
            source.IsPinned != target.IsPinned           || !Sessions.Contains(source) || !Sessions.Contains(target) ||
            source.Id is null                            || target.Id is null ||
            source.StoreDirectory is not { } sourceStore || target.StoreDirectory is not { } targetStore ||
            !ProjectRoots.AreEqual(sourceStore, targetStore))
            return false;

        if (!IsProjectMode || source.IsPinned) return true;
        if (source.ProjectAssignmentState != target.ProjectAssignmentState) return false;

        return source.ProjectAssignmentState switch
        {
            SessionProjectAssignmentState.Project => source.ProjectId == target.ProjectId,
            SessionProjectAssignmentState.PendingInitialization =>
                ProjectRoots.AreEqual(source.WorkspaceRoot, target.WorkspaceRoot),
            SessionProjectAssignmentState.NoProject => true,
            _                                       => false
        };
    }

    /// <summary>
    ///     <para>按拖放位置调整同一侧栏分类内的空闲会话顺序。</para>
    ///     <para>Applies a drop position to reorder idle sessions within one sidebar category.</para>
    /// </summary>
    public void MoveSession(SessionViewModel source, SessionViewModel target, bool insertAfter)
    {
        if (!CanMoveSession(source, target) || source.StoreDirectory is not { } store ||
            source.Id is not { } sourceId   || target.Id is not { } targetId)
            return;

        var changed = false;
        UpdateStoreSidebarPreferences(store, preferences =>
        {
            var order    = preferences.ManualSessionOrder.ToList();
            var original = order.ToArray();
            var known    = order.ToHashSet(StringComparer.Ordinal);
            var missing = Sessions.Where(session => session.Id is not null                     &&
                                                    session.StoreDirectory is { } sessionStore &&
                                                    ProjectRoots.AreEqual(sessionStore, store))
                                  .OrderByDescending(session => session.LastActivityUtc)
                                  .Select(session => session.Id!)
                                  .Where(known.Add)
                                  .Take(Math.Max(0, SidebarPreferencesStore.MaxSessionOrderEntries - order.Count))
                                  .ToArray();
            if (order.Count == 0) order.InsertRange(0, missing);
            else order.AddRange(missing);

            var sourceIndex = order.IndexOf(sourceId);
            var targetIndex = order.IndexOf(targetId);
            if (sourceIndex >= 0 && targetIndex >= 0)
            {
                order.RemoveAt(sourceIndex);
                var insertIndex = targetIndex + (insertAfter ? 1 : 0);
                if (sourceIndex < insertIndex) insertIndex--;
                order.Insert(Math.Clamp(insertIndex, 0, order.Count), sourceId);
            }

            changed = !order.SequenceEqual(original, StringComparer.Ordinal);
            return changed ? preferences with { ManualSessionOrder = order } : preferences;
        });
        if (!changed) return;

        RebuildSidebarRows();
        QueueSidebarPreferencesSave(true);
    }

    /// <summary>
    ///     <para>切换正式会话在侧栏中的置顶状态。</para>
    ///     <para>Toggles an established session's pinned state in the sidebar.</para>
    /// </summary>
    private void ToggleSessionPin(SessionViewModel? session)
    {
        if (session is not { CanPin: true, Id: { } id, StoreDirectory: { } store } ||
            !CanUsePinState(store))
            return;

        _ = SetSessionPinAsync(store, id, !session.IsPinned);
    }

    private void ToggleSessionArchive(SessionViewModel? session)
    {
        if (session is not
                { CanArchive: true, Id: { } id, StoreDirectory: { } store, WorkspaceRoot: { } root } ||
            !CanUseArchiveState(store))
            return;

        if (!session.TryBeginArchiveMutation()) return;
        _ = SetSessionArchivedAsync(session, store, root, id, !session.IsArchived);
    }

    private void ToggleArchiveView()
    {
        if (!IsArchiveView && _storeDirectory is { } store && !CanUseArchiveState(store)) return;
        IsArchiveView = !IsArchiveView;
        OnPropertyChanged(nameof(IsArchiveView));
        OnPropertyChanged(nameof(ArchiveViewButtonName));
        RebuildSidebarRows();
    }

    /// <summary>
    ///     <para>持久化项目置顶状态并重建侧栏，保留项目在普通分类中的手动位置。</para>
    ///     <para>
    ///         Persists a project's pin state and rebuilds the sidebar while preserving its manual position
    ///         among regular projects.
    ///     </para>
    /// </summary>
    private async Task SetProjectPinAsync(string projectId, string storeDirectory, bool isPinned)
    {
        var acquired = false;
        try
        {
            await _pinStateMutationGate.WaitAsync(_lifetime.Token);
            acquired = true;
            if (IsProjectDeletionRunning) return;
            var pins = await SessionStorePinStore.SetProjectPinnedAsync(storeDirectory, projectId, isPinned,
                                                                        _lifetime.Token);
            SetStorePinState(storeDirectory, pins);
            RebuildProjects();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭窗口时放弃尚未提交的置顶操作。
        }
        catch (Exception exception)
        {
            Notice = $"项目置顶状态保存失败：{exception.Message}";
        }
        finally
        {
            if (acquired) _pinStateMutationGate.Release();
        }
    }

    /// <summary>
    ///     <para>持久化会话的目标置顶状态，并刷新侧栏行。</para>
    ///     <para>Persists the requested session pin state and refreshes sidebar rows.</para>
    /// </summary>
    private async Task SetSessionPinAsync(string storeDirectory, string sessionId, bool isPinned)
    {
        var acquired = false;
        try
        {
            await _pinStateMutationGate.WaitAsync(_lifetime.Token);
            acquired = true;
            var pins = await SessionStorePinStore.SetSessionPinnedAsync(storeDirectory, sessionId, isPinned,
                                                                        _lifetime.Token);
            SetStorePinState(storeDirectory, pins);
            RebuildSidebarRows();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭窗口时放弃尚未提交的置顶操作。
        }
        catch (Exception exception)
        {
            Notice = $"会话置顶状态保存失败：{exception.Message}";
        }
        finally
        {
            if (acquired) _pinStateMutationGate.Release();
        }
    }

    private async Task SetSessionArchivedAsync(
        SessionViewModel session, string storeDirectory, string workspaceRoot, string sessionId, bool isArchived)
    {
        var acquired = false;
        try
        {
            await _pinStateMutationGate.WaitAsync(_lifetime.Token);
            acquired = true;
            var protectedStoreDirectory = await ResolveProtectedDirectoryAsync(workspaceRoot, _lifetime.Token);
            if (!ProjectRoots.AreEqual(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before its archive state was saved.");

            var archives = await SessionStoreArchiveStore.SetArchivedAsync(
                                                                           storeDirectory, sessionId, isArchived,
                                                                           _lifetime.Token);
            SetStoreArchiveState(storeDirectory, archives);
            // 权威归档状态已保存；索引投影尽力同步，失败不回滚归档保存，由后台对账从归档来源追平。
            // The authoritative archive state is saved; the index projection syncs best-effort — a failure never
            // rolls the save back, and background reconciliation catches up from the archive source.
            try
            {
                var index        = new SessionIndexStore(storeDirectory);
                // 索引缺失或不可用时不同步也不创建库文件：归档权威已保存，投影缺口留给后台对账追平。
                // A missing or unusable index skips the sync without creating a database file: the authoritative
                // archive state is saved, and background reconciliation closes the projection gap.
                var availability = await index.GetAvailabilityAsync(_lifetime.Token).ConfigureAwait(false);
                if (availability.Status == SessionIndexAvailabilityStatus.Available)
                    await index.SyncArchiveProjectionAsync(
                                                           new HashSet<string>(archives.ArchivedSessionIds,
                                                                               StringComparer.Ordinal),
                                                           _lifetime.Token)
                               .ConfigureAwait(false);
            }
            catch (Exception indexException) when (indexException is SessionIndexException or IOException or
                                                                     UnauthorizedAccessException)
            {
                Trace.TraceError("Session index archive projection for '{0}' failed to sync: {1}",
                                 storeDirectory, indexException.Message);
            }

            foreach (var row in Sessions.Where(row => row.Id == sessionId              &&
                                                      row.StoreDirectory is { } source &&
                                                      ProjectRoots.AreEqual(source, storeDirectory)))
                row.SetArchived(isArchived);
            RebuildProjects();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭窗口时放弃尚未提交的归档操作。
        }
        catch (Exception exception)
        {
            Notice = $"会话归档状态保存失败：{exception.Message}";
        }
        finally
        {
            if (acquired) _pinStateMutationGate.Release();
            session.EndArchiveMutation();
        }
    }

    private async Task MovePinnedProjectAsync(
        string storeDirectory, string sourceProjectId, string targetProjectId, bool insertAfter)
    {
        var acquired = false;
        try
        {
            await _pinStateMutationGate.WaitAsync(_lifetime.Token);
            acquired = true;
            if (IsProjectDeletionRunning) return;
            var pins = await SessionStorePinStore.MovePinnedProjectAsync(storeDirectory, sourceProjectId,
                                                                         targetProjectId, insertAfter,
                                                                         _lifetime.Token);
            SetStorePinState(storeDirectory, pins);
            RebuildProjects();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭窗口时放弃尚未提交的排序操作。
        }
        catch (Exception exception)
        {
            Notice = $"置顶项目顺序保存失败：{exception.Message}";
        }
        finally
        {
            if (acquired) _pinStateMutationGate.Release();
        }
    }

    private bool CanUsePinState(string storeDirectory)
    {
        if (!_pinStateUnavailableStores.Contains(PinStoreKey(storeDirectory))) return true;
        Notice = "当前会话存储目录的置顶状态不可用；请刷新历史后重试。";
        return false;
    }

    private bool CanUseArchiveState(string storeDirectory)
    {
        if (!_archiveStateUnavailableStores.Contains(PinStoreKey(storeDirectory))) return true;
        Notice = "当前归档状态未知，无法查看或修改归档状态；请刷新历史后重试。";
        return false;
    }

    private void SetStorePinState(string storeDirectory, SessionStorePins pins)
    {
        var storeKey = PinStoreKey(storeDirectory);
        _pinStatesByStore[storeKey] = pins;
        _pinStateUnavailableStores.Remove(storeKey);
        // 快照已变：整体失效两个 ID 集合缓存，懒重建成本可忽略。
        _pinnedSessionIdsByStore.Clear();
        _archivedSessionIdsByStore.Clear();
    }

    private SessionStorePins PinsForStore(string? storeDirectory)
    {
        if (storeDirectory is null) return SessionStorePins.Empty();
        return _pinStatesByStore.GetValueOrDefault(PinStoreKey(storeDirectory)) ?? SessionStorePins.Empty();
    }

    private void SetStoreArchiveState(string storeDirectory, SessionStoreArchives archives)
    {
        var storeKey = PinStoreKey(storeDirectory);
        _archiveStatesByStore[storeKey] = archives;
        _archiveStateUnavailableStores.Remove(storeKey);
        // 快照已变：整体失效两个 ID 集合缓存，懒重建成本可忽略。
        _pinnedSessionIdsByStore.Clear();
        _archivedSessionIdsByStore.Clear();
    }

    private SessionStoreArchives ArchivesForStore(string? storeDirectory)
    {
        if (storeDirectory is null) return SessionStoreArchives.Empty();
        return _archiveStatesByStore.GetValueOrDefault(PinStoreKey(storeDirectory)) ??
               SessionStoreArchives.Empty();
    }

    /// <summary>
    ///     <para>按 store 懒构建已归档会话 ID 集合；快照写入点整体失效后由下一次查询重建。</para>
    ///     <para>
    ///         Lazily builds a store's archived-session id set; snapshot writes invalidate it wholesale and the
    ///         next query rebuilds it.
    ///     </para>
    /// </summary>
    private HashSet<string> ArchivedSessionIdsForStore(string storeDirectory)
    {
        var storeKey = PinStoreKey(storeDirectory);
        if (_archivedSessionIdsByStore.TryGetValue(storeKey, out var archivedIds)) return archivedIds;
        var ids = ArchivesForStore(storeDirectory).ArchivedSessionIds.ToHashSet(StringComparer.Ordinal);
        _archivedSessionIdsByStore[storeKey] = ids;
        return ids;
    }

    /// <summary>
    ///     <para>按 store 懒构建置顶会话 ID 集合；快照写入点整体失效后由下一次查询重建。</para>
    ///     <para>
    ///         Lazily builds a store's pinned-session id set; snapshot writes invalidate it wholesale and the
    ///         next query rebuilds it.
    ///     </para>
    /// </summary>
    private HashSet<string> PinnedSessionIdsForStore(string storeDirectory)
    {
        var storeKey = PinStoreKey(storeDirectory);
        if (_pinnedSessionIdsByStore.TryGetValue(storeKey, out var pinnedIds)) return pinnedIds;
        var ids = PinsForStore(storeDirectory).PinnedSessionIds.ToHashSet(StringComparer.Ordinal);
        _pinnedSessionIdsByStore[storeKey] = ids;
        return ids;
    }

    private bool IsSessionArchived(SessionViewModel session)
    {
        if (session.Id is not { } id || session.StoreDirectory is not { } store) return false;
        return ArchivedSessionIdsForStore(store).Contains(id);
    }

    private static string PinStoreKey(string storeDirectory) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(storeDirectory));

    /// <summary>
    ///     <para>按目录定位偏好状态并替换对应快照，不把不同 store 的同名 ID 合并。</para>
    ///     <para>Replaces one store's preference snapshot without merging matching ids from other stores.</para>
    /// </summary>
    private void UpdateStoreSidebarPreferences(string                                                 storeDirectory,
                                               Func<SidebarStorePreferences, SidebarStorePreferences> update)
    {
        var normalized = Path.GetFullPath(storeDirectory);
        var stores     = _sidebarPreferences.Stores.ToList();
        var index      = stores.FindIndex(item => ProjectRoots.AreEqual(item.StoreDirectory, normalized));
        var current = index < 0
            ? new SidebarStorePreferences { StoreDirectory = normalized }
            : stores[index];
        var updated = update(current) with { StoreDirectory = normalized };
        if (index < 0) stores.Add(updated);
        else stores[index] = updated;
        _sidebarPreferences = _sidebarPreferences with { Stores = stores };
    }

    /// <summary>
    ///     <para>将新建或最近活动的会话放到所属 store 的手动顺序顶部。</para>
    ///     <para>Places a new or recently active session at the top of its store's manual order.</para>
    /// </summary>
    private void PrioritizeSessionInManualOrder(SessionViewModel session)
    {
        if (!SidebarPreferencesReady || session.Id is not { } id || session.StoreDirectory is not { } store) return;

        var changed = false;
        UpdateStoreSidebarPreferences(store, preferences =>
        {
            if (preferences.ManualSessionOrder.FirstOrDefault() == id) return preferences;

            var order = preferences.ManualSessionOrder.Where(item => item != id).ToList();
            order.Insert(0, id);
            if (order.Count > SidebarPreferencesStore.MaxSessionOrderEntries)
                order.RemoveAt(order.Count - 1);
            changed = true;
            return preferences with { ManualSessionOrder = order };
        });
        if (changed) QueueSidebarPreferencesSave();
    }

    /// <summary>
    ///     <para>通知绑定侧栏列表设置选项的偏好属性。</para>
    ///     <para>Notifies preference properties bound to sidebar list-settings options.</para>
    /// </summary>
    private void NotifySidebarPreferenceProperties()
    {
        OnPropertyChanged(nameof(IsProjectMode));
        OnPropertyChanged(nameof(IsManualProjectOrder));
        OnPropertyChanged(nameof(SidebarPreferencesReady));
    }

    /// <summary>
    ///     <para>串行保存当前完整偏好快照；自动补齐项目顺序时不覆盖损坏文件。</para>
    ///     <para>Serializes saves of the full preference snapshot; automatic order seeding never overwrites a damaged file.</para>
    /// </summary>
    private void QueueSidebarPreferencesSave(bool userInitiated = false)
    {
        if (userInitiated) _sidebarPreferencesSaveAllowed = true;
        if (!SidebarPreferencesReady || !_sidebarPreferencesSaveAllowed || _closing) return;
        _ = SaveSidebarPreferencesAsync();
    }

    /// <summary>
    ///     <para>在独占写入门内保存最新侧栏偏好，失败时通过窗口提示呈现。</para>
    ///     <para>Saves the latest sidebar preferences under an exclusive gate and reports failures in the window notice.</para>
    /// </summary>
    private async Task SaveSidebarPreferencesAsync()
    {
        var acquired = false;
        try
        {
            await _sidebarPreferencesSaveGate.WaitAsync(_lifetime.Token);
            acquired = true;
            await SidebarPreferencesStore.SaveAsync(_sidebarPreferencesPath, _sidebarPreferences, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭时取消偏好落盘。
        }
        catch (Exception exception)
        {
            Notice = $"侧栏设置保存失败：{exception.Message}";
        }
        finally
        {
            if (acquired) _sidebarPreferencesSaveGate.Release();
        }
    }

    /// <summary>
    ///     <para>取消后台保存前落盘最新快照，避免用户关闭窗口时丢失刚调整的侧栏状态。</para>
    ///     <para>
    ///         Persists the latest snapshot before background cancellation so closing the window does not lose recent
    ///         sidebar changes.
    ///     </para>
    /// </summary>
    private async Task FlushSidebarPreferencesAsync()
    {
        if (!SidebarPreferencesReady || !_sidebarPreferencesSaveAllowed) return;
        await _sidebarPreferencesSaveGate.WaitAsync();
        try
        {
            await SidebarPreferencesStore.SaveAsync(_sidebarPreferencesPath, _sidebarPreferences,
                                                    CancellationToken.None);
        }
        finally
        {
            _sidebarPreferencesSaveGate.Release();
        }
    }

    /// <summary>
    ///     <para>读取指定 store 的侧栏状态；未保存时返回空状态。</para>
    ///     <para>Returns one store's sidebar state, or an empty state when none has been saved.</para>
    /// </summary>
    private SidebarStorePreferences StoreSidebarPreferences(string? storeDirectory)
    {
        if (storeDirectory is null) return new SidebarStorePreferences();
        return _sidebarPreferences.Stores.FirstOrDefault(item =>
                                                             ProjectRoots.AreEqual(item.StoreDirectory,
                                                                 storeDirectory)) ??
               new SidebarStorePreferences { StoreDirectory = Path.GetFullPath(storeDirectory) };
    }

    /// <summary>
    ///     <para>按组成员的存储目录定位折叠偏好；无成员项目使用当前目录册。</para>
    ///     <para>Finds a group's collapse preferences by member store; empty projects use the current store.</para>
    /// </summary>
    private string? SidebarStoreForGroup(ProjectViewModel group) =>
        group.StoreDirectory ??
        group.Sessions.Select(session => session.StoreDirectory).FirstOrDefault(store => store is not null) ??
        _storeDirectory;

    /// <summary>
    ///     <para>比较两个侧栏分组的所属存储目录。</para>
    ///     <para>Compares the owning store directories of two sidebar groups.</para>
    /// </summary>
    private static bool SameSidebarStore(string? left, string? right) =>
        left is null ? right is null : right is not null && ProjectRoots.AreEqual(left, right);

    /// <summary>
    ///     <para>将可能较长的展示组身份转换为有界且跨重启稳定的偏好键。</para>
    ///     <para>Converts a potentially long display-group identity into a bounded, restart-stable preference key.</para>
    /// </summary>
    private static string SidebarGroupExpansionKey(string groupKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(groupKey)));

    private void RebuildProjects()
    {
        var labels   = CaptureWorkspaceTargetLabels();
        var previous = Projects.ToDictionary(group => group.Key);
        var groups   = new Dictionary<string, ProjectViewModel>();

        ProjectViewModel Group(string  key, Project? project, string name, string root, string? detail = null,
                               string? storeDirectory = null)
        {
            if (!groups.TryGetValue(key, out var group))
            {
                var existing            = previous.GetValueOrDefault(key);
                var groupStoreDirectory = storeDirectory ?? _storeDirectory;
                if (existing is not null && existing.Project is null == project is null &&
                    SameSidebarStore(existing.StoreDirectory, groupStoreDirectory))
                {
                    group = existing;
                }
                else
                {
                    group = new ProjectViewModel(key, project, name, root,
                                                 NewProjectSessionAsync,
                                                 exception => Notice = exception.Message);
                    group.StoreDirectory = groupStoreDirectory;
                    var preferences = StoreSidebarPreferences(groupStoreDirectory);
                    group.IsExpanded = !preferences.CollapsedGroupKeys.Contains(SidebarGroupExpansionKey(key),
                                                                                    StringComparer.Ordinal);
                }

                // 本轮首次遇到该分组时清一次成员；同组逐会话追加成员时不得再次清空。
                group.Sessions.Clear();
                groups.Add(key, group);
            }

            if (project is not null) group.Update(project);
            else group.RefreshDisplay(name, detail);
            return group;
        }

        // 一次建立项目 ID 索引：分组注册与会话归属查找共用，会话循环不再逐会话线性扫描目录册。
        var projectsById = new Dictionary<string, Project>(StringComparer.Ordinal);
        foreach (var project in _projects) projectsById.TryAdd(project.Id, project);
        foreach (var project in _projects)
            Group(project.Id, project, project.Name, project.PrimaryRoot, storeDirectory : _storeDirectory);

        // 正式会话行进入所属项目分组；空白新对话页不属于 Sessions，不出现在侧栏投影中。
        foreach (var session in Sessions.Where(session => !session.IsArchived)
                                        .OrderByDescending(session => session.LastActivityUtc))
        {
            var sessionStore = session.StoreDirectory;
            var sameStore = sessionStore is null ||
                            (_storeDirectory is { } activeStoreDirectory &&
                             ProjectRoots.AreEqual(sessionStore, activeStoreDirectory));
            var      isNoProject = session.ProjectAssignmentState == SessionProjectAssignmentState.NoProject;
            Project? project     = null;
            if (sameStore && !isNoProject && session.ProjectId is { } boundProjectId)
                project = projectsById.GetValueOrDefault(boundProjectId);
            // 未归属的存储行构造即带 store，且根目录固定后不可再清空（见 SessionViewModel.WorkspaceRoot 守卫）；
            // 分组键纳入 store，两个 store 的同一执行目录不得并成一组，整理明细才能按 store 精确定位。
            string key;
            if (isNoProject)
                key = NoProjectGroupKey(sessionStore);
            else if (session.ProjectId is { } projectId)
                key = sessionStore is { } otherStore && !sameStore
                    ? OtherStoreProjectGroupKey(otherStore, projectId)
                    : projectId;
            else if (session.StoreDirectory is { } pendingStore)
                key = PendingGroupKey(pendingStore, session.WorkspaceRoot);
            else
                key = "pending:" + ProjectRoots.Normalize(session.WorkspaceRoot);
            string  name;
            string? detail = null;
            if (isNoProject)
            {
                name = "最近";
            }
            else if (project is not null)
            {
                name = project.Name;
            }
            else if (session.ProjectId is not null)
            {
                name = "项目信息缺失";
            }
            else
            {
                // 旧日志未归属：整理结束时仍有待处理或失败项才标记“整理未完成”，否则只按目录展示。
                detail = OrganizeIssueFor(session.StoreDirectory, session.WorkspaceRoot);
                name   = detail is null ? session.WorkspaceLabel : "整理未完成 · " + session.WorkspaceLabel;
            }

            Group(key, project, name, isNoProject ? string.Empty : session.WorkspaceRoot, detail, sessionStore)
               .Sessions.Add(session);
        }

        var activityOrder    = groups.Values.OrderByDescending(ProjectGroupActivity).ToArray();
        var projectGroups    = activityOrder.Where(group => group.Project is not null).ToArray();
        var storePreferences = StoreSidebarPreferences(_storeDirectory);
        var manualOrder      = storePreferences.ManualProjectOrder.ToList();
        var pinnedOrder      = PinsForStore(_storeDirectory).PinnedProjectIds.ToList();
        var pinnedIds        = pinnedOrder.ToHashSet(StringComparer.Ordinal);
        var newProjectKeys = projectGroups.Where(group => !group.IsDeleting)
                                          .Select(group => group.Key)
                                          .Where(key => !manualOrder.Contains(key, StringComparer.Ordinal)).ToArray();
        var orderChanged = newProjectKeys.Length > 0;
        manualOrder.InsertRange(0, newProjectKeys);
        var completeProjectOrderMigration = SidebarPreferencesReady                                &&
                                            _storeDirectory is not null                            &&
                                            projectGroups.Length > 0                               &&
                                            !storePreferences.ManualProjectOrderMigrationCompleted &&
                                            manualOrder.Count == projectGroups.Length              &&
                                            manualOrder.All(key => projectGroups.Any(group => group.Key == key));
        if (completeProjectOrderMigration) orderChanged |= PromoteLatestCreatedProject(manualOrder, projectGroups);

        // 置顶项目保留手动顺序中的位置，取消置顶后回到原位；新项目则补到普通分类顶部。

        if ((orderChanged || completeProjectOrderMigration) && _storeDirectory is { } currentStore &&
            SidebarPreferencesReady)
            UpdateStoreSidebarPreferences(currentStore, preferences => preferences with
            {
                ManualProjectOrder = manualOrder,
                ManualProjectOrderMigrationCompleted = true
            });

        var sessionOrderChanged = SeedManualSessionOrders();
        if (orderChanged || completeProjectOrderMigration || sessionOrderChanged) QueueSidebarPreferencesSave();

        foreach (var group in groups.Values)
            group.SetSidebarState(pinnedIds.Contains(group.Key), IsProjectMode && IsManualProjectOrder);

        var orderedPinned = projectGroups.Where(group => pinnedIds.Contains(group.Key))
                                         .OrderBy(group => pinnedOrder.IndexOf(group.Key));
        var orderedProjects = IsManualProjectOrder
            ? projectGroups.Where(group => !pinnedIds.Contains(group.Key))
                           .OrderBy(group => manualOrder.IndexOf(group.Key))
            : activityOrder.Where(group => group.Project is not null && !pinnedIds.Contains(group.Key));

        Projects.Clear();
        foreach (var group in orderedPinned.Concat(orderedProjects)
                                           .Concat(activityOrder.Where(group => group.Project is null &&
                                                                           !group.IsNoProjectGroup))
                                           .Concat(activityOrder.Where(group => group.IsNoProjectGroup)))
            Projects.Add(group);
        IReadOnlyList<ProjectViewModel> projectOptions = Projects.Where(group => group.CanEdit).ToArray();
        if (_cachedProjectOptions is { } cached  &&
            cached.Count == projectOptions.Count &&
            cached.SequenceEqual(projectOptions))
            // 内容逐引用相同：复用缓存实例，SessionViewModel.ProjectOptions 的引用相等短路使菜单不再逐页重建。
            projectOptions = cached;
        else
            _cachedProjectOptions = projectOptions;

        foreach (var page in TargetLabelPages().Distinct()) page.ProjectOptions = projectOptions;
        RebuildSidebarRows();
        NotifyChangedWorkspaceTargetLabels(labels);
    }

    /// <summary>
    ///     <para>取得项目分组的最近会话活动时间；空项目回退到创建时间。</para>
    ///     <para>Gets a project's latest session activity, falling back to its creation time when empty.</para>
    /// </summary>
    private static DateTimeOffset ProjectGroupActivity(ProjectViewModel group) =>
        group.Sessions.Select(session => session.LastActivityUtc)
             .DefaultIfEmpty(group.Project?.CreatedUtc ?? DateTimeOffset.MinValue).Max();

    /// <summary>
    ///     <para>一次性将旧顺序中唯一最新创建且不在顶部的项目移到顶部。</para>
    ///     <para>Promotes the uniquely newest project when it is not first in a legacy order.</para>
    /// </summary>
    private static bool PromoteLatestCreatedProject(
        List<string> manualOrder, IReadOnlyCollection<ProjectViewModel> groups)
    {
        var latestCreatedUtc = groups.Max(group => group.Project!.CreatedUtc);
        var latestProjects   = groups.Where(group => group.Project!.CreatedUtc == latestCreatedUtc).ToArray();
        if (latestProjects.Length != 1) return false;

        var latestProjectKey = latestProjects[0].Key;
        var index            = manualOrder.IndexOf(latestProjectKey);
        if (index <= 0) return false;

        manualOrder.RemoveAt(index);
        manualOrder.Insert(0, latestProjectKey);
        return true;
    }

    /// <summary>
    ///     <para>将运行中的会话按最近活动排列在前，其余会话按对应 store 的手动顺序排列。</para>
    ///     <para>Orders running sessions first by recent activity and idle sessions by their store's manual order.</para>
    /// </summary>
    private SessionViewModel[] OrderSidebarSessions(IEnumerable<SessionViewModel> sessions)
    {
        var items = sessions.ToArray();
        if (items.Length < 2) return items;

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var orders       = new Dictionary<string, Dictionary<string, int>>(pathComparer);
        foreach (var session in items)
        {
            if (session.StoreDirectory is not { } store) continue;
            var key = PinStoreKey(store);
            if (orders.ContainsKey(key)) continue;

            var manualOrder = StoreSidebarPreferences(key).ManualSessionOrder;
            orders.Add(key, manualOrder.Select((id, index) => (id, index))
                                       .ToDictionary(entry => entry.id, entry => entry.index,
                                                     StringComparer.Ordinal));
        }

        int ManualIndex(SessionViewModel session)
        {
            if (session.Id is { } id                                  && session.StoreDirectory is { } store &&
                orders.TryGetValue(PinStoreKey(store), out var order) && order.TryGetValue(id, out var index))
                return index;
            return int.MaxValue;
        }

        var running = items.Where(session => session.IsRunning)
                           .OrderByDescending(session => session.LastActivityUtc)
                           .ThenBy(ManualIndex);
        var idle = items.Where(session => !session.IsRunning)
                        .OrderBy(ManualIndex)
                        .ThenByDescending(session => session.LastActivityUtc);
        return running.Concat(idle).ToArray();
    }

    /// <summary>
    ///     <para>为尚未记录的历史会话补齐手动顺序；后续分页加载的会话追加在已保存顺序之后。</para>
    ///     <para>Adds unrecorded history sessions to the manual order; sessions loaded by later pages follow saved entries.</para>
    /// </summary>
    private bool SeedManualSessionOrders()
    {
        if (!SidebarPreferencesReady) return false;

        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var changed      = false;
        var sessionsByStore = Sessions.Where(session => session.Id is not null && session.StoreDirectory is not null)
                                      .GroupBy(session => PinStoreKey(session.StoreDirectory!), pathComparer);
        foreach (var storeSessions in sessionsByStore)
        {
            var order = StoreSidebarPreferences(storeSessions.Key).ManualSessionOrder.ToList();
            var known = order.ToHashSet(StringComparer.Ordinal);
            var missing = storeSessions.OrderByDescending(session => session.LastActivityUtc)
                                       .Select(session => session.Id!)
                                       .Where(known.Add)
                                       .Take(Math.Max(0, SidebarPreferencesStore.MaxSessionOrderEntries - order.Count))
                                       .ToArray();
            if (missing.Length == 0) continue;

            if (order.Count == 0) order.InsertRange(0, missing);
            else order.AddRange(missing);
            UpdateStoreSidebarPreferences(storeSessions.Key, preferences => preferences with
            {
                ManualSessionOrder = order
            });
            changed = true;
        }

        return changed;
    }

    /// <summary>
    ///     <para>为其他 store 的已归属会话生成展示键，避免同 ID 合并到当前目录册项目。</para>
    ///     <para>Creates a display key for sessions in another store so matching ids do not join the current catalog project.</para>
    /// </summary>
    private static string OtherStoreProjectGroupKey(string store, string projectId)
    {
        var normalizedStore = ProjectRoots.Normalize(store);
        return "missing-project:" + normalizedStore.Length + ":" + normalizedStore + projectId;
    }

    /// <summary>
    ///     <para>枚举显示目标标签的正式页、失败草稿、空白页与选中页；由调用方按引用去重。</para>
    ///     <para>
    ///         Enumerates formal pages, failed drafts, the blank page and the selected page showing target labels;
    ///         callers deduplicate by reference.
    ///     </para>
    /// </summary>
    private IEnumerable<SessionViewModel> TargetLabelPages()
    {
        foreach (var session in Sessions) yield return session;
        foreach (var draft in _failedDrafts) yield return draft;
        if (_newConversation is { } page) yield return page;
        if (SelectedSession is { } selected) yield return selected;
    }

    /// <summary>
    ///     <para>记录各页面当前目标标签，供分组投影变化后按值差集通知。</para>
    ///     <para>Captures current page target labels so projection changes notify only pages whose labels differ.</para>
    /// </summary>
    private Dictionary<SessionViewModel, string> CaptureWorkspaceTargetLabels() =>
        TargetLabelPages().Distinct().ToDictionary(page => page, page => page.WorkspaceTargetLabel);

    /// <summary>
    ///     <para>
    ///         分组名变化后仅向标签值实际变化的页面发出通知；页面不订阅分组实例，避免泄漏订阅和无意义刷新。
    ///     </para>
    ///     <para>
    ///         Notifies only pages whose target label actually changed after group renames; pages never subscribe to group
    ///         instances, avoiding leaked subscriptions and meaningless refreshes.
    ///     </para>
    /// </summary>
    private void NotifyChangedWorkspaceTargetLabels(Dictionary<SessionViewModel, string> previous)
    {
        foreach (var (page, label) in previous)
            if (page.WorkspaceTargetLabel != label)
                page.NotifyWorkspaceTargetLabelChanged();
    }

    /// <summary>
    ///     <para>未归属旧日志的侧栏分组键：长度前缀分隔 store 与执行目录，避免路径中合法分隔符字符造成键歧义。</para>
    ///     <para>
    ///         Group key for unbound legacy rows: a length prefix separates store and root so legal path separators cannot
    ///         collide.
    ///     </para>
    /// </summary>
    private static string PendingGroupKey(string store, string workspaceRoot)
    {
        var normalizedStore = ProjectRoots.Normalize(store);
        return "pending:" + normalizedStore.Length + ":" + normalizedStore + ProjectRoots.Normalize(workspaceRoot);
    }

    private static string NoProjectGroupKey(string? storeDirectory)
    {
        if (storeDirectory is null) return "no-project:unknown-store";
        var normalizedStore = ProjectRoots.Normalize(storeDirectory);
        return "no-project:" + normalizedStore.Length + ":" + normalizedStore;
    }

    /// <summary>
    ///     <para>按会话 store 与执行目录查询整理未完成明细；行 store 与结果所属 store 不一致时不得串用原因。</para>
    ///     <para>
    ///         Looks up unfinished-organize detail by the row's store and root; a store mismatch never reuses another
    ///         store's reason.
    ///     </para>
    /// </summary>
    private string? OrganizeIssueFor(string? sessionStore, string workspaceRoot)
    {
        if (_organizeIssues is null     || _organizeIssuesStore is not { } issueStore ||
            sessionStore is not { } row || !ProjectRoots.AreEqual(row, issueStore))
            return null;
        var normalized = ProjectRoots.Normalize(workspaceRoot);
        foreach (var issue in _organizeIssues)
            if (ProjectRoots.AreEqual(issue.Root, normalized))
                return issue.Detail;
        return null;
    }

    /// <summary>
    ///     <para>
    ///         选中变化只向新旧选中所在分组发出 IsCurrent 通知，不重建侧栏行：SidebarRows 的行序列不读取
    ///         选中状态，行高亮由行模板的 IsSelected 绑定驱动、组头高亮由 IsCurrent 驱动；整表重建在
    ///         选中变化时本就产生零集合通知，只余 O(N log N) 重排与无条件分组通知。
    ///     </para>
    ///     <para>
    ///         A selection change only raises IsCurrent on the groups holding the old or new selection instead of
    ///         rebuilding sidebar rows: the row sequence never reads selection state, row highlights come from the
    ///         template's IsSelected binding, and header highlights from IsCurrent; a full rebuild already produces
    ///         zero collection notifications for a selection change, leaving only an O(N log N) reorder and
    ///         unconditional group notifications.
    ///     </para>
    /// </summary>
    private void NotifySelectionHighlight(SessionViewModel? previous, SessionViewModel selected)
    {
        // 让位语义：旧选中所在分组失去高亮，新选中所在分组取得高亮，其余分组的通知可以完全省略。
        foreach (var group in Projects)
            if ((previous is { } previousRow && group.Sessions.Contains(previousRow)) ||
                group.Sessions.Contains(selected))
                group.NotifyCurrentChanged();
    }

    /// <summary>
    ///     <para>按视图模式生成侧栏分类；项目模式下无项目会话直接列在底部“最近”分类。</para>
    ///     <para>
    ///         Builds sidebar categories for the active view; unpinned no-project sessions appear directly under a bottom
    ///         Recent category in project mode.
    ///     </para>
    /// </summary>
    private void RebuildSidebarRows()
    {
        foreach (var session in Sessions)
        {
            session.SetPinned(session.CanPin && IsSessionPinned(session));
            if (session.StoreDirectory is not { } archiveStore ||
                !_archiveStateUnavailableStores.Contains(PinStoreKey(archiveStore)))
                session.SetArchived(IsSessionArchived(session));
        }

        foreach (var group in Projects) group.NotifyCurrentChanged();
        var rows = new List<object>(Projects.Count + Sessions.Count);
        if (IsArchiveView)
        {
            var archived = Sessions.Where(session => session.IsArchived)
                                   .OrderByDescending(session => session.LastActivityUtc).ToArray();
            if (archived.Length > 0)
            {
                AppendCategoryRows(rows, ArchivedCategory, archived);
            }
            else
            {
                var stateUnknown = _storeDirectory is { } store &&
                                   _archiveStateUnavailableStores.Contains(PinStoreKey(store));
                rows.Add(stateUnknown ? ArchiveUnavailableCategory : ArchivedEmptyCategory);
            }
        }
        else if (IsProjectMode)
        {
            var pinnedProjects = Projects.Where(group => group.IsPinned).ToArray();
            var regularProjects = Projects.Where(group => !group.IsPinned && !group.IsNoProjectGroup &&
                                                          (group.CanEdit || group.IsDeleting ||
                                                           group.Sessions.Any(session => !session.IsPinned)))
                                          .ToArray();
            var pinnedSessions = OrderSidebarSessions(Sessions.Where(session => !session.IsArchived &&
                                                                         session.IsPinned));
            if (pinnedProjects.Length > 0 || pinnedSessions.Length > 0)
            {
                var pinnedRows = new List<object>(pinnedProjects.Length + pinnedSessions.Length);
                AppendProjectRows(pinnedRows, pinnedProjects);
                pinnedRows.AddRange(pinnedSessions);
                AppendCategoryRows(rows, PinnedCategory, pinnedRows);
            }

            if (regularProjects.Length > 0)
            {
                var projectRows = new List<object>(regularProjects.Length);
                AppendProjectRows(projectRows, regularProjects);
                AppendCategoryRows(rows, ProjectsCategory, projectRows);
            }

            var recentSessions = OrderSidebarSessions(Sessions.Where(session => !session.IsArchived &&
                                                                         !session.IsPinned          &&
                                                                         session.ProjectAssignmentState ==
                                                                         SessionProjectAssignmentState.NoProject));
            if (recentSessions.Length > 0)
                AppendCategoryRows(rows, RecentCategory, recentSessions);
        }
        else
        {
            var pinned  = OrderSidebarSessions(Sessions.Where(session => !session.IsArchived && session.IsPinned));
            var regular = OrderSidebarSessions(Sessions.Where(session => !session.IsArchived && !session.IsPinned));
            if (pinned.Length > 0)
                AppendCategoryRows(rows, PinnedCategory, pinned);

            if (regular.Length > 0)
                AppendCategoryRows(rows, SessionsCategory, regular);
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
    ///     <para>按展开态与分页数量铺平项目头、未置顶会话和展开入口。</para>
    ///     <para>Flattens project headers, unpinned sessions and show-more entries by expansion and paging state.</para>
    /// </summary>
    private void AppendProjectRows(List<object> rows, IEnumerable<ProjectViewModel> groups)
    {
        foreach (var group in groups)
        {
            rows.Add(group);
            if (!group.IsExpanded) continue;
            var sessions = OrderSidebarSessions(group.Sessions.Where(session => !session.IsPinned));
            rows.AddRange(sessions.Take(group.VisibleSessionCount));
            if (sessions.Length > group.VisibleSessionCount) rows.Add(group.MoreSessionsRow);
        }
    }

    /// <summary>
    ///     <para>添加分类标题，并仅在其展开时添加分类内容。</para>
    ///     <para>Adds a category header and its contents only while the category is expanded.</para>
    /// </summary>
    private static void AppendCategoryRows(List<object>        rows, SidebarCategoryViewModel category,
                                           IEnumerable<object> categoryRows)
    {
        rows.Add(category);
        if (category.IsExpanded) rows.AddRange(categoryRows);
    }

    /// <summary>
    ///     <para>按会话所属 store 查询其置顶状态。</para>
    ///     <para>Looks up the pinned state in the session's owning store.</para>
    /// </summary>
    private bool IsSessionPinned(SessionViewModel session)
    {
        if (session.Id is not { } id || session.StoreDirectory is not { } store) return false;
        return PinnedSessionIdsForStore(store).Contains(id);
    }

    /// <summary>
    ///     <para>显式打开目录并保存项目；只创建或切换草稿，不建立会话日志。</para>
    ///     <para>Explicitly opens a directory and saves its project, creating or moving a draft without a session log.</para>
    /// </summary>
    /// <remarks>
    ///     <para>store 切换时作废目录册同步基线；末尾确保跨进程目录册同步循环在运行。</para>
    ///     <para>
    ///         A store switch invalidates the catalog-sync baseline; the end of the method ensures the
    ///         cross-process catalog sync loop is running.
    ///     </para>
    /// </remarks>
    public async Task OpenProjectAsync(string root)
    {
        if (_closing || IsProjectDeletionRunning) return;
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        // 操作开始即冻结页面和意图；期间建立会话或有更新选择时，不能另取新页落入迟到结果。
        var draft         = SelectedSession is { CanChooseWorkspace: true } selected ? selected : ConversationDraft();
        var revision      = draft.BeginTargetIntent();
        var storeRevision = _storeRevision;
        var draftTargets  = CaptureDraftTargets();
        try
        {
            var directory = await ResolveProtectedDirectoryAsync(root, _lifetime.Token);
            if (!CanApplyTarget(draft, revision) ||
                (storeRevision != _storeRevision && !IsApplicableStore(directory, storeRevision))) return;
            var project = await SessionProjectBinding.GetProjectAsync(directory, root, _lifetime.Token);
            if (!CanApplyTarget(draft, revision) ||
                (storeRevision != _storeRevision && !IsApplicableStore(directory, storeRevision))) return;
            var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token);
            if (!CanApplyTarget(draft, revision) ||
                (storeRevision != _storeRevision && !IsApplicableStore(directory, storeRevision))) return;

            var changed = _storeDirectory is not null && !ProjectRoots.AreEqual(_storeDirectory, directory);
            if (changed)
            {
                _organizeSource?.Cancel();
                _initializationCursor = null;
                ResetOrganizeDisplay();
                _catalogMonitor.Reset();
                // 上一 store 的失败提示随切换作废：新 store 的首个故障须重新提示。
                ResetProjectSyncFailureState();
            }

            if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory)) _storeRevision++;
            _storeDirectory = directory;
            _projects       = catalog.Projects;
            storeRevision   = _storeRevision;
            RebuildProjects();
            if (changed)
            {
                await StopOrganizeMigrationAsync();
                if (!CanApplyTarget(draft, revision) || !IsApplicableStore(directory, storeRevision)) return;
            }

            await draft.SelectProjectAsync(project.Id, project.PrimaryRoot, directory);
            if (!CanApplyTarget(draft, revision) || !IsApplicableStore(directory, storeRevision)) return;
            SelectedSession = draft;
            if (changed)
            {
                await RebindUnboundDraftsAsync(directory, draftTargets);
                if (IsApplicableStore(directory, storeRevision)) _ = StartOrganizeRound(directory);
            }

            // 不经 InitializeAsync 的 store 重绑路径也确保目录册同步在运行；循环幂等。
            StartProjectCatalogSyncLoop();
        }
        finally
        {
            draft.CompleteTargetIntent(revision);
        }
    }

    /// <summary>
    ///     <para>项目组头「+」：把空白新对话页切到该项目并保留输入；不新建页、不写目录册。</para>
    ///     <para>
    ///         Project group "+": moves the blank new-conversation page onto the project keeping its input; it creates no
    ///         page and writes no catalog.
    ///     </para>
    /// </summary>
    public async Task NewProjectSessionAsync(ProjectViewModel group)
    {
        if (_closing || IsProjectDeletionRunning || group.Project is not { IsDeleting: false } project) return;
        var page          = ConversationDraft();
        var revision      = page.BeginTargetIntent();
        var storeRevision = _storeRevision;
        try
        {
            var directory = await ApplyProjectChoiceAsync(page, project.PrimaryRoot, revision);
            if (directory is null               ||
                !CanApplyTarget(page, revision) ||
                !IsApplicableStore(directory, storeRevision)) return;
            SelectedSession = page;
        }
        finally
        {
            page.CompleteTargetIntent(revision);
        }
    }

    private async Task<string?> ApplyProjectChoiceAsync(SessionViewModel page, string root, int revision)
    {
        var storeRevision = _storeRevision;
        var directory     = await ResolveProtectedDirectoryAsync(root, _lifetime.Token);
        if (!CanApplyTarget(page, revision) || !IsApplicableStore(directory, storeRevision)) return null;
        // 菜单和组头只读取当前 store 的目录册，不能复制旧 store 分组的项目 ID，也不预创建项目。
        var catalog = await ProjectStore.LoadAsync(ProjectStore.FilePath(directory), _lifetime.Token);
        if (!CanApplyTarget(page, revision) || !IsApplicableStore(directory, storeRevision)) return null;
        var project = catalog.Projects.FirstOrDefault(item =>
                                                          item.Roots.Any(projectRoot =>
                                                                             ProjectRoots.AreEqual(projectRoot.Path,
                                                                                 root)));
        await page.SelectProjectAsync(project?.Id, root, directory);
        return directory;
    }

    /// <summary>
    ///     <para>按显示修订号保存项目名称；冲突不覆盖其他保存。</para>
    ///     <para>Saves a project name against the displayed revision without overwriting conflicting saves.</para>
    /// </summary>
    public async Task RenameProjectAsync(ProjectViewModel group)
    {
        if (_closing || IsProjectDeletionRunning || group.Project is not { } project) return;
        if (project.IsDeleting) throw new InvalidOperationException("项目正在删除，不能重命名。");
        var directory = await ResolveProtectedDirectoryAsync(project.PrimaryRoot, _lifetime.Token);
        var renamed = await ProjectStore.RenameAsync(ProjectStore.FilePath(directory), project.Id, group.EditedName,
                                                     project.Revision, cancellationToken : _lifetime.Token);
        // 改名改变组名：按标签值差集通知相关页面；值未变的页面不刷新。
        var labels = CaptureWorkspaceTargetLabels();
        _projects = _projects.Select(item => item.Id == renamed.Id ? renamed : item).ToArray();
        group.Update(renamed);
        NotifyChangedWorkspaceTargetLabels(labels);
    }

    /// <summary>
    ///     <para>按通用设置打开项目删除确认，或直接开始删除；中断项目复用同一入口继续处理。</para>
    ///     <para>
    ///         Shows project-deletion confirmation when enabled or starts deletion directly; interrupted work resumes
    ///         through the same entry point.
    ///     </para>
    /// </summary>
    private void OpenProjectDeletion(ProjectViewModel? group)
    {
        if (IsProjectDeletionRunning || group is not { CanShowActions: true }) return;
        _projectDeletionTarget = group;
        _projectDeletionDialogVersion++;
        ProjectDeletionErrorText = null;
        IsProjectDeletionOpen    = Settings.General.ShowProjectDeletionPrompt;
        NotifyProjectDeletionProperties();
        if (!IsProjectDeletionOpen) _ = ConfirmProjectDeletionAsync();
    }

    /// <summary>
    ///     <para>标记项目删除中、解除会话归属、清理侧栏状态，最后从目录册移除项目；中断后可从菜单继续。</para>
    ///     <para>
    ///         Marks a project as deleting, unassigns its sessions, clears sidebar state, and then removes it from the
    ///         catalog; interrupted work can resume from the project menu.
    ///     </para>
    /// </summary>
    private async Task ConfirmProjectDeletionAsync()
    {
        if (!CanConfirmProjectDeletion || _projectDeletionTarget?.Project is not { } project) return;

        var group          = _projectDeletionTarget;
        var storeDirectory = group.StoreDirectory ?? _storeDirectory;
        if (storeDirectory is null)
        {
            ReportProjectDeletionIssue("无法确定此项目所属的会话存储目录。");
            return;
        }

        storeDirectory = Path.GetFullPath(storeDirectory);
        var projectSessions = Sessions.Where(session => session.ProjectId == project.Id &&
                                                        SameSidebarStore(session.StoreDirectory,
                                                                         storeDirectory))
                                      .ToArray();
        if (projectSessions.Any(session => session.IsRunning))
        {
            ReportProjectDeletionIssue("该项目仍有运行中的会话。请等待会话结束后再继续删除。");
            return;
        }

        var drafts = ProjectDraftPages().Where(page => page.ProjectId == project.Id &&
                                                       (page.StoreDirectory is null ||
                                                        SameSidebarStore(page.StoreDirectory, storeDirectory)))
                                        .ToArray();
        if (drafts.Any(page => !page.CanChooseWorkspace))
        {
            ReportProjectDeletionIssue("空白会话正在切换或使用此项目。请等待操作完成后再继续删除。");
            return;
        }

        var dialogVersion = _projectDeletionDialogVersion;
        IsProjectDeletionRunning = true;
        NotifyProjectDeletionProperties();
        try
        {
            var protectedStoreDirectory = await ResolveProtectedDirectoryAsync(project.PrimaryRoot, _lifetime.Token);
            if (!SameSidebarStore(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before project deletion; reload the project and retry.");

            storeDirectory = protectedStoreDirectory;
            var catalogPath = ProjectStore.FilePath(storeDirectory);
            var deleting = await ProjectStore.BeginDeletionAsync(
                                                                 catalogPath, project.Id, project.Revision,
                                                                 cancellationToken : _lifetime.Token);
            group.Update(deleting);
            if (SameSidebarStore(_storeDirectory, storeDirectory))
            {
                _projects = _projects.Select(item => item.Id == deleting.Id ? deleting : item).ToArray();
                RebuildProjects();
            }

            NotifyProjectDeletionProperties();

            foreach (var draft in drafts)
                await draft.SelectProjectAsync(null, draft.WorkspaceRoot, storeDirectory);

            foreach (var session in projectSessions.Where(session => session.HasOpenRuntime))
                await session.UnassignProjectAsync(project.Id, _lifetime.Token);

            var result = await SessionProjectUnbinder.UnassignAsync(storeDirectory, project.Id, _lifetime.Token);
            ApplyProjectUnassignmentResult(storeDirectory, project.Id, result);
            // 解绑写入未接索引更新器：有成功解绑时对账把已提交事实追平，失败不回滚删除。
            if (result.Outcomes.Any(outcome => outcome.Status == SessionProjectUnassignmentStatus.Unassigned))
                await TryReconcileIndexAsync(storeDirectory);
            if (SameSidebarStore(_storeDirectory, storeDirectory)) RebuildProjects();
            if (result.Cancelled && _lifetime.IsCancellationRequested)
                throw new OperationCanceledException(_lifetime.Token);
            if (!result.IsComplete)
                throw new InvalidOperationException(DescribeIncompleteProjectUnassignment(result));

            await RemoveProjectSidebarStateAsync(storeDirectory, project.Id, _lifetime.Token);
            await ProjectStore.CompleteDeletionAsync(catalogPath, project.Id, deleting.Revision, _lifetime.Token);

            if (SameSidebarStore(_storeDirectory, storeDirectory))
            {
                _projects = _projects.Where(item => item.Id != project.Id).ToArray();
                RebuildProjects();
            }

            if (_projectDeletionDialogVersion == dialogVersion) CloseProjectDeletion();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 关闭时停止本轮；目录册中的删除标记使下次启动可以继续。
        }
        catch (Exception exception)
        {
            if (_projectDeletionDialogVersion == dialogVersion && IsProjectDeletionOpen)
                ProjectDeletionErrorText = _factory.Redact(exception.Message);
            else
                Notice = _factory.Redact(exception.Message);
        }
        finally
        {
            IsProjectDeletionRunning = false;
            NotifyProjectDeletionProperties();
            if (!_closing && SameSidebarStore(_storeDirectory, storeDirectory)) RebuildProjects();
        }
    }

    /// <summary>
    ///     <para>将删除前置条件错误放入确认面板，或在不显示确认面板时提示到主窗口。</para>
    ///     <para>
    ///         Shows deletion precondition errors in the confirmation panel, or in the main window when confirmation is
    ///         disabled.
    ///     </para>
    /// </summary>
    private void ReportProjectDeletionIssue(string message)
    {
        if (IsProjectDeletionOpen) ProjectDeletionErrorText = message;
        else Notice                                         = message;
    }

    /// <summary>
    ///     <para>返回当前可选项目之外、仍可改目标的空白页和失败草稿。</para>
    ///     <para>Returns blank pages and failed drafts that can still change their project target.</para>
    /// </summary>
    private IEnumerable<SessionViewModel> ProjectDraftPages() =>
        new[] { _newConversation, SelectedSession }.Concat(_failedDrafts)
                                                   .OfType<SessionViewModel>()
                                                   .Where(page => page.Id is null)
                                                   .Distinct();

    /// <summary>
    ///     <para>将已成功解除归属的日志结果同步到现存侧栏行。</para>
    ///     <para>Applies successfully unassigned journal results to existing sidebar rows.</para>
    /// </summary>
    private void ApplyProjectUnassignmentResult(
        string storeDirectory, string projectId, SessionProjectUnassignmentResult result)
    {
        var unassignedIds = result.Outcomes
                                  .Where(outcome => outcome.Status == SessionProjectUnassignmentStatus.Unassigned)
                                  .Select(outcome => outcome.SessionId)
                                  .ToHashSet(StringComparer.Ordinal);
        foreach (var session in Sessions.Where(session => session.ProjectId == projectId &&
                                                          session.Id is { } id && unassignedIds.Contains(id) &&
                                                          SameSidebarStore(session.StoreDirectory, storeDirectory)))
            session.ApplyStoredMetadata(null, SessionProjectAssignmentState.NoProject, session.LastActivityUtc);
    }

    /// <summary>
    ///     <para>汇总占用和损坏日志，提示用户删除仍可重试。</para>
    ///     <para>Summarizes held and damaged journals and explains that project deletion can be retried.</para>
    /// </summary>
    private static string DescribeIncompleteProjectUnassignment(SessionProjectUnassignmentResult result)
    {
        var pending   = result.Outcomes.Count(outcome => outcome.Status == SessionProjectUnassignmentStatus.Pending);
        var failed    = result.Outcomes.Count(outcome => outcome.Status == SessionProjectUnassignmentStatus.Failed);
        var completed = result.Outcomes.Count(outcome => outcome.Status == SessionProjectUnassignmentStatus.Unassigned);
        var details = string.Join("\n", result.Outcomes
                                              .Where(outcome => outcome.Status !=
                                                                SessionProjectUnassignmentStatus.Unassigned)
                                              .Take(5)
                                              .Select(outcome => $"{outcome.SessionId}: {outcome.Detail}"));
        var summary = $"项目删除暂未完成：已解除 {completed} 个会话，{pending} 个会话仍被占用，{failed} 个日志无法处理。关闭占用会话或修复日志后，可再次选择“继续删除”。";
        return details.Length == 0 ? summary : summary + Environment.NewLine + details;
    }

    /// <summary>
    ///     <para>按当前项目所属 store 清理置顶、普通顺序、折叠分组和遗留置顶状态，并可靠保存偏好。</para>
    ///     <para>
    ///         Removes the project's pin, regular order, collapsed-group, and legacy pin references from its store, then
    ///         durably saves preferences.
    ///     </para>
    /// </summary>
    private async Task RemoveProjectSidebarStateAsync(
        string storeDirectory, string projectId, CancellationToken cancellationToken)
    {
        await _pinStateMutationGate.WaitAsync(cancellationToken);
        try
        {
            var pins = await SessionStorePinStore.SetProjectPinnedAsync(
                                                                        storeDirectory, projectId, false,
                                                                        cancellationToken);
            var storeKey = PinStoreKey(storeDirectory);
            _pinStatesByStore[storeKey] = pins;
            _pinStateUnavailableStores.Remove(storeKey);
        }
        finally
        {
            // 置顶快照已直写：整体失效两个 ID 集合缓存，保持与 SetStorePinState 相同的失效纪律。
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            _pinStateMutationGate.Release();
        }

        await EnsureSidebarPreferencesLoadedAsync(cancellationToken);
        if (!_sidebarPreferencesSaveAllowed)
            throw new InvalidOperationException("侧栏偏好文件损坏或无法读取；修复后可继续删除项目。");

        var expansionKey = SidebarGroupExpansionKey(projectId);
        UpdateStoreSidebarPreferences(storeDirectory, preferences => preferences with
        {
            ManualProjectOrder = preferences.ManualProjectOrder
                                            .Where(id => id != projectId)
                                            .ToArray(),
            CollapsedGroupKeys = preferences.CollapsedGroupKeys
                                            .Where(key => key != expansionKey)
                                            .ToArray(),
            LegacyPinnedProjectOrder = preferences.LegacyPinnedProjectOrder
                                                  .Where(id => id != projectId)
                                                  .ToArray()
        });
        QueueSidebarPreferencesSave(true);
        await FlushSidebarPreferencesAsync();
    }

    /// <summary>
    ///     <para>取消项目删除确认；执行中的删除不在界面中途取消。</para>
    ///     <para>Closes project-deletion confirmation; an active deletion is not cancelled mid-operation from the popup.</para>
    /// </summary>
    private void CancelProjectDeletion()
    {
        if (IsProjectDeletionRunning) return;
        CloseProjectDeletion();
    }

    /// <summary>
    ///     <para>关闭项目删除弹窗并清除其目标与错误状态。</para>
    ///     <para>Closes the project-deletion popup and clears its target and error state.</para>
    /// </summary>
    private void CloseProjectDeletion()
    {
        IsProjectDeletionOpen    = false;
        _projectDeletionTarget   = null;
        ProjectDeletionErrorText = null;
        _projectDeletionDialogVersion++;
        NotifyProjectDeletionProperties();
    }

    /// <summary>
    ///     <para>通知删除弹窗绑定的文本与确认可用状态。</para>
    ///     <para>Notifies deletion-popup text and confirmation-availability bindings.</para>
    /// </summary>
    private void NotifyProjectDeletionProperties()
    {
        OnPropertyChanged(nameof(ProjectDeletionTargetName));
        OnPropertyChanged(nameof(ProjectDeletionPrompt));
        OnPropertyChanged(nameof(ProjectDeletionButtonText));
        OnPropertyChanged(nameof(CanConfirmProjectDeletion));
        OnPropertyChanged(nameof(IsProjectDeletionRunning));
        OnPropertyChanged(nameof(HasProjectDeletionError));
    }

    /// <summary>组头「重命名」：以该组为对象打开重命名弹窗并预填当前名称。</summary>
    private void OpenProjectRename(ProjectViewModel? group)
    {
        if (IsProjectDeletionRunning || group is not { CanEdit: true }) return;
        _renameTarget = group;
        // 每次打开都是新的弹窗实例：身份递增，使仍在途的上一次确认结果不落回本弹窗。
        _renameDialogVersion++;
        ProjectRenameDraftText = group.Name;
        ProjectRenameErrorText = null;
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
            ProjectRenameErrorText = null;
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

            ProjectRenameErrorText = _factory.Redact(exception.Message);
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
        IsProjectRenameOpen    = false;
        _renameTarget          = null;
        ProjectRenameErrorText = null;
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
        // 确定性测试注入点：控制解析完成顺序以复现竞态；生产保持为空走真实受保护解析。
        if (ResolveDirectoryOverride is { } resolver) return await resolver(root, token);
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
        string                             directory;
        try
        {
            directory =
                await ResolveProtectedDirectoryAsync(SelectedSession?.WorkspaceRoot ?? Environment.CurrentDirectory,
                                                     source.Token);
            if (!Directory.Exists(directory)) return;
            if (_storeDirectory is null || !ProjectRoots.AreEqual(_storeDirectory, directory))
                _initializationCursor = null;
            // 目录册基线与同步失败提示随生效 store 变化作废，与 PrepareAsync/OpenProjectAsync 的切换写点一致。
            if (_storeDirectory is not null && !ProjectRoots.AreEqual(_storeDirectory, directory))
            {
                _catalogMonitor.Reset();
                ResetProjectSyncFailureState();
            }

            _storeDirectory = directory;
            result = await SessionProjectInitializer.InitializeAsync(directory, _initializationCursor, 100,
                                                                     cancellationToken : source.Token);
            _initializationCursor = result.HasMore ? result.NextCursor : null;
        }
        finally
        {
            EndMigration(source, finished);
        }

        // 显式归属写入未接索引更新器：先对账把已提交事实追平，再按索引刷新列表。
        await TryReconcileIndexAsync(directory);
        // 单批入口保留逐批全库刷新；整轮编排不使用该路径，避免每批重读整个 store。
        await InitializeAsync();
        Notice = $"项目初始化：{result.Outcomes.Count(item => item.Status == SessionProjectBindingStatus.Bound)} 已绑定，"   +
                 $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Pending)} 待处理，" +
                 $"{result.Outcomes.Count(item => item.Status       == SessionProjectBindingStatus.Failed)} 失败；"   +
                 (result.Cancelled ? "已取消，可继续。" : result.HasMore ? "仍有日志，可继续。" : "本轮扫描完成，可重试待处理项。")                +
                 string.Join("", result.Outcomes.Where(item => item.Detail is not null)
                                       .Select(item => $"\n{item.SessionId}: {item.Detail}"));
    }

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
    ///         一轮后台维护：先对账（有效索引下未变日志零正文扫描、首次建库渐进上屏），再定向整理索引标记的
    ///         待初始化日志（索引不可用时回退整轮游标扫描），整理后再对账追平并启动刷新循环；
    ///         所有异常都在本任务内观察转换，调用方持有的返回值只用于等待结束。
    ///     </para>
    ///     <para>
    ///         One background maintenance round: reconcile first (zero body scans for unchanged logs under a valid
    ///         index, progressive first builds), then organize only the indexed pending logs (falling back to the
    ///         full cursor scan without an index), reconcile again to catch up, and start the refresh loop; every
    ///         exception is observed and converted inside this task.
    ///     </para>
    /// </summary>
    private async Task RunOrganizeRoundAsync(CancellationTokenSource source, string store, int generation,
                                             TaskCompletionSource    finished)
    {
        string? failureSummary = null;
        var     merged         = new Dictionary<string, SessionProjectBindingOutcome>(StringComparer.Ordinal);
        try
        {
            // 写入前复用与显式入口一致的完整保护校验：轻量准备只解析路径，不构成创建目录册或追加归属的授权；
            // 沙箱附加写根等边界只有这里与显式入口检查，Core 仅校验各日志自己的执行根。
            var protectedStore = await ResolveProtectedDirectoryAsync(
                                                                      SelectedSession?.WorkspaceRoot ??
                                                                      Environment.CurrentDirectory, source.Token);
            if (!ProjectRoots.AreEqual(protectedStore, store))
            {
                // 配置在本轮启动后变化（含 trim 后与轻量解析不一致）：不写捕获的 store，按未完成报告而非静默成功；
                // 迟到防护保证只有最新代次且 store 未变的轮次才更新共享展示，不覆盖新轮的结果。
                if (IsCurrentStore(store, generation))
                    OrganizeNotice = "历史整理未完成：会话存储解析结果与生效目录不一致，本轮未写入。";
            }
            // 空 store／缺失目录正常结束：不为扫描创建目录、项目、日志、锁文件或索引数据库。
            else if (Directory.Exists(store))
            {
                // 先对账：有效索引下未变日志零正文扫描，首次建库在此完成并渐进上屏。
                var indexReady = await ReconcileForOrganizeAsync(store, generation, source.Token)
                   .ConfigureAwait(true);
                var pending = indexReady
                    ? await CollectPendingSessionIdsAsync(store, source.Token).ConfigureAwait(true)
                    : null;
                if (pending is not null)
                {
                    // 定向整理：只处理索引标记为待初始化的日志；批内扫描与归属追加放工作线程，
                    // 不占用 UI 线程。占用与失败按既有事实合并，取消即停。
                    foreach (var chunk in pending.Chunk(OrganizeBatchSize))
                    {
                        var result = await Task.Run(() => SessionProjectInitializer.InitializeSessionsAsync(
                                                     store, chunk, null, source.Token),
                                                    source.Token).ConfigureAwait(true);
                        foreach (var outcome in result.Outcomes) merged[outcome.SessionId] = outcome;
                        if (result.Cancelled) break;
                    }
                }
                else
                {
                    // 索引不可用时的整轮回退：逐批游标扫描全部日志，行为与旧路径一致。
                    var cursor = (string?)null;
                    while (!_closing)
                    {
                        var result = await Task.Run(() => SessionProjectInitializer.InitializeAsync(
                                                     store, cursor, OrganizeBatchSize,
                                                     cancellationToken : source.Token),
                                                    source.Token).ConfigureAwait(true);
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
                }

                if (IsCurrentStore(store, generation)) PublishOrganizeOutcome(merged.Values);
                if (indexReady && IsCurrentStore(store, generation))
                    try
                    {
                        // 归属追加与解绑经旧 writer 边界写入，未接索引更新器：整理后对账把已提交事实追平。
                        await RunIndexReconcileAsync(store, source.Token).ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is SessionIndexException or IOException
                                                       or UnauthorizedAccessException)
                    {
                        Trace.TraceError("Post-organize index reconciliation for '{0}' failed: {1}", store,
                                         exception.Message);
                    }

                if (indexReady && !_closing && IsCurrentStore(store, generation)) StartIndexRefreshLoop(store);
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
                failureSummary       = _factory.Redact(exception.Message);
                OrganizeNotice       = $"历史整理未完成：{failureSummary}";
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
    ///     <para>
    ///         Groups pending or failed facts into per-directory unfinished details; facts without a locatable root stay in
    ///         the summary only.
    ///     </para>
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

    /// <summary>一条整理未完成明细：按目录定位的原因摘要，仅用于侧栏展示。</summary>
    private sealed record OrganizeIssue(string Root, string Detail);
}
