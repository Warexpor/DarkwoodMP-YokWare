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
    internal sealed partial class ContainerLootNetHandlers
    {
        private readonly LanNetworkManager _net;
        private readonly ContainerPendingNetHandlers _pending;

        private const int MaxContainerPlaceAmount = 999;
        private const float HideoutUpgradeFindRadius = 1.5f;
        private const int MaxPendingHideoutUpgrades = 32;
        private readonly System.Collections.Generic.List<HideoutUpgradeMessage> _pendingHideoutUpgrades =
            new System.Collections.Generic.List<HideoutUpgradeMessage>(8);

        internal ContainerLootNetHandlers(LanNetworkManager net, ContainerPendingNetHandlers pending)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
            _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));
        }

        internal void HandleHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            ApplyHideoutUpgrade(msg, queueIfMissing: true);
        }

        internal void ApplyHideoutUpgrade(HideoutUpgradeMessage msg, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // 0.5f was too tight vs HideoutStateSync (1f) / oven key rounding — late/live miss.
            ExperienceMachine best = WorldQueryHelper.FindNearest<ExperienceMachine>(pos, HideoutUpgradeFindRadius);
            if (best == null)
            {
                if (queueIfMissing)
                {
                    for (int i = _pendingHideoutUpgrades.Count - 1; i >= 0; i--)
                    {
                        var p = _pendingHideoutUpgrades[i];
                        if (Mathf.Abs(p.PosX - msg.PosX) < 0.05f
                            && Mathf.Abs(p.PosY - msg.PosY) < 0.05f
                            && Mathf.Abs(p.PosZ - msg.PosZ) < 0.05f)
                            _pendingHideoutUpgrades.RemoveAt(i);
                    }
                    if (_pendingHideoutUpgrades.Count >= MaxPendingHideoutUpgrades)
                        _pendingHideoutUpgrades.RemoveAt(0);
                    _pendingHideoutUpgrades.Add(msg);
                    ModRuntime.LegacyInfo("[HideoutUpgrade] queued — no ExperienceMachine near " + pos);
                }
                else
                    ModRuntime.Log?.LogWarning("[HideoutUpgrade] no ExperienceMachine found near " + pos);
                return;
            }

            if (msg.IsOn && !best.isOn)
                best.enable();
            else if (!msg.IsOn && best.isOn)
                best.disable();
        }

        internal void TryFlushPendingHideoutUpgrades()
        {
            if (_pendingHideoutUpgrades.Count == 0) return;
            for (int i = _pendingHideoutUpgrades.Count - 1; i >= 0; i--)
            {
                var msg = _pendingHideoutUpgrades[i];
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                if (WorldQueryHelper.FindNearest<ExperienceMachine>(pos, HideoutUpgradeFindRadius) == null)
                    continue;
                _pendingHideoutUpgrades.RemoveAt(i);
                ApplyHideoutUpgrade(msg, queueIfMissing: false);
            }
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
                            && !Patches.ContainerSyncHelpers.ItemTypeMatchesWire(
                                slot.invItem, msg.ItemType, msg.IsRecipe))
                        {
                            ModRuntime.Log?.LogWarning(
                                $"[Container] HandleContainerItem: type mismatch slot {msg.SlotIndex} "
                                + $"have={slot.invItem.type} msg={msg.ItemType} recipe={msg.IsRecipe}");
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
                        // Death bag emptied by take: fan Looted now (do not wait for opener hide).
                        if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                            && inv.invType == Inventory.InvType.deathDrop)
                            _net.CombatHandlers?.TryHostFanDeathBagEmptied(inv);
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
                        if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                            && inv.invType == Inventory.InvType.deathDrop)
                            _net.CombatHandlers?.TryHostFanDeathBagEmptied(inv);
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
                // Host trust: reject out-of-bounds place amounts (no silent mint / vanish).
                if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                    && (msg.Amount <= 0 || msg.Amount > MaxContainerPlaceAmount))
                {
                    if (_net.CurrentReceivePlayerId > 0)
                        DenyContainerPlace(_net.CurrentReceivePlayerId, msg, "amount out of bounds");
                    else
                    {
                        _net._suppressForwardThisMessage = true;
                        ModLog.Warn(LogCat.Container,
                            "[Container] PlaceItem denied — amount out of bounds: " + msg.Amount);
                    }
                    return;
                }

                if (msg.SlotIndex < inv.slots.Count)
                {
                    InvSlot slot = inv.slots[msg.SlotIndex];
                    if (InvItemClass.isNull(slot.invItem))
                    {
                        InvItemClass created = slot.createItem(msg.ItemType, msg.Amount, 1f,
                            InvItem.ModifierQuality.none, msg.IsRecipe);
                        if (!InvItemClass.isNull(created))
                        {
                            Sync.InvItemTransferApply.ApplyMeta(
                                created, msg.Durability, msg.Ammo, msg.ShouldBeActive);
                            Sync.InvItemUpgradeWire.Apply(created, msg.Upgrades);
                        }
                        // Only arm after a successful place (deny must not mark).
                        if (msg.IsPlayerPlaced)
                            Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(pos, msg.SlotIndex);
                    }
                    else if ((!msg.IsRecipe && slot.invItem.type == msg.ItemType)
                        || (msg.IsRecipe && slot.invItem.isRecipe
                            && slot.invItem.recipeFor == msg.ItemType))
                    {
                        // Stack merge race: reject if combined stack would exceed the place cap
                        // (otherwise the placer loses the overflow silently).
                        long merged = (long)slot.invItem.amount + msg.Amount;
                        if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                            && merged > MaxContainerPlaceAmount)
                        {
                            if (_net.CurrentReceivePlayerId > 0)
                                DenyContainerPlace(_net.CurrentReceivePlayerId, msg, "stack overflow");
                            else
                            {
                                _net._suppressForwardThisMessage = true;
                                ModLog.Warn(LogCat.Container,
                                    "[Container] PlaceItem denied — stack overflow to " + merged);
                            }
                            return;
                        }
                        slot.invItem.amount += msg.Amount;
                        slot.invItem.refresh();
                        if (msg.IsPlayerPlaced)
                            Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(pos, msg.SlotIndex);
                    }
                    else if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState)
                    {
                        // Place race: the slot contains a different type; do not overwrite.
                        // Without a refund the placer already removed the item from their bag
                        // and kept it only in the local container — item vanish on deny.
                        if (_net.CurrentReceivePlayerId > 0)
                            DenyContainerPlace(_net.CurrentReceivePlayerId, msg,
                                "slot occupied by " + slot.invItem.type);
                        else
                        {
                            _net._suppressForwardThisMessage = true;
                            ModLog.Warn(LogCat.Container,
                                "[Container] PlaceItem denied — slot occupied by " + slot.invItem.type);
                        }
                    }
                }
                else if (_net.Role == NetworkRole.Host && !LanNetworkManager.IsApplyingRemoteState
                    && _net.CurrentReceivePlayerId > 0)
                {
                    DenyContainerPlace(_net.CurrentReceivePlayerId, msg, "bad slot index");
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
                && !Patches.ContainerSyncHelpers.ItemTypeMatchesWire(
                    slot.invItem, msg.ItemType, msg.IsRecipe))
            {
                reason = "type mismatch have=" + slot.invItem.type
                    + (slot.invItem.isRecipe ? ("/" + slot.invItem.recipeFor) : "");
                return false;
            }
            if (msg.Amount <= 0)
            {
                reason = "bad amount";
                return false;
            }
            return true;
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
