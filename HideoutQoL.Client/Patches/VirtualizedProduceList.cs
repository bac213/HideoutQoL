using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using EFT.Hideout;
using EFT.UI;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Persistent cross-open pool of recycled <see cref="ProduceView"/> cards so switching between
/// Workbench, Lavatory, Medstation, Nutrition Unit, and Intel Center avoids repeated prefab instantiation.
/// Cleared automatically on Hideout unload / <see cref="HideoutRepresentation.DeInit"/>.
/// </summary>
internal static class GlobalProduceCardPool
{
    private const int MaxStandardPool = 14;
    private const int MaxDecryptionPool = 4;

    private static Transform _root;
    private static readonly Stack<ProduceView> StandardPool = new(MaxStandardPool);
    private static readonly Stack<ProduceView> DecryptionPool = new(MaxDecryptionPool);

    internal static ProduceView TryPop(bool isDecryption)
    {
        Stack<ProduceView> pool = isDecryption ? DecryptionPool : StandardPool;
        while (pool.Count > 0)
        {
            ProduceView view = pool.Pop();
            if (view != null && view.gameObject != null)
            {
                return view;
            }
        }
        return null;
    }

    internal static void ReturnOrDestroy(bool isDecryption, ProduceView view)
    {
        if (view == null || view.gameObject == null)
        {
            return;
        }

        try
        {
            view.Close();
        }
        catch
        {
        }

        Stack<ProduceView> pool = isDecryption ? DecryptionPool : StandardPool;
        int cap = isDecryption ? MaxDecryptionPool : MaxStandardPool;

        if (pool.Count < cap && EnsureRoot() != null)
        {
            view.transform.SetParent(_root, worldPositionStays: false);
            pool.Push(view);
            return;
        }

        UnityEngine.Object.Destroy(view.gameObject);
    }

    internal static void ClearAll()
    {
        ClearStack(StandardPool);
        ClearStack(DecryptionPool);
        if (_root != null)
        {
            UnityEngine.Object.Destroy(_root.gameObject);
            _root = null;
        }
    }

    private static void ClearStack(Stack<ProduceView> pool)
    {
        while (pool.Count > 0)
        {
            ProduceView view = pool.Pop();
            if (view != null && view.gameObject != null)
            {
                UnityEngine.Object.Destroy(view.gameObject);
            }
        }
    }

    private static Transform EnsureRoot()
    {
        if (_root != null)
        {
            return _root;
        }

        if (HideoutQoLPlugin.Instance == null)
        {
            return null;
        }

        var go = new GameObject("HideoutQoL_GlobalProduceCardPool", typeof(RectTransform));
        go.SetActive(false);
        go.transform.SetParent(HideoutQoLPlugin.Instance.transform, worldPositionStays: false);
        _root = go.transform;
        return _root;
    }
}

/// <summary>
/// Viewport-virtualized recipe list for <see cref="ProductionPanel"/>.
/// Keeps only the ~7-9 visible (plus buffered) <see cref="ProduceView"/> cards alive, with
/// pixel-exact top/bottom <see cref="LayoutElement"/> spacers, contiguous hysteresis to prevent
/// bounce-recycling, and cross-open <see cref="GlobalProduceCardPool"/> reuse.
/// </summary>
internal sealed class VirtualizedProduceList : MonoBehaviour
{
    private const int MinItemsToVirtualize = 1;
    private const int AlwaysBindAllThreshold = 8;
    private const int BufferAbove = 1;
    private const int BufferBelow = 2;
    private const int HysteresisExtra = 1;
    private const float FallbackCardHeight = 112f;
    private const float FallbackInitialViewportHeight = 620f;

    private static readonly FieldInfo PanelViewsField =
        AccessTools.Field(typeof(ProductionPanel), "_views");
    private static readonly FieldInfo PanelViewListField =
        AccessTools.Field(typeof(ProductionPanel), "_viewList");

