using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         拒读刷新的跨进程串行门：把 [读簿记 → 合并 → helper refresh → 簿记持久化] 的关键节按
///         沙箱 home 键控串行化，使后到的 refresh 必然重新读到先到者已持久化的簿记条目——helper
///         按主体全量同步（不在 payload 即撤销），无门时两步之间的跨进程窗口会让后到的旧 payload
///         撤销另一端刚同步的、共享主体依赖的拒读条目。backend 的 capability SID 解析（cap_sid
///         的 get-or-create 与整表写回）也在同一把门内进行，与另一端 host 解析及门内 helper 的
///         表写互斥；解析不持门覆盖 runner 启动。
///     </para>
///     <para>
///         The cross-process serialization gate for deny-read refreshes: the critical section of
///         [read bookkeeping → merge → helper refresh → bookkeeping persistence] is serialized per
///         sandbox home, so a later refresh necessarily re-reads what the earlier one persisted —
///         the helper syncs wholesale per principal (revoking anything absent from the payload),
///         and without the gate the cross-process window between the two steps would let a stale
///         later payload revoke the deny entries the other side just synced for a shared principal.
///         The backend's capability-SID resolution (the cap_sid get-or-create plus whole-table
///         write-back) also runs inside the same gate, mutually exclusive with the other host's
///         resolution and the helper's in-gate table writes; the resolution never holds the gate
///         across the runner launch.
///     </para>
/// </summary>
public interface ISandboxDenyReadRefreshGate
{
    /// <summary>
    ///     <para>
    ///         限时获取按 <paramref name="sandboxHome" /> 键控的拒读刷新门：期限内获得返回持有句柄
    ///         （Dispose 或 helper 退出确认后释放），超时返回 null，由调用方 fail-closed（后端
    ///         拒绝命令启动，授权协调器转入禁用）；等待被取消抛出取消；内核对象不可用时抛出带上下文
    ///         的异常——门不可用绝不放行无门 refresh。
    ///     </para>
    ///     <para>
    ///         Acquires the deny-read refresh gate keyed by <paramref name="sandboxHome" /> within
    ///         the deadline: returns the held handle (released on Dispose or after the helper exit
    ///         confirmation) on success and null on timeout, where callers fail closed (the backend
    ///         refuses the command, the authorization coordinator disables the subject); waiting
    ///         cancelled throws cancellation, and an unusable kernel object throws with context —
    ///         an unavailable gate never lets an ungated refresh through.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <para>
    ///         home 目录身份无法确定（最终路径解析失败）时同步抛出：按 home 键控的门无法安全建立，
    ///         本 refresh 被拒绝，以免无门 refresh 撤销另一端依赖的拒读条目——backend 通用 catch
    ///         把它包成 <c>ProcessExecutionStartException</c>（命令未启动），授权协调器转入禁用语义。
    ///     </para>
    ///     <para>
    ///         Thrown synchronously when the home's directory identity cannot be determined
    ///         (final-path resolution failed): the home-keyed gate cannot be established safely and
    ///         this refresh is refused so an ungated refresh cannot revoke deny entries the other
    ///         side relies on — the backend's general catch wraps it into
    ///         <c>ProcessExecutionStartException</c> with the command not started, and the
    ///         authorization coordinator transitions to its disabled semantics.
    ///     </para>
    /// </exception>
    Task<ISandboxDenyReadRefreshGateHandle?> TryAcquireAsync(
        string sandboxHome, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
///     <para>
///         拒读刷新门持有句柄：未转移所有权时 Dispose 立即归还门；经
///         <see cref="ReleaseWhenConfirmed" /> 转移后，归还时机由 helper 退出确认决定。
///     </para>
///     <para>
///         A held deny-read refresh gate: Dispose returns the gate immediately while ownership has
///         not been transferred; after a transfer through <see cref="ReleaseWhenConfirmed" />, the
///         helper exit confirmation decides when the gate is returned.
///     </para>
/// </summary>
public interface ISandboxDenyReadRefreshGateHandle : IDisposable
{
    /// <summary>
    ///     <para>
    ///         不立即归还门：等 <paramref name="helperExitConfirmation" /> 完成（true=helper 进程
    ///         已确认退出）后才归还；完成值 false、任务取消或失败时保持持有（fail-closed）并记
    ///         诊断事件。调用后句柄所有权视为已转移，此后 Dispose 是 no-op——防止调用方 finally
    ///         双释放把门提前放掉。保持持有意味着门仍被占：除其他确认途径（如本进程退出后另一端
    ///         按放弃互斥体获得）外，后续按 home 获取将超时 fail-closed。
    ///     </para>
    ///     <para>
    ///         Does not return the gate immediately: waits for
    ///         <paramref name="helperExitConfirmation" /> to complete (true = the helper process has
    ///         confirmed exit) before returning it; a false result, cancellation, or a faulted task
    ///         keeps the gate held (fail-closed) with a diagnostic event recorded. Ownership of the
    ///         handle transfers on the call, so Dispose afterwards is a no-op — a caller's finally
    ///         double-release can no longer drop the gate early. Holding means the gate stays
    ///         occupied: apart from other confirmation paths (such as the abandoned-mutex handling
    ///         on the other side once this process exits), later acquisition for the home times out
    ///         and fails closed.
    ///     </para>
    /// </summary>
    /// <param name="helperExitConfirmation">
    ///     <para>helper 退出确认任务：true 表示 helper 进程已确认终止、不再写簿记。</para>
    ///     <para>
    ///         The helper exit confirmation task: true means the helper process is confirmed
    ///         terminated and no longer writes bookkeeping.
    ///     </para>
    /// </param>
    void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation);
}

/// <summary>
///     <para>
///         <see cref="ISandboxDenyReadRefreshGate" /> 的全局命名互斥体实现，复用授权窗口锁的
///         worker 模式：Mutex 线程亲和使释放必须回到获取线程——获取在专用后台线程上同步完成并
///         驻留等待释放信号，Dispose 经信号把归还调度回该线程；前持有者进程死亡表现为放弃互斥体
///         （AbandonedMutexException），内核已把所有权转移给本进程，按获得处理。
///     </para>
///     <para>
///         The global-named-mutex implementation of <see cref="ISandboxDenyReadRefreshGate" />,
///         reusing the authorization window lock's worker pattern: Mutex thread affinity requires
///         releasing on the acquiring thread — acquisition completes synchronously on a dedicated
///         background thread that stays resident waiting for the release signal, and Dispose
///         schedules the return onto that thread through the signal; a dead prior holder surfaces
///         as an abandoned mutex (AbandonedMutexException) whose ownership the kernel already
///         transferred to this process, treated as acquired.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         互斥体名为 <c>Global\TinyHarness-DenyRead-Refresh-&lt;hash&gt;</c>：const 前缀加按 home
///         归一化键（<see cref="MutexNameForHome" />：先经最终路径解析收敛别名，再 GetFullPath
///         绝对化、去尾部分隔符、ToUpperInvariant，对 UTF-8 字节取 SHA-256 前 8 字节的 16 位大写
///         十六进制）计算的后缀；同机两端解析出同一 home 即同名，哈希不落明文路径。默认获取期限
///         须覆盖另一端最坏 refresh 时长（调用器默认 60s 超时 + 余量）；释放时机依赖调用器契约——
///         Dispose 在 RefreshAsync 返回（含失败/取消/超时路径，helper 进程已确认终止、不再写簿记）
///         后归还，或经 <see cref="ISandboxDenyReadRefreshGateHandle.ReleaseWhenConfirmed" /> 推迟
///         到 helper 退出确认结果为 true。
///     </para>
///     <para>
///         The mutex is named <c>Global\TinyHarness-DenyRead-Refresh-&lt;hash&gt;</c>: a const
///         prefix plus a suffix computed from the normalized home key
///         (<see cref="MutexNameForHome" />: final-path resolution converging aliases first, then
///         GetFullPath absolutization, trailing-separator trim, ToUpperInvariant, and the first 8
///         bytes of the SHA-256 of the UTF-8 bytes as 16 uppercase hex characters); two processes
///         resolving the same home share the name, and the hash never carries the plaintext path.
///         The default acquisition deadline must cover the other side's worst refresh duration (the
///         invoker's default 60s timeout plus margin); the release timing relies on the invoker
///         contract — Dispose returns the gate once RefreshAsync returns (including the failure,
///         cancellation, and timeout paths, with the helper process confirmed terminated and no
///         longer writing bookkeeping), or
///         <see cref="ISandboxDenyReadRefreshGateHandle.ReleaseWhenConfirmed" /> defers the return
///         until the helper exit confirmation completes with true.
///     </para>
///     <para>
///         键控身份是目录最终落点而非词法形态：junction 别名与原路径取得同一互斥身份，不同实际
///         home 各自独立。同一进程内锁键、簿记路径（<see cref="SandboxDenyReadBookkeeping.StateFilePath" />）
///         与 helper 参数都派生自同一 <c>components.SandboxHome</c> 字符串；跨进程的别名形态经
///         最终路径解析收敛到同一实际目录——锁对应的目录身份与读取簿记、执行 refresh 的目录一致。
///         最终路径解析失败抛出 <see cref="InvalidOperationException" /> 并从
///         <see cref="TryAcquireAsync" /> 传播到调用方：backend 通用 catch 包成
///         <c>ProcessExecutionStartException</c>（命令未启动），授权协调器转入禁用语义。
///     </para>
///     <para>
///         The keyed identity is the directory's final location, not a lexical form: junction
///         aliases and the original path obtain the same mutex identity, and distinct actual homes
///         stay independent. Within one process the lock key, the bookkeeping path
///         (<see cref="SandboxDenyReadBookkeeping.StateFilePath" />), and the helper arguments all
///         derive from the same <c>components.SandboxHome</c> string; cross-process alias forms
///         converge onto the same actual directory through final-path resolution — the directory
///         identity the lock stands for matches the directory the bookkeeping is read from and the
///         refresh executes against. A failed final-path resolution throws
///         <see cref="InvalidOperationException" /> propagating out of <see cref="TryAcquireAsync" />:
///         the backend's general catch wraps it into <c>ProcessExecutionStartException</c> with the
///         command not started, and the authorization coordinator transitions to its disabled
///         semantics.
///     </para>
/// </remarks>
public sealed class GlobalDenyReadRefreshGate : ISandboxDenyReadRefreshGate
{
    /// <summary>
    ///     <para>
    ///         缺省获取期限：须覆盖另一端最坏 refresh 时长（调用器默认 60s 超时 + 余量）；
    ///         超时返回 null，由调用方 fail-closed。
    ///     </para>
    ///     <para>
    ///         The default acquisition deadline: must cover the other side's worst refresh duration
    ///         (the invoker's default 60s timeout plus margin); a timeout returns null and the
    ///         caller fails closed.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     <para>互斥体名前缀；后缀为按 home 归一化键计算的哈希（规则见类型 remarks）。</para>
    ///     <para>
    ///         The mutex-name prefix; the suffix is the hash computed from the normalized home key
    ///         (see the type remarks for the rule).
    ///     </para>
    /// </summary>
    private const string MutexNamePrefix = @"Global\TinyHarness-DenyRead-Refresh-";

