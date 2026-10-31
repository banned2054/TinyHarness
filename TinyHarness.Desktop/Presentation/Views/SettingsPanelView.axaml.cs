using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.ComponentModel;
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
    private const double                    PanelMargin    = 48;
    private const double                    MinClampedSize = 240;
    private       CatalogSettingsViewModel? _catalog;

    public SettingsPanelView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>
    ///     <para>跟随目录弹层状态，在打开时聚焦搜索框，关闭后恢复设置面板的键盘导航。</para>
    ///     <para>
    ///         Follows catalog dialog state, focusing search when opened
    ///         and restoring settings keyboard navigation when closed.
    ///     </para>
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_catalog is not null) _catalog.PropertyChanged -= OnCatalogPropertyChanged;
        _catalog = (DataContext as SettingsPanelViewModel)?.Catalog;
        if (_catalog is not null) _catalog.PropertyChanged += OnCatalogPropertyChanged;
    }

    private void OnCatalogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CatalogSettingsViewModel.IsModelPickerOpen)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_catalog?.IsModelPickerOpen == true) ModelDiscoveryDialog.FocusSearch();
            else if (IsEffectivelyVisible) PanelRoot.Focus();
        }, DispatcherPriority.Input);
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
    ///     <para>Esc 优先关闭模型选择，下一次才关闭设置；焦点在输入控件内时同样生效。</para>
    ///     <para>Escape dismisses model selection before settings, including when focus is inside an input.</para>
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsPanelViewModel viewModel)
        {
            e.Handled = true;
            viewModel.DismissCommand.Execute(null);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    ///     <para>遮罩左键点击优先关闭模型选择；没有子面板时关闭设置。</para>
    ///     <para>Left clicks on a mask dismiss model selection first, or settings when no child dialog is open.</para>
    /// </summary>
    private void OnMaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        if (DataContext is SettingsPanelViewModel viewModel) viewModel.DismissCommand.Execute(null);
    }
}
