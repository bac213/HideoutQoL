using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Replaces O(N) linear stash scans across the Hideout with <see cref="StashIndex"/> O(1) template buckets:
/// - Keeps <see cref="HideoutRepresentation._allStashItems"/> in exact sync in O(1) on add/remove/refresh/rebuild.
/// - Accelerates <see cref="ItemRequirement.Test"/> and <see cref="HideoutRepresentation.CG_GetItemReferences"/>.
/// - Accelerates <see cref="HideoutRepresentation.GetAvailableItemsByFilter{T}"/> (closed on <see cref="Item"/>).
/// - Removes full-stash LINQ sorting on <see cref="ScavCaseView.UpdateView"/>.
/// - Caches <see cref="PermanentProducer.AvailableItems"/> inside <see cref="BitcoinProducer.InstallSupplies"/>.
/// </summary>
internal static class StashIndexPatch
{
    private static readonly FieldInfo OnItemsChangedField =
        AccessTools.Field(typeof(HideoutRepresentation), "_onItemsChanged");
    private static readonly FieldInfo OnItemChangedField =
        AccessTools.Field(typeof(HideoutRepresentation), "_onItemChanged");
    private static readonly FieldInfo ScavReqViewListField =
        AccessTools.Field(typeof(ScavCaseView), "_requirementsViewList");

