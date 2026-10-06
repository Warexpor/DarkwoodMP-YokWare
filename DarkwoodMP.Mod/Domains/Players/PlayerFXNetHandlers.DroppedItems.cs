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
        /// <summary>A dropped item with no GUID, of this item and amount, lying at this spot (XZ: drops sit below the ground plane).</summary>
        private static GameObject FindUntaggedDrop(Vector3 pos, string wireType, bool isRecipe, int amount)
        {
            Collider[] buf = WorldQueryHelper.SharedOverlapBuf;
            int n = Physics.OverlapSphereNonAlloc(pos, 30f, buf);
            for (int i = 0; i < n; i++)
            {
                Collider c = buf[i];
                if (c == null)
                    continue;
                Item item = c.GetComponentInParent<Item>();
                if (item == null || !item.isDroppedItem || item.GetComponent<Players.DroppedItemIdentifier>() != null)
                    continue;
                float dx = item.transform.position.x - pos.x, dz = item.transform.position.z - pos.z;
                if (dx * dx + dz * dz > 1.5f * 1.5f)
                    continue;
                Inventory inv = item.GetComponent<Inventory>();
                InvItemClass held = inv != null && inv.slots != null && inv.slots.Count > 0 ? inv.slots[0].invItem : null;
                if (InvItemClass.isNull(held) || held.isRecipe != isRecipe || held.amount != amount)
                    continue;
                string type = held.isRecipe ? held.recipeFor : held.type;
                if (type == wireType)
                    return item.gameObject;
            }
            return null;
        }

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
            // ItemType is recipeFor when IsRecipe (vanilla ctor flips type to "recipe").
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[DroppedItemSpawn] unknown item type: " + msg.ItemType
                    + " recipe=" + msg.IsRecipe);
                return;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);

            // A joiner's world comes from the host's save, which already holds drops that were on
            // the ground when it was written (saved without their GUID). The join snapshot then
            // sends them again: tag the saved copy instead of spawning a second one.
            GameObject saved = FindUntaggedDrop(pos, msg.ItemType, msg.IsRecipe, msg.Amount);
            if (saved != null)
            {
                var adopted = saved.AddComponent<Players.DroppedItemIdentifier>();
                adopted.Id = msg.Guid;
                Players.DroppedItemIdentifier.Register(adopted);
                ModRuntime.LegacyInfo($"[DroppedItemSpawn] adopted saved drop {msg.ItemType} x{msg.Amount} guid={msg.Guid}");
                return;
            }

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
            if (msg.VelX != 0f || msg.VelY != 0f || msg.VelZ != 0f)
            {
                Rigidbody rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                    rb.velocity = new Vector3(msg.VelX, msg.VelY, msg.VelZ);
            }

            var ident = go.AddComponent<Players.DroppedItemIdentifier>();
            ident.Id = msg.Guid;
            Players.DroppedItemIdentifier.Register(ident);

            ModRuntime.LegacyInfo($"[DroppedItemSpawn] {msg.ItemType} x{msg.Amount} recipe={msg.IsRecipe} guid={msg.Guid}");
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
                ModRuntime.LegacyInfo($"[DroppedItemPickup] already consumed: {msg.Guid}");

            var ident = Players.DroppedItemIdentifier.FindById(msg.Guid);
            if (ident != null && ident.gameObject != null)
            {
                ModRuntime.LegacyInfo($"[DroppedItemPickup] removing guid={msg.Guid}");
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
                out string type, out int amt, out int pre, out string recipeFor, out float dur, out int ammo))
            {
                Patches.WorldPickupClaimPending.Refund(type, amt, pre, "guid claim deny", recipeFor, dur, ammo);
                return;
            }

            // No pending entry: the host-won Remove (ClaimedBy != us) already refunded and consumed
            // it, or nothing was granted. A blind Refund(..., preCount -1) here would remove the
            // claimed amount a second time out of the player's own stock.
            ModRuntime.LegacyInfo($"[GuidPickup] claim deny guid={msg.Guid}: no pending grant (already refunded by Remove) — nothing to undo");
        }
    }
}
