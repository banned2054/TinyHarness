using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace TinyHarness.Desktop.Presentation.Views;

/// <summary>
///     <para>显示供应商与模型目录表单。</para>
///     <para>Displays provider and model catalog forms.</para>
/// </summary>
public partial class CatalogSettingsView : UserControl
{
    public CatalogSettingsView() => InitializeComponent();

    /// <summary>
    ///     <para>展开编辑卡时将密钥区带入可见范围，不改变输入焦点。</para>
    ///     <para>Brings the key section into view when an editor opens without changing input focus.</para>
    /// </summary>
    private void OnKeySectionLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control section) return;
        Dispatcher.UIThread.Post(() => section.BringIntoView(), DispatcherPriority.Loaded);
    }
}
