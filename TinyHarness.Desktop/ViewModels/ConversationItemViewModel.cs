namespace TinyHarness.Desktop.ViewModels;

/// <summary>会话时间线条目的界面模型基类；Seq 与后端事件序号对齐。</summary>
public abstract class ConversationItemViewModel : ObservableObject
{
    protected ConversationItemViewModel(long seq)
    {
        Seq = seq;
    }

    public long Seq { get; }
}
