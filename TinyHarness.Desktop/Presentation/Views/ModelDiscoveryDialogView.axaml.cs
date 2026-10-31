using Avalonia.Controls;
using Avalonia.Threading;
using System.ComponentModel;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views;

/// <summary>
///     <para>在设置面板内居中显示模型选择，按可用空间限制尺寸，列表独立滚动。</para>
///     <para>
///         Centers model selection inside settings, bounds it to the available space
///         and scrolls the list independently.
///     </para>
/// </summary>
public partial class ModelDiscoveryDialogView : UserControl
{
    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private          CatalogProviderEditorViewModel? _editor;
    private          bool _isAttached;

    public ModelDiscoveryDialogView()
    {
        InitializeComponent();
        SizeChanged += (_, args) =>
        {
            DialogRoot.MaxWidth  = Math.Max(0, args.NewSize.Width);
            DialogRoot.MaxHeight = Math.Max(0, args.NewSize.Height);
        };
        _loadingTimer.Tick += (_, _) => _editor?.AdvanceLoadingIndicator();
        AttachedToVisualTree += (_, _) =>
        {
            _isAttached = true;
            ObserveEditor();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _isAttached = false;
            _loadingTimer.Stop();
            if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;
            _editor = null;
        };
    }

    internal void FocusSearch() => ModelSearchBox.Focus();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        ObserveEditor();
    }

    private void ObserveEditor()
    {
        if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;
        _editor = DataContext as CatalogProviderEditorViewModel;
        if (_editor is not null) _editor.PropertyChanged += OnEditorPropertyChanged;
        UpdateLoadingTimer();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CatalogProviderEditorViewModel.IsFetchingModels) or
                              nameof(CatalogProviderEditorViewModel.IsModelPickerOpen)) UpdateLoadingTimer();
    }

    /// <summary>
    ///     <para>只在已挂载且打开的获取面板中运行动画；关闭、完成或卸载后停止计时。</para>
    ///     <para>
    ///         Runs animation only in an attached, open fetching panel;
    ///         stops after closing, completion or detachment.
    ///     </para>
    /// </summary>
    private void UpdateLoadingTimer()
    {
        if (_isAttached && _editor is { IsFetchingModels: true, IsModelPickerOpen: true }) _loadingTimer.Start();
        else _loadingTimer.Stop();
    }
}
