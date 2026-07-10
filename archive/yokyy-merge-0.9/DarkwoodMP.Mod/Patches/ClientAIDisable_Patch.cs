using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.Network;
using HarmonyLib;
using UnityEngine;

namespace DarkwoodMP.Patches;

/// <summary>
/// On NON-AUTHORITY machines, blocks enemy AI / pathfinding Update loops so the
/// authority is the single simulator. Without this the client's own AI would
/// move enemies and fight the host's position snapshots (jitter / desync).
/// Ported from the BepInEx mod's ClientAIDisablePatches. Every hooked Update
/// routes through ShouldSkip, so the authority (and single-player) is untouched.
/// </summary>
public sealed class ClientAIDisable_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target, prefix: new HarmonyMethod(typeof(ClientAIDisable_Patch).GetMethod(nameof(SkipPrefix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        // Core AI + the movement/pathfinding components that also self-drive.
        // Missing types are skipped gracefully by the registry.
        yield return ("Character", "Update");
        yield return ("AIPath", "Update");
        yield return ("AILerp", "Update");
        yield return ("Pathfinding.RichAI", "Update");
        yield return ("Pathfinding.RVO.RVOController", "Update");
        yield return ("Flier", "Update");
        yield return ("Shooter", "Update");
        yield return ("RandomMovement", "Update");
        yield return ("InSightOfPlayer", "Update");
        yield return ("Sniffer", "Update");
    }

    /// <summary>Return false to skip the original AI update.</summary>
    public static bool SkipPrefix(object __instance)
    {
        try { return !ShouldSkip(__instance as Component); }
        catch { return true; }
    }

    private static bool ShouldSkip(Component comp)
    {
        if (comp == null) return false;
        var mgr = NetworkManager.Instance;
        if (mgr == null || !mgr.IsConnected) return false; // single-player / offline
        if (mgr.IsTimeAuthority) return false;             // authority simulates AI

        // Never disable the local player or remote-player clones.
        if (comp.transform.root.name.StartsWith("RemotePlayer_")) return false;
        var localPlayer = Player.Instance;
        if (localPlayer != null && comp.gameObject == localPlayer.gameObject) return false;

        return true;
    }
}
