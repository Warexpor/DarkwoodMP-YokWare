using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Porter spawn sync (v0.4, corrected). Caller audit of the Location NPC
/// methods showed that trader and wolfman spawns are TIME-driven
/// (Controller.startAfterNight / addNightSurviveReward) and the despawns are
/// location-exit-driven - all of which run on every machine independently and
/// idempotently (spawnTrader early-returns when the trader exists), so
/// replicating them would only risk double-fires.
///
/// The ONE player-driven path is Location.spawnPorter: it is called from
/// InvItemClass.use (the porter summon item) - without sync the porter would
/// exist only on the machine of the player who used the item.
/// </summary>
public sealed class Porter_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Porter_Patch).GetMethod(nameof(SpawnPorterPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Location", "spawnPorter");
    }

    public static void SpawnPorterPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Location location) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new LocationNpcPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                LocationId = GameIds.ForComponent(location),
                NpcToken = "porter+"
            });
            ModLogger.Msg($"[Porter_Patch] Porter summoned at '{location.gameObject.name}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Porter_Patch] {ex.Message}");
        }
    }
}
