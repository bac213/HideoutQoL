using System;
using System.Collections.Generic;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HideoutQoL.Client.UI;

/// <summary>
/// Single virtualized recipe card inside the Workshop catalog grid.
/// Displays a crisp EFT-native item icon, explicit localized product name, duration,
/// material readiness summary, facility tag, favorite toggle, and live production progress.
/// </summary>
internal sealed class WorkshopRecipeTile : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    private const float IconBoxSize = 62f;
    private const float StripeWidth = 3f;
    private const float ProgressHeight = 3f;
    private const float LockedIconAlpha = 0.45f;
    private static readonly Color SelectedMetaColor = new Color32(40, 40, 40, 255);

    private EftUiTemplateCache _tpl;
    private WorkshopGridView _owner;
    private UiFader _iconFader;
    private bool _iconPending;
    private Image _bgImage;
    private Image _readyFrame;
    private CanvasGroup _iconGroup;
    private Image _clockIcon;
    private Image _statusStripe;
    private Image _progressFill;

    private HideoutItemViewFactory _itemFactory;
    private TextMeshProUGUI _titleText;
    private TextMeshProUGUI _stationText;
    private TextMeshProUGUI _timeText;
    private TextMeshProUGUI _statusBadgeText;
    private Button _favButton;
    private Image _favIcon;
    private Image _boltIcon;

    private WorkshopCatalogModel.RecipeEntry _boundEntry;
    private InventoryController _inventoryController;
    private ItemUiContext _itemUiContext;
    private Action<WorkshopCatalogModel.RecipeEntry> _onSelect;
    private Action<WorkshopCatalogModel.RecipeEntry> _onDoubleClick;
    private Action<WorkshopCatalogModel.RecipeEntry> _onToggleFavorite;

    private bool _selected;
    private bool _hovered;
    private string _boundSchemeId;
    private int _lastCount = -1;
    private bool _lastEncoded;
    private int _lastDurationSec = -1;
    private bool _lastShowStation;
    private string _lastStationLabel;
    private int _lastRequiredLevel = -1;
    private bool _lastNeedFuel;

    // Visual snapshot to avoid redundant string allocations / TMP rebuilds on 1s ticks & refreshes
    private bool _hasDynamicSnapshot;
    private WorkshopRecipeState _visState;
    private int _visPct = -1;
    private int _visRemSec = -1;
    private int _visMissingTypes = -1;
    private int _visTotalReq = -1;
    private bool _visToolMissing;
    private bool _visHasEnergy;
    private bool _visCanStartByCount;
    private bool _visFav;

    // Color snapshot
    private bool _hasColorSnapshot;
    private bool _colorSelected;
    private bool _colorHovered;
    private WorkshopRecipeState _colorState;

    internal WorkshopCatalogModel.RecipeEntry BoundEntry => _boundEntry;

    internal static WorkshopRecipeTile Create(Transform parent, EftUiTemplateCache tpl, float height, WorkshopGridView owner)
    {
        var go = new GameObject("RecipeTile", typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, worldPositionStays: false);

        var le = go.GetComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;
        le.flexibleWidth = 1f;

        var tile = go.AddComponent<WorkshopRecipeTile>();
        tile._owner = owner;
        tile.BuildUi(tpl);
        return tile;
    }

    private void BuildUi(EftUiTemplateCache tpl)
    {
        _tpl = tpl;
        _bgImage = GetComponent<Image>();
        _bgImage.color = EftUiTemplateCache.ColorRowIdle;
        _bgImage.raycastTarget = true;

        // Vanilla list rows are borderless; only a finished craft gets a border_generic frame in the ready color.
        _readyFrame = _tpl.CreateFrame(transform, "ReadyFrame", _tpl.BorderGenericSprite, EftUiTemplateCache.ColorAccentReady);
        if (_readyFrame != null)
        {
            _readyFrame.gameObject.SetActive(false);
        }

        // Left colored craftability / active stripe
        _statusStripe = _tpl.CreatePanel(transform, "StatusStripe", EftUiTemplateCache.ColorAccentLocked);
        RectTransform stripeRt = _statusStripe.rectTransform;
        stripeRt.anchorMin = new Vector2(0f, 0f);
        stripeRt.anchorMax = new Vector2(0f, 1f);
        stripeRt.pivot = new Vector2(0f, 0.5f);
        stripeRt.anchoredPosition = Vector2.zero;
        stripeRt.sizeDelta = new Vector2(StripeWidth, 0f);

        // Live progress fill along the bottom edge (shown when Producing)
        Color progressColor = EftUiTemplateCache.ColorAccentProducing;
        progressColor.a = 0.85f;
        _progressFill = _tpl.CreatePanel(transform, "ProgressFill", progressColor);
        RectTransform progRt = _progressFill.rectTransform;
        progRt.anchorMin = new Vector2(0f, 0f);
        progRt.anchorMax = new Vector2(0f, 0f);
        progRt.pivot = new Vector2(0f, 0f);
        progRt.anchoredPosition = new Vector2(StripeWidth, 0f);
        progRt.sizeDelta = new Vector2(0f, ProgressHeight);
        _progressFill.gameObject.SetActive(false);

        // Horizontal content body inside tile
        var bodyGo = new GameObject("Body", typeof(RectTransform), typeof(HorizontalLayoutGroup));
        bodyGo.transform.SetParent(transform, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)bodyGo.transform, left: StripeWidth + 5f, right: 8f, top: 6f, bottom: 6f);
        HorizontalLayoutGroup bodyHlg = bodyGo.GetComponent<HorizontalLayoutGroup>();
        bodyHlg.padding = new RectOffset(0, 0, 0, 0);
        bodyHlg.spacing = 10f;
        bodyHlg.childControlWidth = true;
        bodyHlg.childControlHeight = true;
        bodyHlg.childForceExpandWidth = false;
        bodyHlg.childForceExpandHeight = false;
        bodyHlg.childAlignment = TextAnchor.MiddleLeft;

        // Icon slot container (62x62, as in the handbook EntityElement); the group dims locked recipes
        var iconSlotGo = new GameObject("IconSlot", typeof(RectTransform), typeof(LayoutElement), typeof(CanvasGroup));
        iconSlotGo.transform.SetParent(bodyGo.transform, worldPositionStays: false);
        _iconGroup = iconSlotGo.GetComponent<CanvasGroup>();
        var iconLe = iconSlotGo.GetComponent<LayoutElement>();
        iconLe.minWidth = IconBoxSize;
        iconLe.preferredWidth = IconBoxSize;
        iconLe.minHeight = IconBoxSize;
        iconLe.preferredHeight = IconBoxSize;

        if (_tpl.ResultItemViewFactoryTemplate != null)
        {
            _itemFactory = UnityEngine.Object.Instantiate(_tpl.ResultItemViewFactoryTemplate, iconSlotGo.transform, worldPositionStays: false);
            _itemFactory.name = "ItemFactory";
            _itemFactory.gameObject.SetActive(true);
            var fRt = (RectTransform)_itemFactory.transform;
            fRt.anchorMin = new Vector2(0.5f, 0.5f);
            fRt.anchorMax = new Vector2(0.5f, 0.5f);
            fRt.pivot = new Vector2(0.5f, 0.5f);
            fRt.anchoredPosition = Vector2.zero;
            fRt.sizeDelta = new Vector2(IconBoxSize, IconBoxSize);
            _iconFader = UiFader.For(_itemFactory.gameObject);
        }

        // Center text stack (title + metadata row + status row)
        var textColGo = new GameObject("TextCol", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(LayoutElement));
        textColGo.transform.SetParent(bodyGo.transform, worldPositionStays: false);
        var textLe = textColGo.GetComponent<LayoutElement>();
        textLe.flexibleWidth = 1f;
        textLe.minWidth = 80f;
        textLe.flexibleHeight = 1f;

        VerticalLayoutGroup vlg = textColGo.GetComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(0, 0, 0, 0);
        vlg.spacing = 1f;
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childAlignment = TextAnchor.MiddleLeft;

        // Type scale of the handbook EntityElement: bold 18 name, 14 secondary lines
        _titleText = _tpl.CreateText(textColGo.transform, "Title", "", EftUiTemplateCache.FontName, EftUiTemplateCache.ColorTextPrimary, TextAlignmentOptions.Left, FontStyles.Bold);
        var titleLe = _titleText.gameObject.AddComponent<LayoutElement>();
        titleLe.preferredHeight = 24f;

        // Meta row: [Station/Level] + [Bolt if fuel required] + [Clock] + [Duration]
        var metaGo = new GameObject("MetaRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        metaGo.transform.SetParent(textColGo.transform, worldPositionStays: false);
        metaGo.GetComponent<LayoutElement>().preferredHeight = 20f;
        HorizontalLayoutGroup metaHlg = metaGo.GetComponent<HorizontalLayoutGroup>();
        metaHlg.spacing = 5f;
        metaHlg.childControlWidth = true;
        metaHlg.childControlHeight = true;
        metaHlg.childForceExpandWidth = false;
        metaHlg.childForceExpandHeight = false;
        metaHlg.childAlignment = TextAnchor.MiddleLeft;

        _stationText = _tpl.CreateText(metaGo.transform, "Station", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary);
        _stationText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        if (_tpl.BoltSprite != null)
        {
            _boltIcon = CreateMetaIcon(metaGo.transform, "FuelIcon", _tpl.BoltSprite);
        }
        if (_tpl.ClockSprite != null)
        {
            _clockIcon = CreateMetaIcon(metaGo.transform, "ClockIcon", _tpl.ClockSprite);
        }

        _timeText = _tpl.CreateText(metaGo.transform, "Duration", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorTextSecondary, TextAlignmentOptions.Right);
        var timeLe = _timeText.gameObject.AddComponent<LayoutElement>();
        timeLe.minWidth = _clockIcon != null ? 0f : 72f;

        // Status / readiness line
        _statusBadgeText = _tpl.CreateText(textColGo.transform, "StatusBadge", "", EftUiTemplateCache.FontSmall, EftUiTemplateCache.ColorAccentCraftable);
        _statusBadgeText.gameObject.AddComponent<LayoutElement>().preferredHeight = 20f;

        // Right slim favorite column
        var favColGo = new GameObject("FavCol", typeof(RectTransform), typeof(LayoutElement));
        favColGo.transform.SetParent(bodyGo.transform, worldPositionStays: false);
        var favColLe = favColGo.GetComponent<LayoutElement>();
        favColLe.minWidth = 26f;
        favColLe.preferredWidth = 26f;
        favColLe.minHeight = 62f;

        var favBtnGo = new GameObject("FavButton", typeof(RectTransform), typeof(Image), typeof(Button));
        favBtnGo.transform.SetParent(favColGo.transform, worldPositionStays: false);
        var favRt = (RectTransform)favBtnGo.transform;
        favRt.anchorMin = new Vector2(0.5f, 1f);
        favRt.anchorMax = new Vector2(0.5f, 1f);
        favRt.pivot = new Vector2(0.5f, 1f);
        favRt.anchoredPosition = new Vector2(0f, -2f);
        favRt.sizeDelta = new Vector2(24f, 24f);

        Image favHit = favBtnGo.GetComponent<Image>();
        favHit.color = new Color(1f, 1f, 1f, 0.01f);
        _favButton = favBtnGo.GetComponent<Button>();
        _favButton.transition = Selectable.Transition.None;
        _favButton.onClick.AddListener(HandleFavoriteClick);

        var favVisGo = new GameObject("Star", typeof(RectTransform), typeof(Image));
        favVisGo.transform.SetParent(favRt, worldPositionStays: false);
        EftUiTemplateCache.Stretch((RectTransform)favVisGo.transform, 2f, 2f, 2f, 2f);
        _favIcon = favVisGo.GetComponent<Image>();
        _favIcon.preserveAspect = true;
        _favIcon.raycastTarget = false;
    }

    private static Image CreateMetaIcon(Transform parent, string name, Sprite sprite)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(LayoutElement));
        go.transform.SetParent(parent, worldPositionStays: false);
        var le = go.GetComponent<LayoutElement>();
        le.minWidth = 16f;
        le.preferredWidth = 16f;
        le.minHeight = 16f;
        le.preferredHeight = 16f;
        Image img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.preserveAspect = true;
        img.raycastTarget = false;
        return img;
    }

    internal void Bind(
        WorkshopCatalogModel.RecipeEntry entry,
        InventoryController inventoryController,
        ItemUiContext itemUiContext,
        bool selected,
        bool showStationLabel,
        Action<WorkshopCatalogModel.RecipeEntry> onSelect,
        Action<WorkshopCatalogModel.RecipeEntry> onDoubleClick,
        Action<WorkshopCatalogModel.RecipeEntry> onToggleFavorite)
    {
        _boundEntry = entry;
        _inventoryController = inventoryController;
        _itemUiContext = itemUiContext;
        _onSelect = onSelect;
        _onDoubleClick = onDoubleClick;
        _onToggleFavorite = onToggleFavorite;
        _selected = selected;

        if (entry == null)
        {
            return;
        }

        bool identityChanged = !string.Equals(_boundSchemeId, entry.SchemeId, StringComparison.Ordinal)
            || _lastCount != entry.Scheme.count
            || _lastEncoded != entry.Scheme.isEncoded;

        if (_itemFactory != null && identityChanged)
        {
            _itemFactory.Close();

            // Creating the HideoutItemView (and the display item behind it) is the expensive part of a bind.
            // Hand it to the grid's per-frame budget so a scrollbar fling or a filter change never builds a
            // whole screen of icons in one frame; the text below is cheap and binds right away.
            if (_owner != null)
            {
                _iconFader?.Snap(0f);
                if (!_iconPending)
                {
                    _iconPending = true;
                    _owner.EnqueueIcon(this);
                }
            }
            else
            {
                ShowIcon(entry);
            }

            _boundSchemeId = entry.SchemeId;
            _lastCount = entry.Scheme.count;
            _lastEncoded = entry.Scheme.isEncoded;
            _hasDynamicSnapshot = false;
            _hasColorSnapshot = false;
        }

        if (_itemFactory != null && entry.Scheme.isCodeProduction)
        {
            if (entry.Producer != null && entry.Producer.DecryptedCodeProductionStorage.TryGetValue(entry.SchemeId, out string code))
            {
                _itemFactory.SetCounterText(code ?? "*********");
            }
            else
            {
                _itemFactory.SetCounterText("*********");
            }
        }

        if (identityChanged)
        {
            _titleText.text = entry.Scheme.count > 1
                ? $"{entry.DisplayName}  (x{entry.Scheme.count})"
                : entry.DisplayName;
        }

        if (identityChanged || _lastShowStation != showStationLabel || _lastRequiredLevel != entry.RequiredAreaLevel || !string.Equals(_lastStationLabel, entry.StationLabel, StringComparison.Ordinal))
        {
            _lastShowStation = showStationLabel;
            _lastRequiredLevel = entry.RequiredAreaLevel;
            _lastStationLabel = entry.StationLabel;
            _stationText.text = showStationLabel
                ? $"{entry.StationLabel} · Lv.{entry.RequiredAreaLevel}"
                : $"Lv.{entry.RequiredAreaLevel}";
        }

        if (identityChanged || _lastNeedFuel != entry.Scheme.needFuelForAllProductionTime)
        {
            _lastNeedFuel = entry.Scheme.needFuelForAllProductionTime;
            if (_boltIcon != null)
            {
                _boltIcon.gameObject.SetActive(_lastNeedFuel);
            }
        }

        if (identityChanged || _lastDurationSec != entry.CraftDurationSeconds)
        {
            _lastDurationSec = entry.CraftDurationSeconds;
            _timeText.SetMonospaceText(FormatCompactDuration(entry.CraftDurationSeconds));
        }

        RefreshDynamicVisuals();
    }

    /// <summary>Called by the grid within its frame budget. Returns false if the request went stale.</summary>
    internal bool TryShowPendingIcon()
    {
        if (!_iconPending)
        {
            return false;
        }
        _iconPending = false;
        if (_boundEntry == null || _itemFactory == null)
        {
            return false;
        }

        ShowIcon(_boundEntry);
        _iconFader?.Play(0f, 1f, WorkshopFx.ContentFade);
        return true;
    }

    private void ShowIcon(WorkshopCatalogModel.RecipeEntry entry)
    {
        Item schemeItem = EftUiTemplateCache.CreateHideoutDisplayItem(
            entry.EndProductId,
            entry.SchemeId,
            EOwnerType.HideoutProduction,
            spawnedInSession: false,
            isEncoded: entry.Scheme.isEncoded);

        if (schemeItem != null)
        {
            _itemFactory.Show(schemeItem, _inventoryController, _itemUiContext);
            _itemFactory.ShowInfo(entry.Scheme.isCodeProduction, showFulfilledStatus: false);
            EftUiTemplateCache.NormalizeItemFactoryRect(_itemFactory, IconBoxSize, showInfo: entry.Scheme.isCodeProduction);
        }
    }

    internal void SetSelected(bool selected)
    {
        if (_selected == selected)
        {
            return;
        }
        _selected = selected;
        ApplyColors();
    }

    internal void RefreshDynamicVisuals()
    {
        if (_boundEntry == null)
        {
            return;
        }

        int pct = _boundEntry.State == WorkshopRecipeState.Producing ? Mathf.FloorToInt(_boundEntry.Progress * 100f) : -1;
        int remSec = _boundEntry.State == WorkshopRecipeState.Producing ? _boundEntry.RemainingSeconds : -1;
        bool canStartByCount = _boundEntry.Producer != null && _boundEntry.Producer.CanStartByProductionCount();
        bool fav = _boundEntry.Scheme.FavoriteScheme;

        bool dynamicChanged = !_hasDynamicSnapshot
            || _visState != _boundEntry.State
            || _visPct != pct
            || _visRemSec != remSec
            || _visMissingTypes != _boundEntry.MissingItemTypes
            || _visTotalReq != _boundEntry.TotalItemRequirements
            || _visToolMissing != _boundEntry.IsToolMissing
            || _visHasEnergy != _boundEntry.HasEnergyForCraft
            || _visCanStartByCount != canStartByCount
            || _visFav != fav;

        if (!dynamicChanged)
        {
            ApplyColors();
            return;
        }

        _hasDynamicSnapshot = true;
        _visState = _boundEntry.State;
        _visPct = pct;
        _visRemSec = remSec;
        _visMissingTypes = _boundEntry.MissingItemTypes;
        _visTotalReq = _boundEntry.TotalItemRequirements;
        _visToolMissing = _boundEntry.IsToolMissing;
        _visHasEnergy = _boundEntry.HasEnergyForCraft;
        _visCanStartByCount = canStartByCount;
        _visFav = fav;

        Color stripeColor;
        string statusLine;

        switch (_boundEntry.State)
        {
            case WorkshopRecipeState.Ready:
                stripeColor = EftUiTemplateCache.ColorAccentReady;
                statusLine = "制造完成 · 可立即领取";
                _progressFill.gameObject.SetActive(false);
                break;

            case WorkshopRecipeState.Producing:
                stripeColor = EftUiTemplateCache.ColorAccentProducing;
                statusLine = $"制造中 {pct}% · 剩余 {FormatCompactDuration(remSec)}";
                _progressFill.gameObject.SetActive(true);
                float fullW = Math.Max(0f, ((RectTransform)transform).rect.width - StripeWidth);
                _progressFill.rectTransform.sizeDelta = new Vector2(fullW * Mathf.Clamp01(_boundEntry.Progress), ProgressHeight);
                break;

            case WorkshopRecipeState.Error:
                stripeColor = EftUiTemplateCache.ColorAccentMissing;
                statusLine = "生产中断 · 请检查电力";
                _progressFill.gameObject.SetActive(false);
                break;

            case WorkshopRecipeState.Locked:
                stripeColor = EftUiTemplateCache.ColorAccentLocked;
                statusLine = $"未解锁 · 需设施等级 {_boundEntry.RequiredAreaLevel} 或前置任务";
                _progressFill.gameObject.SetActive(false);
                break;

            case WorkshopRecipeState.Craftable:
                stripeColor = EftUiTemplateCache.ColorAccentCraftable;
                if (!_boundEntry.HasEnergyForCraft)
                {
                    statusLine = "材料齐备 · 需开启发电机";
                }
                else if (!canStartByCount)
                {
                    statusLine = "材料齐备 · 制作槽位已占满";
                }
                else
                {
                    statusLine = $"材料齐备 ({_boundEntry.TotalItemRequirements}/{_boundEntry.TotalItemRequirements})";
                }
                _progressFill.gameObject.SetActive(false);
                break;

            default:
                stripeColor = EftUiTemplateCache.ColorAccentMissing;
                statusLine = _boundEntry.IsToolMissing
                    ? $"缺少材料/工具 ({_boundEntry.MissingItemTypes} 项不足)"
                    : $"缺少材料 ({_boundEntry.MissingItemTypes} 项不足)";
                _progressFill.gameObject.SetActive(false);
                break;
        }

        _statusStripe.color = stripeColor;
        _statusBadgeText.SetMonospaceText(statusLine, onlyDigits: true);

        UpdateFavoriteVisual();
        ApplyColors();
    }

    private void UpdateFavoriteVisual()
    {
        if (_favIcon == null || _boundEntry == null)
        {
            return;
        }
        bool fav = _boundEntry.Scheme.FavoriteScheme;
        Sprite spr = fav
            ? (_tpl.StarSelectedSprite != null ? _tpl.StarSelectedSprite : _tpl.StarDefaultSprite)
            : (_tpl.StarDefaultSprite != null ? _tpl.StarDefaultSprite : _tpl.StarSelectedSprite);
        _favIcon.sprite = spr;
        _favIcon.color = fav
            ? EftUiTemplateCache.ColorAccentReady
            : new Color(0.75f, 0.75f, 0.72f, _hovered ? 0.9f : 0.5f);
    }

    private void ApplyColors()
    {
        if (_bgImage == null || _boundEntry == null)
        {
            return;
        }

        if (_hasColorSnapshot
            && _colorSelected == _selected
            && _colorHovered == _hovered
            && _colorState == _boundEntry.State)
        {
            return;
        }

        if (_colorHovered != _hovered)
        {
            UpdateFavoriteVisual();
        }

        // Ease only a pure hover change; rebinding, selection and state changes must land immediately.
        bool hoverOnly = _hasColorSnapshot
            && _colorSelected == _selected
            && _colorState == _boundEntry.State
            && _colorHovered != _hovered;

        _hasColorSnapshot = true;
        _colorSelected = _selected;
        _colorHovered = _hovered;
        _colorState = _boundEntry.State;

        if (_readyFrame != null)
        {
            _readyFrame.gameObject.SetActive(_boundEntry.State == WorkshopRecipeState.Ready);
        }
        if (_iconGroup != null)
        {
            _iconGroup.alpha = _boundEntry.State == WorkshopRecipeState.Locked ? LockedIconAlpha : 1f;
        }
        if (_clockIcon != null)
        {
            _clockIcon.color = _selected ? SelectedMetaColor : (Color)EftUiTemplateCache.ColorTextSecondary;
        }

        if (_selected)
        {
            WorkshopFx.SetColor(_bgImage, EftUiTemplateCache.ColorRowSelected, animate: false);
            _titleText.color = EftUiTemplateCache.ColorTextDark;
            _stationText.color = SelectedMetaColor;
            _timeText.color = SelectedMetaColor;
            _statusBadgeText.color = _boundEntry.State switch
            {
                WorkshopRecipeState.Ready => new Color32(120, 72, 0, 255),
                WorkshopRecipeState.Producing or WorkshopRecipeState.Craftable => new Color32(18, 92, 22, 255),
                WorkshopRecipeState.MissingMaterials or WorkshopRecipeState.Error => new Color32(148, 28, 24, 255),
                _ => new Color32(60, 60, 60, 255),
            };
        }
        else
        {
            WorkshopFx.SetColor(
                _bgImage,
                _hovered ? EftUiTemplateCache.ColorRowHover : EftUiTemplateCache.ColorRowIdle,
                animate: hoverOnly);
            _titleText.color = _boundEntry.State == WorkshopRecipeState.Locked
                ? EftUiTemplateCache.ColorTextMuted
                : EftUiTemplateCache.ColorTextPrimary;
            _stationText.color = EftUiTemplateCache.ColorTextSecondary;
            _timeText.color = EftUiTemplateCache.ColorTextSecondary;
            _statusBadgeText.color = _boundEntry.State switch
            {
                WorkshopRecipeState.Ready => EftUiTemplateCache.ColorAccentReady,
                WorkshopRecipeState.Producing => EftUiTemplateCache.ColorAccentProducing,
                WorkshopRecipeState.Craftable => EftUiTemplateCache.ColorAccentCraftable,
                WorkshopRecipeState.MissingMaterials or WorkshopRecipeState.Error => EftUiTemplateCache.ColorAccentMissing,
                _ => EftUiTemplateCache.ColorTextMuted,
            };
        }
    }

    internal void Unbind()
    {
        _iconPending = false;
        if (_itemFactory != null)
        {
            _itemFactory.Close();
        }
        _boundEntry = null;
        _boundSchemeId = null;
        _lastCount = -1;
        _lastDurationSec = -1;
        _lastRequiredLevel = -1;
        _lastStationLabel = null;
        _hasDynamicSnapshot = false;
        _hasColorSnapshot = false;
        _hovered = false;
        _selected = false;
    }

    private void HandleFavoriteClick()
    {
        if (_boundEntry != null)
        {
            _onToggleFavorite?.Invoke(_boundEntry);
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        _hovered = true;
        ApplyColors();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        _hovered = false;
        ApplyColors();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (_boundEntry == null || eventData.button != PointerEventData.InputButton.Left)
        {
            return;
        }

        if (eventData.clickCount >= 2)
        {
            _onDoubleClick?.Invoke(_boundEntry);
        }
        else
        {
            _onSelect?.Invoke(_boundEntry);
        }
    }

    internal static string FormatCompactDuration(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return "00:00";
        }

        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:D2}:{minutes:D2}:{seconds:D2}"
            : $"{minutes:D2}:{seconds:D2}";
    }
}

