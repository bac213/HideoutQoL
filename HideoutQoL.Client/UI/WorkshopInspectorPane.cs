using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using EFT.UI.WeaponModding;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HideoutQoL.Client.UI;

/// <summary>
/// Right-column inspector for the currently selected <see cref="WorkshopCatalogModel.RecipeEntry"/>.
/// Displays an interactive 3D <see cref="WeaponPreview"/> viewport (or compact 2D EFT icon card),
/// clean unclipped product metadata, scrollable <see cref="HideoutProductionRequirementView"/> rows
/// with [反查] reverse ingredient lookup and single-item flea buy, and the adaptive craft dock.
/// </summary>
internal sealed class WorkshopInspectorPane : MonoBehaviour
{
    private sealed class PreviewInputRelay : MonoBehaviour, IDragHandler, IScrollHandler
    {
        public Action<Vector2> OnDragDelta;
        public Action<float> OnScrollDelta;

        public void OnDrag(PointerEventData eventData)
        {
            OnDragDelta?.Invoke(eventData.delta);
        }

        public void OnScroll(PointerEventData eventData)
        {
            OnScrollDelta?.Invoke(eventData.scrollDelta.y);
        }
    }

    private sealed class MaterialRow
    {
        public GameObject Root;
        public Image StatusStripe;
        public HideoutItemViewFactory ItemFactory;
        public TextMeshProUGUI NameText;
        public TextMeshProUGUI DetailText;
        public DefaultUIButton ReverseSearchBtn;
        public DefaultUIButton BuySingleBtn;
        public ItemRequirement BoundReq;
        public string BoundItemName;
        public int MissingCount;
        public string BoundTemplateId;
        public string BoundSchemeId;
        public bool BoundIsFir;
        public bool BoundIsEncoded;
    }

    private EftUiTemplateCache _tpl;
    private InventoryController _inventoryController;
    private ItemUiContext _itemUiContext;
    private WorkshopCatalogModel _model;

    private GameObject _emptyStatePanel;
    private GameObject _bodyPanel;

    private TextMeshProUGUI _stationCrumbText;
    private Button _favToggleBtn;
    private Image _favToggleIcon;
    private TextMeshProUGUI _favToggleLabel;

    // 3D Preview + Clean Metadata
    private GameObject _preview3DBox;
    private CameraImage _cameraImage;
    private WeaponPreview _weaponPreview;
    private GameObject _fallback2DPanel;
    private HideoutItemViewFactory _fallback2DFactory;
    private TextMeshProUGUI _fallback2DTitle;
    private TextMeshProUGUI _fallback2DCategory;

    private GameObject _productMetaPanel;
    private TextMeshProUGUI _productTitleText;
    private TextMeshProUGUI _productWeightText;
    private TextMeshProUGUI _productCategoryText;
    private TextMeshProUGUI _metaStatsText;

    private TextMeshProUGUI _requirementsHeader;
    private RectTransform _reqContent;
    private readonly List<MaterialRow> _materialRows = new(8);
    private int _activeMaterialRowCount;

    private GameObject _fuelWarningBar;
    private GameObject _progressPanel;
    private Image _progressBarFill;
    private TextMeshProUGUI _progressText;
    private TextMeshProUGUI _hintText;

    private DefaultUIButton _startBtn;
    private DefaultUIButton _buyAllBtn;
    private DefaultUIButton _collectBtn;
    private DefaultUIButton _collectRepeatBtn;
    private DefaultUIButton _restartBtn;
    private DefaultUIButton _cancelBtn;

    private WorkshopCatalogModel.RecipeEntry _entry;
    private string _boundPreviewSchemeId;
    private bool _lastShow3D;
    private bool _opBusy;

    private Action<string> _onReverseSearchIngredient;
    private Action<WorkshopCatalogModel.RecipeEntry> _onToggleFavorite;
    private Action _onCraftMutated;

    internal void Setup(
        EftUiTemplateCache tpl,
        WorkshopCatalogModel model,
        InventoryController inventoryController,
        ItemUiContext itemUiContext,
        Action<string> onReverseSearchIngredient,
        Action<WorkshopCatalogModel.RecipeEntry> onToggleFavorite,
        Action onCraftMutated)
    {
        _tpl = tpl;
        _model = model;
        _inventoryController = inventoryController;
        _itemUiContext = itemUiContext;
        _onReverseSearchIngredient = onReverseSearchIngredient;
        _onToggleFavorite = onToggleFavorite;
        _onCraftMutated = onCraftMutated;

        BuildLayout();
        SetEntry(null);
    }

    private void BuildLayout()
    {
        EftUiTemplateCache.Stretch((RectTransform)transform);

        _emptyStatePanel = new GameObject("EmptyInspector", typeof(RectTransform));
        _emptyStatePanel.transform.SetParent(transform, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)_emptyStatePanel.transform);
        _tpl.CreateText(
            _emptyStatePanel.transform,
            "EmptyHint",
            "请从左侧选择一个制作配方查看详情",
            15f,
            EftUiTemplateCache.ColorTextMuted,
            TextAlignmentOptions.Center);

        _bodyPanel = new GameObject("InspectorBody", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _bodyPanel.transform.SetParent(transform, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)_bodyPanel.transform, 10f, 10f, 8f, 10f);
        var bodyVlg = _bodyPanel.GetComponent<VerticalLayoutGroup>();
        bodyVlg.spacing = 6f;
        bodyVlg.childControlWidth = true;
        bodyVlg.childControlHeight = true;
        bodyVlg.childForceExpandWidth = true;
        bodyVlg.childForceExpandHeight = false;

