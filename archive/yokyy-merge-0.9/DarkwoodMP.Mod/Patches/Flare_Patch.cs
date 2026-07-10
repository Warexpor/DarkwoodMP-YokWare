using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Flares are self-contained Flare objects (own Light2D + flicker + longevity),
/// NOT lightEmitter-prefab items. This patch just REGISTERS every ignited flare
/// so HeldLightSync can broadcast its position each tick (a flare MOVES when
/// thrown, so a one-shot broadcast left the light stuck where it was struck).
/// Thrown flares are excluded from the drop path (Throw_Patch) so there is no
/// broken unpickable duplicate.
/// </summary>
public sealed class Flare_Patch : IPatch
{
    /// <summary>Locally-owned live flares, read by HeldLightSync for position broadcast.</summary>
    public static readonly List<Flare> Active = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Flare_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Flare", "Start");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Flare flare || Active.Contains(flare)) return;

            // Mirrors we spawned for a PARTNER's flare (flare-light mirror,
            // armed-throw replay) run their own Start a frame later, outside
            // RemoteApply - registering them would broadcast the partner's
            // flare back at them (mirrors of mirrors, runaway lights). The
            // spawn marker identifies them.
            if (RemoteApply.IsRemoteSpawned(flare.gameObject)) return;

            Active.Add(flare);
            ModLogger.Msg($"[Flare_Patch] flare ignited/registered at {flare.transform.position:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Flare_Patch] {ex.Message}");
        }
    }
}