/// <summary>
/// Row-virtualized multi-column recipe grid keeping only visible rows + small scroll buffer instantiated.
/// Zero-allocation steady scrolling even across 250+ modded recipes.
/// </summary>
internal sealed class WorkshopGridView : MonoBehaviour
{
    private const float RowHeight = 80f;
    private const float RowSpacing = 4f;
    private const int BufferRows = 1;

    // Icon creation budget per frame. At least one icon is always built so the queue drains.
    private const double IconBudgetMs = 3.0;

    private sealed class GridRow
    {
        public int RowIndex = -1;
        public GameObject Root;
        public readonly List<WorkshopRecipeTile> Tiles = new(3);
        public readonly List<LayoutElement> DummySpacers = new(3);
    }

    private EftUiTemplateCache _tpl;
    private UiFader _contentFader;
    private ScrollRect _scrollRect;
    private RectTransform _viewport;
    private RectTransform _content;

    private LayoutElement _topSpacer;
    private LayoutElement _bottomSpacer;
    private Transform _poolHolder;

    private readonly List<GridRow> _activeRows = new(12);
    private readonly Stack<GridRow> _rowPool = new(12);
    private readonly List<WorkshopCatalogModel.RecipeEntry> _items = new(256);
    private readonly List<WorkshopRecipeTile> _pendingIcons = new(48);
    private readonly Stopwatch _iconBudget = new();

