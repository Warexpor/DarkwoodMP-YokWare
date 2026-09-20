using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host migration fields, transport stop, and retry/gossip ticks.
    /// Roster apply/build: <c>HostMigration.PeerRoster</c>. Handoff/promote: <c>HostMigration.Handoff</c>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {

        private int _sessionPort = PluginInfo.DefaultPort;
        private int _hostPlayerId = 1;
        private readonly List<PeerRosterEntry> _peerRoster = new List<PeerRosterEntry>(8);
        private float _peerRosterTimer;
        private const float PeerRosterInterval = 4f;
        private bool _migrationInProgress;
        private float _migrationRetryAt;
        private string _migrationTargetAddress;
        private int _migrationTargetPort;
        private int _migrationElectId;
        private int _migrationRetryCount;
        private const int MigrationMaxRetries = 15;
        private const float MigrationRetrySec = 1.0f;
        /// <summary>Set during local StopNetwork or an intentional tear; do not treat it as a host crash.</summary>
        private bool _suppressHostMigration;
        private int _forcedElectId;
        private bool _handoffInProgress;

        /// <summary>Current host network id (not always 1 after migration).</summary>
        public int HostPlayerId => _hostPlayerId;

        private void NoteSessionPort(int port)
        {
            if (port > 0)
                _sessionPort = port;
        }

        private void ResetMigrationState()
        {
            _migrationInProgress = false;
            _migrationRetryAt = 0f;
            _migrationTargetAddress = null;
            _migrationTargetPort = 0;
            _migrationElectId = 0;
            _migrationRetryCount = 0;
            _peerRoster.Clear();
            _peerRosterTimer = 0f;
            _hostPlayerId = 1;
            _forcedElectId = 0;
            _handoffInProgress = false;
            // Keep _suppressHostMigration under StopNetwork control only.
        }

        /// <summary>
        /// Tear down LiteNetLib or Steam transport only; keep chapter and player state for host promotion.
        /// </summary>
        /// <param name="leaveSteamLobby">
        /// Steam: false keeps lobby membership during host-grant (elect promote / survivor reconnect).
        /// </param>
        private void StopTransportOnly(string reason, bool leaveSteamLobby = true)
        {
            ModLog.Event(LogCat.Network, "StopTransportOnly: " + reason
                + (IsSteamSession ? " leaveLobby=" + leaveSteamLobby : ""));
            if (_net != null)
            {
                try { _net.Stop(); }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "StopTransportOnly net.Stop: " + ex.Message);
                }
                _net = null;
            }
            ShutdownSteamBackend(leaveLobby: leaveSteamLobby);
            ClearAllPeerSlots();
            _handshakedPeers.Clear();
            _handshakeComplete = false;
            _peersLoadingWorld.Clear();
            _peersCoopReconnect.Clear();
            _awaitingLateJoinBulk.Clear();
            _pendingHeavyLateJoinBulk.Clear();
            EntityStateBroadcastService.Stop();
        }

        private void TickPeerRosterGossip()
        {
            if (_role != NetworkRole.Host || !IsConnected || !_handshakeComplete)
                return;
            if (_handshakedPeers.Count == 0)
                return;

            _peerRosterTimer += Time.unscaledDeltaTime;
            if (_peerRosterTimer < PeerRosterInterval)
                return;
            _peerRosterTimer = 0f;
            BroadcastPeerRoster();
        }

        private void TickHostMigrationRetry()
        {
            if (!_migrationInProgress || _role != NetworkRole.Client)
                return;
            if (string.IsNullOrEmpty(_migrationTargetAddress))
                return;
            bool steamTarget = IsSteamRosterAddress(_migrationTargetAddress);
            if (!steamTarget && _migrationTargetPort <= 0)
                return;
            if (Time.unscaledTime < _migrationRetryAt)
                return;
            if (_migrationRetryCount >= MigrationMaxRetries)
            {
                ModLog.Warn(LogCat.Network,
                    "Host migration reconnect exhausted — stopping network");
                _migrationInProgress = false;
                _suppressHostMigration = true;
                StopNetwork();
                StatusText = "Host lost — migration failed";
                return;
            }

            // First attempt already ran in TryBeginHostMigration; retries only after delay.
            if (_migrationRetryCount > 0 || _migrationRetryAt > 0f)
            {
                _migrationRetryCount++;
                _migrationRetryAt = Time.unscaledTime + MigrationRetrySec;
                ModLog.Event(LogCat.Network,
                    "Migration reconnect try " + _migrationRetryCount + "/" + MigrationMaxRetries
                    + " → " + _migrationTargetAddress
                    + (steamTarget ? " (Steam)" : (":" + _migrationTargetPort)));
                if (steamTarget)
                    ConnectSteamPreservingId(_migrationTargetAddress, _migrationElectId);
                else
                    ConnectToHostPreservingId(_migrationTargetAddress, _migrationTargetPort, _migrationElectId);
            }
        }
    }
}
