using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InputSystem;
using EFT.UI;
using HarmonyLib;
using HideoutQoL.Client.UI;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Integrates <see cref="HideoutWorkshopScreen"/> into <see cref="HideoutScreenOverlay"/>:
/// - Intercepts built multi-recipe crafting facility clicks and opens the full 3-column Workshop Studio
///   directly instead of the cramped right drawer (preserving vanilla AreaScreenSubstrate for Lv.0,
///   special areas, and the [设施详情/升级] button).
/// - Adds a compact [工坊总览] button near the Hideout header for instant cross-station access.
/// - Handles Escape, Enter, Mouse Scroll, and WASD camera input isolation while Workshop is open.
/// </summary>
internal static class WorkshopUiPatch
{
    private static readonly FieldInfo AreaSubstrateField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_areaSubstrate");
    private static readonly FieldInfo CanSelectAreaField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_canSelectArea");
    private static readonly FieldInfo HideoutRearField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_hideoutRear");
    private static readonly FieldInfo CameraCtrlField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_hideoutCameraController");
    private static readonly FieldInfo SelectedDataField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_selectedData");

    private static bool _applied;
    private static bool _bypassNextAreaSelect;

    private static HideoutScreenOverlay _currentOverlay;
    private static HideoutWorkshopScreen _workshopScreen;
    private static DefaultUIButton _overviewButton;

    internal static bool IsWorkshopOpen =>
        _workshopScreen != null && _workshopScreen.gameObject.activeSelf;

