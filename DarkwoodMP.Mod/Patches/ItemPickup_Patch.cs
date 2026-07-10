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
/// Patches world item removal so pickups/destructions disappear for other players.
/// Verified game API: Item.getDroppedItem() (player picks up a dropped item),
/// Item.die() (item destroyed by damage/fire).
/// </summary>
public sealed class ItemPickup_Patch : IPatch
{
    private static readonly HashSet<int> _alreadySent = new();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        _alreadySent.Clear(); // instance ids from a previous session are meaningless
        var postfix = new HarmonyMethod(typeof(ItemPickup_Patch).GetMethod(nameof(Postfix), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Item", "getDroppedItem");
        yield return ("Item", "die");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Component item) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            // getDroppedItem and die can both run for the same item
            if (!_alreadySent.Add(item.GetInstanceID())) return;

            // If this item is a tracked drop, send its exact sync id -
            // dropped items all share one generic name, so this is the only
            // reliable way the other side can find the right one
            var itemSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ItemSync>();
            var syncId = itemSync?.GetSyncId(item) ?? "";

            var pos = item.transform.position;
            network.SendReliable(new PickupStatePacket
            {
                PickupId = syncId,
                ItemType = "",
                ItemName = item.gameObject.name,
                X = pos.x, Y = pos.y, Z = pos.z,
                Spawned = false
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[ItemPickup_Patch] {ex.Message}");
        }
    }
}
