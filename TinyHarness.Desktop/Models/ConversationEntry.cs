namespace TinyHarness.Desktop.Models;

/// <summary>会话时间线条目；后端条目使用正 Seq，本地文件摘要使用负 Seq 并按轮末位置插入。</summary>
public abstract record ConversationEntry(long Seq, DateTimeOffset CreatedAt);

/// <summary>工具调用的当前状态。</summary>
public enum ToolActivityStatus
{
    /// <summary><para>已知未开始。</para><para>Known not started.</para></summary>
    NotStarted,
    /// <summary><para>结果未知，需人工检查。</para><para>Unknown outcome requiring inspection.</para></summary>
    Unknown,
    /// <summary><para>权限拒绝，未执行。</para><para>Denied before execution.</para></summary>
    Denied,
    /// <summary>已发起，尚未有结果事件。</summary>
    Running,

    /// <summary>结果为普通内容。</summary>
    Succeeded,

    /// <summary>结果带 isError 标记或错误身份。</summary>
    Failed
}

/// <summary>
///     edit/write 工具 result meta 中由 Host 在编辑现场计算的带上下文差异块
///     （official FsDiffMeta.diffs）：每个 hunk 一条，含 3 行上下文，无原文件行号。
///     oldText 为 null 表示纯插入。Path 是工具使用的路径（可能被桥接层相对化）。
/// </summary>
public sealed record ToolFileDiff(string Path, string? OldText, string NewText);

/// <summary>
///     一次工具调用（tool/call 与其 tool/result 的折叠视图）。
///     ArgumentsJson 是模型产出的原始参数 JSON 文本；ResultText 是结果内容块的文本拼接，
///     截断策略由显示层决定。Turn 是后端标注的轮次序号。MetaDiffs 是 result 事件
///     meta.diffs 携带的编辑现场差异（仅 edit/write 成功结果存在）。
/// </summary>
public sealed record ToolActivity(
    long                      Seq,
    string                    CallId,
    string                    Name,
    string?                   ArgumentsJson,
    ToolActivityStatus        Status,
    string?                   ResultText,
    string?                   ErrorReason,
    DateTimeOffset            CreatedAt,
    DateTimeOffset?           CompletedAt = null,
    long?                     Turn        = null,
    IReadOnlyList<ToolFileDiff>? MetaDiffs = null) : ConversationEntry(Seq, CreatedAt)
{
    public ToolActivity Settle(
        ToolActivityStatus status, string? resultText, string? errorReason, DateTimeOffset completedAt)
    {
        return this with
        {
            Status = status,
            ResultText = resultText,
            ErrorReason = errorReason,
            CompletedAt = completedAt
        };
    }
}

/// <summary>
///     turn/end 边界事件的占位条目（不可见）。一轮对话以 turn/end 收束：
///     界面据此把该轮的过程条目（中间助手消息、工具调用）折叠为过程组，
///     规则与参考 Web 客户端的 turn-process 投影一致。
///     <see cref="Reason" /> 是结束原因（completed/aborted/interrupted）；真实 Host 在
///     follow 快照尾部会为「开放中的轮」合成 interrupted 边界使窗口自洽，消费方需识别。
/// </summary>
public sealed record TurnBoundary(
    long           Seq,
    long           Turn,
    DateTimeOffset CreatedAt,
    string?        Reason = null) : ConversationEntry(Seq, CreatedAt);

/// <summary>
///     一轮文件改动的宣告。本地摘要使用负 Seq，与 Host 正序号隔离；
///     摘要和逐文件对比由 <c>IWorkspaceChangesService</c> 按会话和 Seq 读取。
/// </summary>
public sealed record WorkspaceChangesAnnouncement(
    long           Seq,
    long           Turn,
    DateTimeOffset CreatedAt) : ConversationEntry(Seq, CreatedAt);
