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

        private void PromoteLocalToHost(int keepId, string reason)
        {
            bool steam = IsSteamSession;
            StopTransportOnly("promote after " + reason, leaveSteamLobby: !steam);
            // Steam: keep lobby membership; re-arm backend after StopTransportOnly cleared it.
            if (steam)
                _backend = ConnectionBackend.Steam;

            _role = NetworkRole.Host;
            _localPlayerId = keepId;
            _hostPlayerId = keepId;
            _nextPlayerId = Math.Max(2, keepId + 1);
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                int pid = _peerRoster[i].PlayerId;
                if (pid >= _nextPlayerId)
                    _nextPlayerId = pid + 1;
            }

            // Reclaim sim: release client host-sync freeze so AI/entities run under us.
            ReclaimSimulationAuthorityAfterPromote();

            if (steam)
            {
                PromoteLocalToSteamHost(keepId, reason);
                return;
            }

            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            int port = _sessionPort > 0 ? _sessionPort : PluginInfo.DefaultPort;
            if (!_net.Start(port))
            {
                // Port may still be in TIME_WAIT after a crash; try nearby ports.
                bool bound = false;
                for (int d = 1; d <= 5 && !bound; d++)
                {
                    int alt = port + d;
                    if (_net.Start(alt))
                    {
                        port = alt;
                        bound = true;
                        ModLog.Warn(LogCat.Network,
                            "Host grant bound alternate port " + alt + " (primary busy)");
                    }
                }
                if (!bound)
                {
                    ModLog.Error(LogCat.Network, "Host migration promote failed to bind port " + port);
                    _role = NetworkRole.Offline;
                    _migrationInProgress = false;
                    StatusText = "Host grant failed (port busy)";
                    return;
                }
            }

            NoteSessionPort(port);
            _handshakeComplete = false;
            _handshakedPeers.Clear();
            _migrationInProgress = false;
            StatusText = "HOST GRANTED — port " + port + " (p" + keepId + ")";
            ModLog.Event(LogCat.Network,
                "HOST GRANTED: local p" + keepId + " now host on port " + port
                + " | reason=" + reason);

            // Do NOT auto-Save here. Promote used to checkpoint after host leave, but the
            // survivor was a co-op client with a partially synced world; writing sav.dat corrupted
            // their slot. New host persists via manual F3 when the sim is trustworthy.
            // TryHostMigrationSaveCheckpoint(); // disabled; host-leave client Save

            BroadcastPeerRoster();
            // Time authority is us now; push the clock to reconnecting peers as they join.
            try { SendTimeSyncTo(-1); } catch { /* no peers yet */ }
        }

        private void PromoteLocalToSteamHost(int keepId, string reason)
        {
            var allow = new List<ulong>(8);
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                PeerRosterEntry e = _peerRoster[i];
                if (e.PlayerId == keepId) continue;
                if (ulong.TryParse(e.Address, out ulong sid) && sid != 0)
                    allow.Add(sid);
            }

            if (!Steam.BeginHostingAfterMigration())
            {
                ModLog.Error(LogCat.Network, "Steam host grant promote failed");
                _role = NetworkRole.Offline;
                _backend = ConnectionBackend.None;
                _migrationInProgress = false;
                StatusText = "Host grant failed (Steam)";
                return;
            }

            Steam.ArmMigrationAllowlist(allow, 60);
            _backend = ConnectionBackend.Steam;
            NoteSessionPort(SteamCoopTransport.MigrationVirtualPort);
            _handshakeComplete = false;
            _handshakedPeers.Clear();
            _migrationInProgress = false;
            StatusText = "HOST GRANTED — Steam (p" + keepId + ")";
            ModLog.Event(LogCat.Network,
                "HOST GRANTED (Steam): local p" + keepId + " | reason=" + reason);

            BroadcastPeerRoster();
            try { SendTimeSyncTo(-1); } catch { /* no peers yet */ }
        }

        private void ConnectSteamPreservingId(string steamIdRaw, int electHostId)
        {
            if (!ulong.TryParse(steamIdRaw, out ulong sid) || sid == 0)
            {
                ModLog.Error(LogCat.Network, "Steam migration: bad elect steam id '" + steamIdRaw + "'");
                _migrationInProgress = false;
                StopNetwork();
                StatusText = "Host lost — bad Steam route";
                return;
            }

            int keepId = _localPlayerId;
            // Keep lobby when possible (graceful SetLobbyOwner); crash allowlist covers the rest.
            StopTransportOnly("migration steam reconnect", leaveSteamLobby: false);
            _role = NetworkRole.Client;
            _localPlayerId = keepId;
            _hostPlayerId = electHostId > 0 ? electHostId : _hostPlayerId;
            _backend = ConnectionBackend.Steam;

            var hostSid = new CSteamID(sid);
            if (!Steam.ConnectP2PDirect(hostSid))
            {
                ModLog.Error(LogCat.Network, "Steam migration ConnectP2P failed");
                _backend = ConnectionBackend.None;
                _role = NetworkRole.Offline;
                // Leave _migrationInProgress so TickHostMigrationRetry can try again.
                StatusText = "Migrating — Steam connect failed, retrying…";
                return;
            }

            int hostKey = _hostPlayerId > 0 ? _hostPlayerId : 1;
            _steamPeers[hostKey] = hostSid;
            _steamIdToPlayer[sid] = hostKey;
            NoteSessionPort(SteamCoopTransport.MigrationVirtualPort);
            StatusText = "Migrating → Steam " + sid + " as p" + keepId;
            ModLog.Event(LogCat.Network,
                "Migration Steam connect as p" + keepId + " → " + sid
                + " electHost=" + hostKey);
            // Handshake fires from OnSteamLobbyReady when SNS Connected.
        }

        /// <summary>
        /// Client→host flip: release entity drive, restore clock, drop proxies, refresh grid cull.
        /// </summary>
        private void ReclaimSimulationAuthorityAfterPromote()
        {
            try
            {
                // Release MovePosition/kinematic drive before clearing maps.
                ClientEntityInterpolationService.ReleaseAuthorityForPromote();
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Entity authority reclaim: " + ex.Message);
            }

            try
            {
                Sync.WorldPhysicsSyncService.Reset();
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "Physics reclaim: " + ex.Message);
            }

            try
            {
                Controller ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.DoUpdateTime = true;
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "DoUpdateTime reclaim: " + ex.Message);
            }

            PlayerInteractHandlers?.ClearDragSessionState();
            DWMPHorde.Audio.MovingObjectSoundService.Reset();

            // Drop proxies for peers we expect to reconnect (they re-spawn from PlayerState).
            var proxyIds = new List<int>(_remoteProxies.Keys);
            foreach (int id in proxyIds)
            {
                if (id == _localPlayerId) continue;
                WorldProxyHandlers.DestroyRemoteProxy(id);
                PlayerHeldLightHandlers.DestroyRemoteFlareLight(id);
                PlayerHeldLightHandlers.DestroyRemoteItemLight(id);
                _remotePlayers.Remove(id);
            }

            // Force WorldGrid to re-cull around local player (join load can leave a fat active set).
            try
            {
                Player p = Player.Instance;
                if (p != null && Singleton<WorldGrid>.Instance != null)
                {
                    Vector3 pos = p._transform != null ? p._transform.position : p.transform.position;
                    Singleton<WorldGrid>.Instance.refreshPosition(pos, instant: true, force: true);
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "WorldGrid refresh after promote: " + ex.Message);
            }

            EntityStateBroadcastService.Resume();
            ModLog.Event(LogCat.Network, "Simulation authority reclaimed (entities+clock+AI host path)");
        }

        private void ConnectToHostPreservingId(string address, int port, int electHostId)
        {
            int keepId = _localPlayerId;
            StopTransportOnly("migration reconnect");
            _role = NetworkRole.Client;
            _localPlayerId = keepId;
            _hostPlayerId = electHostId > 0 ? electHostId : _hostPlayerId;
            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            _net.Start();
            string key = Config.ModConfig.GetConnectionKey();
            NetPeer peer = _net.Connect(address, port, key);
            int hostKey = _hostPlayerId > 0 ? _hostPlayerId : 1;
            _peers[hostKey] = peer;
            NoteSessionPort(port);
            StatusText = "Migrating → " + address + ":" + port + " as p" + keepId;
            ModLog.Event(LogCat.Network,
                "Migration connect as p" + keepId + " → " + address + ":" + port
                + " electHost=" + hostKey);
        }

        private int TryRebindPreferredPlayerId(int provisionalId, int preferredId, NetPeer peer)
        {
            if (preferredId <= 0 || preferredId == provisionalId || peer == null)
                return provisionalId;
            if (preferredId == _localPlayerId)
                return provisionalId;
            if (_peers.ContainsKey(preferredId))
                return provisionalId;

            _peers.Remove(provisionalId);
            _peers[preferredId] = peer;
            if (_handshakedPeers.Remove(provisionalId))
                _handshakedPeers.Add(preferredId);
            if (_awaitingLateJoinBulk.TryGetValue(provisionalId, out float t))
            {
                _awaitingLateJoinBulk.Remove(provisionalId);
                _awaitingLateJoinBulk[preferredId] = t;
            }
            if (_pendingHeavyLateJoinBulk.TryGetValue(provisionalId, out int heavyPhase))
            {
                _pendingHeavyLateJoinBulk.Remove(provisionalId);
                _pendingHeavyLateJoinBulk[preferredId] = heavyPhase;
            }
            if (_peersLoadingWorld.Remove(provisionalId))
                _peersLoadingWorld.Add(preferredId);
            if (_peersCoopReconnect.Remove(provisionalId))
                _peersCoopReconnect.Add(preferredId);

            if (preferredId >= _nextPlayerId)
                _nextPlayerId = preferredId + 1;

            ModLog.Event(LogCat.Network,
                "Rebind peer id " + provisionalId + " → preferred " + preferredId);
            return preferredId;
        }

        private static string _cachedLanIPv4;
        private static float _cachedLanIPv4At = -999f;
        private const float LanIPv4CacheSec = 60f;

        /// <summary>
        /// LAN IPv4 for roster gossip. NetworkInterface.GetAllNetworkInterfaces is expensive
        /// on Windows and must not run on the roster timer.
        /// </summary>
    }
}
