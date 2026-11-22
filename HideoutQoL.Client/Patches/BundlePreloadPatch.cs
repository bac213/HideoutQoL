using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.DependencyManager;
using Diz.Resources;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using HarmonyLib;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Replaces Hideout entry's blanket preloading of all ~220 production end-product 3D bundles
/// (<c>TarkovApplication.HideoutController.method_4</c>) with on-demand loading:
/// - On Hideout load, only retains deduplicated bundles for currently active or completed crafts.
/// - Whenever <see cref="HideoutArea.SpawnProducedItems"/> is called for a newly finished craft,
///   any missing prefab bundles (including child weapon mods) are retained and awaited asynchronously
///   before instantiating the 3D loot prefab.
/// - All on-demand tokens are released when <see cref="TarkovApplication.HideoutController.UnloadHideout"/> runs.
/// </summary>
internal static class BundlePreloadPatch
{
    private static readonly FieldInfo BundleTokenField =
        FindFieldByType(typeof(TarkovApplication.HideoutController), typeof(DependencyGraph<IEasyBundle>.Token));
    private static readonly FieldInfo AreaProducersField =
        AccessTools.Field(typeof(ProductionController), "_areaProducers");

    private static FieldInfo FindFieldByType(Type owner, Type fieldType)
    {
        foreach (FieldInfo f in owner.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (f.FieldType == fieldType)
            {
                return f;
            }
        }
        return null;
    }

    private static readonly List<DependencyGraph<IEasyBundle>.Token> OnDemandTokens = new();
    private static bool _insideBypassSpawn;

    internal static void Apply(Harmony harmony)
    {
        MethodInfo prepareBundles = AccessTools.Method(typeof(TarkovApplication.HideoutController), "method_4");
        if (prepareBundles != null)
        {
            harmony.Patch(prepareBundles, prefix: new HarmonyMethod(typeof(BundlePreloadPatch), nameof(PrepareBundlesPrefix)));
        }

        MethodInfo unloadHideout = AccessTools.Method(
            typeof(TarkovApplication.HideoutController),
            nameof(TarkovApplication.HideoutController.UnloadHideout));
        if (unloadHideout != null)
        {
            harmony.Patch(unloadHideout, postfix: new HarmonyMethod(typeof(BundlePreloadPatch), nameof(UnloadHideoutPostfix)));
        }

        MethodInfo spawnProduced = AccessTools.Method(
            typeof(HideoutArea),
            nameof(HideoutArea.SpawnProducedItems),
            new[] { typeof(Item[]) });
        if (spawnProduced != null)
        {
            harmony.Patch(spawnProduced, prefix: new HarmonyMethod(typeof(BundlePreloadPatch), nameof(SpawnProducedItemsPrefix)));
        }
    }

