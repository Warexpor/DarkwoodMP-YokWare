using System.Collections.Generic;
using System.Net;
using DWMPHorde.Logging;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Promoted-host state and the PlayerId rebind a reconnecting survivor asks for.
    /// A client may only take an id the host reserved for it: a chapter-resume reservation
    /// (by stable key) or a migration survivor id from the last roster, matched by address.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>Host after migration promote: the local world was a client copy.</summary>
        private bool _isPromotedHost;

        /// <summary>Promoted host: survivor PlayerId → roster address (IP or SteamID64) it may reclaim.</summary>
        private readonly Dictionary<int, string> _migrationReservedIds = new Dictionary<int, string>(8);

        /// <summary>
        /// Host: title joiners sent the world (they detach for the offline load, then reconnect
        /// AlreadyInWorld). PlayerId → the stable key that may reclaim it.
        /// </summary>
        private readonly Dictionary<int, string> _joinPipelineReservedIds = new Dictionary<int, string>(8);

        /// <summary>Host: remember a world-share recipient's id so its phase-3 reconnect keeps it.</summary>
        internal void ReserveIdForJoinPipeline(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (TryGetStableClientKeyForPlayer(playerId, out string key))
                _joinPipelineReservedIds[playerId] = key;
        }

        /// <summary>Handshake: the claimed id was reserved for this stable key by the join pipeline.</summary>
        private bool TryTakeJoinPipelineReservation(int playerId, string rawStableKey)
        {
            if (!_joinPipelineReservedIds.TryGetValue(playerId, out string key))
                return false;
            string claimant = ClientStateBackup.SanitizeStableClientKey(rawStableKey);
            if (string.IsNullOrEmpty(claimant)
                || !string.Equals(key, claimant, System.StringComparison.OrdinalIgnoreCase))
                return false;
            _joinPipelineReservedIds.Remove(playerId);
            return true;
        }

        /// <summary>This host was promoted by migration; automatic saves are off for the session.</summary>
        internal bool IsPromotedHost => _role == NetworkRole.Host && _isPromotedHost;

        /// <summary>Promote: every other roster entry may come back under its old id.</summary>
        private void ArmMigrationReservations(int keepId)
        {
            _migrationReservedIds.Clear();
            for (int i = 0; i < _peerRoster.Count; i++)
            {
                PeerRosterEntry e = _peerRoster[i];
                if (e.PlayerId <= 1 || e.PlayerId == keepId || string.IsNullOrEmpty(e.Address))
                    continue;
                _migrationReservedIds[e.PlayerId] = e.Address;
            }
            if (_migrationReservedIds.Count > 0)
                ModLog.Event(LogCat.Network,
                    "[HostMigration] reserved " + _migrationReservedIds.Count + " survivor PlayerId(s) for reconnect");
        }

        /// <summary>
        /// Take a survivor reservation for <paramref name="playerId"/> when the connecting address
        /// matches the roster. Steam ids must match exactly; a LAN address also matches across
        /// loopback (old or new host on the same machine as the survivor).
        /// </summary>
        private bool TryConsumeMigrationReservation(int playerId, string address)
        {
            if (!_migrationReservedIds.TryGetValue(playerId, out string reserved))
            {
                ModLog.Warn(LogCat.Network,
                    "Rebind to p" + playerId + " refused — id not reserved for this peer");
                return false;
            }
            bool match = string.Equals(reserved, address, System.StringComparison.OrdinalIgnoreCase)
                || (!IsSteamRosterAddress(reserved) && (IsLoopback(reserved) || IsLoopback(address)));
            if (!match)
            {
                ModLog.Warn(LogCat.Network,
                    "Rebind to p" + playerId + " refused — address does not match the survivor roster");
                return false;
            }
            _migrationReservedIds.Remove(playerId);
            return true;
        }

        private static bool IsLoopback(string address)
        {
            return !string.IsNullOrEmpty(address)
                && IPAddress.TryParse(address, out IPAddress ip)
                && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
        }

        private static string LanAddressOf(NetPeer peer)
        {
            IPAddress ip = peer != null ? peer.Address : null;
            if (ip == null)
                return null;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            return ip.ToString();
        }

        /// <summary>
        /// Move every per-PlayerId record from the provisional id to the rebound one (transport
        /// slots are moved by the caller). Presentation built under the provisional id is dropped;
        /// it is rebuilt from the peer's next PlayerState under the final id.
        /// </summary>
        private void RebindPlayerIdState(int from, int to)
        {
            if (from == to || from <= 0 || to <= 0)
                return;

            if (_session.Link.Handshaked.Remove(from))
                _session.Link.Handshaked.Add(to);
            if (_session.Link.Rejected.Remove(from))
                _session.Link.Rejected.Add(to);
            MoveKey(_session.Link.AwaitingLateJoinBulk, from, to);
            MoveKey(_session.Link.PendingHeavyLateJoinBulk, from, to);
            MoveKey(_session.Link.HeavyPhaseFailures, from, to);
            if (_session.Link.LoadingWorld.Remove(from))
                _session.Link.LoadingWorld.Add(to);
            if (_session.Link.CoopReconnect.Remove(from))
                _session.Link.CoopReconnect.Add(to);
            MoveKey(_session.Link.LastPlayerStateSequence, from, to);
            MoveKey(_session.Link.LastPhysicsStateSequence, from, to);
            MoveKey(_session.Link.LastReliablePhysicsStateSequence, from, to);
            MoveKey(_session.StableKeyByPlayer, from, to);
            MoveKey(_session.RemoteOutsideLocation, from, to);
            MoveSticky(from, to, NetMessageType.PlayerLightState);
            MoveSticky(from, to, NetMessageType.PlayerAnimLibrary);

            if (_remotePlayers.TryGetValue(from, out RemotePlayerState state))
            {
                _remotePlayers.Remove(from);
                if (state != null)
                    state.PlayerId = to;
                _remotePlayers[to] = state;
            }

            if (_remoteProxies.ContainsKey(from))
                WorldProxyLifecycleHandlers.DestroyRemoteProxy(from);
            DestroyRemoteFlareLight(from);
            DestroyRemoteItemLight(from);
            PlayerPositionManager.RemovePlayer(from);
            PlayerLightFxApplyHandlers?.ClearPendingPlayerLightsFor(from);
            PlayerFXHandlers?.ClearPendingAnimLibrary(from);
            Sync.PeerItemPresence.ClearPlayer(from);

            if (to >= _nextPlayerId)
                _nextPlayerId = to + 1;
        }

        private void MoveSticky(int from, int to, NetMessageType type)
        {
            long src = StickyKey(from, type);
            if (_session.StickyPlayerPayloads.TryGetValue(src, out byte[] payload))
            {
                _session.StickyPlayerPayloads.Remove(src);
                _session.StickyPlayerPayloads[StickyKey(to, type)] = payload;
            }
        }

        private static void MoveKey<T>(Dictionary<int, T> map, int from, int to)
        {
            if (map.TryGetValue(from, out T value))
            {
                map.Remove(from);
                map[to] = value;
            }
        }
    }
}
