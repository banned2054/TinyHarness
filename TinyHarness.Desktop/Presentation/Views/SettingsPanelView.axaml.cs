using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views;

/// <summary>
///     <para>
///         设置面板视图的界面行为：遮罩点击关闭、Esc 关闭、打开时聚焦面板根，以及随宿主
///         尺寸把面板 MaxWidth/MaxHeight 钳制为宿主 − 48（小窗不溢出）。业务状态与命令在
///         <see cref="SettingsPanelViewModel" />；关闭请求经 CloseRequested 交宿主收起面板。
///     </para>
///     <para>
///         Presentation behavior of the settings panel: mask click to close, Esc to close, focusing the
///         panel root when shown, and clamping MaxWidth/MaxHeight to host − 48 so small windows do not
///         overflow. State and commands live in <see cref="SettingsPanelViewModel" />; the close request
///         reaches the host through CloseRequested.
///     </para>
/// </summary>
public partial class SettingsPanelView : UserControl
{
    private const double PanelMargin    = 48;
    private const double MinClampedSize = 240;

    public SettingsPanelView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    ///     <para>覆盖层由宿主以 IsVisible 开合：变为可见后聚焦面板根，让 Esc 与键盘导航立即可用。</para>
    ///     <para>Focuses the panel when its visibility changes so Escape and keyboard navigation are available.</para>
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsVisibleProperty && e.NewValue is true)
            Dispatcher.UIThread.Post(() => PanelRoot.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    ///     <para>覆盖层自身随宿主布局变化：钳制面板最大尺寸，小窗口下居中面板不溢出。</para>
    ///     <para>Clamps panel dimensions as the host layout changes to keep the panel within small windows.</para>
    /// </summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        PanelRoot.MaxWidth  = Math.Max(MinClampedSize, e.NewSize.Width  - PanelMargin);
        PanelRoot.MaxHeight = Math.Max(MinClampedSize, e.NewSize.Height - PanelMargin);
    }

    /// <summary>
    ///     <para>Esc 等价关闭（焦点在面板内任意输入控件时经冒泡到达此处）。</para>
    ///     <para>Closes on Escape as the key bubbles from input controls inside the panel.</para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsPanelViewModel viewModel)
        {
            e.Handled = true;
            viewModel.CloseCommand.Execute(null);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    ///     <para>遮罩左键点击关闭面板；面板本体是遮罩的兄弟节点，点击不会冒泡到这里。</para>
    ///     <para>Closes on a left click on the mask; the sibling panel does not bubble clicks here.</para>
    /// </summary>
    private void OnMaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        if (DataContext is SettingsPanelViewModel viewModel) viewModel.CloseCommand.Execute(null);
    }
}