    internal static void Apply(Harmony harmony)
    {
        // Rebuild / Clear index
        MethodInfo rebuildStash = AccessTools.Method(typeof(HideoutRepresentation), "method_25");
        harmony.Patch(rebuildStash, postfix: new HarmonyMethod(typeof(StashIndexPatch), nameof(RebuildStashPostfix)));

        MethodInfo deInit = AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.DeInit));
        harmony.Patch(deInit, postfix: new HarmonyMethod(typeof(StashIndexPatch), nameof(DeInitPostfix)));

        // O(1) stash event handlers (also speeds up main-menu stash dragging / buying / unpacking!)
        harmony.Patch(
            AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.AddItemEventHandler)),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(AddItemEventPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.RemoveItemEventHandler)),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(RemoveItemEventPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.RefreshItemEvent)),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(RefreshItemEventPrefix)));

        // O(1) template-bucket lookup in ItemRequirement.Test & CG_GetItemReferences
        harmony.Patch(
            AccessTools.Method(typeof(ItemRequirement), nameof(ItemRequirement.Test), new[] { typeof(IEnumerable<Item>) }),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(ItemRequirementTestPrefix)));

        MethodInfo getItemRefsHelper = AccessTools.Method(
            typeof(HideoutRepresentation),
            "CG_GetItemReferences",
            new[] { typeof(List<Item>), typeof(ItemRequirement) });
        if (getItemRefsHelper != null)
        {
            harmony.Patch(getItemRefsHelper, prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(GetItemReferencesHelperPrefix)));
        }

        // O(1) template-bucket lookup in GetAvailableItemsByFilter<Item>
        MethodInfo filterOpen = AccessTools.Method(typeof(HideoutRepresentation), "GetAvailableItemsByFilter");
        if (filterOpen != null && filterOpen.IsGenericMethodDefinition)
        {
            MethodInfo filterItem = filterOpen.MakeGenericMethod(typeof(Item));
            harmony.Patch(filterItem, prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(GetAvailableItemsByFilterPrefix)));
        }

        // ScavCaseView.UpdateView: pass AllStashItems directly instead of sorting & cloning whole stash
        harmony.Patch(
            AccessTools.Method(typeof(ScavCaseView), nameof(ScavCaseView.UpdateView)),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(ScavCaseUpdateViewPrefix)));

        // BitcoinProducer.InstallSupplies: hoist AvailableItems outside the GPU install loop
        harmony.Patch(
            AccessTools.Method(typeof(BitcoinProducer), nameof(BitcoinProducer.InstallSupplies)),
            prefix: new HarmonyMethod(typeof(StashIndexPatch), nameof(BitcoinInstallSuppliesPrefix)));
    }

    private static void RebuildStashPostfix(HideoutRepresentation __instance)
    {
        if (__instance != null)
        {
            StashIndex.Instance.RebuildFrom(__instance._allStashItems);
        }
    }

    private static void DeInitPostfix()
    {
        StashIndex.Instance.Clear();
    }

    private static bool AddItemEventPrefix(HideoutRepresentation __instance, AddItemEventArgs args)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (args.Status != CommandStatus.Succeed)
        {
            return false;
        }

        Item item = args.Item;
        List<Item> stashItems = __instance._allStashItems;
        if (StashIndex.Instance.Contains(stashItems, item))
        {
            return false;
        }

        ItemAddress currentAddress = item.CurrentAddress;
        Inventory inventory = __instance._inventoryController.Inventory;
        if (currentAddress != null
            && (currentAddress.IsChildOf(inventory.Stash, notMergedWithThisItem: true)
                || currentAddress.IsChildOf(inventory.QuestStashItems, notMergedWithThisItem: true)
                || currentAddress.IsChildOf(inventory.QuestRaidItems, notMergedWithThisItem: true)))
        {
            __instance.list_3.Clear();
            item.GetAllAssembledItemsNonAlloc(__instance.list_3);
            for (int i = 0; i < __instance.list_3.Count; i++)
            {
                StashIndex.Instance.Add(stashItems, __instance.list_3[i]);
            }
            __instance.list_3.Clear();

            __instance.CalculateTraderRequirements(decide: true);
            InvokeOnItemsChanged(__instance);
            InvokeOnItemChanged(__instance, item);
        }

        return false;
    }

    private static bool RemoveItemEventPrefix(HideoutRepresentation __instance, RemoveItemEventArgs args)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (args.Status != CommandStatus.Succeed)
        {
            return false;
        }

        Item item = args.Item;
        List<Item> stashItems = __instance._allStashItems;
        if (!StashIndex.Instance.Remove(stashItems, item))
        {
            return false;
        }

        __instance.list_3.Clear();
        item.GetAllAssembledItemsNonAlloc(__instance.list_3);
        for (int i = 0; i < __instance.list_3.Count; i++)
        {
            StashIndex.Instance.Remove(stashItems, __instance.list_3[i]);
        }
        __instance.list_3.Clear();

        __instance.CalculateTraderRequirements(decide: true);
        InvokeOnItemsChanged(__instance);
        InvokeOnItemChanged(__instance, item);
        return false;
    }

    private static bool RefreshItemEventPrefix(HideoutRepresentation __instance, RefreshItemEventArgs args)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        Item item = args.Item;
        List<Item> stashItems = __instance._allStashItems;
        if (item.CurrentAddress != null
            && item.CurrentAddress.IsChildOf(__instance._inventoryController.Inventory.Stash, notMergedWithThisItem: true))
        {
            __instance.list_3.Clear();
            item.GetAllAssembledItemsNonAlloc(__instance.list_3);
            for (int i = 0; i < __instance.list_3.Count; i++)
            {
                StashIndex.Instance.Add(stashItems, __instance.list_3[i]);
            }
            __instance.list_3.Clear();
        }

        __instance.CalculateTraderRequirements(decide: true);
        InvokeOnItemsChanged(__instance);
        InvokeOnItemChanged(__instance, item);
        return false;
    }

    private static bool ItemRequirementTestPrefix(ItemRequirement __instance, IEnumerable<Item> value)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (value is not List<Item> list || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return true;
        }

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
        if (!ReferenceEquals(list, hideout._allStashItems))
        {
            return true;
        }

        __instance.UserItemsCount = 0;
        __instance._notEmptyCompoundItems = 0;

        if (!string.IsNullOrEmpty(__instance.TemplateId))
        {
            MongoID tplId;
            try
            {
                tplId = (MongoID)__instance.TemplateId;
            }
            catch
            {
                return true;
            }

            if (StashIndex.Instance.TryGetBucket(list, tplId, out List<Item> bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    Item item = bucket[i];
                    if (__instance.IsSuitableItem(item))
                    {
                        __instance.UserItemsCount += item.StackObjectsCount;
                        if (item is CompoundItem { IsEmpty: false })
                        {
                            __instance._notEmptyCompoundItems++;
                        }
                    }
                }
            }
        }

        __instance.Retest();
        return false;
    }

    private static bool GetItemReferencesHelperPrefix(
        HideoutRepresentation __instance,
        List<Item> itemsBuffer,
        ItemRequirement requirement)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        itemsBuffer.Clear();
        if (!string.IsNullOrEmpty(requirement.TemplateId))
        {
            MongoID tplId;
            try
            {
                tplId = (MongoID)requirement.TemplateId;
            }
            catch
            {
                return true;
            }

            if (StashIndex.Instance.TryGetBucket(__instance._allStashItems, tplId, out List<Item> bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    Item item = bucket[i];
                    if (requirement.IsSuitableItem(item) && item.IsEmpty())
                    {
                        itemsBuffer.Add(item);
                    }
                }
            }
        }

        itemsBuffer.Sort(HideoutRepresentation._comparer);
        return false;
    }

    private static bool GetAvailableItemsByFilterPrefix(
        HideoutRepresentation __instance,
        IEnumerable<MongoID> templateIdFilter,
        Func<Item, bool> predicate,
        bool onlyExamined,
        ref IEnumerable<Item> __result)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (templateIdFilter == null)
        {
            // Unfiltered call (used by ScavCaseView.Show): return _allStashItems directly without sorting 3000+ items
            if (predicate == null && !onlyExamined)
            {
                __result = __instance._allStashItems;
                return false;
            }
            return true;
        }

        List<Item> stashItems = __instance._allStashItems;
        var matched = new List<Item>(8);
        HashSet<MongoID> seenTemplates = null;
        MongoID firstTemplate = default;
        int templateCount = 0;

        foreach (MongoID templateId in templateIdFilter)
        {
            if (templateCount == 0)
            {
                firstTemplate = templateId;
                templateCount = 1;
            }
            else
            {
                if (templateCount == 1)
                {
                    if (templateId == firstTemplate)
                    {
                        continue;
                    }
                    seenTemplates = new HashSet<MongoID> { firstTemplate };
                }
                if (!seenTemplates.Add(templateId))
                {
                    continue;
                }
                templateCount++;
            }

            if (StashIndex.Instance.TryGetBucket(stashItems, templateId, out List<Item> bucket))
            {
                for (int i = 0; i < bucket.Count; i++)
                {
                    Item item = bucket[i];
                    if (predicate != null && !predicate(item))
                    {
                        continue;
                    }
                    if (onlyExamined && !__instance._session.Profile.Examined(item))
                    {
                        continue;
                    }
                    matched.Add(item);
                }
            }
        }

        if (matched.Count > 1)
        {
            matched.Sort(static (a, b) =>
            {
                int cmp = a.TemplateId.CompareTo(b.TemplateId);
                return cmp != 0 ? cmp : a.SpawnedInSession.CompareTo(b.SpawnedInSession);
            });
        }

        __result = matched;
        return false;
    }

    private static bool ScavCaseUpdateViewPrefix(ScavCaseView __instance)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (__instance == null)
        {
            return false;
        }

        // Reproduce ProduceViewBase.UpdateView() without virtual recursion
        EProductionState productionState = __instance.Producer.GetProductionState(__instance.Scheme._id);
        bool isError = productionState == EProductionState.Error;
        if (__instance._electricityNeededIcon != null)
        {
            __instance._electricityNeededIcon.gameObject.SetActive(__instance.Scheme.needFuelForAllProductionTime && !isError);
        }
        __instance.ApplyState(productionState, __instance._stateActiveObjects);

        __instance.UpdateViewData();

        var reqViews = ScavReqViewListField?.GetValue(__instance) as ViewList<ItemRequirement, HideoutProductionRequirementView>;
        if (reqViews == null)
        {
            return false;
        }

        // Pass AllStashItems directly so ItemRequirement.Test hits StashIndex O(1) without sorting 3000 items
        IEnumerable<Item> allStashItems = Singleton<HideoutRepresentation>.Instance.AllStashItems;
        bool allFulfilled = true;
        bool canStart = __instance.Producer.CanStartByProductionCount();
        foreach (KeyValuePair<ItemRequirement, HideoutProductionRequirementView> kvp in reqViews)
        {
            kvp.Value.Show(__instance.ItemUiContext, __instance.InventoryController, kvp.Key, __instance.Scheme, allStashItems, canStart);
            allFulfilled &= kvp.Value.IsFulfilled;
        }

        __instance.IsRequirementsFulfilled = allFulfilled;
        __instance.SetStartButtonActive();
        return false;
    }

    private static bool BitcoinInstallSuppliesPrefix(BitcoinProducer __instance, bool installAll, Action onComplete)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            return true;
        }

        if (Interlocked.CompareExchange(ref __instance._interactionStatus, 1, 0) == 1)
        {
            return false;
        }

        Item[] available = __instance.AvailableItems;
        int availableCount = available?.Length ?? 0;
        int maxTotal = Mathf.Min(__instance.MaxSuppliesCount, availableCount + __instance.InstalledSuppliesCount);
        if (__instance.InstalledSuppliesCount >= maxTotal)
        {
            __instance._interactionStatus = 0;
            return false;
        }

        int startSlot = __instance.InstalledSuppliesCount;
        int toInstall = installAll ? (maxTotal - __instance.InstalledSuppliesCount) : 1;
        var dict = new Dictionary<int, HideoutItemReference>(toInstall);
        for (int i = 0; i < toInstall; i++)
        {
            dict[startSlot + i] = new HideoutItemReference
            {
                Item = available[i],
                Count = 1,
            };
        }

        Singleton<HideoutRepresentation>.Instance.PutManyInAreaSlots(__instance.AreaType, dict, onComplete);
        __instance.InstalledSuppliesCount += toInstall;
        if (!__instance.ProducingItems.Any())
        {
            __instance.TryToStartProduction();
        }
        __instance.AreaData.AttachVideoCard(toInstall);
        __instance.AreaData.Template.AreaBehaviour.PlaySound(EAreaActivityType.ItemInstall);
        __instance._interactionStatus = 0;
        return false;
    }

    private static void InvokeOnItemsChanged(HideoutRepresentation instance)
    {
        (OnItemsChangedField?.GetValue(instance) as Action)?.Invoke();
    }

    private static void InvokeOnItemChanged(HideoutRepresentation instance, Item item)
    {
        (OnItemChangedField?.GetValue(instance) as Action<Item>)?.Invoke(item);
    }
}
