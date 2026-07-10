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
/// Syncs gathering of world pickups (beartraps, mushrooms and other
/// "disarmable" objects). Verified game API: Player interaction runs
/// Item.disarm(), which adds the item to the inventory and - ONLY on success -
/// calls Item.switchTriggerState() (destroys the object or switches its
/// trigger state). The send is keyed on switchTriggerState being reached from
/// inside disarm, so failed pickups (full inventory) are never broadcast.
/// The remote side replays switchTriggerState on the matching item.
/// </summary>
public sealed class Disarm_Patch : IPatch
{
    private static Component _disarming;
    private static bool _sentForCurrent;

    /// <summary>True while Item.disarm runs (used to scope trigger patches).</summary>
    public static bool IsDisarming => _disarming != null;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "disarm")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Disarm_Patch).GetMethod(nameof(DisarmPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Disarm_Patch).GetMethod(nameof(DisarmPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                postfix: new HarmonyMethod(typeof(Disarm_Patch).GetMethod(nameof(TriggerStatePostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Item", "disarm");
        yield return ("Item", "switchTriggerState");
    }

    public static void DisarmPrefix(object __instance)
    {
        if (RemoteApply.Active) return;
        _disarming = __instance as Component;
        _sentForCurrent = false;
    }

    public static void DisarmPostfix()
    {
        _disarming = null;
    }

    public static void TriggerStatePostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (_disarming == null || _sentForCurrent) return;
            if (__instance is not Component item || !ReferenceEquals(item, _disarming)) return;
            _sentForCurrent = true;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var itemId = GameIds.ForComponent(item);
            ModLogger.Msg($"[Disarm_Patch] Broadcasting pickup of '{itemId}'");
            network.SendReliable(new ItemDisarmPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemId = itemId
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Disarm_Patch] {ex.Message}");
        }
    }
}
