using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Co-op loot share on personal pickup (Horde ItemDoublePickup policy A).
/// Scales meat/mushrooms/wood/nail by party multiplier. Shared containers stay 1×.
/// </summary>
public sealed class LootScale_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.Name == "transferItemAllToPlayer" || target.Name == "grabItem")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(LootScale_Patch).GetMethod(nameof(InvSlotPrefix), statics)!));
        }
        else if (target.Name == "addItemTypeToPlayer")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(LootScale_Patch).GetMethod(nameof(AddItemTypePrefix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("InvSlot", "transferItemAllToPlayer");
        yield return ("InvSlot", "grabItem");
        yield return ("Inventory", "addItemTypeToPlayer");
    }

    public static void InvSlotPrefix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (ModConfig.Load().GetLootShareMode() == GameLogic.LootShareMode.Off) return;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return;

            var slot = __instance as InvSlot;
            if (slot == null || InvItemClass.isNull(slot.invItem)) return;
            var type = slot.invItem.type;
            if (!CoopBalance.IsScaledLootType(type))
            {
                // Also scale isExpItem
                if (slot.invItem.baseClass == null || !slot.invItem.baseClass.isExpItem)
                    return;
            }

            int mult = CoopBalance.GetPartyMultiplier();
            if (mult <= 1) return;
            slot.invItem.amount *= mult;
            ModLogger.Msg($"[LootScale] {type} ×{mult} → {slot.invItem.amount}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[LootScale] InvSlot: {ex.Message}");
        }
    }

    public static void AddItemTypePrefix(string type, ref int amount)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (ModConfig.Load().GetLootShareMode() == GameLogic.LootShareMode.Off) return;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return;
            if (!CoopBalance.IsScaledLootType(type)) return;
            int mult = CoopBalance.GetPartyMultiplier();
            if (mult <= 1) return;
            amount *= mult;
        }
        catch { }
    }
}
