using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HideoutQoL.Client.Patches;

namespace HideoutQoL.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class HideoutQoLPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.spt.hideoutqol.client";
    public const string PluginName = "HideoutQoL";
    public const string PluginVersion = "0.2.0";

    internal static ManualLogSource Log { get; private set; }
    internal static HideoutQoLPlugin Instance { get; private set; }

    // 1. Performance
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> VirtualizeCraftList;
    internal static ConfigEntry<bool> InPlaceRequirementUpdate;
    internal static ConfigEntry<bool> StashIndexing;
    internal static ConfigEntry<bool> EventCoalescing;
    internal static ConfigEntry<bool> WishlistCache;
    internal static ConfigEntry<bool> PermanentViewThrottle;
    internal static ConfigEntry<bool> OnDemandCraftBundles;

    // 2. Interaction QoL
    internal static ConfigEntry<bool> SkipAreaSelectDelay;
    internal static ConfigEntry<int> GeneratorCooldownSeconds;
    internal static ConfigEntry<bool> SearchEnhancements;
    internal static ConfigEntry<bool> SearchIncludeMaterials;
    internal static ConfigEntry<int> SearchDebounceMs;

    // 3. Seamless Lobby & Backdrop
    internal static ConfigEntry<bool> EnableHideoutLobbyBackdrop;
    internal static ConfigEntry<bool> WaitForHideoutBeforeMenu;
    internal static ConfigEntry<float> MenuBackdropDimAlpha;
    internal static ConfigEntry<float> MenuUiAlpha;
    internal static ConfigEntry<bool> MenuBackdropBlur;
    internal static ConfigEntry<bool> HideoutAudioInMainMenu;
    internal static ConfigEntry<bool> KeepHideoutBackdropInSubScreens;

    // 4. Crafting Workshop Studio UI
    internal static ConfigEntry<bool> EnableWorkshopUi;
    internal static ConfigEntry<bool> WorkshopOpenOnAreaClick;
    internal static ConfigEntry<bool> WorkshopShow3DPreview;
    internal static ConfigEntry<int> WorkshopGridColumns;
    internal static ConfigEntry<bool> WorkshopHideLockedByDefault;
    internal static ConfigEntry<bool> WorkshopRagfairIncludeAllOwners;
    internal static ConfigEntry<bool> WorkshopAnimations;

    // 9. Diagnostics
    internal static ConfigEntry<bool> PerfLogging;

    internal static bool PerfActive => Enabled.Value;

    private Harmony _harmony;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        BindConfig();

        _harmony = new Harmony(PluginGuid);
        ApplyPatches();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded");
    }

    private void BindConfig()
    {
        var t = new ConfigText(Config);

        const string catPerf = "1. 藏身处性能优化";
        Enabled = t.Bind(
            "1. Performance", "Enabled", true,
            catPerf, "总开关",
            "启用或关闭本模组的所有藏身处优化与交互改善功能。");
        VirtualizeCraftList = t.Bind(
            "1. Performance", "VirtualizeCraftList", true,
            catPerf, "制造列表虚拟滚动（核心）",
            "只实例化视口可见的 ~10 张配方卡片并在滚动时复用，彻底消除工作台/厨房/卫生间打开时的 3-4 秒 Canvas 重建卡顿及收取/开始制造时的全表卡顿。");
        InPlaceRequirementUpdate = t.Bind(
            "1. Performance", "InPlaceRequirementUpdate", true,
            catPerf, "材料格原地更新",
            "刷新配方时复用已有材料图标，跳过销毁重建 ItemView 与反复拆装 LayoutGroup。");
        StashIndexing = t.Bind(
            "1. Performance", "StashIndexing", true,
            catPerf, "仓库物品索引加速",
            "按物品 TemplateId 建索引并用 O(1) 增删替代全仓线性扫描；同步优化 Scav 宝箱排序、比特币矿场一键装卡及主仓库整理卡顿。");
        EventCoalescing = t.Bind(
            "1. Performance", "EventCoalescing", true,
            catPerf, "事件去重与单帧合批",
            "消除重复订阅与打开列表后的冗余全刷；同一帧内多次触发的列表刷新合并为 1 次；阻止材料变动误炸当前制造面板。");
        WishlistCache = t.Bind(
            "1. Performance", "WishlistCache", true,
            catPerf, "藏身处愿望单缓存",
            "缓存藏身处自动愿望单结果，避免每创建一个物品图标都重新遍历全藏身处。");
        PermanentViewThrottle = t.Bind(
            "1. Performance", "PermanentViewThrottle", true,
            catPerf, "滤水器/酿酒刷新节流",
            "将滤水器与酿酒器的倒计时文本更新节流至 0.5 秒一次，消除每帧字符串 GC 与 TMP 重建。");
        OnDemandCraftBundles = t.Bind(
            "1. Performance", "OnDemandCraftBundles", true,
            catPerf, "成品 3D 模型按需加载",
            "进入藏身处时仅预加载正在制造或已完成的成品模型 Bundle（不再一次性预加载全部 220 个配方），新完成时按需异步加载。");

        const string catQol = "2. 藏身处交互改善";
        SkipAreaSelectDelay = t.Bind(
            "2. Interaction", "SkipAreaSelectDelay", true,
            catQol, "消除切换设施 0.5 秒延迟",
            "点击切换工作台/厨房/卫生间等设施时立即打开面板，去掉原版硬编码的 0.5 秒等待。");
        GeneratorCooldownSeconds = t.Bind(
            "2. Interaction", "GeneratorCooldownSeconds", 0,
            catQol, "发电机开关冷却 (秒)",
            "发电机开关按钮的冷却时间（原版为 10 秒，0 表示无冷却）。",
            ConfigText.Range(0, 10));
        SearchEnhancements = t.Bind(
            "2. Interaction", "SearchEnhancements", true,
            catQol, "制造搜索框优化",
            "缓存配方名称并对输入做轻量防抖，减少打字时的界面卡顿。");
        SearchIncludeMaterials = t.Bind(
            "2. Interaction", "SearchIncludeMaterials", true,
            catQol, "搜索同时匹配所需材料",
            "在制造列表搜索框输入关键词时，同时匹配成品名和所需材料/工具名（需开启制造搜索框优化）。");
        SearchDebounceMs = t.Bind(
            "2. Interaction", "SearchDebounceMs", 120,
            catQol, "搜索输入防抖 (ms)",
            "输入停顿多少毫秒后再执行筛选（清空搜索框总是立即生效）。",
            ConfigText.Range(0, 500));

        const string catBackdrop = "3. 藏身处背景与无感加载";
        EnableHideoutLobbyBackdrop = t.Bind(
            "3. Seamless Backdrop", "EnableHideoutLobbyBackdrop", true,
            catBackdrop, "主菜单实时藏身处俯视背景 + 无感预加载",
            "进入大厅后在后台静默加载并初始化完整藏身处，主菜单与兼容子界面后方直接呈现半透明藏身处实时俯视图，点击进入藏身处零等待。");
        WaitForHideoutBeforeMenu = t.Bind(
            "3. Seamless Backdrop", "WaitForHideoutBeforeMenu", false,
            catBackdrop, "等待藏身处就绪后再就绪大厅（方案 B）",
            "关闭（推荐，方案 A）：立即打开主菜单，藏身处后台静默加载完成后平滑淡入背景。开启（方案 B）：等待藏身处同步初始化完成。");
        MenuBackdropDimAlpha = t.Bind(
            "3. Seamless Backdrop", "MenuBackdropDimAlpha", 0.45f,
            catBackdrop, "主菜单暗角遮罩透明度",
            "主菜单按钮背后半透明深色遮罩的浓度（0 = 完全透出藏身处，0.85 = 深色暗化）。",
            ConfigText.Range(0f, 0.85f));
        MenuUiAlpha = t.Bind(
            "3. Seamless Backdrop", "MenuUiAlpha", 0.95f,
            catBackdrop, "主菜单 UI 透明度",
            "主菜单面板与按钮组的整体不透明度（0.5 = 半透明，1.0 = 完全不透明）。",
            ConfigText.Range(0.5f, 1f));
        MenuBackdropBlur = t.Bind(
            "3. Seamless Backdrop", "MenuBackdropBlur", true,
            catBackdrop, "主菜单背景轻微景深模糊",
            "停留在主菜单界面时对后方藏身处俯视镜头施加轻微相机模糊，进入藏身处或子界面后自动取消。");
        HideoutAudioInMainMenu = t.Bind(
            "3. Seamless Backdrop", "HideoutAudioInMainMenu", false,
            catBackdrop, "主菜单保留藏身处环境音",
            "关闭（推荐）：主菜单仅播放大厅音乐，静音发电机与藏身处环境噪音；开启：在主菜单即播放藏身处环境音。");
        KeepHideoutBackdropInSubScreens = t.Bind(
            "3. Seamless Backdrop", "KeepHideoutBackdropInSubScreens", true,
            catBackdrop, "仓库/跳蚤市场等子界面保留背景",
            "开启后仓库、商人界面、跳蚤市场、配装、设置等深色大厅子界面保留藏身处俯视背景；匹配/重连/局内自动切回原版背景。");

        const string catWorkshop = "4. 工坊界面重构";
        EnableWorkshopUi = t.Bind(
            "4. Workshop UI", "EnableWorkshopUi", true,
            catWorkshop, "启用大图鉴工作台界面",
            "开启后提供三栏藏身处全站工坊工作台窗口（设施/分类树、多维筛选与排序网格、3D预览与材料反查面板）。");

        WorkshopOpenOnAreaClick = t.Bind(
            "4. Workshop UI", "WorkshopOpenOnAreaClick", true,
            catWorkshop, "点击制作设施直接打开工作台",
            "开启后点击已建造的多配方制作设施（工作台/医疗站/厕所/营养部/情报中心等）直接打开新工坊界面，替代原版右侧窄抽屉；可在工作台顶部随时点【设施详情/升级】返回原抽屉。");

        WorkshopShow3DPreview = t.Bind(
            "4. Workshop UI", "WorkshopShow3DPreview", true,
            catWorkshop, "详情栏显示 3D 检视预览",
            "开启后在右侧制作详情栏渲染原版可旋转 3D 检视面板；关闭可改用轻量 2D 图标头以进一步节省显存与耗时。");

        WorkshopGridColumns = t.Bind(
            "4. Workshop UI", "WorkshopGridColumns", 2,
            catWorkshop, "配方网格列数",
            "控制工作台中央区域每行显示的配方卡片列数（1~3列，默认2列；2K/4K大屏可设3列）。",
            new AcceptableValueRange<int>(1, 3));

        WorkshopHideLockedByDefault = t.Bind(
            "4. Workshop UI", "WorkshopHideLockedByDefault", false,
            catWorkshop, "默认隐藏未解锁配方",
            "开启后打开工作台时默认勾选【隐藏未解锁】，只显示当前设施等级与任务允许制作的配方。");

        WorkshopRagfairIncludeAllOwners = t.Bind(
            "4. Workshop UI", "WorkshopRagfairIncludeAllOwners", true,
            catWorkshop, "跳蚤购料包含玩家报价（不限商人）",
            "从藏身处点击【购齐缺料】或【跳蚤】跳转购买时，自动取消原版默认的【仅显示商人】过滤，直接显示含玩家在内的全部跳蚤市场报价。");

        WorkshopAnimations = t.Bind(
            "4. Workshop UI", "WorkshopAnimations", true,
            catWorkshop, "界面淡入淡出动画",
            "开启后工坊窗口开关、切换列表/配方、3D 预览加载、卡片图标出现与悬停时使用短暂淡入淡出过渡；关闭则全部立即切换（卡片图标仍分帧加载，不会卡住画面）。");

        t.Advanced = true;
        const string catDiag = "9. 调试";
        PerfLogging = t.Bind(
            "9. Debug", "PerfLogging", false,
            catDiag, "输出性能统计日志",
            "向 BepInEx/LogOutput.log 输出列表构建耗时与跳过的重建次数，便于对比优化效果。");
    }

    private void ApplyPatches()
    {
        TryApply("RequirementViewPatch", () => RequirementViewPatch.Apply(_harmony));
        TryApply("StashIndexPatch", () => StashIndexPatch.Apply(_harmony));
        TryApply("ProductionFlowPatch", () => ProductionFlowPatch.Apply(_harmony));
        TryApply("WishlistCachePatch", () => WishlistCachePatch.Apply(_harmony));
        TryApply("BundlePreloadPatch", () => BundlePreloadPatch.Apply(_harmony));
        TryApply("HideoutInteractionPatch", () => HideoutInteractionPatch.Apply(_harmony));
        TryApply("HideoutUiOptimizationsPatch", () => HideoutUiOptimizationsPatch.Apply(_harmony));
        TryApply("HideoutBackdropPatch", () => HideoutBackdropPatch.Apply(_harmony));
        TryApply("WorkshopUiPatch", () => WorkshopUiPatch.Apply(_harmony));
        // Uses its own Harmony instance; patches are only installed while PerfLogging is on.
        TryApply("HideoutProfilerPatch", HideoutProfilerPatch.Init);
    }

    private static void TryApply(string name, Action apply)
    {
        try
        {
            apply();
            Log.LogInfo($"HideoutQoL: patched {name}");
        }
        catch (Exception ex)
        {
            Log.LogError($"HideoutQoL: failed to apply {name}: {ex}");
        }
    }

    internal static void DebugPerf(string message)
    {
        if (PerfLogging != null && PerfLogging.Value)
        {
            Log.LogInfo($"[HideoutQoL Perf] {message}");
        }
    }

    private void OnDestroy()
    {
        HideoutProfilerPatch.Shutdown();
        _harmony?.UnpatchSelf();
    }
}
