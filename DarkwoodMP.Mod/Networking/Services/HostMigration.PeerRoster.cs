using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Peer roster gossip build/apply and LAN IPv4 helpers.</summary>
    public sealed partial class LanNetworkManager
    {
        internal void BroadcastPeerRoster()
        {
            if (_role != NetworkRole.Host)
                return;
            if (!IsSteamSession && _net == null)
                return;
            if (IsSteamSession && (_steam == null || !_steam.IsActive))
                return;

            var list = BuildRosterEntries();
            for (int i = 0; i < list.Count; i++)
            {
                PeerRosterEntry e = list[i];
                e.Name = PlayerNames.ForRoster(this, e.PlayerId);
                list[i] = e;
            }
            var msg = new PeerRosterMessage
            {
                HostPlayerId = _localPlayerId,
                SessionPort = IsSteamSession
                    ? SteamCoopTransport.MigrationVirtualPort
                    : _sessionPort,
                Entries = list.ToArray()
            };
            ApplyPeerRosterLocal(msg);

            Broadcast(NetMessageType.PeerRoster, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
            // Join/leave changes the party size, and a roster tick is the one place that also
            // notices a host-side setting edit: peers re-sync whenever any of it differs.
            BroadcastSessionSettingsIfChanged();
            // Trace only; frequent Support/Dev events are noise.
            ModLog.Trace(LogCat.Network, () => "[HostMigration] roster peers=" + list.Count
                + " hostId=" + _localPlayerId
                + (IsSteamSession ? " steam" : (" port=" + _sessionPort)));
        }

        private List<PeerRosterEntry> BuildRosterEntries()
        {
            if (IsSteamSession)
                return BuildSteamRosterEntries();

            var list = new List<PeerRosterEntry>(8);
            // Cached because GetAllNetworkInterfaces is expensive on the roster tick.
            string hostIp = GetCachedPrimaryLanIPv4() ?? "127.0.0.1";
            list.Add(new PeerRosterEntry
            {
                PlayerId = _localPlayerId,
                Address = hostIp,
                Port = _sessionPort
            });

            AddPeerRosterEntries(list, _lanPeers, _sessionPort);
            return list;
        }

        private List<PeerRosterEntry> BuildSteamRosterEntries()
        {
            var list = new List<PeerRosterEntry>(8);
            int port = SteamCoopTransport.MigrationVirtualPort;
            CSteamID self = SteamCoopTransport.LocalSteamId();
            if (self.IsValid())
            {
                list.Add(new PeerRosterEntry
                {
                    PlayerId = _localPlayerId,
                    Address = self.m_SteamID.ToString(),
                    Port = port
                });
            }

            AddPeerRosterEntries(list, _steamPeers, port);
            return list;
        }

        private static void AddPeerRosterEntries(List<PeerRosterEntry> list, IPeerTable peers, int port)
        {
            IReadOnlyList<int> ids = peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                string addr = peers.RosterAddress(ids[i]);
                if (addr == null)
                    continue;
                list.Add(new PeerRosterEntry { PlayerId = ids[i], Address = addr, Port = port });
            }
        }

        /// <summary>Roster Address is a SteamID64 decimal string (not IPv4).</summary>
        private static bool IsSteamRosterAddress(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;
            // SteamID64 is 17 digits starting with 7656…; IPv4 has dots.
            if (address.IndexOf('.') >= 0)
                return false;
            return ulong.TryParse(address, out ulong id) && id > 0x0110000100000000UL;
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
            PlayerNames.ApplyRoster(msg.Entries);
            if (msg.Entries == null)
            {
                PruneRemoteProxiesMissingFromRoster();
                return;
            }
            for (int i = 0; i < msg.Entries.Length; i++)
            {
                PeerRosterEntry e = msg.Entries[i];
                if (e.PlayerId <= 0 || string.IsNullOrEmpty(e.Address))
                    continue;
                _peerRoster.Add(e);
            }
            PruneRemoteProxiesMissingFromRoster();
        }

        /// <summary>
        /// True when <paramref name="playerId"/> appears in the latest gossip roster
        /// (host + connected peers). Used by LocationExit disconnect path so clients
        /// DestroyRemoteProxy instead of teleporting a ghost.
        /// </summary>
        internal int PeerRosterCount => _peerRoster.Count;

        internal bool IsPlayerListedInPeerRoster(int playerId)
        {
            if (playerId <= 0) return false;
            if (playerId == _localPlayerId) return true;
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                if (_peerRoster[i].PlayerId == playerId)
                    return true;
            }
            // Host still has a live transport slot even if roster tick is stale.
            if (_role == NetworkRole.Host && HasPeer(playerId))
                return true;
            return false;
        }

        /// <summary>
        /// Drop client proxies whose player ids vanished from the roster (disconnect /
        /// PeerLeft). Never destroys the local player. Clears RemoteOutsideLocation too.
        /// </summary>
        private void PruneRemoteProxiesMissingFromRoster()
        {
            if (_remoteProxies == null || _remoteProxies.Count == 0)
                return;

            var alive = new HashSet<int>();
            alive.Add(_localPlayerId);
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                int pid = _peerRoster[i].PlayerId;
                if (pid > 0) alive.Add(pid);
            }
            // Host: transport slots are authoritative between roster ticks.
            if (_role == NetworkRole.Host)
            {
                foreach (int id in EnumeratePeerIds())
                    alive.Add(id);
            }

            List<int> gone = null;
            foreach (int id in _remoteProxies.Keys)
            {
                if (alive.Contains(id)) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(id);
            }
            if (gone == null) return;

            for (int i = 0; i < gone.Count; i++)
            {
                int id = gone[i];
                DeathStateTracker.OnRemotePeerGone(id);
                // Belt: clear stuck drag claims if host STOP was lost (host already
                // broadcast on disconnect; clients must still drop local claim maps).
                PlayerInteractHandlers?.ReleaseDragClaimsForDisconnectedPlayer(
                    id, broadcastStop: false);
                // Host already broadcast DialogNpcLock release; clients still drop
                // local lease maps so talk is not blocked until 90s expiry.
                Sync.NpcDialogueLock.ReleaseAllForPlayer(id);
                WorldProxyLifecycleHandlers.DestroyRemoteProxy(id);
                _session.RemoteOutsideLocation.Remove(id);
                _remotePlayers.Remove(id);
                PlayerPositionManager.RemovePlayer(id);
                DestroyRemoteFlareLight(id);
                DestroyRemoteItemLight(id);
                PlayerFXHandlers?.ClearPendingAnimLibrary(id);
                Sync.DreamForestSpiritAggro.ClearIfOwner(id);
                ModLog.Event(LogCat.Network,
                    "PeerRoster prune destroyed proxy p" + id);
            }
        }

        private static string GetCachedPrimaryLanIPv4()
        {
            float now = Time.unscaledTime;
            if (!string.IsNullOrEmpty(_cachedLanIPv4) && now - _cachedLanIPv4At < LanIPv4CacheSec)
                return _cachedLanIPv4;
            _cachedLanIPv4 = GetPrimaryLanIPv4();
            _cachedLanIPv4At = now;
            return _cachedLanIPv4;
        }

        /// <summary>Force refresh on host start / bind so roster is not stuck on a stale NIC.</summary>
        internal static void InvalidateLanIPv4Cache()
        {
            _cachedLanIPv4 = null;
            _cachedLanIPv4At = -999f;
        }

        private static string GetPrimaryLanIPv4()
        {
            // The address this player typed in is the one the others can reach (VPN, forwarded port).
            if (Config.ModConfig.TryGetHostAddress(out IPAddress chosen))
                return chosen.ToString();
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
