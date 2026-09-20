using System.Net;
using LiteNetLib;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Sync;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates bulk sync to <see cref="BulkSyncNetHandlers"/>.
    /// LiteNetLib connection callbacks and cross-domain session reset stay here.
    /// Flag bulk is on <see cref="FlagNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal BulkSyncNetHandlers BulkSyncHandlers { get; private set; }

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

        internal void SendReputationBulkSync() => SendReputationBulkSyncTo(-1);

        internal void SendReputationBulkSyncTo(int targetPlayerId)
        {
            BulkSyncHandlers.SendReputationBulkSyncTo(targetPlayerId);
        }

        private void HandleReputationBulkSync(ReputationBulkSyncMessage msg)
        {
            BulkSyncHandlers.HandleReputationBulkSync(msg);
        }

        internal void SendScenarioBulkSync() => SendScenarioBulkSyncTo(-1);

        internal void SendScenarioBulkSyncTo(int targetPlayerId)
        {
            BulkSyncHandlers.SendScenarioBulkSyncTo(targetPlayerId);
        }

        private void HandleScenarioStateSync(ScenarioSyncMessage msg)
        {
            BulkSyncHandlers.HandleScenarioStateSync(msg);
        }

        private void HandleScenarioStateBulk(ScenarioStateBulkMessage msg)
        {
            BulkSyncHandlers.HandleScenarioStateBulk(msg);
        }

        internal void SendHideoutStateSync() => SendHideoutStateSyncTo(-1);

        internal void SendHideoutStateSyncTo(int targetPlayerId)
        {
            BulkSyncHandlers.SendHideoutStateSyncTo(targetPlayerId);
        }

        private void HandleHideoutStateSync(HideoutStateSyncMessage msg)
        {
            BulkSyncHandlers.HandleHideoutStateSync(msg);
        }

        internal void SendWorkbenchLevelSync() => SendWorkbenchLevelSyncTo(-1);

        internal void SendWorkbenchLevelSyncTo(int targetPlayerId)
        {
            BulkSyncHandlers.SendWorkbenchLevelSyncTo(targetPlayerId);
        }

        private void HandleWorkbenchLevelSync(WorkbenchLevelMessage msg)
        {
            BulkSyncHandlers.HandleWorkbenchLevelSync(msg);
        }

        internal void SendMapStateSync() => SendMapStateSyncTo(-1);

        internal void SendMapStateSyncTo(int targetPlayerId)
        {
            BulkSyncHandlers.SendMapStateSyncTo(targetPlayerId);
        }

        private void HandleMapStateSync(MapStateSyncMessage msg)
        {
            BulkSyncHandlers.HandleMapStateSync(msg);
        }

        private void HandlePlayerSkillsSync(PlayerSkillsSyncMessage msg)
        {
            BulkSyncHandlers.HandlePlayerSkillsSync(msg);
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
                // Host counts as 1; _peers are clients
                if (_peers.Count + 1 >= maxPlayers)
                {
                    ModLog.Event(LogCat.Network, $"Rejecting connection — max players ({maxPlayers}) reached");
                    request.Reject();
                    return;
                }

                request.AcceptIfKey(ModConfig.GetConnectionKey());
                ModLog.Event(LogCat.Network, $"Connection accepted (will be peer #{_peers.Count + 1})");
            }
            else
            {
                request.Reject();
            }
        }

        /// <summary>Clear host session maps that are not covered by NetworkResetRegistry.</summary>
        internal void ResetSessionNetworkState()
        {
            _shadowTracked.Clear();
            _nextShadowId = 0;
            ClearShadowLookups();
            ContainerHandlers?.ClearPendingContainerState();
            FlagHandlers?.ClearPendingFlags();
            JournalHandlers?.ClearPendingJournal();
            _awaitingLateJoinBulk.Clear();
            _pendingHeavyLateJoinBulk.Clear();
            _peersLoadingWorld.Clear();
            _peersCoopReconnect.Clear();
            SaveHandlers?.Reset();
            _hostWasShareableForWaitingClients = false;
            TradeHandlers?.ClearPendingTradeInventories();
            LockHandlers?.ClearConstructibleState();
            StationHandlers?.ClearPendingStations();
            ChainHandlers?.ClearPendingChains();
            ShadowArmorHandlers?.ClearPending();
            WorldBurnHandlers?.ClearPending();
            StationSyncHelpers.Reset();
            WorkbenchOpenLock.Reset();
            BarricadeHandlers?.ClearPendingBarricades();
            NightHandlers?.ClearPendingScenario();
            GameEventHandlers?.ClearPendingGameEvents();
            ClearPendingLocks();
            CombatFxHandlers?.ClearMeleeHitDebounce();
            _remoteOutsideLocation.Clear();
        }
    }
}