    private static bool PrepareBundlesPrefix(TarkovApplication.HideoutController __instance, ref Task __result)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.OnDemandCraftBundles.Value)
        {
            return true;
        }

        if (!Singleton<HideoutRepresentation>.Instantiated
            || !Singleton<ItemFactory>.Instantiated
            || !Singleton<IEasyAssets>.Instantiated)
        {
            return true;
        }

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
        ItemFactory itemFactory = Singleton<ItemFactory>.Instance;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        if (hideout?.ProductionController != null
            && AreaProducersField?.GetValue(hideout.ProductionController) is Dictionary<AreaData, List<ItemsProducerBase>> areaProducers)
        {
            foreach (List<ItemsProducerBase> producers in areaProducers.Values)
            {
                if (producers == null)
                {
                    continue;
                }

                for (int i = 0; i < producers.Count; i++)
                {
                    ItemsProducerBase producer = producers[i];
                    if (producer?.Schemes == null)
                    {
                        continue;
                    }

                    foreach (KeyValuePair<string, BaseHideoutScheme> kvp in producer.Schemes)
                    {
                        string schemeId = kvp.Key;
                        bool isActiveOrReady =
                            (producer.ProducingItems != null && producer.ProducingItems.ContainsKey(schemeId))
                            || (producer.CompleteItemsStorage != null && producer.CompleteItemsStorage.GetItemsCount(schemeId) > 0);

                        if (!isActiveOrReady)
                        {
                            continue;
                        }

                        if (kvp.Value is ProductionScheme ps && !string.IsNullOrEmpty(ps.endProduct))
                        {
                            CollectItemPrefabPaths(itemFactory.GetPresetItem(ps.endProduct), keys);
                        }
                    }
                }
            }
        }

        HideoutQoLPlugin.DebugPerf($"Hideout bundle preload trimmed to {keys.Count} active/completed craft bundle(s)");

        if (keys.Count == 0)
        {
            BundleTokenField?.SetValue(__instance, null);
            __result = Task.CompletedTask;
            return false;
        }

        DependencyGraph<IEasyBundle>.Token token = Singleton<IEasyAssets>.Instance.Retain(keys);
        BundleTokenField?.SetValue(__instance, token);
        __result = EasyAssetsExtensions.LoadBundles(token);
        return false;
    }

    private static bool SpawnProducedItemsPrefix(HideoutArea __instance, Item[] items, ref Task __result)
    {
        if (_insideBypassSpawn
            || !HideoutQoLPlugin.PerfActive
            || !HideoutQoLPlugin.OnDemandCraftBundles.Value
            || items == null
            || items.Length == 0
            || !Singleton<IEasyAssets>.Instantiated)
        {
            return true;
        }

        IEasyAssets easyAssets = Singleton<IEasyAssets>.Instance;
        var missingKeys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] == null)
            {
                continue;
            }
            CollectMissingPrefabPaths(items[i], easyAssets, missingKeys);
        }

        if (missingKeys.Count == 0)
        {
            return true;
        }

        __result = LoadMissingAndSpawnAsync(__instance, items, easyAssets, missingKeys);
        return false;
    }

    private static async Task LoadMissingAndSpawnAsync(
        HideoutArea area,
        Item[] items,
        IEasyAssets easyAssets,
        HashSet<string> missingKeys)
    {
        try
        {
            HideoutQoLPlugin.DebugPerf($"On-demand loading {missingKeys.Count} bundle(s) for spawned produced item");
            DependencyGraph<IEasyBundle>.Token token = easyAssets.Retain(missingKeys);
            OnDemandTokens.Add(token);
            await EasyAssetsExtensions.LoadBundles(token);
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log.LogWarning($"HideoutQoL: on-demand craft bundle load failed: {ex.Message}");
            return;
        }

        if (area == null)
        {
            return;
        }

        _insideBypassSpawn = true;
        Task spawnTask;
        try
        {
            spawnTask = area.SpawnProducedItems(items);
        }
        finally
        {
            _insideBypassSpawn = false;
        }

        await spawnTask;
    }

    private static void UnloadHideoutPostfix(ref Task __result)
    {
        if (__result == null)
        {
            ReleaseOnDemandTokens();
            return;
        }

        __result = WrapUnloadAsync(__result);
    }

    private static async Task WrapUnloadAsync(Task original)
    {
        try
        {
            await original;
        }
        finally
        {
            ReleaseOnDemandTokens();
        }
    }

    private static void ReleaseOnDemandTokens()
    {
        for (int i = 0; i < OnDemandTokens.Count; i++)
        {
            try
            {
                OnDemandTokens[i]?.Release();
            }
            catch (Exception ex)
            {
                HideoutQoLPlugin.Log.LogWarning($"HideoutQoL: failed releasing on-demand bundle token: {ex.Message}");
            }
        }
        OnDemandTokens.Clear();
    }

    private static void CollectItemPrefabPaths(Item item, HashSet<string> keys)
    {
        if (item == null)
        {
            return;
        }

        foreach (Item part in item.GetAllItems())
        {
            string path = part?.Prefab?.path;
            if (!string.IsNullOrEmpty(path))
            {
                keys.Add(path);
            }
        }
    }

    private static void CollectMissingPrefabPaths(Item item, IEasyAssets easyAssets, HashSet<string> missingKeys)
    {
        foreach (Item part in item.GetAllItems())
        {
            string path = part?.Prefab?.path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            try
            {
                if (!easyAssets.IsAssetLoaded(path))
                {
                    missingKeys.Add(path);
                }
            }
            catch
            {
                // Path is not registered in EasyAssets dependency graph; skip retaining it.
            }
        }
    }
}
