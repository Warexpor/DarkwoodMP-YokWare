using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Patches item pickups so other players get a notification - and, crucially,
/// SUPPRESSES the pickup entirely while a remote change is being replayed:
/// replaying game code (barricade destruction refunds, journal item pickups,
/// ...) must never grant items to the local player.
/// Verified game API: Inventory.addItemTypeToPlayer(string type, int amount, bool dropIfNoRoom).
/// </summary>
public sealed class Inventory_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            prefix: new HarmonyMethod(typeof(Inventory_Patch).GetMethod(nameof(Prefix), statics)!),
            postfix: new HarmonyMethod(typeof(Inventory_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Inventory", "addItemTypeToPlayer");
    }

    /// <summary>Remote replays never put items into the local player's inventory
    /// (unless explicitly allowed, e.g. journal quest items/keys).</summary>
    public static bool Prefix()
    {
        return !DarkwoodMP.GameLogic.RemoteApply.Active
               || DarkwoodMP.GameLogic.RemoteApply.AllowInventoryGrant;
    }

    // Parameter names must match the game method so Harmony can bind them
    public static void Postfix(string type, int amount)
    {
        try
        {
            if (DarkwoodMP.GameLogic.RemoteApply.Active) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new InventoryUpdatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                ItemId = type,
                Quantity = amount,
                Slot = -1
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Inventory_Patch] {ex.Message}");
        }
    }
}
