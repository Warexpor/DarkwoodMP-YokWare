using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// Host-auth cutscene begin/end; hide remote proxies during movies (Horde CutsceneSync).
/// </summary>
public sealed class Cutscene_Patch : IPatch
{
    private static bool _active;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        var n = target.Name.ToLowerInvariant();
        if (n.Contains("start") || n.Contains("begin") || n.Contains("play") || n == "show")
            baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Cutscene_Patch).GetMethod(nameof(BeginPostfix), statics)!));
        else
            baseHarmony.Patch(target, postfix: new HarmonyMethod(typeof(Cutscene_Patch).GetMethod(nameof(EndPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("CutsceneManager", "startCutscene");
        yield return ("CutsceneManager", "begin");
        yield return ("CutsceneManager", "play");
        yield return ("CutsceneManager", "endCutscene");
        yield return ("CutsceneManager", "stop");
        yield return ("CutsceneManager", "skip");
        yield return ("Movie", "play");
        yield return ("Movie", "stop");
    }

    public static void BeginPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;

            var name = __instance?.GetType().Name ?? "cutscene";
            network.SendReliable(new CutsceneSyncPacket
            {
                PlayerId = manager.LocalPlayerId,
                Begin = true,
                Name = name.Replace(":", "_")
            });
            ApplyRemote("begin");
        }
        catch (Exception ex) { ModLogger.Error($"[Cutscene] begin: {ex.Message}"); }
    }

    public static void EndPostfix()
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (manager == null || network == null || !network.IsConnected) return;
            if (!manager.IsTimeAuthority) return;
            network.SendReliable(new CutsceneSyncPacket
            {
                PlayerId = manager.LocalPlayerId,
                Begin = false,
                Name = ""
            });
            ApplyRemote("end");
        }
        catch (Exception ex) { ModLogger.Error($"[Cutscene] end: {ex.Message}"); }
    }

    public static void ApplyRemote(string action)
    {
        _active = action == "begin";
        var manager = NetworkManager.Instance;
        if (manager == null) return;
        foreach (var kvp in manager.RemotePlayers)
        {
            if (kvp.Value == null) continue;
            // Hide remotes during cutscene so they don't block camera
            foreach (var r in kvp.Value.GetComponentsInChildren<Renderer>(true))
                if (r != null) r.enabled = !_active;
            var proxy = kvp.Value.GetComponent<RemotePlayerProxy>();
            if (proxy != null) proxy.FreezePosition = _active;
        }
    }

    public static void Reset()
    {
        if (_active) ApplyRemote("end");
        _active = false;
    }
}
