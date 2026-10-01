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

        /// <summary>
        /// The slots a vanilla grant can land in: bag then hotbar
        /// (<c>Inventory.getAllItemsInPlayer</c> scans Inventory + Hotbar, and
        /// <c>getNextFreeSlotInPlayer</c> falls through to the Hotbar when the bag is full).
        /// </summary>
        /// <summary>How much a container slot lost to a transfer call (0 when vanilla moved nothing).</summary>
        internal static int AmountMoved(InvSlot slot, int preSlotAmount)
        {
            if (slot == null) return preSlotAmount;
            if (InvItemClass.isNull(slot.invItem)) return preSlotAmount;
            return preSlotAmount - slot.invItem.amount;
        }

        private static List<InvSlot> PlayerGrantSlots()
        {
            var result = new List<InvSlot>();
            Player player = Player.Instance;
            if (player == null) return result;
            // Hotbar first: the refund walks this list from the end, and vanilla grants into the
            // first non-full stack bag-then-hotbar, so the copy a denied take added is the LAST
            // bag match before any hotbar match. A hotbar-first refund unequipped the selected
            // item while the bag kept the extra.
            if (player.Hotbar != null && player.Hotbar.slots != null)
                result.AddRange(player.Hotbar.slots);
            if (player.Inventory != null && player.Inventory.slots != null)
                result.AddRange(player.Inventory.slots);
            return result;
        }

        /// <summary>
        /// Total amount of the wire item (recipe-aware, see <see cref="ItemTypeMatchesWire"/>)
        /// in the local player's bag and hotbar. A recipe's live type is "recipe", so the plain
        /// type count cannot tell which recipe a take granted.
        /// </summary>
        internal static int CountPlayerItem(string wireType, bool isRecipe)
        {
            if (string.IsNullOrEmpty(wireType)) return 0;
            int count = 0;
            foreach (var slot in PlayerGrantSlots())
            {
                if (ItemTypeMatchesWire(slot.invItem, wireType, isRecipe))
                    count += slot.invItem.amount;
            }
            return count;
        }

        /// <summary>
        /// Remove <paramref name="toRemove"/> of the wire item from the local player's bag and
        /// hotbar, preferring the slot that still matches the granted copy's durability / ammo
        /// (non-stackable weapons and tools with different wear are not interchangeable),
        /// then falling back to the last matching slot. Returns the amount removed.
        /// <paramref name="durability"/> &lt; 0 means unknown (no preference).
        /// </summary>
        internal static int RemoveGrantedFromPlayer(
            string wireType, bool isRecipe, int toRemove, float durability, int ammo)
        {
            if (string.IsNullOrEmpty(wireType) || toRemove <= 0)
                return 0;

            List<InvSlot> slots = PlayerGrantSlots();
            int left = toRemove;
            for (int pass = 0; pass < 2 && left > 0; pass++)
            {
                for (int i = slots.Count - 1; i >= 0 && left > 0; i--)
                {
                    InvSlot s = slots[i];
                    if (!ItemTypeMatchesWire(s.invItem, wireType, isRecipe))
                        continue;
                    if (pass == 0 && durability >= 0f
                        && !(Mathf.Abs(s.invItem.durability - durability) < 0.001f
                             && s.invItem.ammo == ammo))
                        continue;
                    // removeAmount (not slot.removeItem) so a held / selected hotbar item
                    // unwinds its aim / attack / selection state the way vanilla does.
                    int take = Mathf.Min(s.invItem.amount, left);
                    left -= take;
                    s.invItem.removeAmount(take);
                }
                if (durability < 0f)
                    break; // no metadata: one pass is the fallback pass
            }
            return toRemove - left;
        }

        /// <summary>
        /// A denied container grab leaves the item on the cursor (vanilla
        /// <c>InvSlot.grabItem</c> → <c>Controller.pickedUpItem</c>), not in a bag slot.
        /// When the cursor still holds exactly what was taken from that container slot, drop it
        /// and its icon the way a cancelled grab ends. Returns true when a cursor item was cancelled.
        /// </summary>
        internal static bool TryCancelCursorItem(
            Vector3 containerPos, int slotIdx, string wireType, bool isRecipe, int amount)
        {
            var controller = Singleton<Controller>.Instance;
            InvItemClass picked = controller != null ? controller.pickedUpItem : null;
            if (InvItemClass.isNull(picked) || !ItemTypeMatchesWire(picked, wireType, isRecipe))
                return false;
            if (amount > 0 && picked.amount != amount)
                return false;

            InvSlot origin = picked.slot;
            if (origin == null || origin.inventory == null || origin.inventory.slots == null
                || origin.inventory.slots.IndexOf(origin) != slotIdx)
                return false;
            Vector3 d = origin.inventory.transform.position - containerPos;
            if (d.sqrMagnitude > 0.05f * 0.05f)
                return false;

            if (picked.UIInvItem != null)
                picked.UIInvItem.despawn();
            picked.UIInvItem = null;
            controller.pickedUpItem = null;
            var ui = Singleton<UI>.Instance;
            if (ui != null && ui.InventorySelectionPrompt != null)
                ui.InventorySelectionPrompt.Hide();
            if (Singleton<Globals>.Instance != null && Singleton<Globals>.Instance.controllerMode
                && origin.UIInvSlot != null && origin.UIInvSlot.controllerPickUp != null)
                origin.UIInvSlot.controllerPickUp.gameObject.SetActive(false);
            return true;
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
                net.RecordPendingTakePreCount(pos, slotIdx, preTakePlayerCount, isRecipe, itemType, durability, ammo);

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
        /// <summary>Stack size in the container slot before the call; what vanilla actually moved is
        /// the difference afterwards (nothing with full bags, part of a stack on a partial merge).</summary>
        public int PreSlotAmount;
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
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItem(__state.Type, __state.IsRecipe);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[Container] GrabItem: IS container (invType={__instance.inventory.invType}) idx={__state.Idx} type={__state.Type}");
        }

        private static void Postfix(InvSlot __instance, bool __runOriginal, ContainerSlotActionState __state)
        {
            if (!__state.Active || !__runOriginal) return;
            // Vanilla grabItem early-outs (item menu / leveling menu / journal note open) leave
            // the slot untouched — only a real grab empties it onto the cursor.
            if (!InvItemClass.isNull(__instance.invItem)) return;
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
            __state.PreSlotAmount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItem(__state.Type, __state.IsRecipe);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            // Full bags: vanilla moves nothing. Announcing the take anyway made the host delete a
            // slot the client never received.
            if (ContainerSyncHelpers.AmountMoved(__instance, __state.PreSlotAmount) <= 0) return;
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
            __state.PreSlotAmount = __instance.invItem.amount;
            __state.Dur = __instance.invItem.durability;
            __state.Ammo = __instance.invItem.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(__instance.invItem);
            __state.ShouldBeActive = __instance.invItem.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
            __state.PreTakePlayerCount = ContainerSyncHelpers.CountPlayerItem(__state.Type, __state.IsRecipe);
        }

        private static void Postfix(InvSlot __instance, ContainerSlotActionState __state)
        {
            if (!__state.Active) return;
            // Vanilla moves what fits: nothing with full bags, part of a stack on a partial merge.
            // Announce only that amount, or the host deletes what the client never received.
            int moved = ContainerSyncHelpers.AmountMoved(__instance, __state.PreSlotAmount);
            if (moved <= 0) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.RemoveItem, __state.Pos, __state.Idx, __state.Type, moved, __state.Dur, __state.Ammo, preTakePlayerCount: __state.PreTakePlayerCount, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
        }
    }

    /// <summary>
    /// Syncs placing an item from the player's hand into a container slot.
    /// </summary>
    [HarmonyPatch(typeof(InvSlot), "placeItem")]
    public static class ContainerPlaceItemPatch
    {
        private static void Prefix(InvSlot __instance, bool force, ref ContainerSlotActionState __state)
        {
            __state = default;
            if (!ContainerSyncHelpers.IsContainer(__instance)) return;
            // Vanilla placeItem is a no-op for a death-drop bag unless forced.
            if (!force && __instance.inventory.invType == Inventory.InvType.deathDrop) return;

            // placeItem drops Controller.pickedUpItem (the cursor stack) into the slot — not
            // Player.currentItem (the held weapon).
            var placed = Singleton<Controller>.Instance?.pickedUpItem;
            if (placed == null || InvItemClass.isNull(placed)) return;

            __state.Active = true;
            __state.IsRecipe = placed.isRecipe;
            __state.Type = __state.IsRecipe ? placed.recipeFor : placed.type;
            __state.Amount = placed.amount;
            __state.Dur = placed.durability;
            __state.Ammo = placed.ammo;
            __state.Upgrades = Sync.InvItemUpgradeWire.CollectNames(placed);
            __state.ShouldBeActive = placed.shouldBeActive;
            __state.Pos = __instance.inventory.transform.position;
            __state.Idx = __instance.inventory.slots.IndexOf(__instance);
        }

        private static void Postfix(InvSlot __instance, bool __runOriginal, ContainerSlotActionState __state)
        {
            if (!__state.Active || !__runOriginal) return;
            ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, __state.Pos, __state.Idx, __state.Type, __state.Amount, __state.Dur, __state.Ammo, isPlayerPlaced: true, isRecipe: __state.IsRecipe, upgrades: __state.Upgrades, shouldBeActive: __state.ShouldBeActive);
        }
    }
}
