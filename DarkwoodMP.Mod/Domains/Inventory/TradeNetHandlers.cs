using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Trade + peer-item presence handlers composed for 0.8.</summary>
    internal sealed class TradeNetHandlers
    {
        private readonly LanNetworkManager _net;

        private readonly Dictionary<string, TradeInventorySyncMessage> _pendingTradeInventories =
            new Dictionary<string, TradeInventorySyncMessage>();

        internal TradeNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingTradeInventories()
        {
            _pendingTradeInventories.Clear();
        }

        internal void HandleTradeSync(TradeSyncMessage msg)
        {
            TradeSyncHandler.HandleTradeSync(msg);
        }

        internal void HandleTradeInventorySync(TradeInventorySyncMessage msg)
        {
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
            {
                // Client stock is not world authority; do not forward the payload.
                _net._suppressForwardThisMessage = true;

                int senderId = _net.CurrentReceivePlayerId;
                NpcDialogueLock.HostRenewLeaseForSender(msg.NpcName, senderId);
                bool tradingPeer = NpcDialogueLock.GetOwner(msg.NpcName) == senderId;
                if (tradingPeer)
                    TradeInventorySync.Handle(msg);
                else
                    ModRuntime.LegacyInfo(
                        $"[TradeSync] rejected inventory from p{senderId} for '{msg.NpcName}' (no dialog lock)");

                NPC npc = TradeInventorySync.FindNpcByName(msg.NpcName);
                if (npc != null)
                    TradeInventorySync.BroadcastNpcInventory(npc);
                return;
            }

            // Clients apply host-authoritative snapshots (join bulk + live fan-out).
            if (_net.Role == NetworkRole.Client)
                TradeInventorySync.Handle(msg);
        }

        /// <summary>Queue absolute trader stock until the NPC exists in the scene.</summary>
        internal void QueuePendingTradeInventory(TradeInventorySyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName)) return;
            _pendingTradeInventories[msg.NpcName] = msg;
            ModRuntime.LegacyInfo($"[TradeSync] queued inventory for '{msg.NpcName}' (NPC not loaded)");
        }

        internal void TryFlushPendingTradeInventories()
        {
            var pending = _pendingTradeInventories;
            if (pending.Count == 0) return;

            var applied = new List<string>();
            foreach (var kvp in pending)
            {
                NPC npc = TradeInventorySync.FindNpcByName(kvp.Key);
                if (npc == null) continue;
                TradeInventorySync.ApplyToNpc(npc, kvp.Value);
                applied.Add(kvp.Key);
            }
            for (int i = 0; i < applied.Count; i++)
                pending.Remove(applied[i]);
        }

        /// <summary>Host: push absolute shop stock for every loaded trader NPC.</summary>
        internal void SendTradeInventoriesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            NPC[] all = Object.FindObjectsOfType<NPC>();
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                NPC npc = all[i];
                if (npc == null || !npc.trader || npc.inventory == null) continue;
                if (string.IsNullOrEmpty(npc.name)) continue;

                var msg = TradeInventorySync.BuildMessage(npc);
                _net.SendBulkOrAll(NetMessageType.TradeInventorySync, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }
            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} trader inventories to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} trader inventories to all clients");
        }

        internal void HandlePeerHasItem(PeerHasItemMessage msg)
        {
            if (_net.Role != NetworkRole.Host) return;
            int id = msg.PlayerId;
            if (id <= 0 && _net.CurrentReceivePlayerId > 0)
                id = _net.CurrentReceivePlayerId;
            PeerItemPresence.Apply(id, msg.ItemType, msg.Amount);
        }
    }
}
