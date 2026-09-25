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
        private readonly List<string> _tradeFlushApplied = new List<string>(8);
        private float _nextTradeFlushTime;
        private const float TradeFlushInterval = 0.5f;
        private const int MaxPendingTradeInventories = 64;

        private static string TradePendingKey(TradeInventorySyncMessage msg)
        {
            string world = msg.InDream ? "dream" : "world";
            if (!msg.HasPos)
                return msg.NpcName + "|" + world;
            return msg.NpcName + "|" + world + "|"
                + Mathf.Round(msg.PosX * 10f) + "|"
                + Mathf.Round(msg.PosZ * 10f);
        }

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

                NPC npc = TradeInventorySync.FindNpcByName(msg);
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
            if (!_pendingTradeInventories.ContainsKey(TradePendingKey(msg))
                && _pendingTradeInventories.Count >= MaxPendingTradeInventories)
            {
                // Drop an arbitrary oldest-ish key so join storms cannot grow forever.
                string drop = null;
                foreach (var k in _pendingTradeInventories.Keys)
                {
                    drop = k;
                    break;
                }
                if (drop != null)
                    _pendingTradeInventories.Remove(drop);
            }
            _pendingTradeInventories[TradePendingKey(msg)] = msg;
            ModRuntime.LegacyInfo($"[TradeSync] queued inventory for '{msg.NpcName}' (NPC not loaded)");
        }

        internal void TryFlushPendingTradeInventories()
        {
            var pending = _pendingTradeInventories;
            if (pending.Count == 0) return;

            float now = Time.unscaledTime;
            if (now < _nextTradeFlushTime) return;
            _nextTradeFlushTime = now + TradeFlushInterval;

            // One NPC scene array per flush (FindNpcByName shares the TTL cache).
            _tradeFlushApplied.Clear();
            foreach (var kvp in pending)
            {
                NPC npc = TradeInventorySync.FindNpcByName(kvp.Value);
                if (npc == null) continue;
                TradeInventorySync.ApplyToNpc(npc, kvp.Value);
                _tradeFlushApplied.Add(kvp.Key);
            }
            for (int i = 0; i < _tradeFlushApplied.Count; i++)
                pending.Remove(_tradeFlushApplied[i]);
        }

        /// <summary>Host: push absolute shop stock for every loaded trader NPC.</summary>
        internal void SendTradeInventoriesTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host) return;

            NPC[] all = WorldQueryHelper.GetCachedSceneComponents<NPC>();
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
