using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Eliminates the #1 hideout crafting performance bottleneck:
/// 1) <see cref="HideoutProductionRequirementView.Show"/> previously tore down and re-instantiated every
///    material's <see cref="ItemView"/>, cloned a new scheme <see cref="Item"/>, ran wishlist full-scans,
///    and leaked duplicate disposables on every single <see cref="ProduceView.UpdateView"/> call.
///    We keep the existing <see cref="ItemView"/> in-place whenever the bound requirement template/flags
///    are unchanged and only refresh counts, fulfilled checkmark, tool border, and error state.
/// 2) <see cref="HideoutItemViewFactory.SetInfoLayout"/> previously called
///    <see cref="UnityEngine.Object.DestroyImmediate(UnityEngine.Object)"/> + <c>AddComponent</c> on the
///    info container's layout group on every <c>Show</c>, even when the layout axis was unchanged.
///    We reuse the existing matching <see cref="HorizontalOrVerticalLayoutGroup"/> in place.
/// </summary>
internal static class RequirementViewPatch
{
    private sealed class BoundRequirement
    {
        public string TemplateId;
        public bool IsSpawnedInSession;
        public bool IsEncoded;
        public EOwnerType OwnerType;
    }

    private static readonly ConditionalWeakTable<HideoutProductionRequirementView, BoundRequirement> BoundTable = new();

    private static int _inPlaceHits;
    private static int _layoutReuses;

    internal static void Apply(Harmony harmony)
    {
        MethodInfo setInfoLayout = AccessTools.Method(
            typeof(HideoutItemViewFactory),
            nameof(HideoutItemViewFactory.SetInfoLayout),
            new[] { typeof(GridLayoutGroup.Axis) });
        harmony.Patch(setInfoLayout, prefix: new HarmonyMethod(typeof(RequirementViewPatch), nameof(SetInfoLayoutPrefix)));

        MethodInfo reqShow = AccessTools.Method(
            typeof(HideoutProductionRequirementView),
            nameof(HideoutProductionRequirementView.Show),
            new[]
            {
                typeof(ItemUiContext),
                typeof(InventoryController),
                typeof(ItemRequirement),
                typeof(BaseHideoutScheme),
                typeof(IEnumerable<Item>),
                typeof(bool),
            });
        harmony.Patch(
            reqShow,
            prefix: new HarmonyMethod(typeof(RequirementViewPatch), nameof(RequirementShowPrefix)),
            postfix: new HarmonyMethod(typeof(RequirementViewPatch), nameof(RequirementShowPostfix)));
    }

    internal static (int inPlaceHits, int layoutReuses) ConsumeMetrics()
    {
        var result = (_inPlaceHits, _layoutReuses);
        _inPlaceHits = 0;
        _layoutReuses = 0;
        return result;
    }

    private static bool SetInfoLayoutPrefix(HideoutItemViewFactory __instance, GridLayoutGroup.Axis layoutType)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.InPlaceRequirementUpdate.Value)
        {
            return true;
        }

        if (__instance == null || __instance._infoContainer == null)
        {
            return true;
        }

        HorizontalOrVerticalLayoutGroup existing = __instance._infoContainer.GetComponent<HorizontalOrVerticalLayoutGroup>();
        HorizontalOrVerticalLayoutGroup source = null;

        if (layoutType == GridLayoutGroup.Axis.Vertical && existing is VerticalLayoutGroup)
        {
            source = __instance._verticalLayout;
        }
        else if (layoutType == GridLayoutGroup.Axis.Horizontal && existing is HorizontalLayoutGroup)
        {
            source = __instance._horizontalLayout;
        }

        if (source == null)
        {
            return true;
        }

        existing.padding = source.padding;
        existing.spacing = source.spacing;
        existing.childControlWidth = source.childControlWidth;
        existing.childControlHeight = source.childControlHeight;
        existing.childForceExpandWidth = source.childForceExpandWidth;
        existing.childForceExpandHeight = source.childForceExpandHeight;
        existing.childAlignment = source.childAlignment;
        _layoutReuses++;
        return false;
    }

    private static bool RequirementShowPrefix(
        HideoutProductionRequirementView __instance,
        ItemRequirement requirement,
        BaseHideoutScheme scheme,
        IEnumerable<Item> allItems,
        bool canStartProduction)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.InPlaceRequirementUpdate.Value)
        {
            return true;
        }

        if (__instance == null || requirement == null || scheme == null)
        {
            return true;
        }

        HideoutItemViewFactory factory = __instance._itemViewFactory;
        if (factory == null || factory.ItemView == null || factory.ItemView.gameObject == null)
        {
            return true;
        }

        if (!BoundTable.TryGetValue(__instance, out BoundRequirement bound))
        {
            return true;
        }

        if (!string.Equals(bound.TemplateId, requirement.TemplateId, StringComparison.Ordinal)
            || bound.IsSpawnedInSession != requirement.IsSpawnedInSession
            || bound.IsEncoded != requirement.IsEncoded
            || bound.OwnerType != scheme.SchemeOwnerType)
        {
            return true;
        }

        __instance.ShowGameObject();
        factory.ShowGameObject();

        requirement.Test(allItems);

        int countToShow = canStartProduction
            ? Mathf.Min(requirement.UserItemsCount, requirement.IntCount)
            : requirement.IntCount;

        __instance.IsFulfilled = countToShow >= requirement.IntCount && requirement.Error == null;
        factory.FulfilledStatus = __instance.IsFulfilled;
        factory.ShowInfo(canStartProduction, canStartProduction);
        __instance.ShowToolMarker(requirement is ToolRequirement);
        factory.SetError(canStartProduction ? requirement.Error : null);
        factory.SetCounterText(
            string.Format(
                "<color=#{2}><b>{0}</b></color>/{1}",
                countToShow.ToString("0.##"),
                requirement.IntCount.ToString("0.##"),
                "C5C3B2"));

        _inPlaceHits++;
        return false;
    }

    private static void RequirementShowPostfix(
        HideoutProductionRequirementView __instance,
        ItemRequirement requirement,
        BaseHideoutScheme scheme)
    {
        if (__instance == null || requirement == null || scheme == null)
        {
            return;
        }

        if (!BoundTable.TryGetValue(__instance, out BoundRequirement bound))
        {
            bound = new BoundRequirement();
            BoundTable.Add(__instance, bound);
        }

        bound.TemplateId = requirement.TemplateId;
        bound.IsSpawnedInSession = requirement.IsSpawnedInSession;
        bound.IsEncoded = requirement.IsEncoded;
        bound.OwnerType = scheme.SchemeOwnerType;
    }
}
