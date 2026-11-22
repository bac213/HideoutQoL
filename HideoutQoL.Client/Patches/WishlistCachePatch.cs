using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.Settings.Game;
using HarmonyLib;
using UnityEngine;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Caches the raw Hideout auto-wishlist template set (zones + favorite recipes) so every
/// <see cref="WishlistManager.IsInHideoutQolWishlist"/> and <see cref="WishlistManager.GetHideoutQolItems"/>
/// call becomes an O(1) <see cref="HashSet{T}"/> lookup instead of enumerating all Hideout areas and
/// favorite recipes on every newly instantiated <c>ItemView</c>.
/// </summary>
internal static class WishlistCachePatch
{
    private const float MaxCacheAgeSeconds = 2f;

    private static readonly HashSet<MongoID> CachedTemplates = new();
    private static bool _dirty = true;
    private static float _builtAt = -100f;
    private static GameSettingsGroup.EAutoAddToWishlist _cachedOption = (GameSettingsGroup.EAutoAddToWishlist)(-1);

    internal static void Apply(Harmony harmony)
    {
        harmony.Patch(
            AccessTools.Method(typeof(WishlistManager), nameof(WishlistManager.IsInHideoutQolWishlist)),
            prefix: new HarmonyMethod(typeof(WishlistCachePatch), nameof(IsInHideoutQolWishlistPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(WishlistManager), nameof(WishlistManager.GetHideoutQolItems)),
            prefix: new HarmonyMethod(typeof(WishlistCachePatch), nameof(GetHideoutQolItemsPrefix)));

        // Invalidate cache whenever Hideout areas, favorites, or statuses change
        InvalidateOn(harmony, typeof(HideoutRepresentation), nameof(HideoutRepresentation.DeInit));
        InvalidateOn(harmony, typeof(HideoutRepresentation), nameof(HideoutRepresentation.SetInventoryController));
        InvalidateOn(harmony, typeof(HideoutRepresentation), nameof(HideoutRepresentation.AddFavoriteScheme));
        InvalidateOn(harmony, typeof(HideoutRepresentation), nameof(HideoutRepresentation.RemoveFavoriteScheme));
        InvalidateOn(harmony, typeof(HideoutRepresentation), nameof(HideoutRepresentation.CompleteUpgradeZone));
        InvalidateOn(harmony, typeof(HideoutRepresentation), "method_37");
    }

    private static void InvalidateOn(Harmony harmony, System.Type type, string methodName)
    {
        var method = AccessTools.Method(type, methodName);
        if (method != null)
        {
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(WishlistCachePatch), nameof(Invalidate)));
        }
    }

    internal static void Invalidate()
    {
        _dirty = true;
    }

    private static void EnsureFresh(WishlistManager manager)
    {
        GameSettingsGroup.EAutoAddToWishlist option = WishlistManager.AutoAddHideoutOption;
        float now = Time.realtimeSinceStartup;
        if (!_dirty && option == _cachedOption && (now - _builtAt) < MaxCacheAgeSeconds)
        {
            return;
        }

        CachedTemplates.Clear();
        _cachedOption = option;
        _builtAt = now;
        _dirty = false;

        if (option == GameSettingsGroup.EAutoAddToWishlist.Disabled)
        {
            return;
        }

        if (!Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
        if (hideout == null)
        {
            return;
        }

        if (WishlistManager.AutoAddZones)
        {
            List<AreaData> areaDatas = hideout.AreaDatas;
            if (areaDatas != null)
            {
                for (int i = 0; i < areaDatas.Count; i++)
                {
                    AreaData area = areaDatas[i];
                    if (area == null || !area.Enabled || area.NextStage == null)
                    {
                        continue;
                    }

                    RelatedRequirements requirements = area.NextStage.Requirements;
                    if (requirements == null)
                    {
                        continue;
                    }

                    bool blockedByNonItem = false;
                    foreach (Requirement req in requirements)
                    {
                        if (req is not ItemRequirement && !req.Fulfilled)
                        {
                            blockedByNonItem = true;
                            break;
                        }
                    }

                    if (blockedByNonItem)
                    {
                        continue;
                    }

                    foreach (Requirement req in requirements)
                    {
                        if (req is ItemRequirement itemReq
                            && !string.IsNullOrEmpty(itemReq.TemplateId)
                            && WishlistManager.IsAvailableForWishlist(itemReq.Item))
                        {
                            CachedTemplates.Add((MongoID)itemReq.TemplateId);
                        }
                    }
                }
            }
        }

        if (WishlistManager.AutoAddRecipes && hideout.FavoriteProductionSchemes != null)
        {
            foreach (ProductionScheme scheme in hideout.FavoriteProductionSchemes)
            {
                if (scheme == null || !scheme.HasRequirements)
                {
                    continue;
                }

                foreach (ItemRequirement req in scheme.RequiredItems)
                {
                    if (!string.IsNullOrEmpty(req.TemplateId) && WishlistManager.IsAvailableForWishlist(req.Item))
                    {
                        CachedTemplates.Add((MongoID)req.TemplateId);
                    }
                }
            }
        }
    }

    private static bool IsInHideoutQolWishlistPrefix(WishlistManager __instance, MongoID templateId, ref bool __result)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.WishlistCache.Value || __instance == null)
        {
            return true;
        }

        if (__instance._userItems != null && __instance._userItems.ContainsKey(templateId))
        {
            __result = false;
            return false;
        }

        EnsureFresh(__instance);
        __result = CachedTemplates.Contains(templateId);
        return false;
    }

    private static bool GetHideoutQolItemsPrefix(WishlistManager __instance, ref HashSet<MongoID> __result)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.WishlistCache.Value || __instance == null)
        {
            return true;
        }

        EnsureFresh(__instance);
        var result = new HashSet<MongoID>();
        var userItems = __instance._userItems;
        foreach (MongoID id in CachedTemplates)
        {
            if (userItems == null || !userItems.ContainsKey(id))
            {
                result.Add(id);
            }
        }

        __result = result;
        return false;
    }
}
