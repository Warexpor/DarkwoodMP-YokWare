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
/// Patches item drops so the dropped world item appears for other players.
/// Verified game API:
///   Transform Player.spawnDroppedInvItemm(bool droppingPickedUpItem, string itemType, int itemAmount)
///   Transform Player.spawnDroppedInvItem(InvItemClass _item)
/// Both return the spawned world item's Transform.
/// </summary>
public sealed class PlayerDrop_Patch : IPatch
{
    // One drop can pass through both methods - dedupe on the spawned transform
    private static int _lastSentInstance;
    private static float _lastSentTime;

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var postfixName = target.Name == "spawnDroppedInvItemm" ? nameof(TypedPostfix) : nameof(ItemClassPostfix);
        var postfix = new HarmonyMethod(typeof(PlayerDrop_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "spawnDroppedInvItemm");
        yield return ("Player", "spawnDroppedInvItem");
    }

    public static void TypedPostfix(string itemType, int itemAmount, Transform __result)
        => Send(itemType, itemAmount, __result);

    public static void ItemClassPostfix(object _item, Transform __result)
    {
        if (_item == null) return;
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = _item.GetType().GetField("type", flags)?.GetValue(_item) as string;
        var amount = _item.GetType().GetField("amount", flags)?.GetValue(_item) is int a ? a : 1;
        Send(type, amount, __result);
    }

    private static void Send(string itemType, int amount, Transform spawned)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (spawned == null || string.IsNullOrEmpty(itemType)) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var instanceId = spawned.GetInstanceID();
            if (instanceId == _lastSentInstance && Time.time - _lastSentTime < 0.5f) return;
            _lastSentInstance = instanceId;
            _lastSentTime = Time.time;

            // Track the drop under a session-unique id so a later pickup can
            // be resolved exactly (all dropped items share the same name)
            var itemSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<ItemSync>();
            var syncId = itemSync?.RegisterLocalDrop(spawned, itemType) ?? "";

            // Capture the item's per-instance state (durability/ammo/modifiers)
            // straight off the world item the game just created - the slot the
            // pickup path reads back - so the mirror keeps a dropped weapon's
            // wear and loaded ammo instead of respawning it at default state.
            var slotItem = ItemState.GetSlotItem(spawned);

            var pos = spawned.position;
            network.SendReliable(new PickupStatePacket
            {
                PickupId = syncId,
                ItemType = itemType,
                ItemName = spawned.gameObject.name,
                Amount = Math.Max(amount, 1),
                X = pos.x, Y = pos.y, Z = pos.z,
                Spawned = true,
                Durability = slotItem != null ? slotItem.durability : -1f,
                Ammo = slotItem != null ? slotItem.ammo : 0,
                ModifierQuality = slotItem != null ? (int)slotItem.modifierQuality : 0,
                Modifiers = slotItem != null ? ItemState.EncodeModifiers(slotItem) : ""
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[PlayerDrop_Patch] {ex.Message}");
        }
    }
}