    private InventoryController _inventoryController;
    private ItemUiContext _itemUiContext;
    private string _selectedSchemeId;
    private bool _showStationLabel;
    private int _columns = 2;

    private int _firstRow = -1;
    private int _lastRow = -1;
    private float _lastScrollY = float.NaN;
    private float _lastViewportH = -1f;

    internal Action<WorkshopCatalogModel.RecipeEntry> OnRecipeSelected;
    internal Action<WorkshopCatalogModel.RecipeEntry> OnRecipeDoubleClicked;
    internal Action<WorkshopCatalogModel.RecipeEntry> OnRecipeFavoriteToggled;

    internal void Setup(
        EftUiTemplateCache tpl,
        ScrollRect scrollRect,
        RectTransform viewport,
        RectTransform content,
        InventoryController inventoryController,
        ItemUiContext itemUiContext)
    {
        _tpl = tpl;
        _scrollRect = scrollRect;
        _viewport = viewport;
        _content = content;
        _inventoryController = inventoryController;
        _itemUiContext = itemUiContext;

        var vlg = _content.GetComponent<VerticalLayoutGroup>();
        if (vlg != null)
        {
            vlg.spacing = RowSpacing;
            vlg.padding = new RectOffset(2, 2, 2, 10);
        }

        _topSpacer = CreateSpacer("TopSpacer", _content);
        _bottomSpacer = CreateSpacer("BottomSpacer", _content);

        var poolGo = new GameObject("RowPool", typeof(RectTransform));
        poolGo.SetActive(false);
        poolGo.transform.SetParent(transform, worldPositionStays: false);
        _poolHolder = poolGo.transform;

        _scrollRect.onValueChanged.AddListener(_ => UpdateVisibleWindow(forceRebind: false));
    }

