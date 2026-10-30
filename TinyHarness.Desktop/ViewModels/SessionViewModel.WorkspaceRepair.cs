using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class SessionViewModel
{
    private bool                         _repairConfirmationElevation;
    private bool                         _repairConfirmationOpen;
    private long                         _repairConfirmationRevision;
    private bool                         _repairDetailsOpen;
    private DesktopWorkspaceRepairState? _workspaceRepair;

    public RelayCommand ShowWorkspaceDetailsCommand { get; private set; } = null!;
    public RelayCommand CloseWorkspaceDetailsCommand { get; private set; } = null!;
    public RelayCommand OpenWorkspaceRepairCommand { get; private set; } = null!;
    public AsyncCommand ConfirmWorkspaceRepairCommand { get; private set; } = null!;
    public AsyncCommand CancelWorkspaceRepairCommand { get; private set; } = null!;
    public bool HasWorkspaceProblem => _workspaceRepair?.Problem is not null;
    public bool CanRepairWorkspace => !IsRunning && _workspaceRepair?.CanRepair == true;
    public bool IsWorkspaceRepairBusy => _workspaceRepair?.Phase is "preparing" or "elevating";
    public string WorkspaceRepairMessage => _factory.Redact(_workspaceRepair?.Message ?? string.Empty);
    public string WorkspaceRepairAction => _workspaceRepair?.NeedsElevation == true ? "确认额外 Windows 授权…" : "修复目录权限…";

    public bool WorkspaceRepairDetailsOpen
    {
        get => _repairDetailsOpen;
        private set => SetProperty(ref _repairDetailsOpen, value);
    }

    public bool WorkspaceRepairConfirmationOpen
    {
        get => _repairConfirmationOpen;
        private set => SetProperty(ref _repairConfirmationOpen, value);
    }

    public string WorkspaceRepairDetails => _workspaceRepair?.Problem is { } problem
        ? _factory.Redact($"工作区：{WorkspaceRoot}\n问题写根：{problem.WriteRoot}\n失败路径：{problem.Path}\n" +
                          $"类别：{problem.Category}\n可管理 ACL：{problem.CanManageAcl}\nWin32：{problem.Win32Error}\n{problem.Detail}\n" +
                          string.Join('\n', problem.MissingAces.Select(ace =>
                                                                           $"SID {ace.Sid} required=0x{ace.RequiredMask:X8} effective=0x{ace.EffectiveMask:X8} " +
                                                                           $"containers=0x{ace.ContainerPropagationMask:X8} objects=0x{ace.ObjectPropagationMask:X8}")))
        : string.Empty;

    public string WorkspaceRepairConfirmationText =>
        $"仅修复当前工作区：{WorkspaceRoot}\n失败路径：{_workspaceRepair?.Problem?.Path}\n" +
        (_repairConfirmationElevation
            ? "普通权限不足。本次确认将请求 Windows UAC；原桌面进程不提升。\n"
            : "先检查普通权限，仅在权限足够时以普通权限准备；不足时另行确认 UAC。\n")                                         +
        "为 TinyHarnessUsers 和当前工作区 capability 合并可继承 Modify，保留原调用账户仅根目录 WRITE_DAC，传播既有未保护子项继承。" +
        "不接管 Owner、不授予 FullControl、不重置受保护 DACL，保留显式规则与元数据 deny。\n"                             +
        "不修复附加写根、不 provisioning、不运行项目命令；失败或取消可能已部分修改 ACL，结果未知时本进程禁止重复授权。修复后不会重放命令。";

    private void InitializeWorkspaceRepair()
    {
        ShowWorkspaceDetailsCommand  = new RelayCommand(() => WorkspaceRepairDetailsOpen = true);
        CloseWorkspaceDetailsCommand = new RelayCommand(() => WorkspaceRepairDetailsOpen = false);
        OpenWorkspaceRepairCommand = new RelayCommand(() =>
        {
            if (!CanRepairWorkspace || _workspaceRepair is null) return;
            _repairConfirmationRevision     = _workspaceRepair.Revision;
            _repairConfirmationElevation    = _workspaceRepair.NeedsElevation;
            WorkspaceRepairConfirmationOpen = true;
            OnPropertyChanged(nameof(WorkspaceRepairConfirmationText));
        }, () => CanRepairWorkspace);
        ConfirmWorkspaceRepairCommand = new AsyncCommand(async () =>
        {
            if (!WorkspaceRepairConfirmationOpen || IsRunning || _runtime?.WorkspaceRepair is not { } repair) return;
            WorkspaceRepairConfirmationOpen = false;
            await repair.RepairAsync(_repairConfirmationRevision, _repairConfirmationElevation);
        }, ShowError);
        CancelWorkspaceRepairCommand = new AsyncCommand(async () =>
        {
            WorkspaceRepairConfirmationOpen = false;
            if (_runtime?.WorkspaceRepair is { } repair) await repair.CancelAsync();
        }, ShowError);
    }

    private void ApplyWorkspaceRepair(DesktopWorkspaceRepairState state)
    {
        _workspaceRepair = state;
        OnPropertyChanged(nameof(HasWorkspaceProblem));
        OnPropertyChanged(nameof(CanRepairWorkspace));
        OnPropertyChanged(nameof(IsWorkspaceRepairBusy));
        OnPropertyChanged(nameof(WorkspaceRepairMessage));
        OnPropertyChanged(nameof(WorkspaceRepairAction));
        OnPropertyChanged(nameof(WorkspaceRepairDetails));
        OpenWorkspaceRepairCommand.RaiseCanExecuteChanged();
    }
}