        // Top crumb + Favorite toggle (fixed 26px)
        var crumbRow = new GameObject("CrumbRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        crumbRow.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(crumbRow, height: 26f, flexW: -1f, flexH: 0f);
        var crumbHlg = crumbRow.GetComponent<HorizontalLayoutGroup>();
        crumbHlg.spacing = 8f;
        crumbHlg.childControlWidth = true;
        crumbHlg.childControlHeight = true;
        crumbHlg.childForceExpandWidth = false;
        crumbHlg.childForceExpandHeight = false;
        crumbHlg.childAlignment = TextAnchor.MiddleLeft;

        _stationCrumbText = _tpl.CreateText(crumbRow.transform, "StationCrumb", "", 13f, EftUiTemplateCache.ColorAccentReady);
        var crumbLe = _stationCrumbText.gameObject.AddComponent<LayoutElement>();
        crumbLe.flexibleWidth = 1f;
        crumbLe.preferredHeight = 24f;

        var favGo = new GameObject("FavoriteToggle", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
        favGo.transform.SetParent(crumbRow.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(favGo, width: 78f, height: 24f, flexW: 0f, flexH: 0f);
        favGo.GetComponent<Image>().color = EftUiTemplateCache.ColorSubBarBg;
        _favToggleBtn = favGo.GetComponent<Button>();
        _favToggleBtn.onClick.AddListener(() =>
        {
            if (_entry != null)
            {
                _onToggleFavorite?.Invoke(_entry);
            }
        });
        var favHlg = favGo.GetComponent<HorizontalLayoutGroup>();
        favHlg.padding = new RectOffset(6, 6, 2, 2);
        favHlg.spacing = 4f;
        favHlg.childAlignment = TextAnchor.MiddleCenter;
        favHlg.childControlWidth = true;
        favHlg.childControlHeight = true;
        favHlg.childForceExpandWidth = false;
        favHlg.childForceExpandHeight = false;

        var starGo = new GameObject("Star", typeof(RectTransform), typeof(Image));
        starGo.transform.SetParent(favGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(starGo, width: 14f, height: 14f, flexW: 0f, flexH: 0f);
        _favToggleIcon = starGo.GetComponent<Image>();
        _favToggleIcon.preserveAspect = true;
        _favToggleIcon.raycastTarget = false;
        _favToggleLabel = _tpl.CreateText(favGo.transform, "Label", "收藏", 12f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Center);

        Build3DPreviewBox();
        Build2DFallbackPreview();
        BuildCleanProductHeader();

        // Compact craft stats bar
        _metaStatsText = _tpl.CreateText(_bodyPanel.transform, "MetaStats", "", 12.5f, EftUiTemplateCache.ColorAccentReady);
        EftUiTemplateCache.SetFixedLayout(_metaStatsText.gameObject, height: 20f, flexW: -1f, flexH: 0f);

        // Requirements header
        var reqHeaderRow = new GameObject("ReqHeader", typeof(RectTransform));
        reqHeaderRow.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(reqHeaderRow, height: 22f, flexW: -1f, flexH: 0f);
        _requirementsHeader = _tpl.CreateText(reqHeaderRow.transform, "Title", "所需材料与工具", 13.5f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        EftUiTemplateCache.Stretch(_requirementsHeader.rectTransform);
        _tpl.CreateDivider(reqHeaderRow.transform, "Divider", vertical: false);

        // Scrollable Requirements list (absorbs flexible vertical space)
        var reqScrollContainer = new GameObject("ReqScrollArea", typeof(RectTransform), typeof(LayoutElement));
        reqScrollContainer.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        var reqScLe = reqScrollContainer.GetComponent<LayoutElement>();
        reqScLe.minHeight = 120f;
        reqScLe.flexibleHeight = 1f;
        var (_, _, reqContent) = _tpl.CreateScrollView(reqScrollContainer.transform, "ReqScroll", addBottomPadding: false);
        _reqContent = reqContent;

        // Fuel warning banner
        _fuelWarningBar = new GameObject("FuelWarning", typeof(RectTransform), typeof(Image));
        _fuelWarningBar.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_fuelWarningBar, height: 24f, flexW: -1f, flexH: 0f);
        _fuelWarningBar.GetComponent<Image>().color = new Color(0.55f, 0.16f, 0.12f, 0.50f);
        TextMeshProUGUI fuelWarnTxt = _tpl.CreateText(_fuelWarningBar.transform, "Text", "需要开启发电机以开始或持续制作", 12f, new Color32(238, 178, 118, 255), TextAlignmentOptions.Center);
        EftUiTemplateCache.Stretch(fuelWarnTxt.rectTransform, 6f, 6f, 0f, 0f);
        _fuelWarningBar.SetActive(false);

        // Progress bar (visible when Producing / Ready)
        _progressPanel = new GameObject("ProgressPanel", typeof(RectTransform), typeof(Image));
        _progressPanel.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_progressPanel, height: 24f, flexW: -1f, flexH: 0f);
        _progressPanel.GetComponent<Image>().color = new Color(0.10f, 0.12f, 0.10f, 0.90f);
        _progressBarFill = _tpl.CreatePanel(_progressPanel.transform, "Fill", new Color(0.28f, 0.56f, 0.28f, 0.65f));
        _progressBarFill.rectTransform.anchorMin = Vector2.zero;
        _progressBarFill.rectTransform.anchorMax = new Vector2(0f, 1f);
        _progressBarFill.rectTransform.pivot = new Vector2(0f, 0.5f);
        _progressBarFill.rectTransform.offsetMin = Vector2.zero;
        _progressBarFill.rectTransform.offsetMax = Vector2.zero;
        _progressText = _tpl.CreateText(_progressPanel.transform, "Label", "", 12.5f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Center, FontStyles.Bold);
        EftUiTemplateCache.Stretch(_progressText.rectTransform, 4f, 4f, 0f, 0f);
        _progressPanel.SetActive(false);

        // Status / lock hint
        _hintText = _tpl.CreateText(_bodyPanel.transform, "ActionHint", "", 12f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Center);
        EftUiTemplateCache.SetFixedLayout(_hintText.gameObject, height: 18f, flexW: -1f, flexH: 0f);

        // Action Buttons Dock (fixed 36px)
        var dockGo = new GameObject("ActionDock", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        dockGo.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(dockGo, height: 36f, flexW: -1f, flexH: 0f);
        var dockHlg = dockGo.GetComponent<HorizontalLayoutGroup>();
        dockHlg.spacing = 8f;
        dockHlg.childControlWidth = true;
        dockHlg.childControlHeight = true;
        dockHlg.childForceExpandWidth = true;
        dockHlg.childForceExpandHeight = false;

        _startBtn = _tpl.CloneActionButton(dockGo.transform, "BtnStart", "开始制造", 0f, 34f, 15, () => RunSafe(ExecuteStartAsync));
        _buyAllBtn = _tpl.CloneActionButton(dockGo.transform, "BtnBuyAll", "购齐缺料", 0f, 34f, 14, HandleBuyAllMissing);
        _collectBtn = _tpl.CloneActionButton(dockGo.transform, "BtnCollect", "领取成品", 0f, 34f, 15, () => RunSafe(() => ExecuteCollectAsync(repeatAfter: false)));
        _collectRepeatBtn = _tpl.CloneActionButton(dockGo.transform, "BtnCollectRepeat", "领完再造", 0f, 34f, 14, () => RunSafe(() => ExecuteCollectAsync(repeatAfter: true)));
        _restartBtn = _tpl.CloneActionButton(dockGo.transform, "BtnRestart", "重新开始", 0f, 34f, 14, () => RunSafe(ExecuteRestartAsync));
        _cancelBtn = _tpl.CloneActionButton(dockGo.transform, "BtnCancel", "取消制造", 0f, 34f, 14, () => RunSafe(ExecuteCancelAsync));
    }

    private void Build3DPreviewBox()
    {
        _preview3DBox = new GameObject("Preview3DBox", typeof(RectTransform), typeof(Image));
        _preview3DBox.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_preview3DBox, height: 172f, flexW: -1f, flexH: 0f);
        _preview3DBox.GetComponent<Image>().color = new Color(0.025f, 0.028f, 0.032f, 0.95f);

        AddOutlineBorder(_preview3DBox.transform, EftUiTemplateCache.ColorCardBorder);

        if (_tpl.CameraImageTemplate != null)
        {
            _cameraImage = Instantiate(_tpl.CameraImageTemplate, _preview3DBox.transform, worldPositionStays: false);
            _cameraImage.name = "Preview3DCameraImage";
            EftUiTemplateCache.Stretch((RectTransform)_cameraImage.transform, 2f, 2f, 2f, 2f);
            var rawImg = _cameraImage.GetComponent<RawImage>();
            if (rawImg != null)
            {
                rawImg.color = Color.white;
                rawImg.raycastTarget = true;
            }

            var relay = _cameraImage.gameObject.AddComponent<PreviewInputRelay>();
            relay.OnDragDelta = delta => _weaponPreview?.Rotate(delta.x, delta.y);
            relay.OnScrollDelta = delta => _weaponPreview?.Zoom(delta * 0.12f);
            _cameraImage.gameObject.SetActive(false);
        }

        TextMeshProUGUI dragHint = _tpl.CreateText(
            _preview3DBox.transform,
            "DragHint",
            "左键拖拽旋转 · 滚轮缩放",
            11f,
            new Color32(140, 146, 150, 180),
            TextAlignmentOptions.BottomRight);
        var hintRt = dragHint.rectTransform;
        hintRt.anchorMin = new Vector2(0f, 0f);
        hintRt.anchorMax = new Vector2(1f, 0f);
        hintRt.pivot = new Vector2(1f, 0f);
        hintRt.offsetMin = new Vector2(8f, 4f);
        hintRt.offsetMax = new Vector2(-8f, 20f);
    }

    private void BuildCleanProductHeader()
    {
        _productMetaPanel = new GameObject("ProductHeader", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _productMetaPanel.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_productMetaPanel, height: 42f, flexW: -1f, flexH: 0f);
        var vlg = _productMetaPanel.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var titleRow = new GameObject("TitleRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        titleRow.transform.SetParent(_productMetaPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(titleRow, height: 22f, flexW: -1f, flexH: 0f);
        var tHlg = titleRow.GetComponent<HorizontalLayoutGroup>();
        tHlg.spacing = 6f;
        tHlg.childControlWidth = true;
        tHlg.childControlHeight = true;
        tHlg.childForceExpandWidth = false;
        tHlg.childForceExpandHeight = true;

        _productTitleText = _tpl.CreateText(titleRow.transform, "Name", "", 16.5f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        _productTitleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        _productWeightText = _tpl.CreateText(titleRow.transform, "Weight", "", 12.5f, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Right);
        EftUiTemplateCache.SetFixedLayout(_productWeightText.gameObject, width: 64f, height: 20f, flexW: 0f, flexH: 0f);

        _productCategoryText = _tpl.CreateText(_productMetaPanel.transform, "Category", "", 12f, EftUiTemplateCache.ColorTextMuted);
        EftUiTemplateCache.SetFixedLayout(_productCategoryText.gameObject, height: 17f, flexW: -1f, flexH: 0f);
    }

    private void Build2DFallbackPreview()
    {
        _fallback2DPanel = new GameObject("Fallback2D", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        _fallback2DPanel.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_fallback2DPanel, height: 92f, flexW: -1f, flexH: 0f);
        _fallback2DPanel.GetComponent<Image>().color = EftUiTemplateCache.ColorSubBarBg;
        AddOutlineBorder(_fallback2DPanel.transform, EftUiTemplateCache.ColorCardBorder);

        var hlg = _fallback2DPanel.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(10, 10, 8, 8);
        hlg.spacing = 12f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        var iconSlot = new GameObject("IconSlot", typeof(RectTransform));
        iconSlot.transform.SetParent(_fallback2DPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(iconSlot, width: 76f, height: 76f, flexW: 0f, flexH: 0f);

        if (_tpl.ResultItemViewFactoryTemplate != null)
        {
            _fallback2DFactory = Instantiate(_tpl.ResultItemViewFactoryTemplate, iconSlot.transform, worldPositionStays: false);
            _fallback2DFactory.name = "FallbackIcon";
            _fallback2DFactory.gameObject.SetActive(true);
            EftUiTemplateCache.Stretch((RectTransform)_fallback2DFactory.transform);
        }

        var textCol = new GameObject("TextCol", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textCol.transform.SetParent(_fallback2DPanel.transform, worldPositionStays: false);
        textCol.GetComponent<LayoutElement>().flexibleWidth = 1f;
        var vlg = textCol.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 4f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        _fallback2DTitle = _tpl.CreateText(textCol.transform, "Title", "", 16f, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        _fallback2DTitle.enableWordWrapping = true;
        EftUiTemplateCache.SetFixedLayout(_fallback2DTitle.gameObject, height: 38f, flexW: -1f, flexH: 0f);
        _fallback2DCategory = _tpl.CreateText(textCol.transform, "Category", "", 12.5f, EftUiTemplateCache.ColorTextSecondary);
        EftUiTemplateCache.SetFixedLayout(_fallback2DCategory.gameObject, height: 18f, flexW: -1f, flexH: 0f);
    }

    private void AddOutlineBorder(Transform parent, Color color)
    {
        Image top = _tpl.CreatePanel(parent, "Top", color);
        top.rectTransform.anchorMin = new Vector2(0f, 1f);
        top.rectTransform.anchorMax = new Vector2(1f, 1f);
        top.rectTransform.pivot = new Vector2(0.5f, 1f);
        top.rectTransform.sizeDelta = new Vector2(0f, 1f);

        Image bot = _tpl.CreatePanel(parent, "Bottom", color);
        bot.rectTransform.anchorMin = new Vector2(0f, 0f);
        bot.rectTransform.anchorMax = new Vector2(1f, 0f);
        bot.rectTransform.pivot = new Vector2(0.5f, 0f);
        bot.rectTransform.sizeDelta = new Vector2(0f, 1f);

        Image left = _tpl.CreatePanel(parent, "Left", color);
        left.rectTransform.anchorMin = new Vector2(0f, 0f);
        left.rectTransform.anchorMax = new Vector2(0f, 1f);
        left.rectTransform.pivot = new Vector2(0f, 0.5f);
        left.rectTransform.sizeDelta = new Vector2(1f, 0f);

        Image right = _tpl.CreatePanel(parent, "Right", color);
        right.rectTransform.anchorMin = new Vector2(1f, 0f);
        right.rectTransform.anchorMax = new Vector2(1f, 1f);
        right.rectTransform.pivot = new Vector2(1f, 0.5f);
        right.rectTransform.sizeDelta = new Vector2(1f, 0f);
    }

    internal void SetEntry(WorkshopCatalogModel.RecipeEntry entry)
    {
        _entry = entry;

        if (entry == null)
        {
            ClosePreview();
            _emptyStatePanel.SetActive(true);
            _bodyPanel.SetActive(false);
            return;
        }

        _emptyStatePanel.SetActive(false);
        _bodyPanel.SetActive(true);

        _stationCrumbText.text = $"{entry.StationLabel} Lv.{entry.RequiredAreaLevel}  ·  {entry.RootCategoryName}";

        UpdatePreview();
        UpdateDynamicDetails();
    }

    internal void RefreshCurrent()
    {
        if (_entry != null)
        {
            UpdatePreviewIfModeChanged();
            UpdateDynamicDetails();
        }
    }

    internal void RefreshTimerOnly()
    {
        if (_entry != null)
        {
            UpdateProgressAndButtons();
        }
    }

    internal void ClosePreview()
    {
        if (_cameraImage != null)
        {
            _cameraImage.InitCamera(null);
            _cameraImage.gameObject.SetActive(false);
        }

        if (_weaponPreview != null)
        {
            _weaponPreview.Hide();
            if (_itemUiContext?.WeaponPreviewPool != null)
            {
                _itemUiContext.WeaponPreviewPool.ReturnToPool(_weaponPreview);
            }
            _weaponPreview = null;
        }

        if (_fallback2DFactory != null)
        {
            _fallback2DFactory.Close();
        }

        for (int i = 0; i < _materialRows.Count; i++)
        {
            _materialRows[i].ItemFactory?.Close();
            _materialRows[i].BoundTemplateId = null;
            _materialRows[i].BoundSchemeId = null;
        }
        _activeMaterialRowCount = 0;

        _boundPreviewSchemeId = null;
    }

    private void UpdatePreviewIfModeChanged()
    {
        bool want3D = HideoutQoLPlugin.WorkshopShow3DPreview == null || HideoutQoLPlugin.WorkshopShow3DPreview.Value;
        if (want3D != _lastShow3D)
        {
            _boundPreviewSchemeId = null;
            UpdatePreview();
        }
    }

    private void UpdatePreview()
    {
        if (_entry == null)
        {
            return;
        }

        bool want3D = HideoutQoLPlugin.WorkshopShow3DPreview == null || HideoutQoLPlugin.WorkshopShow3DPreview.Value;
        if (string.Equals(_boundPreviewSchemeId, _entry.SchemeId, StringComparison.Ordinal) && _lastShow3D == want3D)
        {
            return;
        }

        _boundPreviewSchemeId = _entry.SchemeId;
        _lastShow3D = want3D;

        Item schemeItem = CreateProductItem(_entry);

        bool shown3D = false;
        if (want3D && _cameraImage != null && _itemUiContext?.WeaponPreviewPool != null && schemeItem != null)
        {
            shown3D = TryShow3DModel(schemeItem);
        }

        string catPath = string.IsNullOrEmpty(_entry.SubCategoryName)
            ? _entry.RootCategoryName
            : $"{_entry.RootCategoryName}  >  {_entry.SubCategoryName}";
        string titleStr = _entry.Scheme.count > 1 ? $"{_entry.DisplayName}  (x{_entry.Scheme.count})" : _entry.DisplayName;

        if (shown3D)
        {
            _preview3DBox.SetActive(true);
            _productMetaPanel.SetActive(true);
            _fallback2DPanel.SetActive(false);

            _productTitleText.text = titleStr;
            _productCategoryText.text = catPath;
            _productWeightText.text = schemeItem != null
                ? $"{schemeItem.GetTruncatedWeightString()} kg"
                : string.Empty;
        }
        else
        {
            Release3DPreviewOnly();
            _preview3DBox.SetActive(false);
            _productMetaPanel.SetActive(false);
            _fallback2DPanel.SetActive(true);

            if (_fallback2DFactory != null && schemeItem != null)
            {
                _fallback2DFactory.Close();
                _fallback2DFactory.Show(schemeItem, _inventoryController, _itemUiContext);
                _fallback2DFactory.ShowInfo(false, showFulfilledStatus: false);
                EftUiTemplateCache.NormalizeItemFactoryRect(_fallback2DFactory, 74f, showInfo: false);
            }
            _fallback2DTitle.text = titleStr;
            _fallback2DCategory.text = catPath;
        }
    }

    private bool TryShow3DModel(Item schemeItem)
    {
        try
        {
            if (_weaponPreview == null)
            {
                _weaponPreview = _itemUiContext.WeaponPreviewPool.GetWeaponPreview();
                if (_weaponPreview == null)
                {
                    return false;
                }
                _weaponPreview.Init();
            }

            _cameraImage.gameObject.SetActive(true);
            _cameraImage.InitCamera(_weaponPreview.WeaponPreviewCamera);
            _weaponPreview.SetupItemPreview(schemeItem);
            _weaponPreview.ResetRotator(-1.5f);
            return true;
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log?.LogWarning($"HideoutQoL: 3D preview fallback to 2D: {ex.Message}");
            Release3DPreviewOnly();
            return false;
        }
    }

    private void Release3DPreviewOnly()
    {
        if (_cameraImage != null)
        {
            _cameraImage.InitCamera(null);
            _cameraImage.gameObject.SetActive(false);
        }
        if (_weaponPreview != null)
        {
            _weaponPreview.Hide();
            if (_itemUiContext?.WeaponPreviewPool != null)
            {
                _itemUiContext.WeaponPreviewPool.ReturnToPool(_weaponPreview);
            }
            _weaponPreview = null;
        }
    }

    private static Item CreateProductItem(WorkshopCatalogModel.RecipeEntry entry)
    {
        if (entry == null)
        {
            return null;
        }
        return EftUiTemplateCache.CreateHideoutDisplayItem(
            entry.EndProductId,
            entry.SchemeId,
            EOwnerType.HideoutProduction,
            spawnedInSession: false,
            isEncoded: entry.Scheme.isEncoded);
    }

    private void UpdateDynamicDetails()
    {
        if (_entry == null)
        {
            return;
        }

        bool fav = _entry.Scheme.FavoriteScheme;
        Sprite starSpr = fav
            ? (_tpl.StarSelectedSprite != null ? _tpl.StarSelectedSprite : _tpl.StarDefaultSprite)
            : (_tpl.StarDefaultSprite != null ? _tpl.StarDefaultSprite : _tpl.StarSelectedSprite);
        _favToggleIcon.sprite = starSpr;
        _favToggleIcon.color = fav ? EftUiTemplateCache.ColorAccentReady : EftUiTemplateCache.ColorTextPrimary;
        _favToggleLabel.text = fav ? "已收藏" : "收藏";

        string durText = WorkshopRecipeTile.FormatCompactDuration(_entry.CraftDurationSeconds);
        float bonus = _entry.Producer != null ? _entry.Producer.TimeReduction : 0f;
        string bonusStr = bonus > 0.001f ? $" (-{bonus:0.#%})" : string.Empty;
        _metaStatsText.SetMonospaceText(
            $"产量 x{_entry.Scheme.count}  ·  仓库现有 {_entry.StashOwnedResultCount}  ·  耗时 {durText}{bonusStr}",
            onlyDigits: true);

        BindRequirements();
        UpdateProgressAndButtons();
    }

    private void BindRequirements()
    {
        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        List<Item> allItems = hideout?._allStashItems;
        bool canRagfair = _itemUiContext?.Session?.RagFair != null
            && !_itemUiContext.Session.RagFair.Disabled
            && _itemUiContext.Session.RagFair.Status != RagFair.ERagFairStatus.Banned;

        int reqIdx = 0;
        if (_entry.Scheme.HasRequirements)
        {
            Requirement[] reqs = _entry.Scheme.requirements;
            for (int i = 0; i < reqs.Length; i++)
            {
                if (reqs[i] is not ItemRequirement ir)
                {
                    continue;
                }

                MaterialRow row = EnsureMaterialRow(reqIdx++);
                row.Root.SetActive(true);
                row.BoundReq = ir;
                row.BoundItemName = ir.Item != null
                    ? ir.Item.LocalizedName()
                    : ((MongoID)ir.TemplateId).LocalizedName();

                int owned = WorkshopCatalogModel.CountMatchingRequirementItems(allItems, ir);
                int need = Math.Max(1, ir.IntCount);
                row.MissingCount = Math.Max(0, need - owned);
                bool fulfilled = owned >= need;
                bool isTool = ir is ToolRequirement;

                Color borderColor = fulfilled
                    ? (Color)EftUiTemplateCache.ColorAccentCraftable
                    : (isTool ? (Color)EftUiTemplateCache.ColorAccentTool : (Color)EftUiTemplateCache.ColorAccentMissing);

                if (row.StatusStripe != null)
                {
                    row.StatusStripe.color = borderColor;
                }

                if (row.ItemFactory != null)
                {
                    bool sameVisualItem = string.Equals(row.BoundTemplateId, ir.TemplateId, StringComparison.Ordinal)
                        && string.Equals(row.BoundSchemeId, _entry.SchemeId, StringComparison.Ordinal)
                        && row.BoundIsFir == ir.IsSpawnedInSession
                        && row.BoundIsEncoded == ir.IsEncoded
                        && row.ItemFactory.ItemView != null;

                    if (!sameVisualItem)
                    {
                        row.ItemFactory.Close();
                        Item reqItem = EftUiTemplateCache.CreateHideoutDisplayItem(
                            (MongoID)ir.TemplateId,
                            _entry.SchemeId,
                            _entry.Scheme.SchemeOwnerType,
                            ir.IsSpawnedInSession,
                            ir.IsEncoded);

                        if (reqItem != null)
                        {
                            row.ItemFactory.Show(reqItem, _inventoryController, _itemUiContext);
                            row.ItemFactory.ShowInfo(false, showFulfilledStatus: false);
                            row.ItemFactory.SetError(null);
                            EftUiTemplateCache.NormalizeItemFactoryRect(row.ItemFactory, 50f, showInfo: false);
                            row.BoundTemplateId = ir.TemplateId;
                            row.BoundSchemeId = _entry.SchemeId;
                            row.BoundIsFir = ir.IsSpawnedInSession;
                            row.BoundIsEncoded = ir.IsEncoded;
                        }
                        else
                        {
                            row.BoundTemplateId = null;
                        }
                    }

                    if (row.ItemFactory.ItemView != null)
                    {
                        row.ItemFactory.SetBorderColor(borderColor);
                    }
                }

                row.NameText.text = row.BoundItemName;
                row.NameText.color = fulfilled
                    ? EftUiTemplateCache.ColorTextPrimary
                    : EftUiTemplateCache.ColorAccentMissing;

                string tags = isTool ? " [工具]" : string.Empty;
                if (ir.IsSpawnedInSession)
                {
                    tags += " [战局发现]";
                }
                row.DetailText.SetMonospaceText($"库存 {owned} / 需要 {need}{tags}", onlyDigits: true);
                row.DetailText.color = isTool && !fulfilled
                    ? EftUiTemplateCache.ColorAccentTool
                    : (fulfilled ? EftUiTemplateCache.ColorAccentCraftable : EftUiTemplateCache.ColorAccentMissing);

                if (row.BuySingleBtn != null)
                {
                    bool showBuy = !fulfilled && !ir.IsSpawnedInSession;
                    row.BuySingleBtn.gameObject.SetActive(showBuy);
                    row.BuySingleBtn.Interactable = showBuy && canRagfair;
                }
            }
        }

        _activeMaterialRowCount = reqIdx;
        for (int i = reqIdx; i < _materialRows.Count; i++)
        {
            _materialRows[i].ItemFactory?.Close();
            _materialRows[i].BoundTemplateId = null;
            _materialRows[i].BoundSchemeId = null;
            _materialRows[i].Root.SetActive(false);
        }

        if (_entry.MissingItemTypes > 0)
        {
            _requirementsHeader.text = $"所需材料与工具 ({_entry.MissingItemTypes} 项不足)";
        }
        else
        {
            _requirementsHeader.text = $"所需材料与工具 ({reqIdx} 项已齐备)";
        }
    }

    private MaterialRow EnsureMaterialRow(int index)
    {
        while (_materialRows.Count <= index)
        {
            _materialRows.Add(CreateMaterialRow(_reqContent));
        }
        return _materialRows[index];
    }

    private MaterialRow CreateMaterialRow(Transform parent)
    {
        var rowGo = new GameObject("MaterialRow", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        rowGo.transform.SetParent(parent, worldPositionStays: false);
        rowGo.GetComponent<Image>().color = EftUiTemplateCache.ColorRowIdle;
        EftUiTemplateCache.SetFixedLayout(rowGo, height: 58f, flexW: -1f, flexH: 0f);

        var hlg = rowGo.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(0, 6, 4, 4);
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        Image stripe = _tpl.CreatePanel(rowGo.transform, "Stripe", EftUiTemplateCache.ColorAccentMissing);
        EftUiTemplateCache.SetFixedLayout(stripe.gameObject, width: 3f, height: 50f, flexW: 0f, flexH: 0f);

        var iconSlot = new GameObject("ReqIcon", typeof(RectTransform));
        iconSlot.transform.SetParent(rowGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(iconSlot, width: 50f, height: 50f, flexW: 0f, flexH: 0f);

        HideoutItemViewFactory itemFactory = null;
        if (_tpl.ResultItemViewFactoryTemplate != null)
        {
            itemFactory = Instantiate(_tpl.ResultItemViewFactoryTemplate, iconSlot.transform, worldPositionStays: false);
            itemFactory.name = "ReqItemFactory";
            itemFactory.gameObject.SetActive(true);
            var fRt = (RectTransform)itemFactory.transform;
            fRt.anchorMin = new Vector2(0.5f, 0.5f);
            fRt.anchorMax = new Vector2(0.5f, 0.5f);
            fRt.pivot = new Vector2(0.5f, 0.5f);
            fRt.anchoredPosition = Vector2.zero;
            fRt.sizeDelta = new Vector2(50f, 50f);
        }

        var textCol = new GameObject("InfoCol", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textCol.transform.SetParent(rowGo.transform, worldPositionStays: false);
        textCol.GetComponent<LayoutElement>().flexibleWidth = 1f;
        var vlg = textCol.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 2f;
        vlg.childAlignment = TextAnchor.MiddleLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        TextMeshProUGUI nameText = _tpl.CreateText(textCol.transform, "Name", "", 13.5f, EftUiTemplateCache.ColorTextPrimary);
        EftUiTemplateCache.SetFixedLayout(nameText.gameObject, height: 20f, flexW: -1f, flexH: 0f);
        TextMeshProUGUI detailText = _tpl.CreateText(textCol.transform, "Count", "", 12f, EftUiTemplateCache.ColorTextSecondary);
        EftUiTemplateCache.SetFixedLayout(detailText.gameObject, height: 18f, flexW: -1f, flexH: 0f);

        var row = new MaterialRow
        {
            Root = rowGo,
            StatusStripe = stripe,
            ItemFactory = itemFactory,
            NameText = nameText,
            DetailText = detailText,
        };

        var btnCol = new GameObject("BtnCol", typeof(RectTransform), typeof(VerticalLayoutGroup));
        btnCol.transform.SetParent(rowGo.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(btnCol, width: 64f, height: 50f, flexW: 0f, flexH: 0f);
        var btnVlg = btnCol.GetComponent<VerticalLayoutGroup>();
        btnVlg.spacing = 3f;
        btnVlg.childAlignment = TextAnchor.MiddleCenter;
        btnVlg.childControlWidth = true;
        btnVlg.childControlHeight = true;
        btnVlg.childForceExpandWidth = true;
        btnVlg.childForceExpandHeight = false;

        row.ReverseSearchBtn = _tpl.CloneActionButton(btnCol.transform, "ReverseBtn", "反查", 62f, 23f, 11, () =>
        {
            if (!string.IsNullOrEmpty(row.BoundItemName))
            {
                _onReverseSearchIngredient?.Invoke(row.BoundItemName);
            }
        });
        row.BuySingleBtn = _tpl.CloneActionButton(btnCol.transform, "BuyOneBtn", "跳蚤", 62f, 23f, 11, () =>
        {
            if (row.BoundReq != null && row.MissingCount > 0)
            {
                BuySingleOnRagfair(row.BoundReq.TemplateId, row.MissingCount);
            }
        });

        return row;
    }

    private void UpdateProgressAndButtons()
    {
        if (_entry == null)
        {
            return;
        }

        _fuelWarningBar.SetActive(!_entry.HasEnergyForCraft && (_entry.State == WorkshopRecipeState.Craftable || _entry.State == WorkshopRecipeState.MissingMaterials || _entry.State == WorkshopRecipeState.Error));

        bool showProgress = _entry.State == WorkshopRecipeState.Producing || _entry.State == WorkshopRecipeState.Ready;
        _progressPanel.SetActive(showProgress);
        if (showProgress)
        {
            float pct01 = _entry.State == WorkshopRecipeState.Ready ? 1f : Mathf.Clamp01(_entry.Progress);
            _progressBarFill.rectTransform.anchorMax = new Vector2(pct01, 1f);
            _progressText.SetMonospaceText(
                _entry.State == WorkshopRecipeState.Ready
                    ? "✔ 制造已完成，等待领取"
                    : $"⚙ 制造中 {Mathf.FloorToInt(pct01 * 100f)}%  ·  剩余 {WorkshopRecipeTile.FormatCompactDuration(_entry.RemainingSeconds)}",
                onlyDigits: true);
        }

        bool isReady = _entry.State == WorkshopRecipeState.Ready;
        bool isProducing = _entry.State == WorkshopRecipeState.Producing;
        bool isError = _entry.State == WorkshopRecipeState.Error;
        bool isIdle = !isReady && !isProducing && !isError;

        bool canRagfair = _itemUiContext?.Session?.RagFair != null
            && !_itemUiContext.Session.RagFair.Disabled
            && _itemUiContext.Session.RagFair.Status != RagFair.ERagFairStatus.Banned;
        bool hasBuyableMissing = HasBuyableMissingItems();

        if (_startBtn != null)
        {
            _startBtn.gameObject.SetActive(isIdle);
            _startBtn.Interactable = _entry.CanStartNow && !_opBusy;
        }
        if (_buyAllBtn != null)
        {
            _buyAllBtn.gameObject.SetActive(isIdle && hasBuyableMissing);
            _buyAllBtn.Interactable = canRagfair && !_opBusy;
        }
        if (_collectBtn != null)
        {
            _collectBtn.gameObject.SetActive(isReady);
            _collectBtn.Interactable = !_opBusy;
        }
        if (_collectRepeatBtn != null)
        {
            bool canPossiblyRepeat = !_entry.Scheme.locked && _entry.MissingItemTypes == 0 && !_entry.IsToolMissing && _entry.HasEnergyForCraft;
            _collectRepeatBtn.gameObject.SetActive(isReady);
            _collectRepeatBtn.Interactable = !_opBusy && canPossiblyRepeat;
        }
        if (_restartBtn != null)
        {
            _restartBtn.gameObject.SetActive(isError);
            _restartBtn.Interactable = !_opBusy && _entry.HasEnergyForCraft;
        }
        if (_cancelBtn != null)
        {
            _cancelBtn.gameObject.SetActive(isProducing || isError);
            _cancelBtn.Interactable = !_opBusy;
        }

        if (isIdle && !_entry.CanStartNow)
        {
            if (_entry.Scheme.locked)
            {
                _hintText.text = $"需要设施等级 {_entry.RequiredAreaLevel} 或前置解锁条件";
            }
            else if (_entry.Producer != null && !_entry.Producer.CanStartByProductionCount())
            {
                _hintText.text = "当前设施生产槽位已占满，请先领取或等待完成";
            }
            else if (_entry.MissingItemTypes > 0)
            {
                _hintText.text = $"仍缺少 {_entry.MissingItemTypes} 种材料/工具";
            }
            else if (!_entry.HasEnergyForCraft)
            {
                _hintText.text = "请先在顶部或俯视界面开启发电机";
            }
            else
            {
                _hintText.text = "当前无法开始此配方";
            }
            _hintText.gameObject.SetActive(true);
        }
        else
        {
            _hintText.gameObject.SetActive(false);
        }
    }

    private bool HasBuyableMissingItems()
    {
        for (int i = 0; i < _activeMaterialRowCount; i++)
        {
            MaterialRow row = _materialRows[i];
            if (row.MissingCount > 0 && row.BoundReq != null && !row.BoundReq.IsSpawnedInSession)
            {
                return true;
            }
        }
        return false;
    }

    private void HandleBuyAllMissing()
    {
        if (_entry?.Scheme?.HasRequirements != true || _itemUiContext == null)
        {
            return;
        }
        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        List<Item> allItems = hideout?._allStashItems;

        var buildItems = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ItemRequirement ir in _entry.Scheme.RequiredItems)
        {
            if (ir.IsSpawnedInSession)
            {
                continue;
            }
            int owned = WorkshopCatalogModel.CountMatchingRequirementItems(allItems, ir);
            int missing = ir.IntCount - owned;
            if (missing > 0)
            {
                if (buildItems.TryGetValue(ir.TemplateId, out int existing))
                {
                    buildItems[ir.TemplateId] = existing + missing;
                }
                else
                {
                    buildItems[ir.TemplateId] = missing;
                }
            }
        }

        if (buildItems.Count == 0)
        {
            return;
        }

        _itemUiContext.ExternalRagfairSearch(new RagfairSearch(EFilterType.BuildItems, new BuildItemSearchValue
        {
            BuildName = string.Empty,
            BuildItems = buildItems,
            BuildCount = 1,
        }));
    }

    private void BuySingleOnRagfair(string templateId, int count)
    {
        if (string.IsNullOrEmpty(templateId) || count <= 0 || _itemUiContext == null)
        {
            return;
        }

        _itemUiContext.ExternalRagfairSearch(new RagfairSearch(EFilterType.BuildItems, new BuildItemSearchValue
        {
            BuildName = string.Empty,
            BuildItems = new Dictionary<string, int>(1) { { templateId, count } },
            BuildCount = 1,
        }));
    }

    internal void ExecuteQuickPrimaryAction(WorkshopCatalogModel.RecipeEntry target)
    {
        if (target == null || _opBusy)
        {
            return;
        }
        if (target != _entry)
        {
            SetEntry(target);
        }
        if (target.State == WorkshopRecipeState.Ready)
        {
            RunSafe(() => ExecuteCollectAsync(repeatAfter: false));
        }
        else if (target.State == WorkshopRecipeState.Craftable && target.CanStartNow)
        {
            RunSafe(ExecuteStartAsync);
        }
    }

    private async Task ExecuteStartAsync()
    {
        if (_entry == null || !_entry.CanStartNow || _opBusy || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }
        WorkshopCatalogModel.RecipeEntry target = _entry;
        _opBusy = true;
        UpdateProgressAndButtons();
        try
        {
            await Singleton<HideoutRepresentation>.Instance.StartSingleProduction(target.Scheme, () =>
            {
                target.Producer?.StartProducing(target.Scheme);
            });
        }
        finally
        {
            _opBusy = false;
            _onCraftMutated?.Invoke();
        }
    }

    private async Task ExecuteCollectAsync(bool repeatAfter)
    {
        if (_entry == null || _entry.Producer == null || _opBusy || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }
        WorkshopCatalogModel.RecipeEntry target = _entry;
        _opBusy = true;
        UpdateProgressAndButtons();
        try
        {
            HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
            bool ok = await hideout.GetProducedItems(target.Producer, target.SchemeId, false);
            if (ok)
            {
                target.Producer.GetItems(target.SchemeId);
                if (repeatAfter)
                {
                    _model.RefreshStatesOnly();
                    if (target.CanStartNow)
                    {
                        await hideout.StartSingleProduction(target.Scheme, () =>
                        {
                            target.Producer.StartProducing(target.Scheme);
                        });
                    }
                }
            }
        }
        finally
        {
            _opBusy = false;
            _onCraftMutated?.Invoke();
        }
    }

    private async Task ExecuteRestartAsync()
    {
        if (_entry == null || _opBusy || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }
        WorkshopCatalogModel.RecipeEntry target = _entry;
        _opBusy = true;
        UpdateProgressAndButtons();
        try
        {
            await Singleton<HideoutRepresentation>.Instance.RestartProduction(target.Scheme);
        }
        finally
        {
            _opBusy = false;
            _onCraftMutated?.Invoke();
        }
    }

    private async Task ExecuteCancelAsync()
    {
        if (_entry == null || _opBusy || !Singleton<HideoutRepresentation>.Instantiated)
        {
            return;
        }
        WorkshopCatalogModel.RecipeEntry target = _entry;
        _opBusy = true;
        UpdateProgressAndButtons();
        try
        {
            await Singleton<HideoutRepresentation>.Instance.CancelProduction(target.Scheme);
        }
        finally
        {
            _opBusy = false;
            _onCraftMutated?.Invoke();
        }
    }

    private static async void RunSafe(Func<Task> taskFactory)
    {
        try
        {
            await taskFactory();
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log?.LogError($"HideoutQoL: Workshop inspector task error: {ex}");
        }
    }
}