    private static readonly FieldInfo ProduceAnyItemsReadyField =
        AccessTools.Field(typeof(ProduceView), "_anyItemsReady");
    private static readonly FieldInfo ProduceIsReqFulfilledField =
        AccessTools.Field(typeof(ProduceView), "_isRequirementsFulfilled");

    private ProductionPanel _panel;
    private RectTransform _container;
    private VerticalLayoutGroup _layoutGroup;
    private ScrollRect _scrollRect;
    private RectTransform _viewport;
    private ProduceView _template;
    private bool _isDecryption;
    private ItemUiContext _itemUiContext;
    private ItemsProducerBase _producer;
    private bool _availableSearch;

    private int _baseSiblingOffset;
    private float _spacing;
    private float _paddingTop;
    private float _defaultCardHeight = FallbackCardHeight;
    private float _expectedInitialViewportHeight = FallbackInitialViewportHeight;

    private readonly List<ProductionScheme> _allOrderedSchemes = new(128);
    private readonly List<ProductionScheme> _filteredSchemes = new(128);
    private readonly List<float> _heights = new(128);
    private Func<ProductionScheme, bool> _activeFilter;

    // Currently bound visible views (scheme -> ProduceView)
    private readonly Dictionary<ProductionScheme, ProduceView> _activeViews = new();
    // Shared with ProductionPanel._views so UpdateViews / Coalescer only refresh visible cards
    private readonly Dictionary<BaseHideoutScheme, IProduceView> _panelViews = new();
    // Views temporarily held while GettingItems == true so async collect never races with recycling
    private readonly Dictionary<ProductionScheme, ProduceView> _pinnedViews = new();
    // Local inactive recycled cards during scrolling
    private readonly Stack<ProduceView> _cardPool = new(16);

    // Scratch collections to keep scroll updates allocation-free
    private readonly HashSet<ProductionScheme> _desiredSet = new();
    private readonly List<ProductionScheme> _toUnbind = new(16);
    private readonly List<ProductionScheme> _toUnpin = new(4);
    private readonly Vector3[] _worldCorners = new Vector3[4];

    private RectTransform _topSpacerRect;
    private LayoutElement _topSpacerLayout;
    private RectTransform _bottomSpacerRect;
    private LayoutElement _bottomSpacerLayout;
    private Transform _poolHolder;
    private Transform _pinnedHolder;

    private int _firstVisible = -1;
    private int _lastVisible = -1;
    private int _initFrame = -1;
    private int _initPoolHits;
    private int _initInstantiated;
    private float _lastScrollCheckY = float.NaN;
    private float _lastViewportHeight = -1f;
    private bool _scrollListenerBound;
    private bool _heightCalibrated;
    private bool _initialized;

    internal bool IsActive => _initialized;

    internal static bool ShouldVirtualize(int schemeCount)
    {
        return HideoutQoLPlugin.PerfActive
            && HideoutQoLPlugin.VirtualizeCraftList.Value
            && schemeCount >= MinItemsToVirtualize;
    }

    internal static VirtualizedProduceList GetExisting(ProductionPanel panel)
    {
        return panel != null ? panel.GetComponent<VirtualizedProduceList>() : null;
    }

    internal static VirtualizedProduceList GetOrCreate(ProductionPanel panel)
    {
        VirtualizedProduceList v = panel.GetComponent<VirtualizedProduceList>();
        if (v == null)
        {
            v = panel.gameObject.AddComponent<VirtualizedProduceList>();
        }
        return v;
    }

