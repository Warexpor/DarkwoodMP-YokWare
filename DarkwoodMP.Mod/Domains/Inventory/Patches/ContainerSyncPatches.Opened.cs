using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{

    /// <summary>Syncs transferring 1 item from player inventory to the opened container.</summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemToOpenedInv")]
    public static class ContainerTransferToOpenedInvPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSnapshotState __state)
        {
            __state = default;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (!ContainerSyncHelpers.IsContainer(destInv)) return;
            __state.Active = true;
            __state.Snapshot = ContainerSnapshotHelper.TakeSnapshot(destInv);
        }

        private static void Postfix(InvSlot __instance, ContainerSnapshotState __state)
        {
            if (!__state.Active) return;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (destInv == null) return;
            ContainerSnapshotHelper.SendDiff(destInv, __state.Snapshot);
        }
    }

    /// <summary>Syncs transferring all items from player inventory to the opened container.</summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemAllToOpenedInv")]
    public static class ContainerTransferAllToOpenedInvPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSnapshotState __state)
        {
            __state = default;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (!ContainerSyncHelpers.IsContainer(destInv)) return;
            __state.Active = true;
            __state.Snapshot = ContainerSnapshotHelper.TakeSnapshot(destInv);
        }

        private static void Postfix(InvSlot __instance, ContainerSnapshotState __state)
        {
            if (!__state.Active) return;
            Inventory destInv = Player.Instance?.openedItemInventory2 ?? Player.Instance?.openedItemInventory;
            if (destInv == null) return;
            ContainerSnapshotHelper.SendDiff(destInv, __state.Snapshot);
        }
    }

    /// <summary>Syncs placing an item into a container slot via controller input.</summary>
    [HarmonyPatch(typeof(InvSlot), "controllerPlaceItem")]
    public static class ContainerControllerPlaceItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;

            var pickedUp = Singleton<Controller>.Instance?.pickedUpItem;
            if (pickedUp == null || InvItemClass.isNull(pickedUp)) return;

            __state.Active = true;
            __state.Type = pickedUp.type;
            __state.Amount = pickedUp.amount;
            __state.Dur = pickedUp.durability;
            __state.Ammo = pickedUp.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isPlayerPlaced: true);
        }
    }

    /// <summary>
    /// Syncs container item removal when a player picks up a journal/quest/key
    /// item from a container slot via defaultActivateItem().
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "defaultActivateItem")]
    public static class ContainerDefaultActivateItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            if (InvItemClass.isNull(__instance.invItem)) return;

            var baseClass = __instance.invItem.baseClass;
            bool hadJournalComponent = baseClass != null && (
                baseClass.GetComponent<JournalNoteReference>() != null ||
                baseClass.GetComponent<KeyReference>() != null ||
                baseClass.GetComponent<QuestItemReference>() != null);

            if (!hadJournalComponent) return;

            __state.Active = true;
            __state.Type = __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, bool __result, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            // Journal items cause the slot to be emptied (removeAmount was called)
            // and the method returns true. Send a RemoveItem to clear the
            // corresponding slot on the remote peer.
            if (__result && InvItemClass.isNull(__instance.invItem))
            {
                ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo);
            }
        }
    }

    /// <summary>
    /// Syncs the 'searched' flag on containers so the remote client sees
    /// "(Searched)" when hovering over a container that the host has already
    /// opened. Also syncs Character.searched on dead bodies.
    /// On the Client side, requests the full container state from the Host
    /// so that items the Host already looted are correctly removed.
    /// </summary>
    [HarmonyPatch(typeof(Item), "openInventory")]
    public static class ContainerSearchedPatch
    {
        private static void Postfix(Item __instance)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            // Access the private 'inventory' field via Harmony Traverse
            Inventory inv = HarmonyLib.Traverse.Create(__instance).Field("inventory").GetValue<Inventory>();
            if (inv == null) return;
            var net = ModRuntime.Network;

            Vector3 pos = inv.transform.position;

            if (net.Role == NetworkRole.Host)
            {
                // Host: notify the client that this container was opened
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] SearchedPatch(HOST): sending Searched for {inv.name} at {pos}");

                var msg = new ContainerItemMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Action = ContainerAction.Searched,
                    SlotIndex = 0,
                    ItemType = "",
                    Amount = 0,
                    Durability = 0,
                    Ammo = 0,
                    IsPlayerPlaced = false
                };
                var netInst = LanNetworkManager.Instance;
                if (netInst != null)
                    netInst.Broadcast(NetMessageType.ContainerItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
            else
            {
                // Client: request the full container state from the host.
                // Include the entity's stable hash for exact lookup.
                int entityHash = 0;
                Character c = __instance.GetComponent<Character>();
                if (c != null)
                    entityHash = Sync.CharacterTracker.GetStableId(c);

                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] SearchedPatch(CLIENT): requesting state for {inv.name} at {pos} hash={entityHash}");

                var req = new ContainerStateRequestMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    TargetEntityHash = entityHash
                };
                var netInst = LanNetworkManager.Instance;
                if (netInst != null)
                    netInst.Send(NetMessageType.ContainerStateRequest, w => req.Serialize(w), DeliveryMethod.ReliableOrdered);
            }
        }
    }
}
