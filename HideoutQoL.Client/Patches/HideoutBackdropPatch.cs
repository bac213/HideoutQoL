using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.CameraControl;
using EFT.Hideout;
using EFT.UI;
using EFT.UI.Screens;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace HideoutQoL.Client.Patches;

/// <summary>
/// Implements seamless background Hideout preloading and keeps the live top-down Hideout overview
/// active behind a semi-transparent <see cref="MenuScreen"/> when out of raid:
/// 1) Forces Hideout scene preload (<c>StartLoadHideoutMap</c>) early in <see cref="TarkovApplication.PrepareGameJob"/>,
///    regardless of vanilla <c>EnableHideoutPreload</c>.
/// 2) Runs full <see cref="TarkovApplication.HideoutController"/> initialization silently in the background
///    after <see cref="TarkovApplication.MainMenu"/> completes <c>GamePrepare(false)</c>, suppressing
///    <see cref="HideoutLoadingScreen"/> and restoring <see cref="InGameMemoryManagement.GCEnabled"/>.
/// 3) Keeps <see cref="PlayerCameraController"/> &amp; <see cref="HideoutCameraController"/> alive in top-down
///    overview behind <see cref="MenuScreen"/> (and compatible lobby sub-screens) with a configurable
///    translucent scrim + optional camera blur, while pausing edge-panning input and muting Hideout audio
///    until entering interactive Hideout.
/// 4) Provides instant (&lt;50ms) switching between Main Menu and Hideout while leaving raid/logout/mode-switch
///    <see cref="TarkovApplication.HideoutController.UnloadHideout"/> behavior intact.
/// </summary>
internal static class HideoutBackdropPatch
{
    private static readonly FieldInfo HasBeenInitField =
        AccessTools.Field(typeof(HideoutRepresentation), "_hasBeenInit");
    private static readonly FieldInfo MenuOperationField =
        AccessTools.Field(typeof(TarkovApplication), "_menuOperation");
    private static readonly FieldInfo CameraBool0Field =
        AccessTools.Field(typeof(HideoutCameraController), "bool_0");
    private static readonly FieldInfo ScreenShowCoroutineField =
        AccessTools.Field(typeof(UIScreen), "_showCoroutine");

    private static readonly HashSet<EEftScreenType> VanillaEnvironmentScreens = new()
    {
        EEftScreenType.None,
        EEftScreenType.Login,
        EEftScreenType.ProfileLoading,
        EEftScreenType.Reconnect,
        EEftScreenType.SelectRaidSide,
        EEftScreenType.SelectLocation,
        EEftScreenType.KeyAccess,
        EEftScreenType.MapPoints,
        EEftScreenType.OfflineRaid,
        EEftScreenType.Insurance,
        EEftScreenType.MatchMakerAccept,
        EEftScreenType.TimeHasCome,
        EEftScreenType.FinalCountdown,
        EEftScreenType.ScavInventory,
        EEftScreenType.TraderDialog,
        EEftScreenType.BattleUI,
    };

    private static TarkovApplication _app;
    private static bool _backdropReady;
    private static bool _suppressLoadingScreen;
    private static bool _inNarrateScene;
    private static bool _applyingInternalEnvChange;
    private static bool _overviewInitialized;
    private static bool _screenHookAttached;
    private static bool _needsDataSync;
    private static bool _resyncingOwnData;
    private static int _bootstrapGeneration;

    private static WeakReference<MenuScreen> _activeMenuScreen;
    private static bool _isRaidMenu;

    private static bool IsInRaidSession()
    {
        if (Singleton<AbstractGame>.Instantiated && !(Singleton<AbstractGame>.Instance is HideoutGame))
        {
            return true;
        }

#pragma warning disable CS0618
        return InGameStatus.InRaid;
#pragma warning restore CS0618
    }

    private static void FireAndForget(Task task, string context)
    {
        if (task == null)
        {
            return;
        }

        task.ContinueWith(
            t =>
            {
                if (t.Exception != null)
                {
                    HideoutQoLPlugin.Log?.LogWarning($"HideoutQoL: {context} failed: {t.Exception.GetBaseException()}");
                }
            },
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    internal static bool FeatureActive =>
        HideoutQoLPlugin.Enabled != null
        && HideoutQoLPlugin.Enabled.Value
        && HideoutQoLPlugin.EnableHideoutLobbyBackdrop != null
        && HideoutQoLPlugin.EnableHideoutLobbyBackdrop.Value;

    internal static void Apply(Harmony harmony)
    {
        // 1) Force early Hideout map preload whenever HideoutController is created in PrepareGameJob
        ConstructorInfo hcCtor = AccessTools.Constructor(
            typeof(TarkovApplication.HideoutController),
            new[] { typeof(TarkovApplication) });
        if (hcCtor != null)
        {
            harmony.Patch(hcCtor, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutControllerCtorPostfix)));
        }

        MethodInfo startLoadMap = AccessTools.Method(
            typeof(TarkovApplication.HideoutController),
            nameof(TarkovApplication.HideoutController.StartLoadHideoutMap));
        if (startLoadMap != null)
        {
            harmony.Patch(startLoadMap, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(StartLoadHideoutMapPrefix)));
        }

