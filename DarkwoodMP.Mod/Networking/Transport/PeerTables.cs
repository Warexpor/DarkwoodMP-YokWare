using System;
using System.Collections.Generic;
using System.Net;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using LiteNetLib;
using Steamworks;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Player id ↔ connection for one transport backend. <see cref="LanNetworkManager"/> talks to the
    /// active table through this interface, so session code does not branch on LAN vs Steam to
    /// count, route, rebind or drop a peer.
    /// </summary>
    internal interface IPeerTable
    {
        int Count { get; }

        /// <summary>Connected player ids, in join order. Never modified while a caller iterates it by index.</summary>
        IReadOnlyList<int> Ids { get; }

        bool Contains(int playerId);

        /// <summary>The peer may receive session traffic (Steam: password proven when the host requires one).</summary>
        bool IsRoutable(int playerId);

        /// <summary>Send an already-framed buffer. False when the id is unknown or the backend refused.</summary>
        bool Send(int playerId, byte[] data, int length, DeliveryMethod method);

        /// <summary>Move a peer to another id (reconnect keeps its old id). False if the source is missing or the target taken.</summary>
        bool Rebind(int from, int to);

        /// <summary>Forget the peer (the caller closes the connection where the backend needs it).</summary>
        bool Remove(int playerId);

        void Clear();

        /// <summary>Address the migration roster gossips for this peer (IPv4 or SteamID64), or null.</summary>
        string RosterAddress(int playerId);
    }

    /// <summary>LiteNetLib peers, with a reverse map so a receive resolves its sender in O(1).</summary>
    internal sealed class LanPeerTable : IPeerTable
    {
        private readonly Dictionary<int, NetPeer> _byId = new Dictionary<int, NetPeer>();
        private readonly Dictionary<NetPeer, int> _byPeer = new Dictionary<NetPeer, int>();
        private readonly List<int> _ids = new List<int>(8);

        public int Count => _byId.Count;
        public IReadOnlyList<int> Ids => _ids;
        public bool Contains(int playerId) => _byId.ContainsKey(playerId);
        public bool IsRoutable(int playerId) => _byId.ContainsKey(playerId);

        /// <summary>Map <paramref name="playerId"/> to <paramref name="peer"/>, replacing any older mapping of either.</summary>
        internal void Set(int playerId, NetPeer peer)
        {
            if (peer == null)
                return;
            if (_byPeer.TryGetValue(peer, out int oldId) && oldId != playerId)
                Remove(oldId);
            if (_byId.TryGetValue(playerId, out NetPeer oldPeer))
                _byPeer.Remove(oldPeer);
            else
                _ids.Add(playerId);
            _byId[playerId] = peer;
            _byPeer[peer] = playerId;
        }

        internal bool TryGetPeer(int playerId, out NetPeer peer) => _byId.TryGetValue(playerId, out peer);

        /// <summary>Player id of <paramref name="peer"/>, or -1.</summary>
        internal int IdOf(NetPeer peer)
            => peer != null && _byPeer.TryGetValue(peer, out int id) ? id : -1;

        public bool Rebind(int from, int to)
        {
            if (from == to || _byId.ContainsKey(to) || !_byId.TryGetValue(from, out NetPeer peer))
                return false;
            Remove(from);
            Set(to, peer);
            return true;
        }

        public bool Remove(int playerId)
        {
            if (!_byId.TryGetValue(playerId, out NetPeer peer))
                return false;
            _byId.Remove(playerId);
            if (peer != null)
                _byPeer.Remove(peer);
            _ids.Remove(playerId);
            return true;
        }

        public void Clear()
        {
            _byId.Clear();
            _byPeer.Clear();
            _ids.Clear();
        }

        public string RosterAddress(int playerId)
        {
            if (!_byId.TryGetValue(playerId, out NetPeer peer) || peer == null)
                return null;
            IPAddress ip = peer.Address;
            if (ip == null)
                return null;
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            string addr = ip.ToString();
            return string.IsNullOrEmpty(addr) || addr == "0.0.0.0" ? null : addr;
        }

        public bool Send(int playerId, byte[] data, int length, DeliveryMethod method)
        {
            if (!_byId.TryGetValue(playerId, out NetPeer peer) || peer == null)
                return false;
            return SendTo(peer, playerId, data, length, method);
        }

        /// <summary>LiteNetLib only fragments the two reliable unsequenced methods; the rest must fit one datagram.</summary>
        private static bool CanFragment(DeliveryMethod method)
            => method == DeliveryMethod.ReliableOrdered || method == DeliveryMethod.ReliableUnordered;

        /// <summary>Largest single datagram <paramref name="playerId"/> takes for <paramref name="method"/>, or 0 if unknown.</summary>
        internal int MaxSinglePacketSize(int playerId, DeliveryMethod method)
            => _byId.TryGetValue(playerId, out NetPeer peer) && peer != null ? peer.GetMaxSinglePacketSize(method) : 0;

        /// <summary>
        /// One peer send that can never throw out of the frame. A packet too big for a single datagram
        /// on an unfragmentable method (Unreliable / Sequenced) is promoted to ReliableOrdered, which
        /// LiteNetLib fragments; hot streams are chunked below the limit by their senders, so this is
        /// the backstop for everything else.
        /// </summary>
        private static bool SendTo(NetPeer peer, int playerId, byte[] data, int length, DeliveryMethod method)
        {
            if (!CanFragment(method))
            {
                int max = peer.GetMaxSinglePacketSize(method);
                if (length > max)
                {
                    if (NetLogThrottle.ShouldLog("send-promote:" + (int)(data[0]), 5f, out int dropped))
                        ModLog.Warn(LogCat.Network,
                            "Send " + (NetMessageType)data[0] + " " + length + "B exceeds " + method
                            + " limit " + max + "B to p" + playerId + " — sent ReliableOrdered instead"
                            + NetLogThrottle.SuppressedSuffix(dropped));
                    method = DeliveryMethod.ReliableOrdered;
                }
            }

            try
            {
                peer.Send(data, 0, length, method);
                return true;
            }
            catch (TooBigPacketException ex)
            {
                // Must never escape: this runs inside Update/PollEvents and would abort the frame
                // for every peer. Rate-limited because the same oversize send repeats every tick.
                if (NetLogThrottle.ShouldLog("send-toobig:" + (int)(data[0]), 5f, out int dropped))
                    ModLog.Error(LogCat.Network,
                        "Send " + (NetMessageType)data[0] + " " + length + "B to p" + playerId
                        + " rejected by LiteNetLib (" + method + "): " + ex.Message
                        + NetLogThrottle.SuppressedSuffix(dropped));
            }
            catch (Exception ex)
            {
                // Same guarantee for anything else the socket layer throws (peer torn down between
                // the enumeration and the send, socket closed): one peer's failure stays its own.
                if (NetLogThrottle.ShouldLog("send-fail:" + (int)(data[0]), 5f, out int dropped))
                    ModLog.Error(LogCat.Network,
                        "Send " + (NetMessageType)data[0] + " " + length + "B to p" + playerId
                        + " failed (" + method + "): " + ex.GetType().Name + ": " + ex.Message
                        + NetLogThrottle.SuppressedSuffix(dropped));
            }
            return false;
        }
    }

    /// <summary>
    /// Steam SNS peers by SteamID, plus the host-password gate: a lobby member that has not proved the
    /// host password is known (it can finish the handshake) but not routable.
    /// </summary>
    internal sealed class SteamPeerTable : IPeerTable
    {
        private readonly Func<SteamCoopTransport> _transport;
        private readonly Func<bool> _passwordGate;
        private readonly Dictionary<int, CSteamID> _byId = new Dictionary<int, CSteamID>();
        private readonly Dictionary<ulong, int> _bySteamId = new Dictionary<ulong, int>();
        private readonly List<int> _ids = new List<int>(8);
        /// <summary>Host: SteamIDs whose Handshake carried the right host password.</summary>
        private readonly HashSet<ulong> _passwordOk = new HashSet<ulong>();
        /// <summary>Host: peers accepted but not yet password-verified → accept time.</summary>
        private readonly Dictionary<int, float> _unauthSince = new Dictionary<int, float>();

        /// <param name="transport">The Steam transport (created on first use).</param>
        /// <param name="passwordGate">True while unverified peers must not receive session traffic.</param>
        internal SteamPeerTable(Func<SteamCoopTransport> transport, Func<bool> passwordGate)
        {
            _transport = transport;
            _passwordGate = passwordGate;
        }

        public int Count => _byId.Count;
        public IReadOnlyList<int> Ids => _ids;
        public bool Contains(int playerId) => _byId.ContainsKey(playerId);

        public bool IsRoutable(int playerId)
            => _byId.TryGetValue(playerId, out CSteamID sid)
                && (!_passwordGate() || _passwordOk.Contains(sid.m_SteamID));

        internal void Set(int playerId, CSteamID sid)
        {
            if (_bySteamId.TryGetValue(sid.m_SteamID, out int oldId) && oldId != playerId)
                Remove(oldId);
            if (_byId.TryGetValue(playerId, out CSteamID oldSid))
                _bySteamId.Remove(oldSid.m_SteamID);
            else
                _ids.Add(playerId);
            _byId[playerId] = sid;
            _bySteamId[sid.m_SteamID] = playerId;
        }

        internal bool TryGetSteamId(int playerId, out CSteamID sid) => _byId.TryGetValue(playerId, out sid);

        internal bool TryGetPlayerId(ulong steamId, out int playerId) => _bySteamId.TryGetValue(steamId, out playerId);

        internal bool IsKnown(ulong steamId) => _bySteamId.ContainsKey(steamId);

        internal void MarkPasswordOk(ulong steamId) => _passwordOk.Add(steamId);

        internal bool IsPasswordOk(ulong steamId) => _passwordOk.Contains(steamId);

        internal void MarkUnauthenticated(int playerId, float now) => _unauthSince[playerId] = now;

        /// <summary>The peer proved the password. True if it was waiting on that.</summary>
        internal bool ClearUnauthenticated(int playerId) => _unauthSince.Remove(playerId);

        /// <summary>Peers still unverified <paramref name="graceSec"/> after they were accepted.</summary>
        internal List<int> CollectExpiredUnauthenticated(float now, float graceSec)
        {
            List<int> expired = null;
            foreach (var kvp in _unauthSince)
            {
                if (now - kvp.Value < graceSec)
                    continue;
                (expired ?? (expired = new List<int>())).Add(kvp.Key);
            }
            return expired;
        }

        internal bool HasUnauthenticated => _unauthSince.Count > 0;

        public bool Rebind(int from, int to)
        {
            if (from == to || _byId.ContainsKey(to) || !_byId.TryGetValue(from, out CSteamID sid))
                return false;
            bool unauth = _unauthSince.TryGetValue(from, out float since);
            Remove(from, keepPassword: true);
            Set(to, sid);
            if (unauth)
                _unauthSince[to] = since;
            return true;
        }

        public bool Remove(int playerId) => Remove(playerId, keepPassword: false);

        private bool Remove(int playerId, bool keepPassword)
        {
            if (!_byId.TryGetValue(playerId, out CSteamID sid))
                return false;
            _byId.Remove(playerId);
            _bySteamId.Remove(sid.m_SteamID);
            _ids.Remove(playerId);
            _unauthSince.Remove(playerId);
            if (!keepPassword)
                _passwordOk.Remove(sid.m_SteamID);
            return true;
        }

        public void Clear()
        {
            _byId.Clear();
            _bySteamId.Clear();
            _ids.Clear();
            _passwordOk.Clear();
            _unauthSince.Clear();
        }

        public string RosterAddress(int playerId)
            => _byId.TryGetValue(playerId, out CSteamID sid) && sid.IsValid() ? sid.m_SteamID.ToString() : null;

        public bool Send(int playerId, byte[] data, int length, DeliveryMethod method)
            => _byId.TryGetValue(playerId, out CSteamID sid) && _transport().Send(sid, data, length, method);
    }
}
