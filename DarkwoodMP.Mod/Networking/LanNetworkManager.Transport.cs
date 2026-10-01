using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Packet build / Send* / Broadcast and peer loading-ready gates
    /// (split from main for ownership).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>
        /// Build a complete packet with a stack-local writer so nested Send/Broadcast
        /// from writeBody callbacks cannot corrupt a shared buffer.
        /// </summary>
        private static byte[] BuildPacket(NetMessageType type, Action<NetWriter> writeBody)
        {
            var writer = new NetWriter();
            writer.Put((byte)type);
            writeBody(writer);
            return writer.CopyData();
        }

        private static readonly NetWriter _hotPacketWriter = new NetWriter();
        private static byte[] _hotPacketBuf = Array.Empty<byte>(); // process-scoped: scratch buffer, cleared before each use

        /// <summary>
        /// Hot-path packet build into recycled buffers. <paramref name="writeBody"/> must not
        /// call Send/Broadcast (would re-enter the shared writer). PhysicsState serialize is safe.
        /// </summary>
        private static void BuildPacketHot(NetMessageType type, Action<NetWriter> writeBody,
            out byte[] data, out int length)
        {
            _hotPacketWriter.Reset();
            _hotPacketWriter.Put((byte)type);
            writeBody(_hotPacketWriter);
            _hotPacketWriter.CopyDataInto(ref _hotPacketBuf, out length);
            data = _hotPacketBuf;
        }

        /// <summary>
        /// PhysicsState / other pure-serialize hot broadcasts — recycled writer+buffer.
        /// When <paramref name="excludePlayerId"/> &gt; 0, that peer is skipped (host forward).
        /// </summary>
        public void BroadcastHot(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false,
            int excludePlayerId = 0)
        {
            BuildPacketHot(type, writeBody, out byte[] data, out int length);
            FanOutHot(data, length, method, skipLoadingPeers, excludePlayerId);
        }

        /// <summary>Send an already-framed hot buffer: host to every peer, client to the host only.</summary>
        private void FanOutHot(byte[] data, int length, DeliveryMethod method,
            bool skipLoadingPeers, int excludePlayerId)
        {
            if (length <= 0) return;
            if (_role == NetworkRole.Host)
                SendFramedToPeers(data, length, method,
                    skipLoadingPeers ? FanOutFilter.SkipLoading : FanOutFilter.All, excludePlayerId);
            else
                SendFramedToFirstPeer(data, length, method, excludePlayerId);
        }

        private enum FanOutFilter { All, SkipLoading, GameplayReady }

        private bool PassesFanOut(int peerId, FanOutFilter filter, int excludePlayerId)
        {
            if (excludePlayerId > 0 && peerId == excludePlayerId)
                return false;
            if (_session.Link.Rejected.Count > 0 && _session.Link.Rejected.Contains(peerId))
                return false;
            switch (filter)
            {
                case FanOutFilter.SkipLoading: return !_session.Link.LoadingWorld.Contains(peerId);
                case FanOutFilter.GameplayReady: return IsPeerReadyForGameplay(peerId);
                default: return true;
            }
        }

        /// <summary>
        /// The one fan-out loop behind every broadcast: walks the active peer table by index (no
        /// per-send allocation) with the same gates as EnumeratePeerIds.
        /// </summary>
        private void SendFramedToPeers(byte[] data, int length, DeliveryMethod method,
            FanOutFilter filter, int excludePlayerId)
        {
            if (data == null || length <= 0)
                return;
            IPeerTable peers = Peers;
            IReadOnlyList<int> ids = peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                int id = ids[i];
                if (!peers.IsRoutable(id) || !PassesFanOut(id, filter, excludePlayerId))
                    continue;
                peers.Send(id, data, length, method);
            }
        }

        /// <summary>Client: the only peer is the host.</summary>
        private void SendFramedToFirstPeer(byte[] data, int length, DeliveryMethod method, int excludePlayerId)
        {
            foreach (int peerId in EnumeratePeerIds())
            {
                if (excludePlayerId > 0 && peerId == excludePlayerId)
                    continue;
                SendRawToPlayer(peerId, data, length, method);
                return;
            }
        }

        /// <summary>Send a message to a specific peer by PlayerId (LAN or Steam).</summary>
        public void SendToPlayer(int playerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            byte[] data = BuildPacket(type, writeBody);
            SendRawToPlayer(playerId, data, method);
        }

        /// <summary>Raw bytes already framed with message type byte (entity snapshot path).</summary>
        public void SendRawToPlayer(int playerId, byte[] data, DeliveryMethod method = DeliveryMethod.Unreliable)
            => SendRawToPlayer(playerId, data, data != null ? data.Length : 0, method);

        /// <summary>Framed payload with explicit length (recycled send buffers).</summary>
        public void SendRawToPlayer(int playerId, byte[] data, int length,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (data == null || length <= 0)
                return;
            if (length > data.Length)
                length = data.Length;
            Peers.Send(playerId, data, length, method);
        }

        /// <summary>Steam SNS fragments internally, but a lost fragment drops the message — keep hot chunks near one packet.</summary>
        private const int SteamUnreliableChunkBytes = 1180;
        /// <summary>Fallback when no peer is registered yet (LiteNetLib default MTU 1024 minus header).</summary>
        private const int DefaultUnreliableChunkBytes = 1000;

        /// <summary>
        /// Largest framed unreliable packet every targeted peer can take in one datagram
        /// (hot snapshot senders split to this so nothing hits TooBigPacketException).
        /// </summary>
        internal int MinUnreliablePacketBytes(bool gameplayReadyOnly, bool skipLoadingPeers = false,
            int excludePlayerId = 0)
        {
            int min = int.MaxValue;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (excludePlayerId > 0 && peerId == excludePlayerId)
                    continue;
                if (gameplayReadyOnly && !IsPeerReadyForGameplay(peerId))
                    continue;
                if (skipLoadingPeers && _session.Link.LoadingWorld.Contains(peerId))
                    continue;
                int budget;
                if (IsSteamSession)
                    budget = SteamUnreliableChunkBytes;
                else
                {
                    budget = _lanPeers.MaxSinglePacketSize(peerId, DeliveryMethod.Unreliable);
                    if (budget <= 0)
                        budget = DefaultUnreliableChunkBytes;
                }
                if (budget < min)
                    min = budget;
            }
            return min == int.MaxValue ? DefaultUnreliableChunkBytes : min;
        }

        /// <summary>
        /// Entity broadcast hot path: send framed packet to gameplay-ready peers without
        /// allocating ConnectedPlayerIds list each 10 Hz tick (Steam-era peer abstraction).
        /// </summary>
        public void SendRawToReadyPeers(byte[] data, DeliveryMethod method = DeliveryMethod.Unreliable)
            => SendRawToReadyPeers(data, data != null ? data.Length : 0, method);

        public void SendRawToReadyPeers(byte[] data, int length,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (data == null || length <= 0 || PeerCount == 0)
                return;
            SendFramedToPeers(data, length, method, FanOutFilter.GameplayReady, 0);
        }

        /// <summary>Send a message to all connected peers.</summary>
        /// <param name="skipLoadingPeers">
        /// When true, skip peers in <see cref="_session.Link.LoadingWorld"/> (title join / LoadScene).
        /// World share must pass false (default) so targeted broadcast resends still land.
        /// </param>
        public void SendToAll(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
            => SendToAllExcept(0, type, writeBody, method, skipLoadingPeers);

        /// <summary>Send a message to all peers except one (0 = nobody excluded).</summary>
        public void SendToAllExcept(int excludePlayerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (PeerCount == 0) return;
            // Built before the loop: a body writer that itself sends must not run mid-iteration.
            byte[] data = BuildPacket(type, writeBody);
            SendFramedToPeers(data, data.Length, method,
                skipLoadingPeers ? FanOutFilter.SkipLoading : FanOutFilter.All, excludePlayerId);
        }

        /// <summary>
        /// Sends a message to all connected peers if host, or to the first peer if client.
        /// Use this in sender methods that can be called from both host and client roles.
        /// Host to clients: broadcast to all. Client to host: first peer only.
        /// </summary>
        /// <param name="skipLoadingPeers">Host only: skip joiners still loading the world package.</param>
        public void Broadcast(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (_role == NetworkRole.Host)
                SendToAll(type, writeBody, method, skipLoadingPeers);
            else
                Send(type, writeBody, method);
        }

        /// <summary>Host: joiner is downloading, applying, or loading a scene.</summary>
        public void MarkPeerLoadingWorld(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_session.Link.LoadingWorld.Add(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " marked loading-world (gameplay flood muted)");
        }

        /// <summary>Host: mark every non-host peer as loading (broadcast world resend).</summary>
        /// <param name="excludeCoopReconnect">Skip phase-3 soft reconnect peers (already in world).</param>
        public void MarkAllClientPeersLoadingWorld(bool excludeCoopReconnect = false)
        {
            if (_role != NetworkRole.Host)
                return;
            foreach (int id in EnumeratePeerIds())
            {
                if (id > 1)
                {
                    if (excludeCoopReconnect && _session.Link.CoopReconnect.Contains(id))
                        continue;
                    MarkPeerLoadingWorld(id);
                }
            }
        }

        /// <summary>Host: peer reconnected with AlreadyInWorld (soft join pipeline phase 3).</summary>
        public bool IsCoopReconnectPeer(int playerId)
        {
            return playerId > 1 && _session.Link.CoopReconnect.Contains(playerId);
        }

        /// <summary>Host: joiner sent its first in-world PlayerState.</summary>
        public void MarkPeerGameplayReady(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_session.Link.LoadingWorld.Remove(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " gameplay-ready (first PlayerState)");
        }

        /// <summary>Host: true if peer should receive high-rate gameplay packets.</summary>
        public bool IsPeerReadyForGameplay(int playerId)
        {
            return playerId > 0 && !_session.Link.LoadingWorld.Contains(playerId);
        }

        /// <summary>
        /// Client handshake flag: we already materialised + entered the host world offline.
        /// Prefer playable Player; also true mid-load if reconnect raced (legacy path).
        /// Host uses this to skip a second WorldSaveShare.
        /// </summary>
        internal static bool ClientReportsAlreadyInWorld()
        {
            try
            {
                // Title / cold rejoin MUST receive world share. loadedGame can linger
                // after quit-to-menu (vanilla rarely clears it) — never treat menu as
                // AlreadyInWorld or host skips share and bricks the session.
                if (Core.mainMenu)
                    return false;
                // Preferred: fully playable after offline load (phase 3 soft reconnect).
                if (Sync.ChapterSessionResume.IsLocalPlayableForCoopReconnect())
                    return true;
                // Phase-2 offline load in chapter (scene/load without menu).
                if (Core.loadingGame)
                    return true;
                if (Core.loadedGame && Core.currentProfile != null)
                    return true;
                if (Core.currentProfile != null)
                    return true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Client → host (a client's only peer). On the host this reaches one arbitrary client.</summary>
        public void Send(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (PeerCount == 0) return;
            byte[] data = BuildPacket(type, writeBody);
            SendFramedToFirstPeer(data, data.Length, method, 0);
        }
    }
}
