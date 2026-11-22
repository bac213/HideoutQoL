using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.HandBook;
using EFT.Hideout;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Ragfair;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace HideoutQoL.Client.UI;

/// <summary>
/// Extracts and caches live, genuine Tarkov UI component templates, TMP fonts, sprites, shadows,
/// hairlines, scrollbars, buttons, search fields, dropdowns, and item preview widgets directly
/// from <see cref="CommonUI.Instance"/> so custom Hideout screens match EFT pixel-for-pixel.
/// </summary>
internal sealed class EftUiTemplateCache
{
    // Canonical Tarkov dark military UI palette (verified against EntityListElement & HandbookScreen)
    internal static readonly Color32 ColorTextPrimary = new(197, 195, 178, 255);    // #C5C3B2 bone/parchment
    internal static readonly Color32 ColorTextSecondary = new(166, 176, 181, 255);  // #A6B0B5 muted slate
    internal static readonly Color32 ColorTextDark = new(12, 13, 14, 255);
    internal static readonly Color32 ColorTextMuted = new(120, 125, 128, 255);

    internal static readonly Color ColorWindowScrim = new(0.024f, 0.027f, 0.031f, 0.985f);
    internal static readonly Color ColorHeaderBg = new(0.036f, 0.040f, 0.045f, 0.99f);
    internal static readonly Color ColorPanelBg = new(0.046f, 0.050f, 0.056f, 0.96f);
    internal static readonly Color ColorSubBarBg = new(0.075f, 0.080f, 0.086f, 0.92f);

    internal static readonly Color ColorRowIdle = new(0.275f, 0.275f, 0.275f, 0.314f);
    internal static readonly Color ColorRowHover = new(0.275f, 0.275f, 0.275f, 0.85f);
    internal static readonly Color ColorRowSelected = new Color32(197, 195, 178, 255);

    internal static readonly Color ColorHairline = new(0.42f, 0.42f, 0.40f, 0.48f);
    internal static readonly Color ColorCardBorder = new(0.32f, 0.33f, 0.31f, 0.65f);

    internal static readonly Color32 ColorAccentReady = new(218, 178, 82, 255);
    internal static readonly Color32 ColorAccentProducing = new(115, 194, 96, 255);
    internal static readonly Color32 ColorAccentCraftable = new(124, 174, 108, 255);
    internal static readonly Color32 ColorAccentMissing = new(198, 86, 76, 255);
    internal static readonly Color32 ColorAccentLocked = new(102, 106, 110, 255);
    internal static readonly Color32 ColorAccentTool = new(56, 172, 206, 255);

    private static readonly FieldInfo SubstrateField =
        AccessTools.Field(typeof(HideoutScreenOverlay), "_areaSubstrate");
    private static readonly FieldInfo HandbookCategoriesField =
        AccessTools.Field(typeof(HandbookScreen), "_categoriesPanel");
    private static readonly FieldInfo HandbookEntitiesField =
        AccessTools.Field(typeof(HandbookCategoriesPanel), "_entitiesPanel");
    private static readonly FieldInfo EntitiesPreviewField =
        AccessTools.Field(typeof(EntitiesPanel), "_itemPreview");
    private static readonly FieldInfo CategoryViewOpenSpriteField =
        AccessTools.Field(typeof(CategoryView), "_openSprite");
    private static readonly FieldInfo CategoryViewCloseSpriteField =
        AccessTools.Field(typeof(CategoryView), "_closeSprite");
    private static readonly FieldInfo ComplementSelectedIconField =
        AccessTools.Field(typeof(ComplementaryButton), "_selectedIcon");
    private static readonly FieldInfo ComplementDefaultIconField =
        AccessTools.Field(typeof(ComplementaryButton), "_defaultIcon");

    private static EftUiTemplateCache _instance;
    internal static EftUiTemplateCache Instance => _instance ??= new EftUiTemplateCache();

    internal TMP_FontAsset FontAsset { get; private set; }
    internal Material FontMaterial { get; private set; }

    internal DefaultUIButton BackButtonTemplate { get; private set; }
    internal DefaultUIButton ActionButtonTemplate { get; private set; }
    internal DefaultUIButton CompactButtonTemplate { get; private set; }
    internal ValidationInputField SearchInputTemplate { get; private set; }
    internal DropDownBox DropDownTemplate { get; private set; }
    internal GameObject ScrollbarTemplate { get; private set; }
    internal GameObject TopShadowTemplate { get; private set; }
    internal GameObject BottomShadowTemplate { get; private set; }
    internal Image LineTemplate { get; private set; }

