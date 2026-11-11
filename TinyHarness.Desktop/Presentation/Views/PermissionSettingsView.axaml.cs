using Avalonia.Controls;

namespace TinyHarness.Desktop.Presentation.Views;

/// <summary>
///     <para>通用页内的权限与审核设置块；状态与保存由 ViewModel 承担。</para>
///     <para>The permission and review block on the general page; its view model owns state and persistence.</para>
/// </summary>
public partial class PermissionSettingsView : UserControl
{
    /// <summary>
    ///     <para>加载权限与审核设置块。</para>
    ///     <para>Loads the permission and review settings block.</para>
    /// </summary>
    public PermissionSettingsView() => InitializeComponent();
}
