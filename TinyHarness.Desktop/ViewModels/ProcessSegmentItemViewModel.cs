using System.Collections.ObjectModel;
using Avalonia.Media;
using TinyHarness.Desktop.Utils;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>把正文消息之间连续的思考和工具活动作为一个可折叠过程段展示。</para>
///     <para>Groups consecutive reasoning and tool activity between body messages into a collapsible segment.</para>
/// </summary>
public sealed class ProcessSegmentItemViewModel : ConversationItemViewModel
{
    private bool   _isExpanded;
    private string _summaryText = string.Empty;

    /// <summary>
    ///     <para>根据时间线顺序创建过程段，并默认收起。</para>
    ///     <para>Creates a process segment in timeline order and collapses it by default.</para>
    /// </summary>
    public ProcessSegmentItemViewModel(IReadOnlyList<ConversationItemViewModel> items) : base(
         items.Count > 0 ? items[0].Seq : -1)
    {
        Items         = new ObservableCollection<ConversationItemViewModel>(items);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        RefreshSummary();
    }

    /// <summary>
    ///     <para>段内原始条目，按时间线顺序排列。</para>
    ///     <para>Contains the original segment items in timeline order.</para>
    /// </summary>
    public ObservableCollection<ConversationItemViewModel> Items { get; }

    /// <summary>
    ///     <para>段内条目的展开状态。</para>
    ///     <para>Whether the segment items are expanded.</para>
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    ///     <para>切换过程段的展开状态。</para>
    ///     <para>Toggles the segment's expanded state.</para>
    /// </summary>
    public RelayCommand ToggleCommand { get; }

    /// <summary>
    ///     <para>收起行简要概括思考、去重后的工具类型与中断状态。</para>
    ///     <para>Briefly summarizes reasoning, deduplicated tool types, and interruption state on the collapsed row.</para>
    /// </summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>
    ///     <para>优先使用段内首个工具的图标，没有工具时显示思考图标。</para>
    ///     <para>Uses the first tool icon when present, otherwise shows the reasoning icon.</para>
    /// </summary>
    public StreamGeometry LeadingIconStroke =>
        Items.OfType<ToolActivityItemViewModel>().FirstOrDefault()?.IconStroke ?? ToolCallText.ThinkingIconStroke;

    /// <summary>
    ///     <para>段内首个工具或思考图标的填充路径；纯描边图标时为空。</para>
    ///     <para>Fill path for the first tool or reasoning icon; null for outline-only icons.</para>
    /// </summary>
    public StreamGeometry? LeadingIconFill
    {
        get
        {
            var tool = Items.OfType<ToolActivityItemViewModel>().FirstOrDefault();
            if (tool is not null) return tool.IconFill;

            return Items.OfType<MessageItemViewModel>().Any(message => message.HasReasoning)
                ? ToolCallText.ThinkingIconFill
                : null;
        }
    }

    /// <summary>
    ///     <para>增量更新段内条目，保留展开选择和未变化条目的控件。</para>
    ///     <para>Updates segment items incrementally, retaining expansion and controls for unchanged items.</para>
    /// </summary>
    internal void UpdateItems(IReadOnlyList<ConversationItemViewModel> items)
    {
        ConversationProjection.Synchronize(Items, items);
        RefreshSummary();
    }

    /// <summary>
    ///     <para>原地刷新折叠行文案和图标，不替换分段。</para>
    ///     <para>Refreshes the collapsed row text and icons in place without replacing the segment.</para>
    /// </summary>
    internal void RefreshSummary()
    {
        var tools       = Items.OfType<ToolActivityItemViewModel>().ToArray();
        var messages    = Items.OfType<MessageItemViewModel>().ToArray();
        var hasThinking = messages.Any(message => message.HasReasoning);
        var labels      = new List<string>();

        if (hasThinking) labels.Add("已思考");
        labels.AddRange(tools.Select(tool => tool.Title).Distinct(StringComparer.Ordinal));
        if (messages.Any(message => message.IsInterrupted)) labels.Add("已中断");

        SummaryText = labels.Count == 0 ? "执行过程" : string.Join(" · ", labels);
        OnPropertyChanged(nameof(LeadingIconStroke));
        OnPropertyChanged(nameof(LeadingIconFill));
    }
}
