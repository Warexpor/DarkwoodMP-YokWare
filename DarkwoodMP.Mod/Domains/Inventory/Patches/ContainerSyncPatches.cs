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
            if (slot == null || slot.inventory == null) return false;
            return IsContainer(slot.inventory);
        }

        internal static bool IsContainer(Inventory inv)
        {
            if (inv == null) return false;
            if (inv.invType != Inventory.InvType.itemInv
                && inv.invType != Inventory.InvType.deathDrop)
                return false;

            // Sprung beartrap loot uses itemInv + isDroppedItem; co-op rescue pickup
            // must not run container RemoveItem (host misses the GO → TakeDenied refund).
            if (TrapPickupGuard.IsGuarded(inv))
                return false;
            GameObject go = inv.gameObject;
            if (go != null && (Sync.TrapNetworkId.IsWorldTrap(go) || Sync.TrapNetworkId.IsOccupancyTrap(go)))
                return false;
            Trigger trig = go != null ? go.GetComponent<Trigger>() : null;
            if (trig != null && (trig.isBearTrap || trig.isChainTrap || trig.isMutatedTrap))
                return false;

            return true;
        }

        /// <summary>Count total amount of <paramref name="itemType"/> in the local player's inventory.</summary>
        internal static int CountPlayerItemType(string itemType)
        {
            var pinv = Player.Instance?.Inventory;
            if (pinv == null || pinv.slots == null || string.IsNullOrEmpty(itemType)) return 0;
            int count = 0;
            foreach (var slot in pinv.slots)
            {
                if (!InvItemClass.isNull(slot.invItem) && slot.invItem.type == itemType)
                    count += slot.invItem.amount;
            }
            return count;
        }

        /// <summary>
        /// Wire ItemType is recipeFor when IsRecipe; live slot.type is still "recipe".
        /// </summary>
        internal static bool ItemTypeMatchesWire(InvItemClass item, string wireType, bool isRecipe)
        {
            if (InvItemClass.isNull(item) || string.IsNullOrEmpty(wireType))
                return string.IsNullOrEmpty(wireType);
            if (isRecipe)
                return item.isRecipe && string.Equals(item.recipeFor, wireType, System.StringComparison.Ordinal);
            if (item.isRecipe)
                return false;
            return string.Equals(item.type, wireType, System.StringComparison.Ordinal);
        }

        internal static void SendContainerAction(ContainerAction action, Vector3 pos, int slotIdx, string itemType, int amount, float durability, int ammo, bool isPlayerPlaced = false, int preTakePlayerCount = -1, bool isRecipe = false, string[] upgrades = null, bool shouldBeActive = false)
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
                ModRuntime.LegacyInfo($"[Container] SendContainerAction: {action} pos={pos} slot={slotIdx} type={itemType} amt={amount} recipe={isRecipe}");

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
                IsPlayerPlaced = isPlayerPlaced,
                IsRecipe = isRecipe,
                Upgrades = upgrades,
                ShouldBeActive = shouldBeActive
            };
            var net = LanNetworkManager.Instance;
            if (net == null) return;
            // Host → all; client → host (Forwardable rebroadcasts to other clients).
            net.Broadcast(NetMessageType.ContainerItem, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            // Track pending removes so HandleContainerStateSync doesn't re-add
            // items the player already took (infinite loot dupe prevention).
            if (action == ContainerAction.RemoveItem || action == ContainerAction.TakeItem)
                net.RecordPendingContainerRemove(pos, slotIdx);

            // Track the pre-take player inventory count for a precise denial refund.
            if (preTakePlayerCount >= 0 && (action == ContainerAction.RemoveItem || action == ContainerAction.TakeItem))
                net.RecordPendingTakePreCount(pos, slotIdx, preTakePlayerCount);

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
        public bool IsRecipe;
        public string[] Upgrades;
        public bool ShouldBeActive;
        public Vector3 Pos;
        public int Idx;
        /// <summary>Player inventory count of <see cref="Type"/> before the take.
        /// Used by the denial refund to remove only what the take added, not
        /// pre-existing items.</summary>
        public int PreTakePlayerCount;
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
            __state.IsRecipe = __instance.invItem.isRecipe;
            __state.Type = __state.IsRecipe ? __instance.invItem.recipeFor : __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItemType(__instance.invItem.type);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Container] GrabItem: IS container (invType={__instance.inventory.invType}) idx={__state.Idx} type={__state.Type}");
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, preTakePlayerCount: __state.PreTakePlayerCount, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
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
            __state.IsRecipe = __instance.invItem.isRecipe;
            __state.Type = __state.IsRecipe ? __instance.invItem.recipeFor : __instance.invItem.type;
            __state.Amount = 1;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItemType(__instance.invItem.type);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.TakeItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, preTakePlayerCount: __state.PreTakePlayerCount, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
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
            __state.IsRecipe = __instance.invItem.isRecipe;
            __state.Type = __state.IsRecipe ? __instance.invItem.recipeFor : __instance.invItem.type;
            __state.Amount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItemType(__instance.invItem.type);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, preTakePlayerCount: __state.PreTakePlayerCount, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
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
            __state.IsRecipe = currentItem.isRecipe;
            __state.Type = __state.IsRecipe ? currentItem.recipeFor : currentItem.type;
            __state.Amount = currentItem.amount;
            __state.Dur = currentItem.durability;
            __state.Ammo = currentItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(currentItem);
            __state.ShouldBeActive = currentItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isPlayerPlaced: true, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
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
        public bool IsRecipe;
        public string[] Upgrades;
        public bool ShouldBeActive;
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
                {
                    bool isRecipe = slot.invItem.isRecipe;
                    dict[i] = new SlotSnapshot
                    {
                        Index = i,
                        Type = isRecipe ? slot.invItem.recipeFor : slot.invItem.type,
                        Amount = slot.invItem.amount,
                        Durability = slot.invItem.durability,
                        Ammo = slot.invItem.ammo,
                        IsRecipe = isRecipe,
                        Upgrades = Sync.InvItemUpgradeWire.CollectNames(slot.invItem),
                        ShouldBeActive = slot.invItem.shouldBeActive
                    };
                }
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
                        ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount - prev.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true, isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                }
                else
                {
                    ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true, isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                }
            }
        }

        /// <summary>
        /// Full before/after sync for craft (and similar) mutations that both
        /// remove ingredients from and optionally stack products into a shared
        /// pile. Place-only <see cref="SendDiff"/> misses ingredient consume.
        /// </summary>
        internal static void SendFullDiff(Inventory inv, Dictionary<int, SlotSnapshot> before)
        {
            if (before == null || inv == null) return;
            var after = TakeSnapshot(inv);
            Vector3 pos = inv.transform.position;

            foreach (var kv in before)
            {
                if (!after.TryGetValue(kv.Key, out var now))
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    continue;
                }

                if (kv.Value.Type != now.Type || kv.Value.IsRecipe != now.IsRecipe)
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.PlaceItem, pos, kv.Key, now.Type, now.Amount,
                        now.Durability, now.Ammo, isPlayerPlaced: true, isRecipe: now.IsRecipe,
                        upgrades: now.Upgrades, shouldBeActive: now.ShouldBeActive);
                    continue;
                }

                if (now.Amount < kv.Value.Amount)
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type,
                        kv.Value.Amount - now.Amount, kv.Value.Durability, kv.Value.Ammo,
                        isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades,
                        shouldBeActive: kv.Value.ShouldBeActive);
                }
                else if (now.Amount == kv.Value.Amount
                    && System.Math.Abs(now.Durability - kv.Value.Durability) > 0.001f)
                {
                    // Durability-only drain (recipe durabilityAmount): rewrite slot.
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.PlaceItem, pos, kv.Key, now.Type, now.Amount,
                        now.Durability, now.Ammo, isPlayerPlaced: true, isRecipe: now.IsRecipe,
                        upgrades: now.Upgrades, shouldBeActive: now.ShouldBeActive);
                }
            }

            SendDiff(inv, before);
        }
    }

    /// <summary>Per-invocation snapshot state for transfer-to-opened-inventory patches.</summary>
    internal struct ContainerSnapshotState
    {
        public bool Active;
        public Dictionary<int, SlotSnapshot> Snapshot;
    }
}
