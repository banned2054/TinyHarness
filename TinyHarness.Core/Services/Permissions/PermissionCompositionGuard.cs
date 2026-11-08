namespace TinyHarness.Core.Services.Permissions;

/// <summary>
///     <para>
///         组合授权指纹守卫：绑定组合期捕获的指纹与"当前指纹取值器"，由会话宿主在发送入口与
///         每次工具派发前复核。取值器返回 null、抛出异常或指纹不一致均按失配处理。
///     </para>
///     <para>
///         The composition authorization guard: binds the fingerprint captured at composition with a
///         "current fingerprint provider" that the session host re-checks at the send entry and before
///         every tool dispatch. A null return, a thrown exception, or a divergent fingerprint all count
///         as a mismatch.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         守卫是独立于审批模式切换的失效机制：失配时宿主撤销全部活动授权并置会话失效闩；
///         未注入守卫的宿主不做检查（既有测试与简单宿主保持可构造）。
///     </para>
///     <para>
///         The guard is an invalidation mechanism independent of approval-mode switching: on a mismatch
///         the host revokes every active grant and latches the session invalid. Hosts without an
///         injected guard skip the checks (existing tests and simple hosts stay constructible).
///     </para>
/// </remarks>
public sealed class PermissionCompositionGuard
{
    /// <summary>
    ///     <para>创建守卫；组合指纹与取值器都必须来自同一 <see cref="PermissionCompositionFingerprint" /> 计算。</para>
    ///     <para>
    ///         Creates the guard; the composition fingerprint and the provider must both come from the
    ///         same <see cref="PermissionCompositionFingerprint" /> computation.
    ///     </para>
    /// </summary>
    /// <param name="compositionFingerprint">
    ///     <para>组合点捕获的指纹基线；后续取值与其不一致即失配。</para>
    ///     <para>The fingerprint baseline captured at the composition point; divergence is a mismatch.</para>
    /// </param>
    /// <param name="currentFingerprint">
    ///     <para>当前指纹取值器；每次检查调用一次，null 返回或异常按失配处理。</para>
    ///     <para>
    ///         The current fingerprint provider, invoked once per check; a null return or an exception
    ///         counts as a mismatch.
    ///     </para>
    /// </param>
    public PermissionCompositionGuard(string compositionFingerprint, Func<string?> currentFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(compositionFingerprint);
        ArgumentNullException.ThrowIfNull(currentFingerprint);
        CompositionFingerprint = compositionFingerprint;
        CurrentFingerprint     = currentFingerprint;
    }

    /// <summary>
    ///     <para>组合期捕获的指纹基线。</para>
    ///     <para>The fingerprint baseline captured at composition time.</para>
    /// </summary>
    public string CompositionFingerprint { get; }

    /// <summary>
    ///     <para>当前指纹取值器；实现由组合端闭包提供（重读可信配置并按同一函数重算）。</para>
    ///     <para>
    ///         The current fingerprint provider, implemented by the composition side (rereads the trusted
    ///         config and recomputes through the same function).
    ///     </para>
    /// </summary>
    public Func<string?> CurrentFingerprint { get; }
}
