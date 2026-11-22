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

    // The preview camera renders deferred into a 2x RenderTexture. Keep it on only while the image can change
    // (model load, drag/zoom, texture streaming settle) and leave the last frame on the RawImage otherwise.
    private const float PreviewSettleSeconds = 1.5f;
    private const float PreviewInteractSeconds = 0.5f;
    private float _previewRenderUntil = -1f;
    private RenderTexture _lastPreviewTexture;
    private Action _onPreviewModelLoaded;

    // WeaponPreview.SetupItemPreview overwrites this unsubscribe action on every call; only Hide() invokes it.
    private static readonly HarmonyLib.AccessTools.FieldRef<WeaponPreview, Action> ResolutionBindingRef =
        CreateResolutionBindingRef();

    private UiFader _bodyFader;
    private UiFader _previewFader;
    private bool _previewRevealPending;
    private float _previewRevealDeadline;
    private const float PreviewRevealTimeout = 1.0f;

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
            EftUiTemplateCache.FontName,
            EftUiTemplateCache.ColorTextLabel,
            TextAlignmentOptions.Center);

        _bodyPanel = new GameObject("InspectorBody", typeof(RectTransform), typeof(VerticalLayoutGroup));
        _bodyPanel.transform.SetParent(transform, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)_bodyPanel.transform, 12f, 12f, 8f, 12f);
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

        // Grey column caption, like the handbook's "Description" label
        _stationCrumbText = _tpl.CreateText(crumbRow.transform, "StationCrumb", "", EftUiTemplateCache.FontBody, EftUiTemplateCache.ColorTextLabel);
        var crumbLe = _stationCrumbText.gameObject.AddComponent<LayoutElement>();
        crumbLe.flexibleWidth = 1f;
        crumbLe.preferredHeight = 24f;

        var favGo = new GameObject("FavoriteToggle", typeof(RectTransform), typeof(Image), typeof(Button), typeof(HorizontalLayoutGroup));
        favGo.transform.SetParent(crumbRow.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(favGo, width: 92f, height: 26f, flexW: 0f, flexH: 0f);
        favGo.GetComponent<Image>().color = EftUiTemplateCache.ColorRowIdle;
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
        EftUiTemplateCache.SetFixedLayout(starGo, width: 18f, height: 18f, flexW: 0f, flexH: 0f);
        _favToggleIcon = starGo.GetComponent<Image>();
        _favToggleIcon.preserveAspect = true;
        _favToggleIcon.raycastTarget = false;
        _favToggleLabel = _tpl.CreateText(favGo.transform, "Label", "收藏", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Center);

        Build3DPreviewBox();
        Build2DFallbackPreview();
        BuildCleanProductHeader();

        // Compact craft stats bar
        _metaStatsText = _tpl.CreateText(_bodyPanel.transform, "MetaStats", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextBright);
        EftUiTemplateCache.SetFixedLayout(_metaStatsText.gameObject, height: 22f, flexW: -1f, flexH: 0f);

        // Requirements header
        var reqHeaderRow = new GameObject("ReqHeader", typeof(RectTransform));
        reqHeaderRow.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(reqHeaderRow, height: 26f, flexW: -1f, flexH: 0f);
        _requirementsHeader = _tpl.CreateText(reqHeaderRow.transform, "Title", "所需材料与工具", EftUiTemplateCache.FontBody, EftUiTemplateCache.ColorTextLabel);
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
        EftUiTemplateCache.SetFixedLayout(_fuelWarningBar, height: 26f, flexW: -1f, flexH: 0f);
        _fuelWarningBar.GetComponent<Image>().color = EftUiTemplateCache.ColorWarnBg;
        TextMeshProUGUI fuelWarnTxt = _tpl.CreateText(_fuelWarningBar.transform, "Text", "需要开启发电机以开始或持续制作", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorWarnText, TextAlignmentOptions.Center);
        EftUiTemplateCache.Stretch(fuelWarnTxt.rectTransform, 6f, 6f, 0f, 0f);
        _fuelWarningBar.SetActive(false);

        // Progress bar (visible when Producing / Ready)
        _progressPanel = new GameObject("ProgressPanel", typeof(RectTransform), typeof(Image));
        _progressPanel.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        // Vanilla percentage track (black 42%) with the ProductionStatus text style (bold 16, #CAD6DC)
        EftUiTemplateCache.SetFixedLayout(_progressPanel, height: 30f, flexW: -1f, flexH: 0f);
        _progressPanel.GetComponent<Image>().color = EftUiTemplateCache.ColorTrackBg;
        Color fillColor = EftUiTemplateCache.ColorAccentProducing;
        fillColor.a = 0.45f;
        _progressBarFill = _tpl.CreatePanel(_progressPanel.transform, "Fill", fillColor);
        _progressBarFill.rectTransform.anchorMin = Vector2.zero;
        _progressBarFill.rectTransform.anchorMax = new Vector2(0f, 1f);
        _progressBarFill.rectTransform.pivot = new Vector2(0f, 0.5f);
        _progressBarFill.rectTransform.offsetMin = Vector2.zero;
        _progressBarFill.rectTransform.offsetMax = Vector2.zero;
        _progressText = _tpl.CreateText(_progressPanel.transform, "Label", "", EftUiTemplateCache.FontBody, EftUiTemplateCache.ColorTextBright, TextAlignmentOptions.Center, FontStyles.Bold);
        EftUiTemplateCache.Stretch(_progressText.rectTransform, 4f, 4f, 0f, 0f);
        _progressPanel.SetActive(false);

        // Status / lock hint
        _hintText = _tpl.CreateText(_bodyPanel.transform, "ActionHint", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Center);
        EftUiTemplateCache.SetFixedLayout(_hintText.gameObject, height: 20f, flexW: -1f, flexH: 0f);

        // Action dock: one wide primary action per state, secondary actions at a fixed narrower width.
        var dockGo = new GameObject("ActionDock", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        dockGo.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(dockGo, height: 42f, flexW: -1f, flexH: 0f);
        var dockHlg = dockGo.GetComponent<HorizontalLayoutGroup>();
        dockHlg.spacing = 8f;
        dockHlg.childAlignment = TextAnchor.MiddleRight;
        dockHlg.childControlWidth = true;
        dockHlg.childControlHeight = true;
        dockHlg.childForceExpandWidth = false;
        dockHlg.childForceExpandHeight = false;

        _startBtn = CreateDockButton(dockGo.transform, "BtnStart", "开始制造", primary: true, () => RunSafe(ExecuteStartAsync));
        _buyAllBtn = CreateDockButton(dockGo.transform, "BtnBuyAll", "购齐缺料", primary: false, HandleBuyAllMissing);
        _collectBtn = CreateDockButton(dockGo.transform, "BtnCollect", "领取成品", primary: true, () => RunSafe(() => ExecuteCollectAsync(repeatAfter: false)));
        _collectRepeatBtn = CreateDockButton(dockGo.transform, "BtnCollectRepeat", "领完再造", primary: false, () => RunSafe(() => ExecuteCollectAsync(repeatAfter: true)));
        _restartBtn = CreateDockButton(dockGo.transform, "BtnRestart", "重新开始", primary: true, () => RunSafe(ExecuteRestartAsync));
        _cancelBtn = CreateDockButton(dockGo.transform, "BtnCancel", "取消制造", primary: false, () => RunSafe(ExecuteCancelAsync));
    }

    private DefaultUIButton CreateDockButton(Transform parent, string name, string text, bool primary, UnityEngine.Events.UnityAction onClick)
    {
        const float SecondaryWidth = 132f;
        const float Height = 40f;

        DefaultUIButton btn = _tpl.CloneActionButton(parent, name, text, primary ? 0f : SecondaryWidth, Height, primary ? 20 : 16, onClick);
        if (btn != null && primary)
        {
            LayoutElement le = btn.gameObject.GetOrAddComponent<LayoutElement>();
            le.minWidth = SecondaryWidth;
            le.flexibleWidth = 1f;
        }
        return btn;
    }

    private void Build3DPreviewBox()
    {
        _preview3DBox = new GameObject("Preview3DBox", typeof(RectTransform), typeof(Image));
        _preview3DBox.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        // Vanilla item preview is 800x228 with the info_window_back hatch pattern and an amber corner glow.
        EftUiTemplateCache.SetFixedLayout(_preview3DBox, height: 204f, flexW: -1f, flexH: 0f);
        _preview3DBox.GetComponent<Image>().color = EftUiTemplateCache.ColorPreviewBg;

        if (_tpl.PreviewBackSprite != null)
        {
            Image pattern = _tpl.CreatePanel(_preview3DBox.transform, "Pattern", Color.white);
            pattern.sprite = _tpl.PreviewBackSprite;
            EftUiTemplateCache.Stretch(pattern.rectTransform);
        }

        if (_tpl.CreateFrame(_preview3DBox.transform, "Frame", _tpl.BorderGenericSprite, EftUiTemplateCache.ColorCardBorder) == null)
        {
            AddOutlineBorder(_preview3DBox.transform, EftUiTemplateCache.ColorCardBorder);
        }

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
            relay.OnDragDelta = delta =>
            {
                _weaponPreview?.Rotate(delta.x, delta.y);
                KeepPreviewRendering(PreviewInteractSeconds);
            };
            relay.OnScrollDelta = delta =>
            {
                _weaponPreview?.Zoom(delta * 0.12f);
                KeepPreviewRendering(PreviewInteractSeconds);
            };
            _cameraImage.gameObject.SetActive(false);
        }

        if (_tpl.PreviewCornerSprite != null)
        {
            Image corner = _tpl.CreatePanel(_preview3DBox.transform, "CornerGlow", Color.white);
            corner.sprite = _tpl.PreviewCornerSprite;
            RectTransform cornerRt = corner.rectTransform;
            cornerRt.anchorMin = Vector2.zero;
            cornerRt.anchorMax = Vector2.zero;
            cornerRt.pivot = Vector2.zero;
            cornerRt.anchoredPosition = new Vector2(2f, 2f);
            cornerRt.sizeDelta = new Vector2(124f, 122f);
        }

        TextMeshProUGUI dragHint = _tpl.CreateText(
            _preview3DBox.transform,
            "DragHint",
            "左键拖拽旋转 · 滚轮缩放",
            13f,
            EftUiTemplateCache.ColorTextLabel,
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
        // Mirrors the handbook description caption: name on a 7% white strip (18), then item type left and
        // weight right underneath (14, #A6B0B5).
        EftUiTemplateCache.SetFixedLayout(_productMetaPanel, height: 60f, flexW: -1f, flexH: 0f);
        var vlg = _productMetaPanel.GetComponent<VerticalLayoutGroup>();
        vlg.spacing = 3f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;

        var titleRow = new GameObject("TitleRow", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        titleRow.transform.SetParent(_productMetaPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(titleRow, height: 36f, flexW: -1f, flexH: 0f);
        Image titleBg = titleRow.GetComponent<Image>();
        titleBg.color = EftUiTemplateCache.ColorCaptionPanel;
        titleBg.raycastTarget = false;
        var tHlg = titleRow.GetComponent<HorizontalLayoutGroup>();
        tHlg.padding = new RectOffset(10, 10, 0, 0);
        tHlg.childAlignment = TextAnchor.MiddleLeft;
        tHlg.childControlWidth = true;
        tHlg.childControlHeight = true;
        tHlg.childForceExpandWidth = false;
        tHlg.childForceExpandHeight = true;

        _productTitleText = _tpl.CreateText(titleRow.transform, "Name", "", EftUiTemplateCache.FontName, EftUiTemplateCache.ColorTextPrimary);
        _productTitleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        var subRow = new GameObject("SubRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        subRow.transform.SetParent(_productMetaPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(subRow, height: 20f, flexW: -1f, flexH: 0f);
        var sHlg = subRow.GetComponent<HorizontalLayoutGroup>();
        sHlg.padding = new RectOffset(10, 10, 0, 0);
        sHlg.spacing = 8f;
        sHlg.childAlignment = TextAnchor.MiddleLeft;
        sHlg.childControlWidth = true;
        sHlg.childControlHeight = true;
        sHlg.childForceExpandWidth = false;
        sHlg.childForceExpandHeight = true;

        _productCategoryText = _tpl.CreateText(subRow.transform, "Category", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary);
        _productCategoryText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        _productWeightText = _tpl.CreateText(subRow.transform, "Weight", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Right);
        EftUiTemplateCache.SetFixedLayout(_productWeightText.gameObject, width: 80f, height: 20f, flexW: 0f, flexH: 0f);
    }

    private void Build2DFallbackPreview()
    {
        _fallback2DPanel = new GameObject("Fallback2D", typeof(RectTransform), typeof(Image), typeof(HorizontalLayoutGroup));
        _fallback2DPanel.transform.SetParent(_bodyPanel.transform, worldPositionStays: false);
        EftUiTemplateCache.SetFixedLayout(_fallback2DPanel, height: 92f, flexW: -1f, flexH: 0f);
        _fallback2DPanel.GetComponent<Image>().color = EftUiTemplateCache.ColorCaptionPanel;
        if (_tpl.CreateFrame(_fallback2DPanel.transform, "Frame", _tpl.BorderGenericSprite, EftUiTemplateCache.ColorCardBorder) == null)
        {
            AddOutlineBorder(_fallback2DPanel.transform, EftUiTemplateCache.ColorCardBorder);
        }

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

        _fallback2DTitle = _tpl.CreateText(textCol.transform, "Title", "", EftUiTemplateCache.FontName, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        _fallback2DTitle.enableWordWrapping = true;
        EftUiTemplateCache.SetFixedLayout(_fallback2DTitle.gameObject, height: 44f, flexW: -1f, flexH: 0f);
        _fallback2DCategory = _tpl.CreateText(textCol.transform, "Category", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary);
        EftUiTemplateCache.SetFixedLayout(_fallback2DCategory.gameObject, height: 20f, flexW: -1f, flexH: 0f);
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
        // SetEntry also runs on every catalog refresh; only a different recipe gets a transition.
        bool recipeChanged = entry != null
            && !string.Equals(_entry?.SchemeId, entry.SchemeId, StringComparison.Ordinal);
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

        if (recipeChanged)
        {
            _bodyFader ??= UiFader.For(_bodyPanel);
            _bodyFader.Play(0f, 1f, WorkshopFx.ContentFade);
        }
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
        ResetPreviewRenderState();

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

            Camera previewCamera = _weaponPreview.WeaponPreviewCamera;
            if (!_cameraImage.gameObject.activeSelf)
            {
                _cameraImage.gameObject.SetActive(true);
            }

            // InitCamera always releases and reallocates the 2x RenderTexture; only rebind when the camera changed
            // or CameraImage dropped its texture (e.g. after OnDisable).
            if (previewCamera == null
                || !ReferenceEquals(_cameraImage.targetCamera, previewCamera)
                || previewCamera.targetTexture == null)
            {
                _cameraImage.InitCamera(previewCamera);
            }

            // Hide the image until the new model is in: otherwise the previous item lingers and the new one
            // pops in whenever its bundles finish. OnPreviewModelLoaded (or the timeout) fades it back.
            // Armed before SetupItemPreview because the callback fires synchronously for cached bundles.
            _previewFader ??= UiFader.For(_cameraImage.gameObject);
            _previewFader.Snap(0f);
            _previewRevealPending = true;
            _previewRevealDeadline = Time.unscaledTime + PreviewRevealTimeout;

            ReleaseStaleResolutionBinding(_weaponPreview);
            _onPreviewModelLoaded ??= OnPreviewModelLoaded;
            _weaponPreview.SetupItemPreview(schemeItem, onLoadingFinished: _onPreviewModelLoaded);
            _weaponPreview.ResetRotator(-1.5f);
            KeepPreviewRendering(PreviewSettleSeconds);
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
        ResetPreviewRenderState();
    }

    private void OnPreviewModelLoaded()
    {
        // The new model is instantiated right after this callback; give texture streaming time to settle.
        KeepPreviewRendering(PreviewSettleSeconds);
        RevealPreview();
    }

    private void RevealPreview()
    {
        if (!_previewRevealPending)
        {
            return;
        }
        _previewRevealPending = false;
        _previewFader?.Play(0f, 1f, WorkshopFx.PreviewFade);
    }

    private void KeepPreviewRendering(float seconds)
    {
        float until = Time.unscaledTime + seconds;
        if (until > _previewRenderUntil)
        {
            _previewRenderUntil = until;
        }
    }

    private void ResetPreviewRenderState()
    {
        _previewRenderUntil = -1f;
        _lastPreviewTexture = null;
        _previewRevealPending = false;
        _previewFader?.Snap(1f);
    }

    private void LateUpdate()
    {
        if (_cameraImage == null || _weaponPreview == null)
        {
            return;
        }

        // Never leave the preview hidden if the load callback does not arrive.
        if (_previewRevealPending && Time.unscaledTime >= _previewRevealDeadline)
        {
            RevealPreview();
        }

        Camera cam = _cameraImage.targetCamera;
        if (cam == null)
        {
            return;
        }

        RenderTexture rt = cam.targetTexture;
        if (rt == null)
        {
            // CameraImage is disabled or cleaned up and already turned the camera off.
            _lastPreviewTexture = null;
            return;
        }

        // CameraImage.RecreateTexture (resize / re-enable) hands us a blank texture, and GPU resets can drop
        // the contents of the existing one: render again in both cases.
        if (!ReferenceEquals(rt, _lastPreviewTexture) || !rt.IsCreated())
        {
            _lastPreviewTexture = rt;
            KeepPreviewRendering(PreviewSettleSeconds);
        }

        bool shouldRender = Time.unscaledTime < _previewRenderUntil;
        if (cam.enabled != shouldRender)
        {
            cam.enabled = shouldRender;
        }
    }

    private static HarmonyLib.AccessTools.FieldRef<WeaponPreview, Action> CreateResolutionBindingRef()
    {
        try
        {
            return HarmonyLib.AccessTools.FieldRefAccess<WeaponPreview, Action>("_resolutionBinding");
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log?.LogWarning($"HideoutQoL: WeaponPreview._resolutionBinding not found, display-settings subscriptions will not be released: {ex.Message}");
            return null;
        }
    }

    private static void ReleaseStaleResolutionBinding(WeaponPreview preview)
    {
        if (ResolutionBindingRef == null || preview == null)
        {
            return;
        }

        ref Action binding = ref ResolutionBindingRef(preview);
        Action unbind = binding;
        binding = null;
        unbind?.Invoke();
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
                // Vanilla requirement counter format ("700/2500")
                row.DetailText.SetMonospaceText($"{owned}/{need}{tags}", onlyDigits: true);
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
        EftUiTemplateCache.SetFixedLayout(rowGo, height: 60f, flexW: -1f, flexH: 0f);

        var hlg = rowGo.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(0, 6, 4, 4);
        hlg.spacing = 8f;
        hlg.childAlignment = TextAnchor.MiddleLeft;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;

        Image stripe = _tpl.CreatePanel(rowGo.transform, "Stripe", EftUiTemplateCache.ColorAccentMissing);
        EftUiTemplateCache.SetFixedLayout(stripe.gameObject, width: 3f, height: 52f, flexW: 0f, flexH: 0f);

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

        TextMeshProUGUI nameText = _tpl.CreateText(textCol.transform, "Name", "", EftUiTemplateCache.FontBody, EftUiTemplateCache.ColorTextPrimary);
        EftUiTemplateCache.SetFixedLayout(nameText.gameObject, height: 22f, flexW: -1f, flexH: 0f);
        TextMeshProUGUI detailText = _tpl.CreateText(textCol.transform, "Count", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary);
        EftUiTemplateCache.SetFixedLayout(detailText.gameObject, height: 20f, flexW: -1f, flexH: 0f);

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
        EftUiTemplateCache.SetFixedLayout(btnCol, width: 72f, height: 52f, flexW: 0f, flexH: 0f);
        var btnVlg = btnCol.GetComponent<VerticalLayoutGroup>();
        btnVlg.spacing = 2f;
        btnVlg.childAlignment = TextAnchor.MiddleCenter;
        btnVlg.childControlWidth = true;
        btnVlg.childControlHeight = true;
        btnVlg.childForceExpandWidth = true;
        btnVlg.childForceExpandHeight = false;

        row.ReverseSearchBtn = _tpl.CloneActionButton(btnCol.transform, "ReverseBtn", "反查", 70f, 25f, 13, () =>
        {
            if (!string.IsNullOrEmpty(row.BoundItemName))
            {
                _onReverseSearchIngredient?.Invoke(row.BoundItemName);
            }
        });
        row.BuySingleBtn = _tpl.CloneActionButton(btnCol.transform, "BuyOneBtn", "跳蚤", 70f, 25f, 13, () =>
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
                    ? "制造已完成，等待领取"
                    : $"制造中 {Mathf.FloorToInt(pct01 * 100f)}%  ·  剩余 {WorkshopRecipeTile.FormatCompactDuration(_entry.RemainingSeconds)}",
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

        var search = new RagfairSearch(EFilterType.BuildItems, new BuildItemSearchValue
        {
            BuildName = string.Empty,
            BuildItems = buildItems,
            BuildCount = 1,
        });
        Patches.WorkshopUiPatch.MarkHideoutRagfairSearch(search);
        _itemUiContext.ExternalRagfairSearch(search);
    }

    private void BuySingleOnRagfair(string templateId, int count)
    {
        if (string.IsNullOrEmpty(templateId) || count <= 0 || _itemUiContext == null)
        {
            return;
        }

        var search = new RagfairSearch(EFilterType.BuildItems, new BuildItemSearchValue
        {
            BuildName = string.Empty,
            BuildItems = new Dictionary<string, int>(1) { { templateId, count } },
            BuildCount = 1,
        });
        Patches.WorkshopUiPatch.MarkHideoutRagfairSearch(search);
        _itemUiContext.ExternalRagfairSearch(search);
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
