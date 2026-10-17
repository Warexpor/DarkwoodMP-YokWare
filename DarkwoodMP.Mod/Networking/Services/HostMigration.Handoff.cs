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
        /// This player leaves the session on purpose (the credits): the host going away meanwhile
        /// is not a crash, so it does not take the host.
        /// </summary>
        internal void MarkLeavingSession()
        {
            _suppressHostMigration = true;
        }

        /// <summary>
        /// Host UI / graceful leave: elect a survivor, announce handoff, release port, then clean stop.
        /// <paramref name="eligible"/> limits who may take the host (the credits hand the world to a
        /// player who stays in it). Returns true if a handoff was started (async finish).
        /// </summary>
        public bool TryGracefulHostLeave(Func<int, bool> eligible = null)
        {
            if (_role != NetworkRole.Host || !IsConnected)
                return false;
            if (_handoffInProgress)
                return true;

            var survivors = new List<int>(8);
            foreach (int id in _session.Link.Handshaked)
            {
                if (id > 0 && id != _localPlayerId && (eligible == null || eligible(id)))
                    survivors.Add(id);
            }
            if (survivors.Count == 0)
                return false;

            int elect = HostMigrationPolicy.ElectNewHost(survivors);
            if (elect <= 0)
                return false;

            _handoffInProgress = true;
            BroadcastPeerRoster();

            if (IsSteamSession && _steamPeers.TryGetSteamId(elect, out CSteamID electSid))
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
        /// <param name="afterReconnectWindow">
        /// The reconnect window ran out: the link was already torn for its dials (no handshake left
        /// to check) and the character was kept when the link was lost.
        /// </param>
        private void TryBeginHostMigration(string reason, bool afterReconnectWindow = false)
        {
            if (!_suppressHostMigration && !_migrationInProgress && _session.Link.HandshakeComplete)
                SaveHandlers?.PersistOnLinkLoss("host lost: " + reason);
            if (_suppressHostMigration)
            {
                StopNetwork();
                return;
            }

            // Duplicate signals (handoff + SNS fail + lobby left) must not tear a running grant.
            if (_migrationInProgress)
                return;

            // A link that never completed its handshake is a failed connect, not a host loss:
            // electing here would promote a client that was never part of the session.
            if (!_session.Link.HandshakeComplete && !afterReconnectWindow)
            {
                OnClientLinkFailed(reason);
                return;
            }

            bool enabled = Config.ModConfig.HostMigrationEnabled == null
                || Config.ModConfig.HostMigrationEnabled.Value;
            bool playable = false;
            try
            {
                playable = !GameScreen.AtTitle && (Player.Instance != null || Core.loadedGame || Core.coreStarted);
            }
            catch (Exception ex) { ModLog.Swallowed(ex); /* unity tear */ }

            if (!HostMigrationPolicy.ShouldAttemptMigration(
                    enabled, _role == NetworkRole.Client, GameScreen.AtTitle, playable, _migrationInProgress))
            {
                StopNetwork();
                return;
            }

            int deadHost = _hostPlayerId > 0 ? _hostPlayerId : 1;
            int forced = _forcedElectId;
            _forcedElectId = 0;
            _migrationFailedElects.Clear();
            _migrationTotalTries = 0;
            int elect = ElectMigrationHost(deadHost, forced);
            if (elect <= 0)
            {
                ModLog.Warn(LogCat.Network, "Host migration: no electable survivor — StopNetwork");
                StopNetwork();
                return;
            }

            ModLog.Event(LogCat.Network,
                "HOST GRANT migration (" + reason + "): elect=" + elect
                + " local=" + _localPlayerId + " deadHost=" + deadHost
                + (IsSteamSession ? " steam" : " lan"));

            // A dream in progress carries over to the new host (each peer has its own pad).
            try
            {
                Sync.DreamSyncManager.OnHostMigrating(deadHost, elect,
                    HostMigrationPolicy.IsLocalElected(_localPlayerId, elect));
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Mid-dream migration: " + ex.Message);
            }
            CleanupDeadHostLocal(deadHost);

            if (!StartMigrationToElect(elect, reason))
                StopNetwork();
        }

        /// <summary>
        /// Who takes the host: the handoff's pick when it is still a survivor, else the lowest id
        /// among this player and the roster's survivors, without the dead host and without any
        /// player that already failed to take it in this migration. -1 when nobody is left.
        /// </summary>
        private int ElectMigrationHost(int deadHost, int forced)
        {
            var candidates = new List<int>(8);
            // This player only stands when the host listed it as in the world: still joining,
            // it has no session world to host.
            if (!_migrationFailedElects.Contains(_localPlayerId)
                && (_peerRoster.Count == 0 || IsPlayerListedInPeerRoster(_localPlayerId, rosterOnly: true)))
                candidates.Add(_localPlayerId);
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                PeerRosterEntry e = _peerRoster[i];
                if (e.PlayerId == deadHost) continue;
                if (e.PlayerId == _localPlayerId) continue;
                if (string.IsNullOrEmpty(e.Address)) continue;
                if (_migrationFailedElects.Contains(e.PlayerId)) continue;
                if (!candidates.Contains(e.PlayerId))
                    candidates.Add(e.PlayerId);
            }
            if (forced > 0 && candidates.Contains(forced))
                return forced;
            return HostMigrationPolicy.ElectNewHost(candidates);
        }

        /// <summary>Promote this player or dial the elected one. False when there is no route to it.</summary>
        private bool StartMigrationToElect(int elect, string reason)
        {
            if (elect <= 0)
                return false;
            int keepId = _localPlayerId;
            _migrationInProgress = true;
            _migrationElectId = elect;

            if (HostMigrationPolicy.IsLocalElected(keepId, elect))
            {
                PromoteLocalToHost(keepId, reason);
                return true;
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
                StatusText = "Host lost — no route to new host";
                return false;
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
            return true;
        }

        private void CleanupDeadHostLocal(int deadHost)
        {
            WorldProxyLifecycleHandlers.DestroyRemoteProxy(deadHost);
            PlayerHeldLightApplyHandlers.DestroyRemoteFlareLight(deadHost);
            PlayerHeldLightApplyHandlers.DestroyRemoteItemLight(deadHost);
            if (_remotePlayers.ContainsKey(deadHost))
                _remotePlayers.Remove(deadHost);
            PlayerPositionManager.RemovePlayer(deadHost);
            _session.RemoteOutsideLocation.Remove(deadHost);
            try
            {
                DeathStateTracker.OnRemoteDisconnected(deadHost);
            }
            catch (Exception ex) { ModLog.Swallowed(ex); /* optional */ }
            Sync.FinalDreamsceneManager.OnRemoteDisconnected(deadHost);
            // Its NPC talk lock and this player's view of its talk: nobody is left to release
            // them (the lock held the NPC for up to its 90 s lease).
            Sync.NpcDialogueLock.ReleaseAllForPlayer(deadHost);
            Sync.DialogMirror.OwnerGone(deadHost);
        }
    }
}
