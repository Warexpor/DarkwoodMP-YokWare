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
        // Longer than LiteNetLib's connect-failure timeout (~5 s): a 1 s retry tore every attempt
        // down before it could succeed or fail.
        private const float MigrationRetrySec = 6.0f;
        /// <summary>Set during local StopNetwork or an intentional tear; do not treat it as a host crash.</summary>
        private bool _suppressHostMigration;
        private int _forcedElectId;
        private bool _handoffInProgress;

        // Bounded retry for an in-world (soft) reconnect whose link never completed a handshake.
        // Without it a host that is not listening yet (still loading the chapter) ended the session.
        private const int SoftReconnectMaxTries = 3;
        private const float SoftReconnectRetrySec = 5f;
        private int _softReconnectTries;
        private float _softReconnectRetryAt;
        private bool _softReconnectRetrying;
        private string _softReconnectAddress;
        private int _softReconnectPort;
        private ulong _softReconnectLobby;

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
            _softReconnectTries = 0;
            _softReconnectRetryAt = 0f;
            _softReconnectRetrying = false;
            _softReconnectAddress = null;
            _softReconnectPort = 0;
            _softReconnectLobby = 0;
            _isPromotedHost = false;
            _migrationReservedIds.Clear();
            _joinPipelineReservedIds.Clear();
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
            ResetInboundSequenceState();
        }

        /// <summary>
        /// The next link talks to a different sender (new host after migration / soft reconnect), and a
        /// promoted host restarts its own counters. Stale last-seen sequences would reject every
        /// snapshot and PlayerState from it until it overtook the old host's count.
        /// </summary>
        private void ResetInboundSequenceState()
        {
            ClientEntityInterpolationService.ResetSnapshotSequence();
            _lastPlayerStateSequence.Clear();
            _lastPhysicsStateSequence.Clear();
            _lastReliablePhysicsStateSequence.Clear();
        }

        /// <summary>Record the target of an in-world reconnect so a failed attempt can be retried.</summary>
        private void NoteSoftReconnectAttempt(string address, int port, ulong steamLobby)
        {
            if (_softReconnectRetrying)
            {
                _softReconnectRetrying = false;
                _softReconnectTries++;
            }
            else
            {
                _softReconnectTries = 1;
            }
            _softReconnectRetryAt = 0f;
            _softReconnectAddress = address;
            _softReconnectPort = port;
            _softReconnectLobby = steamLobby;
        }

        /// <summary>
        /// Failures that mean "the connect itself did not work", never "an established host went away".
        /// Treating these as a host loss promoted a client that was never in the session.
        /// </summary>
        private static bool IsConnectFailureReason(DisconnectReason reason)
        {
            switch (reason)
            {
                case DisconnectReason.ConnectionFailed:
                case DisconnectReason.ConnectionRejected:
                case DisconnectReason.InvalidProtocol:
                case DisconnectReason.UnknownHost:
                case DisconnectReason.PeerNotFound:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Client link never completed a handshake (or the connect was refused). Retry an in-world
        /// reconnect a few times, otherwise end the session visibly. Never elects a host.
        /// </summary>
        private void OnClientLinkFailed(string reason)
        {
            if (_role == NetworkRole.Client && _softReconnectTries > 0
                && _softReconnectTries < SoftReconnectMaxTries)
            {
                _softReconnectRetryAt = Time.unscaledTime + SoftReconnectRetrySec;
                // Dead peer slots out: IsConnected / sends must not target a link that never came up.
                ClearAllPeerSlots();
                StatusText = "Reconnect failed (" + reason + ") — retry "
                    + (_softReconnectTries + 1) + "/" + SoftReconnectMaxTries + " in "
                    + (int)SoftReconnectRetrySec + "s";
                ModLog.Warn(LogCat.Network,
                    "Client reconnect attempt " + _softReconnectTries + "/" + SoftReconnectMaxTries
                    + " failed (" + reason + ") — retrying in " + SoftReconnectRetrySec + "s");
                return;
            }

            bool wasReconnect = _softReconnectTries > 0;
            string status = wasReconnect
                ? "Could not reconnect to the host (" + reason + ")"
                : "Connection failed (" + reason + ")";
            ModLog.Warn(LogCat.Network, "Client link failed without handshake (" + reason + ") — ending session, no host grant");
            Sync.ChapterSessionResume.Reset();
            StopNetwork();
            StatusText = status;
            try
            {
                if (wasReconnect && Player.Instance != null && !Core.mainMenu && !Core.loadingGame)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(status); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch { /* non-fatal */ }
        }

        private void TickSoftReconnectRetry()
        {
            if (_softReconnectRetryAt <= 0f || Time.unscaledTime < _softReconnectRetryAt)
                return;
            _softReconnectRetryAt = 0f;
            if (_role != NetworkRole.Client)
                return;

            ModLog.Event(LogCat.Network,
                "Reconnect retry " + (_softReconnectTries + 1) + "/" + SoftReconnectMaxTries);
            _softReconnectRetrying = true;
            try
            {
                if (_softReconnectLobby != 0)
                    ConnectSteamLobby(new CSteamID(_softReconnectLobby));
                else
                    ConnectToHost(_softReconnectAddress, _softReconnectPort);
            }
            finally
            {
                // A non-soft connect path runs StopNetwork (state wiped); never leave the flag armed.
                _softReconnectRetrying = false;
            }
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

            // First attempt already ran in TryBeginHostMigration, which armed _migrationRetryAt.
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
