using Avalonia.Controls;

namespace TinyHarness.Desktop.Presentation.Views;

/// <summary>
///     <para>权限设置分区视图；状态与保存由 ViewModel 承担。</para>
///     <para>The permissions settings view; its view model owns state and persistence.</para>
/// </summary>
public partial class PermissionSettingsView : UserControl
{
    /// <summary>
    ///     <para>加载权限分区。</para>
    ///     <para>Loads the permissions section.</para>
    /// </summary>
    public PermissionSettingsView() => InitializeComponent();
}
