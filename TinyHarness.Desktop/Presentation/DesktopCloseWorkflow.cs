namespace TinyHarness.Desktop.Presentation;

/// <summary>
///     <para>只执行一次关闭清理；清理失败后允许再次确认退出，不重新释放资源。</para>
///     <para>Runs shutdown cleanup once; failed cleanup permits another exit confirmation without disposing again.</para>
/// </summary>
/// <remarks>
///     <para>由 UI 线程调用；暂不退出只保留窗口，不恢复已经关闭的应用资源。</para>
///     <para>Called on the UI thread; declining exit retains the window without restoring closed application resources.</para>
/// </remarks>
internal sealed class DesktopCloseWorkflow(Func<Task> cleanup, Func<Exception, Task<bool>> confirmExit, Action exit)
{
    private Task? _cleanup;
    private bool  _exited;
    private bool  _requesting;

    /// <summary>
    ///     <para>等待清理及必要的退出确认；重复请求不会并行清理或打开多个确认框。</para>
    ///     <para>Awaits cleanup and any required exit confirmation; duplicate requests cannot clean up or prompt concurrently.</para>
    /// </summary>
    internal async Task RequestCloseAsync()
    {
        if (_requesting || _exited) return;
        _requesting = true;
        try
        {
            _cleanup ??= CleanupAsync();
            try
            {
                await _cleanup;
            }
            catch (Exception exception)
            {
                if (!await confirmExit(exception)) return;
            }

            exit();
            _exited = true;
        }
        finally
        {
            _requesting = false;
        }
    }

    private async Task CleanupAsync() => await cleanup();
}