    /// <summary>
    ///     <para>
    ///         计算按 home 键控的互斥体名：先解析目录最终落点（junction/8.3 别名与原路径收敛到
    ///         同一实际目录），再按类型 remarks 的归一化与哈希规则计算后缀；名称由测试直接断言。
    ///     </para>
    ///     <para>
    ///         Computes the home-keyed mutex name: resolves the directory's final location first
    ///         (junction/8.3 aliases converge with the original path onto the same actual
    ///         directory), then computes the suffix per the normalization and hash rules in the
    ///         type remarks; asserted directly by tests.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     <para>
    ///         目录身份无法确定（最终路径解析失败：home 不存在、无权限、打开失败或非 Windows）：
    ///         按 home 键控的拒读刷新门无法安全建立，调用方必须拒绝本次 refresh，以免无门 refresh
    ///         的旧 payload 撤销另一端依赖的拒读条目。异常消息包含 home 原路径。
    ///     </para>
    ///     <para>
    ///         The directory identity cannot be determined (final-path resolution failed: the home
    ///         is missing, inaccessible, its open fails, or the platform is not Windows): the
    ///         home-keyed deny-read refresh gate cannot be established safely, and callers must
    ///         refuse this refresh so an ungated refresh cannot revoke deny entries the other side
    ///         relies on with a stale payload. The message contains the original home path.
    ///     </para>
    /// </exception>
    internal static string MutexNameForHome(string sandboxHome)
    {
        // 同一个实际 home 必须共用一把锁：junction/8.3 别名先收敛到最终落点再键控；解析失败
        // （不存在/无权限/非 Windows）无法建立安全键控，拒绝 refresh 而不是放行无门 refresh。
        var finalPath = SandboxFinalPathResolver.TryGetFinalPath(sandboxHome) ??
                        throw new
                            InvalidOperationException($"The sandbox home '{sandboxHome}' cannot be pinned to a directory " +
                                                      "identity (final-path resolution failed: the path may be missing, " +
                                                      "inaccessible, or the platform may not support resolution); " +
                                                      "the home-keyed deny-read refresh gate cannot be established safely, " +
                                                      "so this refresh is refused — an ungated refresh could revoke deny " +
                                                      "entries the other side relies on with a stale payload.");

        // 最终落点已是绝对规范形态，GetFullPath 幂等；沿用既有归一化（去尾部分隔符 + 大写不变式）与哈希。
        var normalized = Path.GetFullPath(finalPath)
                         .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return MutexNamePrefix + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private sealed class Worker(
        TaskCompletionSource<ISandboxDenyReadRefreshGateHandle?> completion,
        Mutex                                                    mutex,
        TimeSpan                                                 timeout,
        CancellationToken                                        cancellationToken)
    {
        private readonly ManualResetEventSlim _releaseSignal = new(false);

        // 所有权标志：0=未转移（Dispose 即归还），1=已转移给 ReleaseWhenConfirmed（Dispose 为 no-op）。
        private int _ownershipTransferred;

        public void AcquireAndHold()
        {
            try
            {
                // WaitHandle 无取消重载：以固定短窗口轮询，兼顾等待有界与取消可见。
                var stopwatch = Stopwatch.StartNew();
                var owned     = false;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        owned = mutex.WaitOne(PollInterval);
                    }
                    catch (AbandonedMutexException)
                    {
                        // 前持有者进程死亡：内核已把所有权交给本线程，门按获得处理。
                        owned = true;
                    }

                    if (owned) break;
                    if (stopwatch.Elapsed >= timeout) break;

                    Thread.Sleep(PollInterval);
                }

                if (!owned)
                {
                    completion.TrySetResult(null);
                    return;
                }

                completion.TrySetResult(new GateHandle(this));
                // 释放必须回到获取线程（Mutex 线程亲和）；信号置位后本线程归还并退出。
                _releaseSignal.Wait();
                TryRelease();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                _releaseSignal.Dispose();
            }
        }

