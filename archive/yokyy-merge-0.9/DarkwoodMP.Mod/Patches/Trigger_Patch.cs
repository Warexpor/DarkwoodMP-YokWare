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
/// Two trigger-related fixes (verified in IL):
///
/// 1. Trigger.checkCollision resolves GetComponent&lt;Player&gt; on the collider but
///    applies the EFFECTS to Player.Instance - so a remote-player clone walking
///    over a poison mushroom poisons the OBSERVER. The prefix makes clones
///    invisible to triggers; only the machine of the player who actually steps
///    on something runs the trigger.
///
/// 2. Trigger.switchToTriggered is the state change when a trap fires
///    (beartrap snaps shut). Broadcast it so the other machine's trap shows
///    the same state instead of staying armed.
/// </summary>
public sealed class Trigger_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "checkCollision")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Trigger_Patch).GetMethod(nameof(CollisionPrefix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Trigger_Patch).GetMethod(nameof(TriggeredPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Trigger", "checkCollision");
        yield return ("Trigger", "switchToTriggered");
    }

    // Parameter name matches Trigger.checkCollision(Collider other)
    public static bool CollisionPrefix(Collider other)
    {
        try
        {
            if (other != null && other.transform.root.name.StartsWith("RemotePlayer_"))
                return false; // clones never trip triggers
        }
        catch { /* fall through */ }
        return true;
    }

    public static void TriggeredPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (Disarm_Patch.IsDisarming) return; // pickup path is synced via itemdisarm
            if (__instance is not Component trigger) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var id = GameIds.ForComponent(trigger);
            ModLogger.Msg($"[Trigger_Patch] Trap fired: '{id}'");
            network.SendReliable(new TrapFirePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemId = id
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Trigger_Patch] {ex.Message}");
        }
    }
}