    internal void Initialize(
        ProductionPanel panel,
        List<ProductionScheme> orderedSchemes,
        ItemUiContext itemUiContext,
        ProduceView template,
        bool availableSearch,
        ItemsProducerBase producer)
    {
        DisposeList();

        var sw = Stopwatch.StartNew();

        _panel = panel;
        _container = panel._container;
        _template = template;
        _isDecryption = ReferenceEquals(template, panel._decryptionProductionView);
        _itemUiContext = itemUiContext;
        _producer = producer;
        _availableSearch = availableSearch;

        _layoutGroup = _container.GetComponent<VerticalLayoutGroup>();
        _spacing = _layoutGroup != null ? _layoutGroup.spacing : 0f;
        _paddingTop = _layoutGroup != null ? _layoutGroup.padding.top : 0f;

        _scrollRect = _container.GetComponentInParent<ScrollRect>();
        if (_scrollRect != null)
        {
            _viewport = _scrollRect.viewport != null
                ? _scrollRect.viewport
                : _scrollRect.transform as RectTransform;
        }

        // Derive the exact expected viewport height from AreaScreenSubstrate._maxHeight so frame 0
        // immediately binds the full initial visible set in ONE pass (avoiding [0..3] -> [0..7] -> [0..6] churn).
        AreaScreenSubstrate substrate = _container.GetComponentInParent<AreaScreenSubstrate>();
        if (substrate != null && substrate._maxHeight >= 300f)
        {
            _expectedInitialViewportHeight = Mathf.Max(420f, substrate._maxHeight - 160f);
        }
        else
        {
            _expectedInitialViewportHeight = FallbackInitialViewportHeight;
        }

        // Preserve any pre-existing children inside _container (e.g., template or headers)
        _baseSiblingOffset = _container.childCount;

        CreateHelpers();

        _allOrderedSchemes.Clear();
        _allOrderedSchemes.AddRange(orderedSchemes);
        _activeFilter = null;
        RebuildFilteredList();

        // Expose our visible-only dictionary to ProductionPanel and null out _viewList
        PanelViewListField?.SetValue(_panel, null);
        PanelViewsField?.SetValue(_panel, _panelViews);

        // Measure exact card height from the very first scheme (kept in _activeViews, zero wasted work)
        _initPoolHits = 0;
        _initInstantiated = 0;
        _defaultCardHeight = MeasureDefaultCardHeight();
        ResetHeights(_defaultCardHeight);

        if (_scrollRect != null)
        {
            _scrollRect.onValueChanged.AddListener(OnScrollChanged);
            _scrollListenerBound = true;
        }

        _initialized = true;
        _heightCalibrated = false;
        _initFrame = Time.frameCount;
        _firstVisible = -1;
        _lastVisible = -1;
        _lastScrollCheckY = float.NaN;
        _lastViewportHeight = -1f;

        RefreshVisibleWindow(force: true);

        sw.Stop();
        HideoutProfilerPatch.RecordVirtualizedInit(
            panel.AreaData?.Template?.Type.ToString() ?? "?",
            _allOrderedSchemes.Count,
            _activeViews.Count,
            _defaultCardHeight,
            _spacing,
            sw.Elapsed.TotalMilliseconds);
        HideoutQoLPlugin.DebugPerf(
            $"VirtualizedProduceList({panel.AreaData?.Template?.Type}) init: total={_allOrderedSchemes.Count}, visible={_activeViews.Count}, poolHit={_initPoolHits}, inst={_initInstantiated}, cardH={_defaultCardHeight:F1}px, spacing={_spacing:F1}px in {sw.Elapsed.TotalMilliseconds:F1}ms");
    }

