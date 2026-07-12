using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DWMPHorde.Logging;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Full host-crash / host-leave grant for n+ LAN:
    /// roster gossip → deterministic elect → soft promote or reconnect → reclaim sim authority.
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
        /// <summary>Set while local StopNetwork / intentional tear — do not treat as host crash.</summary>
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
        /// Tear LiteNetLib only — keep chapter/player state for host grant promote.
        /// </summary>
        private void StopTransportOnly(string reason)
        {
            ModLog.Event(LogCat.Network, "StopTransportOnly: " + reason);
            if (_net != null)
            {
                try { _net.Stop(); }
                catch (Exception ex)
                {
                    ModLog.Warn(LogCat.Network, "StopTransportOnly net.Stop: " + ex.Message);
                }
                _net = null;
            }
            _peers.Clear();
            _handshakedPeers.Clear();
            _handshakeComplete = false;
            _peersLoadingWorld.Clear();
            _peersCoopReconnect.Clear();
            _awaitingLateJoinBulk.Clear();
            _pendingHeavyLateJoinBulk.Clear();
            EntityStateBroadcastService.SetPeers(_peers);
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
            if (string.IsNullOrEmpty(_migrationTargetAddress) || _migrationTargetPort <= 0)
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
                    + " → " + _migrationTargetAddress + ":" + _migrationTargetPort);
                ConnectToHostPreservingId(_migrationTargetAddress, _migrationTargetPort, _migrationElectId);
            }
        }

        internal void BroadcastPeerRoster()
        {
            if (_role != NetworkRole.Host || _net == null)
                return;

            var list = BuildRosterEntries();
            var msg = new PeerRosterMessage
            {
                HostPlayerId = _localPlayerId,
                SessionPort = _sessionPort,
                Entries = list.ToArray()
            };
            ApplyPeerRosterLocal(msg);

            Broadcast(NetMessageType.PeerRoster, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            // Trace only — was flooding Support/Dev Event every 4s (not a hitch, but noise).
            ModLog.Trace(LogCat.Network, () => "[HostMigration] roster peers=" + list.Count
                + " hostId=" + _localPlayerId + " port=" + _sessionPort);
        }

        private List<PeerRosterEntry> BuildRosterEntries()
        {
            var list = new List<PeerRosterEntry>(8);
            string hostIp = GetPrimaryLanIPv4() ?? "127.0.0.1";
            list.Add(new PeerRosterEntry
            {
                PlayerId = _localPlayerId,
                Address = hostIp,
                Port = _sessionPort
            });

            foreach (var kvp in _peers)
            {
                NetPeer peer = kvp.Value;
                if (peer == null) continue;
                IPAddress ip = peer.Address;
                if (ip == null) continue;
                if (ip.IsIPv4MappedToIPv6)
                    ip = ip.MapToIPv4();
                string addr = ip.ToString();
                if (string.IsNullOrEmpty(addr) || addr == "0.0.0.0")
                    continue;
                list.Add(new PeerRosterEntry
                {
                    PlayerId = kvp.Key,
                    Address = addr,
                    Port = _sessionPort
                });
            }
            return list;
        }

        private void HandlePeerRoster(PeerRosterMessage msg)
        {
            if (_role != NetworkRole.Client)
                return;
            ApplyPeerRosterLocal(msg);
        }

        private void ApplyPeerRosterLocal(PeerRosterMessage msg)
        {
            if (msg.HostPlayerId > 0)
                _hostPlayerId = msg.HostPlayerId;
            if (msg.SessionPort > 0)
                _sessionPort = msg.SessionPort;

            _peerRoster.Clear();
            if (msg.Entries == null) return;
            for (int i = 0; i < msg.Entries.Length; i++)
            {
                PeerRosterEntry e = msg.Entries[i];
                if (e.PlayerId <= 0 || string.IsNullOrEmpty(e.Address))
                    continue;
                _peerRoster.Add(e);
            }
        }

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
            Broadcast(NetMessageType.HostHandoff, w => new HostHandoffMessage
            {
                ElectPlayerId = elect,
                SessionPort = _sessionPort
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            ModLog.Event(LogCat.Network,
                "Graceful host leave — handoff to p" + elect);
            StatusText = "Handing host to p" + elect + "…";

            // Flush packets then FREE listen port immediately so elect can bind the same port.
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
            // Suppress already set — StopNetwork will not re-enter migration.
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
                // Handoff elect not in roster — fall back to pure elect.
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
                + " candidates=" + candidates.Count);

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

            // Dual-box same machine: roster may say 127.0.0.1 for everyone — fine.
            // Prefer connect address from config if elect address is loopback and we used LAN before.
            string addr = target.Value.Address;
            int port = target.Value.Port > 0 ? target.Value.Port : _sessionPort;
            _migrationTargetAddress = addr;
            _migrationTargetPort = port;
            _migrationRetryCount = 0;
            // First connect now; first retry after MigrationRetrySec (avoid double-connect same frame).
            _migrationRetryAt = Time.unscaledTime + MigrationRetrySec;
            _localPlayerId = keepId;
            StatusText = "Host lost — reconnecting to p" + elect + "…";
            ConnectToHostPreservingId(addr, port, elect);
        }

        private void CleanupDeadHostLocal(int deadHost)
        {
            DestroyRemoteProxy(deadHost);
            DestroyRemoteFlareLight(deadHost);
            DestroyRemoteItemLight(deadHost);
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

        private void PromoteLocalToHost(int keepId, string reason)
        {
            StopTransportOnly("promote after " + reason);
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

            _net = new NetManager(this) { UnconnectedMessagesEnabled = false, DisconnectTimeout = 30000 };
            int port = _sessionPort > 0 ? _sessionPort : PluginInfo.DefaultPort;
            if (!_net.Start(port))
            {
                // Port may still be in TIME_WAIT after crash — try a few nearby ports.
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
            // survivor was a co-op client (half-synced AI/world) — writing sav.dat corrupted
            // their slot. New host persists via manual F3 when the sim is trustworthy.
            // TryHostMigrationSaveCheckpoint(); // disabled — host-leave client Save

            BroadcastPeerRoster();
            // Time authority is us now — push clock to reconnecting peers as they join.
            try { SendTimeSyncTo(-1); } catch { /* no peers yet */ }
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

            _dragClaims.Clear();
            _remoteDragItemIds.Clear();
            _remoteDragItemNames.Clear();
            DWMPHorde.Audio.MovingObjectSoundService.Reset();

            // Drop proxies for peers we expect to reconnect (they re-spawn from PlayerState).
            var proxyIds = new List<int>(_remoteProxies.Keys);
            foreach (int id in proxyIds)
            {
                if (id == _localPlayerId) continue;
                DestroyRemoteProxy(id);
                DestroyRemoteFlareLight(id);
                DestroyRemoteItemLight(id);
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

        private void TryHostMigrationSaveCheckpoint()
        {
            // Defer heavy Save off the promote frame — dual-box mid-session Save was a multi-second
            // hitch on the new host (looked like "FPS dies after grant").
            try
            {
                if (this == null || !isActiveAndEnabled) return;
                StartCoroutine(HostMigrationSaveCheckpointDeferred());
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Host grant Save schedule failed: " + ex.Message);
            }
        }

        private IEnumerator HostMigrationSaveCheckpointDeferred()
        {
            // Let a few frames settle after bind + reclaim.
            for (int i = 0; i < 30; i++)
                yield return null;
            try
            {
                if (Singleton<SaveManager>.Instance == null) yield break;
                if (Core.mainMenu || Core.loadingGame) yield break;
                if (_role != NetworkRole.Host) yield break;
                Singleton<SaveManager>.Instance.Save(doJson: true);
                ModLog.Event(LogCat.Save, "Host grant checkpoint Save() (deferred after promote)");
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Host grant Save checkpoint failed: " + ex.Message);
            }
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

        private static string GetPrimaryLanIPv4()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    foreach (UnicastIPAddressInformation ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string s = ip.Address.ToString();
                        if (s.StartsWith("127.")) continue;
                        // Skip APIPA
                        if (s.StartsWith("169.254.")) continue;
                        return s;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.Network, "GetPrimaryLanIPv4: " + ex.Message);
            }
            return "127.0.0.1";
        }
    }
}
