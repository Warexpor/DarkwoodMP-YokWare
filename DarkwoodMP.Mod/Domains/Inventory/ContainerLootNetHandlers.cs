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
                    ModRuntime.LegacyInfo($"[HideoutUpgrade] queued — no ExperienceMachine near {pos}");
                }
                else
                    ModRuntime.Log?.LogWarning("[HideoutUpgrade] no ExperienceMachine found near " + pos);
                return;
            }

            if (msg.IsOn && !best.isOn)
            {
                // Vanilla enable() also makes it the local player's home (and respawn point);
                // a peer lighting its oven must not move this player's home.
                Player local = Player.Instance;
                ExperienceMachine ownHome = local != null ? local.experienceMachine : null;
                best.enable();
                if (local != null)
                    local.experienceMachine = ownHome;
            }
            else if (!msg.IsOn && best.isOn)
            {
                // Another player moved out; the oven stays lit while it is still someone's home.
                Player local = Player.Instance;
                bool stillHome = (local != null && local.experienceMachine == best)
                    || Patches.OvenHomes.IsOtherPlayersHome(best, _net.CurrentReceivePlayerId);
                if (!stillHome)
                    best.disable();
            }
        }

        /// <summary>Session reset: a queued upgrade must not apply to a later session's hideout.</summary>
        internal void ClearPendingHideoutUpgrades() => _pendingHideoutUpgrades.Clear();

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

        /// <summary>The shop of the trader standing at <paramref name="pos"/> (its own spot), or null.</summary>
        private static Inventory FindTraderShop(Vector3 pos)
        {
            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                NPC npc = all[i];
                if (npc == null || !npc.trader || npc.inventory == null)
                    continue;
                if (Vector3.Distance(npc.inventory.transform.position, pos) <= 2.5f)
                    return npc.inventory;
            }
            return null;
        }

        internal void HandleContainerItem(ContainerItemMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // A closed trade window closes the trader's shop, which the container lookup (chests,
            // bodies, bags) never finds: its story trigger was dropped with a warning.
            Inventory inv = msg.Action == ContainerAction.CloseContainer ? FindTraderShop(pos) : null;
            if (inv == null)
                inv = WorldQueryHelper.FindInventoryByPos(pos);
            if (inv == null)
            {
                ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: no inventory at {pos} for {msg.Action} slot={msg.SlotIndex} type={msg.ItemType}");
                        // Host: peer took from a missing inventory; refund the optimistic loot.
                if (_net.Role == NetworkRole.Host
                    && msg.Action != ContainerAction.CloseContainer
                    && (msg.Action == ContainerAction.TakeItem || msg.Action == ContainerAction.RemoveItem)
                    && _net.CurrentReceivePlayerId > 0)
                {
                    DenyContainerTake(_net.CurrentReceivePlayerId, msg, "no inventory");
                }
                if (msg.Action == ContainerAction.CloseContainer && _net.Role == NetworkRole.Host)
                    _net.SuppressRelay();
                return;
            }

            if (msg.Action == ContainerAction.CloseContainer)
            {
                if (_net.Role == NetworkRole.Host)
                    _net.SuppressRelay();
                FireRemoteContainerStoryTrigger(inv, EventTrigger.Type.onCloseContainer, msg);
                return;
            }

            // Only inbound dispatch reaches here: on the host a positive receive id is a client
            // message (IsApplyingRemoteState is true for every inbound message, so it cannot
            // tell host-origin from client-origin).
            bool fromClient = _net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0;

            ModRuntime.LegacyInfo($"[Container] HandleContainerItem: {msg.Action} inv={inv.name} type={inv.invType} pos={pos} slot={msg.SlotIndex} type={msg.ItemType} amt={msg.Amount}");

            if (msg.Action == ContainerAction.TakeItem || msg.Action == ContainerAction.RemoveItem)
            {
                // Apply the removal only if the slot still contains the matching item.
                // Simultaneous dual-loot: second peer loses the race → deny + refund (no relay).
                if (fromClient && !TryHostValidateContainerTake(inv, msg, out string denyReason))
                {
                    DenyContainerTake(_net.CurrentReceivePlayerId, msg, denyReason);
                    return;
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
                        FireRemoteContainerStoryTrigger(inv, EventTrigger.Type.onTakeInvItem, msg);

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
                        if (_net.Role == NetworkRole.Host && inv.invType == Inventory.InvType.deathDrop)
                            _net.CombatDeathBagHandlers?.TryHostFanDeathBagEmptied(inv);
                    }
                    else
                    {
                        ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot {msg.SlotIndex} already empty (type={msg.ItemType})");
                        // Already empty itemInv; still try to clear the ghost mesh.
                        if (inv.invType == Inventory.InvType.itemInv)
                        {
                            try { Sync.WorldPhysicsSyncService.DestroyEmptyItemInvAt(pos); }
                            catch { /* non-fatal */ }
                        }
                        if (_net.Role == NetworkRole.Host && inv.invType == Inventory.InvType.deathDrop)
                            _net.CombatDeathBagHandlers?.TryHostFanDeathBagEmptied(inv);
                    }
                }
                else
                {
                    ModRuntime.Log?.LogWarning($"[Container] HandleContainerItem: slot index {msg.SlotIndex} >= slots count {inv.slots.Count}");
                }
            }
            else if (msg.Action == ContainerAction.PlaceItem)
            {
                // Host trust: reject out-of-bounds place amounts (no silent mint / vanish).
                if (fromClient && (msg.Amount <= 0 || msg.Amount > MaxContainerPlaceAmount))
                {
                    DenyContainerPlace(_net.CurrentReceivePlayerId, msg, "amount out of bounds");
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
                        FireRemoteContainerStoryTrigger(inv, EventTrigger.Type.onPlaceItem, msg);
                        // Only arm after a successful place (deny must not mark).
                        if (msg.IsPlayerPlaced)
                            Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(inv, msg.SlotIndex);
                    }
                    else if ((!msg.IsRecipe && slot.invItem.type == msg.ItemType)
                        || (msg.IsRecipe && slot.invItem.isRecipe
                            && slot.invItem.recipeFor == msg.ItemType))
                    {
                        // Stack merge race: reject if combined stack would exceed the place cap
                        // (otherwise the placer loses the overflow silently).
                        long merged = (long)slot.invItem.amount + msg.Amount;
                        if (fromClient && merged > MaxContainerPlaceAmount)
                        {
                            DenyContainerPlace(_net.CurrentReceivePlayerId, msg, "stack overflow");
                            return;
                        }
                        slot.invItem.amount += msg.Amount;
                        slot.invItem.refresh();
                        FireRemoteContainerStoryTrigger(inv, EventTrigger.Type.onPlaceItem, msg);
                        if (msg.IsPlayerPlaced)
                            Patches.ItemDoublePickupPatch.MarkContainerSlotPlayerPlaced(inv, msg.SlotIndex);
                    }
                    else if (fromClient)
                    {
                        // Place race: the slot contains a different type; do not overwrite.
                        // Without a refund the placer already removed the item from their bag
                        // and kept it only in the local container — item vanish on deny.
                        DenyContainerPlace(_net.CurrentReceivePlayerId, msg,
                            "slot occupied by " + slot.invItem.type);
                    }
                }
                else if (fromClient)
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
            // A claim larger than the host's slot would apply as "remove the whole slot"
            // and still grant the claimed amount: reject, the peer refunds the surplus.
            if (msg.Amount > slot.invItem.amount)
            {
                reason = "amount " + msg.Amount + " > slot " + slot.invItem.amount;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Apply shared NPC reputation (and optional attackedID / dead trailers). Host and
        /// clients both apply; night-trader reputation is ignored (per-player) but
        /// attackedID / dead / deadID still apply — world story state. Writes Flags.npcStates
        /// directly so it works if the NPC GameObject is not loaded yet.
        /// </summary>
        internal void HandleReputationSync(ReputationSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;

            bool perPlayerRep = Patches.ReputationSyncUtil.IsPerPlayerReputationNpcName(msg.NpcName);
            if (perPlayerRep && !msg.HasAttackedId && !msg.HasDead
                && !msg.HasPortrait && !msg.HasAnimLibrary)
                return;

            // Portrait / anim-library trailers can arrive without needing Flags; apply first.
            if (msg.HasPortrait)
                Patches.NpcAttackedIdSync.ApplyPortrait(msg);
            if (msg.HasAnimLibrary)
                Patches.NpcAttackedIdSync.ApplyAnimLibrary(msg);

            // Night-trader visual fan: do not create/overwrite Flags standing.
            if (perPlayerRep && !msg.HasAttackedId && !msg.HasDead)
                return;

            var flags = Singleton<Flags>.Instance;
            if (flags == null) return;

            var state = flags.getNPCState(msg.NpcName);
            if (state == null)
            {
                state = new Flags.NPCState
                {
                    name = msg.NpcName,
                    // Never seed night-trader standing from a host attackedID/dead fan-out.
                    reputation = perPlayerRep ? 0 : msg.Reputation,
                    wantsToTalk = true
                };
                flags.npcStates.Add(state);
            }
            else if (!perPlayerRep)
            {
                state.reputation = msg.Reputation;
            }

            if (msg.HasAttackedId)
                Patches.NpcAttackedIdSync.ApplyAttackedId(state, msg.NpcName, msg.AttackedId);
            if (msg.HasDead)
                Patches.NpcAttackedIdSync.ApplyDead(state, msg.NpcName, msg.Dead, msg.DeadId);

            if (!perPlayerRep)
                ModRuntime.LegacyInfo($"[RepSync] applied shared rep '{msg.NpcName}': {msg.Reputation}");
            else if (msg.HasAttackedId || msg.HasDead)
                ModRuntime.LegacyInfo(
                    $"[RepSync] applied story marks '{msg.NpcName}' attackedID={state.attackedID} dead={state.dead} (rep left per-player)");
        }

        private void FireRemoteContainerStoryTrigger(Inventory inv, EventTrigger.Type triggerType, ContainerItemMessage msg)
        {
            FireRemoteContainerStoryTrigger(_net, inv, triggerType, msg.ItemType, msg.IsRecipe);
        }

        /// <summary>
        /// Client container open/take/place runs <c>sendTriggerInfo</c> only on that
        /// machine, and client one-shots are blocked. Replay on the host so the
        /// GameEvent fans out. Host's own open/take already fired locally.
        /// </summary>
        internal static void FireRemoteContainerStoryTrigger(
            LanNetworkManager net, Inventory inv, EventTrigger.Type triggerType, string itemType, bool isRecipe)
        {
            if (net == null || net.Role != NetworkRole.Host || net.CurrentReceivePlayerId <= 0)
                return;
            if (inv == null) return;

            string value = "";
            if (triggerType != EventTrigger.Type.onOpenContainer
                && triggerType != EventTrigger.Type.onCloseContainer)
                value = isRecipe ? "recipe" : (itemType ?? "");

            DialogHostApplyGuard.BeginWorldOnly();
            try
            {
            if (triggerType == EventTrigger.Type.onOpenContainer
                || triggerType == EventTrigger.Type.onCloseContainer)
                Core.sendTriggerInfo(inv.gameObject, triggerType);
                else
                    Core.sendTriggerInfo(inv.gameObject, triggerType, value);
                ModRuntime.LegacyInfo(
                    "[Container] host story trigger " + triggerType + " on " + inv.name
                    + (string.IsNullOrEmpty(value) ? "" : " value=" + value));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[Container] story trigger: " + ex.Message);
            }
            finally
            {
                DialogHostApplyGuard.EndWorldOnly();
            }
        }
    }
}
