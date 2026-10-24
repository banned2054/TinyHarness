using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

/// <summary>
///     <para>保留原有输入区的发送快捷键与整卡焦点反馈。</para>
///     <para>Preserves composer send shortcuts and focus feedback on the whole input card.</para>
/// </summary>
public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();
        MessageInput.AddHandler(KeyDownEvent, OnMessageInputKeyDown, RoutingStrategies.Tunnel);
        MessageInput.GotFocus += (_, _) => ComposerSurface.Classes.Set("focused", true);
        MessageInput.LostFocus += (_, _) => ComposerSurface.Classes.Set("focused", false);
    }

    private void OnMessageInputKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || args.KeyModifiers is not (KeyModifiers.None or KeyModifiers.Control) ||
            DataContext is not SessionViewModel session || !session.SendCommand.CanExecute(null)) return;
        args.Handled = true;
        session.SendCommand.Execute(null);
    }
}
