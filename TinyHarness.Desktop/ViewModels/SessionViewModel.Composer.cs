using System.Collections.ObjectModel;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class SessionViewModel
{
    private ModelChoice? _defaultModel;
    private long         _defaultModelRevision;
    private bool         _isModelMenuOpen;
    private bool         _isPermissionMenuOpen;
    private bool         _isWorkspaceMenuOpen;
    private int          _modelMenuPage;

    public RelayCommand                             ToggleModelMenuCommand { get; private set; } = null!;
    public RelayCommand                             ShowModelMenuRootCommand { get; private set; } = null!;
    public RelayCommand                             ShowModelsPageCommand { get; private set; } = null!;
    public RelayCommand                             ShowEffortsPageCommand { get; private set; } = null!;
    public RelayCommand                             TogglePermissionMenuCommand { get; private set; } = null!;
    public RelayCommand                             ToggleWorkspaceMenuCommand { get; private set; } = null!;
    public ObservableCollection<ComposerModelGroup> ModelGroups { get; } = [];
    public ObservableCollection<ComposerMenuOption> PermissionOptions { get; } = [];
    public ObservableCollection<ComposerMenuOption> ProjectMenuOptions { get; } = [];
    public IReadOnlyList<ComposerMenuOption>        EffortOptions { get; private set; } = [];
    public bool                                     CanChangeModel => !IsRunning && !_opening && !_changing;
    public bool                                     CanSelectModel => CanChangeModel;
    public RelayCommand?                            ManageModelsCommand { get; init; }
    public string                                   ModelPickerLabel => $"{CurrentModelLabel} · {CurrentEffortText}";

    public string CurrentModelNameText => (_runtime?.Host.Session.ModelSelection ?? _savedSelection)?.Model.ModelId ??
                                          _historicalIdentity?.ModelId ?? SelectedModel?.ModelId ??
                                          _defaultModel?.ModelId ?? "请选择模型";

    public string CurrentModelLabel => (_runtime?.Host.Session.ModelSelection ?? _savedSelection) is { } selection
        ? selection.Model.ModelId
        : Id is not null
            ? CurrentModelNameText
            : (SelectedModel ?? _defaultModel)?.Label ?? "请选择模型";

    public string CurrentEffortText => (_runtime?.Host.Session.ModelSelection ?? _savedSelection) is { } selection
        ? EffortText(selection.ReasoningEffort)
        : Id is not null
            ? "未记录"
            : EffortText(SelectedModel is null ? _defaultModel?.DefaultEffort : _effort);

    public string ChangeModelHint       => CanChangeModel ? "选择将可靠保存到本会话" : "当前运行结束后可切换";
    public string PermissionPickerLabel => AllowChanges ? "按规则审批" : "只读";

    public string WorkspaceLabel => Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspaceRoot)) is
        { Length: > 0 } name
        ? name
        : WorkspaceRoot;

    /// <summary>
    ///     <para>由主窗口按页注入的项目选择命令，切换本页空白草稿的目标。</para>
    ///     <para>The page-bound project selection command injected by the main window to retarget this blank draft.</para>
    /// </summary>
    public RelayCommand<ProjectViewModel>? SelectProjectCommand { get; set; }

    /// <summary>
    ///     <para>项目分组投影，供项目菜单与目标标签解析；不复制第二份目录册。</para>
    ///     <para>The project-group projection used by the project menu and target label, without duplicating the catalog.</para>
    /// </summary>
    public IReadOnlyList<ProjectViewModel>? ProjectOptions { get; init; }

    /// <summary>
    ///     <para>
    ///         空白页目标标签：已绑定项目时显示项目名，否则显示执行目录名；未绑定页面不得套用无项目的展示组。
    ///     </para>
    ///     <para>
    ///         Blank-page target label: shows the project name when bound, else the execution directory; an unbound page
    ///         never borrows a display-only group's name.
    ///     </para>
    /// </summary>
    public string WorkspaceTargetLabel =>
        ProjectId is { } id && ProjectOptions?.FirstOrDefault(item => item.Project?.Id == id) is { } group
            ? group.Name
            : WorkspaceLabel;

    /// <summary>
    ///     <para>主窗口在项目组名可能变化后请求重算目标标签；仅标签实际变化的页面被调用。</para>
    ///     <para>Requests a target-label recompute after group names may change; only pages whose label changed are called.</para>
    /// </summary>
    internal void NotifyWorkspaceTargetLabelChanged() => OnPropertyChanged(nameof(WorkspaceTargetLabel));

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

    private static string EffortText(ModelReasoningEffort? effort) =>
        effort is { } level ? ModelReasoningEffortParser.ToValueString(level) : "默认";

    private static string SelectionLabel(SessionModelSelection selection) =>
        $"{selection.ProfileHint ?? ""} / {selection.Model.ModelId} · {EffortText(selection.ReasoningEffort)}";

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
    ///     <para>刷新未手动选择的草稿默认项；迟到结果不能覆盖新目录、手动选择或已建立会话。</para>
    ///     <para>
    ///         Refreshes an implicit draft default; late results cannot replace a new directory,
    ///         explicit choice or established session.
    ///     </para>
    /// </summary>
    public async Task RefreshDefaultModelAsync()
    {
        var revision = ++_defaultModelRevision;
        if (!CanChooseWorkspace || SelectedModel is not null) return;
        var targetRevision = TargetIntentRevision;
        var root = WorkspaceRoot;
        _defaultModel = null;
        NotifyModelSelection();
        try
        {
            var resolution = await _factory.ResolveDefaultConfigAsync(root, CancellationToken.None);
            if (revision       != _defaultModelRevision ||
                targetRevision != TargetIntentRevision  ||
                !CanChooseWorkspace                     ||
                SelectedModel is not null               ||
                root != WorkspaceRoot) return;
            var user   = await UserConfigStore.LoadAsync(resolution.UserConfigPath, CancellationToken.None);
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
                SelectedModel is not null               ||
                root != WorkspaceRoot) return;
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
                SelectedModel is not null               ||
                root != WorkspaceRoot) return;
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
        var levels = choice?.Efforts ?? selection?.ReasoningCapabilities ?? [];
        EffortOptions = new ModelReasoningEffort?[] { null }
                       .Concat(levels.Select(level => (ModelReasoningEffort?)level))
                       .Select(level => new ComposerMenuOption(EffortText(level),
                                                               () => _ = ApplyMenuSelectionAsync(choice, level),
                                                               () => CanChangeModel && choice is not null)
                                   { IsSelected = CurrentEffortText == EffortText(level) }).ToArray();
        OnPropertyChanged(nameof(EffortOptions));
    }

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
            SelectedModel = choice;
            _effort       = effort;
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
        var request = new DesktopSessionRequest(WorkspaceRoot, AllowChanges, Id, SelectedModel?.ProfileName,
                                                SelectedModel?.ModelId, true, _effort,
                                                SelectedModel is not null, reselect)
        {
            ProjectId = ProjectId, StoreDirectory = StoreDirectory
        };
        _runtime = await _manager.OpenAsync(Id,
                                            token => _factory.OpenAsync(request, OnEventAsync, OnApprovalAsync, token),
                                            _openingCancellation?.Token ?? CancellationToken.None);
        Id             = _runtime.Id;
        _workspaceRoot = _runtime.Host.Session.WorkspaceRoot;
        StoreDirectory = Path.GetDirectoryName(_runtime.Host.PersistencePath);
        ProjectId      = _runtime.Host.Session.ProjectId;
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(WorkspaceRoot));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(WorkspaceTargetLabel));
        NotifyModelSelection();
        // 空白页在此取得真实 SessionId：提升为正式会话行由主窗口响应，不等待本轮模型完成。
        if (!_established)
        {
            _established = true;
            OnPropertyChanged(nameof(Id));
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanChooseWorkspace));
            Established?.Invoke(this);
        }
    }

    private void RebuildPermissionMenu()
    {
        PermissionOptions.Clear();
        PermissionOptions.Add(PermissionOption("只读", false));
        PermissionOptions.Add(PermissionOption("允许修改 · 按规则审批", true));
        PermissionOptions.Add(new ComposerMenuOption("完全访问（未接入）", () => { }, () => false));
        PermissionOptions.Add(new ComposerMenuOption("自动（未接入）", () => { }, () => false));
    }

    /// <summary>
    ///     <para>项目菜单是否有可选项；用于控制项目分区标题的可见性。</para>
    ///     <para>Whether the project menu has choices, controlling its project-section heading visibility.</para>
    /// </summary>
    public bool HasProjectChoices => ProjectMenuOptions.Count > 0;

    /// <summary>
    ///     <para>按打开时的侧栏分组重建空白页项目菜单；只列已有项目，不在此创建项目或写目录册。</para>
    ///     <para>Rebuilds the blank-page project menu from the sidebar groups at open time; it lists existing projects only and never writes the catalog.</para>
    /// </summary>
    private void RebuildProjectMenu()
    {
        ProjectMenuOptions.Clear();
        foreach (var group in ProjectOptions ?? [])
        {
            if (group.Project is not { } project) continue;
            ProjectMenuOptions.Add(new ComposerMenuOption(group.Name,
                                                          () => SelectProjectCommand?.Execute(group),
                                                          () => CanChooseWorkspace)
            {
                IsSelected = project.Id == ProjectId,
                ToolTip    = group.HeaderTip
            });
        }

        OnPropertyChanged(nameof(HasProjectChoices));
    }

    private ComposerMenuOption PermissionOption(string label, bool allowChanges) =>
        new(label, () =>
        {
            if (!CanConfigure) return;
            // 显式重选当前权限也属于用户预选，不能被空白槽回收忽略。
            HasUserPreselection  = true;
            AllowChanges         = allowChanges;
            IsPermissionMenuOpen = false;
        }, () => CanConfigure) { IsSelected = AllowChanges == allowChanges };

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
public sealed class ComposerMenuOption(string label, Action select, Func<bool> canSelect)
{
    public string  Label      { get; } = label;
    public bool    IsSelected { get; init; }
    public string? ToolTip    { get; init; }

    public RelayCommand SelectCommand { get; } = new(() =>
    {
        if (canSelect()) select();
    }, canSelect);
}
