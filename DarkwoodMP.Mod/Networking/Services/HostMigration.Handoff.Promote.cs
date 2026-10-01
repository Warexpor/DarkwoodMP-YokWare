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
            // Survivor's world was a client copy: no automatic save may ever write it (leave
            // checkpoint, SaveSync). Cleared with the session in ResetMigrationState.
            _isPromotedHost = true;
            ArmMigrationReservations(keepId);
            _localPlayerId = keepId;
            _hostPlayerId = keepId;
            _nextPlayerId = Math.Max(2, keepId + 1);
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                int pid = _peerRoster[i].PlayerId;
                if (pid >= _nextPlayerId)
                    _nextPlayerId = pid + 1;
            }

            // The new host owns the session settings: drop the old host's applied values so this
            // install's own config (and the roster-derived party size) applies, and announce them
            // to the survivors on the next roster tick / handshake.
            SessionSettings.ResetToLocal();
            _sessionSettingsBroadcastOnce = false;

            // Reclaim sim: release client host-sync freeze so AI/entities run under us.
            ReclaimSimulationAuthorityAfterPromote();

            if (steam)
            {
                PromoteLocalToSteamHost(keepId, reason);
                return;
            }

            _net = CreateNetManager();
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
                    _migrationInProgress = false;
                    // Full teardown (reset registry, share service, unstarted socket, proxies). The
                    // promoted flag keeps StopNetwork from writing a leave checkpoint.
                    StopNetwork();
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
            // their slot. Old host already flushed via graceful-leave checkpoint; survivor
            // persists via manual F3 when the sim is trustworthy.
            // TryHostMigrationSaveCheckpoint(); // disabled; corrupts survivor sav
            NotifyPromotedHostSaveReminder();

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
                _migrationInProgress = false;
                StopNetwork();
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
            NotifyPromotedHostSaveReminder();

            BroadcastPeerRoster();
            try { SendTimeSyncTo(-1); } catch { /* no peers yet */ }
        }


        /// <summary>
        /// Safer than auto-Save on promote: remind the survivor to F3 when the sim is
        /// trustworthy. Does not write sav.dat (that corrupted co-op client slots).
        /// </summary>
        private void NotifyPromotedHostSaveReminder()
        {
            const string tip = "You are host now — press F3 to save when ready (auto-save on promote is disabled)";
            try
            {
                StatusText = tip;
                ModLog.Event(LogCat.Save, tip);
                if (Player.Instance != null && !Core.mainMenu && !Core.loadingGame)
                {
                    DWMPHorde.Patches.PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage(tip); }
                    finally { DWMPHorde.Patches.PersonalFlavorHud.EndBypass(); }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Promote F3 reminder: " + ex.Message);
            }
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
                // Stay a migrating Client on the Steam backend: TickHostMigrationRetry only runs
                // for Role==Client, so dropping to Offline here stranded the session with no retry
                // and no teardown. It retries up to MigrationMaxRetries, then StopNetwork.
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
                Sync.WorldPhysicsSyncService.ResetForPromote();
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
            _net = CreateNetManager();
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

        private int TryRebindPreferredPlayerId(int provisionalId, int preferredId, NetPeer peer,
            bool reservedForResume)
        {
            if (preferredId <= 0 || preferredId == provisionalId || peer == null)
                return provisionalId;
            if (preferredId == _localPlayerId)
                return provisionalId;
            if (_peers.ContainsKey(preferredId))
                return provisionalId;
            if (!reservedForResume && !TryConsumeMigrationReservation(preferredId, LanAddressOf(peer)))
                return provisionalId;

            _peers.Remove(provisionalId);
            _peers[preferredId] = peer;
            RebindPlayerIdState(provisionalId, preferredId);

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
