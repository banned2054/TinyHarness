using Avalonia.Controls;
using Avalonia.Input;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Presentation.Views.Conversation;

/// <summary>
///     <para>协调模型选择弹层的 Esc 关闭与推理强度滑块的拖拽预览和档位提交。</para>
///     <para>Coordinates Escape dismissal and effort-slider drag preview and commit for the model picker popup.</para>
/// </summary>
public partial class ModelPickerView : UserControl
{
    public ModelPickerView()
    {
        InitializeComponent();
        ModelPickerPopup.AddHandler(KeyDownEvent, OnPopupKeyDown);
        CompactEffortSlider.PreviewIndexChanged += OnSliderPreviewIndexChanged;
        CompactEffortSlider.IndexCommitted      += OnSliderIndexCommitted;
        UnifiedEffortSlider.PreviewIndexChanged += OnSliderPreviewIndexChanged;
        UnifiedEffortSlider.IndexCommitted      += OnSliderIndexCommitted;
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || DataContext is not SessionViewModel session) return;
        session.IsModelMenuOpen = false;
        args.Handled = true;
    }

    private void OnSliderPreviewIndexChanged(int? index)
    {
        if (DataContext is SessionViewModel session) session.SetEffortPreviewIndex(index);
    }

    private void OnSliderIndexCommitted(int index)
    {
        if (DataContext is SessionViewModel session) session.SelectEffortByIndex(index);
    }
}
