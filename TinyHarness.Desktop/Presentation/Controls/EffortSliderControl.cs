using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace TinyHarness.Desktop.Presentation.Controls;

/// <summary>
///     <para>
///         离散推理强度滑块：2 档及以上按档位数均分刻度并支持点击、拖拽与方向键切换；
///         仅有 1 档或无档位时将滑块圆点居中展示并禁用交互。
///     </para>
///     <para>
///         Discrete reasoning-effort slider: distributes ticks evenly for 2+ levels with click, drag and arrow-key
///         selection; centers a non-interactive thumb when 0 or 1 level is available.
///     </para>
/// </summary>
public sealed class EffortSliderControl : Control
{
    public static readonly StyledProperty<int> OptionCountProperty =
        AvaloniaProperty.Register<EffortSliderControl, int>(nameof(OptionCount));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<EffortSliderControl, int>(nameof(SelectedIndex), -1);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<IBrush?> FillBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(FillBrush));

    public static readonly StyledProperty<IBrush?> ThumbBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(ThumbBrush));

    public static readonly StyledProperty<IBrush?> ThumbBorderBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(ThumbBorderBrush));

    public static readonly StyledProperty<IBrush?> MutedThumbBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(MutedThumbBrush));

    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(TickBrush));

    public static readonly StyledProperty<IBrush?> ActiveTickBrushProperty =
        AvaloniaProperty.Register<EffortSliderControl, IBrush?>(nameof(ActiveTickBrush));

    private bool    _isDragging;
    private int     _previewIndex = -1;
    private double? _dragX;

    static EffortSliderControl()
    {
        AffectsRender<EffortSliderControl>(OptionCountProperty,
                                           SelectedIndexProperty,
                                           TrackBrushProperty,
                                           FillBrushProperty,
                                           ThumbBrushProperty,
                                           ThumbBorderBrushProperty,
                                           MutedThumbBrushProperty,
                                           TickBrushProperty,
                                           ActiveTickBrushProperty,
                                           IsEnabledProperty);
    }

    public EffortSliderControl()
    {
        Focusable = true;
        Height    = 26;
        MinWidth  = 120;
    }

    public int OptionCount
    {
        get => GetValue(OptionCountProperty);
        set => SetValue(OptionCountProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public IBrush? FillBrush
    {
        get => GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public IBrush? ThumbBrush
    {
        get => GetValue(ThumbBrushProperty);
        set => SetValue(ThumbBrushProperty, value);
    }

    public IBrush? ThumbBorderBrush
    {
        get => GetValue(ThumbBorderBrushProperty);
        set => SetValue(ThumbBorderBrushProperty, value);
    }

    public IBrush? MutedThumbBrush
    {
        get => GetValue(MutedThumbBrushProperty);
        set => SetValue(MutedThumbBrushProperty, value);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public IBrush? ActiveTickBrush
    {
        get => GetValue(ActiveTickBrushProperty);
        set => SetValue(ActiveTickBrushProperty, value);
    }

    /// <summary>
    ///     <para>用户通过点击、拖拽松手或键盘方向键确认新档位时触发。</para>
    ///     <para>Raised when the user commits a new effort index via click, drag release, or arrow keys.</para>
    /// </summary>
    public event Action<int>? IndexCommitted;

    /// <summary>
    ///     <para>拖拽过程中预览档位变化时触发，便于标题实时显示目标档位名。</para>
    ///     <para>Raised while dragging as the preview index changes so headers can reflect the target effort.</para>
    /// </summary>
    public event Action<int?>? PreviewIndexChanged;

    private bool CanInteract => IsEnabled && OptionCount >= 2;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OptionCountProperty || change.Property == IsEnabledProperty)
        {
            Cursor = CanInteract ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            if (!CanInteract && _isDragging) CancelDrag();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width  = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 4 || height <= 4) return;

        var trackHeight = Math.Min(24.0, height);
        var trackTop    = (height - trackHeight) / 2.0;
        var radius      = trackHeight            / 2.0;
        var trackRect   = new Rect(0, trackTop, width, trackHeight);

        var trackBrush = TrackBrush ?? Brushes.Gray;
        context.DrawRectangle(trackBrush, null, trackRect, radius, radius);

        var count = OptionCount;
        var thumbRadius = radius - 1.0;
        var centerY     = trackTop + radius;

        // 0 档或仅有 1 档：居中展示不可交互滑块，避免左右偏置造成“关闭/最低”误解。
        if (count < 2)
        {
            var centerX    = width / 2.0;
            var thumbBrush = count == 1 ? ThumbBrush ?? Brushes.White : MutedThumbBrush ?? Brushes.DarkGray;
            var pen        = count == 1 && ThumbBorderBrush is not null ? new Pen(ThumbBorderBrush, 1) : null;
            using (count == 1 ? context.PushOpacity(0.88) : default)
            {
                context.DrawEllipse(thumbBrush, pen, new Point(centerX, centerY), thumbRadius, thumbRadius);
            }

            return;
        }

        var activeIndex = _isDragging && _previewIndex >= 0
            ? _previewIndex
            : Math.Clamp(SelectedIndex, 0, count - 1);
        var thumbCenterX = _isDragging && _dragX is { } dragX
            ? Math.Clamp(dragX, radius, width - radius)
            : StopCenterX(activeIndex, count, width, radius);

        // 有效激活进度条：从轨道左端包覆到当前滑块中心右侧。
        var fillWidth = Math.Clamp(thumbCenterX + radius, trackHeight, width);
        var fillRect  = new Rect(0, trackTop, fillWidth, trackHeight);
        using (IsEnabled ? default : context.PushOpacity(0.5))
        {
            context.DrawRectangle(FillBrush ?? Brushes.DodgerBlue, null, fillRect, radius, radius);

            var tickRadius      = 2.2;
            var tickBrush       = TickBrush       ?? Brushes.Silver;
            var activeTickBrush = ActiveTickBrush ?? Brushes.White;
            for (var i = 0; i < count; i++)
            {
                var stopX = StopCenterX(i, count, width, radius);
                if (Math.Abs(stopX - thumbCenterX) < thumbRadius * 0.85) continue;
                var brush = stopX < thumbCenterX ? activeTickBrush : tickBrush;
                context.DrawEllipse(brush, null, new Point(stopX, centerY), tickRadius, tickRadius);
            }

            var thumbPen = ThumbBorderBrush is not null ? new Pen(ThumbBorderBrush, 1) : null;
            context.DrawEllipse(ThumbBrush ?? Brushes.White, thumbPen, new Point(thumbCenterX, centerY),
                                thumbRadius, thumbRadius);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanInteract || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _isDragging = true;
        e.Pointer.Capture(this);
        UpdateDrag(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isDragging) return;
        UpdateDrag(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging) return;
        UpdateDrag(e.GetPosition(this).X);
        var committed = _previewIndex;
        _isDragging   = false;
        _dragX        = null;
        _previewIndex = -1;
        e.Pointer.Capture(null);
        PreviewIndexChanged?.Invoke(null);
        InvalidateVisual();
        if (committed >= 0 && committed < OptionCount && committed != SelectedIndex)
            IndexCommitted?.Invoke(committed);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isDragging) CancelDrag();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!CanInteract) return;
        var current = Math.Clamp(SelectedIndex, 0, OptionCount - 1);
        var next = e.Key switch
        {
            Key.Left or Key.Down  => Math.Max(0, current - 1),
            Key.Right or Key.Up   => Math.Min(OptionCount - 1, current + 1),
            Key.Home              => 0,
            Key.End               => OptionCount - 1,
            _                     => current
        };
        if (next == current) return;
        IndexCommitted?.Invoke(next);
        e.Handled = true;
    }

    private void UpdateDrag(double x)
    {
        _dragX = x;
        var radius = Math.Min(24.0, Bounds.Height) / 2.0;
        var index  = NearestStopIndex(x, OptionCount, Bounds.Width, radius);
        if (index != _previewIndex)
        {
            _previewIndex = index;
            PreviewIndexChanged?.Invoke(index);
        }

        InvalidateVisual();
    }

    private void CancelDrag()
    {
        _isDragging   = false;
        _dragX        = null;
        _previewIndex = -1;
        PreviewIndexChanged?.Invoke(null);
        InvalidateVisual();
    }

    private static double StopCenterX(int index, int count, double width, double radius)
    {
        if (count <= 1) return width / 2.0;
        var usable = Math.Max(0.0, width - radius * 2.0);
        return radius + usable * index / (count - 1);
    }

    private static int NearestStopIndex(double x, int count, double width, double radius)
    {
        if (count <= 1) return 0;
        var usable = Math.Max(1.0, width - radius * 2.0);
        var ratio  = Math.Clamp((x - radius) / usable, 0.0, 1.0);
        return (int)Math.Round(ratio * (count - 1), MidpointRounding.AwayFromZero);
    }
}
