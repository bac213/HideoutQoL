using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DG.Tweening;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Eliminates Hideout crafting list-build O(N^2) Canvas stalls, multi-frame substrate open latency,
/// event storms, redundant full refreshes, unthrottled per-frame updates, and unnecessary panel teardowns:
/// 1) Virtualizes <see cref="ProductionPanel.ShowProduceView"/> via <see cref="VirtualizedProduceList"/> +
///    persistent <see cref="GlobalProduceCardPool"/> so only ~7-9 visible recipe cards exist at once.
/// 2) Accelerates <see cref="AreaScreenSubstrate.CreateViewList"/> (creates the 2-3 area sub-panels in 1 pass
///    instead of 5-6 frame yields) and shortens <see cref="AreaScreenSubstrate.ViewListCreated"/>'s 300ms fade to 60ms.
/// 3) Removes duplicate <see cref="ItemsProducerBase.OnProduceStatusChanged"/> subscription on <see cref="ProduceView"/>.
/// 4) Coalesces <see cref="ProductionPanel.UpdateViews"/> and <see cref="ProductionPanel.ItemChangedHandler"/> to at most
///    one flush per frame in <c>LateUpdate</c>, and skips the redundant full refresh immediately after <c>ShowProduceView</c>.
/// 5) Makes <see cref="ProductionPanel.UpdateOrder"/> drive the virtualized list or no-op when visual order is unchanged.
/// 6) Throttles <see cref="PermanentProductionView.Update"/> to 0.5s (matching <see cref="FarmingView"/>).
/// 7) Prevents <see cref="AreaScreenSubstrate.method_5"/> from tearing down the active production panel when area upgrade
///    status toggles between LockedToUpgrade and ReadyToUpgrade while crafting.
/// </summary>
internal static class ProductionFlowPatch
{
    private const float FastPanelFadeSeconds = 0.06f;

    private static readonly FieldInfo PanelViewsField =
        AccessTools.Field(typeof(ProductionPanel), "_views");
    private static readonly FieldInfo PanelViewListField =
        AccessTools.Field(typeof(ProductionPanel), "_viewList");

    private static readonly FieldInfo SubstrateDataToShowField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_dataToShow");
    private static readonly FieldInfo SubstrateChosenDataField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_chosenData");
    private static readonly FieldInfo SubstrateInventoryControllerField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_inventoryController");
    private static readonly FieldInfo SubstrateSessionField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_session");
    private static readonly FieldInfo SubstrateSelectedLevelField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_selectedLevel");
    private static readonly FieldInfo SubstrateCurrentStateField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_currentState");
    private static readonly FieldInfo SubstrateCurrentViewListField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_currentViewList");
    private static readonly FieldInfo SubstrateShowOnEnableField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_showOnEnable");
    private static readonly FieldInfo SubstrateStateChangedTokenField =
        AccessTools.Field(typeof(AreaScreenSubstrate), "_stateChangedToken");

    private sealed class TimerBox
    {
        public float LastUpdateTime = -10f;
    }

    private static readonly ConditionalWeakTable<PermanentProductionView, TimerBox> PermanentTimers = new();
    private static int _method4Depth;

