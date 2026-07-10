using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Shared helper methods for container (item-inventory) interaction sync.
    /// </summary>
    internal static class ContainerSyncHelpers
    {
        internal static bool IsContainer(InvSlot slot)
        {
            return slot.inventory != null &&
                (slot.inventory.invType == Inventory.InvType.itemInv ||
                 slot.inventory.invType == Inventory.InvType.deathDrop);
        }

        internal static bool IsContainer(Inventory inv)
        {
            return inv != null &&
                (inv.invType == Inventory.InvType.itemInv ||
                 inv.invType == Inventory.InvType.deathDrop);
        }

        internal static void SendContainerAction(ContainerAction action, Vector3 pos, int slotIdx, string itemType, int amount, float durability, int ammo, bool isPlayerPlaced = false)
        {
            if (LanNetworkManager.IsApplyingRemoteState)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] SendContainerAction BLOCKED (applying remote state): {action} slot={slotIdx} type={itemType}");
                return;
            }
            if (Core.loadingGame) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (slotIdx < 0)
            {
                ModRuntime.Log?.LogWarning($"[Container] SendContainerAction BLOCKED (bad slotIdx): {action} slot={slotIdx} type={itemType}");
                return;
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Container] SendContainerAction: {action} pos={pos} slot={slotIdx} type={itemType} amt={amount}");

            var msg = new ContainerItemMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                Action = action,
                SlotIndex = (byte)slotIdx,
                ItemType = itemType ?? "",
                Amount = amount,
                Durability = durability,
                Ammo = ammo,
                IsPlayerPlaced = isPlayerPlaced
            };
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            // Host → all; client → host (Forwardable rebroadcasts to other clients).
            net.Broadcast(NetMessageType.ContainerItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            // Track pending removes so HandleContainerStateSync doesn't re-add
            // items the player already took (infinite loot dupe prevention).
            if (action == ContainerAction.RemoveItem || action == ContainerAction.TakeItem)
                net.RecordPendingContainerRemove(pos, slotIdx);

            // Dream item pickup visual for spectators / other peers (host → all via Broadcast).
            if (Sync.DreamSyncManager.IsDreamActive && (action == ContainerAction.TakeItem || action == ContainerAction.RemoveItem))
            {
                net.Broadcast(NetMessageType.DreamItemPickup,
                    w => new DreamItemPickupMessage
                    {
                        ItemType = itemType ?? "",
                        Amount = amount,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
            }
        }
    }

    /// <summary>Per-invocation state for container slot Prefix/Postfix (avoids static reentrancy bugs).</summary>
    internal struct ContainerSlotActionState
    {
        public bool Active;
        public string Type;
        public int Amount;
        public float Dur;
        public int Ammo;
        public Vector3 Pos;
        public int Idx;
    }

    /// <summary>
    /// Syncs container item removal when a player grabs an item from a
    /// container slot (e.g. looting a crate).
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "grabItem")]
    public static class ContainerGrabItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance))
            {
                if (__instance?.inventory != null && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] GrabItem: NOT container (invType={__instance.inventory.invType}) slot={__instance.inventory.slots.IndexOf(__instance)}");
                return;
            }
            if (InvItemClass.isNull(__instance.invItem)) return;

            __state.Active = true;
            __state.Type = __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Container] GrabItem: IS container (invType={__instance.inventory.invType}) idx={__state.Idx} type={__state.Type}");
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo);
        }
    }

    /// <summary>
    /// Syncs container item transfer to player inventory (single item
    /// from a container slot).
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemToPlayer")]
    public static class ContainerTransferItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            if (InvItemClass.isNull(__instance.invItem)) return;

            __state.Active = true;
            __state.Type = __instance.invItem.type;
            __state.Amount = 1;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.TakeItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo);
        }
    }

    /// <summary>
    /// Syncs transferring all items from a container slot to the player
    /// inventory (e.g. shift-click to grab a stack).
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "transferItemAllToPlayer")]
    public static class ContainerTransferAllPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            if (InvItemClass.isNull(__instance.invItem)) return;

            __state.Active = true;
            __state.Type = __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo);
        }
    }

    /// <summary>
    /// Syncs placing an item from the player's hand into a container slot.
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "placeItem")]
    public static class ContainerPlaceItemPatch
    {
        private static void Prefix(InvSlot __instance, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;

            var currentItem = Player.Instance?.currentItem;
            if (currentItem == null || InvItemClass.isNull(currentItem)) return;

            __state.Active = true;
            __state.Type = currentItem.type;
            __state.Amount = currentItem.amount;
            __state.Dur = currentItem.durability;
            __state.Ammo = currentItem.ammo;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isPlayerPlaced: true);
        }
    }

    /// <summary>Slot state snapshot for change detection.</summary>
    internal struct SlotSnapshot
    {
        public int Index;
        public string Type;
        public int Amount;
        public float Durability;
        public int Ammo;
    }

    /// <summary>
    /// Shared helper for taking inventory snapshots and sending diffs.
    /// </summary>
    internal static class ContainerSnapshotHelper
    {
        internal static Dictionary<int, SlotSnapshot> TakeSnapshot(Inventory inv)
        {
            var dict = new Dictionary<int, SlotSnapshot>();
            for (int i = 0; i < inv.slots.Count; i++)
            {
                var slot = inv.slots[i];
                if (!InvItemClass.isNull(slot.invItem))
                    dict[i] = new SlotSnapshot { Index = i, Type = slot.invItem.type, Amount = slot.invItem.amount, Durability = slot.invItem.durability, Ammo = slot.invItem.ammo };
            }
            return dict;
        }

        internal static void SendDiff(Inventory inv, Dictionary<int, SlotSnapshot> before)
        {
            if (before == null || inv == null) return;
            var after = TakeSnapshot(inv);
            Vector3 pos = inv.transform.position;

            foreach (var kv in after)
            {
                if (before.TryGetValue(kv.Key, out var prev))
                {
                    if (kv.Value.Type == prev.Type && kv.Value.Amount > prev.Amount)
                        ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount - prev.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true);
                }
                else
                {
                    ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true);
                }
            }
        }
    }

    /// <summary>Per-invocation snapshot state for transfer-to-opened-inventory patches.</summary>
    internal struct ContainerSnapshotState
    {
        public bool Active;
        public Dictionary<int, SlotSnapshot> Snapshot;
    }

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