    internal void SetItems(
        IReadOnlyList<WorkshopCatalogModel.RecipeEntry> items,
        int columns,
        string selectedSchemeId,
        bool showStationLabel,
        bool resetScroll)
    {
        _items.Clear();
        if (items != null)
        {
            for (int i = 0; i < items.Count; i++)
            {
                _items.Add(items[i]);
            }
        }

        int newCols = Mathf.Clamp(columns, 1, 3);
        if (newCols != _columns)
        {
            ReturnAllRowsToPool();
            ClearRowPool();
            _columns = newCols;
        }

        _selectedSchemeId = selectedSchemeId;
        _showStationLabel = showStationLabel;

        if (resetScroll && _content != null)
        {
            _content.anchoredPosition = Vector2.zero;

            // A different list is about to be bound (station / category / filter / search): fade it in
            // instead of swapping every card in one frame. State refreshes keep resetScroll false.
            _contentFader ??= UiFader.For(_content.gameObject);
            _contentFader.Play(0.15f, 1f, WorkshopFx.ContentFade);
        }

        _firstRow = -1;
        _lastRow = -1;
        _lastScrollY = float.NaN;
        UpdateVisibleWindow(forceRebind: true);
    }

    internal void UpdateSelection(string selectedSchemeId)
    {
        _selectedSchemeId = selectedSchemeId;
        for (int r = 0; r < _activeRows.Count; r++)
        {
            GridRow row = _activeRows[r];
            for (int c = 0; c < row.Tiles.Count; c++)
            {
                WorkshopRecipeTile tile = row.Tiles[c];
                if (tile.gameObject.activeSelf && tile.BoundEntry != null)
                {
                    tile.SetSelected(string.Equals(tile.BoundEntry.SchemeId, _selectedSchemeId, StringComparison.Ordinal));
                }
            }
        }
    }

