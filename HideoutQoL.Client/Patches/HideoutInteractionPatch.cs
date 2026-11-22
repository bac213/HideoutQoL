using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Hideout interaction QoL improvements:
/// 1) Removes the hardcoded 0.5s delay when switching Hideout areas (<see cref="HideoutScreenOverlay.AreaSelected"/>).
/// 2) Makes the Generator toggle cooldown configurable (default 0s instead of vanilla 10s).
/// 3) Enhances crafting search (<see cref="ProductionPanel.Search"/>) with debouncing, cached localized names,
///    and optional matching against required material/tool names.
/// </summary>
internal static class HideoutInteractionPatch
{
    private static readonly FieldInfo RefreshCooldownField =
        AccessTools.Field(typeof(ComplementaryButton), "_refreshCooldown");
    private static readonly FieldInfo TimerField =
        AccessTools.Field(typeof(ComplementaryButton), "_timer");
    private static readonly FieldInfo CanRefreshField =
        AccessTools.Field(typeof(ComplementaryButton), "_canRefresh");
    private static readonly FieldInfo ClickTimeField =
        AccessTools.Field(typeof(ComplementaryButton), "_clickTime");
    private static readonly FieldInfo ButtonCanvasGroupField =
        AccessTools.Field(typeof(ComplementaryButton), "_canvasGroup");

    private static WeakReference<ComplementaryButton> _lastGeneratorButton;

    // Cached search strings per scheme id, invalidated if locale or include-materials setting changes
    private static readonly Dictionary<string, string> SearchIndex = new(256, StringComparer.Ordinal);
    private static string _cachedLocaleSample;
    private static bool _cachedIncludeMaterials;

    internal static void Apply(Harmony harmony)
    {
        // 1) Skip hardcoded 0.5s wait in HideoutScreenOverlay.AreaSelected
        MethodInfo areaSelected = AccessTools.Method(
            typeof(HideoutScreenOverlay),
            nameof(HideoutScreenOverlay.AreaSelected),
            new[] { typeof(AreaData), typeof(bool) });
        harmony.Patch(areaSelected, prefix: new HarmonyMethod(typeof(HideoutInteractionPatch), nameof(AreaSelectedPrefix)));

        // 2) Configurable Generator cooldown
        MethodInfo enableGenBtn = AccessTools.Method(
            typeof(HideoutScreenOverlay),
            nameof(HideoutScreenOverlay.EnableGeneratorButton));
        harmony.Patch(enableGenBtn, postfix: new HarmonyMethod(typeof(HideoutInteractionPatch), nameof(EnableGeneratorButtonPostfix)));

        HideoutQoLPlugin.GeneratorCooldownSeconds.SettingChanged += OnGeneratorCooldownChanged;
        HideoutQoLPlugin.Enabled.SettingChanged += OnGeneratorCooldownChanged;

        // 3) Debounced, cached, material-aware crafting search
        MethodInfo searchMethod = AccessTools.Method(
            typeof(ProductionPanel),
            nameof(ProductionPanel.Search),
            new[] { typeof(string) });
        harmony.Patch(searchMethod, prefix: new HarmonyMethod(typeof(HideoutInteractionPatch), nameof(SearchPrefix)));
    }

    private static void AreaSelectedPrefix(ref bool wait)
    {
        if (HideoutQoLPlugin.PerfActive && HideoutQoLPlugin.SkipAreaSelectDelay.Value)
        {
            wait = false;
        }
    }

    private static void EnableGeneratorButtonPostfix(HideoutScreenOverlay __instance)
    {
        if (__instance == null || __instance._generatorButton == null)
        {
            return;
        }

        _lastGeneratorButton = new WeakReference<ComplementaryButton>(__instance._generatorButton);
        ApplyGeneratorCooldown(__instance._generatorButton);
    }

    private static void OnGeneratorCooldownChanged(object sender, EventArgs e)
    {
        if (_lastGeneratorButton != null
            && _lastGeneratorButton.TryGetTarget(out ComplementaryButton btn)
            && btn != null)
        {
            ApplyGeneratorCooldown(btn);
        }
    }

    private static void ApplyGeneratorCooldown(ComplementaryButton btn)
    {
        int cooldown = HideoutQoLPlugin.PerfActive
            ? Mathf.Clamp(HideoutQoLPlugin.GeneratorCooldownSeconds.Value, 0, 10)
            : 10;

        RefreshCooldownField?.SetValue(btn, cooldown);
        TimerField?.SetValue(btn, (float)cooldown);

        if (cooldown == 0)
        {
            ClickTimeField?.SetValue(btn, 0.0);
            CanRefreshField?.SetValue(btn, true);
            if (ButtonCanvasGroupField?.GetValue(btn) is CanvasGroup cg && cg != null && btn.IsUnlocked)
            {
                cg.alpha = 1f;
            }
        }
    }