    internal static void Apply(Harmony harmony)
    {
        if (_applied)
        {
            return;
        }
        _applied = true;

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.Show)),
            postfix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(OverlayShowPostfix)));

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.AreaSelected)),
            prefix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(AreaSelectedPrefix)));

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.ReturnToPreviousState)),
            prefix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(ReturnToPreviousStatePrefix)));

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.TranslateCommand)),
            prefix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(TranslateCommandPrefix)));

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.TranslateAxes)),
            prefix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(TranslateAxesPrefix)));

        harmony.Patch(
            AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.Close)),
            prefix: new HarmonyMethod(typeof(WorkshopUiPatch), nameof(OverlayClosePrefix)));

        HideoutQoLPlugin.Log?.LogInfo("HideoutQoL: patched HideoutScreenOverlay for HideoutWorkshopScreen");
    }

    internal static void RequestVanillaAreaDrawer(HideoutScreenOverlay overlay, AreaData area)
    {
        if (overlay == null || area == null)
        {
            return;
        }

        if (_workshopScreen != null && _workshopScreen.gameObject.activeSelf)
        {
            _workshopScreen.Close();
        }

        _bypassNextAreaSelect = true;
        try
        {
            overlay.AreaSelected(area, wait: false).ContinueWith(t =>
            {
                if (t.Exception != null)
                {
                    HideoutQoLPlugin.Log?.LogError($"HideoutQoL: AreaSelected error: {t.Exception}");
                }
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
        finally
        {
            _bypassNextAreaSelect = false;
        }
    }

    private static void OverlayShowPostfix(
        HideoutScreenOverlay __instance,
        HideoutPlayerOwner player,
        AreaData[] areaDatas)
    {
        _currentOverlay = __instance;

        if (!HideoutQoLPlugin.EnableWorkshopUi.Value)
        {
            if (_overviewButton != null)
            {
                _overviewButton.gameObject.SetActive(false);
            }
            return;
        }

        EftUiTemplateCache.Instance.EnsureInitialized(__instance);

        bool isGuest = player != null && player.IsGuest;
        EnsureOverviewButton(__instance, isGuest, areaDatas);
    }

    private static bool AreaSelectedPrefix(
        HideoutScreenOverlay __instance,
        AreaData data,
        ref Task __result)
    {
        if (_bypassNextAreaSelect)
        {
            return true;
        }

        if (!HideoutQoLPlugin.EnableWorkshopUi.Value || !HideoutQoLPlugin.WorkshopOpenOnAreaClick.Value)
        {
            return true;
        }

        if (__instance == null || data == null)
        {
            return true;
        }

        bool canSelect = CanSelectAreaField != null && (bool)CanSelectAreaField.GetValue(__instance);
        var rear = HideoutRearField?.GetValue(__instance) as HideoutScreenRear;
        if (!canSelect || rear == null || rear.Closed)
        {
            return true;
        }

        if (!WorkshopCatalogModel.IsWorkbenchEligibleArea(data))
        {
            return true;
        }

        OpenWorkshopForOverlay(__instance, data, null);
        __result = Task.CompletedTask;
        return false;
    }

    private static bool ReturnToPreviousStatePrefix(HideoutScreenOverlay __instance)
    {
        if (!IsWorkshopOpen)
        {
            return true;
        }

        var rear = HideoutRearField?.GetValue(__instance) as HideoutScreenRear;
        if (rear != null && !rear.Closed)
        {
            CloseWorkshopAndClearSelection(__instance);
        }
        return false;
    }

    private static bool TranslateCommandPrefix(
        HideoutScreenOverlay __instance,
        ECommand command,
        ref InputNode.ETranslateResult __result)
    {
        if (!IsWorkshopOpen)
        {
            return true;
        }

        switch (command)
        {
            case ECommand.Escape:
                if (_workshopScreen.TryCloseOpenPopup())
                {
                    __result = InputNode.ETranslateResult.Block;
                    return false;
                }
                CloseWorkshopAndClearSelection(__instance);
                __result = InputNode.ETranslateResult.Block;
                return false;

            case ECommand.Enter:
                __result = InputNode.ETranslateResult.Block;
                return false;

            case ECommand.ScrollPrevious:
            case ECommand.ScrollNext:
                __result = InputNode.ETranslateResult.Ignore;
                return false;
        }

        if (_workshopScreen.IsSearchFocused)
        {
            __result = InputNode.ETranslateResult.Block;
            return false;
        }

        return true;
    }

    private static bool TranslateAxesPrefix(ref float[] axes)
    {
        if (!IsWorkshopOpen)
        {
            return true;
        }

        axes = null;
        return false;
    }

    private static void OverlayClosePrefix(HideoutScreenOverlay __instance)
    {
        if (_workshopScreen != null && _workshopScreen.gameObject.activeSelf)
        {
            _workshopScreen.Close();
        }
        if (_overviewButton != null && __instance == _currentOverlay)
        {
            _overviewButton.gameObject.SetActive(false);
        }
        EftUiTemplateCache.Instance.ClearDisplayItemCache();
    }

    internal static void OpenWorkshopForOverlay(
        HideoutScreenOverlay overlay,
        AreaData focusArea,
        AreaData[] explicitAreas)
    {
        if (overlay == null)
        {
            return;
        }

        if (!EftUiTemplateCache.Instance.EnsureInitialized(overlay))
        {
            return;
        }

        // Close vanilla AreaScreenSubstrate if currently open
        if (AreaSubstrateField?.GetValue(overlay) is AreaScreenSubstrate substrate && substrate != null && substrate.gameObject.activeSelf)
        {
            substrate.Close();
        }

        if (focusArea != null)
        {
            overlay.SetSelectedArea(focusArea);
            if (CameraCtrlField?.GetValue(overlay) is HideoutCameraController cam && cam != null)
            {
                cam.AreaSelected = true;
            }
        }
        else
        {
            overlay.SetSelectedArea(null);
        }

        EnsureWorkshopScreen(overlay);

        AreaData[] areas = explicitAreas;
        if ((areas == null || areas.Length == 0) && Singleton<HideoutRepresentation>.Instantiated)
        {
            List<AreaData> list = Singleton<HideoutRepresentation>.Instance.AreaDatas;
            if (list != null)
            {
                areas = list.ToArray();
            }
        }

        EFT.EAreaType? initialStation = focusArea != null ? focusArea.Template.Type : null;
        _workshopScreen.Show(overlay, areas, initialStation, () => CloseWorkshopAndClearSelection(overlay));
    }

    private static void CloseWorkshopAndClearSelection(HideoutScreenOverlay overlay)
    {
        if (_workshopScreen != null && _workshopScreen.gameObject.activeSelf)
        {
            _workshopScreen.Close();
        }

        if (overlay != null && SelectedDataField?.GetValue(overlay) != null)
        {
            overlay.SetSelectedArea(null);
        }
    }

    private static void EnsureWorkshopScreen(HideoutScreenOverlay overlay)
    {
        Transform hostParent = overlay._areaScreenContainer != null
            ? overlay._areaScreenContainer
            : overlay.transform;

        if (_workshopScreen != null && _workshopScreen.transform.parent != hostParent)
        {
            UnityEngine.Object.Destroy(_workshopScreen.gameObject);
            _workshopScreen = null;
        }

        if (_workshopScreen != null)
        {
            _workshopScreen.transform.SetAsLastSibling();
            return;
        }

        var go = new GameObject("HideoutWorkshopScreen", typeof(RectTransform), typeof(CanvasGroup));
        go.transform.SetParent(hostParent, worldPositionStays: false);
        go.SetActive(false);

        _workshopScreen = go.AddComponent<HideoutWorkshopScreen>();
        _workshopScreen.BuildOnce(EftUiTemplateCache.Instance);
    }

    private static void EnsureOverviewButton(
        HideoutScreenOverlay overlay,
        bool isGuest,
        AreaData[] areaDatas)
    {
        DefaultUIButton anchorBtn = overlay._enterHideoutButtonSpawner;
        if (anchorBtn == null)
        {
            return;
        }

        if (_overviewButton == null)
        {
            DefaultUIButton template = EftUiTemplateCache.Instance.CompactButtonTemplate ?? anchorBtn;
            _overviewButton = UnityEngine.Object.Instantiate(template, anchorBtn.transform.parent, worldPositionStays: false);
            _overviewButton.name = "WorkshopOverviewButton";
            _overviewButton.SetIcon(null);
            _overviewButton.SetRawText("工坊总览", 16);
            _overviewButton.OnClick.RemoveAllListeners();

            RectTransform anchorRt = (RectTransform)anchorBtn.transform;
            RectTransform btnRt = (RectTransform)_overviewButton.transform;
            HorizontalLayoutGroup parentHlg = anchorBtn.transform.parent.GetComponent<HorizontalLayoutGroup>();
            if (parentHlg != null)
            {
                _overviewButton.transform.SetSiblingIndex(anchorBtn.transform.GetSiblingIndex());
                var le = _overviewButton.gameObject.GetOrAddComponent<LayoutElement>();
                le.preferredWidth = 112f;
                le.minWidth = 112f;
            }
            else
            {
                btnRt.anchorMin = anchorRt.anchorMin;
                btnRt.anchorMax = anchorRt.anchorMax;
                btnRt.pivot = anchorRt.pivot;
                float w = anchorRt.rect.width > 10f ? 116f : 116f;
                float h = anchorRt.rect.height > 10f ? anchorRt.rect.height : 34f;
                btnRt.sizeDelta = new Vector2(w, h);
                btnRt.anchoredPosition = anchorRt.anchoredPosition + new Vector2(-(anchorRt.rect.width * 0.5f + w * 0.5f + 12f), 0f);
            }
        }

        _overviewButton.OnClick.RemoveAllListeners();
        AreaData[] capturedAreas = areaDatas;
        _overviewButton.OnClick.AddListener(() =>
        {
            OpenWorkshopForOverlay(overlay, null, capturedAreas);
        });

        _overviewButton.gameObject.SetActive(!isGuest && HideoutQoLPlugin.EnableWorkshopUi.Value);
    }
}
