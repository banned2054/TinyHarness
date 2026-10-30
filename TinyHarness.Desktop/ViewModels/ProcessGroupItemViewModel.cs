using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using TinyHarness.Desktop.Utils;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>承载一次运行的标题与过程；回答正文由时间线独立展示。</para>
///     <para>Holds one run's header and process; answer bodies remain separate timeline items.</para>
/// </summary>
public sealed class ProcessGroupItemViewModel : ConversationItemViewModel
{
    private readonly Dictionary<MessageItemViewModel, (bool HasBody, bool HasReasoning, bool IsInterrupted)>
        _messageStates = [];

    private readonly HashSet<ConversationItemViewModel> _observedItems = [];

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
        Items.CollectionChanged += OnItemsChanged;
    }

    public string                                          RunId            { get; }
    public string                                          ModelLabel       { get; }
    public bool                                            HasRunModelLabel => !string.IsNullOrWhiteSpace(ModelLabel);
    public ObservableCollection<ConversationItemViewModel> Items            { get; } = [];

    /// <summary>
    ///     <para>按正文边界分段后的展示投影；相邻思考和工具条目合并为独立折叠段。</para>
    ///     <para>
    ///         Display projection segmented at body boundaries; adjacent reasoning and tool items share a collapsible
    ///         segment.
    ///     </para>
    /// </summary>
    public ObservableCollection<ConversationItemViewModel> SegmentedItems { get; } = [];

    public RelayCommand ToggleCommand { get; }
    public bool         HasItems      => Items.Count > 0;
    public bool         IsRunning     => _isRunning;
    public string       Summary       => IsRunning ? "执行过程 · 运行中" : $"执行过程 · {Items.Count} 项";

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

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _observedItems) item.PropertyChanged -= OnItemPropertyChanged;
            _observedItems.Clear();
            _messageStates.Clear();
            foreach (var item in Items) ObserveItem(item);
        }
        else
        {
            if (args.OldItems is not null)
                foreach (var item in args.OldItems.OfType<ConversationItemViewModel>())
                    StopObservingItem(item);

            if (args.NewItems is not null)
                foreach (var item in args.NewItems.OfType<ConversationItemViewModel>())
                    ObserveItem(item);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(Summary));
        RefreshSegments(args);
    }

    private void ObserveItem(ConversationItemViewModel item)
    {
        if (!_observedItems.Add(item)) return;

        item.PropertyChanged += OnItemPropertyChanged;
        if (item is MessageItemViewModel message) _messageStates[message] = MessageState(message);
    }

    private void StopObservingItem(ConversationItemViewModel item)
    {
        if (Items.Contains(item) || !_observedItems.Remove(item)) return;

        item.PropertyChanged -= OnItemPropertyChanged;
        if (item is MessageItemViewModel message) _messageStates.Remove(message);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is MessageItemViewModel message &&
            args.PropertyName is nameof(MessageItemViewModel.HasProcessBody) or
                                 nameof(MessageItemViewModel.HasReasoning)
                              or nameof(MessageItemViewModel.HasStatusHint))
        {
            var state = MessageState(message);
            if (_messageStates.GetValueOrDefault(message) == state) return;

            _messageStates[message] = state;
            RefreshSegments();
            return;
        }

        if (sender is ToolActivityItemViewModel tool &&
            args.PropertyName == nameof(ToolActivityItemViewModel.Name))
            foreach (var segment in SegmentedItems.OfType<ProcessSegmentItemViewModel>())
                if (segment.Items.Contains(tool))
                {
                    segment.RefreshSummary();
                    break;
                }
    }

    /// <summary>
    ///     <para>按正文边界更新分段，复用已有分段并保留拆分、合并及替换前的展开选择。</para>
    ///     <para>
    ///         Updates body-delimited segments, reusing segments and retaining expansion across splits, merges and
    ///         replacements.
    ///     </para>
    /// </summary>
    private void RefreshSegments(NotifyCollectionChangedEventArgs? change = null)
    {
        var previous = new Dictionary<(Type ItemType, long Seq, string? Id),
            (ProcessSegmentItemViewModel Segment, bool Expanded)>();
        foreach (var segment in SegmentedItems.OfType<ProcessSegmentItemViewModel>())
        foreach (var item in segment.Items)
            previous[SegmentIdentity(item)] = (segment, segment.IsExpanded);

        if (change is
            {
                Action  : NotifyCollectionChangedAction.Replace, OldItems: { } oldItems,
                NewItems: { } newItems
            })
            for (var index = 0; index < oldItems.Count; index++)
                if (oldItems[index] is ConversationItemViewModel oldItem &&
                    newItems[index] is ConversationItemViewModel newItem &&
                    previous.TryGetValue(SegmentIdentity(oldItem), out var state))
                    previous[SegmentIdentity(newItem)] = state;

        var                              desired  = new List<ConversationItemViewModel>();
        var                              reused   = new HashSet<ProcessSegmentItemViewModel>();
        List<ConversationItemViewModel>? adjacent = null;
        foreach (var item in Items)
        {
            if (item is MessageItemViewModel { HasProcessBody: true } message)
            {
                if (message.GetProcessReasoningProjection() is { } reasoning)
                    (adjacent ??= []).Add(reasoning);

                AppendSegment(adjacent);
                adjacent = null;
                desired.Add(message);
                continue;
            }

            (adjacent ??= []).Add(item);
        }

        AppendSegment(adjacent);
        ConversationProjection.Synchronize(SegmentedItems, desired);

        void AppendSegment(List<ConversationItemViewModel>? items)
        {
            if (items is null || items.Count == 0) return;

            ProcessSegmentItemViewModel? segment  = null;
            var                          expanded = false;
            foreach (var item in items)
            {
                if (!previous.TryGetValue(SegmentIdentity(item), out var state)) continue;

                expanded |= state.Expanded;
                if (segment is null && reused.Add(state.Segment)) segment = state.Segment;
            }

            if (segment is null) segment = new ProcessSegmentItemViewModel(items);
            else segment.UpdateItems(items);
            segment.IsExpanded = expanded;
            desired.Add(segment);
        }
    }

    private static (Type ItemType, long Seq, string? Id) SegmentIdentity(ConversationItemViewModel item) =>
        (item.GetType(), item.Seq, item switch
        {
            MessageItemViewModel message   => message.Id,
            ToolActivityItemViewModel tool => tool.CallId,
            _                              => null
        });

    private static (bool HasBody, bool HasReasoning, bool IsInterrupted) MessageState(
        MessageItemViewModel message) =>
        (message.HasProcessBody, message.HasReasoning, message.IsInterrupted);
}
