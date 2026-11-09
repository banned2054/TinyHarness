using System.Collections.ObjectModel;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class SessionViewModel
{
    private readonly Dictionary<ComposerMenuOption, string> _projectMenuProjectIds = [];
    private          ModelChoice?                           _defaultModel;
    private          long                                   _defaultModelRevision;
    private          bool                                   _isModelMenuOpen;
    private          bool                                   _isPermissionMenuOpen;
    private          bool                                   _isWorkspaceMenuOpen;
    private          int                                    _modelMenuPage;
    private          IReadOnlyList<ProjectViewModel>?       _projectOptions;
    private          string                                 _projectSearchText = string.Empty;

    public RelayCommand                             ToggleModelMenuCommand { get; private set; } = null!;
    public RelayCommand                             ShowModelMenuRootCommand { get; private set; } = null!;
    public RelayCommand                             ShowModelsPageCommand { get; private set; } = null!;
    public RelayCommand                             ShowEffortsPageCommand { get; private set; } = null!;
    public RelayCommand                             TogglePermissionMenuCommand { get; private set; } = null!;
    public RelayCommand                             ToggleWorkspaceMenuCommand { get; private set; } = null!;
    public ObservableCollection<ComposerModelGroup> ModelGroups { get; } = [];
    public ObservableCollection<ComposerMenuOption> PermissionOptions { get; } = [];
    public ObservableCollection<ComposerMenuOption> ProjectMenuOptions { get; } = [];
    public ObservableCollection<ComposerMenuOption> VisibleProjectMenuOptions { get; } = [];
    public IReadOnlyList<ComposerMenuOption>        EffortOptions { get; private set; } = [];
    public bool                                     CanChangeModel => !IsRunning && !_opening && !_changing;

    public bool CanConfirmPermissionMigration => PermissionMigrationPending &&
                                                 !IsRunning                 && !_opening &&
                                                 !_changing;

    public bool          CanSelectModel      => CanChangeModel;
    public RelayCommand? ManageModelsCommand { get; init; }

    public string ModelPickerLabel => CurrentEffortText.Length == 0
        ? CurrentModelLabel
        : $"{CurrentModelLabel} · {CurrentEffortText}";

    // 未打开宿主时按供应商名与模型 ID 预判已保存选择是否仍在目录中：缺失则选择器显示选择提示、发送被拦截；
    // 宿主已打开后标签以运行时选择为准，仅凭据失效的未绑定宿主不视为缺失，等待发送时自动刷新。
    private bool IsSavedSelectionMissing =>
        Id is not null                        && _runtime is null && _savedSelection is { } saved &&
        SavedSelectionName(saved) is { } name &&
        !ModelOptions.Any(option => option.ProfileName == name && option.ModelId == saved.Model.ModelId);

    public string CurrentModelNameText => IsSavedSelectionMissing
        ? "请选择模型"
        : (_runtime?.Host.Session.ModelSelection ?? _savedSelection)?.Model.ModelId ??
          _historicalIdentity?.ModelId ?? SelectedModel?.ModelId ??
          _defaultModel?.ModelId ?? "请选择模型";

    public string CurrentModelLabel => IsSavedSelectionMissing
        ? "请选择模型"
        : (_runtime?.Host.Session.ModelSelection ?? _savedSelection) is { } selection
            ? selection.Model.ModelId
            : Id is not null
                ? CurrentModelNameText
                : (SelectedModel ?? _defaultModel)?.Label ?? "请选择模型";

    public string CurrentEffortText => IsSavedSelectionMissing
        ? string.Empty
        : (_runtime?.Host.Session.ModelSelection ?? _savedSelection) is { } selection
            ? EffortText(selection.ReasoningEffort ?? CurrentChoice()?.DefaultEffort)
            : Id is not null
                ? "未记录"
                : EffortText(SelectedModel is null
                                 ? _defaultModel?.DefaultEffort
                                 : _effort ?? SelectedModel.DefaultEffort);

    public string              ChangeModelHint       => CanChangeModel ? "选择将可靠保存到本会话" : "当前运行结束后可切换";
    public string              PermissionPickerLabel => _storedPermissionLabel ?? PermissionMode.Label();
    public SessionApprovalMode PermissionMode        { get; private set; } = SessionApprovalMode.HumanApproval;

    public string PermissionChangeHint => CanChangeModel
        ? "选择将保存到本会话；默认与菜单显示可在设置中调整。"
        : "请先停止当前运行，再切换权限；对话会保留。";

    public string WorkspaceLabel => Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspaceRoot)) is
        { Length: > 0 } name
        ? name
        : WorkspaceRoot;

    /// <summary>
    ///     <para>由主窗口按页注入的项目选择命令，切换本页空白草稿的目标。</para>
    ///     <para>The page-bound project selection command injected by the main window to retarget this blank draft.</para>
    /// </summary>
    public RelayCommand<ProjectViewModel?>? SelectProjectCommand { get; set; }

    /// <summary>
    ///     <para>项目分组投影，供项目菜单与目标标签解析；不复制第二份目录册。</para>
    ///     <para>The project-group projection used by the project menu and target label, without duplicating the catalog.</para>
    /// </summary>
    public IReadOnlyList<ProjectViewModel>? ProjectOptions
    {
        get => _projectOptions;
        set
        {
            if (!SetProperty(ref _projectOptions, value)) return;
            var search = ProjectSearchText;
            RebuildProjectMenu();
            ProjectSearchText = search;
        }
    }

    public string ProjectSearchText
    {
        get => _projectSearchText;
        set
        {
            if (!SetProperty(ref _projectSearchText, value)) return;
            RebuildVisibleProjectMenu();
        }
    }

    public bool HasVisibleProjectChoices => VisibleProjectMenuOptions.Count > 0;
    public bool HasNoProjects            => ProjectMenuOptions.Count        == 0;

    public bool HasProjectSearchNoResults => HasProjectChoices && !HasVisibleProjectChoices &&
                                             !string.IsNullOrWhiteSpace(ProjectSearchText);

    public bool HasSelectedProject => ProjectId is not null;

    public bool IsNoProjectSelection => ProjectId is null &&
                                        ProjectAssignmentState == SessionProjectAssignmentState.NoProject;

    /// <summary>
    ///     <para>
    ///         空白页目标标签：已绑定项目时显示项目名；无项目时显示项目选择提示。
    ///     </para>
    ///     <para>
    ///         Blank-page target label: shows the project name when bound and a project-selection prompt otherwise.
    ///     </para>
    /// </summary>
    public string WorkspaceTargetLabel => ProjectId is { } id
        ? ProjectOptions?.FirstOrDefault(item => item.Project?.Id == id)?.Name ?? WorkspaceLabel
        : "选择项目";

    public bool IsModelMenuRootPage => _modelMenuPage == 0;
    public bool IsModelsMenuPage    => _modelMenuPage == 1;
    public bool IsEffortsMenuPage   => _modelMenuPage == 2;

    public bool IsModelMenuOpen
    {
        get => _isModelMenuOpen;
        set
        {
            if (!SetProperty(ref _isModelMenuOpen, value) || !value) return;
            IsPermissionMenuOpen = false;
            IsWorkspaceMenuOpen  = false;
            SetModelMenuPage(0);
            RebuildModelMenu();
        }
    }

    public bool IsPermissionMenuOpen
    {
        get => _isPermissionMenuOpen;
        set
        {
            if (!SetProperty(ref _isPermissionMenuOpen, value) || !value) return;
            IsModelMenuOpen     = false;
            IsWorkspaceMenuOpen = false;
            RebuildPermissionMenu();
        }
    }

    /// <summary>
    ///     <para>空白页项目／目录菜单的打开状态；列出现有项目并复用「打开目录」入口。</para>
    ///     <para>The blank-page project/directory menu state; it lists existing projects and reuses the open-directory entry.</para>
    /// </summary>
    public bool IsWorkspaceMenuOpen
    {
        get => _isWorkspaceMenuOpen;
        set
        {
            if (!SetProperty(ref _isWorkspaceMenuOpen, value) || !value) return;
            IsModelMenuOpen      = false;
            IsPermissionMenuOpen = false;
            RebuildProjectMenu();
        }
    }

    /// <summary>
    ///     <para>当前项目菜单是否包含任何已存在项目。</para>
    ///     <para>Whether the project menu contains any existing projects.</para>
    /// </summary>
    public bool HasProjectChoices => ProjectMenuOptions.Count > 0;

    /// <summary>
    ///     <para>主窗口在项目组名可能变化后请求重算目标标签；仅标签实际变化的页面被调用。</para>
    ///     <para>Requests a target-label recompute after group names may change; only pages whose label changed are called.</para>
    /// </summary>
    internal void NotifyWorkspaceTargetLabelChanged() => OnPropertyChanged(nameof(WorkspaceTargetLabel));

    // 档位未指定时各标签自行省略强度段；composer 不再提供“默认”档位项。
    private static string EffortText(ModelReasoningEffort? effort) =>
        effort is { } level ? ModelReasoningEffortParser.ToValueString(level) : string.Empty;

    private static string SelectionLabel(SessionModelSelection selection)
    {
        var label  = $"{selection.ProfileHint ?? ""} / {selection.Model.ModelId}";
        var effort = EffortText(selection.ReasoningEffort);
        return effort.Length == 0 ? label : $"{label} · {effort}";
    }

    /// <summary>
    ///     <para>推导已保存选择的供应商名：优先供应商提示，其次凭据路由前缀；两者皆无返回 null。</para>
    ///     <para>
    ///         Derives the provider name of a saved selection: the profile hint first, then the credential-route
    ///         prefix; null when neither is recorded.
    ///     </para>
    /// </summary>
    private static string? SavedSelectionName(SessionModelSelection selection)
    {
        if (selection.ProfileHint is { } hint) return hint;
        var route     = selection.CredentialRoute;
        var separator = route is null ? -1 : route.IndexOf('|');
        return separator > 0 ? route![..separator] : null;
    }

    /// <summary>
    ///     <para>建立输入区菜单；模型强度来自目录，未接入的权限预设禁用。</para>
    ///     <para>Builds composer menus using catalog effort capabilities and disables unsupported permission presets.</para>
    /// </summary>
    private void InitializeComposer()
    {
        ToggleModelMenuCommand   = new RelayCommand(() => IsModelMenuOpen = !IsModelMenuOpen);
        ShowModelMenuRootCommand = new RelayCommand(() => SetModelMenuPage(0));
        ShowModelsPageCommand = new RelayCommand(() =>
        {
            RebuildModelMenu();
            SetModelMenuPage(1);
        });
        ShowEffortsPageCommand = new RelayCommand(() =>
        {
            RebuildEfforts();
            SetModelMenuPage(2);
        });
        TogglePermissionMenuCommand = new RelayCommand(() => IsPermissionMenuOpen = !IsPermissionMenuOpen);
        ToggleWorkspaceMenuCommand  = new RelayCommand(() => IsWorkspaceMenuOpen  = !IsWorkspaceMenuOpen);
        RebuildEfforts();
    }

    private void SetModelMenuPage(int page)
    {
        _modelMenuPage = page;
        OnPropertyChanged(nameof(IsModelMenuRootPage));
        OnPropertyChanged(nameof(IsModelsMenuPage));
        OnPropertyChanged(nameof(IsEffortsMenuPage));
    }

    private void RebuildModelMenu()
    {
        ModelGroups.Clear();
        foreach (var group in ModelOptions.GroupBy(model => model.ProfileName))
            ModelGroups.Add(new ComposerModelGroup(group.Key, group.Select(model => ModelMenuOption(model)).ToArray()));
    }

    private ComposerMenuOption ModelMenuOption(ModelChoice choice) =>
        new(choice.Label, () => _ = ApplyMenuSelectionAsync(choice, choice.DefaultEffort),
            () => CanChangeModel)
        {
            IsSelected = choice.ProfileName ==
                         ((_runtime?.Host.Session.ModelSelection ?? _savedSelection)?.ProfileHint ??
                          (SelectedModel                         ?? _defaultModel)?.ProfileName) &&
                         choice.ModelId == CurrentModelNameText
        };

    /// <summary>
    ///     <para>刷新未手动选择的草稿默认项；默认目标只来自全局用户配置，迟到结果不能覆盖手动选择或已建立会话。</para>
    ///     <para>
    ///         Refreshes an implicit draft default; the default target comes from the global user config only, and
    ///         late results cannot replace an explicit choice or established session.
    ///     </para>
    /// </summary>
    public async Task RefreshDefaultModelAsync()
    {
        var revision = ++_defaultModelRevision;
        if (!CanChooseWorkspace || SelectedModel is not null) return;
        var targetRevision = TargetIntentRevision;
        _defaultModel = null;
        NotifyModelSelection();
        try
        {
            var resolution = await _factory.ResolveDefaultConfigAsync(CancellationToken.None);
            if (revision       != _defaultModelRevision ||
                targetRevision != TargetIntentRevision  ||
                !CanChooseWorkspace                     ||
                SelectedModel is not null) return;
            var user = await UserConfigStore.LoadAsync(resolution.UserConfigPath, CancellationToken.None);
            ApplyPermissionSettings(user.Settings?.Permissions ?? new PermissionSettings());
            var config = resolution.Config;
            var profiles =
                user.Profiles.Where(profile => profile.Endpoint                  == config.Endpoint                  &&
                                               profile.ChatApi                   == config.ChatApi                   &&
                                               profile.ApiKeyEnvironmentVariable == config.ApiKeyEnvironmentVariable &&
                                               profile.ApiKeyCredentialTarget    == config.ApiKeyCredentialTarget    &&
                                               profile.Models.Any(model => model.Id == config.Model)).ToArray();
            var profile = resolution.ProfileName is { } name
                ? user.Profiles.SingleOrDefault(item => item.Name == name)
                : profiles.Length == 1
                    ? profiles[0]
                    : null;
            var model = profile?.Models.SingleOrDefault(item => item.Id == config.Model);
            if (revision       != _defaultModelRevision ||
                targetRevision != TargetIntentRevision  ||
                !CanChooseWorkspace                     ||
                SelectedModel is not null) return;
            _defaultModel = string.IsNullOrWhiteSpace(config.Model)
                ? null
                : new ModelChoice(profile?.Name ?? config.Endpoint, config.Model, model?.DisplayName,
                                  model?.ReasoningEfforts,
                                  resolution.ProfileName is null ? null : model?.DefaultReasoningEffort);
            NotifyModelSelection();
            RebuildModelMenu();
            RebuildEfforts();
        }
        catch (Exception exception)
        {
            if (revision       != _defaultModelRevision ||
                targetRevision != TargetIntentRevision  ||
                !CanChooseWorkspace                     ||
                SelectedModel is not null) return;
            _defaultModel = null;
            NotifyModelSelection();
            RebuildModelMenu();
            RebuildEfforts();
            ShowError(exception);
        }
    }

    private void RebuildEfforts()
    {
        var selection = _runtime?.Host.Session.ModelSelection ?? _savedSelection;
        var choice = selection is null
            ? SelectedModel ?? ModelOptions.FirstOrDefault(item => item.ProfileName == _defaultModel?.ProfileName &&
                                                                   item.ModelId     == _defaultModel?.ModelId)
            : ModelOptions.FirstOrDefault(item => item.ProfileName == selection.ProfileHint &&
                                                  item.ModelId     == selection.Model.ModelId);
        var levels  = choice?.Efforts            ?? selection?.ReasoningCapabilities ?? [];
        // 选中态取真实生效档位，未显式选择时以模型默认档为当前档；都没有则不高亮任何项。
        var current = selection?.ReasoningEffort ?? (Id is null ? _effort : null)    ?? choice?.DefaultEffort;
        EffortOptions = levels.Select(level => new ComposerMenuOption(EffortText(level),
                                                                      () => _ = ApplyMenuSelectionAsync(choice, level),
                                                                      () => CanChangeModel && choice is not null)
                                          { IsSelected = current == level }).ToArray();
        OnPropertyChanged(nameof(EffortOptions));
    }

    /// <summary>
    ///     <para>按会话选择的 profile 与模型 ID 解析目录选项；未声明时返回 null。</para>
    ///     <para>Resolves the catalog choice for a session selection; returns null when it is not declared.</para>
    /// </summary>
    private ModelChoice? CurrentChoice() => (_runtime?.Host.Session.ModelSelection ?? _savedSelection) is { } selection
        ? ModelOptions.FirstOrDefault(item => item.ProfileName == selection.ProfileHint &&
                                              item.ModelId     == selection.Model.ModelId)
        : null;

    private async Task ApplyMenuSelectionAsync(ModelChoice? choice, ModelReasoningEffort? effort)
    {
        try
        {
            await SelectModelAsync(choice, effort);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>
    ///     <para>草稿只更新偏好；已有会话通过已注册宿主异步提交，失败保留标签和草稿。</para>
    ///     <para>
    ///         Updates only draft preferences, or commits asynchronously through the registered host, keeping confirmed
    ///         labels and drafts on failure.
    ///     </para>
    /// </summary>
    public async Task SelectModelAsync(ModelChoice? choice, ModelReasoningEffort? effort)
    {
        if (!CanChangeModel) throw new InvalidOperationException("当前运行结束后可切换。");
        if (Id is null)
        {
            SelectedModel       = choice;
            _effort             = effort;
            // 草稿模型偏好是用户显式选择，参与空白页回收判定。
            HasUserPreselection = true;
            NotifyModelSelection();
            CloseComposerMenus();
            return;
        }

        if (choice is null) throw new InvalidOperationException("请选择已配置的模型。");
        _changing = true;
        OnPropertyChanged(nameof(CanChangeModel));
        Status = "正在切换模型";
        try
        {
            await EnsureHistoryLoadedAsync();
            while (HasMoreHistory) await LoadHistoryAsync();
            if (_runtime is null) await OpenRuntimeAsync(true);
            var revision = _runtime!.Host.Session.ModelSelection!.Revision;
            _savedSelection = await _runtime.ChangeModelAsync(choice.ProfileName, choice.ModelId, effort, revision);
            _model          = choice;
            _effort         = _savedSelection.ReasoningEffort;
            Status          = "模型选择已保存";
            UpdateNotices();
            NotifyModelSelection();
            CloseComposerMenus();
        }
        finally
        {
            _changing = false;
            OnPropertyChanged(nameof(CanChangeModel));
        }
    }

    private async Task OpenRuntimeAsync(bool reselect)
    {
        var request = new DesktopSessionRequest(WorkspaceRoot, PermissionMode, Id, SelectedModel?.ProfileName,
                                                SelectedModel?.ModelId, true, _effort,
                                                SelectedModel is not null, reselect)
        {
            ProjectId              = ProjectId, StoreDirectory = StoreDirectory,
            ProjectAssignmentState = ProjectAssignmentState
        };
        _runtime = await _manager.OpenAsync(Id,
                                            token => _factory.OpenAsync(request, OnEventAsync, OnApprovalAsync, token),
                                            _openingCancellation?.Token ?? CancellationToken.None);
        Id                     = _runtime.Id;
        _workspaceRoot         = _runtime.Host.Session.WorkspaceRoot;
        StoreDirectory         = Path.GetDirectoryName(_runtime.Host.PersistencePath);
        ProjectId              = _runtime.Host.Session.ProjectId;
        ProjectAssignmentState = _runtime.Host.Session.ProjectAssignmentState;
        if (_runtime.Host.Session.PermissionSelectionV2 is { } selectionV2)
        {
            SetPermissionMode(selectionV2.ApprovalMode, false);
        }
        else if (_runtime.Host.Session.PermissionSelection is { } legacy)
        {
            SetPermissionMode(SessionApprovalMode.HumanApproval, false);
            _storedPermissionLabel = null;
            OnPropertyChanged(nameof(PermissionPickerLabel));
        }

        _permissionMigrationPending = _runtime.Host.Session.PermissionMigrationPending;
        _permissionMigrationError   = string.Empty;
        OnPropertyChanged(nameof(PermissionMigrationPending));
        OnPropertyChanged(nameof(PermissionMigrationError));
        OnPropertyChanged(nameof(ProjectId));
        RefreshProjectMenuSelection();
        OnPropertyChanged(nameof(HasSelectedProject));
        OnPropertyChanged(nameof(IsNoProjectSelection));
        OnPropertyChanged(nameof(ProjectAssignmentState));
        OnPropertyChanged(nameof(WorkspaceRoot));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(WorkspaceTargetLabel));
        NotifyModelSelection();
        // 空白页在此取得真实 SessionId：提升为正式会话行由主窗口响应，不等待本轮模型完成。
        if (!_established)
        {
            _established = true;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(CanPin));
            OnPropertyChanged(nameof(CanArchive));
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanChooseWorkspace));
            Established?.Invoke(this);
        }
    }

    private void RebuildPermissionMenu()
    {
        PermissionOptions.Clear();
        // 双审批模式菜单（人工审批 / AI 审核）。
        foreach (var mode in SessionApprovalModes.All)
            PermissionOptions.Add(new ComposerMenuOption(mode.Label(), () => _ = ApplyPermissionSelectionAsync(mode),
                                                         () => CanChangeModel)
            {
                IsSelected = PermissionMode == mode, ToolTip = mode.Description()
            });
    }

    /// <summary>
    ///     <para>按打开时的侧栏分组重建空白页项目菜单；只列已有项目，不在此创建项目或写目录册。</para>
    ///     <para>
    ///         Rebuilds the blank-page project menu from the sidebar groups at open time; it lists existing projects only
    ///         and never writes the catalog.
    ///     </para>
    /// </summary>
    private void RebuildProjectMenu()
    {
        ProjectSearchText = string.Empty;
        ProjectMenuOptions.Clear();
        _projectMenuProjectIds.Clear();
        foreach (var group in ProjectOptions ?? [])
        {
            if (group.Project is not { } project) continue;
            var option = new ComposerMenuOption(group.Name,
                                                () => SelectProjectCommand?.Execute(group),
                                                () => CanChooseWorkspace)
            {
                IsSelected = project.Id == ProjectId,
                ToolTip    = group.HeaderTip
            };
            ProjectMenuOptions.Add(option);
            _projectMenuProjectIds.Add(option, project.Id);
        }

        OnPropertyChanged(nameof(HasProjectChoices));
        OnPropertyChanged(nameof(HasNoProjects));
        RebuildVisibleProjectMenu();
    }

    private void RefreshProjectMenuSelection()
    {
        foreach (var (option, projectId) in _projectMenuProjectIds)
            option.IsSelected = projectId == ProjectId;
    }

    private void RebuildVisibleProjectMenu()
    {
        VisibleProjectMenuOptions.Clear();
        var search = ProjectSearchText.Trim();
        foreach (var option in ProjectMenuOptions)
            if (search.Length == 0 || option.Label.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                VisibleProjectMenuOptions.Add(option);

        OnPropertyChanged(nameof(HasVisibleProjectChoices));
        OnPropertyChanged(nameof(HasProjectSearchNoResults));
    }

    /// <summary>
    ///     <para>刷新菜单和新会话默认值，保留显式选择与已有会话模式。</para>
    ///     <para>Refreshes the menu and draft default while preserving explicit choices and established session modes.</para>
    /// </summary>
    public void ApplyPermissionSettings(PermissionSettings settings)
    {
        settings.Validate();
        _permissionSettings = settings;
        if (Id is null && !_permissionExplicit && _runtime is null)
            SetPermissionMode(
                              settings.PendingMigration
                                  ? LegacyPermissionSelectionMapper.FromLegacyName(settings.LegacyDefaultMode)
                                 ?? SessionApprovalMode.HumanApproval
                                  : settings.DefaultApprovalMode, false);
        RebuildPermissionMenu();
    }

    /// <summary>
    ///     <para>历史列表只投影已保存模式，不打开宿主或授予执行权限。</para>
    ///     <para>Projects a saved mode in history without opening a host or granting execution.</para>
    /// </summary>
    public void ApplyStoredPermissionMode(string? legacyModeName)
    {
        if (_runtime is null && Id is not null && !_changing && legacyModeName is not null)
        {
            if (Enum.TryParse<SessionApprovalMode>(legacyModeName, out var approvalMode) &&
                Enum.IsDefined(approvalMode) && approvalMode.ToString() == legacyModeName)
            {
                SetPermissionMode(approvalMode, false);
                return;
            }

            SetPermissionMode(SessionApprovalMode.HumanApproval, false);
            _storedPermissionLabel = null;
            OnPropertyChanged(nameof(PermissionPickerLabel));
        }
    }

    /// <summary>
    ///     <para>从侧栏摘要推导历史会话迁移提示；宿主恢复后以持久化恢复判定为准。</para>
    ///     <para>
    ///         Projects the historical migration notice from sidebar metadata; persisted recovery is authoritative once the
    ///         host opens.
    ///     </para>
    /// </summary>
    public void ApplyStoredPermissionMigration(string? permissionModeName, int turnCount, string summary)
    {
        var hasCurrentMode = Enum.TryParse<SessionApprovalMode>(permissionModeName, out var mode) &&
                             Enum.IsDefined(mode) && mode.ToString() == permissionModeName;
        if (_runtime is null)
        {
            _permissionMigrationPending = !hasCurrentMode &&
                                          (permissionModeName is not null || turnCount > 0);
            OnPropertyChanged(nameof(PermissionMigrationPending));
        }

        ApplyPermissionMigrationSummary(summary);
    }

    /// <summary>
    ///     <para>更新本会话迁移确认所需的可信沙箱配置摘要，不读取代理地址或凭据。</para>
    ///     <para>
    ///         Updates the trusted sandbox summary for this session's migration prompt without reading proxy addresses or
    ///         credentials.
    ///     </para>
    /// </summary>
    public void ApplyPermissionMigrationSummary(string summary)
    {
        PermissionMigrationSummary = summary;
        OnPropertyChanged(nameof(CanConfirmPermissionMigration));
    }

    private void SetPermissionMode(SessionApprovalMode mode, bool userSelected)
    {
        // 同值静默：每轮刷新都会重放保存模式，无变化且非显式重选时不发通知；显式同值重选仍通知（评审既定语义）。
        var changed = PermissionMode != mode;
        PermissionMode         = mode;
        _storedPermissionLabel = null;
        if (userSelected)
        {
            _permissionExplicit = true;
            HasUserPreselection = true;
        }

        if (!changed && !userSelected) return;
        OnPropertyChanged(nameof(PermissionPickerLabel));
        OnPropertyChanged(nameof(PermissionMode));
    }

    private async Task ApplyPermissionSelectionAsync(SessionApprovalMode mode)
    {
        try
        {
            await SelectPermissionModeAsync(mode);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    /// <summary>
    ///     <para>草稿只更新选择；正式会话在空闲时可靠提交，失败保留旧标签和对话。</para>
    ///     <para>
    ///         Updates drafts locally or durably commits an idle established session, retaining its old label and
    ///         conversation on failure.
    ///     </para>
    /// </summary>
    public async Task SelectPermissionModeAsync(SessionApprovalMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!CanChangeModel) throw new InvalidOperationException("请先停止当前运行，再切换权限。");
        if (Id is null)
        {
            SetPermissionMode(mode, true);
            RebuildPermissionMenu();
            CloseComposerMenus();
            return;
        }

        _changing = true;
        OnPropertyChanged(nameof(CanChangeModel));
        OnPropertyChanged(nameof(PermissionChangeHint));
        try
        {
            await EnsureHistoryLoadedAsync();
            while (HasMoreHistory) await LoadHistoryAsync();
            if (_runtime is null) await OpenRuntimeAsync(false);
            var applied = await _runtime!.ChangeApprovalModeAsync(mode);
            SetPermissionMode(applied, true);
            Status = "权限模式已保存并生效";
            CloseComposerMenus();
        }
        finally
        {
            _changing = false;
            OnPropertyChanged(nameof(CanChangeModel));
            OnPropertyChanged(nameof(PermissionChangeHint));
            RebuildPermissionMenu();
        }
    }

    private void NotifyModelSelection()
    {
        OnPropertyChanged(nameof(CurrentModelNameText));
        OnPropertyChanged(nameof(CurrentModelLabel));
        OnPropertyChanged(nameof(ModelPickerLabel));
        OnPropertyChanged(nameof(CurrentEffortText));
        OnPropertyChanged(nameof(ChangeModelHint));
    }

    private void CloseComposerMenus()
    {
        IsModelMenuOpen      = false;
        IsPermissionMenuOpen = false;
        IsWorkspaceMenuOpen  = false;
    }
}

/// <summary>
///     <para>模型弹层中的提供方分组。</para>
///     <para>A provider group in the model popup.</para>
/// </summary>
public sealed record ComposerModelGroup(string Name, IReadOnlyList<ComposerMenuOption> Options);

/// <summary>
///     <para>菜单展示值与带可用性检查的操作；禁用项不会更改配置。</para>
///     <para>A menu label and guarded action; disabled entries never change configuration.</para>
/// </summary>
public sealed class ComposerMenuOption(string label, Action select, Func<bool> canSelect) : ObservableObject
{
    private bool _isSelected;

    public string Label { get; } = label;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string? ToolTip { get; init; }

    public RelayCommand SelectCommand { get; } = new(() =>
    {
        if (canSelect()) select();
    }, canSelect);
}
