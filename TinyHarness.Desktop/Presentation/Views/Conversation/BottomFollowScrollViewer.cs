using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

/// <summary>
///     <para>在底部时跟随内容增长；向上滚动后保留阅读位置，回到底部时恢复跟随。</para>
///     <para>
///         Follows growing content at the bottom; preserves the reading position after scrolling up and resumes at the
///         bottom.
///     </para>
/// </summary>
public sealed class BottomFollowScrollViewer : ScrollViewer
{
    private const double               BottomTolerance = 1;
    private       bool                 _isFollowing    = true;
    private       DispatcherOperation? _pendingFollow;

    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OffsetProperty)
        {
            var previous = change.GetOldValue<Vector>();
            var current  = change.GetNewValue<Vector>();
            if (current.Y == previous.Y) return;

            var bottom = Math.Max(0, Extent.Height - Viewport.Height);
            _isFollowing = bottom - current.Y <= BottomTolerance;
        }
        else if (change.Property == ExtentProperty || change.Property == ViewportProperty)
        {
            QueueFollow();
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _isFollowing = true;
        QueueFollow();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        QueueFollow();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _pendingFollow?.Abort();
        _pendingFollow = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    ///     <para>合并布局期间的跟随请求，并在执行前重新检查用户是否已离开底部。</para>
    ///     <para>Coalesces follow requests during layout and rechecks whether the user has left the bottom before scrolling.</para>
    /// </summary>
    private void QueueFollow()
    {
        if (!_isFollowing || _pendingFollow is not null) return;

        // 等布局确定最终高度；Offset 变化同步处理，避免排队期间的用户上滚被覆盖。
        _pendingFollow = Dispatcher.UIThread.InvokeAsync(() =>
        {
            _pendingFollow = null;
            if (_isFollowing) Offset = new Vector(Offset.X, Math.Max(0, Extent.Height - Viewport.Height));
        }, DispatcherPriority.Loaded);
    }
}
