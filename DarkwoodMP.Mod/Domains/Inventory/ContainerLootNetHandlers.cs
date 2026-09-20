using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Container loot take/put/place + deny/refund + hideout/reputation.
    /// </summary>
    internal sealed class ContainerLootNetHandlers
    {
        private readonly LanNetworkManager _net;
        private readonly ContainerPendingNetHandlers _pending;

        private const int MaxContainerPlaceAmount = 999;

        internal ContainerLootNetHandlers(LanNetworkManager net, ContainerPendingNetHandlers pending)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
            _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));
        }

        internal void HandleHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            ExperienceMachine best = WorldQueryHelper.FindNearest<ExperienceMachine>(pos, 0.5f);
            if (best == null)
            {
                ModRuntime.Log?.LogWarning("[HideoutUpgrade] no ExperienceMachine found near " + pos);
                return;
            }

            if (msg.IsOn && !best.isOn)
                best.enable();
            else if (!msg.IsOn && best.isOn)
                best.disable();
        }

        internal void HandleContainerItem(ContainerItemMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Inventory inv = WorldQueryHelper.FindInventoryByPos(pos);
            if (inv == null)
            {
                ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: no inventory at {pos} for {msg.Action} slot={msg.SlotIndex} type={msg.ItemType}");
                        // Host: peer took from a missing inventory; refund the optimistic loot.
                if (_net.Role == NetworkRole.Host
                    && (msg.Action == ContainerAction.TakeItem || msg.Action == ContainerAction.RemoveItem)
                    && _net.CurrentReceivePlayerId > 0)
                {
                    DenyContainerTake(_net.CurrentReceivePlayerId, msg, "no inventory");
                }
                return;
            }

            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: {msg.Action} inv={inv.name} type={inv.invType} pos={pos} slot={msg.SlotIndex} type={msg.ItemType} amt={msg.Amount}");

            if (msg.Action == ContainerAction.TakeItem || msg.Action == ContainerAction.RemoveItem)
            {
            // Apply the removal only if the slot still contains the matching item.
                // Simultaneous dual-loot: second peer loses the race → deny + refund.
                if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState)
                {
                    if (!TryHostValidateContainerTake(inv, msg, out string denyReason))
                    {
                        if (_net.CurrentReceivePlayerId > 0)
                            DenyContainerTake(_net.CurrentReceivePlayerId, msg, denyReason);
                        else
                            ModLog.Warn(LogCat.Container,
                                "[Container] local host take race: " + denyReason);
                        return;
                    }
                }

                if (msg.SlotIndex < inv.slots.Count)
                {
                    InvSlot slot = inv.slots[msg.SlotIndex];
                    if (!InvItemClass.isNull(slot.invItem))
                    {
                        // Soft type check on clients (host already validated).
                        if (!string.IsNullOrEmpty(msg.ItemType)
                            && !string.Equals(slot.invItem.type, msg.ItemType, System.StringComparison.Ordinal))
                        {
                            ModRuntime.Log?.LogWarning(
                                $"[Container] HandleContainerItem: type mismatch slot {msg.SlotIndex} "
                                + $"have={slot.invItem.type} msg={msg.ItemType}");
                            return;
                        }

                        if (msg.Amount >= slot.invItem.amount)
                        {
                            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: removing {slot.invItem.type} x{slot.invItem.amount} from slot {msg.SlotIndex}");
                            slot.removeItem();
                        }
                        else
                        {
                            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: removing {msg.Amount} from {slot.invItem.type} (had {slot.invItem.amount})");
                            slot.invItem.removeAmount(msg.Amount);
                        }

                        // World dropped-item pickups (shiny stone): empty inventory still leaves the GO.
                        // DestroyEmptyItemInvAt only destroys Item.isDroppedItem, not wardrobes or chests.
                        if (inv.invType == Inventory.InvType.itemInv)
                        {
                            try { Sync.WorldPhysicsSyncService.DestroyEmptyItemInvAt(pos); }
                            catch (System.Exception ex)
                            {
                                if (ModRuntime.VerboseLogging)
                                    ModRuntime.Log?.LogWarning("[Container] empty itemInv destroy: " + ex.Message);
                            }
                        }
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot {msg.SlotIndex} already empty (type={msg.ItemType})");
                        if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0 && !LanNetworkManager.IsApplyingRemoteState)
                            DenyContainerTake(_net.CurrentReceivePlayerId, msg, "slot empty");
                        // Already empty itemInv; still try to clear the ghost mesh.
                        if (inv.invType == Inventory.InvType.itemInv)
                        {
                            try { Sync.WorldPhysicsSyncService.DestroyEmptyItemInvAt(pos); }
                            catch { /* non-fatal */ }
                        }
                    }
                }
                else
                {
                    ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot index {msg.SlotIndex} >= slots count {inv.slots.Count}");
                    if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0 && !LanNetworkManager.IsApplyingRemoteState)
                        DenyContainerTake(_net.CurrentReceivePlayerId, msg, "bad slot index");
                }
            }
            else if (msg.Action == ContainerAction.PlaceItem)
            {
                // Host trust: clamp a client-side place amount so a peer cannot mint
                // items into a container with an oversized amount field.
                if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                    && (msg.Amount <= 0 || msg.Amount > MaxContainerPlaceAmount))
                {
                    _net._suppressForwardThisMessage = true;
                    ModLog.Warn(LogCat.Container,
                        "[Container] PlaceItem denied — amount out of bounds: " + msg.Amount);
                    return;
                }

                if (msg.IsPlayerPlaced)
                    Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(pos, msg.SlotIndex);

                if (msg.SlotIndex < inv.slots.Count)
                {
                    InvSlot slot = inv.slots[msg.SlotIndex];
                    if (InvItemClass.isNull(slot.invItem))
                    {
                        slot.createItem(msg.ItemType, msg.Amount, msg.Durability > 0f ? msg.Durability : 1f);
                        if (msg.Ammo > 0 && !InvItemClass.isNull(slot.invItem))
                            slot.invItem.ammo = msg.Ammo;
                    }
                    else if (slot.invItem.type == msg.ItemType)
                    {
                        slot.invItem.amount += msg.Amount;
                        slot.invItem.refresh();
                    }
                    else if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState)
                    {
                        // Place race: the slot contains a different type; do not overwrite it.
                        ModLog.Warn(LogCat.Container,
                            "[Container] PlaceItem denied — slot occupied by " + slot.invItem.type);
                        _net._suppressForwardThisMessage = true;
                    }
                }
            }
            else if (msg.Action == ContainerAction.Searched)
            {
                Item item = inv.GetComponent<Item>();
                if (item != null)
                {
                    item.searched = true;
                    Character c = inv.GetComponent<Character>();
                    if (c != null)
                        c.searched = true;
                }
            }
        }

        /// <summary>Host: slot still holds the claimed type/amount for a take/remove.</summary>
        internal static bool TryHostValidateContainerTake(Inventory inv, ContainerItemMessage msg, out string reason)
        {
            reason = null;
            if (inv == null || inv.slots == null)
            {
                reason = "no inv";
                return false;
            }
            if (msg.SlotIndex >= inv.slots.Count)
            {
                reason = "bad slot";
                return false;
            }
            InvSlot slot = inv.slots[msg.SlotIndex];
            if (InvItemClass.isNull(slot.invItem))
            {
                reason = "slot empty";
                return false;
            }
            if (!string.IsNullOrEmpty(msg.ItemType)
                && !string.Equals(slot.invItem.type, msg.ItemType, System.StringComparison.Ordinal))
            {
                reason = "type mismatch have=" + slot.invItem.type;
                return false;
            }
            if (msg.Amount <= 0)
            {
                reason = "bad amount";
                return false;
            }
            return true;
        }

        internal void DenyContainerTake(int playerId, ContainerItemMessage msg, string reason)
        {
            _net._suppressForwardThisMessage = true;
            ModLog.Event(LogCat.Container,
                "[Container] H6 deny take p" + playerId + " slot=" + msg.SlotIndex
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
                    Amount = msg.Amount > 0 ? msg.Amount : 1
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
                slots.Add(new SlotStateEntry
                {
                    SlotIndex = (byte)i,
                    ItemType = s.invItem.type,
                    Amount = s.invItem.amount,
                    Durability = s.invItem.durability,
                    Ammo = s.invItem.ammo
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

            // Build the pending pre-count key from the denied message (matches
            // the key format in RecordPendingTakePreCount).
            string preKey = $"{msg.PosX:F2}_{msg.PosY:F2}_{msg.PosZ:F2}_{msg.SlotIndex}";
            _pending.ConsumePendingTakePreCount(preKey, out int preTakeCount);

            ModLog.Event(LogCat.Container,
                "[Container] take denied by host — refunding " + msg.ItemType + " x" + msg.Amount
                + " (preTakeCount=" + preTakeCount + ")");

            try
            {
                Inventory pinv = Player.Instance != null ? Player.Instance.Inventory : null;
                if (pinv == null || pinv.slots == null || string.IsNullOrEmpty(msg.ItemType) || msg.Amount <= 0)
                    return;

                int totalNow = ContainerSyncHelpers.CountPlayerItemType(msg.ItemType);

                int toRemove;
                if (preTakeCount >= 0)
                {
                    // Precise refund: calculate what the take actually added.
                    // If the player already had some of this type, only
                    // remove the surplus, not the pre-existing items.
                    toRemove = Math.Max(0, totalNow - preTakeCount);
                }
                else
                {
                    // No pre-count recorded, for example after a reconnect;
                    // fall back to the old type-scan behavior.
                    toRemove = msg.Amount;
                }

                if (toRemove <= 0)
                {
                    ModLog.Warn(LogCat.Container,
                        "[Container] refund: nothing to remove (totalNow=" + totalNow
                        + " preTakeCount=" + preTakeCount + ")");
                    return;
                }

                int left = Math.Min(toRemove, totalNow);
                for (int i = pinv.slots.Count - 1; i >= 0 && left > 0; i--)
                {
                    InvSlot s = pinv.slots[i];
                    if (InvItemClass.isNull(s.invItem)) continue;
                    if (!string.Equals(s.invItem.type, msg.ItemType, System.StringComparison.Ordinal))
                        continue;
                    if (s.invItem.amount <= left)
                    {
                        left -= s.invItem.amount;
                        s.removeItem();
                    }
                    else
                    {
                        s.invItem.removeAmount(left);
                        left = 0;
                    }
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Container, "refund failed: " + ex.Message);
            }

            try
            {
                if (Player.Instance != null)
                    Player.Instance.displayMessage("Already taken…");
            }
            catch { /* ignore */ }
        }
        /// <summary>
        /// Apply shared NPC reputation. Host and clients both apply;
        /// night-trader names are ignored (per-player). Writes Flags.npcStates
        /// directly so it works if the NPC GameObject is not loaded yet.
        /// </summary>
        internal void HandleReputationSync(ReputationSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            if (Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(msg.NpcName))
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            var state = flags.getNPCState(msg.NpcName);
            if (state != null)
            {
                state.reputation = msg.Reputation;
            }
            else
            {
                state = new Flags.NPCState
                {
                    name = msg.NpcName,
                    reputation = msg.Reputation,
                    wantsToTalk = true
                };
                flags.npcStates.Add(state);
            }

            ModRuntime.LegacyInfo($"[RepSync] applied shared rep '{msg.NpcName}': {msg.Reputation}");
        }
    }
}