    internal void UpdateOrder(IEnumerable<ProductionScheme> sortedSchemes)
    {
        if (!_initialized || sortedSchemes == null)
        {
            return;
        }

        _allOrderedSchemes.Clear();
        foreach (ProductionScheme scheme in sortedSchemes)
        {
            if (scheme != null && !scheme.continuous)
            {
                _allOrderedSchemes.Add(scheme);
            }
        }

        var prevFiltered = new List<ProductionScheme>(_filteredSchemes);
        RebuildFilteredList();

        bool changed = prevFiltered.Count != _filteredSchemes.Count;
        if (!changed)
        {
            for (int i = 0; i < prevFiltered.Count; i++)
            {
                if (!ReferenceEquals(prevFiltered[i], _filteredSchemes[i]))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        ResetHeights(_defaultCardHeight);
        RefreshVisibleWindow(force: true);
    }

    internal void SetFilter(Func<ProductionScheme, bool> predicate)
    {
        if (!_initialized)
        {
            return;
        }

        _activeFilter = predicate;
        RebuildFilteredList();
        ResetHeights(_defaultCardHeight);

        if (_scrollRect != null)
        {
            _scrollRect.StopMovement();
            _scrollRect.verticalNormalizedPosition = 1f;
        }

        RefreshVisibleWindow(force: true);
    }

    internal void DisposeList(bool allowGlobalPool = true)
    {
        if (_scrollRect != null && _scrollListenerBound)
        {
            _scrollRect.onValueChanged.RemoveListener(OnScrollChanged);
        }
        _scrollListenerBound = false;
        _initialized = false;

        foreach (KeyValuePair<ProductionScheme, ProduceView> kvp in _activeViews)
        {
            if (allowGlobalPool)
            {
                ReleaseCardToGlobalPool(kvp.Value);
            }
            else
            {
                DestroyCard(kvp.Value);
            }
        }
        _activeViews.Clear();

        foreach (KeyValuePair<ProductionScheme, ProduceView> kvp in _pinnedViews)
        {
            DestroyCard(kvp.Value);
        }
        _pinnedViews.Clear();

        while (_cardPool.Count > 0)
        {
            ProduceView pooled = _cardPool.Pop();
            if (allowGlobalPool)
            {
                ReleaseCardToGlobalPool(pooled);
            }
            else
            {
                DestroyCard(pooled);
            }
        }

        _panelViews.Clear();
        _allOrderedSchemes.Clear();
        _filteredSchemes.Clear();
        _heights.Clear();
        _activeFilter = null;
        _firstVisible = -1;
        _lastVisible = -1;

        DestroyHelper(ref _topSpacerRect);
        _topSpacerLayout = null;
        DestroyHelper(ref _bottomSpacerRect);
        _bottomSpacerLayout = null;
        DestroyHelper(ref _poolHolder);
        DestroyHelper(ref _pinnedHolder);
    }

    private void OnDestroy()
    {
        DisposeList(allowGlobalPool: false);
    }

    private void OnScrollChanged(Vector2 _)
    {
        if (_initialized)
        {
            CheckAndUpdateWindow();
        }
    }

    private void LateUpdate()
    {
        if (!_initialized || _container == null)
        {
            return;
        }

        ReleaseFinishedPinnedViews();

        // Wait until AreaScreenSubstrate.ViewListCreated() has run (frame +1) and Unity has laid out
        // the updated _contentLayout.preferredHeight (frame +2) before reading live viewport rects.
        if (Time.frameCount < _initFrame + 2)
        {
            return;
        }

        CalibrateCardHeightOnce();
        CheckAndUpdateWindow();
    }

    private void CalibrateCardHeightOnce()
    {
        if (_heightCalibrated || _activeViews.Count == 0)
        {
            return;
        }

        float maxH = 0f;
        foreach (ProduceView view in _activeViews.Values)
        {
            if (view != null && view.transform is RectTransform rt && rt.rect.height > maxH)
            {
                maxH = rt.rect.height;
            }
        }

        if (maxH >= 40f)
        {
            _heightCalibrated = true;
            if (Mathf.Abs(maxH - _defaultCardHeight) > 1.5f)
            {
                _defaultCardHeight = maxH;
                ResetHeights(maxH);
                UpdateSpacers(_firstVisible, _lastVisible, _filteredSchemes.Count);
            }
        }
    }

    private void CheckAndUpdateWindow()
    {
        if (!_initialized || _container == null || _filteredSchemes.Count <= AlwaysBindAllThreshold)
        {
            return;
        }

        // Wait until layout has settled before reading live viewport height
        if (!_heightCalibrated || Time.frameCount < _initFrame + 2)
        {
            return;
        }

        float vpH = _viewport != null ? _viewport.rect.height : 0f;
        float containerY = _container.position.y;

        if (!float.IsNaN(_lastScrollCheckY)
            && Mathf.Abs(containerY - _lastScrollCheckY) < 1f
            && Mathf.Abs(vpH - _lastViewportHeight) < 1f)
        {
            return;
        }

        _lastScrollCheckY = containerY;
        _lastViewportHeight = vpH;
        RefreshVisibleWindow(force: false);
    }

    private void RefreshVisibleWindow(bool force)
    {
        if (!_initialized || _container == null)
        {
            return;
        }

        int total = _filteredSchemes.Count;
        if (total == 0)
        {
            UnbindAllActive();
            UpdateSpacers(0, -1, 0);
            _firstVisible = -1;
            _lastVisible = -1;
            return;
        }

        ComputeTargetRange(total, out int bindFirst, out int bindLast);

        int firstIdx = bindFirst;
        int lastIdx = bindLast;

        // Contiguous hysteresis when scrolling / minor layout shifts (avoid bounce-recycling)
        if (!force && _firstVisible >= 0 && _lastVisible >= _firstVisible && _lastVisible < total)
        {
            int keepFirst = Mathf.Max(0, bindFirst - HysteresisExtra);
            int keepLast = Mathf.Min(total - 1, bindLast + HysteresisExtra);

            if (_firstVisible < bindFirst && _firstVisible >= keepFirst)
            {
                firstIdx = _firstVisible;
            }
            if (_lastVisible > bindLast && _lastVisible <= keepLast)
            {
                lastIdx = _lastVisible;
            }
        }

        if (!force && firstIdx == _firstVisible && lastIdx == _lastVisible)
        {
            return;
        }

        var sw = HideoutQoLPlugin.PerfLogging != null && HideoutQoLPlugin.PerfLogging.Value
            ? Stopwatch.StartNew()
            : null;

        _firstVisible = firstIdx;
        _lastVisible = lastIdx;

        // 1) Collect desired schemes for [firstIdx .. lastIdx]
        _desiredSet.Clear();
        for (int i = firstIdx; i <= lastIdx; i++)
        {
            _desiredSet.Add(_filteredSchemes[i]);
        }

        // 2) Unbind views that moved outside the window
        _toUnbind.Clear();
        foreach (KeyValuePair<ProductionScheme, ProduceView> kvp in _activeViews)
        {
            if (!_desiredSet.Contains(kvp.Key))
            {
                _toUnbind.Add(kvp.Key);
            }
        }

        int recycled = 0;
        for (int i = 0; i < _toUnbind.Count; i++)
        {
            UnbindScheme(_toUnbind[i]);
            recycled++;
        }

        // 3) Bind missing views & ensure exact sibling order
        int bound = 0;
        int siblingCursor = _baseSiblingOffset;
        if (_topSpacerRect != null)
        {
            _topSpacerRect.SetSiblingIndex(siblingCursor++);
        }

        for (int i = firstIdx; i <= lastIdx; i++)
        {
            ProductionScheme scheme = _filteredSchemes[i];
            if (!_activeViews.TryGetValue(scheme, out ProduceView view) || view == null)
            {
                view = BindScheme(scheme);
                bound++;
            }

            if (view != null)
            {
                if (view.transform.parent != _container)
                {
                    view.transform.SetParent(_container, worldPositionStays: false);
                }
                if (view.transform.GetSiblingIndex() != siblingCursor)
                {
                    view.transform.SetSiblingIndex(siblingCursor);
                }
                siblingCursor++;
            }
        }

        if (_bottomSpacerRect != null)
        {
            _bottomSpacerRect.SetSiblingIndex(siblingCursor);
        }

        // 4) Update top & bottom spacers to preserve exact total list height
        UpdateSpacers(firstIdx, lastIdx, total);

        if (sw != null)
        {
            sw.Stop();
            if (bound > 0 || recycled > 0)
            {
                HideoutQoLPlugin.DebugPerf(
                    $"VirtualizedWindow [{firstIdx}..{lastIdx}]/{total} (bound={bound}, recycled={recycled}, pool={_cardPool.Count}) in {sw.Elapsed.TotalMilliseconds:F1}ms");
            }
        }
    }

    private void ComputeTargetRange(int total, out int firstIdx, out int lastIdx)
    {
        if (total <= AlwaysBindAllThreshold)
        {
            firstIdx = 0;
            lastIdx = total - 1;
            return;
        }

        float visibleTopDist = 0f;
        float visibleBottomDist = _expectedInitialViewportHeight;

        // Only read live viewport corners after layout has expanded the viewport to its real height
        if (_heightCalibrated && _viewport != null && _viewport.rect.height >= 200f)
        {
            _viewport.GetWorldCorners(_worldCorners);
            float vpBottomLocalY = _container.InverseTransformPoint(_worldCorners[0]).y;
            float vpTopLocalY = _container.InverseTransformPoint(_worldCorners[1]).y;

            float staticOffset = ComputeStaticSiblingsHeight();
            float contentTopY = _container.rect.yMax - _paddingTop - staticOffset;

            float topDist = contentTopY - vpTopLocalY;
            float bottomDist = contentTopY - vpBottomLocalY;

            if (bottomDist > topDist + 50f)
            {
                visibleTopDist = topDist;
                visibleBottomDist = bottomDist;
            }
        }

        float y = 0f;
        int rawFirst = total - 1;
        int rawLast = 0;
        bool foundFirst = false;

        for (int i = 0; i < total; i++)
        {
            float h = _heights[i];
            float itemBottom = y + h;

            if (!foundFirst && itemBottom >= visibleTopDist)
            {
                rawFirst = i;
                foundFirst = true;
            }

            if (y <= visibleBottomDist)
            {
                rawLast = i;
            }
            else if (foundFirst)
            {
                break;
            }

            y = itemBottom + _spacing;
        }

        if (!foundFirst)
        {
            rawFirst = Mathf.Max(0, total - 1);
            rawLast = rawFirst;
        }

        firstIdx = Mathf.Max(0, rawFirst - BufferAbove);
        lastIdx = Mathf.Min(total - 1, Mathf.Max(rawFirst, rawLast) + BufferBelow);
    }

    private float ComputeStaticSiblingsHeight()
    {
        if (_baseSiblingOffset <= 0)
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = 0; i < _baseSiblingOffset && i < _container.childCount; i++)
        {
            Transform child = _container.GetChild(i);
            if (!child.gameObject.activeSelf)
            {
                continue;
            }
            LayoutElement le = child.GetComponent<LayoutElement>();
            if (le != null && le.ignoreLayout)
            {
                continue;
            }
            if (child is RectTransform rt)
            {
                sum += rt.rect.height + _spacing;
            }
        }
        return sum;
    }

    private void UpdateSpacers(int firstIdx, int lastIdx, int total)
    {
        if (firstIdx > 0 && total > 0)
        {
            float topH = SumHeightSpan(0, firstIdx - 1);
            if (!_topSpacerRect.gameObject.activeSelf)
            {
                _topSpacerRect.gameObject.SetActive(true);
            }
            if (Mathf.Abs(_topSpacerLayout.preferredHeight - topH) > 0.5f)
            {
                _topSpacerLayout.preferredHeight = topH;
                _topSpacerLayout.minHeight = topH;
            }
        }
        else if (_topSpacerRect != null && _topSpacerRect.gameObject.activeSelf)
        {
            _topSpacerLayout.preferredHeight = 0f;
            _topSpacerLayout.minHeight = 0f;
            _topSpacerRect.gameObject.SetActive(false);
        }

        if (lastIdx >= 0 && lastIdx < total - 1)
        {
            float bottomH = SumHeightSpan(lastIdx + 1, total - 1);
            if (!_bottomSpacerRect.gameObject.activeSelf)
            {
                _bottomSpacerRect.gameObject.SetActive(true);
            }
            if (Mathf.Abs(_bottomSpacerLayout.preferredHeight - bottomH) > 0.5f)
            {
                _bottomSpacerLayout.preferredHeight = bottomH;
                _bottomSpacerLayout.minHeight = bottomH;
            }
        }
        else if (_bottomSpacerRect != null && _bottomSpacerRect.gameObject.activeSelf)
        {
            _bottomSpacerLayout.preferredHeight = 0f;
            _bottomSpacerLayout.minHeight = 0f;
            _bottomSpacerRect.gameObject.SetActive(false);
        }
    }

    private float SumHeightSpan(int startInclusive, int endInclusive)
    {
        if (endInclusive < startInclusive)
        {
            return 0f;
        }

        float sum = 0f;
        for (int i = startInclusive; i <= endInclusive; i++)
        {
            sum += _heights[i];
        }
        int count = endInclusive - startInclusive + 1;
        if (count > 1)
        {
            sum += (count - 1) * _spacing;
        }
        return sum;
    }

    private ProduceView BindScheme(ProductionScheme scheme)
    {
        if (_pinnedViews.TryGetValue(scheme, out ProduceView pinned) && pinned != null)
        {
            _pinnedViews.Remove(scheme);
            pinned.transform.SetParent(_container, worldPositionStays: false);
            _activeViews[scheme] = pinned;
            _panelViews[scheme] = pinned;
            return pinned;
        }

        ProduceView view = PopOrInstantiateCard();
        if (view == null)
        {
            return null;
        }

        view.GettingItems = false;
        ProduceAnyItemsReadyField?.SetValue(view, false);
        ProduceIsReqFulfilledField?.SetValue(view, true);
        if (view._expectedTimePanel != null)
        {
            view._expectedTimePanel.SetActive(true);
        }

        view.transform.SetParent(_container, worldPositionStays: false);
        view.Show(
            _itemUiContext,
            _panel.InventoryController,
            scheme,
            _producer,
            _panel.StartProducing,
            _panel.GetProducedItems,
            _availableSearch);

        view.OnUpdateFavoriteSchemesList += _panel.FavoriteSchemeUpdatedHandler;

        _activeViews[scheme] = view;
        _panelViews[scheme] = view;
        return view;
    }

    private ProduceView PopOrInstantiateCard()
    {
        while (_cardPool.Count > 0)
        {
            ProduceView local = _cardPool.Pop();
            if (local != null && local.gameObject != null)
            {
                return local;
            }
        }

        ProduceView global = GlobalProduceCardPool.TryPop(_isDecryption);
        if (global != null)
        {
            _initPoolHits++;
            return global;
        }

        if (_template == null || _template.gameObject == null)
        {
            return null;
        }

        _initInstantiated++;
        GameObject go = UnityEngine.Object.Instantiate(_template.gameObject, null, worldPositionStays: false);
        return go.GetComponent<ProduceView>();
    }

    private void UnbindScheme(ProductionScheme scheme)
    {
        if (!_activeViews.TryGetValue(scheme, out ProduceView view))
        {
            return;
        }

        _activeViews.Remove(scheme);
        _panelViews.Remove(scheme);

        if (view == null)
        {
            return;
        }

        if (view.GettingItems)
        {
            view.transform.SetParent(_pinnedHolder, worldPositionStays: false);
            _pinnedViews[scheme] = view;
            return;
        }

        RecycleCardLocally(view);
    }

    private void UnbindAllActive()
    {
        _toUnbind.Clear();
        foreach (ProductionScheme scheme in _activeViews.Keys)
        {
            _toUnbind.Add(scheme);
        }
        for (int i = 0; i < _toUnbind.Count; i++)
        {
            UnbindScheme(_toUnbind[i]);
        }
    }

    private void ReleaseFinishedPinnedViews()
    {
        if (_pinnedViews.Count == 0)
        {
            return;
        }

        _toUnpin.Clear();
        foreach (KeyValuePair<ProductionScheme, ProduceView> kvp in _pinnedViews)
        {
            if (kvp.Value == null || !kvp.Value.GettingItems)
            {
                _toUnpin.Add(kvp.Key);
            }
        }

        for (int i = 0; i < _toUnpin.Count; i++)
        {
            ProductionScheme scheme = _toUnpin[i];
            if (_pinnedViews.TryGetValue(scheme, out ProduceView view))
            {
                _pinnedViews.Remove(scheme);
                if (view != null)
                {
                    RecycleCardLocally(view);
                }
            }
        }
    }

    private void RecycleCardLocally(ProduceView view)
    {
        view.OnUpdateFavoriteSchemesList -= _panel.FavoriteSchemeUpdatedHandler;
        view.Close();
        if (_poolHolder != null)
        {
            view.transform.SetParent(_poolHolder, worldPositionStays: false);
        }
        _cardPool.Push(view);
    }

    private void ReleaseCardToGlobalPool(ProduceView view)
    {
        if (view == null)
        {
            return;
        }

        if (_panel != null)
        {
            view.OnUpdateFavoriteSchemesList -= _panel.FavoriteSchemeUpdatedHandler;
        }

        GlobalProduceCardPool.ReturnOrDestroy(_isDecryption, view);
    }

    private float MeasureDefaultCardHeight()
    {
        if (_filteredSchemes.Count == 0)
        {
            return FallbackCardHeight;
        }

        ProduceView first = BindScheme(_filteredSchemes[0]);
        if (first != null && first.transform is RectTransform rt)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            float h = LayoutUtility.GetPreferredHeight(rt);
            if (h < 40f)
            {
                h = rt.rect.height;
            }
            if (h >= 40f)
            {
                return h;
            }
        }

        if (_template != null && _template.transform is RectTransform trt && trt.rect.height >= 40f)
        {
            return trt.rect.height;
        }

        return FallbackCardHeight;
    }