    internal void RefreshVisibleStates()
    {
        for (int r = 0; r < _activeRows.Count; r++)
        {
            GridRow row = _activeRows[r];
            for (int c = 0; c < row.Tiles.Count; c++)
            {
                WorkshopRecipeTile tile = row.Tiles[c];
                if (tile.gameObject.activeSelf && tile.BoundEntry != null)
                {
                    tile.RefreshDynamicVisuals();
                }
            }
        }
    }

    internal void Clear()
    {
        ReturnAllRowsToPool();
        _pendingIcons.Clear();
        _items.Clear();
        _firstRow = -1;
        _lastRow = -1;
        if (_topSpacer != null) _topSpacer.preferredHeight = 0f;
        if (_bottomSpacer != null) _bottomSpacer.preferredHeight = 0f;
    }

    internal void EnqueueIcon(WorkshopRecipeTile tile)
    {
        _pendingIcons.Add(tile);
    }

    private void LateUpdate()
    {
        if (_viewport != null && _items.Count > 0)
        {
            float vpH = _viewport.rect.height;
            float scrollY = _content != null ? _content.anchoredPosition.y : 0f;
            if (Mathf.Abs(vpH - _lastViewportH) > 2f || Mathf.Abs(scrollY - _lastScrollY) > 1f)
            {
                UpdateVisibleWindow(forceRebind: false);
            }
        }

        // Runs after the window update so rows bound this frame start loading this frame.
        DrainIconQueue();
    }

