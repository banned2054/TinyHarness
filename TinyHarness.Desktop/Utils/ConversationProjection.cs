using System.Collections.ObjectModel;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Utils;

/// <summary>
///     <para>增量同步会话展示集合，保留未变化条目的容器。</para>
///     <para>Synchronizes conversation projections incrementally, retaining containers for unchanged items.</para>
/// </summary>
internal static class ConversationProjection
{
    /// <summary>
    ///     <para>按目标顺序插入、移动或移除条目，不清空集合。</para>
    ///     <para>Inserts, moves, or removes items in target order without resetting the collection.</para>
    /// </summary>
    internal static void Synchronize(ObservableCollection<ConversationItemViewModel> current,
                                     IReadOnlyList<ConversationItemViewModel>        desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < current.Count && ReferenceEquals(current[index], item)) continue;

            var existing = current.IndexOf(item);
            if (existing >= 0) current.Move(existing, index);
            else if (index < current.Count && !desired.Contains(current[index])) current[index] = item;
            else current.Insert(index, item);
        }

        while (current.Count > desired.Count) current.RemoveAt(current.Count - 1);
    }
}