    internal static void Apply(Harmony harmony)
    {
        // 1) Remove duplicate OnProduceStatusChanged listener on ProduceView
        MethodInfo produceViewShow = AccessTools.Method(
            typeof(ProduceView),
            nameof(ProduceView.Show),
            new[]
            {
                typeof(ItemUiContext),
                typeof(InventoryController),
                typeof(ProductionScheme),
                typeof(ItemsProducerBase),
                typeof(Action<string>),
                typeof(Action<string>),
                typeof(bool),
            });
        harmony.Patch(produceViewShow, postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ProduceViewShowPostfix)));

        // 2) ProductionPanel virtualization, coalescing, skip post-populate redundant full refresh, diff-based UpdateOrder
        harmony.Patch(
            AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.ShowProduceView)),
            prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ShowProduceViewPrefix)),
            postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ShowProduceViewPostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.UpdateViews)),
            prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(UpdateViewsPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.ItemChangedHandler)),
            prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ItemChangedHandlerPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.UpdateOrder)),
            prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(UpdateOrderPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.Close)),
            postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ProductionPanelClosePostfix)));

        // 3) Fast AreaScreenSubstrate panel creation (eliminates 5-frame wait) & snappy fade-in
        MethodInfo createViewList = AccessTools.Method(
            typeof(AreaScreenSubstrate),
            nameof(AreaScreenSubstrate.CreateViewList),
            new[] { typeof(AsyncViewList<RelatedData, IAreaRelatedPanel>), typeof(ELevelType) });
        if (createViewList != null)
        {
            harmony.Patch(createViewList, prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(SubstrateCreateViewListPrefix)));
        }

        MethodInfo viewListCreated = AccessTools.Method(
            typeof(AreaScreenSubstrate),
            nameof(AreaScreenSubstrate.ViewListCreated));
        if (viewListCreated != null)
        {
            harmony.Patch(viewListCreated, postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(SubstrateViewListCreatedPostfix)));
        }

        // 4) Clear persistent card pool cleanly on Hideout DeInit / Unload
        MethodInfo deInit = AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.DeInit));
        if (deInit != null)
        {
            harmony.Patch(deInit, postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ClearGlobalCardPool)));
        }
        MethodInfo unloadHideout = AccessTools.Method(typeof(TarkovApplication.HideoutController), nameof(TarkovApplication.HideoutController.UnloadHideout));
        if (unloadHideout != null)
        {
            harmony.Patch(unloadHideout, postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(ClearGlobalCardPool)));
        }

        // 5) Throttle PermanentProductionView.Update (water collector / booze generator)
        harmony.Patch(
            AccessTools.Method(typeof(PermanentProductionView), nameof(PermanentProductionView.Update)),
            prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(PermanentUpdatePrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(PermanentProductionView), nameof(PermanentProductionView.UpdateView)),
            postfix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(PermanentUpdateViewPostfix)));

        // 6) Prevent AreaScreenSubstrate.method_5 from tearing down active Current view on upgrade-status flip
        MethodInfo method4 = AccessTools.Method(typeof(AreaScreenSubstrate), "method_4");
        MethodInfo method5 = AccessTools.Method(typeof(AreaScreenSubstrate), "method_5");
        if (method4 != null && method5 != null)
        {
            harmony.Patch(
                method4,
                prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(SubstrateMethod4Prefix)),
                finalizer: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(SubstrateMethod4Finalizer)));
            harmony.Patch(
                method5,
                prefix: new HarmonyMethod(typeof(ProductionFlowPatch), nameof(SubstrateMethod5Prefix)));
        }
    }

    private static void ClearGlobalCardPool()
    {
        GlobalProduceCardPool.ClearAll();
    }

    private static void ProduceViewShowPostfix(ProduceView __instance)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value)
        {
            return;
        }

        // ProductionPanel already subscribes Producer.OnProduceStatusChanged += UpdateViews;
        // removing the per-card duplicate avoids running every card twice per status event.
        if (__instance?.Producer != null)
        {
            __instance.Producer.OnProduceStatusChanged -= __instance.UpdateView;
        }
    }

    private static bool ShowProduceViewPrefix(
        ProductionPanel __instance,
        ItemUiContext itemUiContext,
        ProduceView template,
        bool availableSearch,
        ItemsProducerBase itemsProducerBase,
        ref Task<bool> __result)
    {
        if (__instance == null || !HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.VirtualizeCraftList.Value)
        {
            return true;
        }

        IEnumerable<ProductionScheme> sortedSchemes = __instance.GetSortedSchemes(includeContinuous: false);
        if (sortedSchemes == null)
        {
            return true;
        }

        List<ProductionScheme> ordered = sortedSchemes.ToList();
        if (!VirtualizedProduceList.ShouldVirtualize(ordered.Count))
        {
            return true;
        }

        __instance._header.gameObject.SetActive(ordered.Count > 0);
        VirtualizedProduceList.GetOrCreate(__instance).Initialize(
            __instance,
            ordered,
            itemUiContext,
            template,
            availableSearch,
            itemsProducerBase);

        if (HideoutQoLPlugin.EventCoalescing.Value)
        {
            ProductionPanelCoalescer.GetOrCreate(__instance).SkipNextUpdateViewsFrame = Time.frameCount;
        }

        __result = Task.FromResult(true);
        return false;
    }

    private static void ShowProduceViewPostfix(ProductionPanel __instance, ref Task<bool> __result)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value || __result == null)
        {
            return;
        }

        if (__result.IsCompleted)
        {
            if (__result.Status == TaskStatus.RanToCompletion && __result.Result && __instance != null)
            {
                ProductionPanelCoalescer.GetOrCreate(__instance).SkipNextUpdateViewsFrame = Time.frameCount;
            }
            return;
        }

        __result = WrapShowProduceView(__instance, __result);
    }

    private static async Task<bool> WrapShowProduceView(ProductionPanel panel, Task<bool> original)
    {
        bool ok = await original;
        if (ok && panel != null)
        {
            ProductionPanelCoalescer.GetOrCreate(panel).SkipNextUpdateViewsFrame = Time.frameCount;
        }
        return ok;
    }

    private static bool SubstrateCreateViewListPrefix(
        AreaScreenSubstrate __instance,
        AsyncViewList<RelatedData, IAreaRelatedPanel> viewList,
        ELevelType levelType,
        ref Task __result)
    {
        if (__instance == null
            || viewList == null
            || !HideoutQoLPlugin.PerfActive
            || !HideoutQoLPlugin.VirtualizeCraftList.Value)
        {
            return true;
        }

        __result = FastCreateViewListAsync(__instance, viewList, levelType);
        return false;
    }

    private static async Task FastCreateViewListAsync(
        AreaScreenSubstrate substrate,
        AsyncViewList<RelatedData, IAreaRelatedPanel> viewList,
        ELevelType levelType)
    {
        bool stateChanged = false;
        (SubstrateStateChangedTokenField?.GetValue(substrate) as Action)?.Invoke();

        Action onStateChanged = null;
        onStateChanged = () =>
        {
            substrate.Event_0 -= onStateChanged;
            stateChanged = true;
        };
        substrate.Event_0 += onStateChanged;

        try
        {
            var dataToShow = SubstrateDataToShowField?.GetValue(substrate) as AreaData;
            var chosenData = SubstrateChosenDataField?.GetValue(substrate) as AreaData;
            var inventoryController = SubstrateInventoryControllerField?.GetValue(substrate) as InventoryController;
            var session = SubstrateSessionField?.GetValue(substrate) as IEftSession;

            if (substrate == null || dataToShow == null)
            {
                return;
            }

            Stage stage;
            RectTransform container;
            switch (levelType)
            {
                case ELevelType.Next:
                    stage = dataToShow.NextStage;
                    container = substrate._nextStageContainer;
                    break;
                case ELevelType.Current:
                    stage = dataToShow.CurrentStage;
                    container = substrate._currentStageContainer;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(levelType), levelType, null);
            }

            if (levelType == ELevelType.Current)
            {
                AvailableInteractionState availableActions = InteractionContextHelper.GetAvailableActions(chosenData, inventoryController);
                stage.Interactions = new RelatedInteractions { Data = availableActions };
            }

            Dictionary<EPanelType, RelatedData> dictionary = substrate.DataToDisplay(stage, levelType);
            if (stateChanged || substrate == null || SubstrateDataToShowField?.GetValue(substrate) == null)
            {
                return;
            }

            // Instantiate all 2-3 area sub-panels directly in 1 pass instead of 1-panel-per-frame AsyncViewList
            List<RelatedData> order = dictionary.Values.ToList();
            var showTasks = new List<Task>(order.Count);
            viewList.Init(
                order,
                item => viewList.InstantiateViewCopy(substrate.ChooseView(item)),
                _ => container,
                (item, view) => showTasks.Add(view.Show(item, stage, levelType, dataToShow, inventoryController, session)));

            for (int i = 0; i < order.Count; i++)
            {
                if (!viewList.IsAlive || stateChanged || substrate == null)
                {
                    return;
                }
                viewList.CreateView(order[i]);
            }

            // Single yield so Unity updates container.rect.height for ViewListCreated()
            await Task.Yield();
            if (stateChanged || substrate == null)
            {
                return;
            }

            if (showTasks.Count > 0 && container.rect.height < substrate._maxHeight)
            {
                Task allShow = Task.WhenAll(showTasks);
                while (!stateChanged && substrate != null && container.rect.height < substrate._maxHeight && !allShow.IsCompleted)
                {
                    await Task.Yield();
                }
            }

            if (stateChanged || substrate == null)
            {
                return;
            }

            substrate.Event_0 -= onStateChanged;
            onStateChanged = null;

            if (!substrate.gameObject.activeInHierarchy)
            {
                SubstrateShowOnEnableField?.SetValue(substrate, true);
            }
            else
            {
                await substrate.ViewListCreated();
            }
        }
        finally
        {
            if (onStateChanged != null && substrate != null)
            {
                substrate.Event_0 -= onStateChanged;
            }
        }
    }

    private static void SubstrateViewListCreatedPostfix(AreaScreenSubstrate __instance, ref Task __result)
    {
        if (__instance == null
            || __result == null
            || !HideoutQoLPlugin.PerfActive
            || !HideoutQoLPlugin.SkipAreaSelectDelay.Value)
        {
            return;
        }

        if (__result.IsCompleted)
        {
            ApplyFastSubstrateFade(__instance);
            return;
        }

        __result = WrapViewListCreatedFade(__instance, __result);
    }

    private static async Task WrapViewListCreatedFade(AreaScreenSubstrate substrate, Task original)
    {
        await original;
        ApplyFastSubstrateFade(substrate);
    }

    private static void ApplyFastSubstrateFade(AreaScreenSubstrate substrate)
    {
        if (substrate == null || SubstrateDataToShowField?.GetValue(substrate) == null)
        {
            return;
        }

        if (substrate.CanvasGroup != null)
        {
            substrate.CanvasGroup.DOKill();
            substrate.CanvasGroup.DOFade(1f, FastPanelFadeSeconds);
        }

        var currentState = (ELevelType)SubstrateCurrentStateField.GetValue(substrate);
        CanvasGroup activeGroup = currentState == ELevelType.Current ? substrate._currentStageGroup : substrate._nextStageGroup;
        CanvasGroup oldGroup = currentState == ELevelType.Next ? substrate._currentStageGroup : substrate._nextStageGroup;

        if (activeGroup != null)
        {
            activeGroup.DOKill();
            activeGroup.DOFade(1f, FastPanelFadeSeconds);
        }

        if (oldGroup != null && oldGroup.gameObject.activeSelf)
        {
            oldGroup.DOKill();
            oldGroup.alpha = 0f;
            oldGroup.gameObject.SetActive(false);
        }
    }

    private static bool UpdateViewsPrefix(ProductionPanel __instance)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value || __instance == null)
        {
            return true;
        }

        ProductionPanelCoalescer coalescer = ProductionPanelCoalescer.GetOrCreate(__instance);
        if (coalescer.SkipNextUpdateViewsFrame == Time.frameCount
            && PanelViewsField.GetValue(__instance) != null)
        {
            coalescer.SkipNextUpdateViewsFrame = -1;
            HideoutQoLPlugin.DebugPerf("Skipped redundant post-ShowProduceView UpdateViews()");
            return false;
        }

        coalescer.ScheduleFullRefresh();
        return false;
    }

    private static bool ItemChangedHandlerPrefix(ProductionPanel __instance, Item item)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value || __instance == null)
        {
            return true;
        }

        if (item != null)
        {
            ProductionPanelCoalescer.GetOrCreate(__instance).ScheduleTemplateRefresh(item.TemplateId);
        }
        return false;
    }

    private static bool UpdateOrderPrefix(ProductionPanel __instance)
    {
        if (__instance == null)
        {
            return true;
        }

        VirtualizedProduceList vlist = VirtualizedProduceList.GetExisting(__instance);
        if (vlist != null && vlist.IsActive)
        {
            vlist.UpdateOrder(__instance.GetSortedSchemes(includeContinuous: false));
            return false;
        }

        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value)
        {
            return true;
        }

        var viewList = PanelViewListField.GetValue(__instance) as AsyncViewList<ProductionScheme, ProduceView>;
        if (viewList == null || viewList.Views == null || viewList.Views.Count <= 1)
        {
            return false;
        }

        var orderedViews = new List<Transform>(viewList.Views.Count);
        foreach (ProductionScheme scheme in __instance.GetSortedSchemes(includeContinuous: true))
        {
            if (viewList.Views.TryGetValue(scheme, out ProduceView view) && view != null)
            {
                orderedViews.Add(view.Transform);
            }
        }

        if (orderedViews.Count <= 1)
        {
            return false;
        }

        bool alreadyOrdered = true;
        int prevIndex = orderedViews[0].GetSiblingIndex();
        for (int i = 1; i < orderedViews.Count; i++)
        {
            int idx = orderedViews[i].GetSiblingIndex();
            if (idx <= prevIndex)
            {
                alreadyOrdered = false;
                break;
            }
            prevIndex = idx;
        }

        if (alreadyOrdered)
        {
            return false;
        }

        int minSibling = int.MaxValue;
        for (int i = 0; i < orderedViews.Count; i++)
        {
            int idx = orderedViews[i].GetSiblingIndex();
            if (idx < minSibling)
            {
                minSibling = idx;
            }
        }

        for (int i = 0; i < orderedViews.Count; i++)
        {
            int targetIdx = minSibling + i;
            if (orderedViews[i].GetSiblingIndex() != targetIdx)
            {
                orderedViews[i].SetSiblingIndex(targetIdx);
            }
        }

        return false;
    }

    private static void ProductionPanelClosePostfix(ProductionPanel __instance)
    {
        if (__instance == null)
        {
            return;
        }

        VirtualizedProduceList.GetExisting(__instance)?.DisposeList();

        ProductionPanelCoalescer coalescer = __instance.GetComponent<ProductionPanelCoalescer>();
        if (coalescer != null)
        {
            coalescer.ResetState();
        }
    }

    private static bool PermanentUpdatePrefix(PermanentProductionView __instance)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.PermanentViewThrottle.Value || __instance == null)
        {
            return true;
        }

        TimerBox box = PermanentTimers.GetOrCreateValue(__instance);
        float now = Time.unscaledTime;
        if (now - box.LastUpdateTime < 0.5f)
        {
            return false;
        }

        box.LastUpdateTime = now;
        return true;
    }

    private static void PermanentUpdateViewPostfix(PermanentProductionView __instance)
    {
        if (__instance != null && PermanentTimers.TryGetValue(__instance, out TimerBox box))
        {
            box.LastUpdateTime = -10f;
        }
    }

    private static void SubstrateMethod4Prefix()
    {
        _method4Depth++;
    }

    private static void SubstrateMethod4Finalizer()
    {
        if (_method4Depth > 0)
        {
            _method4Depth--;
        }
    }

    private static bool SubstrateMethod5Prefix(AreaScreenSubstrate __instance)
    {
        if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.EventCoalescing.Value || __instance == null)
        {
            return true;
        }

        if (_method4Depth > 0)
        {
            return true;
        }

        var dataToShow = SubstrateDataToShowField.GetValue(__instance) as AreaData;
        if (dataToShow == null)
        {
            return true;
        }

        int selectedLevel = (int)SubstrateSelectedLevelField.GetValue(__instance);
        if (selectedLevel != dataToShow.CurrentLevel)
        {
            return false;
        }

        var currentState = (ELevelType)SubstrateCurrentStateField.GetValue(__instance);
        ELevelType appropriateState = __instance.GetAppropriateState();

        if (currentState != ELevelType.Current || appropriateState != ELevelType.Current)
        {
            return true;
        }

        var currentViewList = SubstrateCurrentViewListField.GetValue(__instance) as AsyncViewList<RelatedData, IAreaRelatedPanel>;
        if (currentViewList == null || !currentViewList.IsAlive || currentViewList.Count == 0)
        {
            return true;
        }

        Dictionary<EPanelType, RelatedData> expected = __instance.DataToDisplay(dataToShow.CurrentStage, ELevelType.Current);
        if (expected.Count != currentViewList.Count)
        {
            return true;
        }

        foreach (RelatedData existingData in currentViewList.Views.Keys)
        {
            if (existingData == null || !expected.ContainsKey(existingData.Type))
            {
                return true;
            }
        }

        var (displayStatus, text) = dataToShow.ActionButtonStatus;
        __instance._actionButton.enabled = displayStatus != EButtonDisplayStatus.Disabled;
        __instance._actionButton.SetHeaderText(text, 24);
        __instance._actionButton.Interactable = displayStatus == EButtonDisplayStatus.Enabled;
        __instance._areaStatusImage.sprite = __instance._areaPanelSettings[dataToShow.Status];
        __instance._areaDetailsPanel.Close();
        __instance._areaDetailsPanel.Show(dataToShow);

        HideoutQoLPlugin.DebugPerf($"Prevented AreaScreenSubstrate teardown on StatusUpdated ({dataToShow.Template.Type} -> {dataToShow.Status})");
        return false;
    }

    internal static Dictionary<BaseHideoutScheme, IProduceView> GetPanelViews(ProductionPanel panel)
        => PanelViewsField.GetValue(panel) as Dictionary<BaseHideoutScheme, IProduceView>;

    internal static AsyncViewList<ProductionScheme, ProduceView> GetPanelViewList(ProductionPanel panel)
        => PanelViewListField.GetValue(panel) as AsyncViewList<ProductionScheme, ProduceView>;
}