    private void RebuildFilteredList()
    {
        _filteredSchemes.Clear();
        for (int i = 0; i < _allOrderedSchemes.Count; i++)
        {
            ProductionScheme s = _allOrderedSchemes[i];
            if (_activeFilter == null || _activeFilter(s))
            {
                _filteredSchemes.Add(s);
            }
        }
    }

    private void ResetHeights(float height)
    {
        _heights.Clear();
        for (int i = 0; i < _filteredSchemes.Count; i++)
        {
            _heights.Add(height);
        }
    }

    private void CreateHelpers()
    {
        (_topSpacerRect, _topSpacerLayout) = CreateSpacer("HideoutQoL_TopSpacer");
        (_bottomSpacerRect, _bottomSpacerLayout) = CreateSpacer("HideoutQoL_BottomSpacer");

        var poolGo = new GameObject("HideoutQoL_CardPool", typeof(RectTransform));
        poolGo.SetActive(false);
        _poolHolder = poolGo.transform;
        _poolHolder.SetParent(_panel.transform, worldPositionStays: false);

        var pinnedGo = new GameObject("HideoutQoL_PinnedCards", typeof(RectTransform), typeof(CanvasGroup));
        var cg = pinnedGo.GetComponent<CanvasGroup>();
        cg.alpha = 0f;
        cg.blocksRaycasts = false;
        cg.interactable = false;
        var pinnedLe = pinnedGo.AddComponent<LayoutElement>();
        pinnedLe.ignoreLayout = true;
        _pinnedHolder = pinnedGo.transform;
        _pinnedHolder.SetParent(_panel.transform, worldPositionStays: false);
    }

    private (RectTransform rect, LayoutElement layout) CreateSpacer(string goName)
    {
        var go = new GameObject(goName, typeof(RectTransform), typeof(LayoutElement));
        var rect = (RectTransform)go.transform;
        rect.SetParent(_container, worldPositionStays: false);
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = Vector2.zero;

        var layout = go.GetComponent<LayoutElement>();
        layout.minHeight = 0f;
        layout.preferredHeight = 0f;
        layout.flexibleHeight = 0f;
        go.SetActive(false);
        return (rect, layout);
    }

    private static void DestroyCard(ProduceView view)
    {
        if (view == null)
        {
            return;
        }
        try
        {
            view.Close();
        }
        catch
        {
        }
        if (view.gameObject != null)
        {
            UnityEngine.Object.Destroy(view.gameObject);
        }
    }

    private static void DestroyHelper<T>(ref T obj) where T : Component
    {
        if (obj != null)
        {
            UnityEngine.Object.Destroy(obj.gameObject);
            obj = null;
        }
    }
}