    private void DrainIconQueue()
    {
        if (_pendingIcons.Count == 0)
        {
            return;
        }

        _iconBudget.Restart();
        int consumed = 0;
        bool builtAny = false;
        while (consumed < _pendingIcons.Count)
        {
            if (builtAny && _iconBudget.Elapsed.TotalMilliseconds >= IconBudgetMs)
            {
                break;
            }

            // Tiles scrolled away or rebound before their turn report false and cost nothing.
            if (_pendingIcons[consumed++].TryShowPendingIcon())
            {
                builtAny = true;
            }
        }
        _pendingIcons.RemoveRange(0, consumed);
    }

    private void UpdateVisibleWindow(bool forceRebind)
    {
        if (_content == null || _viewport == null)
        {
            return;
        }

        int totalItems = _items.Count;
        if (totalItems == 0)
        {
            ReturnAllRowsToPool();
            _firstRow = -1;
            _lastRow = -1;
            _topSpacer.preferredHeight = 0f;
            _bottomSpacer.preferredHeight = 0f;
            return;
        }

        int totalRows = (totalItems + _columns - 1) / _columns;
        float step = RowHeight + RowSpacing;
        float vpHeight = _viewport.rect.height > 10f ? _viewport.rect.height : 620f;
        float scrollY = Mathf.Max(0f, _content.anchoredPosition.y);

        _lastViewportH = vpHeight;
        _lastScrollY = scrollY;

        int first = Mathf.Max(0, Mathf.FloorToInt(scrollY / step) - BufferRows);
        int last = Mathf.Min(totalRows - 1, Mathf.CeilToInt((scrollY + vpHeight) / step) + BufferRows);

        if (!forceRebind && first == _firstRow && last == _lastRow)
        {
            return;
        }

        int neededCount = (last - first) + 1;
        var keptByRowIndex = new Dictionary<int, GridRow>(neededCount);

        if (!forceRebind && _firstRow >= 0 && _lastRow >= _firstRow)
        {
            // Sliding window: keep rows still inside [first, last] untouched to avoid killing/recreating ItemViews
            for (int i = _activeRows.Count - 1; i >= 0; i--)
            {
                GridRow existing = _activeRows[i];
                if (existing.RowIndex >= first && existing.RowIndex <= last)
                {
                    keptByRowIndex[existing.RowIndex] = existing;
                }
                else
                {
                    RecycleRow(existing);
                }
                _activeRows.RemoveAt(i);
            }
        }
        else if (forceRebind)
        {
            // Filter/resort refresh: transfer active rows by slot order WITHOUT unbinding first,
            // so any tile whose SchemeId stays in the same slot reuses its HideoutItemView in O(1).
            int reuseIdx = 0;
            for (int rowIdx = first; rowIdx <= last; rowIdx++)
            {
                if (reuseIdx < _activeRows.Count)
                {
                    GridRow reused = _activeRows[reuseIdx++];
                    keptByRowIndex[rowIdx] = reused;
                }
            }
            for (int i = reuseIdx; i < _activeRows.Count; i++)
            {
                RecycleRow(_activeRows[i]);
            }
            _activeRows.Clear();
        }
        else
        {
            ReturnAllRowsToPool();
        }

        _firstRow = first;
        _lastRow = last;

        _topSpacer.preferredHeight = first > 0 ? first * step - RowSpacing : 0f;
        _topSpacer.gameObject.SetActive(first > 0);
        _topSpacer.transform.SetAsFirstSibling();

        int siblingIdx = 1;
        for (int rowIdx = first; rowIdx <= last; rowIdx++)
        {
            bool wasAlreadyBoundToSameRow = keptByRowIndex.TryGetValue(rowIdx, out GridRow row) && !forceRebind;
            if (row == null)
            {
                row = PopOrCreateRow();
                row.Root.transform.SetParent(_content, worldPositionStays: false);
                row.Root.SetActive(true);
            }

            row.RowIndex = rowIdx;
            if (row.Root.transform.GetSiblingIndex() != siblingIdx)
            {
                row.Root.transform.SetSiblingIndex(siblingIdx);
            }
            siblingIdx++;

            if (!wasAlreadyBoundToSameRow)
            {
                BindRowTiles(row, rowIdx, totalItems);
            }

            _activeRows.Add(row);
        }

        int remainingRows = (totalRows - 1) - last;
        _bottomSpacer.preferredHeight = remainingRows > 0 ? remainingRows * step - RowSpacing : 0f;
        _bottomSpacer.gameObject.SetActive(remainingRows > 0);
        _bottomSpacer.transform.SetAsLastSibling();
    }