/// <summary>
/// Attached lazily to each <see cref="ProductionPanel"/> instance to coalesce same-frame view refreshes
/// and debounce search input.
/// </summary>
internal sealed class ProductionPanelCoalescer : MonoBehaviour
{
    private ProductionPanel _panel;
    private bool _dirtyAll;
    private readonly HashSet<MongoID> _dirtyTemplates = new();

    private bool _searchPending;
    private string _pendingSearchQuery;
    private float _searchExecuteAt;
    private Action<ProductionPanel, string> _searchExecutor;

    internal int SkipNextUpdateViewsFrame = -1;

    internal static ProductionPanelCoalescer GetOrCreate(ProductionPanel panel)
    {
        ProductionPanelCoalescer c = panel.GetComponent<ProductionPanelCoalescer>();
        if (c == null)
        {
            c = panel.gameObject.AddComponent<ProductionPanelCoalescer>();
        }
        c._panel = panel;
        return c;
    }

    internal void ScheduleFullRefresh()
    {
        _dirtyAll = true;
    }

    internal void ScheduleTemplateRefresh(MongoID templateId)
    {
        if (!_dirtyAll)
        {
            _dirtyTemplates.Add(templateId);
        }
    }

    internal void ScheduleSearch(string query, float delaySeconds, Action<ProductionPanel, string> executor)
    {
        _pendingSearchQuery = query;
        _searchExecutor = executor;
        if (delaySeconds <= 0f || string.IsNullOrEmpty(query))
        {
            _searchPending = false;
            executor(_panel, query);
            return;
        }

        _searchPending = true;
        _searchExecuteAt = Time.unscaledTime + delaySeconds;
    }

