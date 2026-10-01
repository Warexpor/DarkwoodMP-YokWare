using System.Net;
using LiteNetLib;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Session transport callbacks and cross-domain reset (not a bulk-message façade).
    /// Bulk message send/apply live on <see cref="BulkSyncNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
        }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
        }

        /// <summary>
        /// Host helper: send reliable bulk payload to one peer, or all peers if targetPlayerId &lt;= 0.
        /// </summary>
        internal void SendBulkOrAll(NetMessageType type, System.Action<NetWriter> writeBody, int targetPlayerId)
        {
            if (targetPlayerId > 0)
                SendToPlayer(targetPlayerId, type, writeBody, DeliveryMethod.ReliableOrdered);
            else
                SendToAll(type, writeBody, DeliveryMethod.ReliableOrdered);
        }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (_role == NetworkRole.Host)
            {
                // Reject joins during a dream unless configuration allows them.
                // Match Steam gate: IsDreamActive covers entry transition before DreamSession.Active.
                bool allowDreamJoin = ModConfig.AllowJoinDuringDream != null
                    && ModConfig.AllowJoinDuringDream.Value;
                if (!allowDreamJoin
                    && (DreamSession.ShouldRejectNewConnections
                        || DreamSyncManager.IsDreamActive))
                {
                    ModLog.Event(LogCat.Network, "Rejecting connection — dream session active");
                    request.Reject();
                    return;
                }

                int maxPlayers = ModConfig.MaxPlayers != null ? ModConfig.MaxPlayers.Value : 8;
                // Host counts as 1; the peer table holds the clients.
                if (_lanPeers.Count + 1 >= maxPlayers)
                {
                    ModLog.Event(LogCat.Network, $"Rejecting connection — max players ({maxPlayers}) reached");
                    request.Reject();
                    return;
                }

                request.AcceptIfKey(ModConfig.GetConnectionKey());
                ModLog.Event(LogCat.Network, $"Connection accepted (will be peer #{_lanPeers.Count + 1})");
            }
            else
            {
                request.Reject();
            }
        }

        /// <summary>
        /// Network stop: a fresh <see cref="SessionState"/> (peer bookkeeping, presence caches, stable
        /// keys, world-ready flags) and the handler pending queues that NetworkResetRegistry does not own.
        /// </summary>
        internal void ResetSessionNetworkState()
        {
            _session = new SessionState();
            Shadows.Reset();
            NightHandlers?.ClearShadowLookups();
            ContainerPendingHandlers?.ClearPendingContainerState();
            ContainerLootHandlers?.ClearPendingHideoutUpgrades();
            FlagHandlers?.ClearPendingFlags();
            JournalHandlers?.ClearPendingJournal();
            SaveHandlers?.Reset();
            TradeHandlers?.ClearPendingTradeInventories();
            LockHandlers?.ClearConstructibleState();
            StationHandlers?.ClearPendingStations();
            ChainHandlers?.ClearPendingChains();
            ShadowArmorHandlers?.ClearPending();
            WorldBurnHandlers?.ClearPending();
            StationSyncHelpers.Reset();
            BarricadeHandlers?.ClearPendingBarricades();
            NightHandlers?.ClearPendingScenario();
            GameEventHandlers?.ClearPendingGameEvents();
            LockHandlers?.ClearPendingLocks();
            CombatFxImpactHandlers?.ClearMeleeHitDebounce();
        }
    }
}
