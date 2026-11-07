namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>审批输入条目的结构化来源；来源由程序赋予，条目文本不参与来源判定。</para>
///     <para>
///         The structured source of one approval-input entry; sources are assigned by the program and
///         never derived from the entry text.
///     </para>
/// </summary>
public enum ApprovalInputSource
{
    /// <summary>
    ///     <para>用户在会话中的原始发言。</para>
    ///     <para>The user's original wording in the session.</para>
    /// </summary>
    UserUtterance,

    /// <summary>
    ///     <para>已生效的运行期补充发言。</para>
    ///     <para>An applied run-scoped amendment.</para>
    /// </summary>
    EffectiveAmendment,

    /// <summary>
    ///     <para>已生效的撤销或收窄记录；其效力优先于较早授权。</para>
    ///     <para>An applied revocation or narrowing; it outweighs earlier grants.</para>
    /// </summary>
    Revocation,

    /// <summary>
    ///     <para>可信用户设置中的策略摘要。</para>
    ///     <para>A policy summary from trusted user settings.</para>
    /// </summary>
    TrustedPolicy,

    /// <summary>
    ///     <para>系统生成的说明（如窗口截断说明）；不可充当授权。</para>
    ///     <para>A system-generated notice (such as the window notice); never grants authority.</para>
    /// </summary>
    SystemNotice,

    /// <summary>
    ///     <para>待审核的调用描述；不可信，不能授予任何权限。</para>
    ///     <para>The pending invocation description; untrusted and never grants authority.</para>
    /// </summary>
    PendingOperation,

    /// <summary>
    ///     <para>有界脱敏的工具输出证据；不可信，仅提供事实参考。</para>
    ///     <para>Bounded redacted tool-output evidence; untrusted and factual only.</para>
    /// </summary>
    ToolEvidence,

    /// <summary>
    ///     <para>当前活动授权与相关拒绝历史。</para>
    ///     <para>Active grants and related denial history.</para>
    /// </summary>
    GrantHistory
}

/// <summary>
///     <para>一条结构化审批输入：来源标记、稳定记录标识与文本。来源判定只看 <see cref="Source" />。</para>
///     <para>
///         One structured approval input: source marker, stable record id and text. Source decisions read
///         only <see cref="Source" />.
///     </para>
/// </summary>
public sealed record ApprovalContextItem(ApprovalInputSource Source, string RecordId, string Text);

/// <summary>
///     <para>
///         一次审批的完整结构化输入：按时间排序的来源条目集合。条目文本只是内容，
///         来源与效力一律由 <see cref="ApprovalContextItem.Source" /> 决定。
///     </para>
///     <para>
///         The complete structured input of one approval: a time-ordered set of source entries. Entry text is
///         content only; source and authority are decided solely by <see cref="ApprovalContextItem.Source" />.
///     </para>
/// </summary>
public sealed record ApprovalContext(IReadOnlyList<ApprovalContextItem> Items);

/// <summary>
///     <para>审批上下文构建器的输入条目：发生时间、来源、稳定记录标识与文本。</para>
///     <para>A builder input entry: occurrence time, source, stable record id and text.</para>
/// </summary>
public sealed record ApprovalContextEntry(
    DateTimeOffset      Time,
    ApprovalInputSource Source,
    string              RecordId,
    string              Text);
