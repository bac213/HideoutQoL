using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InputSystem;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using EFT.UI.Screens;
using HideoutQoL.Client.Patches;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutQoL.Client.UI;

/// <summary>
/// Full-screen 3-column EFT-native Hideout Crafting Studio (<see cref="UIScreen"/>):
/// - Left rail: Built crafting stations + 2-level Handbook category tree with native icons.
/// - Center catalog: Multi-facet status/level/locked/search/sort toolbar + row-virtualized grid.
/// - Right inspector: 3D/2D item preview, requirement breakdown, [反查] reverse lookup, flea buy, and craft dock.
/// </summary>
internal sealed class HideoutWorkshopScreen : UIScreen
{
    private sealed class ChipButton
    {
        public GameObject Root;
        public Button Button;
        public Image Background;
        public TextMeshProUGUI Label;
    }

    private sealed class CategoryRowView
    {
        public GameObject Root;
        public Image Background;
        public Button RowButton;
        public Button ArrowButton;
        public Image ArrowIcon;
        public Image CategoryIcon;
        public LayoutElement IndentSpacer;
        public TextMeshProUGUI TitleText;
        public TextMeshProUGUI CountText;
        public string RootId;
        public string SubId;
    }

    private sealed class StationRowView
    {
        public GameObject Root;
        public Image Background;
        public Button Button;
        public TextMeshProUGUI TitleText;
        public TextMeshProUGUI BadgeText;
        public EAreaType? StationFilter;
    }

    private static readonly Dictionary<string, Sprite> CategoryIconCache =
        new(StringComparer.Ordinal);

    private EftUiTemplateCache _tpl;
    private WorkshopCatalogModel _model;
    private WorkshopGridView _gridView;
    private WorkshopInspectorPane _inspector;

    private HideoutScreenOverlay _overlay;
    private Action _onCloseRequested;
    private bool _built;
    private bool _isOpen;
    private bool _eventsHooked;

    // Header widgets
    private Image _headerStationIcon;
    private TextMeshProUGUI _headerTitleText;
    private TextMeshProUGUI _headerCountsText;
    private Button _generatorBtn;
    private Image _generatorBtnBg;
    private Image _generatorLed;
    private TextMeshProUGUI _generatorBtnText;
    private DefaultUIButton _collectAllBtn;
    private DefaultUIButton _areaDetailsBtn;
    private DefaultUIButton _backBtn;

    // Left rail
    private RectTransform _stationListContent;
    private readonly List<StationRowView> _stationRows = new(8);
    private DefaultUIButton _clearCategoryBtn;
    private RectTransform _categoryListContent;
    private readonly List<CategoryRowView> _categoryRows = new(32);

    // Center filters & grid
    private readonly List<(WorkshopStatusFilter filter, ChipButton chip)> _statusChips = new(4);
    private readonly List<(int level, ChipButton chip)> _levelChips = new(4);
    private ChipButton _hideLockedChip;
    private ValidationInputField _searchInput;
    private Button _searchClearBtn;
    private TextMeshProUGUI _resultCountText;
    private DropDownBox _sortDropdown;
    private DefaultUIButton _sortFallbackBtn;
    private GameObject _emptyCatalogPanel;

    // Live state
    private readonly List<ItemsProducerBase> _subscribedProducers = new(8);
    private EnergyController _subscribedEnergy;
    private HideoutRepresentation _subscribedHideout;

    private string _selectedSchemeId;
    private string _pendingSearchText;
    private float _searchDebounceDeadline = -1f;
    private float _nextTimerTick;
    private bool _dirtyCatalog;
    private bool _dirtyStates;
    private bool _collectingAll;
    private int _lastAppliedColumns = -1;

    // Generator pill snapshot to avoid rebuilding text every second when unchanged
    private bool _hasGenSnapshot;
    private bool _genSnapActive;
    private bool _genSnapOn;
    private EDetailsType _genSnapFuelType;
    private int _genSnapTime;
    private int _genSnapPct;

    private static readonly string[] SortLabels =
    {
        "综合状态",
        "耗时最短",
        "缺料最少",
        "名称排序",
    };

    internal void BuildOnce(EftUiTemplateCache tpl)
    {
        if (_built)
        {
            return;
        }
        _built = true;
        _tpl = tpl;
        _model = new WorkshopCatalogModel();

        var rootRt = (RectTransform)transform;
        // Leave bottom clear for EFT MenuTaskBar (~48px) and slight breathing margin for backdrop visibility
        EftUiTemplateCache.Stretch(rootRt, left: 16f, right: 16f, top: 44f, bottom: 52f);

        Image frameBg = _tpl.CreatePanel(transform, "Backdrop", EftUiTemplateCache.ColorWindowScrim, raycastTarget: true);
        EftUiTemplateCache.Stretch(frameBg.rectTransform);
        _tpl.AddShadowPair(transform);
        AddFrameOutline(transform);

        var rootVlgGO = new GameObject("RootLayout", typeof(RectTransform), typeof(VerticalLayoutGroup));
        rootVlgGO.transform.SetParent(transform, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)rootVlgGO.transform);
        var rootVlg = rootVlgGO.GetComponent<VerticalLayoutGroup>();
        rootVlg.padding = new RectOffset(0, 0, 0, 0);
        rootVlg.spacing = 0f;
        rootVlg.childControlWidth = true;
        rootVlg.childControlHeight = true;
        rootVlg.childForceExpandWidth = true;
        rootVlg.childForceExpandHeight = false;

