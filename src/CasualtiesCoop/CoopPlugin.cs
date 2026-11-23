using System;
using System.Reflection;
using System.Collections;
using BepInEx;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;

namespace CasualtiesCoop;

[BepInPlugin(PluginId, "Casualties Co-op", PluginVersion)]
internal sealed class CoopPlugin : BaseUnityPlugin
{
    internal const string PluginId = "casualtiesunknown.coop";
    internal const string PluginVersion = "0.1.8";

    private static readonly FieldInfo? SteamInitialized = AccessTools.Field(typeof(SteamManager), "initialized");
    private static CoopPlugin? instance;
    private Harmony? harmony;
    private SteamLobby? lobby;
    private bool lobbyInitializationFailed;
    private bool previousRunInBackground;
    private Coroutine? menuObserver;
    private float nextCanvasProbe;
    private bool reportedCanvas;
    private float nextPlayerLoopProbe;
    private bool reportedPlayerLoop;

    internal static SteamLobby? Lobby => instance?.lobby;
    internal static string SteamStatus { get; private set; } = "Waiting for Steam.";

    private void Awake()
    {
        instance = this;
        gameObject.hideFlags = HideFlags.HideAndDontSave;
        previousRunInBackground = Application.runInBackground;
        Application.runInBackground = true;
        Logger.LogInfo($"Casualties Co-op {PluginVersion} initialized.");

        harmony = new Harmony(PluginId);
        MethodInfo attach = AccessTools.Method(typeof(CoopPlugin), nameof(AttachMenu));
        HarmonyMethod postfix = new(attach);
        harmony.Patch(AccessTools.Method(typeof(PreRunScript), "Awake"), postfix: postfix);
        harmony.Patch(AccessTools.Method(typeof(PreRunScript), "Update"), postfix: postfix);
        SceneManager.sceneLoaded += OnSceneLoaded;
        Canvas.preWillRenderCanvases += ObserveCanvas;
        InstallPlayerLoopObserver();
        AttachMenu(PreRunScript.instance);
        menuObserver = StartCoroutine(ObserveMenus());
        Logger.LogInfo("Native co-op menu hooks installed.");
    }

    private void InstallPlayerLoopObserver()
    {
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        PlayerLoopSystem[] phases = loop.subSystemList;
        int update = Array.FindIndex(phases, phase => phase.type == typeof(UnityEngine.PlayerLoop.Update));
        if (update < 0)
        {
            Logger.LogWarning("Co-op could not locate Unity's Update phase.");
            return;
        }

        PlayerLoopSystem[] children = phases[update].subSystemList ?? Array.Empty<PlayerLoopSystem>();
        Array.Resize(ref children, children.Length + 1);
        children[children.Length - 1] = new PlayerLoopSystem
        {
            type = typeof(CoopPlugin),
            updateDelegate = ObservePlayerLoop
        };
        phases[update].subSystemList = children;
        PlayerLoop.SetPlayerLoop(loop);
        Logger.LogInfo("Co-op registered its Unity player-loop observer.");
    }

    private void ObservePlayerLoop()
    {
        if (!reportedPlayerLoop)
        {
            reportedPlayerLoop = true;
            Logger.LogInfo("Co-op received a Unity player-loop frame.");
        }

        if (Time.realtimeSinceStartup >= nextPlayerLoopProbe)
        {
            nextPlayerLoopProbe = Time.realtimeSinceStartup + 0.35f;
            PreRunScript menu = PreRunScript.instance;
            AttachMenu(menu ? menu : FindObjectOfType<PreRunScript>());
        }
    }

    private static void RemovePlayerLoopObserver()
    {
        PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
        PlayerLoopSystem[] phases = loop.subSystemList;
        int update = Array.FindIndex(phases, phase => phase.type == typeof(UnityEngine.PlayerLoop.Update));
        if (update >= 0 && phases[update].subSystemList is { } children)
        {
            phases[update].subSystemList = Array.FindAll(children, child => child.type != typeof(CoopPlugin));
            PlayerLoop.SetPlayerLoop(loop);
        }
    }

    private IEnumerator ObserveMenus()
    {
        string? lastScene = null;
        while (true)
        {
            string scene = SceneManager.GetActiveScene().name;
            PreRunScript menu = PreRunScript.instance;
            if (!menu && SceneManager.sceneCount > 0)
            {
                menu = FindObjectOfType<PreRunScript>(true);
            }

            if (lastScene != scene)
            {
                lastScene = scene;
                Logger.LogInfo($"Co-op menu observer: scene '{scene}', menu found: {(bool)menu}.");
            }

            AttachMenu(menu);
            yield return new WaitForSecondsRealtime(0.35f);
        }
    }

    private void ObserveCanvas()
    {
        if (!reportedCanvas)
        {
            reportedCanvas = true;
            Logger.LogInfo("Co-op received a native canvas render callback.");
        }

        PreRunScript menu = PreRunScript.instance;
        if (!menu && Time.realtimeSinceStartup >= nextCanvasProbe)
        {
            nextCanvasProbe = Time.realtimeSinceStartup + 0.5f;
            menu = FindObjectOfType<PreRunScript>();
        }

        AttachMenu(menu);
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        instance?.Logger.LogDebug($"Checking scene {scene.name} for the co-op menu.");
        AttachMenu(PreRunScript.instance);
    }

    private static void AttachMenu(PreRunScript? __instance)
    {
        if (!__instance || !__instance!.mainCanvas || __instance.TryGetComponent<CoopMenuView>(out _))
        {
            return;
        }

        try
        {
            CoopMenuView view = __instance.gameObject.AddComponent<CoopMenuView>();
            view.Install(__instance);
            instance?.Logger.LogInfo("Native Co-op button attached to the main menu canvas.");
        }
        catch (Exception ex)
        {
            instance?.Logger.LogError($"Co-op menu attachment failed: {ex}");
        }
    }

    internal static void TickMenu()
    {
        if (instance is { lobby: null, lobbyInitializationFailed: false } plugin)
        {
            plugin.TryInitializeLobby();
        }
    }

    private void TryInitializeLobby()
    {
        try
        {
            SteamManager manager = SteamManager.instance;
            if (SteamInitialized is null || !manager || SteamInitialized.GetValue(manager) is not true)
            {
                SetSteamStatus("Steam is unavailable. Launch from Steam.");
                return;
            }

            if (!SteamUser.BLoggedOn())
            {
                SetSteamStatus("Sign in to Steam.");
                return;
            }

            lobby = new SteamLobby();
            Logger.LogInfo("Steam lobby integration ready.");
            lobby.TryJoinLaunchInvitation();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Steam lobby initialization failed: {ex}");
            SetSteamStatus("Steam lobby error. See BepInEx log.");
            lobby?.Dispose();
            lobby = null;
            lobbyInitializationFailed = true;
        }
    }

    private void SetSteamStatus(string status)
    {
        if (SteamStatus != status)
        {
            SteamStatus = status;
            Logger.LogInfo(status);
        }
    }

    private void OnDestroy()
    {
        RemovePlayerLoopObserver();
        if (menuObserver is not null)
        {
            StopCoroutine(menuObserver);
        }
        SceneManager.sceneLoaded -= OnSceneLoaded;
        Canvas.preWillRenderCanvases -= ObserveCanvas;
        harmony?.UnpatchSelf();
        Application.runInBackground = previousRunInBackground;
        lobby?.Dispose();
        lobby = null;
        instance = null;
    }
}