        // 2) Start silent background Hideout bootstrap right after MainMenu() completes GamePrepare(false)
        MethodInfo mainMenu = AccessTools.Method(
            typeof(TarkovApplication),
            nameof(TarkovApplication.MainMenu),
            new[] { typeof(Action) });
        if (mainMenu != null)
        {
            harmony.Patch(mainMenu, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(MainMenuPostfix)));
        }

        // 3) Suppress HideoutLoadingScreen only during silent background Hideout bootstrap
        MethodInfo loadingShow = AccessTools.Method(typeof(HideoutLoadingScreen), nameof(HideoutLoadingScreen.Show));
        if (loadingShow != null)
        {
            harmony.Patch(loadingShow, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutLoadingShowPrefix)));
        }

        MethodInfo loadingComplete = AccessTools.Method(typeof(HideoutLoadingScreen), nameof(HideoutLoadingScreen.LoadComplete));
        if (loadingComplete != null)
        {
            harmony.Patch(loadingComplete, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutLoadingCompletePrefix)));
        }

        // 4) Restore GCEnabled=true after HideoutGame.Run() inside method_7 when out of raid
        MethodInfo hcMethod7 = AccessTools.Method(typeof(TarkovApplication.HideoutController), "method_7");
        if (hcMethod7 != null)
        {
            harmony.Patch(hcMethod7, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutMethod7Postfix)));
        }

        // 5) Keep audio muted on silent HideoutAwake when MainMenu is showing
        MethodInfo hcMethod8 = AccessTools.Method(typeof(TarkovApplication.HideoutController), "method_8");
        if (hcMethod8 != null)
        {
            harmony.Patch(hcMethod8, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutMethod8Postfix)));
        }

        // 6) HideoutSelectedHandler: handle concurrent background load or instant entry
        MethodInfo selectedHandler = AccessTools.Method(
            typeof(TarkovApplication.HideoutController),
            nameof(TarkovApplication.HideoutController.HideoutSelectedHandler));
        if (selectedHandler != null)
        {
            harmony.Patch(
                selectedHandler,
                prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutSelectedHandlerPrefix)),
                postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutSelectedHandlerPostfix)));
        }

        // 7) Preserve PlayerCameraController & overview camera on HideHideout, clean up on UnloadHideout
        MethodInfo hideHideout = AccessTools.Method(
            typeof(TarkovApplication.HideoutController),
            nameof(TarkovApplication.HideoutController.HideHideout));
        if (hideHideout != null)
        {
            harmony.Patch(hideHideout, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideHideoutPrefix)));
        }

        MethodInfo unloadHideout = AccessTools.Method(
            typeof(TarkovApplication.HideoutController),
            nameof(TarkovApplication.HideoutController.UnloadHideout));
        if (unloadHideout != null)
        {
            harmony.Patch(unloadHideout, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(UnloadHideoutPrefix)));
        }

        // 8) NarrateController (3D Traders) yields Hideout camera cleanly and restores on Hide
        MethodInfo narrateShow = AccessTools.Method(typeof(TarkovApplication.NarrateController), nameof(TarkovApplication.NarrateController.Show));
        if (narrateShow != null)
        {
            harmony.Patch(narrateShow, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(NarrateShowPrefix)));
        }

        MethodInfo narrateHide = AccessTools.Method(typeof(TarkovApplication.NarrateController), nameof(TarkovApplication.NarrateController.Hide));
        if (narrateHide != null)
        {
            harmony.Patch(narrateHide, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(NarrateHidePostfix)));
        }

        // 9) EnvironmentUI & CameraManager & HideoutCameraController coordination
        MethodInfo showEnv = AccessTools.Method(typeof(EnvironmentUI), nameof(EnvironmentUI.ShowEnvironment));
        if (showEnv != null)
        {
            harmony.Patch(showEnv, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(ShowEnvironmentPrefix)));
        }

        MethodInfo showEnvCam = AccessTools.Method(typeof(EnvironmentUI), nameof(EnvironmentUI.ShowCameraContainer));
        if (showEnvCam != null)
        {
            harmony.Patch(showEnvCam, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(ShowCameraContainerPrefix)));
        }

        MethodInfo camLateUpdate = AccessTools.Method(typeof(HideoutCameraController), nameof(HideoutCameraController.LateUpdate));
        if (camLateUpdate != null)
        {
            harmony.Patch(camLateUpdate, prefix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(HideoutCameraLateUpdatePrefix)));
        }

        // 10) MenuScreen translucent overlay & scrim lifecycle
        MethodInfo menuShow = AccessTools.Method(
            typeof(MenuScreen),
            nameof(MenuScreen.Show),
            new[] { typeof(MenuScreen.MainMenuBaseScreenController) });
        if (menuShow != null)
        {
            harmony.Patch(menuShow, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(MenuScreenShowPostfix)));
        }

        MethodInfo menuClose = AccessTools.Method(typeof(MenuScreen), nameof(MenuScreen.Close));
        if (menuClose != null)
        {
            harmony.Patch(menuClose, postfix: new HarmonyMethod(typeof(HideoutBackdropPatch), nameof(MenuScreenClosePostfix)));
        }

        // Live setting hooks
        HideoutQoLPlugin.Enabled.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.EnableHideoutLobbyBackdrop.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.MenuBackdropDimAlpha.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.MenuUiAlpha.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.MenuBackdropBlur.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.HideoutAudioInMainMenu.SettingChanged += OnBackdropSettingChanged;
        HideoutQoLPlugin.KeepHideoutBackdropInSubScreens.SettingChanged += OnBackdropSettingChanged;
    }

    private static void OnBackdropSettingChanged(object sender, EventArgs e)
    {
        if (IsInRaidSession())
        {
            return;
        }

        if (FeatureActive && !_backdropReady && _app != null && _app.HideoutControllerAccess != null)
        {
            int gen = _bootstrapGeneration;
            FireAndForget(RunSilentHideoutBootstrapAsync(_app, _app.HideoutControllerAccess, gen), "SilentHideoutBootstrap");
        }

        SyncBackdropForScreen(GetCurrentScreenType());
    }

    // --- 1) Early Map Preload ---

    private static void HideoutControllerCtorPostfix(TarkovApplication.HideoutController __instance, TarkovApplication application)
    {
        _app = application;
        _backdropReady = false;
        _overviewInitialized = false;
        _inNarrateScene = false;
        _needsDataSync = false;
        _bootstrapGeneration++;

        EnsureScreenHookAttached();

        if (FeatureActive && __instance != null && __instance.task_1 == null)
        {
            HideoutQoLPlugin.DebugPerf("Early StartLoadHideoutMap() triggered on HideoutController init");
            __instance.StartLoadHideoutMap();
        }
    }

    private static bool StartLoadHideoutMapPrefix(TarkovApplication.HideoutController __instance)
    {
        return __instance == null || __instance.task_1 == null;
    }

    // --- 2) Silent Background Bootstrap ---

    private static void MainMenuPostfix(TarkovApplication __instance, ref Task __result)
    {
        if (__instance != null)
        {
            _app = __instance;
        }
        if (__result == null)
        {
            return;
        }

        __result = WrapMainMenuAsync(__instance, __result);
    }

    private static async Task WrapMainMenuAsync(TarkovApplication app, Task original)
    {
        await original;

        if (!FeatureActive || app == null || app.HideoutControllerAccess == null)
        {
            return;
        }

        int gen = _bootstrapGeneration;
        if (HideoutQoLPlugin.WaitForHideoutBeforeMenu.Value)
        {
            await RunSilentHideoutBootstrapAsync(app, app.HideoutControllerAccess, gen);
        }
        else
        {
            FireAndForget(RunSilentHideoutBootstrapAsync(app, app.HideoutControllerAccess, gen), "SilentHideoutBootstrap");
        }
    }

    private static async Task RunSilentHideoutBootstrapAsync(
        TarkovApplication app,
        TarkovApplication.HideoutController hc,
        int generation)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (IsStale(app, hc, generation))
            {
                return;
            }

            if (hc.task_1 == null)
            {
                hc.StartLoadHideoutMap();
            }
            if (hc.task_2 == null)
            {
                hc.StartLoadHideoutBundles();
            }

            // Wait until HideoutRepresentation has finished backend init and GameUI + _menuOperation exist
            await WaitForPrerequisitesAsync(app, hc, generation);
            if (IsStale(app, hc, generation))
            {
                return;
            }

            if (hc._playerOwner == null)
            {
                _suppressLoadingScreen = true;
                try
                {
                    if (hc.task_0 == null)
                    {
                        hc.task_0 = hc.method_0();
                    }

                    Option<HideoutGame> option = await hc.task_0;
                    InGameMemoryManagement.GCEnabled = true;

                    if (IsStale(app, hc, generation) || option.Failed)
                    {
                        if (option.Failed)
                        {
                            HideoutQoLPlugin.Log.LogWarning($"HideoutQoL: silent Hideout init failed: {option.Error}");
                        }
                        return;
                    }

                    hc._playerOwner = option.Value.PlayerOwner;
                }
                finally
                {
                    _suppressLoadingScreen = false;
                    InGameMemoryManagement.GCEnabled = true;
                }
            }

            if (IsStale(app, hc, generation) || hc._playerOwner == null)
            {
                return;
            }

            // If player already clicked into Hideout while silent load was running, don't hijack their session
            EEftScreenType currentScreen = GetCurrentScreenType();
            bool userAlreadyInHideout = currentScreen == EEftScreenType.Hideout
                || IsHideoutOverlayScreen()
                || hc._playerOwner.IsGuest;

            if (!userAlreadyInHideout)
            {
                HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
                Profile profile = app.Session.Profile;
                if (hideout != null && profile != null)
                {
                    HideoutData ownData = new HideoutData(
                        profile.Hideout,
                        profile.Inventory.HideoutAreaStashes,
                        profile.Inventory.HideoutCustomizationStash,
                        isGuest: false,
                        profile.AccountId);

                    await hideout.SetHideoutData(ownData, updateVisual: false);
                    _needsDataSync = false;
                }

                if (IsStale(app, hc, generation))
                {
                    return;
                }

                hc.method_10();
                SetupOverviewCamera(hc, resetCamera: true);

                // Let Cinemachine snap/blend onto the overview camera for 1 frame before dropping EnvironmentUI
                await Task.Yield();
                if (IsStale(app, hc, generation))
                {
                    return;
                }
            }

            _backdropReady = true;
            sw.Stop();
            HideoutQoLPlugin.DebugPerf($"SilentHideoutBootstrap ready in {sw.Elapsed.TotalMilliseconds:F0}ms");

            currentScreen = GetCurrentScreenType();
            if (currentScreen != EEftScreenType.Hideout)
            {
                SyncBackdropForScreen(currentScreen);
            }
        }
        catch (Exception ex)
        {
            _suppressLoadingScreen = false;
            HideoutQoLPlugin.Log.LogWarning($"HideoutQoL: silent Hideout bootstrap error: {ex}");
        }
    }

    private static async Task WaitForPrerequisitesAsync(
        TarkovApplication app,
        TarkovApplication.HideoutController hc,
        int generation)
    {
        while (!IsStale(app, hc, generation))
        {
            bool hasMenuOp = MenuOperationField?.GetValue(app) != null;
            bool hasGameUi = MonoBehaviourSingleton<GameUI>.Instantiated;
            bool hasHideoutRep = Singleton<HideoutRepresentation>.Instantiated
                && HasBeenInitField != null
                && (bool)HasBeenInitField.GetValue(Singleton<HideoutRepresentation>.Instance);

            if (hasMenuOp && hasGameUi && hasHideoutRep)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private static bool IsStale(TarkovApplication app, TarkovApplication.HideoutController hc, int generation)
    {
        return generation != _bootstrapGeneration
            || app == null
            || hc == null
            || !ReferenceEquals(app.HideoutControllerAccess, hc)
            || app.Session == null
            || app.Session.Destroyed
            || IsInRaidSession();
    }

    // --- 3) Loading Screen Suppression & GC / Audio Guards ---

    private static bool HideoutLoadingShowPrefix()
    {
        return !_suppressLoadingScreen;
    }

    private static bool HideoutLoadingCompletePrefix()
    {
        return !_suppressLoadingScreen;
    }

    private static void HideoutMethod7Postfix(ref Task<HideoutGame> __result)
    {
        if (__result == null)
        {
            return;
        }

        __result = WrapMethod7Async(__result);
    }

    private static async Task<HideoutGame> WrapMethod7Async(Task<HideoutGame> original)
    {
        HideoutGame game = await original;
        if (!IsInRaidSession())
        {
            InGameMemoryManagement.GCEnabled = true;
        }
        return game;
    }

    private static void HideoutMethod8Postfix()
    {
        if (_suppressLoadingScreen && !HideoutQoLPlugin.HideoutAudioInMainMenu.Value)
        {
            ApplyMenuAudioState(forceInstantMute: true);
        }
    }

    // --- 4) Instant HideoutEntry / HideHideout / UnloadHideout / Narrate ---

    private static void HideoutSelectedHandlerPrefix(
        TarkovApplication.HideoutController __instance,
        HideoutData hideoutData,
        out Stopwatch __state)
    {
        __state = Stopwatch.StartNew();

        // If user explicitly clicked Hideout while silent bootstrap was mid-way, un-suppress loading screen for remaining wait
        if (__instance != null && __instance._playerOwner == null && _suppressLoadingScreen)
        {
            _suppressLoadingScreen = false;
            if (__instance.task_0 != null && !__instance.task_0.IsCompleted && MonoBehaviourSingleton<PreloaderUI>.Instantiated)
            {
                MonoBehaviourSingleton<PreloaderUI>.Instance.HideoutLoadingScreen.Show();
            }
        }

        if (hideoutData != null && hideoutData.IsGuest)
        {
            _needsDataSync = true;
        }

        if (FeatureActive && hideoutData != null && !hideoutData.IsGuest)
        {
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
            ApplyHideoutAudioState();
        }
    }

    private static void HideoutSelectedHandlerPostfix(
        TarkovApplication.HideoutController __instance,
        HideoutData hideoutData,
        Stopwatch __state,
        ref Task __result)
    {
        if (__result == null)
        {
            return;
        }

        __result = WrapSelectedHandlerAsync(__instance, hideoutData, __state, __result);
    }

    private static async Task WrapSelectedHandlerAsync(
        TarkovApplication.HideoutController hc,
        HideoutData hideoutData,
        Stopwatch sw,
        Task original)
    {
        await original;
        sw?.Stop();

        if (hc != null && hc._playerOwner != null && hideoutData != null && !hideoutData.IsGuest)
        {
            _backdropReady = true;
            _overviewInitialized = true;
            _needsDataSync = false;
            ApplyHideoutAudioState();
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);

            // Ensure overview camera is active if returning to non-first-person Hideout
            var rear = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.HideoutScreenRear : null;
            if (rear != null && !rear.FirstPerson && rear.HideoutCameraController != null)
            {
                rear.HideoutCameraController.SetActiveState(true);
            }
        }

        if (sw != null)
        {
            HideoutQoLPlugin.DebugPerf($"HideoutSelectedHandler completed in {sw.Elapsed.TotalMilliseconds:F1}ms");
        }
    }

    private static bool HideHideoutPrefix(TarkovApplication.HideoutController __instance)
    {
        if (!FeatureActive || !_backdropReady || _inNarrateScene || __instance == null || __instance._playerOwner == null)
        {
            return true;
        }

        // Let guest hideout visits clean up via vanilla path, and mark own hideout for visual resync next time
        if (__instance._playerOwner.IsGuest)
        {
            _needsDataSync = true;
            _overviewInitialized = false;
            return true;
        }

        // Flush queued hideout operations just like vanilla HideHideout()
        if (Singleton<HideoutRepresentation>.Instantiated)
        {
            Singleton<HideoutRepresentation>.Instance.ApplyChanges();
        }

        // Ensure we return to top-down overview camera rather than staying in 1st-person or Shooting Range
        ReturnOwnerToOverview(__instance);

        if (!HideoutQoLPlugin.HideoutAudioInMainMenu.Value)
        {
            ApplyMenuAudioState(forceInstantMute: false);
            __instance._playerOwner.Mute(muted: true);
        }

        SyncBackdropForScreen(GetCurrentScreenType());
        return false;
    }

    private static void UnloadHideoutPrefix()
    {
        _bootstrapGeneration++;
        _backdropReady = false;
        _overviewInitialized = false;
        _suppressLoadingScreen = false;
        _inNarrateScene = false;
        _needsDataSync = false;

        SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
        BetterAudio.IsInHideout = false;
        if (Singleton<GUISounds>.Instantiated)
        {
            Singleton<GUISounds>.Instance.method_11(active: false);
        }
    }

    private static void NarrateShowPrefix()
    {
        _inNarrateScene = true;
        _suppressLoadingScreen = false;
        _overviewInitialized = false;
        SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
    }

    private static void NarrateHidePostfix(ref Task __result)
    {
        if (__result == null)
        {
            OnNarrateExited();
            return;
        }

        __result = WrapNarrateHideAsync(__result);
    }

    private static async Task WrapNarrateHideAsync(Task original)
    {
        try
        {
            await original;
        }
        finally
        {
            OnNarrateExited();
        }
    }

    private static void OnNarrateExited()
    {
        _inNarrateScene = false;
        if (FeatureActive && _backdropReady && _app?.HideoutControllerAccess != null)
        {
            _app.HideoutControllerAccess.method_10();
            SetupOverviewCamera(_app.HideoutControllerAccess, resetCamera: true);
            SyncBackdropForScreen(GetCurrentScreenType());
        }
    }

    // --- 5) Camera & EnvironmentUI Synchronization ---

    private static void EnsureScreenHookAttached()
    {
        if (_screenHookAttached)
        {
            return;
        }

        EftScreenManager.Instance.OnScreenChanged += OnEftScreenChanged;
        _screenHookAttached = true;
    }

    private static void OnEftScreenChanged(EEftScreenType screenType)
    {
        if (IsInRaidSession())
        {
            return;
        }

        SyncBackdropForScreen(screenType);
    }

    private static EEftScreenType GetCurrentScreenType()
    {
        var ctrl = EftScreenManager.Instance.CurrentScreenController;
        return ctrl != null ? ctrl.ScreenType : EEftScreenType.None;
    }

    private static bool IsHideoutOverlayScreen()
    {
        var controller = EftScreenManager.Instance.CurrentBaseScreenController;
        if (controller is InventoryScreen.HideoutInventoryScreenController
            || controller is EftBattleUIScreen.HideoutBattleUIScreenController)
        {
            return true;
        }

        while (controller != null && !controller.Root)
        {
            controller = controller.PreviousScreen;
            if (controller?.ScreenType == EEftScreenType.Hideout)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldUseHideoutBackdrop(EEftScreenType screenType)
    {
        if (!FeatureActive || !_backdropReady || _inNarrateScene || IsInRaidSession())
        {
            return false;
        }

        if (_app?.HideoutControllerAccess?._playerOwner == null || _app.HideoutControllerAccess._playerOwner.IsGuest)
        {
            return false;
        }

        if (screenType == EEftScreenType.Hideout || screenType == EEftScreenType.None)
        {
            return true;
        }

        if (IsHideoutOverlayScreen())
        {
            return false;
        }

        if (screenType == EEftScreenType.MainMenu)
        {
            return !_isRaidMenu;
        }

        if (!HideoutQoLPlugin.KeepHideoutBackdropInSubScreens.Value)
        {
            return false;
        }

        return !VanillaEnvironmentScreens.Contains(screenType);
    }

    private static bool ShowEnvironmentPrefix(EnvironmentUI __instance, ref bool value)
    {
        if (_applyingInternalEnvChange || !value)
        {
            return true;
        }

        if (ShouldUseHideoutBackdrop(GetCurrentScreenType()))
        {
            value = false;
        }

        return true;
    }

    private static bool ShowCameraContainerPrefix(EnvironmentUI __instance, ref bool value)
    {
        if (_applyingInternalEnvChange || !value)
        {
            return true;
        }

        if (ShouldUseHideoutBackdrop(GetCurrentScreenType()))
        {
            value = false;
        }

        return true;
    }

    private static bool HideoutCameraLateUpdatePrefix()
    {
        // Only let the player pan/rotate the Hideout camera when the Hideout screen is actively focused
        if (!FeatureActive || !_backdropReady)
        {
            return true;
        }

        return GetCurrentScreenType() == EEftScreenType.Hideout;
    }

    private static void SyncBackdropForScreen(EEftScreenType screenType)
    {
        if (IsInRaidSession() || _inNarrateScene || screenType == EEftScreenType.None)
        {
            return;
        }

        // Screens stacked on Hideout can return to the player's current view.
        // Leave their camera and input transitions to the game.
        if (IsHideoutOverlayScreen())
        {
            return;
        }

        TarkovApplication.HideoutController hc = _app?.HideoutControllerAccess;
        if (!_backdropReady || hc == null || hc._playerOwner == null)
        {
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: false);
            return;
        }

        if (_needsDataSync && !_resyncingOwnData && !hc._playerOwner.IsGuest && _app?.Session?.Profile != null)
        {
            FireAndForget(ResyncOwnHideoutDataAsync(_app, _bootstrapGeneration), "ResyncOwnHideoutData");
        }

        if (!FeatureActive)
        {
            SetEnvironmentUiVisible(true);
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
            return;
        }

        if (screenType == EEftScreenType.Hideout)
        {
            SetEnvironmentUiVisible(false);
            EnsureCameraAndLevelSettings(hc);
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
            ApplyHideoutAudioState();
            return;
        }

        if (ShouldUseHideoutBackdrop(screenType))
        {
            SetEnvironmentUiVisible(false);
            EnsureCameraAndLevelSettings(hc);
            SetupOverviewCamera(hc, resetCamera: !_overviewInitialized);

            bool isMainMenu = screenType == EEftScreenType.MainMenu;
            bool blur = isMainMenu && HideoutQoLPlugin.MenuBackdropBlur.Value;
            SetMenuOverlayState(showScrim: isMainMenu, applyBlur: blur, instant: false);

            if (HideoutQoLPlugin.HideoutAudioInMainMenu.Value)
            {
                ApplyHideoutAudioState();
            }
            else
            {
                ApplyMenuAudioState(forceInstantMute: false);
                hc._playerOwner.Mute(muted: true);
            }
        }
        else
        {
            // Matchmaker / ProfileLoading / Reconnect: yield cleanly to EnvironmentUI without unloading Hideout
            SetMenuOverlayState(showScrim: false, applyBlur: false, instant: true);
            SetEnvironmentUiVisible(true);
        }
    }

    private static void SetEnvironmentUiVisible(bool visible)
    {
        if (!MonoBehaviourSingleton<EnvironmentUI>.Instantiated)
        {
            return;
        }

        EnvironmentUI env = MonoBehaviourSingleton<EnvironmentUI>.Instance;
        _applyingInternalEnvChange = true;
        try
        {
            env.ShowEnvironment(visible);
            env.ShowCameraContainer(visible);
            if (!visible)
            {
                env.EnableOverlay(false);
            }
        }
        finally
        {
            _applyingInternalEnvChange = false;
        }
    }

    private static void EnsureCameraAndLevelSettings(TarkovApplication.HideoutController hc)
    {
        if (hc == null || hc._playerOwner == null || hc._playerOwner.Player == null)
        {
            return;
        }

        if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance != hc.gameWorld_0)
        {
            Singleton<GameWorld>.Create(hc.gameWorld_0);
            Singleton<IGameLevel>.Create(hc.gameWorld_0);
        }

        if (hc.levelSettings_0 != null)
        {
            if (!Singleton<LevelSettings>.Instantiated || Singleton<LevelSettings>.Instance != hc.levelSettings_0)
            {
                Singleton<LevelSettings>.Create(hc.levelSettings_0);
            }
            hc.levelSettings_0.ApplySettings();
        }

        if (CameraManager.Instance.Camera == null)
        {
            PlayerCameraController.Create(hc._playerOwner.Player);
        }

        Camera cam = CameraManager.Instance.Camera;
        if (cam != null)
        {
            cam.clearFlags = CameraClearFlags.Color;
            CameraManager.Instance.IsActive = true;
        }
    }

    private static void SetupOverviewCamera(TarkovApplication.HideoutController hc, bool resetCamera)
    {
        if (hc == null || hc._playerOwner == null)
        {
            return;
        }

        if (hc._playerOwner.FirstPersonMode)
        {
            hc._playerOwner.SetPointOfView(false);
        }

        HideoutScreenRear rear = MonoBehaviourSingleton<CommonUI>.Instantiated
            ? MonoBehaviourSingleton<CommonUI>.Instance.HideoutScreenRear
            : null;
        HideoutCameraController camCtrl = rear != null ? rear.HideoutCameraController : null;
        if (camCtrl == null)
        {
            return;
        }

        camCtrl.AreaSelected = false;
        camCtrl.EnableFlashLightForce = false;

        if (resetCamera || !_overviewInitialized)
        {
            camCtrl.Show();
            camCtrl.Zoom(null);
            _overviewInitialized = true;
        }
        else
        {
            camCtrl.SetActiveState(true);
            camCtrl.Zoom(null);
        }
    }

    private static void ReturnOwnerToOverview(TarkovApplication.HideoutController hc)
    {
        if (hc == null || hc._playerOwner == null)
        {
            return;
        }

        if (hc._playerOwner.InShootingRange)
        {
            FireAndForget(hc._playerOwner.SetShootingRangeStatus(false), "SetShootingRangeStatus");
        }

        bool wasFirstPerson = hc._playerOwner.FirstPersonMode;
        if (wasFirstPerson)
        {
            hc._playerOwner.SetPointOfView(false);
        }

        SetupOverviewCamera(hc, resetCamera: wasFirstPerson);
    }

    private static async Task ResyncOwnHideoutDataAsync(TarkovApplication app, int generation)
    {
        _resyncingOwnData = true;
        try
        {
            HideoutRepresentation hideout = Singleton<HideoutRepresentation>.Instance;
            Profile profile = app?.Session?.Profile;
            if (hideout == null || profile == null || generation != _bootstrapGeneration)
            {
                return;
            }

            HideoutData ownData = new HideoutData(
                profile.Hideout,
                profile.Inventory.HideoutAreaStashes,
                profile.Inventory.HideoutCustomizationStash,
                isGuest: false,
                profile.AccountId);

            await hideout.SetHideoutData(ownData, updateVisual: false);
            if (generation == _bootstrapGeneration)
            {
                _needsDataSync = false;
            }
        }
        finally
        {
            _resyncingOwnData = false;
        }
    }

    // --- 6) MenuScreen Visual Overlay & Audio ---

    private static void MenuScreenShowPostfix(MenuScreen __instance, MenuScreen.MainMenuBaseScreenController controller)
    {
        if (__instance == null)
        {
            return;
        }

        _isRaidMenu = controller is MenuScreen.RaidMainMenuScreenController || IsInRaidSession();
        if (_isRaidMenu)
        {
            _activeMenuScreen = null;
            return;
        }

        _activeMenuScreen = new WeakReference<MenuScreen>(__instance);
        HideoutBackdropMenuVisual.GetOrCreate(__instance);

        SyncBackdropForScreen(EEftScreenType.MainMenu);
    }

    private static void MenuScreenClosePostfix(MenuScreen __instance)
    {
        if (__instance == null)
        {
            return;
        }

        HideoutBackdropMenuVisual visual = __instance.GetComponent<HideoutBackdropMenuVisual>();
        if (visual != null)
        {
            visual.SetScrimTarget(0f, instant: true);
        }
    }

    private static void SetMenuOverlayState(bool showScrim, bool applyBlur, bool instant)
    {
        if (_activeMenuScreen != null && _activeMenuScreen.TryGetTarget(out MenuScreen menu) && menu != null)
        {
            HideoutBackdropMenuVisual visual = menu.GetComponent<HideoutBackdropMenuVisual>();
            if (visual != null)
            {
                float targetScrim = showScrim ? Mathf.Clamp(HideoutQoLPlugin.MenuBackdropDimAlpha.Value, 0f, 0.85f) : 0f;
                float targetUiAlpha = showScrim ? Mathf.Clamp(HideoutQoLPlugin.MenuUiAlpha.Value, 0.5f, 1f) : 1f;
                visual.SetVisualTargets(targetScrim, targetUiAlpha, instant);
            }
        }

        if (!IsInRaidSession() && CameraManager.Exist && CameraManager.Instance.Camera != null)
        {
            CameraManager.Instance.Blur(applyBlur, instant ? 0f : 0.35f);
        }
    }

    private static void ApplyMenuAudioState(bool forceInstantMute)
    {
        BetterAudio.IsInHideout = false;
        if (!Singleton<GUISounds>.Instantiated)
        {
            return;
        }

        GUISounds gui = Singleton<GUISounds>.Instance;
        gui.method_11(active: false);
        gui.method_9(isActive: true);

        if (forceInstantMute && gui.MasterMixer != null)
        {
            gui.MasterMixer.SetFloat("HideoutVolume", -80f);
            gui.MasterMixer.SetFloat("InGame", -80f);
        }
    }

    private static void ApplyHideoutAudioState()
    {
        BetterAudio.IsInHideout = true;
        if (_app?.HideoutControllerAccess?._playerOwner != null)
        {
            _app.HideoutControllerAccess._playerOwner.Mute(muted: false);
        }
        if (Singleton<GUISounds>.Instantiated)
        {
            GUISounds gui = Singleton<GUISounds>.Instance;
            gui.method_9(isActive: false);
            gui.method_11(active: true);
        }
    }

    internal static bool IsUIScreenShowing(UIScreen screen)
    {
        return screen != null && ScreenShowCoroutineField != null && ScreenShowCoroutineField.GetValue(screen) != null;
    }
}

