using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using EFT.UI.Insurance;
using EFT.UI.Ragfair;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Granular wall-clock, CPU, pool, icon-cache, GC, and frame-hitch profiler for the entire
/// Hideout crafting workflow (entry -> area open -> craft list load -> start craft -> collect).
/// Active only when <see cref="HideoutQoLPlugin.PerfLogging"/> is enabled.
/// </summary>
internal static class HideoutProfilerPatch
{
    private sealed class Counter
    {
        public int Count;
        public double TotalMs;
        public double MaxMs;

        public void Add(double ms)
        {
            Count++;
            TotalMs += ms;
            if (ms > MaxMs)
            {
                MaxMs = ms;
            }
        }

        public void Reset()
        {
            Count = 0;
            TotalMs = 0.0;
            MaxMs = 0.0;
        }

        public override string ToString() => $"{Count}x {TotalMs:F1}ms (max {MaxMs:F1}ms)";
    }

    private sealed class ListLoadSession
    {
        public string AreaName;
        public long StartTicks;
        public int StartFrame;
        public int Gc0Start;
        public int Gc1Start;
        public int Gc2Start;

        public readonly Counter ProduceViewShow = new();
        public readonly Counter ReqViewShow = new();
        public readonly Counter FactoryShow = new();
        public readonly Counter HideoutItemCreate = new();
        public readonly Counter IconChanged = new();
        public readonly Counter GetItemIcon = new();
        public readonly Counter SetMonospace = new();
        public readonly Counter GetSchemeItem = new();

        public int EmptyPoolCreates;
        public int IconMemHits;
        public int IconDiskLoads;
        public int Icon3DRenders;

        public bool VirtualizedRan;
        public int VirtualizedTotal;
        public int VirtualizedVisible;
        public float VirtualizedCardH;
        public float VirtualizedSpacing;
        public double VirtualizedInitMs;
    }

    private static readonly FieldInfo UiPoolsDictField =
        AccessTools.Field(typeof(UiPools), "_pools");
    private static readonly FieldInfo HandoverPlayerItemsField =
        AccessTools.Field(typeof(HandoverExchangeableItemsWindow), "_playerItems");
    private static readonly FieldInfo HandoverFilteredItemsField =
        AccessTools.Field(typeof(HandoverExchangeableItemsWindow), "_filteredPlayerItems");

    private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    private static ListLoadSession _activeLoad;
    private static long _areaSelectedTicks;
    private static string _areaSelectedName = string.Empty;
    private static long _craftStartTicks;
    private static long _handoverAcceptTicks;
    private static float _lastHideoutActivityUnscaledTime = -999f;
    private static string _lastAction = "none";

    // Active scope stack for frame-hitch attribution
    private static readonly List<string> ActiveScopes = new(8);

    // ApplyStashChanges nested counters
    private static int _applyStashDepth;
    private static readonly Counter StashRaiseAdd = new();
    private static readonly Counter StashRaiseRemove = new();
    private static readonly Counter StashRaiseRefresh = new();
    private static int _lastItemControllerViewsCount;

    // Handover Show nested timing
    private static int _handoverShowDepth;
    private static double _handoverUpdateItemsMs;
    private static double _handoverGridUpdateMs;

    private static bool Enabled => HideoutQoLPlugin.PerfLogging != null && HideoutQoLPlugin.PerfLogging.Value;

    private static double ElapsedMs(long startTicks) => (Stopwatch.GetTimestamp() - startTicks) * TicksToMs;

    private static void MarkActivity(string action)
    {
        _lastHideoutActivityUnscaledTime = Time.unscaledTime;
        _lastAction = action;
    }

    private static void LogProf(string msg)
    {
        HideoutQoLPlugin.Log.LogInfo($"[HideoutQoL Prof] {msg}");
    }

