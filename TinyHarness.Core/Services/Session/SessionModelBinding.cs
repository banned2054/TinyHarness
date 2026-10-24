using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Core.Services.Session;

/// <summary>
///     <para>选择快照、模型客户端与请求参数的整体运行时绑定，持有明确的客户端资源所有权。</para>
///     <para>One runtime binding of a selection snapshot, the model client, and request parameters, holding explicit client ownership.</para>
/// </summary>
/// <remarks>
///     <para>
///         候选准备期间归调用方；宿主确认接受（含 no-op 短路）后归宿主，由宿主在替换或关闭时释放；
///         拒绝、取消或提交失败以异常返回时所有权不变，仍由调用方释放一次。
///         同一客户端承载仅改强度的新选择时，以借用方式构造候选并在绑定更替间移交所有权，绝不释放在用实例。
///     </para>
///     <para>
///         Candidates belong to the caller until the host accepts them (including the no-op short-circuit); the host
///         then owns the binding and releases it on replacement or close. Rejections, cancellation, or failed commits
///         return by exception and leave disposal with the caller, exactly once. When one client carries an
///         effort-only new selection, the candidate borrows it and ownership moves across the binding swap — the
///         live instance is never released.
///     </para>
///     <para>
///         绑定只包含可信配置派生的请求事实；凭据解析与客户端构造留在组合层，Core 不扫描 profile 或探测服务。
///     </para>
///     <para>
///         Bindings carry only request facts derived from trusted configuration; credential resolution and client
///         construction stay in the composition layer — Core never scans profiles or probes services.
///     </para>
/// </remarks>
public sealed class SessionModelBinding : IDisposable
{
    private bool _ownsClient;
    private int _released;

    /// <summary>
    ///     <para>构造一个候选绑定；<paramref name="ownsClient" /> 为 false 时仅借用客户端，不承担释放责任。</para>
    ///     <para>Creates a candidate binding; a false <paramref name="ownsClient" /> borrows the client without disposal responsibility.</para>
    /// </summary>
    public SessionModelBinding(
        SessionModelIdentity model, IChatCompletionClient client, ContextOptions? context,
        ModelReasoningEffort? reasoningEffort, string? credentialRoute = null, string? profileHint = null,
        IReadOnlyList<ModelReasoningEffort>? reasoningCapabilities = null, bool ownsClient = true)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(client);
        if (credentialRoute is not null && string.IsNullOrWhiteSpace(credentialRoute))
            throw new ArgumentException("The credential route must be a non-empty string when provided.",
                                        nameof(credentialRoute));
        if (profileHint is not null && string.IsNullOrWhiteSpace(profileHint))
            throw new ArgumentException("The profile hint must be a non-empty string when provided.",
                                        nameof(profileHint));
        Model                 = model;
        Client                = client;
        Context               = context;
        ReasoningEffort       = reasoningEffort;
        CredentialRoute       = credentialRoute;
        ProfileHint           = profileHint;
        ReasoningCapabilities = reasoningCapabilities?.ToArray();
        _ownsClient           = ownsClient;
    }

    public SessionModelIdentity                 Model                 { get; }
    public IChatCompletionClient                Client                { get; }
    public ContextOptions?                      Context               { get; }
    public ModelReasoningEffort?                ReasoningEffort       { get; }
    public string?                              CredentialRoute       { get; }
    public string?                              ProfileHint           { get; }
    public IReadOnlyList<ModelReasoningEffort>? ReasoningCapabilities { get; }

    /// <summary>是否已结束生命周期；释放幂等，重复释放不再次触碰客户端。Whether the binding is spent; disposal is idempotent.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>当前是否持有客户端释放责任。Whether this binding currently owns client disposal.</summary>
    internal bool OwnsClient => _ownsClient;

    /// <summary>
    ///     <para>结束生命周期并按所有权释放客户端一次；借用客户端时不释放。释放异常由调用方报告。</para>
    ///     <para>Ends the lifetime and releases the client once when owned; borrowed clients are not released. Release failures propagate to the caller for reporting.</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        if (!_ownsClient) return;
        (Client as IDisposable)?.Dispose();
    }

    /// <summary>
    ///     <para>
    ///         后继绑定复用同一客户端时移交所有权：本绑定不再释放该客户端，也不得被再次释放。
    ///         借用的客户端不因移交升格为拥有，保持其原所有者的进程生命周期约定。
    ///     </para>
    ///     <para>
    ///         Moves ownership to a successor reusing the same client: this binding stops owning it and is never
    ///         released twice. A borrowed client never gains ownership through the transfer and keeps its original
    ///         owner's process-lifetime convention.
    ///     </para>
    /// </summary>
    internal bool TryTransferClientOwnershipTo(SessionModelBinding successor)
    {
        ArgumentNullException.ThrowIfNull(successor);
        if (!ReferenceEquals(Client, successor.Client)) return false;
        var ownedByThis   = _ownsClient;
        _ownsClient       = false;
        Interlocked.Exchange(ref _released, 1);
        successor._ownsClient |= ownedByThis;
        return true;
    }

    /// <summary>
    ///     <para>不释放客户端地结束生命周期；用于借用当前客户端的候选未被采纳时丢弃候选。</para>
    ///     <para>Ends the lifetime without touching the client; drops a borrowed, unadopted candidate.</para>
    /// </summary>
    internal void MarkReleasedWithoutDisposal() => Interlocked.Exchange(ref _released, 1);
}