    private void BindRowTiles(GridRow row, int rowIdx, int totalItems)
    {
        for (int col = 0; col < _columns; col++)
        {
            int itemIdx = rowIdx * _columns + col;
            if (itemIdx < totalItems)
            {
                WorkshopCatalogModel.RecipeEntry entry = _items[itemIdx];
                if (row.DummySpacers[col].gameObject.activeSelf)
                {
                    row.DummySpacers[col].gameObject.SetActive(false);
                }
                if (!row.Tiles[col].gameObject.activeSelf)
                {
                    row.Tiles[col].gameObject.SetActive(true);
                }
                bool isSel = string.Equals(entry.SchemeId, _selectedSchemeId, StringComparison.Ordinal);
                row.Tiles[col].Bind(
                    entry,
                    _inventoryController,
                    _itemUiContext,
                    isSel,
                    _showStationLabel,
                    OnRecipeSelected,
                    OnRecipeDoubleClicked,
                    OnRecipeFavoriteToggled);
            }
            else
            {
                row.Tiles[col].Unbind();
                if (row.Tiles[col].gameObject.activeSelf)
                {
                    row.Tiles[col].gameObject.SetActive(false);
                }
                if (!row.DummySpacers[col].gameObject.activeSelf)
                {
                    row.DummySpacers[col].gameObject.SetActive(true);
                }
            }
        }
    }