    internal HideoutItemViewFactory ResultItemViewFactoryTemplate { get; private set; }
    internal HideoutProductionRequirementView RequirementViewTemplate { get; private set; }
    internal ItemSpecificationPanel SpecificationPanelTemplate { get; private set; }
    internal CameraImage CameraImageTemplate { get; private set; }

    internal Sprite StarSelectedSprite { get; private set; }
    internal Sprite StarDefaultSprite { get; private set; }
    internal Sprite BuySprite { get; private set; }
    internal Sprite SearchIconSprite { get; private set; }
    internal Sprite CategoryOpenSprite { get; private set; }
    internal Sprite CategoryCloseSprite { get; private set; }
    internal Sprite BoltSprite { get; private set; }
    internal Sprite ClockSprite { get; private set; }
    internal Sprite WhiteBoxSprite { get; private set; }

    private readonly struct DisplayItemKey : IEquatable<DisplayItemKey>
    {
        public readonly string SchemeId;
        public readonly MongoID TemplateId;
        public readonly EOwnerType OwnerType;
        public readonly bool SpawnedInSession;
        public readonly bool IsEncoded;

        public DisplayItemKey(string schemeId, MongoID templateId, EOwnerType ownerType, bool spawnedInSession, bool isEncoded)
        {
            SchemeId = schemeId ?? string.Empty;
            TemplateId = templateId;
            OwnerType = ownerType;
            SpawnedInSession = spawnedInSession;
            IsEncoded = isEncoded;
        }

        public bool Equals(DisplayItemKey other)
        {
            return string.Equals(SchemeId, other.SchemeId, StringComparison.Ordinal)
                && TemplateId == other.TemplateId
                && OwnerType == other.OwnerType
                && SpawnedInSession == other.SpawnedInSession
                && IsEncoded == other.IsEncoded;
        }

        public override bool Equals(object obj) => obj is DisplayItemKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = StringComparer.Ordinal.GetHashCode(SchemeId);
                hash = (hash * 397) ^ TemplateId.GetHashCode();
                hash = (hash * 397) ^ (int)OwnerType;
                hash = (hash * 397) ^ (SpawnedInSession ? 1 : 0);
                hash = (hash * 397) ^ (IsEncoded ? 2 : 0);
                return hash;
            }
        }
    }

    private readonly Dictionary<DisplayItemKey, Item> _displayItemCache = new(256);

    internal bool IsReady { get; private set; }

    internal void ClearDisplayItemCache()
    {
        _displayItemCache.Clear();
    }

    internal bool EnsureInitialized(HideoutScreenOverlay overlay)
    {
        if (IsReady && ActionButtonTemplate != null && ResultItemViewFactoryTemplate != null)
        {
            return true;
        }

        try
        {
            CommonUI commonUi = MonoBehaviourSingleton<CommonUI>.Instantiated
                ? MonoBehaviourSingleton<CommonUI>.Instance
                : null;
            if (commonUi == null)
            {
                return false;
            }

            ExtractFromHandbook(commonUi.HandbookScreen);
            ExtractFromHideout(overlay != null ? overlay : commonUi.HideoutScreenOverlay);
            ExtractDropDown(commonUi);
            ResolveFallbackFont(commonUi);

            IsReady = ActionButtonTemplate != null && ResultItemViewFactoryTemplate != null;
            if (!IsReady)
            {
                HideoutQoLPlugin.Log?.LogWarning("HideoutQoL: EftUiTemplateCache incomplete after extraction.");
            }
            return IsReady;
        }
        catch (Exception ex)
        {
            HideoutQoLPlugin.Log?.LogError($"HideoutQoL: EftUiTemplateCache init failed: {ex}");
            return false;
        }
    }

    private void ExtractFromHandbook(HandbookScreen handbook)
    {
        if (handbook == null)
        {
            return;
        }

        if (BackButtonTemplate == null)
        {
            BackButtonTemplate = handbook._backButton;
        }

        TextMeshProUGUI captionLabel = handbook.transform.Find("Caption/Label")?.GetComponent<TextMeshProUGUI>();
        if (captionLabel != null)
        {
            FontAsset = captionLabel.font;
            FontMaterial = captionLabel.fontSharedMaterial;
        }

        Transform panelsBg = handbook.transform.Find("Panels/Background");
        if (panelsBg != null)
        {
            TopShadowTemplate ??= panelsBg.Find("Top Shadow")?.gameObject;
            BottomShadowTemplate ??= panelsBg.Find("Bottom Shadow")?.gameObject;
            LineTemplate ??= panelsBg.Find("Top Shadow/Line")?.GetComponent<Image>();
        }

        Transform catPanel = handbook.transform.Find("Panels/CategoriesPanel");
        if (catPanel != null)
        {
            ScrollbarTemplate ??= catPanel.Find("Scroll Area/Scrollbar")?.gameObject;
            LineTemplate ??= catPanel.Find("Line")?.GetComponent<Image>();
        }

        Transform searchTransform = handbook.transform.Find("SearchInputField");
        if (searchTransform != null)
        {
            SearchIconSprite ??= searchTransform.Find("SearchIcon")?.GetComponent<Image>()?.sprite;
            SearchInputTemplate ??= searchTransform.GetComponent<ValidationInputField>();
        }

        if (HandbookCategoriesField?.GetValue(handbook) is HandbookCategoriesPanel catComp && catComp != null)
        {
            if (catComp.SearchInputField is ValidationInputField vif)
            {
                SearchInputTemplate ??= vif;
            }

            if (catComp.CombinedCategoryView?.CategoryView is CategoryView cv)
            {
                CategoryOpenSprite ??= CategoryViewOpenSpriteField?.GetValue(cv) as Sprite;
                CategoryCloseSprite ??= CategoryViewCloseSpriteField?.GetValue(cv) as Sprite;
            }

            if (HandbookEntitiesField?.GetValue(catComp) is EntitiesPanel entities && entities != null)
            {
                if (EntitiesPreviewField?.GetValue(entities) is HandbookItemPreview preview && preview != null)
                {
                    SpecificationPanelTemplate ??= preview._itemSpecificationPanel;
                    if (SpecificationPanelTemplate != null && CameraImageTemplate == null)
                    {
                        CameraImageTemplate = SpecificationPanelTemplate._cameraImage;
                    }
                }
            }
        }
    }

    private void ExtractFromHideout(HideoutScreenOverlay overlay)
    {
        if (overlay == null)
        {
            return;
        }

        CompactButtonTemplate ??= overlay._enterHideoutButtonSpawner;
        BackButtonTemplate ??= overlay._returnToMenuButtonSpawner;

        AreaScreenSubstrate substrate = SubstrateField?.GetValue(overlay) as AreaScreenSubstrate;
        if (substrate == null)
        {
            substrate = overlay._areaSubstrateTemplate;
        }

        if (substrate == null)
        {
            return;
        }

        ActionButtonTemplate ??= substrate._actionButton;
        ScrollbarTemplate ??= substrate.transform.Find("Content/CurrentLevel/CurrentContainer/Scrollbar")?.gameObject;

        IAreaRelatedPanel relatedPanel = substrate.ChooseView(new RelatedProduction());
        if (relatedPanel is ProductionPanel prodPanel && prodPanel != null)
        {
            if (prodPanel._searchInputField != null)
            {
                SearchInputTemplate = prodPanel._searchInputField;
                SearchIconSprite ??= prodPanel._searchInputField.transform.Find("SearchIcon")?.GetComponent<Image>()?.sprite;
            }

            if (prodPanel._header != null && FontAsset == null)
            {
                FontAsset = prodPanel._header.font;
                FontMaterial = prodPanel._header.fontSharedMaterial;
            }

            ProduceView pv = prodPanel._produceViewTemplate;
            if (pv != null)
            {
                ResultItemViewFactoryTemplate ??= pv._resultItemIconViewFactory;
                RequirementViewTemplate ??= pv._requiredItemTemplate;

                if (pv._startButton != null)
                {
                    // StartButton has no back-arrow icon and uses clean text styling
                    ActionButtonTemplate = pv._startButton;
                }

                if (pv._favoriteButton != null)
                {
                    StarSelectedSprite ??= ComplementSelectedIconField?.GetValue(pv._favoriteButton) as Sprite;
                    StarDefaultSprite ??= ComplementDefaultIconField?.GetValue(pv._favoriteButton) as Sprite;
                }

                if (pv._buyButton != null)
                {
                    BuySprite ??= ComplementDefaultIconField?.GetValue(pv._buyButton) as Sprite
                        ?? ComplementSelectedIconField?.GetValue(pv._buyButton) as Sprite;
                }

                ClockSprite ??= pv.transform.Find("TimePanel/RequiredTimePanel/Image")?.GetComponent<Image>()?.sprite;
                BoltSprite ??= pv._electricityNeededIcon?.GetComponentInChildren<Image>(includeInactive: true)?.sprite;

                WhiteBoxSprite ??= pv.transform.Find("Background")?.GetComponent<Image>()?.sprite;
            }
        }
    }

    private void ExtractDropDown(CommonUI commonUi)
    {
        if (DropDownTemplate != null || commonUi.SettingsScreen == null)
        {
            return;
        }

        DropDownTemplate = commonUi.SettingsScreen.GetComponentInChildren<DropDownBox>(includeInactive: true);
    }

    private void ResolveFallbackFont(CommonUI commonUi)
    {
        if (FontAsset != null)
        {
            return;
        }

        TextMeshProUGUI anyTmp = commonUi.GetComponentInChildren<TextMeshProUGUI>(includeInactive: true);
        if (anyTmp != null)
        {
            FontAsset = anyTmp.font;
            FontMaterial = anyTmp.fontSharedMaterial;
        }
    }

    internal TextMeshProUGUI CreateText(
        Transform parent,
        string name,
        string initialText,
        float fontSize,
        Color color,
        TextAlignmentOptions alignment = TextAlignmentOptions.Left,
        FontStyles fontStyle = FontStyles.Normal,
        bool raycastTarget = false)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, worldPositionStays: false);

        var tmp = go.GetComponent<TextMeshProUGUI>();
        if (FontAsset != null)
        {
            tmp.font = FontAsset;
        }
        if (FontMaterial != null)
        {
            tmp.fontSharedMaterial = FontMaterial;
        }
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = alignment;
        tmp.fontStyle = fontStyle;
        tmp.raycastTarget = raycastTarget;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.text = initialText ?? string.Empty;
        return tmp;
    }

    internal Image CreatePanel(Transform parent, string name, Color color, bool raycastTarget = false)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, worldPositionStays: false);
        var img = go.GetComponent<Image>();
        img.color = color;
        img.raycastTarget = raycastTarget;
        return img;
    }

    internal void AddShadowPair(Transform parent)
    {
        if (TopShadowTemplate != null)
        {
            GameObject top = UnityEngine.Object.Instantiate(TopShadowTemplate, parent, worldPositionStays: false);
            top.name = "TopShadow";
            top.SetActive(true);
            var rt = (RectTransform)top.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, 48f);
        }

        if (BottomShadowTemplate != null)
        {
            GameObject bot = UnityEngine.Object.Instantiate(BottomShadowTemplate, parent, worldPositionStays: false);
            bot.name = "BottomShadow";
            bot.SetActive(true);
            var rt = (RectTransform)bot.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, 36f);
        }
    }

    internal Image CreateDivider(Transform parent, string name, bool vertical)
    {
        Image img;
        if (LineTemplate != null)
        {
            img = UnityEngine.Object.Instantiate(LineTemplate, parent, worldPositionStays: false);
            img.name = name;
            img.gameObject.SetActive(true);
        }
        else
        {
            img = CreatePanel(parent, name, ColorHairline);
        }

        img.color = ColorHairline;
        img.raycastTarget = false;
        var rt = img.rectTransform;
        if (vertical)
        {
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(1f, 0f);
        }
        else
        {
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(0f, 1f);
        }

        return img;
    }

    internal DefaultUIButton CloneActionButton(
        Transform parent,
        string name,
        string rawText,
        float width,
        float height,
        int fontSize,
        UnityAction onClick)
    {
        DefaultUIButton template = ActionButtonTemplate != null ? ActionButtonTemplate : BackButtonTemplate;
        if (template == null)
        {
            return null;
        }

        DefaultUIButton btn = UnityEngine.Object.Instantiate(template, parent, worldPositionStays: false);
        btn.name = name;
        btn.gameObject.SetActive(true);

        var rt = (RectTransform)btn.transform;
        rt.sizeDelta = new Vector2(width > 0f ? width : rt.sizeDelta.x, height > 0f ? height : rt.sizeDelta.y);

        LayoutElement le = btn.gameObject.GetOrAddComponent<LayoutElement>();
        if (width > 0f)
        {
            le.preferredWidth = width;
            le.minWidth = width;
            le.flexibleWidth = 0f;
        }
        if (height > 0f)
        {
            le.preferredHeight = height;
            le.minHeight = height;
            le.flexibleHeight = 0f;
        }

        btn.SetIcon(null);
        btn.SetRawText(rawText, fontSize);
        btn.OnClick.RemoveAllListeners();
        if (onClick != null)
        {
            btn.OnClick.AddListener(onClick);
        }

        return btn;
    }

    internal DefaultUIButton CloneBackButton(Transform parent, string name, UnityAction onClick)
    {
        DefaultUIButton template = BackButtonTemplate != null ? BackButtonTemplate : ActionButtonTemplate;
        if (template == null)
        {
            return null;
        }

        DefaultUIButton btn = UnityEngine.Object.Instantiate(template, parent, worldPositionStays: false);
        btn.name = name;
        btn.gameObject.SetActive(true);
        btn.OnClick.RemoveAllListeners();
        if (onClick != null)
        {
            btn.OnClick.AddListener(onClick);
        }
        return btn;
    }

    internal ValidationInputField CloneSearchField(Transform parent, string placeholderText)
    {
        if (SearchInputTemplate == null)
        {
            return null;
        }

        ValidationInputField field = UnityEngine.Object.Instantiate(SearchInputTemplate, parent, worldPositionStays: false);
        field.name = "WorkshopSearchInput";
        field.gameObject.SetActive(true);
        field.onValueChanged.RemoveAllListeners();
        field.text = string.Empty;

        if (field.placeholder is TextMeshProUGUI ph)
        {
            ph.text = placeholderText;
        }

        Transform loader = field.transform.Find("Loader");
        if (loader != null)
        {
            loader.gameObject.SetActive(false);
        }

        return field;
    }

    internal (ScrollRect scrollRect, RectTransform viewport, RectTransform content) CreateScrollView(
        Transform parent,
        string name,
        bool addBottomPadding = true)
    {
        var scrollGo = new GameObject(name, typeof(RectTransform), typeof(ScrollRectNoDrag));
        scrollGo.transform.SetParent(parent, worldPositionStays: false);
        var scrollRt = (RectTransform)scrollGo.transform;
        Stretch(scrollRt);

        var scroll = scrollGo.GetComponent<ScrollRectNoDrag>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 32f;

        var viewportGo = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
        viewportGo.transform.SetParent(scrollRt, worldPositionStays: false);
        var viewportRt = (RectTransform)viewportGo.transform;
        Stretch(viewportRt, 0f, ScrollbarTemplate != null ? 12f : 4f, 0f, 0f);
        var maskImg = viewportGo.GetComponent<Image>();
        maskImg.color = new Color(1f, 1f, 1f, 0.01f);
        viewportGo.GetComponent<Mask>().showMaskGraphic = false;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        contentGo.transform.SetParent(viewportRt, worldPositionStays: false);
        var contentRt = (RectTransform)contentGo.transform;
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta = new Vector2(0f, 0f);

        VerticalLayoutGroup vlg = contentGo.AddComponent<VerticalLayoutGroup>();
        vlg.childControlWidth = true;
        vlg.childControlHeight = true;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.padding = new RectOffset(4, 4, 4, addBottomPadding ? 12 : 4);
        vlg.spacing = 4f;

        ContentSizeFitter csf = contentGo.AddComponent<ContentSizeFitter>();
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        scroll.viewport = viewportRt;
        scroll.content = contentRt;

        if (ScrollbarTemplate != null)
        {
            GameObject sbGo = UnityEngine.Object.Instantiate(ScrollbarTemplate, scrollRt, worldPositionStays: false);
            sbGo.name = "Scrollbar";
            sbGo.SetActive(true);
            var sbRt = (RectTransform)sbGo.transform;
            sbRt.anchorMin = new Vector2(1f, 0f);
            sbRt.anchorMax = new Vector2(1f, 1f);
            sbRt.pivot = new Vector2(1f, 0.5f);
            sbRt.anchoredPosition = Vector2.zero;
            sbRt.sizeDelta = new Vector2(8f, 0f);

            Scrollbar sb = sbGo.GetComponent<Scrollbar>();
            if (sb != null)
            {
                scroll.verticalScrollbar = sb;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
                scroll.verticalScrollbarSpacing = 4f;
            }
        }

        return (scroll, viewportRt, contentRt);
    }

    internal static void Stretch(RectTransform rt, float left = 0f, float right = 0f, float top = 0f, float bottom = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
        rt.localScale = Vector3.one;
    }

    internal static LayoutElement SetFixedLayout(GameObject go, float width = -1f, float height = -1f, float flexW = 0f, float flexH = 0f)
    {
        LayoutElement le = go.GetOrAddComponent<LayoutElement>();
        if (width >= 0f)
        {
            le.minWidth = width;
            le.preferredWidth = width;
        }
        if (height >= 0f)
        {
            le.minHeight = height;
            le.preferredHeight = height;
        }
        if (flexW >= 0f)
        {
            le.flexibleWidth = flexW;
        }
        if (flexH >= 0f)
        {
            le.flexibleHeight = flexH;
        }
        return le;
    }

    internal static Item CreateHideoutDisplayItem(
        MongoID templateId,
        string schemeId,
        EOwnerType ownerType,
        bool spawnedInSession,
        bool isEncoded)
    {
        if (!Singleton<ItemFactory>.Instantiated)
        {
            return null;
        }

        var key = new DisplayItemKey(schemeId, templateId, ownerType, spawnedInSession, isEncoded);
        EftUiTemplateCache cache = Instance;
        if (cache._displayItemCache.TryGetValue(key, out Item cached) && cached != null)
        {
            return cached;
        }

        Item item = Singleton<ItemFactory>.Instance.GetHideoutSchemeItem(
            templateId,
            schemeId,
            ownerType,
            spawnedInSession);

        if (item is IRecodableItem)
        {
            foreach (RecodableComponent comp in item.GetItemComponentsInChildren<RecodableComponent>())
            {
                comp.SetEncoded(isEncoded);
                break;
            }
        }

        if (item != null)
        {
            cache._displayItemCache[key] = item;
        }
        return item;
    }

    internal static void NormalizeItemFactoryRect(HideoutItemViewFactory factory, float boxSize, bool showInfo = false)
    {
        if (factory == null)
        {
            return;
        }

        var fRt = (RectTransform)factory.transform;
        fRt.anchorMin = Vector2.zero;
        fRt.anchorMax = Vector2.one;
        fRt.pivot = new Vector2(0.5f, 0.5f);
        fRt.offsetMin = Vector2.zero;
        fRt.offsetMax = Vector2.zero;
        fRt.localScale = Vector3.one;

        // Disable root VerticalLayoutGroup ONLY so it cannot clamp ItemViewContainer (minHeight=64) to y=-32 inside a 50px slot.
        // Keep ItemViewContainer's internal layout components enabled so HideoutItemView expands properly to 64x64.
        if (factory.GetComponent<LayoutGroup>() is LayoutGroup rootLg && rootLg.enabled)
        {
            rootLg.enabled = false;
        }
        if (factory.GetComponent<ContentSizeFitter>() is ContentSizeFitter rootCsf && rootCsf.enabled)
        {
            rootCsf.enabled = false;
        }

        if (factory._infoContainer != null)
        {
            factory._infoContainer.GetOrAddComponent<LayoutElement>().ignoreLayout = true;
            if (factory._infoContainer.transform is RectTransform infoRt)
            {
                infoRt.anchorMin = Vector2.zero;
                infoRt.anchorMax = Vector2.one;
                infoRt.pivot = new Vector2(0.5f, 0.5f);
                infoRt.offsetMin = Vector2.zero;
                infoRt.offsetMax = Vector2.zero;
                infoRt.localScale = Vector3.one;
            }
        }

        if (factory._errorBlock != null)
        {
            factory._errorBlock.GetOrAddComponent<LayoutElement>().ignoreLayout = true;
            factory._errorBlock.SetActive(false);
        }

        Transform container = factory.ItemView != null
            ? factory.ItemView.transform.parent
            : (factory._itemViewContainer != null ? factory._itemViewContainer : factory.transform.Find("ItemViewContainer"));
        float fitScale = Mathf.Min(1f, (boxSize - 2f) / 64f);
        if (container is RectTransform cRt)
        {
            cRt.anchorMin = new Vector2(0.5f, 0.5f);
            cRt.anchorMax = new Vector2(0.5f, 0.5f);
            cRt.pivot = new Vector2(0.5f, 0.5f);
            cRt.anchoredPosition = Vector2.zero;
            cRt.sizeDelta = new Vector2(64f, 64f);
            cRt.localScale = new Vector3(fitScale, fitScale, 1f);
        }

        if (factory.ItemView != null)
        {
            RectTransform ivRt = factory.ItemView.RectTransform;
            ivRt.anchorMin = Vector2.zero;
            ivRt.anchorMax = Vector2.one;
            ivRt.pivot = new Vector2(0.5f, 0.5f);
            ivRt.offsetMin = Vector2.zero;
            ivRt.offsetMax = Vector2.zero;
            ivRt.localScale = Vector3.one;
        }
    }
}
