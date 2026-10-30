using Avalonia.Media;
using LiveMarkdown.Avalonia;
using TinyHarness.Desktop.Models;
using TinyHarness.Desktop.Utils;

namespace TinyHarness.Desktop.ViewModels;

public sealed class MessageItemViewModel : ConversationItemViewModel
{
    /// <summary>思考摘要的截断长度（对齐参考实现的收起形态：首行 + 省略号）。</summary>
    private const int ReasoningSummaryLimit = 80;

    private string _content;
    private bool   _isInterrupted;
    private bool   _isReasoningExpanded;
    private bool   _isReasoningProjected;
    private bool   _isStreaming;

    private MessageItemViewModel? _processReasoningProjection;
    private string?               _reasoning;

    public MessageItemViewModel(ConversationMessage message) : base(message.Seq)
    {
        Id                     = message.Id;
        Role                   = message.Role;
        _content               = message.Content;
        _reasoning             = message.Reasoning;
        _isInterrupted         = message.IsInterrupted;
        CreatedAtText          = message.CreatedAt.ToLocalTime().ToString("HH:mm");
        MarkdownBuilder        = new ObservableStringBuilder(message.Content);
        ToggleReasoningCommand = new RelayCommand(() => IsReasoningExpanded = !IsReasoningExpanded);
    }

    private MessageItemViewModel(string id, string content) : base(-1)
    {
        Id                     = id;
        Role                   = MessageRole.Assistant;
        _content               = content;
        _reasoning             = null;
        CreatedAtText          = string.Empty;
        _isStreaming           = true;
        MarkdownBuilder        = new ObservableStringBuilder(content);
        ToggleReasoningCommand = new RelayCommand(() => IsReasoningExpanded = !IsReasoningExpanded);
    }

    private MessageItemViewModel(MessageItemViewModel source) : base(source.Seq)
    {
        Id                     = source.Id;
        Role                   = source.Role;
        _content               = string.Empty;
        _reasoning             = source._reasoning;
        _isReasoningExpanded   = source.IsReasoningExpanded;
        CreatedAtText          = source.CreatedAtText;
        MarkdownBuilder        = new ObservableStringBuilder(string.Empty);
        ToggleReasoningCommand = new RelayCommand(() => IsReasoningExpanded = !IsReasoningExpanded);
        IsInProcessGroup       = true;
    }

    public string Id { get; }

    public MessageRole Role { get; }

    /// <summary>
    ///     <para>思考（reasoning）全文；不算回复正文，以独立可折叠行展示；流式期间逐段追加。</para>
    ///     <para>
    ///         Full reasoning text; shown in its own collapsible row rather than the body, and appended live while
    ///         streaming.
    ///     </para>
    /// </summary>
    public string? Reasoning
    {
        get => _reasoning;
        private set
        {
            if (!SetProperty(ref _reasoning, value))
                return;

            OnPropertyChanged(nameof(HasReasoning));
            OnPropertyChanged(nameof(HasVisibleReasoning));
            OnPropertyChanged(nameof(ReasoningSummary));
            if (_processReasoningProjection is { } projection) projection.Reasoning = value;
        }
    }

    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    /// <summary>
    ///     已收束轮次的最终回复把 reasoning 投影进过程组后置位；
    ///     <see cref="Reasoning" /> 仍保留原始消息数据，仅控制本条目是否重复展示思考行。
    /// </summary>
    public bool IsReasoningProjected
    {
        get => _isReasoningProjected;
        private set
        {
            if (SetProperty(ref _isReasoningProjected, value)) OnPropertyChanged(nameof(HasVisibleReasoning));
        }
    }

    public bool HasVisibleReasoning => HasReasoning && !IsReasoningProjected;

    /// <summary>思考行图标（官方 ReasoningRow 的 IconThinkOutlineRegular，弧线+中心点）。</summary>
    public StreamGeometry ThinkingIconStroke => ToolCallText.ThinkingIconStroke;

    public StreamGeometry ThinkingIconFill => ToolCallText.ThinkingIconFill;

