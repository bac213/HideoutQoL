using System.Collections.Generic;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;

namespace HideoutQoL.Client;

/// <summary>
/// Maintains an O(1) lookup mirror of <see cref="HideoutRepresentation._allStashItems"/> grouped by
/// <see cref="MongoID"/> (TemplateId), plus an Item -> list-index map so add/remove/contains on
/// <see cref="HideoutRepresentation._allStashItems"/> stay O(1) instead of O(N).
/// </summary>
internal sealed class StashIndex
{
    private readonly Dictionary<Item, int> _listIndex = new(2048);
    private readonly Dictionary<MongoID, List<Item>> _byTemplate = new(512);
    private List<Item> _trackedList;

    internal static readonly StashIndex Instance = new();

    internal int Count => _listIndex.Count;

    internal bool IsTracking(object enumerable) => ReferenceEquals(enumerable, _trackedList);

    internal void Clear()
    {
        _listIndex.Clear();
        _byTemplate.Clear();
        _trackedList = null;
    }

    internal void RebuildFrom(List<Item> stashItems)
    {
        _listIndex.Clear();
        _byTemplate.Clear();
        _trackedList = stashItems;
        if (stashItems == null)
        {
            return;
        }

        for (int i = 0; i < stashItems.Count; i++)
        {
            Item item = stashItems[i];
            if (item == null)
            {
                continue;
            }
            if (_listIndex.ContainsKey(item))
            {
                continue;
            }
            _listIndex[item] = i;
            AddToBucket(item);
        }
    }

    internal bool EnsureSynced(List<Item> stashItems)
    {
        if (stashItems == null)
        {
            return false;
        }
        if (!ReferenceEquals(_trackedList, stashItems) || _listIndex.Count != stashItems.Count)
        {
            RebuildFrom(stashItems);
        }
        return true;
    }

    internal bool Contains(List<Item> stashItems, Item item)
    {
        if (item == null)
        {
            return false;
        }
        EnsureSynced(stashItems);
        return _listIndex.ContainsKey(item);
    }

    /// <summary>
    /// Appends <paramref name="item"/> to both <paramref name="stashItems"/> and the index if not already present.
    /// </summary>
    internal bool Add(List<Item> stashItems, Item item)
    {
        if (stashItems == null || item == null)
        {
            return false;
        }
        EnsureSynced(stashItems);
        if (_listIndex.ContainsKey(item))
        {
            return false;
        }

        int idx = stashItems.Count;
        stashItems.Add(item);
        _listIndex[item] = idx;
        AddToBucket(item);
        return true;
    }

    /// <summary>
    /// Removes <paramref name="item"/> from both <paramref name="stashItems"/> and the index in O(1) via swap-remove.
    /// </summary>
    internal bool Remove(List<Item> stashItems, Item item)
    {
        if (stashItems == null || item == null)
        {
            return false;
        }
        EnsureSynced(stashItems);
        if (!_listIndex.TryGetValue(item, out int idx))
        {
            return false;
        }

        int lastIdx = stashItems.Count - 1;
        if (idx < lastIdx)
        {
            Item moved = stashItems[lastIdx];
            stashItems[idx] = moved;
            if (moved != null)
            {
                _listIndex[moved] = idx;
            }
        }
        stashItems.RemoveAt(lastIdx);
        _listIndex.Remove(item);
        RemoveFromBucket(item);
        return true;
    }

    internal bool TryGetBucket(List<Item> stashItems, MongoID templateId, out List<Item> bucket)
    {
        EnsureSynced(stashItems);
        return _byTemplate.TryGetValue(templateId, out bucket);
    }

    private void AddToBucket(Item item)
    {
        MongoID tpl = item.TemplateId;
        if (!_byTemplate.TryGetValue(tpl, out List<Item> bucket))
        {
            bucket = new List<Item>(4);
            _byTemplate[tpl] = bucket;
        }
        bucket.Add(item);
    }

    private void RemoveFromBucket(Item item)
    {
        MongoID tpl = item.TemplateId;
        if (!_byTemplate.TryGetValue(tpl, out List<Item> bucket))
        {
            return;
        }

        int idx = bucket.IndexOf(item);
        if (idx >= 0)
        {
            int last = bucket.Count - 1;
            if (idx < last)
            {
                bucket[idx] = bucket[last];
            }
            bucket.RemoveAt(last);
        }
    }
}
