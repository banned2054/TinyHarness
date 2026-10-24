using System.Collections.ObjectModel;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class SessionViewModel
{
    private bool _isModelMenuOpen;
    private bool _isPermissionMenuOpen;
    private int _modelMenuPage;

    public RelayCommand ToggleModelMenuCommand { get; private set; } = null!;
    public RelayCommand ShowModelMenuRootCommand { get; private set; } = null!;
    public RelayCommand ShowModelsPageCommand { get; private set; } = null!;
    public RelayCommand ShowEffortsPageCommand { get; private set; } = null!;
    public RelayCommand TogglePermissionMenuCommand { get; private set; } = null!;
    public ObservableCollection<ComposerModelGroup> ModelGroups { get; } = [];
    public ObservableCollection<ComposerMenuOption> PermissionOptions { get; } = [];
    public IReadOnlyList<ComposerMenuOption> EffortOptions { get; private set; } = [];
    public bool CanSelectModel => Id is null && CanConfigure;
    public string ModelPickerLabel => CurrentModelNameText;
    public string CurrentModelNameText => _runtime?.Host.Session.Model.ModelId ?? SelectedModel?.ModelId ?? "工作区默认";
    public string CurrentEffortText => "默认";
    public string PermissionPickerLabel => AllowChanges ? "按规则审批" : "只读";
    public string WorkspaceLabel => Path.GetFileName(Path.TrimEndingDirectorySeparator(WorkspaceRoot)) is { Length: > 0 } name
        ? name : WorkspaceRoot;
    public bool IsModelMenuRootPage => _modelMenuPage == 0;
    public bool IsModelsMenuPage => _modelMenuPage == 1;
    public bool IsEffortsMenuPage => _modelMenuPage == 2;

    public bool IsModelMenuOpen
    {
        get => _isModelMenuOpen;
        set
        {
            if (!SetProperty(ref _isModelMenuOpen, value) || !value) return;
            IsPermissionMenuOpen = false;
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
            IsModelMenuOpen = false;
            RebuildPermissionMenu();
        }
    }

    /// <summary>
    ///     <para>恢复输入区的菜单结构；未接入的推理等级和权限预设保留禁用入口。</para>
    ///     <para>Restores composer menus while retaining disabled entries for unsupported effort and permission presets.</para>
    /// </summary>
    private void InitializeComposer()
    {
        ToggleModelMenuCommand = new RelayCommand(() => IsModelMenuOpen = !IsModelMenuOpen);
        ShowModelMenuRootCommand = new RelayCommand(() => SetModelMenuPage(0));
        ShowModelsPageCommand = new RelayCommand(() => { RebuildModelMenu(); SetModelMenuPage(1); });
        ShowEffortsPageCommand = new RelayCommand(() => SetModelMenuPage(2));
        TogglePermissionMenuCommand = new RelayCommand(() => IsPermissionMenuOpen = !IsPermissionMenuOpen);
        EffortOptions = [new("默认（由模型决定）", () => { }, () => false) { IsSelected = true },
                        new("关闭", () => { }, () => false), new("低", () => { }, () => false),
                        new("高", () => { }, () => false), new("最高", () => { }, () => false)];
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
        ModelGroups.Add(new ComposerModelGroup("工作区", [ModelMenuOption(null)]));
        foreach (var group in ModelOptions.GroupBy(model => model.ProfileName))
            ModelGroups.Add(new ComposerModelGroup(group.Key, group.Select(model => ModelMenuOption(model)).ToArray()));
    }

    private ComposerMenuOption ModelMenuOption(ModelChoice? choice) =>
        new(choice?.ModelId ?? "使用工作区配置", () =>
        {
            if (!CanSelectModel) return;
            SelectedModel = choice;
            IsModelMenuOpen = false;
        }, () => CanSelectModel) { IsSelected = Equals(SelectedModel, choice) };

    private void RebuildPermissionMenu()
    {
        PermissionOptions.Clear();
        PermissionOptions.Add(PermissionOption("只读", false));
        PermissionOptions.Add(PermissionOption("允许修改 · 按规则审批", true));
        PermissionOptions.Add(new ComposerMenuOption("完全访问（未接入）", () => { }, () => false));
        PermissionOptions.Add(new ComposerMenuOption("自动（未接入）", () => { }, () => false));
    }

    private ComposerMenuOption PermissionOption(string label, bool allowChanges) =>
        new(label, () =>
        {
            if (!CanConfigure) return;
            AllowChanges = allowChanges;
            IsPermissionMenuOpen = false;
        }, () => CanConfigure) { IsSelected = AllowChanges == allowChanges };

    private void NotifyModelSelection()
    {
        OnPropertyChanged(nameof(CurrentModelNameText));
        OnPropertyChanged(nameof(ModelPickerLabel));
    }

    private void CloseComposerMenus()
    {
        IsModelMenuOpen = false;
        IsPermissionMenuOpen = false;
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
    public string Label { get; } = label;
    public bool IsSelected { get; init; }
    public RelayCommand SelectCommand { get; } = new(() => { if (canSelect()) select(); }, canSelect);
}
