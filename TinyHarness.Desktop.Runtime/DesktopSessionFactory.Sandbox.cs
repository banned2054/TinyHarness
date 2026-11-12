using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Desktop.Runtime;

public sealed partial class DesktopSessionFactory
{
    /// <summary>
    ///     <para>隔离测试可替换基线扫描；生产默认使用真实的可取消扫描。</para>
    ///     <para>Allows isolated tests to replace baseline scanning; production uses the real cancellable scan.</para>
    /// </summary>
    internal Func<CancellationToken, SandboxBaselineReadProtection?>? BaselineReadProtectionOverride { get; init; }

    /// <summary>
    ///     <para>只在异步准备时构建沙箱 Shell；成功后复用，失败与取消允许下次显式调用重试。</para>
    ///     <para>
    ///         Builds a sandbox shell only during asynchronous preparation; reuses success and allows retry after failure or
    ///         cancellation.
    ///     </para>
    /// </summary>
    private sealed class DeferredShellTool(ToolDefinition definition, Func<CancellationToken, ShellTool> initialize)
        : ITool
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private          ShellTool?    _tool;

        public ToolDefinition Definition => definition;

        public ToolPreparation Prepare(ChatToolCall call) =>
            (_tool ??
             throw new InvalidOperationException("The sandbox shell requires cancellable asynchronous preparation."))
           .Prepare(call);

        /// <summary>
        ///     <para>用当前运行的取消令牌在后台扫描；只有完整成功的组合才发布给后续调用。</para>
        ///     <para>Scans in the background with the current run token, publishing only a fully successful composition.</para>
        /// </summary>
        public async ValueTask<ToolPreparation> PrepareAsync(ChatToolCall call, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_tool is null)
                {
                    var tool = await Task.Run(() => initialize(cancellationToken), cancellationToken)
                                         .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    _tool = tool;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return _tool.Prepare(call);
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<ToolResult> ExecuteAsync(ToolPreparation preparation, CancellationToken cancellationToken) =>
            (_tool ?? throw new InvalidOperationException("The sandbox shell has not been prepared."))
           .ExecuteAsync(preparation, cancellationToken);
    }
}
