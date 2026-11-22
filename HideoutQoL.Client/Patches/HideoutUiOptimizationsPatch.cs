using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Insurance;
using HarmonyLib;
using TMPro;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Targeted Hideout UI micro-optimizations discovered via live profiling:
/// 1) Unregisters <see cref="HideoutItemView"/> from <see cref="InventoryController.Views"/> so fake scheme preview
///    icons never receive <c>RaiseAddEvent</c>/<c>RaiseRemoveEvent</c>/<c>RaiseRefreshEvent</c> broadcasts on every
///    stash change (cuts <c>ApplyStashChanges</c> event dispatch by >90%).
/// 2) Deduplicates <see cref="HideoutItemViewFactory.SetCounterText"/> so unchanged counters do not dirty TMP meshes.
/// 3) Replaces <see cref="StringExtensions.SetMonospaceText"/>'s double synchronous <c>TMP_Text.GetTextInfo()</c>
///    calls with a pure-C# digit-run formatter + text-equality check (eliminates ~45ms TMP churn and prevents
///    <c>Mesh.vertices is too small</c> errors on CJK/uninitialized TMP objects).
/// 4) Fixes vanilla <see cref="ProduceView.UpdateButtonsActiveness"/> forgetting to re-enable
///    <c>_expectedTimePanel</c> when returning to idle state (also required for recycled virtualized cards).
/// </summary>
internal static class HideoutUiOptimizationsPatch
{
    private static readonly FieldInfo CounterFieldInfo =
        AccessTools.Field(typeof(HideoutItemViewFactory), "_counterField");
    private static readonly FieldInfo DigitSizesCacheField =
        AccessTools.Field(typeof(StringExtensions), "_digitSizesCache");

    [ThreadStatic]
    private static StringBuilder _monoSb;

    internal static void Apply(Harmony harmony)
    {
        // 1) Unregister HideoutItemView from InventoryController.Views
        MethodInfo newHideoutItemView = AccessTools.Method(
            typeof(HideoutItemView),
            nameof(HideoutItemView.NewHideoutItemView),
            new[]
            {
                typeof(Item),
                typeof(ItemContext),
                typeof(ItemRotation),
                typeof(ItemController),
                typeof(ItemUiContext),
                typeof(InsuranceCompany),
                typeof(WishlistManager),
                typeof(bool),
            });
        if (newHideoutItemView != null)
        {
            harmony.Patch(
                newHideoutItemView,
                postfix: new HarmonyMethod(typeof(HideoutUiOptimizationsPatch), nameof(NewHideoutItemViewPostfix)));
        }

        // 2) Skip unchanged SetCounterText assignments
        MethodInfo setCounterText = AccessTools.Method(
            typeof(HideoutItemViewFactory),
            nameof(HideoutItemViewFactory.SetCounterText),
            new[] { typeof(string) });
        if (setCounterText != null)
        {
            harmony.Patch(
                setCounterText,
                prefix: new HarmonyMethod(typeof(HideoutUiOptimizationsPatch), nameof(SetCounterTextPrefix)));
        }

        // 3) Fast zero-GetTextInfo SetMonospaceText
        MethodInfo setMonospaceText = AccessTools.Method(
            typeof(StringExtensions),
            nameof(StringExtensions.SetMonospaceText),
            new[] { typeof(TMP_Text), typeof(string), typeof(bool) });
        if (setMonospaceText != null)
        {
            harmony.Patch(
                setMonospaceText,
                prefix: new HarmonyMethod(typeof(HideoutUiOptimizationsPatch), nameof(SetMonospaceTextPrefix)));
        }

        // 4) Re-enable _expectedTimePanel when ProduceView returns to idle state
        MethodInfo updateButtons = AccessTools.Method(
            typeof(ProduceView),
            nameof(ProduceView.UpdateButtonsActiveness));
        if (updateButtons != null)
        {
            harmony.Patch(
                updateButtons,
                postfix: new HarmonyMethod(typeof(HideoutUiOptimizationsPatch), nameof(UpdateButtonsActivenessPostfix)));
        }
    }

    private static void NewHideoutItemViewPostfix(HideoutItemView __instance, ItemController itemController)
    {
        if (!HideoutQoLPlugin.PerfActive || __instance == null || itemController == null)
        {
            return;
        }

        // HideoutItemView displays cloned scheme items owned by throwaway controllers;
        // real InventoryController add/remove/refresh events can never target them.
        // ExamineEvent is subscribed directly on ItemController and stays intact.
        itemController.UnregisterView(__instance);
    }

    private static bool SetCounterTextPrefix(HideoutItemViewFactory __instance, string text)
    {
        if (!HideoutQoLPlugin.PerfActive || __instance == null || CounterFieldInfo == null)
        {
            return true;
        }

        if (CounterFieldInfo.GetValue(__instance) is TextMeshProUGUI field && field != null)
        {
            if (string.Equals(field.text, text, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SetMonospaceTextPrefix(TMP_Text textField, string text, bool onlyDigits)
    {
        if (!HideoutQoLPlugin.PerfActive || textField == null)
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            if (!string.IsNullOrEmpty(textField.text))
            {
                textField.text = text;
            }
            return false;
        }

        // Fast path handles plain digit-monospace strings (timers, percentages, durations)
        if (!onlyDigits || text.IndexOf('<') >= 0)
        {
            return true;
        }

        TMP_FontAsset font = textField.font;
        if (font == null)
        {
            return true;
        }

        float digitWidth = GetCachedDigitWidth(font);
        if (digitWidth <= 0f)
        {
            return true;
        }

        if ((textField.fontStyle & FontStyles.Bold) != 0)
        {
            digitWidth += font.boldSpacing / 100f;
        }

        var sb = _monoSb ?? (_monoSb = new StringBuilder(64));
        sb.Clear();

        string openTag = null;
        bool inDigits = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool isAsciiDigit = c >= '0' && c <= '9';
            if (isAsciiDigit)
            {
                if (!inDigits)
                {
                    openTag ??= "<mspace=" + digitWidth.ToString("F2", CultureInfo.InvariantCulture) + "em>";
                    sb.Append(openTag);
                    inDigits = true;
                }
                sb.Append(c);
            }
            else
            {
                if (inDigits)
                {
                    sb.Append("</mspace>");
                    inDigits = false;
                }
                sb.Append(c);
            }
        }

        if (inDigits)
        {
            sb.Append("</mspace>");
        }

        string formatted = sb.ToString();
        if (!string.Equals(textField.text, formatted, StringComparison.Ordinal))
        {
            textField.text = formatted;
        }
        return false;
    }

    private static float GetCachedDigitWidth(TMP_FontAsset font)
    {
        if (DigitSizesCacheField?.GetValue(null) is Dictionary<TMP_FontAsset, float> cache)
        {
            if (cache.TryGetValue(font, out float width))
            {
                return width;
            }
        }

        try
        {
            return StringExtensions.CacheFontDigits(font);
        }
        catch
        {
            return 0f;
        }
    }

    private static void UpdateButtonsActivenessPostfix(ProduceView __instance)
    {
        if (__instance == null || __instance._expectedTimePanel == null || __instance._productionPercentagesPanel == null)
        {
            return;
        }

        bool shouldShowExpectedTime = !__instance._productionPercentagesPanel.activeSelf;
        if (shouldShowExpectedTime && !__instance._expectedTimePanel.activeSelf)
        {
            __instance._expectedTimePanel.SetActive(true);
        }
    }
}
