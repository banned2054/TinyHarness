using Avalonia.Media;
using TinyHarness.Desktop.Models;
using TinyHarness.Desktop.Utils;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     工具调用条目：折叠态为「口语化标题 + 参数摘要 + 状态」单行（文案对齐
///     官方 DSH 客户端推导规则），展开态显示参数与结果文本；运行中条目在
///     结果事件到达后原地落定。
/// </summary>
public sealed class ToolActivityItemViewModel : ConversationItemViewModel
{
    /// <summary>结果文本的截断上限；完整文本不再重复保存，避免长会话双份大字符串。</summary>
    private const int ResultPreviewLimit = 2000;

    /// <summary>todo_write 的 diff 摘要段（新增/更新/移除），发起时由组装器定格；其余工具为 null。</summary>
    private readonly string? _todoDiffSummary;

    private string? _errorReason;
    private bool    _isExpanded;
    private string? _resultText;

    private ToolActivityStatus _status;

    public ToolActivityItemViewModel(ToolActivity activity, string? todoDiffSummary = null) : base(activity.Seq)
    {
        CallId               = activity.CallId;
        Name                 = activity.Name;
        ArgumentsText        = activity.ArgumentsJson;
        _status              = activity.Status;
        _resultText          = activity.ResultText;
        _errorReason         = activity.ErrorReason;
        _todoDiffSummary     = todoDiffSummary;
        CreatedAtText        = activity.CreatedAt.ToLocalTime().ToString("HH:mm");
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string CallId { get; }

    public string Name { get; private set; }

    public string? ArgumentsText { get; private set; }

    public string CreatedAtText { get; }

    public ToolActivityStatus Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsSucceeded));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(ShowStatusText));
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "工具调用" : Name;

    /// <summary>折叠行口语化标题：已知工具映射中文动词，未知名回退通用词。</summary>
    public string Title => ToolCallText.GetTitle(Name);

    /// <summary>
    ///     折叠行摘要：失败时显示错误首行，否则按工具变体从参数推导；
    ///     todo_write 在其后拼 diff 段（对齐官方 summarySuffix 的拼接位）。
    /// </summary>
    public string SummaryText => IsFailed && !string.IsNullOrWhiteSpace(ErrorReason)
        ? ToolCallText.FirstLine(ErrorReason)
        : JoinWithDiff(ToolCallText.GetSummary(Name, ArgumentsText), _todoDiffSummary);

    public string StatusText => ToolCallText.GetStatusText(Name, Status);

    public bool IsRunning => Status == ToolActivityStatus.Running;

    public bool IsFailed => Status == ToolActivityStatus.Failed;

    public bool IsSucceeded => Status == ToolActivityStatus.Succeeded;

    public string? ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public string? ErrorReason
    {
        get => _errorReason;
        private set
        {
            if (!SetProperty(ref _errorReason, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorReason) || IsFailed;

    /// <summary>条目已折叠进过程组：组内隐藏成功状态尾注，异常与未定状态保留可见。</summary>
    public bool IsInProcessGroup { get; private set; }

    /// <summary>
    ///     <para>折叠行是否显示状态尾注：顶层恒显示；过程组内仅成功收口后隐藏，异常与未定状态保留。</para>
    ///     <para>
    ///         Whether the collapsed row keeps its status tail: always at top level; inside a process group only settled
    ///         successes hide it, so uncertain or blocked results stay visible.
    ///     </para>
    /// </summary>
    public bool ShowStatusText => !IsInProcessGroup || Status is not ToolActivityStatus.Succeeded;

    /// <summary>结果文本的截断展示，仅在展开时渲染。</summary>
    public string ResultPreview => Truncate(ResultText);

    public string ArgumentsPreview => Truncate(ArgumentsText);

    public bool HasDetails =>
        !string.IsNullOrWhiteSpace(ArgumentsText) ||
        !string.IsNullOrWhiteSpace(ResultText)    ||
        HasError;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand ToggleDetailsCommand { get; }

    /// <summary>折叠行图标描边部件（对齐官方 VARIANT_ICONS，未知工具回退四角星）。</summary>
    public StreamGeometry IconStroke => ToolCallText.GetIconStroke(Name);

    /// <summary>折叠行图标填充部件；纯描边图标为 null（Data 空即不渲染）。</summary>
    public StreamGeometry? IconFill => ToolCallText.GetIconFill(Name);

    /// <summary>过程组收纳条目时置位；运行中的顶层工具卡片不受影响。</summary>
    public void MarkInProcessGroup()
    {
        if (IsInProcessGroup) return;

        IsInProcessGroup = true;
        OnPropertyChanged(nameof(IsInProcessGroup));
        OnPropertyChanged(nameof(ShowStatusText));
    }

    private static string JoinWithDiff(string summary, string? diffSummary)
    {
        if (string.IsNullOrEmpty(diffSummary)) return summary;
        return string.IsNullOrEmpty(summary) ? diffSummary : $"{summary} · {diffSummary}";
    }

    /// <summary>结果事件到达：保持发起位置与参数，落定状态与结果。</summary>
    public void Settle(ToolActivity settled)
    {
        if (!string.IsNullOrWhiteSpace(settled.Name))
        {
            Name = settled.Name;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(IconStroke));
            OnPropertyChanged(nameof(IconFill));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SummaryText));
        }

        if (!string.IsNullOrWhiteSpace(settled.ArgumentsJson))
        {
            ArgumentsText = settled.ArgumentsJson;
            OnPropertyChanged(nameof(ArgumentsText));
            OnPropertyChanged(nameof(SummaryText));
        }

        Status      = settled.Status;
        ResultText  = settled.ResultText;
        ErrorReason = settled.ErrorReason;
        OnPropertyChanged(nameof(ResultPreview));
        OnPropertyChanged(nameof(ArgumentsPreview));
        OnPropertyChanged(nameof(HasDetails));
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= ResultPreviewLimit) return text ?? string.Empty;

        return $"{text[..ResultPreviewLimit]}…（已截断）";
    }
}
