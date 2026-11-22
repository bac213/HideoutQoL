using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.HandBook;
using EFT.Hideout;
using EFT.InventoryLogic;
using HideoutQoL.Client.Patches;
using UnityEngine;

namespace HideoutQoL.Client.UI;

internal enum WorkshopRecipeState
{
    Ready = 0,
    Producing = 1,
    Error = 2,
    Craftable = 3,
    MissingMaterials = 4,
    Locked = 5,
}

internal enum WorkshopStatusFilter
{
    All = 0,
    CraftableNow = 1,
    Favorites = 2,
    ActiveOrReady = 3,
}

internal enum WorkshopSortMode
{
    DefaultStatus = 0,
    ShortestTime = 1,
    FewestMissing = 2,
    NameAscending = 3,
}

/// <summary>
/// Central model indexing all multi-recipe Hideout crafting stations, Handbook 2-level categories,
/// real-time craftability (backed by <see cref="StashIndex"/>), and multi-facet search/filters.
/// </summary>
internal sealed class WorkshopCatalogModel
{
    internal const string OtherCategoryId = "__other__";

    internal sealed class StationInfo
    {
        public EAreaType AreaType;
        public AreaData Area;
        public ItemsProducerBase Producer;
        public string DisplayName;
        public int Level;
        public int TotalCount;
        public int CraftableCount;
        public int ProducingCount;
        public int ReadyCount;
    }

    internal sealed class CategoryNode
    {
        public string Id;
        public string Name;
        public string IconPath;
        public int Order;
        public int Count;
        public bool Expanded;
        public readonly List<CategoryNode> Children = new();
    }

    internal sealed class RecipeEntry
    {
        public ProductionScheme Scheme;
        public ItemsProducerBase Producer;
        public AreaData Area;
        public StationInfo Station;
        public string SchemeId;
        public MongoID EndProductId;
        public string DisplayName;
        public string ShortName;
        public string StationLabel;
        public int RequiredAreaLevel;
        public string RootCategoryId;
        public string RootCategoryName;
        public string RootCategoryIcon;
        public int RootCategoryOrder;
        public string SubCategoryId;
        public string SubCategoryName;
        public int SubCategoryOrder;

        // Live mutable state evaluated on refresh / 1s tick
        public WorkshopRecipeState State;
        public float Progress;
        public int RemainingSeconds;
        public int CraftDurationSeconds;
        public int MissingItemTypes;
        public int TotalItemRequirements;
        public bool IsToolMissing;
        public bool HasEnergyForCraft;
        public bool CanStartNow;
        public int StashOwnedResultCount;
        public ProducingItemController ProducingController;
    }

    // Special areas keep their bespoke vanilla screens (water collector, bitcoin, scav case, cultists)
    internal static bool IsCraftingStationType(EAreaType type)
    {
        return type switch
        {
            EAreaType.Workbench => true,
            EAreaType.MedStation => true,
            EAreaType.WaterCloset => true,
            EAreaType.Kitchen => true,
            EAreaType.IntelligenceCenter => true,
            EAreaType.ChristmasIllumination => true,
            _ => false,
        };
    }

    internal static bool IsWorkbenchEligibleArea(AreaData area)
    {
        if (area == null || area.CurrentLevel <= 0 || area.IsGuestData)
        {
            return false;
        }

        if (IsCraftingStationType(area.Template.Type))
        {
            return HasDiscreteProductionSchemes(area);
        }

        // Also support modded custom crafting areas if they use ItemsProducer with discrete schemes
        return HasDiscreteProductionSchemes(area)
            && area.Template.Type != EAreaType.ScavCase
            && area.Template.Type != EAreaType.BitcoinFarm
            && area.Template.Type != EAreaType.WaterCollector
            && area.Template.Type != EAreaType.AirFilteringUnit
            && area.Template.Type != EAreaType.CircleOfCultists
            && area.Template.Type != EAreaType.Generator;
    }

