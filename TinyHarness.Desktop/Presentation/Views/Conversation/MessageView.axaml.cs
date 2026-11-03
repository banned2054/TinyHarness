using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

public partial class MessageView : UserControl
{
    public MessageView()
    {
        InitializeComponent();
    }

    /// <summary>
    ///     <para>复制按钮把用户消息原文写入系统剪贴板；无剪贴板或非用户消息时跳过。</para>
    ///     <para>
    ///         Copies the raw user message to the system clipboard; skips silently when no clipboard is available or
    ///         the item is not a user message.
    ///     </para>
    /// </summary>
    private async void OnCopyMessageClick(object? sender, RoutedEventArgs args)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
         || DataContext is not MessageItemViewModel { IsUserMessage: true } message)
            return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(message.Content));
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception exception)
        {
            // 视图无通知通道：剪贴板占用等失败转诊断输出，不静默吞栈。
            Trace.WriteLine($"Copy user message to clipboard failed: {exception}");
        }
    }
}