    internal void ResetState()
    {
        _dirtyAll = false;
        _dirtyTemplates.Clear();
        _searchPending = false;
        _pendingSearchQuery = null;
        SkipNextUpdateViewsFrame = -1;
    }

    private void OnDisable()
    {
        ResetState();
    }

    private void Update()
    {
        if (_searchPending && _panel != null && Time.unscaledTime >= _searchExecuteAt)
        {
            _searchPending = false;
            _searchExecutor?.Invoke(_panel, _pendingSearchQuery);
        }
    }

    private void LateUpdate()
    {
        if (_panel == null || (!_dirtyAll && _dirtyTemplates.Count == 0))
        {
            return;
        }

        Dictionary<BaseHideoutScheme, IProduceView> views = ProductionFlowPatch.GetPanelViews(_panel);
        if (views == null)
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        int updated = 0;

        if (_dirtyAll)
        {
            _dirtyAll = false;
            _dirtyTemplates.Clear();
            foreach (KeyValuePair<BaseHideoutScheme, IProduceView> kvp in views)
            {
                kvp.Value?.UpdateView();
                updated++;
            }
        }
        else
        {
            foreach (KeyValuePair<BaseHideoutScheme, IProduceView> kvp in views)
            {
                BaseHideoutScheme scheme = kvp.Key;
                if (scheme == null || !scheme.HasRequirements)
                {
                    continue;
                }

                bool affected = false;
                Requirement[] reqs = scheme.requirements;
                for (int i = 0; i < reqs.Length; i++)
                {
                    if (reqs[i] is ItemRequirement ir && !string.IsNullOrEmpty(ir.TemplateId) && _dirtyTemplates.Contains((MongoID)ir.TemplateId))
                    {
                        affected = true;
                        break;
                    }
                }

                if (affected)
                {
                    kvp.Value?.UpdateView();
                    updated++;
                }
            }
            _dirtyTemplates.Clear();
        }

        sw.Stop();
        if (updated > 0)
        {
            var (inPlace, layoutReuses) = RequirementViewPatch.ConsumeMetrics();
            HideoutQoLPlugin.DebugPerf(
                $"Coalesced refresh updated {updated} view(s) in {sw.Elapsed.TotalMilliseconds:F2} ms (inPlace={inPlace}, layoutReuses={layoutReuses})");
        }
    }
}