    internal static void Apply(Harmony harmony)
    {
        // Attach hitch monitor
        if (HideoutQoLPlugin.Instance != null && HideoutQoLPlugin.Instance.GetComponent<HideoutHitchMonitor>() == null)
        {
            HideoutQoLPlugin.Instance.gameObject.AddComponent<HideoutHitchMonitor>();
        }

        // 1) Hideout entry
        PatchPair(harmony, AccessTools.Method(typeof(TarkovApplication.HideoutController), "method_2"),
            nameof(HideoutLoad2Prefix), nameof(HideoutLoad2Postfix));
        PatchPair(harmony, AccessTools.Method(typeof(TarkovApplication.HideoutController), "method_4"),
            nameof(HideoutLoad4Prefix), nameof(HideoutLoad4Postfix));

        // 2) Area selection & substrate lifecycle
        PatchPair(harmony, AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.AreaSelected), new[] { typeof(AreaData), typeof(bool) }),
            nameof(AreaSelectedPrefix), nameof(AreaSelectedPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutScreenOverlay), nameof(HideoutScreenOverlay.SetSelectedArea), new[] { typeof(AreaData) }),
            nameof(SetSelectedAreaPrefix), nameof(SetSelectedAreaPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(AreaScreenSubstrate), nameof(AreaScreenSubstrate.SelectArea), new[] { typeof(AreaData) }),
            nameof(SubstrateSelectAreaPrefix), nameof(SubstrateSelectAreaPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(AreaScreenSubstrate), nameof(AreaScreenSubstrate.SetState), new[] { typeof(ELevelType), typeof(bool) }),
            nameof(SubstrateSetStatePrefix), nameof(SubstrateSetStatePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(AreaScreenSubstrate), nameof(AreaScreenSubstrate.ViewListCreated)),
            nameof(ViewListCreatedPrefix), nameof(ViewListCreatedPostfix));

        // 3) ProductionPanel & ProduceView list build
        PatchPair(harmony, AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.ShowContents)),
            nameof(ShowContentsPrefix), nameof(ShowContentsPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.ShowProduceView)),
            nameof(ShowProduceViewPrefix), nameof(ShowProduceViewPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.StartProducing), new[] { typeof(string) }),
            nameof(PanelStartProducingPrefix), nameof(PanelStartProducingPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProductionPanel), nameof(ProductionPanel.GetProducedItems), new[] { typeof(string) }),
            nameof(PanelGetProducedItemsPrefix), nameof(PanelGetProducedItemsPostfix));

        PatchPair(harmony, AccessTools.Method(typeof(ProduceView), nameof(ProduceView.Show), new[]
            {
                typeof(ItemUiContext), typeof(InventoryController), typeof(ProductionScheme),
                typeof(ItemsProducerBase), typeof(Action<string>), typeof(Action<string>), typeof(bool)
            }),
            nameof(ProduceViewShowPrefix), nameof(ProduceViewShowPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProduceView), nameof(ProduceView.UpdateView)),
            nameof(ProduceViewUpdatePrefix), nameof(ProduceViewUpdatePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProduceView), nameof(ProduceView.StartProducing)),
            nameof(ProduceViewStartClickPrefix), null);

        // 4) Sub-element hot calls (accumulated during list load)
        PatchPair(harmony, AccessTools.Method(typeof(HideoutProductionRequirementView), nameof(HideoutProductionRequirementView.Show), new[]
            {
                typeof(ItemUiContext), typeof(InventoryController), typeof(ItemRequirement),
                typeof(BaseHideoutScheme), typeof(IEnumerable<Item>), typeof(bool)
            }),
            nameof(ReqViewShowPrefix), nameof(ReqViewShowPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutItemViewFactory), nameof(HideoutItemViewFactory.Show), new[]
            {
                typeof(Item), typeof(InventoryController), typeof(ItemUiContext)
            }),
            nameof(FactoryShowPrefix), nameof(FactoryShowPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutItemView), nameof(HideoutItemView.Create), new[]
            {
                typeof(Item), typeof(ItemContext), typeof(ItemRotation), typeof(ItemController),
                typeof(ItemUiContext), typeof(InsuranceCompany), typeof(WishlistManager), typeof(bool)
            }),
            nameof(HideoutItemCreatePrefix), nameof(HideoutItemCreatePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemView), nameof(ItemView.IconChangedHandler)),
            nameof(IconChangedPrefix), nameof(IconChangedPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemIconCreator), nameof(ItemIconCreator.GetItemIcon), new[]
            {
                typeof(Item), typeof(IntVec2).MakeByRefType(), typeof(bool)
            }),
            nameof(GetItemIconPrefix), nameof(GetItemIconPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemIconCreator), nameof(ItemIconCreator.RenderModel)),
            nameof(RenderModelPrefix), nameof(RenderModelPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(StringExtensions), nameof(StringExtensions.SetMonospaceText), new[]
            {
                typeof(TMP_Text), typeof(string), typeof(bool)
            }),
            nameof(SetMonospacePrefix), nameof(SetMonospacePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemFactory), nameof(ItemFactory.GetHideoutSchemeItem)),
            nameof(GetSchemeItemPrefix), nameof(GetSchemeItemPostfix));

        // 5) Start craft / Handover / Collect / Stash sync / Backend profile update
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.StartSingleProduction), new[] { typeof(BaseHideoutScheme), typeof(Action) }),
            nameof(StartSingleProductionPrefix), nameof(StartSingleProductionPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.GetItemReferences), new[] { typeof(ItemRequirement[]) }),
            nameof(GetItemRefsPrefix), nameof(GetItemRefsPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), nameof(HideoutRepresentation.GetProducedItems), new[] { typeof(ItemsProducerBase), typeof(string), typeof(bool) }),
            nameof(RepGetProducedItemsPrefix), nameof(RepGetProducedItemsPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), "method_24"),
            nameof(RepMethod24Prefix), nameof(RepMethod24Postfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), "method_25"),
            nameof(RepMethod25Prefix), nameof(RepMethod25Postfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutRepresentation), "method_37"),
            nameof(RepMethod37Prefix), nameof(RepMethod37Postfix));

        PatchPair(harmony, AccessTools.Method(typeof(HandoverExchangeableItemsWindow), nameof(HandoverExchangeableItemsWindow.Show), new[]
            {
                typeof(EExchangeableWindowType), typeof(Dictionary<IExchangeable, int>), typeof(bool),
                typeof(Profile), typeof(InventoryController), typeof(Action<CommoditiesToPurchase>),
                typeof(bool), typeof(Predicate<Item>)
            }),
            nameof(HandoverShowPrefix), nameof(HandoverShowPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HandoverExchangeableItemsWindow), nameof(HandoverExchangeableItemsWindow.UpdateItems), new[] { typeof(IEnumerable<Item>) }),
            nameof(HandoverUpdateItemsPrefix), nameof(HandoverUpdateItemsPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HandoverItemsWindow), nameof(HandoverItemsWindow.UpdateItems), new[] { typeof(IEnumerable<Item>) }),
            nameof(HandoverBaseUpdateItemsPrefix), nameof(HandoverBaseUpdateItemsPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HandoverExchangeableItemsWindow), nameof(HandoverExchangeableItemsWindow.Accept)),
            nameof(HandoverAcceptPrefix), nameof(HandoverAcceptPostfix));

        PatchPair(harmony, AccessTools.Method(typeof(ProfileUpdatesHandler), "EFT.IProfileUpdatesHandler.UpdateProfile"),
            nameof(UpdateProfilePrefix), nameof(UpdateProfilePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ProfileUpdatesHandler), nameof(ProfileUpdatesHandler.ApplyStashChanges)),
            nameof(ApplyStashChangesPrefix), nameof(ApplyStashChangesPostfix));

        PatchPair(harmony, AccessTools.Method(typeof(ItemController), nameof(ItemController.RaiseAddEvent), new[] { typeof(AddItemEventArgs) }),
            nameof(RaiseAddPrefix), nameof(RaiseAddPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemController), nameof(ItemController.RaiseRemoveEvent), new[] { typeof(RemoveItemEventArgs) }),
            nameof(RaiseRemovePrefix), nameof(RaiseRemovePostfix));
        PatchPair(harmony, AccessTools.Method(typeof(ItemController), nameof(ItemController.RaiseEvent), new[] { typeof(RefreshItemEventArgs) }),
            nameof(RaiseRefreshPrefix), nameof(RaiseRefreshPostfix));

        PatchPair(harmony, AccessTools.Method(typeof(HideoutArea), nameof(HideoutArea.SpawnProducedItems), new[] { typeof(Item[]) }),
            nameof(SpawnProducedPrefix), nameof(SpawnProducedPostfix));
        PatchPair(harmony, AccessTools.Method(typeof(HideoutArea), nameof(HideoutArea.RemoveProducedItems), new[] { typeof(IEnumerable<Item>) }),
            nameof(RemoveProducedPrefix), nameof(RemoveProducedPostfix));
    }

    private static void PatchPair(Harmony harmony, MethodInfo target, string prefixName, string postfixName)
    {
        if (target == null)
        {
            HideoutQoLPlugin.Log.LogWarning($"HideoutProfilerPatch: missing target for {prefixName ?? postfixName}");
            return;
        }

        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(HideoutProfilerPatch), prefixName) { priority = Priority.First } : null;
        HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(HideoutProfilerPatch), postfixName) { priority = Priority.Last } : null;
        harmony.Patch(target, prefix: prefix, postfix: postfix);
    }

    internal static void RecordVirtualizedInit(
        string areaName,
        int total,
        int visible,
        float cardH,
        float spacing,
        double initMs)
    {
        if (!Enabled)
        {
            return;
        }

        if (_activeLoad != null)
        {
            _activeLoad.VirtualizedRan = true;
            _activeLoad.VirtualizedTotal = total;
            _activeLoad.VirtualizedVisible = visible;
            _activeLoad.VirtualizedCardH = cardH;
            _activeLoad.VirtualizedSpacing = spacing;
            _activeLoad.VirtualizedInitMs = initMs;
        }
        else
        {
            LogProf($"VirtualizedProduceList({areaName}) total={total} visible={visible} cardH={cardH:F1} spacing={spacing:F1} init={initMs:F1}ms");
        }
    }

    // --- 1) Hideout entry ---

    private static void HideoutLoad2Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity("HideoutEntry.method_2");
            ActiveScopes.Add("HideoutEntry");
        }
    }

    private static void HideoutLoad2Postfix(long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        __result = WrapTask(__result, __state, "HideoutEntry.method_2(LoadHideoutResources)", "HideoutEntry");
    }

    private static void HideoutLoad4Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void HideoutLoad4Postfix(long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        __result = WrapTask(__result, __state, "HideoutEntry.method_4(CraftBundles)", null);
    }

    // --- 2) Area selection & substrate ---

    private static void AreaSelectedPrefix(AreaData data, bool wait, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            _areaSelectedTicks = __state;
            _areaSelectedName = data != null ? data.Template.Type.ToString() : "null";
            MarkActivity($"AreaSelected({_areaSelectedName})");
            LogProf($"=== AreaSelected START area={_areaSelectedName} wait={wait} frame={Time.frameCount} ===");
        }
    }

    private static void AreaSelectedPostfix(long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        string area = _areaSelectedName;
        __result = WrapTask(__result, __state, $"AreaSelected({area}) DONE", null);
    }

    private static void SetSelectedAreaPrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void SetSelectedAreaPostfix(AreaData area, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (ms >= 2.0)
        {
            LogProf($"SetSelectedArea({area?.Template?.Type.ToString() ?? "null"}) {ms:F1}ms");
        }
    }

    private static void SubstrateSelectAreaPrefix(AreaData areaData, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"SelectArea({areaData?.Template?.Type})");
            ActiveScopes.Add("SelectArea");
        }
    }

    private static void SubstrateSelectAreaPostfix(AreaData areaData, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("SelectArea");
        double sinceClick = _areaSelectedTicks != 0 ? ElapsedMs(_areaSelectedTicks) : 0;
        LogProf($"AreaScreenSubstrate.SelectArea({areaData?.Template?.Type}) sync={ElapsedMs(__state):F1}ms (sinceClick={sinceClick:F1}ms)");
    }

    private static void SubstrateSetStatePrefix(ELevelType state, bool force, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            ActiveScopes.Add("SetState");
        }
    }

    private static void SubstrateSetStatePostfix(ELevelType state, bool force, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("SetState");
        LogProf($"AreaScreenSubstrate.SetState({state}, force={force}) sync={ElapsedMs(__state):F1}ms");
    }

    private static void ViewListCreatedPrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            double sinceClick = _areaSelectedTicks != 0 ? ElapsedMs(_areaSelectedTicks) : 0;
            LogProf($"AreaScreenSubstrate.ViewListCreated ENTER (panel visible! sinceClick={sinceClick:F1}ms, frame={Time.frameCount})");
        }
    }

    private static void ViewListCreatedPostfix(long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        __result = WrapTask(__result, __state, "AreaScreenSubstrate.ViewListCreated DONE", null);
    }

    // --- 3) ProductionPanel & ProduceView ---

    private static void ShowContentsPrefix(ProductionPanel __instance, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"ShowContents({__instance?.AreaData?.Template?.Type})");
            ActiveScopes.Add("ShowContents");
        }
    }

    private static void ShowContentsPostfix(ProductionPanel __instance, long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        string area = __instance?.AreaData?.Template?.Type.ToString() ?? "?";
        __result = WrapTask(__result, __state, $"ProductionPanel.ShowContents({area}) DONE", "ShowContents");
    }

    private static void ShowProduceViewPrefix(ProductionPanel __instance, out ListLoadSession __state)
    {
        if (!Enabled)
        {
            __state = null;
            return;
        }

        var session = new ListLoadSession
        {
            AreaName = __instance?.AreaData?.Template?.Type.ToString() ?? "?",
            StartTicks = Stopwatch.GetTimestamp(),
            StartFrame = Time.frameCount,
            Gc0Start = GC.CollectionCount(0),
            Gc1Start = GC.CollectionCount(1),
            Gc2Start = GC.CollectionCount(2),
        };
        _activeLoad = session;
        __state = session;
        MarkActivity($"ShowProduceView({session.AreaName})");
        ActiveScopes.Add("ShowProduceView");
    }

    private static void ShowProduceViewPostfix(ListLoadSession __state, ref Task<bool> __result)
    {
        if (__state == null || __result == null)
        {
            return;
        }

        __result = WrapShowProduceView(__result, __state);
    }

    private static async Task<bool> WrapShowProduceView(Task<bool> original, ListLoadSession s)
    {
        try
        {
            return await original;
        }
        finally
        {
            ActiveScopes.Remove("ShowProduceView");
            if (ReferenceEquals(_activeLoad, s))
            {
                _activeLoad = null;
            }

            double wallMs = ElapsedMs(s.StartTicks);
            double sinceClick = _areaSelectedTicks != 0 ? ElapsedMs(_areaSelectedTicks) : 0;
            int frames = Math.Max(1, Time.frameCount - s.StartFrame);
            int d0 = GC.CollectionCount(0) - s.Gc0Start;
            int d1 = GC.CollectionCount(1) - s.Gc1Start;
            int d2 = GC.CollectionCount(2) - s.Gc2Start;

            var sb = new StringBuilder(512);
            sb.AppendLine($"=== ShowProduceView({s.AreaName}) SUMMARY ===");
            sb.AppendLine($"  Wall={wallMs:F1}ms | SinceAreaClick={sinceClick:F1}ms | Frames={frames} ({wallMs / frames:F1}ms/f) | GC={d0}/{d1}/{d2}");
            sb.AppendLine($"  Virtualized: ran={s.VirtualizedRan} total={s.VirtualizedTotal} visible={s.VirtualizedVisible} cardH={s.VirtualizedCardH:F1} spacing={s.VirtualizedSpacing:F1} init={s.VirtualizedInitMs:F1}ms");
            sb.AppendLine($"  ProduceView.Show:     {s.ProduceViewShow}");
            sb.AppendLine($"  ReqView.Show:         {s.ReqViewShow}");
            sb.AppendLine($"  ItemViewFactory.Show: {s.FactoryShow}");
            sb.AppendLine($"  HideoutItem.Create:   {s.HideoutItemCreate} | emptyPool={s.EmptyPoolCreates}");
            sb.AppendLine($"  GetHideoutSchemeItem: {s.GetSchemeItem}");
            sb.AppendLine($"  GetItemIcon:          {s.GetItemIcon} | memHit={s.IconMemHits} diskLoad={s.IconDiskLoads} render3D={s.Icon3DRenders}");
            sb.AppendLine($"  IconChangedHandler:   {s.IconChanged}");
            sb.Append($"  SetMonospaceText:     {s.SetMonospace}");
            LogProf(sb.ToString());
        }
    }

    private static void PanelStartProducingPrefix(string schemeId, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"Panel.StartProducing({schemeId})");
            ActiveScopes.Add("Panel.StartProducing");
        }
    }

    private static void PanelStartProducingPostfix(string schemeId, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("Panel.StartProducing");
        double sinceAccept = _handoverAcceptTicks != 0 ? ElapsedMs(_handoverAcceptTicks) : 0;
        LogProf($"ProductionPanel.StartProducing({schemeId}) sync={ElapsedMs(__state):F1}ms (sinceHandoverAccept={sinceAccept:F1}ms)");
    }

    private static void PanelGetProducedItemsPrefix(string schemeId, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"Panel.GetProducedItems({schemeId})");
            ActiveScopes.Add("Panel.GetProducedItems");
        }
    }

    private static void PanelGetProducedItemsPostfix(string schemeId, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("Panel.GetProducedItems");
        LogProf($"ProductionPanel.GetProducedItems({schemeId}) sync={ElapsedMs(__state):F1}ms");
    }

    private static void ProduceViewShowPrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void ProduceViewShowPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.ProduceViewShow.Add(ElapsedMs(__state));
        }
    }

    private static void ProduceViewUpdatePrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void ProduceViewUpdatePostfix(ProduceView __instance, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (_activeLoad == null && ms >= 5.0)
        {
            LogProf($"ProduceView.UpdateView({__instance?.Scheme?._id}) SLOW {ms:F1}ms");
        }
    }

    private static void ProduceViewStartClickPrefix(ProduceView __instance)
    {
        if (!Enabled)
        {
            return;
        }

        _craftStartTicks = Stopwatch.GetTimestamp();
        string id = __instance?.Scheme?._id ?? "?";
        string endName = __instance?.Scheme != null ? __instance.Scheme.endProduct.LocalizedShortName() : "?";
        MarkActivity($"StartClick({endName})");
        LogProf($"=== StartProducing CLICK scheme={id} product='{endName}' frame={Time.frameCount} ===");
    }

    // --- 4) Sub-element accumulators ---

    private static void ReqViewShowPrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void ReqViewShowPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.ReqViewShow.Add(ElapsedMs(__state));
        }
    }

    private static void FactoryShowPrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void FactoryShowPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.FactoryShow.Add(ElapsedMs(__state));
        }
    }

    private static void HideoutItemCreatePrefix(out long __state)
    {
        if (!Enabled || _activeLoad == null)
        {
            __state = 0;
            return;
        }

        __state = Stopwatch.GetTimestamp();
        if (IsHideoutLayoutPoolEmpty())
        {
            _activeLoad.EmptyPoolCreates++;
        }
    }

    private static void HideoutItemCreatePostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.HideoutItemCreate.Add(ElapsedMs(__state));
        }
    }

    private static bool IsHideoutLayoutPoolEmpty()
    {
        try
        {
            UiPools pools = MonoBehaviourSingleton<UiPools>.Instance;
            if (pools == null || UiPoolsDictField == null)
            {
                return false;
            }
            if (UiPoolsDictField.GetValue(pools) is Dictionary<string, TaskCompletionSource<IAssetPool>> dict
                && dict.TryGetValue("Prefabs/UGUI/Layouts/hideout_layout", out var tcs)
                && tcs != null
                && tcs.Task.IsCompleted
                && tcs.Task.Result != null)
            {
                return tcs.Task.Result.CurrentPoolSize <= 0;
            }
        }
        catch
        {
        }
        return false;
    }

    private static void IconChangedPrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void IconChangedPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.IconChanged.Add(ElapsedMs(__state));
        }
    }

    private static void GetItemIconPrefix(ItemIconCreator __instance, Item item, bool forcedGeneration, out long __state)
    {
        if (!Enabled || _activeLoad == null)
        {
            __state = 0;
            return;
        }

        __state = Stopwatch.GetTimestamp();
        try
        {
            int hash = IconsHash.GetItemHash(item);
            if (!forcedGeneration && __instance.TryGetCachedIcon(hash, out ItemIcon cached) && !cached.IsGeneratedInRaid)
            {
                _activeLoad.IconMemHits++;
            }
            else if (!forcedGeneration && __instance.TryGetIconPath(hash, out _))
            {
                _activeLoad.IconDiskLoads++;
            }
            else
            {
                _activeLoad.Icon3DRenders++;
            }
        }
        catch
        {
        }
    }

    private static void GetItemIconPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.GetItemIcon.Add(ElapsedMs(__state));
        }
    }

    private static void RenderModelPrefix(Item item, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0 && IsRecentlyInHideout())
        {
            LogProf($"ItemIconCreator.RenderModel START item='{item?.ShortName?.Localized()}' ({item?.TemplateId})");
        }
    }

    private static void RenderModelPostfix(Item item, long __state, ref Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> __result)
    {
        if (__state == 0 || __result == null || !IsRecentlyInHideout())
        {
            return;
        }

        string name = item?.ShortName?.Localized() ?? "?";
        __result = WrapRenderModel(__result, __state, name);
    }

    private static async Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> WrapRenderModel(
        Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> original,
        long startTicks,
        string name)
    {
        try
        {
            return await original;
        }
        finally
        {
            LogProf($"ItemIconCreator.RenderModel DONE '{name}' wall={ElapsedMs(startTicks):F1}ms");
        }
    }

    private static void SetMonospacePrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void SetMonospacePostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.SetMonospace.Add(ElapsedMs(__state));
        }
    }

    private static void GetSchemeItemPrefix(out long __state)
    {
        __state = (Enabled && _activeLoad != null) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void GetSchemeItemPostfix(long __state)
    {
        if (__state != 0 && _activeLoad != null)
        {
            _activeLoad.GetSchemeItem.Add(ElapsedMs(__state));
        }
    }

    // --- 5) Craft start / Handover / Collect / ProfileUpdate ---

    private static void StartSingleProductionPrefix(BaseHideoutScheme scheme, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"StartSingleProduction({scheme?._id})");
            ActiveScopes.Add("StartSingleProduction");
        }
    }

    private static void StartSingleProductionPostfix(BaseHideoutScheme scheme, long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        __result = WrapTask(__result, __state, $"HideoutRep.StartSingleProduction({scheme?._id}) DONE", "StartSingleProduction");
    }

    private static void GetItemRefsPrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void GetItemRefsPostfix(ItemRequirement[] requirements, List<HideoutItemReference> __result, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (ms >= 1.0)
        {
            LogProf($"GetItemReferences(reqs={requirements?.Length ?? 0} -> refs={__result?.Count ?? 0}) {ms:F2}ms");
        }
    }

    private static void HandoverShowPrefix(EExchangeableWindowType windowType, out long __state)
    {
        if (!Enabled || windowType != EExchangeableWindowType.Hideout)
        {
            __state = 0;
            return;
        }

        __state = Stopwatch.GetTimestamp();
        _handoverShowDepth++;
        _handoverUpdateItemsMs = 0;
        _handoverGridUpdateMs = 0;
        MarkActivity("HandoverWindow.Show");
        ActiveScopes.Add("HandoverShow");
    }

    private static void HandoverShowPostfix(HandoverExchangeableItemsWindow __instance, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        _handoverShowDepth = Math.Max(0, _handoverShowDepth - 1);
        ActiveScopes.Remove("HandoverShow");
        int playerCount = (HandoverPlayerItemsField?.GetValue(__instance) as ICollection)?.Count ?? -1;
        int filteredCount = (HandoverFilteredItemsField?.GetValue(__instance) as ICollection)?.Count ?? -1;
        double sinceClick = _craftStartTicks != 0 ? ElapsedMs(_craftStartTicks) : 0;
        LogProf($"HandoverExchangeableItemsWindow.Show total={ElapsedMs(__state):F1}ms (updateItems={_handoverUpdateItemsMs:F1}ms, gridShow={_handoverGridUpdateMs:F1}ms, stash={playerCount}, filtered={filteredCount}, sinceClick={sinceClick:F1}ms)");
    }

    private static void HandoverUpdateItemsPrefix(out long __state)
    {
        __state = (Enabled && _handoverShowDepth > 0) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void HandoverUpdateItemsPostfix(long __state)
    {
        if (__state != 0)
        {
            _handoverUpdateItemsMs += ElapsedMs(__state);
        }
    }

    private static void HandoverBaseUpdateItemsPrefix(out long __state)
    {
        __state = (Enabled && _handoverShowDepth > 0) ? Stopwatch.GetTimestamp() : 0;
    }

    private static void HandoverBaseUpdateItemsPostfix(long __state)
    {
        if (__state != 0)
        {
            _handoverGridUpdateMs += ElapsedMs(__state);
        }
    }

    private static void HandoverAcceptPrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            _handoverAcceptTicks = __state;
            MarkActivity("HandoverWindow.Accept");
            ActiveScopes.Add("HandoverAccept");
            LogProf($"=== HandoverWindow.Accept CLICK frame={Time.frameCount} ===");
        }
    }

    private static void HandoverAcceptPostfix(long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("HandoverAccept");
        LogProf($"HandoverWindow.Accept sync={ElapsedMs(__state):F1}ms");
    }

    private static void RepGetProducedItemsPrefix(string schemeId, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"Collect({schemeId})");
            ActiveScopes.Add("GetProducedItems");
            LogProf($"=== GetProducedItems CLICK scheme={schemeId} frame={Time.frameCount} ===");
        }
    }

    private static void RepGetProducedItemsPostfix(string schemeId, long __state, ref Task<bool> __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        __result = WrapBoolTask(__result, __state, $"HideoutRep.GetProducedItems({schemeId}) DONE", "GetProducedItems");
    }

    private static void RepMethod24Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void RepMethod24Postfix(long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (ms >= 1.0 || IsRecentlyInHideout())
        {
            LogProf($"HideoutRep.method_24(rebuild+recalc) {ms:F2}ms");
        }
    }

    private static void RepMethod25Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void RepMethod25Postfix(HideoutRepresentation __instance, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (ms >= 1.0)
        {
            LogProf($"HideoutRep.method_25(rebuildStash items={__instance?._allStashItems?.Count ?? -1}) {ms:F2}ms");
        }
    }

    private static void RepMethod37Prefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void RepMethod37Postfix(long __state)
    {
        if (__state == 0)
        {
            return;
        }

        double ms = ElapsedMs(__state);
        if (ms >= 2.0)
        {
            LogProf($"HideoutRep.method_37(DecideStatus all areas) {ms:F1}ms");
        }
    }

    private static void UpdateProfilePrefix(out (long ticks, int g0, int g1, int g2) __state)
    {
        if (!Enabled || !IsRecentlyInHideout())
        {
            __state = default;
            return;
        }

        __state = (Stopwatch.GetTimestamp(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        ActiveScopes.Add("UpdateProfile");
    }

    private static void UpdateProfilePostfix((long ticks, int g0, int g1, int g2) __state)
    {
        if (__state.ticks == 0)
        {
            return;
        }

        ActiveScopes.Remove("UpdateProfile");
        double ms = ElapsedMs(__state.ticks);
        int d0 = GC.CollectionCount(0) - __state.g0;
        int d1 = GC.CollectionCount(1) - __state.g1;
        int d2 = GC.CollectionCount(2) - __state.g2;
        LogProf($"ProfileUpdatesHandler.UpdateProfile total={ms:F1}ms gc={d0}/{d1}/{d2}");
    }

    private static void ApplyStashChangesPrefix(StashChangesResponse stashChanges, out long __state)
    {
        if (!Enabled || !IsRecentlyInHideout())
        {
            __state = 0;
            return;
        }

        __state = Stopwatch.GetTimestamp();
        _applyStashDepth++;
        if (_applyStashDepth == 1)
        {
            StashRaiseAdd.Reset();
            StashRaiseRemove.Reset();
            StashRaiseRefresh.Reset();
            _lastItemControllerViewsCount = 0;
        }
        ActiveScopes.Add("ApplyStashChanges");
    }

    private static void ApplyStashChangesPostfix(StashChangesResponse stashChanges, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        ActiveScopes.Remove("ApplyStashChanges");
        _applyStashDepth = Math.Max(0, _applyStashDepth - 1);
        int del = stashChanges?.del?.Length ?? 0;
        int chg = stashChanges?.change?.Length ?? 0;
        int add = stashChanges?.@new?.Length ?? 0;
        LogProf($"ApplyStashChanges total={ElapsedMs(__state):F1}ms (del={del} chg={chg} new={add} | Views={_lastItemControllerViewsCount} | RaiseAdd={StashRaiseAdd} | RaiseRem={StashRaiseRemove} | RaiseRef={StashRaiseRefresh})");
    }

    private static void RaiseAddPrefix(ItemController __instance, out long __state)
    {
        __state = (Enabled && _applyStashDepth > 0) ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0 && __instance?.Views != null)
        {
            _lastItemControllerViewsCount = Math.Max(_lastItemControllerViewsCount, __instance.Views.Count);
        }
    }

    private static void RaiseAddPostfix(long __state)
    {
        if (__state != 0)
        {
            StashRaiseAdd.Add(ElapsedMs(__state));
        }
    }

    private static void RaiseRemovePrefix(ItemController __instance, out long __state)
    {
        __state = (Enabled && _applyStashDepth > 0) ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0 && __instance?.Views != null)
        {
            _lastItemControllerViewsCount = Math.Max(_lastItemControllerViewsCount, __instance.Views.Count);
        }
    }

    private static void RaiseRemovePostfix(long __state)
    {
        if (__state != 0)
        {
            StashRaiseRemove.Add(ElapsedMs(__state));
        }
    }

    private static void RaiseRefreshPrefix(ItemController __instance, out long __state)
    {
        __state = (Enabled && _applyStashDepth > 0) ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0 && __instance?.Views != null)
        {
            _lastItemControllerViewsCount = Math.Max(_lastItemControllerViewsCount, __instance.Views.Count);
        }
    }

    private static void RaiseRefreshPostfix(long __state)
    {
        if (__state != 0)
        {
            StashRaiseRefresh.Add(ElapsedMs(__state));
        }
    }

    private static void SpawnProducedPrefix(HideoutArea __instance, Item[] items, out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
        if (__state != 0)
        {
            MarkActivity($"SpawnProduced({__instance?.AreaTemplate?.Type})");
        }
    }

    private static void SpawnProducedPostfix(HideoutArea __instance, Item[] items, long __state, ref Task __result)
    {
        if (__state == 0 || __result == null)
        {
            return;
        }

        string label = $"HideoutArea.SpawnProducedItems({__instance?.AreaTemplate?.Type}, count={items?.Length ?? 0})";
        __result = WrapTask(__result, __state, label, null);
    }

    private static void RemoveProducedPrefix(out long __state)
    {
        __state = Enabled ? Stopwatch.GetTimestamp() : 0;
    }

    private static void RemoveProducedPostfix(HideoutArea __instance, long __state)
    {
        if (__state == 0)
        {
            return;
        }

        LogProf($"HideoutArea.RemoveProducedItems({__instance?.AreaTemplate?.Type}) {ElapsedMs(__state):F2}ms");
    }

    private static async Task WrapTask(Task original, long startTicks, string label, string scopeToPop)
    {
        try
        {
            await original;
        }
        finally
        {
            if (scopeToPop != null)
            {
                ActiveScopes.Remove(scopeToPop);
            }
            LogProf($"{label} wall={ElapsedMs(startTicks):F1}ms (frame={Time.frameCount})");
        }
    }

    private static async Task<bool> WrapBoolTask(Task<bool> original, long startTicks, string label, string scopeToPop)
    {
        try
        {
            return await original;
        }
        finally
        {
            if (scopeToPop != null)
            {
                ActiveScopes.Remove(scopeToPop);
            }
            LogProf($"{label} wall={ElapsedMs(startTicks):F1}ms (frame={Time.frameCount})");
        }
    }

    internal static bool IsRecentlyInHideout()
    {
        return (Time.unscaledTime - _lastHideoutActivityUnscaledTime) <= 20f;
    }

    internal static string GetActiveScopeSummary()
    {
        if (ActiveScopes.Count == 0)
        {
            return "none";
        }
        return string.Join(",", ActiveScopes);
    }

    internal static string LastAction => _lastAction;
}