/// <summary>
/// Attached to <see cref="MenuScreen"/> to draw a non-blocking translucent dark scrim behind
/// menu controls and smoothly crossfade UI opacity when the Hideout backdrop initializes.
/// </summary>
internal sealed class HideoutBackdropMenuVisual : MonoBehaviour
{
    private const float FadeSpeed = 3.2f;

    private MenuScreen _menu;
    private Image _scrimImage;
    private float _currentScrimAlpha;
    private float _targetScrimAlpha;
    private float _targetUiAlpha = 1f;

    internal static HideoutBackdropMenuVisual GetOrCreate(MenuScreen menu)
    {
        HideoutBackdropMenuVisual visual = menu.GetComponent<HideoutBackdropMenuVisual>();
        if (visual == null)
        {
            visual = menu.gameObject.AddComponent<HideoutBackdropMenuVisual>();
        }
        visual.EnsureScrim(menu);
        return visual;
    }

    private void EnsureScrim(MenuScreen menu)
    {
        _menu = menu;
        if (_scrimImage != null)
        {
            return;
        }

        Transform existing = menu.transform.Find("HideoutQoL_MenuScrim");
        GameObject scrimGo;
        if (existing != null)
        {
            scrimGo = existing.gameObject;
        }
        else
        {
            scrimGo = new GameObject("HideoutQoL_MenuScrim", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)scrimGo.transform;
            rt.SetParent(menu.transform, worldPositionStays: false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        scrimGo.transform.SetSiblingIndex(0);
        _scrimImage = scrimGo.GetComponent<Image>();
        _scrimImage.color = new Color(0.02f, 0.03f, 0.04f, 0f);
        _scrimImage.raycastTarget = false;
        scrimGo.SetActive(false);
    }

    internal void SetScrimTarget(float targetAlpha, bool instant)
    {
        SetVisualTargets(targetAlpha, 1f, instant);
    }

    internal void SetVisualTargets(float scrimAlpha, float uiAlpha, bool instant)
    {
        _targetScrimAlpha = scrimAlpha;
        _targetUiAlpha = uiAlpha;

        if (instant && _scrimImage != null)
        {
            _currentScrimAlpha = scrimAlpha;
            ApplyScrimColor();
            if (_menu != null && !HideoutBackdropPatch.IsUIScreenShowing(_menu))
            {
                _menu.CanvasGroup.alpha = _targetUiAlpha;
            }
        }
        else if (_scrimImage != null && scrimAlpha > 0.001f && !_scrimImage.gameObject.activeSelf)
        {
            _scrimImage.gameObject.SetActive(true);
        }
    }

    private void LateUpdate()
    {
        if (_menu == null || !_menu.gameObject.activeSelf)
        {
            return;
        }

        if (_scrimImage != null && !Mathf.Approximately(_currentScrimAlpha, _targetScrimAlpha))
        {
            _currentScrimAlpha = Mathf.MoveTowards(_currentScrimAlpha, _targetScrimAlpha, FadeSpeed * Time.unscaledDeltaTime);
            ApplyScrimColor();
        }

        // Apply UI alpha only after UIScreen.SoftShow finishes fading CanvasGroup in
        if (!HideoutBackdropPatch.IsUIScreenShowing(_menu) && !Mathf.Approximately(_menu.CanvasGroup.alpha, _targetUiAlpha))
        {
            _menu.CanvasGroup.alpha = Mathf.MoveTowards(_menu.CanvasGroup.alpha, _targetUiAlpha, FadeSpeed * Time.unscaledDeltaTime);
        }
    }

    private void ApplyScrimColor()
    {
        if (_scrimImage == null)
        {
            return;
        }

        bool visible = _currentScrimAlpha > 0.002f;
        if (_scrimImage.gameObject.activeSelf != visible)
        {
            _scrimImage.gameObject.SetActive(visible);
        }
        if (visible)
        {
            _scrimImage.color = new Color(0.02f, 0.03f, 0.04f, _currentScrimAlpha);
        }
    }
}
