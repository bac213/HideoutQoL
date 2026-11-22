# HideoutQoL (SPT 4.1.6 client mod) — working notes

Client-only BepInEx plugin targeting `net472` for SPT 4.1.6 (EFT `0.16.9.40743`), focused on Hideout QoL & performance fixes.

## Scope (Phase 1 & 1.5 — implemented)

1. **Hideout crafting & stash performance**
   - `Patches/RequirementViewPatch.cs`:
     - `HideoutProductionRequirementView.Show`: updates counts, fulfilled status, tool border, and error state in-place when the same requirement is already bound, eliminating `ItemView` teardown/recreation, scheme item cloning, and duplicate `UI.AddDisposable` leaks.
     - `HideoutItemViewFactory.SetInfoLayout`: reuses an existing matching `HorizontalOrVerticalLayoutGroup` instead of calling `DestroyImmediate` + `AddComponent` on every `Show`.
   - `StashIndex.cs` + `Patches/StashIndexPatch.cs`:
     - Mirrors `HideoutRepresentation._allStashItems` into an O(1) `TemplateId -> List<Item>` bucket index and O(1) swap-remove index map.
     - Accelerates `HideoutRepresentation.AddItemEventHandler` / `RemoveItemEventHandler` / `RefreshItemEvent`, `ItemRequirement.Test`, `CG_GetItemReferences`, and `GetAvailableItemsByFilter<Item>`.
     - Removes full-stash LINQ sorting in `ScavCaseView.UpdateView`.
     - Hoists `BitcoinProducer.AvailableItems` out of the GPU installation loop in `BitcoinProducer.InstallSupplies`.
   - `Patches/VirtualizedProduceList.cs` + `Patches/ProductionFlowPatch.cs`:
     - Viewport-virtualizes `ProductionPanel.ShowProduceView` with top/bottom spacers, scroll hysteresis, pinned in-flight `GettingItems` cards, and cross-open `GlobalProduceCardPool`.
     - Unsubscribes the duplicate `Producer.OnProduceStatusChanged += UpdateView` listener on `ProduceView`.
     - Coalesces `ProductionPanel.UpdateViews` and `ItemChangedHandler` to at most one flush per frame in `LateUpdate` via `ProductionPanelCoalescer`, and skips the redundant `UpdateViews()` immediately after `ShowProduceView`.
     - Makes `ProductionPanel.UpdateOrder` drive the virtualized list or no-op when sibling order is already sorted.
     - Instantiates `AreaScreenSubstrate.CreateViewList` sub-panels in 1 pass and accelerates substrate fade-in to 60ms.
     - Throttles `PermanentProductionView.Update` (Water Collector / Booze Generator) to 0.5s.
     - Prevents `AreaScreenSubstrate.method_5` from tearing down the active `Current` crafting view when `StatusUpdated` flips upgrade readiness without changing level or visible panel set.
   - `Patches/HideoutUiOptimizationsPatch.cs`:
     - Unregisters `HideoutItemView` from throwaway `ItemController.Views`, deduplicates `SetCounterText`, replaces `SetMonospaceText` double `GetTextInfo()` calls with pure-C# `<mspace>` formatting, and fixes `_expectedTimePanel` reactivation on idle `ProduceView`.
   - `Patches/WishlistCachePatch.cs`:
     - Caches the Hideout auto-wishlist template set (`WishlistManager.IsInHideoutQolWishlist` / `GetHideoutQolItems`), invalidated on Hideout state changes and option changes (2s max TTL fallback).
   - `Patches/BundlePreloadPatch.cs`:
     - Trims `TarkovApplication.HideoutController.method_4` (`PrepareProductionSchemesBundles`) from all ~220 production end products to only currently producing/completed crafts, and loads newly completed craft prefab bundles on demand in `HideoutArea.SpawnProducedItems` (released on `UnloadHideout`).

2. **Hideout interaction QoL & Seamless Lobby Backdrop**
   - `Patches/HideoutInteractionPatch.cs`:
     - Removes the hardcoded 0.5s delay in `HideoutScreenOverlay.AreaSelected`.
     - Makes the Generator toggle button cooldown configurable (default 0s vs vanilla 10s), live-updated via F12.
     - Enhances `ProductionPanel.Search` with configurable debounce, cached localized names, and optional matching against required material/tool names.
   - `Patches/HideoutBackdropPatch.cs`:
     - Forces early `StartLoadHideoutMap` preload and silently initializes `HideoutGame` + `HideoutAwake` in the background on entering the lobby (suppressing `HideoutLoadingScreen`, restoring `GCEnabled = true`).
     - Keeps `PlayerCameraController` and `HideoutCameraController` alive in top-down overview behind a translucent `MenuScreen` overlay (and compatible lobby sub-screens) for instant `<50ms` Main Menu ↔ Hideout switching.