        public void RequestRelease()
        {
            // Dispose 路径：所有权已转移给 ReleaseWhenConfirmed 后不再直接归还，归还时机由确认任务决定。
            if (Volatile.Read(ref _ownershipTransferred) != 0) return;
            SignalRelease();
        }

        /// <summary>置位释放信号：双重释放或工作线程已退出时信号可能已释放，归还竞态由信号语义兜底。 Sets the release signal: double releases or an exited worker may find it disposed, with the return race covered by the signal semantics.</summary>
        private void SignalRelease()
        {
            try
            {
                _releaseSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>
        ///     <para>
        ///         把句柄所有权原子转移给 helper 退出确认：此后 Dispose/RequestRelease 直接 no-op；
        ///         重复调用以首次转移为准，不重复挂确认续体。
        ///     </para>
        ///     <para>
        ///         Atomically transfers handle ownership to the helper exit confirmation: Dispose and
        ///         RequestRelease become no-ops afterwards; a repeated call keeps the first transfer
        ///         and does not attach a second confirmation continuation.
        ///     </para>
        /// </summary>
        public void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation)
        {
            if (Interlocked.Exchange(ref _ownershipTransferred, 1) != 0) return;

            // 已完成任务同步处理，不等 continuation 调度；未完成的挂续体，完成时按结果决定释放或保持持有。
            if (helperExitConfirmation.IsCompleted)
            {
                HandleConfirmation(helperExitConfirmation);
                return;
            }

            helperExitConfirmation.ContinueWith(HandleConfirmation,
                                                CancellationToken.None,
                                                TaskContinuationOptions.ExecuteSynchronously,
                                                TaskScheduler.Default);
        }

        private void HandleConfirmation(Task<bool> confirmation)
        {
            // true=helper 进程已确认退出（不再写簿记）→ 直接置位释放信号归还门；确认路径是转移后
            // 的所有者，不受 Dispose 的所有权标志检查约束。
            if (confirmation.Status == TaskStatus.RanToCompletion && confirmation.Result)
            {
                SignalRelease();
                return;
            }

            // false/取消/失败都不是确认退出：fail-closed 保持持有——后续按 home 获取失败即可见，
            // 直到有其他确认途径（如本进程退出后另一端按放弃互斥体获得）。诊断只记结果类别与
            // 异常类型名，不携带路径或异常消息。
            var reason = confirmation.Status switch
            {
                TaskStatus.Faulted  => $"faulted:{confirmation.Exception?.InnerException?.GetType().Name ?? nameof(Exception)}",
                TaskStatus.Canceled => "canceled",
                _                   => "result-false"
            };
            SandboxDiagnosticLog.Event("deny-read-refresh-gate",
                                       $"release-withheld reason={reason} gate=held-fail-closed");
        }

        /// <summary>轮询短窗：保持取消可见的固定等待粒度。 The polling slice: the fixed wait granularity keeping cancellation visible.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

        private void TryRelease()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 互斥体已被放弃或所有权不在本线程（竞态下的防御），内核状态仍一致。
            }
        }

        private sealed class GateHandle(Worker worker) : ISandboxDenyReadRefreshGateHandle
        {
            public void Dispose() => worker.RequestRelease();

            public void ReleaseWhenConfirmed(Task<bool> helperExitConfirmation) =>
                worker.ReleaseWhenConfirmed(helperExitConfirmation);
        }
    }

    /// <inheritdoc />
    public Task<ISandboxDenyReadRefreshGateHandle?> TryAcquireAsync(
        string sandboxHome, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var mutexName = MutexNameForHome(sandboxHome);
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, mutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            throw new
                InvalidOperationException($"The deny-read refresh mutex '{mutexName}' cannot be created or opened: {ex.Message}; " +
                                          "the cross-process gate is unavailable, refusing this refresh so a stale payload can never " +
                                          "revoke deny entries another side relies on.",
                                          ex);
        }

        var completion = new TaskCompletionSource<ISandboxDenyReadRefreshGateHandle?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Worker(completion, mutex, timeout, cancellationToken);
        var thread = new Thread(worker.AcquireAndHold)
        {
            IsBackground = true,
            Name         = "TinyHarness-DenyRead-Refresh"
        };
        thread.Start();
        return completion.Task;
    }
}
