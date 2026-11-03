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
    ///     <para>复制按钮把用户消息原文写入剪贴板；非用户消息时跳过。</para>
    ///     <para>Copies the raw user message to the clipboard; skips when the item is not a user message.</para>
    /// </summary>
    private void OnCopyMessageClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MessageItemViewModel { IsUserMessage: true } message) CopyTextToClipboard(message.Content);
    }

    /// <summary>
    ///     <para>复制按钮把本轮回复正文写入剪贴板；过程组内的中间条目没有该按钮，不参与此复制。</para>
    ///     <para>
    ///         Copies the turn's reply body to the clipboard; intermediate items inside process groups carry no such
    ///         button and never participate.
    ///     </para>
    /// </summary>
    private void OnCopyAssistantClick(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MessageItemViewModel { IsAssistantMessage: true, IsInProcessGroup: false } message)
            CopyTextToClipboard(message.TurnCopyText);
    }

    /// <summary>
    ///     <para>把文本写入系统剪贴板；无剪贴板时跳过，失败转诊断输出。</para>
    ///     <para>
    ///         Writes text to the system clipboard; skips when none is available and reports failures to diagnostic
    ///         output.
    ///     </para>
    /// </summary>
    private async void CopyTextToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        try
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(transfer);
        }
        catch (Exception exception)
        {
            // 视图无通知通道：剪贴板占用等失败转诊断输出，不静默吞栈。
            Trace.WriteLine($"Copy message to clipboard failed: {exception}");
        }
    }
}
