using System;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Container take/place deny + refund + state snapshot push.</summary>
    internal sealed partial class ContainerLootNetHandlers
    {
        internal void DenyContainerTake(int playerId, ContainerItemMessage msg, string reason)
        {
            SendContainerDenied(playerId, msg, reason, ContainerTakeDeniedMessage.ModeTakeRefund);
        }

        /// <summary>
        /// Host: place lost the race (type clash / bad amount / stack overflow).
        /// Refunds the optimistic place into the peer bag and snaps the container.
        /// </summary>
        internal void DenyContainerPlace(int playerId, ContainerItemMessage msg, string reason)
        {
            SendContainerDenied(playerId, msg, reason, ContainerTakeDeniedMessage.ModePlaceRefund);
        }

        private void SendContainerDenied(int playerId, ContainerItemMessage msg, string reason, byte mode)
        {
            _net._suppressForwardThisMessage = true;
            string kind = mode == ContainerTakeDeniedMessage.ModePlaceRefund ? "place" : "take";
            ModLog.Event(LogCat.Container,
                "[Container] H6 deny " + kind + " p" + playerId + " slot=" + msg.SlotIndex
                + " type=" + msg.ItemType + " amt=" + msg.Amount + " (" + reason + ")");

            _net.SendToPlayer(playerId, NetMessageType.ContainerTakeDenied, w =>
            {
                new ContainerTakeDeniedMessage
                {
                    PosX = msg.PosX,
                    PosY = msg.PosY,
                    PosZ = msg.PosZ,
                    SlotIndex = msg.SlotIndex,
                    ItemType = msg.ItemType ?? "",
                    Amount = msg.Amount > 0 ? msg.Amount : 1,
                    Mode = mode,
                    Durability = msg.Durability,
                    Ammo = msg.Ammo,
                    Upgrades = msg.Upgrades,
                    ShouldBeActive = msg.ShouldBeActive
                }.Serialize(w);
            }, LiteNetLib.DeliveryMethod.ReliableOrdered);

            // Push authoritative container snapshot so UI matches host.
            try
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Inventory inv = WorldQueryHelper.FindInventoryByPos(pos);
                if (inv != null)
                    SendContainerStateSnapshotTo(playerId, inv, pos);
            }
            catch { /* ignore */ }
        }

        internal void SendContainerStateSnapshotTo(int playerId, Inventory inv, Vector3 pos)
        {
            if (inv == null || inv.slots == null) return;
            var slots = new System.Collections.Generic.List<SlotStateEntry>();
            for (int i = 0; i < inv.slots.Count; i++)
            {
                InvSlot s = inv.slots[i];
                if (InvItemClass.isNull(s.invItem)) continue;
                InvItemClass it = s.invItem;
                bool isRecipe = it.isRecipe;
                slots.Add(new SlotStateEntry
                {
                    SlotIndex = (byte)i,
                    ItemType = isRecipe ? it.recipeFor : it.type,
                    Amount = it.amount,
                    Durability = it.durability,
                    Ammo = it.ammo,
                    IsRecipe = isRecipe,
                    Upgrades = Sync.InvItemUpgradeWire.CollectNames(it),
                    ShouldBeActive = it.shouldBeActive
                });
            }
            var sync = new ContainerStateSyncMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                EntityHash = 0,
                SlotCount = slots.Count,
                Slots = slots.ToArray()
            };
            _net.SendToPlayer(playerId, NetMessageType.ContainerStateSync,
                w => sync.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
        }

        internal void HandleContainerTakeDenied(ContainerTakeDeniedMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;

            if (msg.Mode == ContainerTakeDeniedMessage.ModePlaceRefund)
            {
                HandleContainerPlaceDenied(msg);
                return;
            }

            // Build the pending pre-count key from the denied message (matches
            // the key format in RecordPendingTakePreCount).
            string preKey = $"{msg.PosX:F2}_{msg.PosY:F2}_{msg.PosZ:F2}_{msg.SlotIndex}";
            bool haveTake = _pending.ConsumePendingTake(preKey, out ContainerPendingNetHandlers.PendingTake take);

            ModLog.Event(LogCat.Container,
                "[Container] take denied by host — refunding " + msg.ItemType + " x" + msg.Amount
                + " (preTakeCount=" + take.PreCount + ")");

            // The host still owns this slot: the ContainerStateSync that follows the deny must
            // show it, so the optimistic local-remove mark for it must not hide it.
            Vector3 containerPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            _pending.ClearPendingContainerRemove(containerPos, msg.SlotIndex);

            try
            {
                Inventory pinv = Player.Instance != null ? Player.Instance.Inventory : null;
                if (pinv == null || pinv.slots == null || string.IsNullOrEmpty(msg.ItemType) || msg.Amount <= 0)
                    return;

                // The pending record knows what the optimistic take actually granted
                // (recipe flag, durability, ammo); the wire message only carries the type.
                bool isRecipe = haveTake && take.IsRecipe;
                string wireType = haveTake && !string.IsNullOrEmpty(take.ItemType) ? take.ItemType : msg.ItemType;
                float durability = haveTake ? take.Durability : -1f;
                int ammo = haveTake ? take.Ammo : 0;

                // Vanilla grabItem parks the item on the cursor, not in a bag slot — a denied
                // drag-take is undone by dropping the cursor stack, not by a bag search.
                bool cursorCancelled = ContainerSyncHelpers.TryCancelCursorItem(
                    containerPos, msg.SlotIndex, wireType, isRecipe, msg.Amount);

                int totalNow = ContainerSyncHelpers.CountPlayerItem(wireType, isRecipe);

                int toRemove;
                if (take.PreCount >= 0)
                {
                    // Precise refund: calculate what the take actually added.
                    // If the player already had some of this type, only
                    // remove the surplus, not the pre-existing items.
                    toRemove = Math.Max(0, totalNow - take.PreCount);
                }
                else if (cursorCancelled)
                {
                    // The cursor stack was the whole take; nothing reached the bag.
                    toRemove = 0;
                }
                else
                {
                    // No pre-count recorded, for example after a reconnect;
                    // fall back to the claimed amount.
                    toRemove = msg.Amount;
                }

                if (toRemove <= 0)
                {
                    if (!cursorCancelled)
                    {
                        ModLog.Warn(LogCat.Container,
                            "[Container] refund: nothing to remove (totalNow=" + totalNow
                            + " preTakeCount=" + take.PreCount + ")");
                    }
                    return;
                }

                ContainerSyncHelpers.RemoveGrantedFromPlayer(
                    wireType, isRecipe, Math.Min(toRemove, totalNow), durability, ammo);
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Container, "refund failed: " + ex.Message);
            }

            try
            {
                if (Player.Instance != null)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage("Already taken…"); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Host rejected an optimistic PlaceItem. ContainerStateSync (sent with the deny)
        /// clears the local container slot; restore the item into the player bag.
        /// </summary>
        private void HandleContainerPlaceDenied(ContainerTakeDeniedMessage msg)
        {
            ModLog.Event(LogCat.Container,
                "[Container] place denied by host — restoring " + msg.ItemType + " x" + msg.Amount
                + " to bag");

            _pending.ClearPendingContainerRemove(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.SlotIndex);

            try
            {
                if (Player.Instance == null || Player.Instance.Inventory == null
                    || string.IsNullOrEmpty(msg.ItemType) || msg.Amount <= 0)
                    return;

                Inventory pinv = Player.Instance.Inventory;
                // createItem Durability arg is 0..1 multiplier — pass 1f then absolute meta.
                InvItemClass item = new InvItemClass(msg.ItemType, 1f, msg.Amount);
                if (item.baseClass == null)
                {
                    pinv.addItemType(msg.ItemType, msg.Amount);
                }
                else
                {
                    Sync.InvItemTransferApply.ApplyMeta(
                        item, msg.Durability, msg.Ammo, msg.ShouldBeActive);
                    Sync.InvItemUpgradeWire.Apply(item, msg.Upgrades);
                    // addItem → free/new slot via createItem(source); preserves ammo/dur/active.
                    InvItemClass created = pinv.addItem(item, addSlotIfNoPlace: true);
                    if (InvItemClass.isNull(created))
                    {
                        ModLog.Warn(LogCat.Container,
                            "[Container] place refund: addItem failed for " + msg.ItemType);
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Container, "place refund failed: " + ex.Message);
            }

            try
            {
                if (Player.Instance != null)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage("Could not place — returned to bag"); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch { /* ignore */ }
        }
    }
}
