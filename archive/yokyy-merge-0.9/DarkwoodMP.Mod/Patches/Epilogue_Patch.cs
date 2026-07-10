using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkwoodMP.Patches;

/// <summary>
/// Coordinated credits load (Horde EpilogueGoToCreditsPatch).
/// Prevents one peer walking to credits alone.
/// </summary>
public sealed class Epilogue_Patch : IPatch
{
    private static bool _sceneLoadPending;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(Epilogue_Patch).GetMethod(nameof(GoToCreditsPrefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("EpilogueOutcomes", "goToCredits");
    }

    public static bool GoToCreditsPrefix()
    {
        try
        {
            if (RemoteApply.Active) return true;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            var manager = NetworkManager.Instance;
            if (network == null || manager == null || !network.IsConnected)
                return true;

            PreFade();
            network.SendReliableCritical(new SceneLoadPacket
            {
                PlayerId = manager.LocalPlayerId,
                SceneName = "credits",
                DelaySeconds = 8f
            });
            ApplySceneLoad("credits", 8f);
            ModLogger.Msg("[Epilogue] goToCredits multiplayer path");
            return false;
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Epilogue] {ex.Message}");
            return true;
        }
    }

    public static void ApplySceneLoad(string sceneName, float delaySeconds = 0.5f)
    {
        if (string.IsNullOrEmpty(sceneName)) return;
        if (_sceneLoadPending) return;
        _sceneLoadPending = true;

        try
        {
            PreFade();
            if (delaySeconds <= 0f)
            {
                FinishLoad(sceneName);
                return;
            }

            // Simple delayed load via coroutine host on NetworkManager
            var nm = NetworkManager.Instance;
            if (nm != null)
                nm.StartCoroutine(DelayedLoad(sceneName, delaySeconds));
            else
                FinishLoad(sceneName);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Epilogue] ApplySceneLoad: {ex.Message}");
            _sceneLoadPending = false;
        }
    }

    private static System.Collections.IEnumerator DelayedLoad(string sceneName, float delay)
    {
        yield return new WaitForSecondsRealtime(delay);
        FinishLoad(sceneName);
    }

    private static void FinishLoad(string sceneName)
    {
        try
        {
            NetworkManager.Instance?.Disconnect();
        }
        catch { }
        try
        {
            SceneManager.LoadScene(sceneName);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Epilogue] LoadScene: {ex.Message}");
        }
        _sceneLoadPending = false;
    }

    private static void PreFade()
    {
        try
        {
            if (Singleton<Controller>.Instance != null)
                Singleton<Controller>.Instance.fadeAudio(fadeOut: true, 10f, musicToo: true);
            Core.hideGameCursor();
            if (Singleton<global::UI>.Instance != null)
                Singleton<global::UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 8f);
            Core.forbidInputs = true;
            Core.coreStarted = false;
            Core.loadingGame = false;
            Core.loadedGame = false;
        }
        catch (Exception ex)
        {
            ModLogger.Warning($"[Epilogue] pre-fade: {ex.Message}");
        }
    }

    public static void Reset()
    {
        _sceneLoadPending = false;
    }
}