    private void RecycleRow(GridRow row)
    {
        for (int c = 0; c < row.Tiles.Count; c++)
        {
            row.Tiles[c].Unbind();
        }
        row.RowIndex = -1;
        row.Root.SetActive(false);
        row.Root.transform.SetParent(_poolHolder, worldPositionStays: false);
        _rowPool.Push(row);
    }

    private void ReturnAllRowsToPool()
    {
        for (int i = 0; i < _activeRows.Count; i++)
        {
            RecycleRow(_activeRows[i]);
        }
        _activeRows.Clear();
    }

    private void ClearRowPool()
    {
        while (_rowPool.Count > 0)
        {
            GridRow row = _rowPool.Pop();
            if (row.Root != null)
            {
                UnityEngine.Object.Destroy(row.Root);
            }
        }
    }

    private GridRow PopOrCreateRow()
    {
        if (_rowPool.Count > 0)
        {
            return _rowPool.Pop();
        }

        var rowGo = new GameObject("RecipeRow", typeof(RectTransform), typeof(HorizontalLayoutGroup), typeof(LayoutElement));
        var le = rowGo.GetComponent<LayoutElement>();
        le.preferredHeight = RowHeight;
        le.minHeight = RowHeight;
        le.flexibleWidth = 1f;

        var hlg = rowGo.GetComponent<HorizontalLayoutGroup>();
        hlg.padding = new RectOffset(0, 0, 0, 0);
        hlg.spacing = RowSpacing;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;

        var row = new GridRow { Root = rowGo };
        for (int c = 0; c < _columns; c++)
        {
            WorkshopRecipeTile tile = WorkshopRecipeTile.Create(rowGo.transform, _tpl, RowHeight, this);
            row.Tiles.Add(tile);

            var dummyGo = new GameObject("SpacerSlot", typeof(RectTransform), typeof(LayoutElement));
            dummyGo.transform.SetParent(rowGo.transform, worldPositionStays: false);
            dummyGo.GetComponent<LayoutElement>().flexibleWidth = 1f;
            dummyGo.SetActive(false);
            row.DummySpacers.Add(dummyGo.GetComponent<LayoutElement>());
        }
        return row;
    }

    private static LayoutElement CreateSpacer(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(LayoutElement));
        go.transform.SetParent(parent, worldPositionStays: false);
        var le = go.GetComponent<LayoutElement>();
        le.preferredHeight = 0f;
        le.flexibleWidth = 1f;
        go.SetActive(false);
        return le;
    }
}
