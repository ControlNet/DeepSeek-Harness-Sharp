using System.Collections.ObjectModel;

namespace Dsh.Gui.ViewModels;

/** Brings an observable collection to a desired order with moves, inserts and removals, so unchanged items keep their containers and the list does not jump. */
public static class CollectionSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (var index = 0; index < desired.Count; index++)
        {
            var item = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], item))
                continue;
            var existing = IndexOf(target, item, index + 1);
            if (existing >= 0)
                target.Move(existing, index);
            else
                target.Insert(index, item);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start) where T : class
    {
        for (var index = start; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
                return index;
        }
        return -1;
    }
}
