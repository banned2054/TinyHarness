using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         把按时间的来源条目组装成结构化审批输入：保留最近窗口、附加系统窗口说明、
///         约束证据条数与长度。来源标记由程序赋予并原样保留，条目文本不参与来源判定。
///     </para>
///     <para>
///         Assembles time-ordered source entries into the structured approval input: it keeps the recent window,
///         appends a system window notice and bounds evidence entries. Source markers are assigned by the
///         program and preserved verbatim; entry text never decides a source.
///     </para>
/// </summary>
public static class ApprovalContextBuilder
{
    /// <summary>窗口保留的最大条目数。Maximum entries kept in the window.</summary>
    public const int MaxEntries = 32;

    /// <summary>窗口保留的最大字符数（窗口说明不计入预算）。Maximum characters kept in the window (the notice is not budgeted).</summary>
    public const int MaxCharacters = 30_000;

    /// <summary>工具输出证据最多保留条数。Maximum tool-evidence entries kept.</summary>
    public const int MaxEvidenceEntries = 3;

    /// <summary>单条证据的最大字符数，超出截断并标记。Maximum characters per evidence entry; longer text is truncated.</summary>
    public const int MaxEvidenceCharacters = 500;

    /// <summary>系统窗口说明的稳定记录标识。The stable record id of the system window notice.</summary>
    public const string WindowNoticeRecordId = "system:window-notice";

    /// <summary>
    ///     <para>
    ///         从按时间的条目构建审批输入：证据条目先取最近 <see cref="MaxEvidenceEntries" /> 条；
    ///         其余条目自最新向前保留至多 <see cref="MaxEntries" /> 条、累计 <see cref="MaxCharacters" />
    ///         字符（首条超预算也保留，保证窗口非空）；丢弃更早条目时在末尾附加
    ///         <see cref="WindowNoticeRecordId" /> 的 SystemNotice 条目；证据逐条截断到
    ///         <see cref="MaxEvidenceCharacters" />。
    ///     </para>
    ///     <para>
    ///         Builds the context from time-ordered entries: evidence keeps only the newest
    ///         <see cref="MaxEvidenceEntries" /> entries; the remaining entries keep at most
    ///         <see cref="MaxEntries" /> entries totalling <see cref="MaxCharacters" /> characters from the
    ///         newest end (the first candidate is kept even over budget so the window is never empty); dropping
    ///         older entries appends a SystemNotice item with <see cref="WindowNoticeRecordId" /> last; evidence
    ///         text is truncated per entry to <see cref="MaxEvidenceCharacters" />.
    ///     </para>
    /// </summary>
    public static ApprovalContext Build(IReadOnlyList<ApprovalContextEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var ordered = entries.OrderBy(entry => entry.Time).ToArray();

        // 证据先行封顶：只保留最近 N 条，更早的证据不进入窗口也不触发窗口说明。
        var evidenceBudget  = MaxEvidenceEntries;
        var windowCandidates = new List<ApprovalContextEntry>(ordered.Length);
        for (var i = ordered.Length - 1; i >= 0; i--)
            if (ordered[i].Source == ApprovalInputSource.ToolEvidence)
            {
                if (evidenceBudget > 0) windowCandidates.Add(ordered[i]);
                evidenceBudget--;
            }
            else
            {
                windowCandidates.Add(ordered[i]);
            }

        windowCandidates.Reverse();

        var kept       = new List<ApprovalContextEntry>(windowCandidates.Count);
        var characters = 0;
        for (var i = windowCandidates.Count - 1; i >= 0 && kept.Count < MaxEntries; i--)
        {
            // 尚未保留任何条目时不做预算检查，保证窗口至少包含最新一条。
            if (kept.Count > 0 && characters + windowCandidates[i].Text.Length > MaxCharacters) break;
            kept.Add(windowCandidates[i]);
            characters += windowCandidates[i].Text.Length;
        }

        kept.Reverse();

        var items = new List<ApprovalContextItem>(kept.Count + 1);
        foreach (var entry in kept)
            items.Add(entry.Source == ApprovalInputSource.ToolEvidence
                ? new ApprovalContextItem(entry.Source, entry.RecordId, Truncate(entry.Text))
                : new ApprovalContextItem(entry.Source, entry.RecordId, entry.Text));

        if (kept.Count < windowCandidates.Count)
            items.Add(new ApprovalContextItem(
                          ApprovalInputSource.SystemNotice, WindowNoticeRecordId,
                          $"〔窗口说明〕以上为最近保留的 {kept.Count} 条审批上下文条目；另有 {windowCandidates.Count - kept.Count} 条更早记录未包含在内，未被包含的更早发言既不授予也不否定本次授权。"));
        return new ApprovalContext(items);
    }

    /// <summary>
    ///     <para>把单条证据截断到 <see cref="MaxEvidenceCharacters" />，截断处以省略号标记。</para>
    ///     <para>Truncates one evidence entry to <see cref="MaxEvidenceCharacters" />, marking the cut with an ellipsis.</para>
    /// </summary>
    private static string Truncate(string text) =>
        text.Length <= MaxEvidenceCharacters
            ? text
            : string.Concat(text.AsSpan(0, MaxEvidenceCharacters - 1), "…");
}