    public RelayCommand ToggleReasoningCommand { get; }

    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        private set => SetProperty(ref _isReasoningExpanded, value);
    }

    /// <summary>思考行收起时的首行摘要；去掉 Markdown 强调标记并截断。</summary>
    public string? ReasoningSummary
    {
        get
        {
            if (!HasReasoning) return null;

            var text      = Reasoning!.Replace("**", string.Empty, StringComparison.Ordinal);
            var firstLine = text.Split('\n', 2)[0].Trim();
            return firstLine.Length <= ReasoningSummaryLimit
                ? firstLine
                : $"{firstLine[..ReasoningSummaryLimit]}…";
        }
    }

    /// <summary>这条消息是被取消的部分回复（或流式尝试被放弃且无正式消息）。</summary>
    public bool IsInterrupted
    {
        get => _isInterrupted;
        private set
        {
            if (SetProperty(ref _isInterrupted, value)) OnPropertyChanged(nameof(HasStatusHint));
        }
    }

    public string Content
    {
        get => _content;
        private set
        {
            if (!SetProperty(ref _content, value)) return;
            OnPropertyChanged(nameof(HasResponseMetadata));
            OnPropertyChanged(nameof(HasProcessBody));
        }
    }

    /// <summary>
    ///     <para>正文非空时作为相邻思考与工具段之间的独立边界。</para>
    ///     <para>Indicates whether a non-empty body separates adjacent reasoning and tool segments.</para>
    /// </summary>
    public bool HasProcessBody => !string.IsNullOrWhiteSpace(Content);

    /// <summary>
    ///     <para>仅组外、正文非空的助手消息显示时间；过程条目与思考行不显示。</para>
    ///     <para>
    ///         Shows time only beside non-empty assistant bodies outside process groups; process entries and reasoning rows
    ///         never do.
    ///     </para>
    /// </summary>
    public bool HasResponseMetadata =>
        IsAssistantMessage && !IsInProcessGroup && !string.IsNullOrWhiteSpace(Content);

    public string CreatedAtText { get; }

    /// <summary>
    ///     助手气泡的 Markdown 渲染源。流式增量直接追加，渲染端按块增量更新；
    ///     与 <see cref="Content" /> 保持一致，仅在 UI 线程上修改。
    /// </summary>
    public ObservableStringBuilder MarkdownBuilder { get; }

    public bool IsStreaming
    {
        get => _isStreaming;
        private set
        {
            if (SetProperty(ref _isStreaming, value)) OnPropertyChanged(nameof(HasStatusHint));
        }
    }

    /// <summary>角色标签已从助手消息移除（思考行与工具行并列）；本行仅在流式或中断时保留。</summary>
    public bool HasStatusHint => IsStreaming || IsInterrupted;

    /// <summary>条目已折叠进过程组：组内中间消息不再显示各自时间戳。</summary>
    public bool IsInProcessGroup { get; private set; }

    public bool IsUserMessage => Role == MessageRole.User;

    public bool IsAssistantMessage => Role == MessageRole.Assistant;

    public bool IsSystemMessage => Role == MessageRole.System;

    /// <summary>过程组收纳条目时置位；最终回复留在组外，不受影响。</summary>
    public void MarkInProcessGroup()
    {
        if (IsInProcessGroup) return;

        IsInProcessGroup = true;
        OnPropertyChanged(nameof(IsInProcessGroup));
        OnPropertyChanged(nameof(HasResponseMetadata));
    }

    /// <summary>
    ///     <para>创建临时流式条目；正式消息到达后替换。</para>
    ///     <para>Creates a temporary streaming item, replaced once the recorded message arrives.</para>
    /// </summary>
    public static MessageItemViewModel CreateStreaming() =>
        new($"streaming-{Guid.NewGuid():N}", string.Empty);

    /// <summary>
    ///     供过程投影使用的稳定 reasoning-only 条目。正文消息仍保留独立展示，
    ///     原消息隐藏重复 reasoning；缓存副本使段折叠在重建投影时保持身份稳定。
    /// </summary>
    public MessageItemViewModel? GetProcessReasoningProjection()
    {
        if (!HasReasoning) return null;

        IsReasoningProjected = true;
        return _processReasoningProjection ??= new MessageItemViewModel(this);
    }

    /// <summary>
    ///     <para>沿用临时条目当前可见思考行的展开选择。</para>
    ///     <para>Inherits the expansion choice of the temporary item's currently displayed reasoning row.</para>
    /// </summary>
    internal void PreserveReasoningExpansion(MessageItemViewModel source)
    {
        IsReasoningExpanded = (source._processReasoningProjection ?? source).IsReasoningExpanded;
    }

    public void AppendText(string text)
    {
        if (text.Length <= 0) return;
        Content += text;
        MarkdownBuilder.Append(text);
    }

    /// <summary>
    ///     <para>向思考行追加一段流式推理文本；已投影进过程组的条目不再重复展示。</para>
    ///     <para>
    ///         Appends one live reasoning fragment to the thinking row; projected entries keep it hidden to avoid
    ///         duplication.
    ///     </para>
    /// </summary>
    public void AppendReasoning(string text)
    {
        if (text.Length <= 0) return;
        Reasoning += text;
    }

    /// <summary>流式尝试被放弃（取消/失败）且不会有正式消息：保留已生成内容并标注中断。</summary>
    public void MarkInterrupted()
    {
        IsStreaming   = false;
        IsInterrupted = true;
    }
}
