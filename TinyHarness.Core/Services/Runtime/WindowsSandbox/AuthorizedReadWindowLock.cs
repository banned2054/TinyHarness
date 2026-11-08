using System.Diagnostics;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     <para>
///         授权窗口的机器级串行门：一次只允许一个 TinyHarness 进程持有"施加授权 → 执行 → 恢复"
///         窗口。实现须使用 <c>Global\</c> 前缀命名内核对象，覆盖所有 TinyHarness 进程实例；
///         获取失败与超时都必须可区分。
///     </para>
///     <para>
///         The machine-level serialization gate for the authorization window: only one TinyHarness
///         process at a time may hold the "apply authorization → execute → restore" window.
///         Implementations must use a <c>Global\</c>-prefixed kernel object covering every TinyHarness
///         process instance; acquisition failures and timeouts must stay distinguishable.
///     </para>
/// </summary>
public interface IAuthorizedReadWindowLock
{
    /// <summary>
    ///     <para>
    ///         限时获取授权窗口；期限内获得返回持有句柄（Dispose 释放），超时返回 null。
    ///         等待被取消时抛出取消；内核对象不可用时抛出带上下文的异常。
    ///     </para>
    ///     <para>
    ///         Acquires the authorization window within the deadline: returns the held handle
    ///         (Dispose releases it) on success and null on timeout; waiting cancelled throws
    ///         cancellation and an unusable kernel object throws with context.
    ///     </para>
    /// </summary>
    Task<IAuthorizedReadWindowHandle?> TryAcquireAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>授权窗口持有句柄；Dispose 归还窗口。 A held authorization window; Dispose returns it.</summary>
public interface IAuthorizedReadWindowHandle : IDisposable;

/// <summary>
///     <para>
///         <see cref="IAuthorizedReadWindowLock" /> 的全局命名互斥体实现。Mutex 的线程亲和使释放
///         必须回到获取线程：获取在专用后台线程上同步完成并驻留等待释放信号，Dispose 经信号把
///         归还调度回该线程。前持有者进程死亡表现为放弃互斥体（AbandonedMutexException），此时
///         内核已把所有权交给本进程，按获得处理，窗口继续。
///     </para>
///     <para>
///         The global-named-mutex implementation of <see cref="IAuthorizedReadWindowLock" />. Mutex
///         thread affinity requires releasing on the acquiring thread: acquisition completes
///         synchronously on a dedicated background thread that stays resident waiting for the release
///         signal, and Dispose schedules the return onto that thread through the signal. A dead prior
///         holder surfaces as an abandoned mutex (AbandonedMutexException) whose ownership the kernel
///         already transferred to this process — treated as acquired, the window continues.
///     </para>
/// </summary>
public sealed class GlobalAuthorizedReadWindowLock : IAuthorizedReadWindowLock
{
    /// <summary>机器级互斥体名；Global 前缀覆盖所有 TinyHarness 进程实例。</summary>
    /// <summary>The machine-level mutex name; the Global prefix covers every TinyHarness process instance.</summary>
    public const string MutexName = @"Global\TinyHarness-Authorized-Read-Window";

    private sealed class Worker(
        TaskCompletionSource<IAuthorizedReadWindowHandle?> completion,
        Mutex                                              mutex,
        TimeSpan                                           timeout,
        CancellationToken                                  cancellationToken)
    {
        private readonly ManualResetEventSlim _releaseSignal = new(false);

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
                        // 前持有者进程死亡：内核已把所有权交给本线程，窗口按获得处理。
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

                completion.TrySetResult(new WindowHandle(this));
                // 释放必须回到获取线程（Mutex 线程亲和）；Dispose 发出信号后本线程归还并退出。
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
            // 双重 Dispose 或工作线程已退出时信号已释放；归还竞态由信号语义兜底。
            try
            {
                _releaseSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
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

        private sealed class WindowHandle(Worker worker) : IAuthorizedReadWindowHandle
        {
            public void Dispose() => worker.RequestRelease();
        }
    }

    /// <inheritdoc />
    public Task<IAuthorizedReadWindowHandle?> TryAcquireAsync(
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, MutexName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SystemException)
        {
            throw new
                InvalidOperationException($"The authorized-read window mutex '{MutexName}' cannot be created or opened: {ex.Message}; " +
                                          "the serialization gate is unavailable, refusing the authorized execution.",
                                          ex);
        }

        var completion = new TaskCompletionSource<IAuthorizedReadWindowHandle?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Worker(completion, mutex, timeout, cancellationToken);
        var thread = new Thread(worker.AcquireAndHold)
        {
            IsBackground = true,
            Name         = "TinyHarness-AuthorizedRead-Window"
        };
        thread.Start();
        return completion.Task;
    }
}
