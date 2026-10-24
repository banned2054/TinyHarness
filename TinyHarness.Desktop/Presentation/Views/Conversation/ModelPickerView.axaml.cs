using Avalonia.Controls;
using Avalonia.Input;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

/// <summary>
///     <para>复用原有模型菜单布局，Esc 关闭当前会话的弹层。</para>
///     <para>Reuses the model menu layout and closes the current session's popup on Escape.</para>
/// </summary>
public partial class ModelPickerView : UserControl
{
    public ModelPickerView()
    {
        InitializeComponent();
        ModelPickerPopup.AddHandler(KeyDownEvent, OnPopupKeyDown);
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || DataContext is not SessionViewModel session) return;
        session.IsModelMenuOpen = false;
        args.Handled = true;
    }
}