3. **Crafting Workshop Studio (`v0.1.0`, vanilla-styled in `v0.2.0`)**
   - `UI/EftUiTemplateCache.cs`: Extracts live EFT templates, TMP font (`Jovanny Lemonad - Bender`), sprites, shadows, hairlines, buttons, dropdowns, search input, `HideoutItemViewFactory`, and `CameraImage` from `CommonUI.Instance`; caches read-only display `Item`s (`ClearDisplayItemCache`) and normalizes `HideoutItemViewFactory` rects (`NormalizeItemFactoryRect`).
   - `UI/WorkshopCatalogModel.cs`: Indexes multi-recipe Hideout crafting stations, 2-level `Handbook` categories, `StashIndex`-accelerated craftability with per-refresh `_reqCountCache`, active producing fast-list (`TickActiveTimers`), multi-facet filters, and sort comparator.
   - `UI/WorkshopGridView.cs`: Row-virtualized multi-column recipe grid (`WorkshopRecipeTile`) with sliding-window row preservation, identity-reused `HideoutItemView`s, and snapshot-guarded dynamic updates.
   - `UI/WorkshopInspectorPane.cs`: Right inspector with direct interactive 3D `CameraImage` + `WeaponPreview` (or normalized 2D fallback), unclipped product header/weight, normalized `HideoutItemViewFactory` material rows with `[反查]` reverse ingredient search + `[跳蚤]` flea buy, and adaptive craft dock (`开始/领完再造/取消/购齐缺料`).
   - `UI/WorkshopFx.cs` (`v0.2.0`): `UiFader` (CanvasGroup fade, per-frame step clamped to 1/30s so the frame doing the heavy work cannot finish the fade), `SmoothWheelScroll` + `ScrollGlide` (wheel receiver must stay enabled — the EventSystem skips disabled handlers — while the glide driver only runs mid-glide), `HoverTint`. All gated by `WorkshopAnimations`.
   - Styling values (colors, type scale, row heights, sprites `main_part_gradient` / `border_shadow` / `line_fader_white` / `border_generic` / `info_window_back` / `bottom_left_corner`) are taken from the vanilla `HandbookScreen` (scene `level44`), `AreaScreenSubstrate` and `ProduceView` prefabs (`sharedassets44.assets`); sprites are borrowed at runtime in `EftUiTemplateCache` and every use falls back to flat colors if missing.
   - `WorkshopGridView` builds item icons through a 3ms/frame queue (`EnqueueIcon` / `DrainIconQueue`); text binds immediately.
   - `UI/HideoutWorkshopScreen.cs`: Full 3-column Tarkov-native Crafting Studio (`UIScreen`) with station switcher, generator quick toggle + fuel countdown, Handbook category tree, multi-facet filters, and single-frame event coalescing.
   - `Patches/WorkshopUiPatch.cs`: Hooks `HideoutScreenOverlay` to replace built crafting station clicks with `HideoutWorkshopScreen`, adds `[工坊总览]`, handles `Escape`/scroll/WASD input, clears display cache on Hideout exit, strips `EFilterType.OfferOwnerType` (`AnyOwnerType`) on Hideout `Buy` Ragfair jumps (`ProduceView.BuyMaterials` / `WorkshopInspectorPane`), and allows jumping to vanilla `AreaScreenSubstrate` via `[设施详情/升级]`.

4. **Diagnostics**
   - `Patches/HideoutProfilerPatch.cs`:
     - Granular wall-clock, GC, icon-cache, stash-event, and >=80ms frame-hitch profiler. Since `v0.2.0` its patches live on a separate Harmony instance (`<guid>.profiler`) and are installed/removed live with `PerfLogging`; when off, the log shows `profiler dormant`.

## Planned (Phase 2 — split to standalone mod)
- Phase 2 (in-raid/out-of-raid item inspection & tooltip enhancement showing stash/carried FiR & non-FiR counts and active/future quest + hideout upgrade requirements) has been split out into the standalone `E:/SPT/raid-intel-main` (`RaidIntel`) server + client mod.

## Build & Deploy

```bash
cd E:/SPT/hideout-qol-main
dotnet build -c Release HideoutQoL.Client/HideoutQoL.Client.csproj
# Ensure game is closed before overwriting the DLL
cp HideoutQoL.Client/bin/Release/HideoutQoL.Client.dll E:/SPT/BepInEx/plugins/
```

Verify in `E:/SPT/BepInEx/LogOutput.log`:
- `HideoutQoL 0.2.0 loaded`
- Ten `HideoutQoL: patched ...` lines with zero errors.
- Optional perf diagnostics: enable **F12 → HideoutQoL → 9. 调试 → 输出性能统计日志**.
