namespace TinyHarness.Desktop.Models;

/// <summary>任务清单条目的状态（官方 todo/write 事件的三值枚举）。</summary>
public enum SessionTodoStatus
{
    /// <summary>待处理（wire 值 pending）。</summary>
    Pending,

    /// <summary>进行中（wire 值 in_progress）。</summary>
    InProgress,

    /// <summary>已完成（wire 值 completed）。</summary>
    Completed
}

/// <summary>
///     任务清单的一个条目（todo/write 事件与 todo_write 工具参数共用的条目形状）。
///     后端不携带 id，条目以 content 为身份；整表 last-write-wins。
/// </summary>
public sealed record SessionTodoItem(string Content, SessionTodoStatus Status);
