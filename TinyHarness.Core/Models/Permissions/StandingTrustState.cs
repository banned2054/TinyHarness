namespace TinyHarness.Core.Models.Permissions;

/// <summary>
///     <para>
///         常设信任的三态：None 表示无可常设扩大的可信设置；Unconfirmed 表示检测到常设网络信任
///         或附加写根但用户尚未显式确认，联网与附加写根写一律逐次审批；Confirmed 表示用户已按
///         确认时的设置范围显式确认，这些设置作为显式持久授权运行。
///     </para>
///     <para>
///         The three standing-trust states: None means no trusted settings that could silently expand
///         the baseline; Unconfirmed means standing network trust or additional write roots were
///         detected without explicit confirmation, so networking and additional-root writes always
///         require per-call approval; Confirmed means the user explicitly confirmed exactly the
///         settings shown at confirmation time, and they run as explicit persistent authorization.
///     </para>
/// </summary>
public enum StandingTrustState
{
    /// <summary>
    ///     <para>无可常设扩大的可信设置（或沙箱未启用）；行为与历史缺省一致。</para>
    ///     <para>
    ///         No trusted settings that could silently expand the baseline (or the sandbox is disabled);
    ///         behavior matches the historical default.
    ///     </para>
    /// </summary>
    None,

    /// <summary>
    ///     <para>
    ///         检测到常设网络信任或附加写根但未确认：OS 注入门禁关闭，逐次审批，
    ///         审批文案注明"常设信任未确认"。
    ///     </para>
    ///     <para>
    ///         Standing network trust or additional write roots detected without confirmation: the OS
    ///         injection gate stays closed, every operation runs per-call approval, and approval text
    ///         notes that the standing trust is unconfirmed.
    ///     </para>
    /// </summary>
    Unconfirmed,

    /// <summary>
    ///     <para>
    ///         用户已显式确认且设置范围与确认时的范围修订一致：常设在线与附加读写根按显式持久
    ///         授权运行。
    ///     </para>
    ///     <para>
    ///         The user explicitly confirmed and the settings match the confirmed scope revision:
    ///         standing online access and additional read/write roots run as explicit persistent
    ///         authorization.
    ///     </para>
    /// </summary>
    Confirmed
}
