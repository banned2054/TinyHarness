using System.Collections.ObjectModel;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>承载一次运行的标题与过程；回答正文由时间线独立展示。</para>
///     <para>Holds one run's header and process; answer bodies remain separate timeline items.</para>
/// </summary>
public sealed class ProcessGroupItemViewModel : ConversationItemViewModel
{
    private bool _isExpanded;
    private bool _isRunning;
    private bool _userToggled;

    public ProcessGroupItemViewModel(long seq, string runId, string? modelLabel, bool isRunning) : base(seq)
    {
        RunId       = runId;
        ModelLabel  = modelLabel ?? string.Empty;
        _isRunning  = isRunning;
        _isExpanded = isRunning;
        ToggleCommand = new RelayCommand(() =>
        {
            _userToggled = true;
            IsExpanded   = !IsExpanded;
        });
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(Summary));
        };
    }

    public string RunId { get; }
    public string ModelLabel { get; }
    public bool HasRunModelLabel => !string.IsNullOrWhiteSpace(ModelLabel);
    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];
    public RelayCommand ToggleCommand { get; }
    public bool HasItems => Items.Count > 0;
    public bool IsRunning => _isRunning;
    public string Summary => IsRunning ? "执行过程 · 运行中" : $"执行过程 · {Items.Count} 项";

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    ///     <para>运行结束时默认收起；用户显式选择过的展开状态在后续更新中保持。</para>
    ///     <para>Collapses on completion by default, preserving expansion explicitly chosen by the user.</para>
    /// </summary>
    public void Complete()
    {
        if (!SetProperty(ref _isRunning, false, nameof(IsRunning))) return;
        if (!_userToggled) IsExpanded = false;
        OnPropertyChanged(nameof(Summary));
    }
}