/// <summary>
/// Watches <see cref="Time.unscaledDeltaTime"/> every frame and reports any frame hitch >= 80ms
/// during or shortly after Hideout interactions, including GC collection deltas.
/// </summary>
internal sealed class HideoutHitchMonitor : MonoBehaviour
{
    private int _prevGc0;
    private int _prevGc1;
    private int _prevGc2;

    private void Awake()
    {
        _prevGc0 = GC.CollectionCount(0);
        _prevGc1 = GC.CollectionCount(1);
        _prevGc2 = GC.CollectionCount(2);
    }

    private void Update()
    {
        int g0 = GC.CollectionCount(0);
        int g1 = GC.CollectionCount(1);
        int g2 = GC.CollectionCount(2);
        int d0 = g0 - _prevGc0;
        int d1 = g1 - _prevGc1;
        int d2 = g2 - _prevGc2;
        _prevGc0 = g0;
        _prevGc1 = g1;
        _prevGc2 = g2;

        if (HideoutQoLPlugin.PerfLogging == null || !HideoutQoLPlugin.PerfLogging.Value)
        {
            return;
        }

        float dtMs = Time.unscaledDeltaTime * 1000f;
        if (dtMs >= 80f && HideoutProfilerPatch.IsRecentlyInHideout())
        {
            HideoutQoLPlugin.Log.LogInfo(
                $"[HideoutQoL Prof] [HITCH] frame={Time.frameCount} dt={dtMs:F1}ms gc={d0}/{d1}/{d2} last={HideoutProfilerPatch.LastAction} scopes=[{HideoutProfilerPatch.GetActiveScopeSummary()}]");
        }
    }
}
