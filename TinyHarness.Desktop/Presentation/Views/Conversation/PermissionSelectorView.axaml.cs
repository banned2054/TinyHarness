using Avalonia.Controls;
using Avalonia.Input;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

/// <summary>
///     <para>输入区权限菜单，只处理弹层的键盘关闭。</para>
///     <para>The composer permission menu, handling only keyboard dismissal.</para>
/// </summary>
public partial class PermissionSelectorView : UserControl
{
    public PermissionSelectorView()
    {
        InitializeComponent();
        PermissionPickerPopup.AddHandler(KeyDownEvent, OnPopupKeyDown);
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || DataContext is not SessionViewModel session) return;
        session.IsPermissionMenuOpen = false;
        args.Handled = true;
    }
}