    private static bool SearchPrefix(ProductionPanel __instance, string value)
    {
        if (__instance == null)
        {
            return true;
        }

        VirtualizedProduceList vlist = VirtualizedProduceList.GetExisting(__instance);
        bool virtualActive = vlist != null && vlist.IsActive;

        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.SearchEnhancements.Value)
        {
            if (virtualActive)
            {
                // Keep search functional when virtualized even if SearchEnhancements is toggled off
                if (string.IsNullOrEmpty(value))
                {
                    vlist.SetFilter(null);
                }
                else
                {
                    vlist.SetFilter(scheme =>
                        scheme != null && scheme.endProduct.LocalizedName().IndexOf(value, StringComparison.InvariantCultureIgnoreCase) >= 0);
                }
                return false;
            }
            return true;
        }

        float delaySec = Mathf.Clamp(HideoutQoLPlugin.SearchDebounceMs.Value, 0, 500) / 1000f;
        ProductionPanelCoalescer.GetOrCreate(__instance).ScheduleSearch(value, delaySec, ExecuteSearch);
        return false;
    }

    private static void ExecuteSearch(ProductionPanel panel, string rawQuery)
    {
        if (panel == null)
        {
            return;
        }

        VirtualizedProduceList vlist = VirtualizedProduceList.GetExisting(panel);
        bool virtualActive = vlist != null && vlist.IsActive;

        var viewList = virtualActive ? null : ProductionFlowPatch.GetPanelViewList(panel);
        if (!virtualActive && viewList == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            if (virtualActive)
            {
                vlist.SetFilter(null);
            }
            else
            {
                viewList.FilterBy(null);
            }
            return;
        }

        string query = rawQuery.Trim();
        bool includeMaterials = HideoutQoLPlugin.SearchIncludeMaterials.Value;

        // Cheap locale change detection via a standard Hideout localization key
        string localeProbe = "CONSTRUCT".Localized();
        if (!string.Equals(_cachedLocaleSample, localeProbe, StringComparison.Ordinal)
            || _cachedIncludeMaterials != includeMaterials)
        {
            SearchIndex.Clear();
            _cachedLocaleSample = localeProbe;
            _cachedIncludeMaterials = includeMaterials;
        }

        Func<ProductionScheme, bool> predicate = scheme =>
        {
            if (scheme == null)
            {
                return false;
            }
            string haystack = GetOrBuildSearchText(scheme, includeMaterials);
            return haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        };

        if (virtualActive)
        {
            vlist.SetFilter(predicate);
        }
        else
        {
            viewList.FilterBy(predicate);
        }
    }

    internal static string GetOrBuildSearchText(ProductionScheme scheme, bool includeMaterials)
    {
        string id = scheme._id ?? string.Empty;
        if (SearchIndex.TryGetValue(id, out string cached) && _cachedIncludeMaterials == includeMaterials)
        {
            return cached;
        }

        string localeProbe = "CONSTRUCT".Localized();
        if (!string.Equals(_cachedLocaleSample, localeProbe, StringComparison.Ordinal)
            || _cachedIncludeMaterials != includeMaterials)
        {
            SearchIndex.Clear();
            _cachedLocaleSample = localeProbe;
            _cachedIncludeMaterials = includeMaterials;
        }

        var sb = new StringBuilder(128);
        AppendTemplateNames(sb, scheme.endProduct);

        if (includeMaterials && scheme.HasRequirements)
        {
            Requirement[] reqs = scheme.requirements;
            for (int i = 0; i < reqs.Length; i++)
            {
                if (reqs[i] is ItemRequirement ir && !string.IsNullOrEmpty(ir.TemplateId))
                {
                    AppendTemplateNames(sb, (MongoID)ir.TemplateId);
                }
            }
        }

        string text = sb.ToString();
        SearchIndex[id] = text;
        return text;
    }

    internal static void AppendTemplateNames(StringBuilder sb, MongoID templateId)
    {
        if (string.IsNullOrEmpty(templateId))
        {
            return;
        }

        string fullName = templateId.LocalizedName();
        if (!string.IsNullOrEmpty(fullName))
        {
            sb.Append(fullName).Append('\n');
        }

        string shortName = templateId.LocalizedShortName();
        if (!string.IsNullOrEmpty(shortName) && !string.Equals(shortName, fullName, StringComparison.Ordinal))
        {
            sb.Append(shortName).Append('\n');
        }
    }
}