    private static bool HasDiscreteProductionSchemes(AreaData area)
    {
        ProductionController prodCtrl = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance.ProductionController
            : null;
        if (prodCtrl == null || area == null)
        {
            return false;
        }

        List<ItemsProducerBase> producers = prodCtrl.GetAreaProducers(area);
        if (producers == null)
        {
            return false;
        }

        for (int i = 0; i < producers.Count; i++)
        {
            if (producers[i] is ItemsProducer && producers[i] is not CultistsProducer)
            {
                if (producers[i].Schemes != null)
                {
                    foreach (BaseHideoutScheme raw in producers[i].Schemes.Values)
                    {
                        if (raw is ProductionScheme && !raw.continuous)
                        {
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private readonly struct ReqCacheKey : IEquatable<ReqCacheKey>
    {
        public readonly MongoID TemplateId;
        public readonly byte Flags;

        public ReqCacheKey(MongoID templateId, bool requireFir, bool requireFunctional, bool requireEncoded, bool checkEncoded)
        {
            TemplateId = templateId;
            Flags = (byte)((requireFir ? 1 : 0)
                | (requireFunctional ? 2 : 0)
                | (requireEncoded ? 4 : 0)
                | (checkEncoded ? 8 : 0));
        }

        public bool Equals(ReqCacheKey other) => TemplateId == other.TemplateId && Flags == other.Flags;
        public override bool Equals(object obj) => obj is ReqCacheKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                return (TemplateId.GetHashCode() * 397) ^ Flags;
            }
        }
    }

    private readonly List<StationInfo> _stations = new();
    private readonly Dictionary<EAreaType, StationInfo> _stationByType = new(8);
    private readonly List<RecipeEntry> _allRecipes = new(256);
    private readonly List<RecipeEntry> _producingRecipes = new(16);
    private readonly List<RecipeEntry> _filteredRecipes = new(256);
    private readonly List<CategoryNode> _categories = new();
    private readonly Dictionary<string, RecipeEntry> _bySchemeId = new(256, StringComparer.Ordinal);
    private readonly Dictionary<ReqCacheKey, int> _reqCountCache = new(256);

    internal IReadOnlyList<StationInfo> Stations => _stations;
    internal IReadOnlyList<RecipeEntry> AllRecipes => _allRecipes;
    internal IReadOnlyList<RecipeEntry> FilteredRecipes => _filteredRecipes;
    internal IReadOnlyList<CategoryNode> Categories => _categories;

    internal EAreaType? SelectedStationFilter { get; set; }
    internal string SelectedRootCategoryId { get; set; }
    internal string SelectedSubCategoryId { get; set; }
    internal WorkshopStatusFilter StatusFilter { get; set; } = WorkshopStatusFilter.All;
    internal int LevelFilter { get; set; } // 0 = All, 1/2/3 = specific area level
    internal bool HideLocked { get; set; }
    internal WorkshopSortMode SortMode { get; set; } = WorkshopSortMode.DefaultStatus;
    internal string SearchQuery { get; set; } = string.Empty;

    internal int TotalReadyCount { get; private set; }
    internal int TotalProducingCount { get; private set; }

    internal event Action OnDataUpdated;

    internal void RebuildFromHideout(AreaData[] areaDatas)
    {
        _stations.Clear();
        _stationByType.Clear();
        _allRecipes.Clear();
        _producingRecipes.Clear();
        _bySchemeId.Clear();

        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        ProductionController prodCtrl = hideout?.ProductionController;
        if (hideout == null || prodCtrl == null || areaDatas == null)
        {
            RebuildCategories();
            ApplyFiltersAndSort();
            return;
        }

        Handbook handbook = Singleton<Handbook>.Instantiated ? Singleton<Handbook>.Instance : null;

        for (int i = 0; i < areaDatas.Length; i++)
        {
            AreaData area = areaDatas[i];
            if (area == null || area.CurrentLevel <= 0)
            {
                continue;
            }

            List<ItemsProducerBase> producers = prodCtrl.GetAreaProducers(area);
            if (producers == null)
            {
                continue;
            }

            ItemsProducer primaryProducer = null;
            for (int p = 0; p < producers.Count; p++)
            {
                if (producers[p] is ItemsProducer ip && producers[p] is not CultistsProducer)
                {
                    primaryProducer = ip;
                    break;
                }
            }

            if (primaryProducer == null || primaryProducer.Schemes == null)
            {
                continue;
            }

            EAreaType areaType = area.Template.Type;
            if (!IsCraftingStationType(areaType) && (areaType == EAreaType.ScavCase || areaType == EAreaType.BitcoinFarm || areaType == EAreaType.WaterCollector || areaType == EAreaType.AirFilteringUnit))
            {
                continue;
            }

            var schemes = new List<ProductionScheme>();
            foreach (BaseHideoutScheme raw in primaryProducer.Schemes.Values)
            {
                if (raw is ProductionScheme ps && !ps.continuous)
                {
                    schemes.Add(ps);
                }
            }

            if (schemes.Count == 0)
            {
                continue;
            }

            var station = new StationInfo
            {
                AreaType = areaType,
                Area = area,
                Producer = primaryProducer,
                DisplayName = area.Template.Name,
                Level = area.CurrentLevel,
                TotalCount = schemes.Count,
            };
            _stations.Add(station);
            _stationByType[areaType] = station;

            for (int s = 0; s < schemes.Count; s++)
            {
                ProductionScheme scheme = schemes[s];
                RecipeEntry entry = CreateRecipeEntry(scheme, primaryProducer, area, station, handbook);
                _allRecipes.Add(entry);
                if (!string.IsNullOrEmpty(entry.SchemeId) && !_bySchemeId.ContainsKey(entry.SchemeId))
                {
                    _bySchemeId.Add(entry.SchemeId, entry);
                }
            }
        }

        EvaluateAllStates();
        RebuildCategories();
        ApplyFiltersAndSort();
    }

    internal void RefreshStatesOnly(bool rebuildCategories = false)
    {
        EvaluateAllStates();
        if (rebuildCategories)
        {
            RebuildCategories();
        }
        ApplyFiltersAndSort();
        OnDataUpdated?.Invoke();
    }

    internal bool TickActiveTimers()
    {
        bool anyChanged = false;
        for (int i = 0; i < _producingRecipes.Count; i++)
        {
            RecipeEntry entry = _producingRecipes[i];
            if (entry.State == WorkshopRecipeState.Producing && entry.Producer != null)
            {
                if (entry.Producer.ProducingItems.TryGetValue(entry.SchemeId, out ProducingItemController pic))
                {
                    float newProg = Mathf.Clamp01((float)pic.Progress);
                    int newRem = Math.Max(0, pic.EstimatedTimeInt);
                    if (!Mathf.Approximately(entry.Progress, newProg) || entry.RemainingSeconds != newRem)
                    {
                        entry.Progress = newProg;
                        entry.RemainingSeconds = newRem;
                        anyChanged = true;
                    }
                }
            }
        }
        return anyChanged;
    }

    internal RecipeEntry FindBySchemeId(string schemeId)
    {
        if (string.IsNullOrEmpty(schemeId))
        {
            return null;
        }
        _bySchemeId.TryGetValue(schemeId, out RecipeEntry entry);
        return entry;
    }

    internal StationInfo GetStation(EAreaType? areaType)
    {
        if (!areaType.HasValue)
        {
            return null;
        }
        _stationByType.TryGetValue(areaType.Value, out StationInfo st);
        return st;
    }

    internal void SetFiltersAndRefresh()
    {
        RebuildCategories();
        ApplyFiltersAndSort();
        OnDataUpdated?.Invoke();
    }

    private RecipeEntry CreateRecipeEntry(
        ProductionScheme scheme,
        ItemsProducerBase producer,
        AreaData area,
        StationInfo station,
        Handbook handbook)
    {
        string displayName = scheme.endProduct.LocalizedName();
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = scheme._id;
        }
        string shortName = scheme.endProduct.LocalizedShortName();
        if (string.IsNullOrEmpty(shortName))
        {
            shortName = displayName;
        }

        ResolveHandbookHierarchy(handbook, scheme.endProduct, out string rootId, out string rootName, out string rootIcon, out int rootOrder, out string subId, out string subName, out int subOrder);

        return new RecipeEntry
        {
            Scheme = scheme,
            Producer = producer,
            Area = area,
            Station = station,
            SchemeId = scheme._id,
            EndProductId = scheme.endProduct,
            DisplayName = displayName,
            ShortName = shortName,
            StationLabel = station.DisplayName,
            RequiredAreaLevel = Math.Max(1, scheme.Level),
            RootCategoryId = rootId,
            RootCategoryName = rootName,
            RootCategoryIcon = rootIcon,
            RootCategoryOrder = rootOrder,
            SubCategoryId = subId,
            SubCategoryName = subName,
            SubCategoryOrder = subOrder,
        };
    }

    private static void ResolveHandbookHierarchy(
        Handbook handbook,
        MongoID templateId,
        out string rootId,
        out string rootName,
        out string rootIcon,
        out int rootOrder,
        out string subId,
        out string subName,
        out int subOrder)
    {
        rootId = OtherCategoryId;
        rootName = "其他物品";
        rootIcon = null;
        rootOrder = int.MaxValue;
        subId = null;
        subName = null;
        subOrder = int.MaxValue;

        if (handbook?.StructuredItems == null)
        {
            return;
        }

        HandbookNode itemNode = handbook.StructuredItems[templateId];
        if (itemNode == null)
        {
            return;
        }

        // Collect category ancestors up to root (Parent == null)
        var chain = new List<HandbookNode>(4);
        HandbookNode cur = itemNode.Parent;
        int guard = 16;
        while (cur != null && guard-- > 0)
        {
            if (cur.Data != null && cur.Data.Type == ENodeType.Category)
            {
                chain.Add(cur);
            }
            cur = cur.Parent;
        }

        if (chain.Count == 0)
        {
            return;
        }

        HandbookNode top = chain[chain.Count - 1];
        rootId = top.Data.Id ?? OtherCategoryId;
        rootName = !string.IsNullOrEmpty(top.Data.Name) ? top.Data.Name.Localized() : rootId;
        rootIcon = top.Data.Icon;
        rootOrder = top.Data.Order;

        if (chain.Count >= 2)
        {
            HandbookNode directOrSecond = chain[chain.Count - 2];
            subId = directOrSecond.Data.Id;
            subName = !string.IsNullOrEmpty(directOrSecond.Data.Name) ? directOrSecond.Data.Name.Localized() : subId;
            subOrder = directOrSecond.Data.Order;
        }
    }

    private void EvaluateAllStates()
    {
        HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instantiated
            ? Singleton<HideoutRepresentation>.Instance
            : null;
        List<Item> allStashList = hideout?._allStashItems;
        bool energyOn = hideout?.EnergyController != null && hideout.EnergyController.IsEnergyGenerationOn;

        if (allStashList != null && HideoutQoLPlugin.PerfActive && HideoutQoLPlugin.StashIndexing.Value)
        {
            StashIndex.Instance.EnsureSynced(allStashList);
        }

        _reqCountCache.Clear();
        _producingRecipes.Clear();

        TotalReadyCount = 0;
        TotalProducingCount = 0;

        for (int s = 0; s < _stations.Count; s++)
        {
            StationInfo st = _stations[s];
            st.TotalCount = 0;
            st.CraftableCount = 0;
            st.ProducingCount = 0;
            st.ReadyCount = 0;
        }

        for (int i = 0; i < _allRecipes.Count; i++)
        {
            RecipeEntry r = _allRecipes[i];
            ProductionScheme scheme = r.Scheme;
            ItemsProducerBase producer = r.Producer;

            r.CraftDurationSeconds = producer != null
                ? Mathf.CeilToInt((float)producer.CalculateProductionTime(scheme))
                : Mathf.CeilToInt(scheme.productionTime);

            r.StashOwnedResultCount = CountNonLockedStashItemsCached(allStashList, r.EndProductId, requireFir: false, requireFunctional: false, requireEncoded: false, checkEncoded: false);

            bool isReady = producer != null && producer.CompleteItemsStorage.GetItemsCount(r.SchemeId) > 0;
            ProducingItemController producingItem = null;
            bool isProducing = producer != null && producer.ProducingItems.TryGetValue(r.SchemeId, out producingItem);
            bool isError = producer != null && producer.GetProductionState(r.SchemeId) == EProductionState.Error;

            r.ProducingController = producingItem;
            r.Progress = isProducing && producingItem != null ? Mathf.Clamp01((float)producingItem.Progress) : 0f;
            r.RemainingSeconds = isProducing && producingItem != null ? Math.Max(0, producingItem.EstimatedTimeInt) : 0;

            EvaluateRequirements(r, allStashList);

            r.HasEnergyForCraft = !scheme.needFuelForAllProductionTime || energyOn;
            bool producerCanAcceptMore = producer != null && producer.CanStartByProductionCount();
            bool hasErrorsOnProducer = producer != null && producer.GetErrorsCountRaw() > 0;

            r.CanStartNow = !scheme.locked
                && !isReady
                && !isProducing
                && !isError
                && r.MissingItemTypes == 0
                && !r.IsToolMissing
                && r.HasEnergyForCraft
                && producerCanAcceptMore
                && !hasErrorsOnProducer;

            if (isReady)
            {
                r.State = WorkshopRecipeState.Ready;
                TotalReadyCount++;
            }
            else if (isError)
            {
                r.State = WorkshopRecipeState.Error;
            }
            else if (isProducing)
            {
                r.State = WorkshopRecipeState.Producing;
                TotalProducingCount++;
                _producingRecipes.Add(r);
            }
            else if (scheme.locked)
            {
                r.State = WorkshopRecipeState.Locked;
            }
            else if (r.MissingItemTypes == 0 && !r.IsToolMissing)
            {
                r.State = WorkshopRecipeState.Craftable;
            }
            else
            {
                r.State = WorkshopRecipeState.MissingMaterials;
            }

            StationInfo st = r.Station;
            if (st != null)
            {
                st.TotalCount++;
                if (r.State == WorkshopRecipeState.Ready) st.ReadyCount++;
                else if (r.State == WorkshopRecipeState.Producing) st.ProducingCount++;
                else if (r.State == WorkshopRecipeState.Craftable) st.CraftableCount++;
            }
        }
    }

    private void EvaluateRequirements(RecipeEntry entry, List<Item> allStashList)
    {
        entry.MissingItemTypes = 0;
        entry.TotalItemRequirements = 0;
        entry.IsToolMissing = false;

        if (!entry.Scheme.HasRequirements)
        {
            return;
        }

        Requirement[] reqs = entry.Scheme.requirements;
        for (int i = 0; i < reqs.Length; i++)
        {
            if (reqs[i] is not ItemRequirement ir)
            {
                continue;
            }

            entry.TotalItemRequirements++;
            int owned = CountMatchingRequirementItemsCached(allStashList, ir);
            if (owned < ir.IntCount)
            {
                entry.MissingItemTypes++;
                if (ir is ToolRequirement)
                {
                    entry.IsToolMissing = true;
                }
            }
        }
    }

    private int CountMatchingRequirementItemsCached(List<Item> allStashList, ItemRequirement ir)
    {
        if (ir == null || string.IsNullOrEmpty(ir.TemplateId))
        {
            return 0;
        }

        bool checkEncoded = ir.Item is IRecodableItem;
        return CountNonLockedStashItemsCached(
            allStashList,
            (MongoID)ir.TemplateId,
            ir.IsSpawnedInSession,
            ir.OnlyFunctional,
            ir.IsEncoded,
            checkEncoded);
    }

    private int CountNonLockedStashItemsCached(
        List<Item> allStashList,
        MongoID templateId,
        bool requireFir,
        bool requireFunctional,
        bool requireEncoded,
        bool checkEncoded)
    {
        if (allStashList == null || string.IsNullOrEmpty(templateId))
        {
            return 0;
        }

        var key = new ReqCacheKey(templateId, requireFir, requireFunctional, requireEncoded, checkEncoded);
        if (_reqCountCache.TryGetValue(key, out int cached))
        {
            return cached;
        }

        int total = CountNonLockedStashItems(allStashList, templateId, requireFir, requireFunctional, requireEncoded, checkEncoded);
        _reqCountCache[key] = total;
        return total;
    }

    internal static int CountMatchingRequirementItems(List<Item> allStashList, ItemRequirement ir)
    {
        if (ir == null || string.IsNullOrEmpty(ir.TemplateId))
        {
            return 0;
        }

        bool checkEncoded = ir.Item is IRecodableItem;
        return CountNonLockedStashItems(
            allStashList,
            (MongoID)ir.TemplateId,
            ir.IsSpawnedInSession,
            ir.OnlyFunctional,
            ir.IsEncoded,
            checkEncoded);
    }

    internal static int CountNonLockedStashItems(
        List<Item> allStashList,
        MongoID templateId,
        bool requireFir,
        bool requireFunctional,
        bool requireEncoded,
        bool checkEncoded)
    {
        if (allStashList == null || string.IsNullOrEmpty(templateId))
        {
            return 0;
        }

        List<Item> itemsToScan = null;
        if (HideoutQoLPlugin.PerfActive
            && HideoutQoLPlugin.StashIndexing.Value
            && StashIndex.Instance.TryGetBucket(allStashList, templateId, out List<Item> bucket))
        {
            itemsToScan = bucket;
        }
        else if (!HideoutQoLPlugin.PerfActive || !HideoutQoLPlugin.StashIndexing.Value)
        {
            itemsToScan = allStashList;
        }

        if (itemsToScan == null)
        {
            return 0;
        }

        int total = 0;
        for (int i = 0; i < itemsToScan.Count; i++)
        {
            Item item = itemsToScan[i];
            if (item == null || item.TemplateId != templateId)
            {
                continue;
            }
            if (item.PinLockState == EItemPinLockState.Locked)
            {
                continue;
            }
            if (requireFir && !item.SpawnedInSession)
            {
                continue;
            }
            if (requireFunctional && item is Weapon weapon && weapon.MissingVitalParts.Any())
            {
                continue;
            }
            if (checkEncoded && item is IRecodableItem recodable && recodable.IsEncoded() != requireEncoded)
            {
                continue;
            }
            total += item.StackObjectsCount;
        }

        return total;
    }

    private void RebuildCategories()
    {
        var previousExpanded = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _categories.Count; i++)
        {
            if (_categories[i].Expanded)
            {
                previousExpanded.Add(_categories[i].Id);
            }
        }

        _categories.Clear();
        var rootMap = new Dictionary<string, CategoryNode>(StringComparer.Ordinal);
        var subMaps = new Dictionary<string, Dictionary<string, CategoryNode>>(StringComparer.Ordinal);

        for (int i = 0; i < _allRecipes.Count; i++)
        {
            RecipeEntry r = _allRecipes[i];
            if (SelectedStationFilter.HasValue && r.Area.Template.Type != SelectedStationFilter.Value)
            {
                continue;
            }
            if (HideLocked && r.Scheme.locked)
            {
                continue;
            }

            if (!rootMap.TryGetValue(r.RootCategoryId, out CategoryNode root))
            {
                root = new CategoryNode
                {
                    Id = r.RootCategoryId,
                    Name = r.RootCategoryName,
                    IconPath = r.RootCategoryIcon,
                    Order = r.RootCategoryOrder,
                    Expanded = previousExpanded.Contains(r.RootCategoryId)
                        || string.Equals(SelectedRootCategoryId, r.RootCategoryId, StringComparison.Ordinal),
                };
                rootMap[r.RootCategoryId] = root;
                subMaps[r.RootCategoryId] = new Dictionary<string, CategoryNode>(StringComparer.Ordinal);
                _categories.Add(root);
            }
            root.Count++;

            if (!string.IsNullOrEmpty(r.SubCategoryId) && !string.Equals(r.SubCategoryId, r.RootCategoryId, StringComparison.Ordinal))
            {
                Dictionary<string, CategoryNode> subDict = subMaps[r.RootCategoryId];
                if (!subDict.TryGetValue(r.SubCategoryId, out CategoryNode sub))
                {
                    sub = new CategoryNode
                    {
                        Id = r.SubCategoryId,
                        Name = r.SubCategoryName,
                        Order = r.SubCategoryOrder,
                    };
                    subDict[r.SubCategoryId] = sub;
                    root.Children.Add(sub);
                }
                sub.Count++;
            }
        }

        _categories.Sort((a, b) =>
        {
            int cmp = a.Order.CompareTo(b.Order);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        for (int i = 0; i < _categories.Count; i++)
        {
            _categories[i].Children.Sort((a, b) =>
            {
                int cmp = a.Order.CompareTo(b.Order);
                return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        // Clear stale category selections if they no longer exist under the chosen station
        if (!string.IsNullOrEmpty(SelectedRootCategoryId) && !rootMap.ContainsKey(SelectedRootCategoryId))
        {
            SelectedRootCategoryId = null;
            SelectedSubCategoryId = null;
        }
        else if (!string.IsNullOrEmpty(SelectedSubCategoryId)
            && !string.IsNullOrEmpty(SelectedRootCategoryId)
            && subMaps.TryGetValue(SelectedRootCategoryId, out var subs)
            && !subs.ContainsKey(SelectedSubCategoryId))
        {
            SelectedSubCategoryId = null;
        }
    }

    private void ApplyFiltersAndSort()
    {
        _filteredRecipes.Clear();

        string query = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery.Trim();
        bool includeMaterials = HideoutQoLPlugin.SearchIncludeMaterials == null || HideoutQoLPlugin.SearchIncludeMaterials.Value;

        for (int i = 0; i < _allRecipes.Count; i++)
        {
            RecipeEntry r = _allRecipes[i];

            if (SelectedStationFilter.HasValue && r.Area.Template.Type != SelectedStationFilter.Value)
            {
                continue;
            }
            if (!string.IsNullOrEmpty(SelectedRootCategoryId) && !string.Equals(r.RootCategoryId, SelectedRootCategoryId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(SelectedSubCategoryId) && !string.Equals(r.SubCategoryId, SelectedSubCategoryId, StringComparison.Ordinal))
            {
                continue;
            }
            if (HideLocked && r.Scheme.locked)
            {
                continue;
            }
            if (LevelFilter > 0 && r.RequiredAreaLevel != LevelFilter)
            {
                continue;
            }

            switch (StatusFilter)
            {
                case WorkshopStatusFilter.CraftableNow:
                    if (r.State != WorkshopRecipeState.Craftable && r.State != WorkshopRecipeState.Ready)
                    {
                        continue;
                    }
                    break;
                case WorkshopStatusFilter.Favorites:
                    if (!r.Scheme.FavoriteScheme)
                    {
                        continue;
                    }
                    break;
                case WorkshopStatusFilter.ActiveOrReady:
                    if (r.State != WorkshopRecipeState.Producing && r.State != WorkshopRecipeState.Ready && r.State != WorkshopRecipeState.Error)
                    {
                        continue;
                    }
                    break;
            }

            if (query != null)
            {
                string haystack = HideoutInteractionPatch.GetOrBuildSearchText(r.Scheme, includeMaterials);
                if (haystack.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
            }

            _filteredRecipes.Add(r);
        }

        _filteredRecipes.Sort(CompareRecipes);
    }

    private int CompareRecipes(RecipeEntry a, RecipeEntry b)
    {
        // Ready / Error / Producing always float cleanly to top in DefaultStatus
        switch (SortMode)
        {
            case WorkshopSortMode.ShortestTime:
                int timeCmp = a.CraftDurationSeconds.CompareTo(b.CraftDurationSeconds);
                return timeCmp != 0 ? timeCmp : string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);

            case WorkshopSortMode.FewestMissing:
                int readyCmp = IsActionableTop(a.State).CompareTo(IsActionableTop(b.State));
                if (readyCmp != 0) return readyCmp;
                int missCmp = a.MissingItemTypes.CompareTo(b.MissingItemTypes);
                if (missCmp != 0) return missCmp;
                return a.Scheme.FavoriteIndex.CompareTo(b.Scheme.FavoriteIndex);

            case WorkshopSortMode.NameAscending:
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);

            default:
                int stateA = GetSortBucket(a.State);
                int stateB = GetSortBucket(b.State);
                if (stateA != stateB) return stateA.CompareTo(stateB);
                int favCmp = a.Scheme.FavoriteIndex.CompareTo(b.Scheme.FavoriteIndex);
                if (favCmp != 0) return favCmp;
                int lvlCmp = a.RequiredAreaLevel.CompareTo(b.RequiredAreaLevel);
                if (lvlCmp != 0) return lvlCmp;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private static int GetSortBucket(WorkshopRecipeState state)
    {
        return state switch
        {
            WorkshopRecipeState.Ready => 0,
            WorkshopRecipeState.Error => 1,
            WorkshopRecipeState.Producing => 2,
            WorkshopRecipeState.Craftable => 3,
            WorkshopRecipeState.MissingMaterials => 4,
            _ => 5,
        };
    }

    private static int IsActionableTop(WorkshopRecipeState state)
    {
        return state switch
        {
            WorkshopRecipeState.Ready => 0,
            WorkshopRecipeState.Producing => 1,
            WorkshopRecipeState.Craftable => 2,
            WorkshopRecipeState.MissingMaterials => 3,
            _ => 4,
        };
    }
}
