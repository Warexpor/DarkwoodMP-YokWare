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
    /// <summary>Graceful host leave, election, promote, and reconnect-with-id.</summary>
    public sealed partial class LanNetworkManager
    {
        private void HandleHostHandoff(HostHandoffMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;
            if (msg.ElectPlayerId <= 0)
                return;

            _forcedElectId = msg.ElectPlayerId;
            if (msg.SessionPort > 0)
                _sessionPort = msg.SessionPort;

            ModLog.Event(LogCat.Network,
                "Host handoff received — elect p" + msg.ElectPlayerId
                + " port=" + _sessionPort);
            // Host is about to disconnect; start grant immediately so we do not wait on timeout.
            TryBeginHostMigration("host handoff");
        }

        /// <summary>
        /// Host UI / graceful leave: elect a survivor, announce handoff, release port, then clean stop.
        /// Returns true if a handoff was started (async finish).
        /// </summary>
        public bool TryGracefulHostLeave()
        {
            if (_role != NetworkRole.Host || !IsConnected)
                return false;
            if (_handoffInProgress)
                return true;

            var survivors = new List<int>(8);
            foreach (int id in _handshakedPeers)
            {
                if (id > 0 && id != _localPlayerId)
                    survivors.Add(id);
            }
            if (survivors.Count == 0)
                return false;

            int elect = HostMigrationPolicy.ElectNewHost(survivors);
            if (elect <= 0)
                return false;

            _handoffInProgress = true;
            BroadcastPeerRoster();

            if (IsSteamSession && _steamPeers.TryGetValue(elect, out CSteamID electSid))
                Steam.TransferLobbyOwner(electSid);

            Broadcast(NetMessageType.HostHandoff, w => new HostHandoffMessage
            {
                ElectPlayerId = elect,
                SessionPort = IsSteamSession
                    ? SteamCoopTransport.MigrationVirtualPort
                    : _sessionPort
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            ModLog.Event(LogCat.Network,
                "Graceful host leave — handoff to p" + elect
                + (IsSteamSession ? " (Steam)" : ""));
            StatusText = "Handing host to p" + elect + "…";

            // Flush packets then FREE listen port / tear SNS so elect can promote.
            // Full StopNetwork after a short delay for local registry cleanup.
            _suppressHostMigration = true;
            StartCoroutine(GracefulHostLeaveReleasePortThenStop(0.15f, 0.4f));
            return true;
        }

        private IEnumerator GracefulHostLeaveReleasePortThenStop(float flushDelay, float cleanupDelay)
        {
            float t = 0f;
            while (t < flushDelay)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            // Port free for elect promote; local still in-world until StopNetwork.
            StopTransportOnly("graceful handoff port release");
            _role = NetworkRole.Offline;

            t = 0f;
            while (t < cleanupDelay)
            {
                t += Time.unscaledDeltaTime;
                yield return null;
            }
            _handoffInProgress = false;
            // Suppression is already set; StopNetwork will not re-enter migration.
            StopNetwork();
        }

        /// <summary>
        /// Client: host peer dropped or handoff. Elect + promote or reconnect (n+).
        /// </summary>
        private void TryBeginHostMigration(string reason)
        {
            if (_suppressHostMigration)
            {
                StopNetwork();
                return;
            }

            // Duplicate signals (handoff + SNS fail + lobby left) must not tear a running grant.
            if (_migrationInProgress)
                return;

                // Refuse a mid-dream authority flip; tear down dream state, then disconnect.
            if (Sync.DreamSession.IsActive || Sync.DreamSyncManager.IsDreamActive)
            {
                ModLog.Warn(LogCat.Network,
                    "Host migration refused mid-dream (" + reason + ") — disconnect without GRANT");
                try
                {
                    Sync.DreamSyncManager.ForceLocalDreamCleanup("hostLostMidDream");
                }
                catch (System.Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "Mid-dream cleanup: " + ex.Message);
                }
                StopNetwork();
                StatusText = "Host lost mid-dream — disconnected";
                return;
            }

            bool enabled = Config.ModConfig.HostMigrationEnabled == null
                || Config.ModConfig.HostMigrationEnabled.Value;
            bool playable = false;
            try
            {
                playable = !Core.mainMenu && (Player.Instance != null || Core.loadedGame || Core.coreStarted);
            }
            catch { /* unity tear */ }

            if (!HostMigrationPolicy.ShouldAttemptMigration(
                    enabled, _role == NetworkRole.Client, Core.mainMenu, playable, _migrationInProgress))
            {
                StopNetwork();
                return;
            }

            int deadHost = _hostPlayerId > 0 ? _hostPlayerId : 1;
            var candidates = new List<int>(8) { _localPlayerId };
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                PeerRosterEntry e = _peerRoster[i];
                if (e.PlayerId == deadHost) continue;
                if (e.PlayerId == _localPlayerId) continue;
                if (string.IsNullOrEmpty(e.Address)) continue;
                if (!candidates.Contains(e.PlayerId))
                    candidates.Add(e.PlayerId);
            }

            int elect = _forcedElectId > 0
                ? _forcedElectId
                : HostMigrationPolicy.ElectNewHost(candidates);
            _forcedElectId = 0;

            // Forced elect must still be a known survivor (or self).
            if (elect != _localPlayerId && !candidates.Contains(elect))
            {
                // Handoff elect is not in the roster; fall back to pure election.
                elect = HostMigrationPolicy.ElectNewHost(candidates);
            }

            if (elect <= 0)
            {
                ModLog.Warn(LogCat.Network, "Host migration: no electable survivor — StopNetwork");
                StopNetwork();
                return;
            }

            _migrationInProgress = true;
            _migrationElectId = elect;
            ModLog.Event(LogCat.Network,
                "HOST GRANT migration (" + reason + "): elect=" + elect
                + " local=" + _localPlayerId + " deadHost=" + deadHost
                + " candidates=" + candidates.Count
                + (IsSteamSession ? " steam" : " lan"));

            int keepId = _localPlayerId;
            CleanupDeadHostLocal(deadHost);

            if (HostMigrationPolicy.IsLocalElected(keepId, elect))
            {
                PromoteLocalToHost(keepId, reason);
                return;
            }

            PeerRosterEntry? target = null;
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                if (_peerRoster[i].PlayerId == elect)
                {
                    target = _peerRoster[i];
                    break;
                }
            }

            if (target == null || string.IsNullOrEmpty(target.Value.Address))
            {
                ModLog.Warn(LogCat.Network,
                    "Host migration: elected p" + elect + " has no roster address — cannot reconnect");
                _migrationInProgress = false;
                StopNetwork();
                StatusText = "Host lost — no route to new host";
                return;
            }

            string addr = target.Value.Address;
            int port = target.Value.Port > 0 ? target.Value.Port : _sessionPort;
            _migrationTargetAddress = addr;
            _migrationTargetPort = port;
            _migrationRetryCount = 0;
            // First connect now; first retry after MigrationRetrySec (avoid double-connect same frame).
            _migrationRetryAt = Time.unscaledTime + MigrationRetrySec;
            _localPlayerId = keepId;
            StatusText = "Host lost — reconnecting to p" + elect + "…";
            if (IsSteamSession || IsSteamRosterAddress(addr))
                ConnectSteamPreservingId(addr, elect);
            else
                ConnectToHostPreservingId(addr, port, elect);
        }

        private void CleanupDeadHostLocal(int deadHost)
        {
            WorldProxyHandlers.DestroyRemoteProxy(deadHost);
            PlayerHeldLightHandlers.DestroyRemoteFlareLight(deadHost);
            PlayerHeldLightHandlers.DestroyRemoteItemLight(deadHost);
            if (_remotePlayers.ContainsKey(deadHost))
                _remotePlayers.Remove(deadHost);
            PlayerPositionManager.RemovePlayer(deadHost);
            _remoteOutsideLocation.Remove(deadHost);
            try
            {
                DeathStateTracker.OnRemoteDisconnected(deadHost);
            }
            catch { /* optional */ }
            Sync.FinalDreamsceneManager.OnRemoteDisconnected(deadHost);
        }
    }
}