        BuildHeader(rootVlgGO.transform);
        BuildBodyColumns(rootVlgGO.transform);
    }

    private void AddFrameOutline(Transform parent)
    {
        CreateEdge(parent, "TopBorder", new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 1f));
        CreateEdge(parent, "BottomBorder", new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 1f));
        CreateEdge(parent, "LeftBorder", new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(1f, 0f));
        CreateEdge(parent, "RightBorder", new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(1f, 0f));
    }

    private void CreateEdge(Transform parent, string name, Vector2 aMin, Vector2 aMax, Vector2 pivot, Vector2 sizeDelta)
    {
        Image img = _tpl.CreatePanel(parent, name, EftUiTemplateCache.ColorCardBorder);
        RectTransform rt = img.rectTransform;
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.pivot = pivot;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = sizeDelta;
    }

    private void BuildHeader(Transform parent)
    {
        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        headerGo.transform.SetParent(parent, worldPositionStays: false);
        headerGo.GetComponent<Image>().color = EftUiTemplateCache.ColorHeaderBg;
        EftUiTemplateCache.SetFixedLayout(headerGo, height: 52f, flexW: -1f, flexH: 0f);

        var hlg = headerGo.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(14, 14, 6, 6);
        hlg.spacing = 10f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        var iconGo = new GameObject("StationIcon", typeof(RectTransform), typeof(Image));
        iconGo.transform.SetParent(headerGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(iconGo, width: 32f, height: 32f, flexW: 0f, flexH: 0f);
        _headerStationIcon = iconGo.GetComponent<Image>();
        _headerStationIcon.preserveAspect = true;
        _headerStationIcon.raycastTarget = false;

        var titleCol = new GameObject("TitleCol", typeof(RectTransform), typeof(VerticalLayoutGroup));
        titleCol.transform.SetParent(headerGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(titleCol, width: 250f, height: 38f, flexW: 0f, flexH: 0f);
        var titleVlg = titleCol.GetComponent<VerticalLayoutGroup>();
        titleVlg.spacing = 1f;
        titleVlg.childControlWidth = true;
        titleVlg.childControlHeight = true;
        titleVlg.childForceExpandWidth = true;
        titleVlg.childForceExpandHeight = false;

        _headerTitleText = _tpl.CreateText(titleCol.transform, "Title", "藏身处制作工坊", 18f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        EftUiTemplateCache.SetFixedLayout(_headerTitleText.gameObject, height: 22f, flexW: -1f, flexH: 0f);
        _headerCountsText = _tpl.CreateText(titleCol.transform, "SubCounts", "", 12f, EftUiTemplateCache.ColorTextSecondary);
        EftUiTemplateCache.SetFixedLayout(_headerCountsText.gameObject, height: 15f, flexW: -1f, flexH: 0f);

        // Flexible spacer
        var spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        spacer.transform.SetParent(headerGo.transform, worldPositionStays: false);
        spacer.GetComponent<LayoutElement>().flexibleWidth = 1f;

        // Compact Generator status pill (fixed 224x32, subtle LED dot instead of oversized error icon)
        var genGo = new GameObject("GeneratorPill", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
        genGo.transform.SetParent(headerGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(genGo, width: 224f, height: 32f, flexW: 0f, flexH: 0f);
        _generatorBtnBg = genGo.GetComponent<Image>();
        _generatorBtnBg.color = EftUiTemplateCache.ColorSubBarBg;
        _generatorBtn = genGo.GetComponent<Button>();
        _generatorBtn.onClick.AddListener(HandleToggleGenerator);
        var genHlg = genGo.GetComponent<HorizontalLayoutGroup>();
        genHlg.padding = new RectOffset(10, 10, 4, 4);
        genHlg.spacing = 8f;
        genHlg.childAlignment = TextAnchor.MiddleCenter;
        genHlg.childControlWidth = true;
        genHlg.childControlHeight = true;
        genHlg.childForceExpandWidth = false;
        genHlg.childForceExpandHeight = false;

        var ledGo = new GameObject("StatusDot", typeof(RectTransform), typeof(Image));
        ledGo.transform.SetParent(genGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(ledGo, width: 8f, height: 8f, flexW: 0f, flexH: 0f);
        _generatorLed = ledGo.GetComponent<Image>();
        _generatorLed.color = EftUiTemplateCache.ColorAccentProducing;
        _generatorLed.raycastTarget = false;

        _generatorBtnText = _tpl.CreateText(genGo.transform, "Text", "发电机", 12.5f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left);
        var genTxtLe = _generatorBtnText.gameObject.AddComponent<LayoutElement>();
        genTxtLe.flexibleWidth = 1f;
        genTxtLe.minHeight = 20f;
        genTxtLe.preferredHeight = 20f;
        genTxtLe.flexibleHeight = 0f;

        _collectAllBtn = _tpl.CloneActionButton(headerGo.transform, "CollectAllBtn", "一键全领", 118f, 32f, 13, () => RunSafe(CollectAllReadyAsync));
        _areaDetailsBtn = _tpl.CloneActionButton(headerGo.transform, "AreaDetailsBtn", "设施详情/升级", 132f, 32f, 13, HandleOpenVanillaAreaDetails);
        _backBtn = _tpl.CloneBackButton(headerGo.transform, "BackBtn", HandleCloseClicked);
        if (_backBtn != null)
        {
            EftUiTemplateCache.SetFixedLayout(_backBtn.gameObject, width: 96f, height: 32f, flexW: 0f, flexH: 0f);
            _backBtn.SetRawText("关闭", 14);
        }

        _tpl.CreateDivider(headerGo.transform, "HeaderBottomLine", vertical: false);
    }

    private void BuildBodyColumns(Transform parent)
    {
        var bodyGo = new GameObject("ColumnsBody", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        bodyGo.transform.SetParent(parent, worldPositionStays: false);
        bodyGo.GetComponent<LayoutElement>().flexibleHeight = 1f;
        var hlg = bodyGo.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(0, 0, 0, 0);
        hlg.spacing = 0f;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;

        BuildLeftRail(bodyGo.transform);
        BuildCenterCatalog(bodyGo.transform);
        BuildRightInspector(bodyGo.transform);
    }

    private void BuildLeftRail(Transform parent)
    {
        var railGo = new GameObject("LeftRail", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        railGo.transform.SetParent(parent, worldPositionStays: false);
        railGo.GetComponent<Image>().color = EftUiTemplateCache.ColorPanelBg;
        EftUiTemplateCache.SetFixedLayout(railGo, width: 262f, flexW: 0f, flexH: -1f);

        var vlg = railGo.GetComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(8, 8, 8, 8);
        vlg.spacing = 6f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var stationHdr = _tpl.CreateText(railGo.transform, "StationsHdr", "制作设施", 13.5f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Left, FontStyles.Bold);
        EftUiTemplateCache.SetFixedLayout(stationHdr.gameObject, height: 20f, flexW: -1f, flexH: 0f);

        var stationScrollHost = new GameObject("StationScrollHost", typeof(RectTransform));
        stationScrollHost.transform.SetParent(railGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(stationScrollHost, height: 192f, flexW: -1f, flexH: 0f);
        var (_, _, stationContent) = _tpl.CreateScrollView(stationScrollHost.transform, "StationScroll", addBottomPadding: false);
        _stationListContent = stationContent;

        // Divider between Stations and Categories
        var divHost = new GameObject("RailDivider", typeof(RectTransform));
        divHost.transform.SetParent(railGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(divHost, height: 2f, flexW: -1f, flexH: 0f);
        _tpl.CreateDivider(divHost.transform, "Line", vertical: false);

        // Categories Header
        var catHdrRow = new GameObject("CategoriesHdrRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        catHdrRow.transform.SetParent(railGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(catHdrRow, height: 24f, flexW: -1f, flexH: 0f);
        var catHlg = catHdrRow.GetComponent<HorizontalLayoutGroup>();
        catHlg.childControlWidth = true;
        catHlg.childControlHeight = true;
        catHlg.childForceExpandWidth = false;
        catHlg.childForceExpandHeight = false;
        catHlg.childAlignment = TextAnchor.MiddleLeft;

        var catHdr = _tpl.CreateText(catHdrRow.transform, "CategoriesHdr", "物品分类 (Handbook)", 13.5f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Left, FontStyles.Bold);
        catHdr.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        _clearCategoryBtn = _tpl.CloneActionButton(catHdrRow.transform, "ClearCatBtn", "全部", 52f, 22f, 11, () =>
        {
            _model.SelectedRootCategoryId = null;
            _model.SelectedSubCategoryId = null;
            ApplyFilterMutation(resetScroll: true);
        });

        var catScrollHost = new GameObject("CatScrollHost", typeof(RectTransform), typeof(LayoutElement));
        catScrollHost.transform.SetParent(railGo.transform, worldPositionStays: false);
        catScrollHost.GetComponent<LayoutElement>().flexibleHeight = 1f;
        catScrollHost.GetComponent<LayoutElement>().minHeight = 140f;
        var (_, _, catContent) = _tpl.CreateScrollView(catScrollHost.transform, "CatScroll");
        _categoryListContent = catContent;

        // Right vertical hairline
        var vLine = _tpl.CreatePanel(railGo.transform, "RightSeparator", EftUiTemplateCache.ColorHairline);
        var vLe = vLine.gameObject.AddComponent<LayoutElement>();
        vLe.ignoreLayout = true;
        vLine.rectTransform.anchorMin = new Vector2(1f, 0f);
        vLine.rectTransform.anchorMax = new Vector2(1f, 1f);
        vLine.rectTransform.pivot = new Vector2(1f, 0.5f);
        vLine.rectTransform.anchoredPosition = Vector2.zero;
        vLine.rectTransform.sizeDelta = new Vector2(1f, 0f);
    }

    private void BuildCenterCatalog(Transform parent)
    {
        var centerGo = new GameObject("CenterCatalog", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        centerGo.transform.SetParent(parent, worldPositionStays: false);
        centerGo.GetComponent<LayoutElement>().flexibleWidth = 1f;
        centerGo.GetComponent<LayoutElement>().minWidth = 420f;

        var vlg = centerGo.GetComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(10, 10, 8, 8);
        vlg.spacing = 6f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        // Toolbar Row 1: Status + Level + HideLocked chips (strictly 30px tall)
        var row1 = new GameObject("FilterRow1", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row1.transform.SetParent(centerGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(row1, height: 30f, flexW: -1f, flexH: 0f);
        var r1Hlg = row1.GetComponent<HorizontalLayoutGroup>();
        r1Hlg.spacing = 5f;
        r1Hlg.childAlignment = TextAnchor.MiddleLeft;
        r1Hlg.childControlWidth = true;
        r1Hlg.childControlHeight = true;
        r1Hlg.childForceExpandWidth = false;
        r1Hlg.childForceExpandHeight = false;

        AddStatusChip(row1.transform, WorkshopStatusFilter.All, "全部", 52f);
        AddStatusChip(row1.transform, WorkshopStatusFilter.CraftableNow, "可立即做", 76f);
        AddStatusChip(row1.transform, WorkshopStatusFilter.Favorites, "★ 收藏", 66f);
        AddStatusChip(row1.transform, WorkshopStatusFilter.ActiveOrReady, "进行中/待领", 88f);

        var sepGo = new GameObject("Sep", typeof(RectTransform), typeof(Image));
        sepGo.transform.SetParent(row1.transform, worldPositionStays: false);
        sepGo.GetComponent<Image>().color = EftUiTemplateCache.ColorHairline;
        EftUiTemplateCache.SetFixedLayout(sepGo, width: 1f, height: 20f, flexW: 0f, flexH: 0f);

        AddLevelChip(row1.transform, 0, "全等级", 56f);
        AddLevelChip(row1.transform, 1, "Lv.1", 44f);
        AddLevelChip(row1.transform, 2, "Lv.2", 44f);
        AddLevelChip(row1.transform, 3, "Lv.3", 44f);

        var r1Spacer = new GameObject("Spacer", typeof(RectTransform), typeof(LayoutElement));
        r1Spacer.transform.SetParent(row1.transform, worldPositionStays: false);
        r1Spacer.GetComponent<LayoutElement>().flexibleWidth = 1f;

        _hideLockedChip = CreateChipButton(row1.transform, "HideLockedChip", "隐藏未解锁", 90f, () =>
        {
            _model.HideLocked = !_model.HideLocked;
            ApplyFilterMutation(resetScroll: true);
        });

        // Toolbar Row 2: Search + Count + Sort DropDown (strictly 32px tall)
        var row2 = new GameObject("FilterRow2", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        row2.transform.SetParent(centerGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(row2, height: 32f, flexW: -1f, flexH: 0f);
        var r2Hlg = row2.GetComponent<HorizontalLayoutGroup>();
        r2Hlg.spacing = 8f;
        r2Hlg.childAlignment = TextAnchor.MiddleLeft;
        r2Hlg.childControlWidth = true;
        r2Hlg.childControlHeight = true;
        r2Hlg.childForceExpandWidth = false;
        r2Hlg.childForceExpandHeight = false;

        var searchWrap = new GameObject("SearchWrap", typeof(RectTransform));
        searchWrap.transform.SetParent(row2.transform, worldPositionStays: false);
        var searchLe = EftUiTemplateCache.SetFixedLayout(searchWrap, height: 30f, flexW: 1f, flexH: 0f);
        searchLe.minWidth = 160f;

        _searchInput = _tpl.CloneSearchField(searchWrap.transform, "搜索成品名称或所需材料/工具...");
        if (_searchInput != null)
        {
            EftUiTemplateCache.Stretch((RectTransform)_searchInput.transform);
            _searchInput.onValueChanged.AddListener(HandleSearchTextChanged);
        }

        // Clear X button positioned to the left of the EFT magnifying glass icon
        var clearGo = new GameObject("ClearSearch", typeof(RectTransform), typeof(Image), typeof(Button));
        clearGo.transform.SetParent(searchWrap.transform, worldPositionStays: false);
        var clearRt = (RectTransform)clearGo.transform;
        clearRt.anchorMin = new Vector2(1f, 0.5f);
        clearRt.anchorMax = new Vector2(1f, 0.5f);
        clearRt.pivot = new Vector2(1f, 0.5f);
        clearRt.anchoredPosition = new Vector2(-30f, 0f);
        clearRt.sizeDelta = new Vector2(20f, 20f);
        clearGo.GetComponent<Image>().color = new Color(0.22f, 0.22f, 0.22f, 0.78f);
        _tpl.CreateText(clearGo.transform, "X", "×", 14f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Center);
        _searchClearBtn = clearGo.GetComponent<Button>();
        _searchClearBtn.onClick.AddListener(() => SetSearchQueryImmediate(string.Empty));
        clearGo.SetActive(false);

        _resultCountText = _tpl.CreateText(row2.transform, "ResultCount", "", 13f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Right);
        EftUiTemplateCache.SetFixedLayout(_resultCountText.gameObject, width: 78f, height: 28f, flexW: 0f, flexH: 0f);

        BuildSortSelector(row2.transform);

        // Grid Scroll Container
        var gridHost = new GameObject("GridHost", typeof(RectTransform), typeof(LayoutElement));
        gridHost.transform.SetParent(centerGo.transform, worldPositionStays: false);
        gridHost.GetComponent<LayoutElement>().flexibleHeight = 1f;
        var (scrollRect, viewport, content) = _tpl.CreateScrollView(gridHost.transform, "RecipeGridScroll");

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        InventoryController invCtrl = hideout?._inventoryController;
        ItemUiContext itemUiCtx = ItemUiContext.Instance;

        _gridView = gridHost.AddComponent<WorkshopGridView>();
        _gridView.Setup(_tpl, scrollRect, viewport, content, invCtrl, itemUiCtx);
        _gridView.OnRecipeSelected = HandleRecipeSelected;
        _gridView.OnRecipeDoubleClicked = HandleRecipeDoubleClicked;
        _gridView.OnRecipeFavoriteToggled = HandleRecipeFavoriteToggled;

        // Empty state overlay centered inside GridHost
        _emptyCatalogPanel = new GameObject("EmptyCatalog", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _emptyCatalogPanel.transform.SetParent(gridHost.transform, worldPositionStays: false);
        var empRt = (RectTransform)_emptyCatalogPanel.transform;
        empRt.anchorMin = new Vector2(0.5f, 0.5f);
        empRt.anchorMax = new Vector2(0.5f, 0.5f);
        empRt.pivot = new Vector2(0.5f, 0.5f);
        empRt.sizeDelta = new Vector2(320f, 96f);
        var empVlg = _emptyCatalogPanel.GetComponent<VerticalLayoutGroup>();
        empVlg.spacing = 10f;
        empVlg.childAlignment = TextAnchor.MiddleCenter;
        empVlg.childControlWidth = true;
        empVlg.childControlHeight = true;
        empVlg.childForceExpandWidth = false;
        empVlg.childForceExpandHeight = false;

        var empLbl = _tpl.CreateText(_emptyCatalogPanel.transform, "Hint", "没有符合当前筛选条件的配方", 15f, EftUiTemplateCache.ColorTextMuted, TextAlignmentOptions.Center);
        empLbl.gameObject.AddComponent<LayoutElement>().preferredHeight = 24f;
        _tpl.CloneActionButton(_emptyCatalogPanel.transform, "ResetFiltersBtn", "重置筛选条件", 148f, 32f, 14, ResetAllFilters);
        _emptyCatalogPanel.SetActive(false);

        // Right vertical hairline
        var vLine = _tpl.CreatePanel(centerGo.transform, "CenterSeparator", EftUiTemplateCache.ColorHairline);
        var vLe = vLine.gameObject.AddComponent<LayoutElement>();
        vLe.ignoreLayout = true;
        vLine.rectTransform.anchorMin = new Vector2(1f, 0f);
        vLine.rectTransform.anchorMax = new Vector2(1f, 1f);
        vLine.rectTransform.pivot = new Vector2(1f, 0.5f);
        vLine.rectTransform.anchoredPosition = Vector2.zero;
        vLine.rectTransform.sizeDelta = new Vector2(1f, 0f);
    }

    private void BuildSortSelector(Transform parent)
    {
        if (_tpl.DropDownTemplate != null)
        {
            _sortDropdown = Instantiate(_tpl.DropDownTemplate, parent, worldPositionStays: false);
            _sortDropdown.name = "SortDropDown";
            _sortDropdown.gameObject.SetActive(true);
            EftUiTemplateCache.SetFixedLayout(_sortDropdown.gameObject, width: 132f, height: 30f, flexW: 0f, flexH: 0f);
            _sortDropdown.Show(SortLabels);
            _sortDropdown.UpdateValue((int)_model.SortMode, sendCallback: false);
            _sortDropdown.OnValueChanged.Subscribe(idx =>
            {
                _model.SortMode = (WorkshopSortMode)Mathf.Clamp(idx, 0, SortLabels.Length - 1);
                ApplyFilterMutation(resetScroll: true);
            });
        }
        else
        {
            _sortFallbackBtn = _tpl.CloneActionButton(parent, "SortCycleBtn", $"排序: {SortLabels[0]}", 132f, 30f, 13, () =>
            {
                int next = ((int)_model.SortMode + 1) % SortLabels.Length;
                _model.SortMode = (WorkshopSortMode)next;
                _sortFallbackBtn.SetRawText($"排序: {SortLabels[next]}", 13);
                ApplyFilterMutation(resetScroll: true);
            });
        }
    }

    private void BuildRightInspector(Transform parent)
    {
        var inspGo = new GameObject("RightInspector", typeof(RectTransform), typeof(Image));
        inspGo.transform.SetParent(parent, worldPositionStays: false);
        inspGo.GetComponent<Image>().color = EftUiTemplateCache.ColorPanelBg;
        EftUiTemplateCache.SetFixedLayout(inspGo, width: 438f, flexW: 0f, flexH: -1f);

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        InventoryController invCtrl = hideout?._inventoryController;

        _inspector = inspGo.AddComponent<WorkshopInspectorPane>();
        _inspector.Setup(
            _tpl,
            _model,
            invCtrl,
            ItemUiContext.Instance,
            onReverseSearchIngredient: HandleReverseSearchIngredient,
            onToggleFavorite: HandleRecipeFavoriteToggled,
            onCraftMutated: HandleCraftMutated);
    }

    private void AddStatusChip(Transform parent, WorkshopStatusFilter filter, string label, float width)
    {
        ChipButton chip = CreateChipButton(parent, $"Status_{filter}", label, width, () =>
        {
            _model.StatusFilter = filter;
            ApplyFilterMutation(resetScroll: true);
        });
        _statusChips.Add((filter, chip));
    }

    private void AddLevelChip(Transform parent, int level, string label, float width)
    {
        ChipButton chip = CreateChipButton(parent, $"Level_{level}", label, width, () =>
        {
            _model.LevelFilter = level;
            ApplyFilterMutation(resetScroll: true);
        });
        _levelChips.Add((level, chip));
    }

    private ChipButton CreateChipButton(Transform parent, string name, string label, float width, Action onClick)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(go, width: width, height: 26f, flexW: 0f, flexH: 0f);

        Image bg = go.GetComponent<Image>();
        bg.color = EftUiTemplateCache.ColorRowIdle;

        Button btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => onClick?.Invoke());

        TextMeshProUGUI txt = _tpl.CreateText(go.transform, "Label", label, 12.5f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Center);
        EftUiTemplateCache.Stretch(txt.rectTransform, 4f, 4f, 0f, 0f);

        return new ChipButton
        {
            Root = go,
            Button = btn,
            Background = bg,
            Label = txt,
        };
    }

    internal void Show(
        HideoutScreenOverlay overlay,
        AreaData[] areas,
        EAreaType? initialStation,
        Action onCloseRequested)
    {
        _overlay = overlay;
        _onCloseRequested = onCloseRequested;

        _model.SelectedStationFilter = initialStation;
        _model.SelectedRootCategoryId = null;
        _model.SelectedSubCategoryId = null;
        _model.StatusFilter = WorkshopStatusFilter.All;
        _model.LevelFilter = 0;
        _model.HideLocked = HideoutQoLPlugin.WorkshopHideLockedByDefault != null && HideoutQoLPlugin.WorkshopHideLockedByDefault.Value;
        _model.SearchQuery = string.Empty;

        _pendingSearchText = string.Empty;
        _searchDebounceDeadline = -1f;
        if (_searchInput != null)
        {
            _searchInput.SetTextWithoutNotify(string.Empty);
        }
        if (_searchClearBtn != null)
        {
            _searchClearBtn.gameObject.SetActive(false);
        }

        _model.RebuildFromHideout(areas);

        EnsureDefaultSelection();

        _isOpen = true;
        gameObject.SetActive(true);
        transform.SetAsLastSibling();
        ShowGameObject(instant: true);

        SubscribeHideoutEvents();

        _lastAppliedColumns = HideoutQoLPlugin.WorkshopGridColumns != null ? HideoutQoLPlugin.WorkshopGridColumns.Value : 2;
        _hasGenSnapshot = false;
        RefreshAllUi(resetScroll: true);
        _nextTimerTick = Time.unscaledTime + 1f;
    }

    internal bool TryCloseOpenPopup()
    {
        if (_sortDropdown != null && _sortDropdown.CurrentState)
        {
            _sortDropdown.ToggleMenu(state: false);
            return true;
        }
        return false;
    }

    internal bool IsSearchFocused => _searchInput != null && _searchInput.isFocused;

    public override void Close()
    {
        if (!_isOpen && !gameObject.activeSelf)
        {
            return;
        }

        _isOpen = false;
        UnsubscribeHideoutEvents();

        if (_sortDropdown != null && _sortDropdown.CurrentState)
        {
            _sortDropdown.ToggleMenu(state: false);
        }

        _inspector?.ClosePreview();
        _gridView?.Clear();

        base.Close();
    }

    public override ETranslateResult TranslateCommand(ECommand command)
    {
        if (command == ECommand.Escape)
        {
            if (TryCloseOpenPopup())
            {
                return ETranslateResult.Block;
            }
            HandleCloseClicked();
            return ETranslateResult.Block;
        }
        return ETranslateResult.Ignore;
    }

    private void Update()
    {
        if (!_isOpen)
        {
            return;
        }

        // 1. Debounced search input flush
        if (_searchDebounceDeadline >= 0f && Time.unscaledTime >= _searchDebounceDeadline)
        {
            _searchDebounceDeadline = -1f;
            if (!string.Equals(_model.SearchQuery, _pendingSearchText, StringComparison.Ordinal))
            {
                _model.SearchQuery = _pendingSearchText ?? string.Empty;
                ApplyFilterMutation(resetScroll: true);
            }
        }

        // 2. 1-second timer tick (live craft progress + generator fuel countdown)
        if (Time.unscaledTime >= _nextTimerTick)
        {
            _nextTimerTick = Time.unscaledTime + 1f;
            if (_model.TickActiveTimers())
            {
                _gridView.RefreshVisibleStates();
                _inspector.RefreshTimerOnly();
            }
            UpdateGeneratorHeaderPill();
        }
    }

    private void LateUpdate()
    {
        if (!_isOpen)
        {
            return;
        }

        if (_dirtyCatalog || _dirtyStates)
        {
            bool rebuildCats = _dirtyCatalog;
            _dirtyCatalog = false;
            _dirtyStates = false;

            _model.RefreshStatesOnly(rebuildCategories: rebuildCats);
            SyncSelectionAfterDataChange();
            RefreshAllUi(resetScroll: false);
        }
    }

    private void HandleSearchTextChanged(string raw)
    {
        _pendingSearchText = raw ?? string.Empty;
        if (_searchClearBtn != null)
        {
            _searchClearBtn.gameObject.SetActive(!string.IsNullOrEmpty(_pendingSearchText));
        }

        float delay = HideoutQoLPlugin.SearchDebounceMs != null
            ? Mathf.Clamp(HideoutQoLPlugin.SearchDebounceMs.Value / 1000f, 0f, 0.6f)
            : 0.15f;

        if (delay <= 0.01f)
        {
            _searchDebounceDeadline = -1f;
            _model.SearchQuery = _pendingSearchText;
            ApplyFilterMutation(resetScroll: true);
        }
        else
        {
            _searchDebounceDeadline = Time.unscaledTime + delay;
        }
    }

    private void SetSearchQueryImmediate(string query)
    {
        _pendingSearchText = query ?? string.Empty;
        _searchDebounceDeadline = -1f;
        if (_searchInput != null)
        {
            _searchInput.SetTextWithoutNotify(_pendingSearchText);
        }
        if (_searchClearBtn != null)
        {
            _searchClearBtn.gameObject.SetActive(!string.IsNullOrEmpty(_pendingSearchText));
        }
        _model.SearchQuery = _pendingSearchText;
        ApplyFilterMutation(resetScroll: true);
    }

    private void HandleReverseSearchIngredient(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return;
        }
        // Broaden scope so the user immediately sees every craft across all stations using that item
        _model.SelectedRootCategoryId = null;
        _model.SelectedSubCategoryId = null;
        _model.StatusFilter = WorkshopStatusFilter.All;
        _model.LevelFilter = 0;
        SetSearchQueryImmediate(itemName.Trim());
    }

    private void ResetAllFilters()
    {
        _model.SelectedRootCategoryId = null;
        _model.SelectedSubCategoryId = null;
        _model.StatusFilter = WorkshopStatusFilter.All;
        _model.LevelFilter = 0;
        _model.HideLocked = false;
        SetSearchQueryImmediate(string.Empty);
    }

    private void ApplyFilterMutation(bool resetScroll)
    {
        _model.SetFiltersAndRefresh();
        SyncSelectionAfterDataChange();
        RefreshAllUi(resetScroll);
    }

    private void EnsureDefaultSelection()
    {
        if (_model.FilteredRecipes.Count > 0)
        {
            _selectedSchemeId = _model.FilteredRecipes[0].SchemeId;
        }
        else
        {
            _selectedSchemeId = null;
        }
    }

    private void SyncSelectionAfterDataChange()
    {
        if (!string.IsNullOrEmpty(_selectedSchemeId))
        {
            WorkshopCatalogModel.RecipeEntry existing = _model.FindBySchemeId(_selectedSchemeId);
            if (existing != null)
            {
                return;
            }
        }
        EnsureDefaultSelection();
    }

    private void RefreshAllUi(bool resetScroll)
    {
        int cols = HideoutQoLPlugin.WorkshopGridColumns != null
            ? Mathf.Clamp(HideoutQoLPlugin.WorkshopGridColumns.Value, 1, 3)
            : 2;
        _lastAppliedColumns = cols;

        RefreshHeader();
        RefreshStationRail();
        RefreshCategoryRail();
        RefreshFilterChips();

        bool showStationTag = !_model.SelectedStationFilter.HasValue;
        _gridView.SetItems(_model.FilteredRecipes, cols, _selectedSchemeId, showStationTag, resetScroll);

        _resultCountText.SetMonospaceText($"{_model.FilteredRecipes.Count} / {_model.AllRecipes.Count}", onlyDigits: true);
        _emptyCatalogPanel.SetActive(_model.FilteredRecipes.Count == 0);

        WorkshopCatalogModel.RecipeEntry selected = _model.FindBySchemeId(_selectedSchemeId);
        if (selected == null && _model.FilteredRecipes.Count > 0)
        {
            selected = _model.FilteredRecipes[0];
            _selectedSchemeId = selected.SchemeId;
            _gridView.UpdateSelection(_selectedSchemeId);
        }

        _inspector.SetEntry(selected);
    }

    private void RefreshHeader()
    {
        WorkshopCatalogModel.StationInfo st = _model.GetStation(_model.SelectedStationFilter);
        if (st != null)
        {
            _headerTitleText.text = $"{st.DisplayName}  Lv.{st.Level}  ·  工坊";
            if (_headerStationIcon != null)
            {
                _headerStationIcon.sprite = st.Area?.Template?.AreaIcon;
                _headerStationIcon.gameObject.SetActive(_headerStationIcon.sprite != null);
            }
        }
        else
        {
            _headerTitleText.text = "藏身处全站制作工坊";
            if (_headerStationIcon != null)
            {
                Sprite fallback = _model.Stations.Count > 0 ? _model.Stations[0].Area?.Template?.AreaIcon : null;
                _headerStationIcon.sprite = fallback;
                _headerStationIcon.gameObject.SetActive(fallback != null);
            }
        }

        _headerCountsText.SetMonospaceText(
            $"进行中 {_model.TotalProducingCount}  ·  待领取 {_model.TotalReadyCount}  ·  全部配方 {_model.AllRecipes.Count}",
            onlyDigits: true);

        if (_collectAllBtn != null)
        {
            _collectAllBtn.gameObject.SetActive(_model.TotalReadyCount > 0);
            _collectAllBtn.SetRawText($"一键全领 ({_model.TotalReadyCount})", 14);
            _collectAllBtn.Interactable = !_collectingAll && _model.TotalReadyCount > 0;
        }

        if (_areaDetailsBtn != null)
        {
            AreaData targetArea = GetTargetAreaForDetails();
            _areaDetailsBtn.gameObject.SetActive(targetArea != null);
        }

        UpdateGeneratorHeaderPill();
    }

    private AreaData GetTargetAreaForDetails()
    {
        if (_model.SelectedStationFilter.HasValue)
        {
            return _model.GetStation(_model.SelectedStationFilter)?.Area;
        }
        WorkshopCatalogModel.RecipeEntry sel = _model.FindBySchemeId(_selectedSchemeId);
        if (sel != null)
        {
            return sel.Area;
        }
        return _model.Stations.Count > 0 ? _model.Stations[0].Area : null;
    }

    private void UpdateGeneratorHeaderPill()
    {
        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        EnergyController energy = hideout?.EnergyController;
        AreaDetailsData fuel = GeneratorBehaviour.FuelDetails;

        bool isActive = energy != null && fuel != null && fuel.IsActive;
        bool isOn = isActive && energy.IsEnergyGenerationOn;
        EDetailsType fuelType = isActive ? fuel.Type : default;
        int timeSec = isActive ? fuel.Time : -1;
        int pct = isActive ? fuel.Percentages : -1;

        if (_hasGenSnapshot
            && _genSnapActive == isActive
            && _genSnapOn == isOn
            && _genSnapFuelType == fuelType
            && _genSnapTime == timeSec
            && _genSnapPct == pct)
        {
            return;
        }

        _hasGenSnapshot = true;
        _genSnapActive = isActive;
        _genSnapOn = isOn;
        _genSnapFuelType = fuelType;
        _genSnapTime = timeSec;
        _genSnapPct = pct;

        if (!isActive)
        {
            _generatorBtnText.text = "未建造发电机";
            _generatorBtnBg.color = EftUiTemplateCache.ColorSubBarBg;
            _generatorLed.color = EftUiTemplateCache.ColorAccentLocked;
            _generatorBtn.interactable = false;
            return;
        }

        _generatorBtn.interactable = true;
        if (energy.IsEnergyGenerationOn)
        {
            string rem = fuel.Time > 0 ? WorkshopRecipeTile.FormatCompactDuration(fuel.Time) : "--:--";
            _generatorBtnText.SetMonospaceText($"运行中 · {rem} ({fuel.Percentages}%)", onlyDigits: true);
            _generatorBtnBg.color = new Color(0.14f, 0.24f, 0.14f, 0.90f);
            _generatorLed.color = EftUiTemplateCache.ColorAccentProducing;
        }
        else if (fuel.Type == EDetailsType.NoFuel)
        {
            _generatorBtnText.text = "发电机已关闭 (缺燃料)";
            _generatorBtnBg.color = new Color(0.36f, 0.14f, 0.12f, 0.90f);
            _generatorLed.color = EftUiTemplateCache.ColorAccentMissing;
        }
        else
        {
            _generatorBtnText.text = "发电机已关闭 (点击开启)";
            _generatorBtnBg.color = new Color(0.26f, 0.16f, 0.12f, 0.90f);
            _generatorLed.color = EftUiTemplateCache.ColorAccentReady;
        }
    }

    private void RefreshStationRail()
    {
        int totalRows = 1 + _model.Stations.Count;
        while (_stationRows.Count < totalRows)
        {
            _stationRows.Add(CreateStationRow(_stationListContent));
        }

        // Row 0: All stations
        BindStationRow(
            _stationRows[0],
            null,
            "全部工坊",
            BuildStationBadge(_model.TotalReadyCount, _model.TotalProducingCount, _model.AllRecipes.Count, _model.AllRecipes.Count, isOverview: true),
            !_model.SelectedStationFilter.HasValue,
            hasHighlight: _model.TotalReadyCount > 0,
            hasCraftable: false);

        for (int i = 0; i < _model.Stations.Count; i++)
        {
            WorkshopCatalogModel.StationInfo st = _model.Stations[i];
            bool selected = _model.SelectedStationFilter.HasValue && _model.SelectedStationFilter.Value == st.AreaType;
            BindStationRow(
                _stationRows[1 + i],
                st.AreaType,
                $"{st.DisplayName} Lv.{st.Level}",
                BuildStationBadge(st.ReadyCount, st.ProducingCount, st.CraftableCount, st.TotalCount, isOverview: false),
                selected,
                hasHighlight: st.ReadyCount > 0,
                hasCraftable: st.CraftableCount > 0);
        }

        for (int i = totalRows; i < _stationRows.Count; i++)
        {
            _stationRows[i].Root.SetActive(false);
        }
    }

    private static string BuildStationBadge(int ready, int producing, int craftable, int total, bool isOverview)
    {
        if (ready > 0)
        {
            return $"✔{ready}";
        }
        if (producing > 0)
        {
            return $"⚙{producing}";
        }
        return isOverview ? total.ToString() : $"{craftable}/{total}";
    }

    private void BindStationRow(StationRowView row, EAreaType? filter, string title, string badge, bool selected, bool hasHighlight, bool hasCraftable)
    {
        row.Root.SetActive(true);
        row.StationFilter = filter;
        row.TitleText.text = title;
        row.BadgeText.SetMonospaceText(badge, onlyDigits: true);

        row.Background.color = selected ? EftUiTemplateCache.ColorRowSelected : EftUiTemplateCache.ColorRowIdle;
        row.TitleText.color = selected ? EftUiTemplateCache.ColorTextDark : EftUiTemplateCache.ColorTextPrimary;
        row.BadgeText.color = selected
            ? EftUiTemplateCache.ColorTextDark
            : (hasHighlight
                ? EftUiTemplateCache.ColorAccentReady
                : (hasCraftable ? EftUiTemplateCache.ColorAccentCraftable : EftUiTemplateCache.ColorTextSecondary));
    }

    private StationRowView CreateStationRow(Transform parent)
    {
        var go = new GameObject("StationRow", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
        go.transform.SetParent(parent, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(go, height: 28f, flexW: -1f, flexH: 0f);

        Image bg = go.GetComponent<Image>();
        bg.color = EftUiTemplateCache.ColorRowIdle;

        var hlg = go.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(8, 8, 2, 2);
        hlg.spacing = 6f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = true;

        TextMeshProUGUI title = _tpl.CreateText(go.transform, "Name", "", 13f, EftUiTemplateCache.ColorTextPrimary);
        title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        TextMeshProUGUI badge = _tpl.CreateText(go.transform, "Badge", "", 12f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Right);
        EftUiTemplateCache.SetFixedLayout(badge.gameObject, width: 50f, height: 22f, flexW: 0f, flexH: 0f);

        var row = new StationRowView
        {
            Root = go,
            Background = bg,
            Button = go.GetComponent<Button>(),
            TitleText = title,
            BadgeText = badge,
        };

        row.Button.onClick.AddListener(() =>
        {
            _model.SelectedStationFilter = row.StationFilter;
            ApplyFilterMutation(resetScroll: true);
        });

        return row;
    }

    private void RefreshCategoryRail()
    {
        bool hasCatFilter = !string.IsNullOrEmpty(_model.SelectedRootCategoryId);
        if (_clearCategoryBtn != null)
        {
            _clearCategoryBtn.gameObject.SetActive(hasCatFilter);
        }

        int rowIdx = 0;
        IReadOnlyList<WorkshopCatalogModel.CategoryNode> cats = _model.Categories;
        for (int i = 0; i < cats.Count; i++)
        {
            WorkshopCatalogModel.CategoryNode root = cats[i];
            CategoryRowView rootRow = EnsureCategoryRow(rowIdx++);
            bool rootSelected = string.Equals(_model.SelectedRootCategoryId, root.Id, StringComparison.Ordinal)
                && string.IsNullOrEmpty(_model.SelectedSubCategoryId);
            BindCategoryRow(rootRow, root.Id, null, root.Name, root.Count, root.IconPath, root.Children.Count > 0, root.Expanded, isSub: false, selected: rootSelected);

            if (root.Expanded)
            {
                for (int c = 0; c < root.Children.Count; c++)
                {
                    WorkshopCatalogModel.CategoryNode sub = root.Children[c];
                    CategoryRowView subRow = EnsureCategoryRow(rowIdx++);
                    bool subSelected = string.Equals(_model.SelectedRootCategoryId, root.Id, StringComparison.Ordinal)
                        && string.Equals(_model.SelectedSubCategoryId, sub.Id, StringComparison.Ordinal);
                    BindCategoryRow(subRow, root.Id, sub.Id, sub.Name, sub.Count, null, hasChildren: false, expanded: false, isSub: true, selected: subSelected);
                }
            }
        }

        for (int i = rowIdx; i < _categoryRows.Count; i++)
        {
            _categoryRows[i].Root.SetActive(false);
        }
    }

    private CategoryRowView EnsureCategoryRow(int index)
    {
        while (_categoryRows.Count <= index)
        {
            _categoryRows.Add(CreateCategoryRow(_categoryListContent));
        }
        return _categoryRows[index];
    }

    private void BindCategoryRow(
        CategoryRowView row,
        string rootId,
        string subId,
        string title,
        int count,
        string iconPath,
        bool hasChildren,
        bool expanded,
        bool isSub,
        bool selected)
    {
        row.Root.SetActive(true);
        row.RootId = rootId;
        row.SubId = subId;

        row.IndentSpacer.minWidth = isSub ? 20f : 0f;
        row.IndentSpacer.preferredWidth = isSub ? 20f : 0f;

        row.ArrowButton.gameObject.SetActive(!isSub && hasChildren);
        if (hasChildren && row.ArrowIcon != null)
        {
            row.ArrowIcon.sprite = expanded
                ? (_tpl.CategoryOpenSprite != null ? _tpl.CategoryOpenSprite : _tpl.CategoryCloseSprite)
                : (_tpl.CategoryCloseSprite != null ? _tpl.CategoryCloseSprite : _tpl.CategoryOpenSprite);
        }

        LoadCategoryIconAsync(row, iconPath, isSub);

        row.TitleText.text = title;
        row.CountText.SetMonospaceText(count.ToString(), onlyDigits: true);

        row.Background.color = selected ? EftUiTemplateCache.ColorRowSelected : EftUiTemplateCache.ColorRowIdle;
        row.TitleText.color = selected ? EftUiTemplateCache.ColorTextDark : (isSub ? EftUiTemplateCache.ColorTextSecondary : EftUiTemplateCache.ColorTextPrimary);
        row.CountText.color = selected ? EftUiTemplateCache.ColorTextDark : EftUiTemplateCache.ColorTextMuted;
    }

    private static void LoadCategoryIconAsync(CategoryRowView row, string iconPath, bool isSub)
    {
        if (row.CategoryIcon == null)
        {
            return;
        }

        if (isSub || string.IsNullOrEmpty(iconPath) || iconPath == "/files/handbook/")
        {
            row.CategoryIcon.gameObject.SetActive(false);
            return;
        }

        if (CategoryIconCache.TryGetValue(iconPath, out Sprite cached) && cached != null)
        {
            row.CategoryIcon.sprite = cached;
            row.CategoryIcon.gameObject.SetActive(true);
            return;
        }

        row.CategoryIcon.gameObject.SetActive(false);
        if (RagFair.IconsLoader == null)
        {
            return;
        }

        string expectedRoot = row.RootId;
        RagFair.IconsLoader.GetIcon(iconPath, sprite =>
        {
            if (sprite != null)
            {
                CategoryIconCache[iconPath] = sprite;
            }
            if (row.Root != null && sprite != null && string.Equals(row.RootId, expectedRoot, StringComparison.Ordinal))
            {
                row.CategoryIcon.sprite = sprite;
                row.CategoryIcon.gameObject.SetActive(true);
            }
        });
    }

    private CategoryRowView CreateCategoryRow(Transform parent)
    {
        var go = new GameObject("CategoryRow", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        go.transform.SetParent(parent, worldPositionStays: false);
        go.GetComponent<LayoutElement>().preferredHeight = 26f;

        Image bg = go.GetComponent<Image>();
        bg.color = EftUiTemplateCache.ColorRowIdle;

        var hlg = go.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(6, 8, 2, 2);
        hlg.spacing = 5f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        var indentGo = new GameObject("Indent", typeof(RectTransform), typeof(LayoutElement));
        indentGo.transform.SetParent(go.transform, worldPositionStays: false);
        var indentLe = indentGo.GetComponent<LayoutElement>();

        var arrowGo = new GameObject("ExpandBtn", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        arrowGo.transform.SetParent(go.transform, worldPositionStays: false);
        var arrowLe = arrowGo.GetComponent<LayoutElement>();
        arrowLe.preferredWidth = 16f;
        arrowLe.preferredHeight = 16f;
        Image arrowImg = arrowGo.GetComponent<Image>();
        arrowImg.preserveAspect = true;
        arrowImg.color = EftUiTemplateCache.ColorTextSecondary;

        var iconGo = new GameObject("CatIcon", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        iconGo.transform.SetParent(go.transform, worldPositionStays: false);
        var iconLe = iconGo.GetComponent<LayoutElement>();
        iconLe.preferredWidth = 18f;
        iconLe.preferredHeight = 18f;
        Image catImg = iconGo.GetComponent<Image>();
        catImg.preserveAspect = true;
        catImg.raycastTarget = false;

        TextMeshProUGUI title = _tpl.CreateText(go.transform, "Title", "", 13f, EftUiTemplateCache.ColorTextPrimary);
        title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        TextMeshProUGUI count = _tpl.CreateText(go.transform, "Count", "", 11.5f, EftUiTemplateCache.ColorTextMuted, TextAlignmentOptions.Right);
        count.gameObject.AddComponent<LayoutElement>().minWidth = 28f;

        var row = new CategoryRowView
        {
            Root = go,
            Background = bg,
            RowButton = go.GetComponent<Button>(),
            ArrowButton = arrowGo.GetComponent<Button>(),
            ArrowIcon = arrowImg,
            CategoryIcon = catImg,
            IndentSpacer = indentLe,
            TitleText = title,
            CountText = count,
        };

        row.ArrowButton.onClick.AddListener(() => ToggleCategoryExpanded(row.RootId));
        row.RowButton.onClick.AddListener(() => SelectCategoryNode(row.RootId, row.SubId));
        return row;
    }

    private void ToggleCategoryExpanded(string rootId)
    {
        for (int i = 0; i < _model.Categories.Count; i++)
        {
            if (string.Equals(_model.Categories[i].Id, rootId, StringComparison.Ordinal))
            {
                _model.Categories[i].Expanded = !_model.Categories[i].Expanded;
                RefreshCategoryRail();
                break;
            }
        }
    }

    private void SelectCategoryNode(string rootId, string subId)
    {
        if (string.IsNullOrEmpty(subId))
        {
            if (string.Equals(_model.SelectedRootCategoryId, rootId, StringComparison.Ordinal)
                && string.IsNullOrEmpty(_model.SelectedSubCategoryId))
            {
                _model.SelectedRootCategoryId = null;
            }
            else
            {
                _model.SelectedRootCategoryId = rootId;
                _model.SelectedSubCategoryId = null;
                for (int i = 0; i < _model.Categories.Count; i++)
                {
                    if (string.Equals(_model.Categories[i].Id, rootId, StringComparison.Ordinal)
                        && _model.Categories[i].Children.Count > 0)
                    {
                        _model.Categories[i].Expanded = true;
                        break;
                    }
                }
            }
        }
        else
        {
            if (string.Equals(_model.SelectedRootCategoryId, rootId, StringComparison.Ordinal)
                && string.Equals(_model.SelectedSubCategoryId, subId, StringComparison.Ordinal))
            {
                _model.SelectedSubCategoryId = null;
            }
            else
            {
                _model.SelectedRootCategoryId = rootId;
                _model.SelectedSubCategoryId = subId;
            }
        }

        ApplyFilterMutation(resetScroll: true);
    }

    private void RefreshFilterChips()
    {
        for (int i = 0; i < _statusChips.Count; i++)
        {
            (WorkshopStatusFilter filter, ChipButton chip) = _statusChips[i];
            StyleChip(chip, _model.StatusFilter == filter);
        }

        for (int i = 0; i < _levelChips.Count; i++)
        {
            (int level, ChipButton chip) = _levelChips[i];
            StyleChip(chip, _model.LevelFilter == level);
        }

        StyleChip(_hideLockedChip, _model.HideLocked);
    }

    private static void StyleChip(ChipButton chip, bool active)
    {
        if (chip == null)
        {
            return;
        }
        chip.Background.color = active ? EftUiTemplateCache.ColorRowSelected : EftUiTemplateCache.ColorRowIdle;
        chip.Label.color = active ? EftUiTemplateCache.ColorTextDark : EftUiTemplateCache.ColorTextPrimary;
    }

    private void HandleRecipeSelected(WorkshopCatalogModel.RecipeEntry entry)
    {
        if (entry == null)
        {
            return;
        }
        _selectedSchemeId = entry.SchemeId;
        _gridView.UpdateSelection(_selectedSchemeId);
        _inspector.SetEntry(entry);
        RefreshHeader();
    }

    private void HandleRecipeDoubleClicked(WorkshopCatalogModel.RecipeEntry entry)
    {
        if (entry == null)
        {
            return;
        }
        HandleRecipeSelected(entry);
        _inspector.ExecuteQuickPrimaryAction(entry);
    }

    private void HandleRecipeFavoriteToggled(WorkshopCatalogModel.RecipeEntry entry)
    {
        if (entry?.Scheme == null)
        {
            return;
        }
        entry.Scheme.FavoriteScheme = !entry.Scheme.FavoriteScheme;
        _dirtyStates = true;
    }

    private void HandleCraftMutated()
    {
        _dirtyStates = true;
    }

    private void HandleToggleGenerator()
    {
        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        if (_overlay == null || hideout?.EnergyController == null)
        {
            return;
        }

        bool nextState = !hideout.EnergyController.IsEnergyGenerationOn;
        if (Singleton<GUISounds>.Instantiated)
        {
            Singleton<GUISounds>.Instance.PlayUISound(nextState ? EUISoundType.GeneratorTurnOn : EUISoundType.GeneratorTurnOff);
        }
        _overlay.OnGeneratorSwitched(nextState);
        _dirtyStates = true;
    }

    private async Task CollectAllReadyAsync()
    {
        if (_collectingAll || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }

        var readyList = new List<WorkshopCatalogModel.RecipeEntry>();
        IReadOnlyList<WorkshopCatalogModel.RecipeEntry> all = _model.AllRecipes;
        for (int i = 0; i < all.Count; i++)
        {
            if (all[i].State == WorkshopRecipeState.Ready)
            {
                readyList.Add(all[i]);
            }
        }

        if (readyList.Count == 0)
        {
            return;
        }

        _collectingAll = true;
        RefreshHeader();
        try
        {
            HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
            for (int i = 0; i < readyList.Count; i++)
            {
                if (!_isOpen)
                {
                    break;
                }
                WorkshopCatalogModel.RecipeEntry r = readyList[i];
                if (r.Producer == null)
                {
                    continue;
                }
                bool ok = await hideout.GetProducedItems(r.Producer, r.SchemeId, false);
                if (ok)
                {
                    r.Producer.GetItems(r.SchemeId);
                }
            }
        }
        finally
        {
            _collectingAll = false;
            _dirtyStates = true;
        }
    }

    private void HandleOpenVanillaAreaDetails()
    {
        AreaData target = GetTargetAreaForDetails();
        if (_overlay != null && target != null)
        {
            WorkshopUiPatch.RequestVanillaAreaDrawer(_overlay, target);
        }
    }

    private void HandleCloseClicked()
    {
        _onCloseRequested?.Invoke();
    }

    private void SubscribeHideoutEvents()
    {
        UnsubscribeHideoutEvents();

        for (int i = 0; i < _model.Stations.Count; i++)
        {
            ItemsProducerBase p = _model.Stations[i].Producer;
            if (p != null && !_subscribedProducers.Contains(p))
            {
                p.OnDataChanged += OnProducerDataChanged;
                p.OnItemChanged += OnProducerItemChanged;
                p.OnProduceStatusChanged += OnProducerStatusChanged;
                p.OnProductionComplete += OnProducerComplete;
                _subscribedProducers.Add(p);
            }
        }

        if (Singleton<HideoutRepresentation>.Instantiated)
        {
            _subscribedHideout = Singleton<HideoutRepresentation>.Instance;
            _subscribedHideout.OnItemsChanged += OnHideoutItemsChanged;
            _subscribedEnergy = _subscribedHideout.EnergyController;
            if (_subscribedEnergy != null)
            {
                _subscribedEnergy.OnEnergyGenerationChanged += OnEnergyChanged;
            }
        }

        _eventsHooked = true;
    }

    private void UnsubscribeHideoutEvents()
    {
        if (!_eventsHooked)
        {
            return;
        }
        _eventsHooked = false;

        for (int i = 0; i < _subscribedProducers.Count; i++)
        {
            ItemsProducerBase p = _subscribedProducers[i];
            if (p != null)
            {
                p.OnDataChanged -= OnProducerDataChanged;
                p.OnItemChanged -= OnProducerItemChanged;
                p.OnProduceStatusChanged -= OnProducerStatusChanged;
                p.OnProductionComplete -= OnProducerComplete;
            }
        }
        _subscribedProducers.Clear();

        if (_subscribedEnergy != null)
        {
            _subscribedEnergy.OnEnergyGenerationChanged -= OnEnergyChanged;
            _subscribedEnergy = null;
        }

        if (_subscribedHideout != null)
        {
            _subscribedHideout.OnItemsChanged -= OnHideoutItemsChanged;
            _subscribedHideout = null;
        }
    }

    private void OnProducerDataChanged() => _dirtyCatalog = true;
    private void OnProducerItemChanged(Item _) => _dirtyStates = true;
    private void OnProducerStatusChanged() => _dirtyStates = true;
    private void OnProducerComplete(ItemsProducerBase _, Item[] __, ProducingItemController ___) => _dirtyStates = true;
    private void OnEnergyChanged(bool _) => _dirtyStates = true;
    private void OnHideoutItemsChanged() => _dirtyStates = true;

    private static async void RunSafe(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log?.LogError($"HideoutQoL: Workshop task error: {ex}");
        }
    }
}
