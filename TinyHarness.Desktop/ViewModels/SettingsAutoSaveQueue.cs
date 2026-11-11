namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>合并设置改动并串行保存；显式操作先等待已排队的保存。</para>
///     <para>Coalesces setting changes and serializes saves; explicit operations wait for queued saves.</para>
/// </summary>
internal sealed class SettingsAutoSaveQueue(Func<Task<bool>> save, Action<Exception> failed, TimeSpan delay = default)
{
    private readonly SemaphoreSlim _gate    = new(1, 1);
    private          Task          _pending = Task.CompletedTask;
    private          long          _revision;
    private          long          _savedRevision;

    internal bool HasChanges => _revision != _savedRevision;

    /// <summary>
    ///     <para>排队提交最新完整选择；未开始的旧版本自动合并。</para>
    ///     <para>Queues the latest complete selection and coalesces older saves that have not started.</para>
    /// </summary>
    internal void Request() => _pending = SaveQueuedAsync(_pending, ++_revision);

    /// <summary>
    ///     <para>等待当前已排队的保存完成。</para>
    ///     <para>Waits for the currently queued saves.</para>
    /// </summary>
    internal Task WaitAsync() => _pending;

    /// <summary>
    ///     <para>标记已加载的设置为基线，不触发写入。</para>
    ///     <para>Marks loaded settings as the baseline without writing.</para>
    /// </summary>
    internal void Reset() => _savedRevision = _revision;

    /// <summary>
    ///     <para>在排队保存之后串行执行加载或其他显式操作。</para>
    ///     <para>Serializes loading and other explicit operations after queued saves.</para>
    /// </summary>
    internal async Task ExecuteAsync(Func<Task> operation)
    {
        await _pending;
        await _gate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    ///     <para>显式提交当前编辑内容；失败交由调用方报告。</para>
    ///     <para>Explicitly commits current edits and lets the caller report failures.</para>
    /// </summary>
    internal Task SaveAsync() => ExecuteAsync(async () =>
    {
        var revision                     = _revision;
        if (await save()) _savedRevision = revision;
    });

    private async Task SaveQueuedAsync(Task previous, long revision)
    {
        await Task.Yield();
        await previous;
        if (revision != _revision) return;
        if (delay    > TimeSpan.Zero) await Task.Delay(delay);
        if (revision != _revision) return;
        await _gate.WaitAsync();
        try
        {
            if (revision != _revision) return;
            if (await save()) _savedRevision = revision;
        }
        catch (Exception exception)
        {
            failed(exception);
        }
        finally
        {
            _gate.Release();
        }
    }
}
