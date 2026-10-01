using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Dropped-item spawn/pickup + GUID host-auth claim (composed for 0.8).</summary>
    internal sealed partial class PlayerFXNetHandlers
    {
        internal void HandleDroppedItemSpawn(DroppedItemSpawnMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Guid) || string.IsNullOrEmpty(msg.PrefabPath) || string.IsNullOrEmpty(msg.ItemType))
                return;
            if (Players.DroppedItemIdentifier.FindById(msg.Guid) != null)
                return;

            // Only the host's drops are authoritative. Client drops are local-only.
            // Receiving a client drop on the host would spawn a second copy,
            // causing item multiplication (both sides can pick up their copy).
            // Allow client drops through the GUID-based system (DroppedItemIdentifier +
            // LanNetworkManager.ConsumedDropGuids) prevents multiplication: when one player picks
            // up, the other player's copy is destroyed via DroppedItemPickupMessage.
            // 0.8.63: ItemType is recipeFor when IsRecipe (vanilla ctor flips type to "recipe").
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] unknown item type: " + msg.ItemType
                    + " recipe=" + msg.IsRecipe);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            GameObject go = Core.AddPrefab(msg.PrefabPath, pos, rot, Core.ItemContainer);
            if (go == null) return;

            Inventory inv = go.GetComponent<Inventory>();
            if (inv == null || inv.slots == null || inv.slots.Count == 0) return;

            InvSlot slot = inv.slots[0];
            slot.inventory = inv;

            InvItemClass created = slot.createItem(msg.ItemType, msg.Amount, 1f,
                InvItem.ModifierQuality.none, msg.IsRecipe);
            if (InvItemClass.isNull(created) || created.baseClass == null)
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] failed to create item for " + msg.ItemType
                    + " recipe=" + msg.IsRecipe);
                return;
            }

            Sync.InvItemTransferApply.ApplyMeta(created, msg.Durability, msg.Ammo, msg.ShouldBeActive);
            Sync.InvItemUpgradeWire.Apply(created, msg.Upgrades);

            Core.addToSaveable(go, isDynamic: true);
            Singleton<WorldGrid>.Instance?.registerToNode(go);

            var ident = go.AddComponent<Players.DroppedItemIdentifier>();
            ident.Id = msg.Guid;
            Players.DroppedItemIdentifier.Register(ident);

            ModRuntime.LegacyInfo("[DroppedItemSpawn] " + msg.ItemType + " x" + msg.Amount
                + " recipe=" + msg.IsRecipe + " guid=" + msg.Guid);
        }

        /// <summary>Resets consumed GUID tracking (call on scene change / disconnect).</summary>
        internal void ResetConsumedDropGuids()
        {
            LanNetworkManager.ConsumedDropGuids.Clear();
        }

        internal void HandleDroppedItemPickup(DroppedItemPickupMessage msg)
        {
            bool fromClient = _net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0;
            if (string.IsNullOrEmpty(msg.Guid))
            {
                if (fromClient) _net.SuppressRelay();
                return;
            }

            // A client may only ask to claim (FinishGuidPickupClaim). Deny / Remove are host-issued:
            // a client Remove with a made-up ClaimedBy, relayed, would refund or delete the other
            // peers' pending pickups.
            if (fromClient && msg.Mode != DroppedItemPickupMessage.ModeClaimRequest)
            {
                _net.SuppressRelay();
                ModLog.WarnRate(LogCat.World, "drop-pickup-mode:" + _net.CurrentReceivePlayerId,
                    "[DroppedItemPickup] rejected client mode " + msg.Mode + " from p"
                    + _net.CurrentReceivePlayerId + " guid=" + msg.Guid);
                return;
            }

            if (msg.Mode == DroppedItemPickupMessage.ModeClaimRequest)
            {
                HandleGuidPickupClaimRequest(msg);
                return;
            }
            if (msg.Mode == DroppedItemPickupMessage.ModeClaimDeny)
            {
                HandleGuidPickupClaimDeny(msg);
                return;
            }

            // ModeRemove (default / legacy Guid-only): mark consumed + destroy world copy.
            bool first = LanNetworkManager.ConsumedDropGuids.Add(msg.Guid);
            if (!first)
                ModRuntime.LegacyInfo("[DroppedItemPickup] already consumed: " + msg.Guid);

            var ident = Players.DroppedItemIdentifier.FindById(msg.Guid);
            if (ident != null && ident.gameObject != null)
            {
                ModRuntime.LegacyInfo("[DroppedItemPickup] removing guid=" + msg.Guid);
                UnityEngine.Object.Destroy(ident.gameObject);
            }

            // Optimistic client lost the host-auth race: refund once via pending.
            if (_net.Role == NetworkRole.Client
                && msg.ClaimedByPlayerId > 0
                && msg.ClaimedByPlayerId != _net.LocalPlayerId)
            {
                Patches.WorldPickupClaimPending.TryRefundIfPendingGuid(
                    msg.Guid, "guid remove claimedBy=" + msg.ClaimedByPlayerId);
            }
            else
            {
                Patches.WorldPickupClaimPending.ClearGuid(msg.Guid);
            }
        }

        /// <summary>
        /// Host: first ClaimRequest wins — consume, destroy local, fan Remove with ClaimedBy.
        /// Loser gets ClaimDeny (optimistic grant refund on client).
        /// </summary>
        private void HandleGuidPickupClaimRequest(DroppedItemPickupMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
                return;
            // ClaimRequest must not Forwardable-fan to other clients (host owns grant).
            _net.SuppressRelay();

            int claimer = _net.CurrentReceivePlayerId;
            if (claimer <= 0)
                return;

            if (!WorldObjectSendNetHandlers.TryConsumeDropGuid(msg.Guid))
            {
                var deny = new DroppedItemPickupMessage
                {
                    Guid = msg.Guid,
                    Mode = DroppedItemPickupMessage.ModeClaimDeny,
                    ClaimedByPlayerId = 0,
                    ItemType = msg.ItemType ?? "",
                    Amount = msg.Amount,
                    Durability = msg.Durability,
                    Ammo = msg.Ammo
                };
                _net.SendToPlayer(claimer, NetMessageType.DroppedItemPickup, w => deny.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                ModLog.Event(LogCat.World,
                    "[GuidPickup] deny p" + claimer + " guid=" + msg.Guid);
                return;
            }

            var ident = Players.DroppedItemIdentifier.FindById(msg.Guid);
            if (ident != null && ident.gameObject != null)
                UnityEngine.Object.Destroy(ident.gameObject);

            var remove = new DroppedItemPickupMessage
            {
                Guid = msg.Guid,
                Mode = DroppedItemPickupMessage.ModeRemove,
                ClaimedByPlayerId = claimer,
                ItemType = msg.ItemType ?? "",
                Amount = msg.Amount,
                Durability = msg.Durability,
                Ammo = msg.Ammo
            };
            _net.Broadcast(NetMessageType.DroppedItemPickup, w => remove.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModLog.Event(LogCat.World,
                "[GuidPickup] grant p" + claimer + " guid=" + msg.Guid);
        }

        private void HandleGuidPickupClaimDeny(DroppedItemPickupMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            if (Patches.WorldPickupClaimPending.TryTakeGuid(msg.Guid,
                out string type, out int amt, out int pre, out string recipeFor))
            {
                Patches.WorldPickupClaimPending.Refund(type, amt, pre, "guid claim deny", recipeFor);
                return;
            }

            // No pending entry: the host-won Remove (ClaimedBy != us) already refunded and consumed
            // it, or nothing was granted. A blind Refund(..., preCount -1) here would remove the
            // claimed amount a second time out of the player's own stock.
            ModRuntime.LegacyInfo("[GuidPickup] claim deny guid=" + msg.Guid
                + ": no pending grant (already refunded by Remove) — nothing to undo");
        }
    }
}
