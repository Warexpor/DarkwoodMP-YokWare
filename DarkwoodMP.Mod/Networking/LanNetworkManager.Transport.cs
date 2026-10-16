using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking.Steam;
using DWMPHorde.Sync;
using LiteNetLib;
using Steamworks;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Packet build / Send* / Broadcast and peer loading-ready gates
    /// (split from main for ownership).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>
        /// Build a complete packet in a recycled writer of its own nesting depth, so a nested
        /// Send/Broadcast from a writeBody callback writes into the next one and cannot corrupt
        /// this. The bytes are sent from the writer itself (every peer send copies them) and the
        /// writer is given back with <see cref="ReturnPacket"/> once they are out.
        /// </summary>
        private static NetWriter BuildPacket(NetMessageType type, Action<NetWriter> writeBody)
        {
            List<NetWriter> writers = _packetWriters ?? (_packetWriters = new List<NetWriter>(4));
            if (_packetDepth == writers.Count)
                writers.Add(new NetWriter());
            NetWriter writer = writers[_packetDepth++];
            try
            {
                writer.Reset();
                writer.Put((byte)type);
                writeBody(writer);
            }
            catch
            {
                ReturnPacket(writer);
                throw;
            }
            return writer;
        }

        /// <summary>Gives the innermost <see cref="BuildPacket"/> writer back (a very large one is let go).</summary>
        private static void ReturnPacket(NetWriter writer)
        {
            if (_packetDepth <= 0)
                return;
            _packetDepth--;
            if (writer.Capacity > MaxKeptPacketBytes)
                _packetWriters[_packetDepth] = new NetWriter();
        }

        private const int MaxKeptPacketBytes = 64 * 1024;
        [ThreadStatic] private static List<NetWriter> _packetWriters; // process-scoped: per-thread writers by nesting depth
        [ThreadStatic] private static int _packetDepth; // process-scoped: BuildPacket nesting on this thread

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
            if (Sync.PersonalPads.BlocksSend(type, _role)) return;
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

        private enum FanOutFilter { All, SkipLoading, GameplayReady, InWorld }

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
                case FanOutFilter.InWorld: return !_session.Link.LoadingWorld.Contains(peerId) && IsPeerInWorld(peerId);
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
            if (data == null || length <= 0)
                return;
            // EnumeratePeerIds' gates, by index (no iterator per send).
            IPeerTable peers = Peers;
            IReadOnlyList<int> ids = peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                int peerId = ids[i];
                if (!peers.IsRoutable(peerId) || IsRejectedPeer(peerId))
                    continue;
                if (excludePlayerId > 0 && peerId == excludePlayerId)
                    continue;
                peers.Send(peerId, data, Math.Min(length, data.Length), method);
                return;
            }
        }

        private bool IsRejectedPeer(int peerId)
            => _session.Link.Rejected.Count > 0 && _session.Link.Rejected.Contains(peerId);

        /// <summary>Send a message to a specific peer by PlayerId (LAN or Steam).</summary>
        public void SendToPlayer(int playerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            if (Sync.PersonalPads.BlocksSend(type, _role)) return;
            NetWriter packet = BuildPacket(type, writeBody);
            try { SendRawToPlayer(playerId, packet.RawBuffer, packet.Length, method); }
            finally { ReturnPacket(packet); }
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
            IPeerTable peers = Peers;
            IReadOnlyList<int> ids = peers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                int peerId = ids[i];
                if (!peers.IsRoutable(peerId) || IsRejectedPeer(peerId))
                    continue;
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

        /// <summary>
        /// LiteNetLib queues unreliable sends for its logic thread, which wakes every
        /// <c>UpdateTime</c> (15 ms): a snapshot burst waited 0-15 ms on top of the network, a
        /// jitter the client's render delay had to absorb. Wake the thread to send now. Steam SNS
        /// unreliable sends already go out at once (NoNagle | NoDelay).
        /// </summary>
        public void FlushQueuedSends() => _net?.TriggerUpdate();

        /// <summary>How long a leaving player waits for its last reliable messages to go out.</summary>
        private const int LeaveFlushMs = 250;

        /// <summary>
        /// LAN, just before the connections are closed: LiteNetLib sends from its own thread
        /// and closing drops whatever is still queued, so a reliable message written in the same
        /// call never left. Wake the thread and wait until the
        /// queues are empty. Steam closes with linger and sends its queue itself.
        /// </summary>
        private void FlushReliableBeforeLeave()
        {
            if (_net == null || _role == NetworkRole.Offline || _backend != ConnectionBackend.Lan)
                return;
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (ReliableQueuedToPeers() > 0)
            {
                if (wait.ElapsedMilliseconds >= LeaveFlushMs)
                {
                    ModLog.Warn(LogCat.Network, "Leaving with reliable messages still queued after "
                        + LeaveFlushMs + " ms");
                    return;
                }
                _net.TriggerUpdate();
                System.Threading.Thread.Sleep(2);
            }
        }

        private int ReliableQueuedToPeers()
        {
            int queued = 0;
            IReadOnlyList<int> ids = _lanPeers.Ids;
            for (int i = 0; i < ids.Count; i++)
            {
                if (_lanPeers.TryGetPeer(ids[i], out NetPeer peer) && peer != null
                    && peer.ConnectionState == ConnectionState.Connected)
                    queued += peer.GetPacketsCountInReliableQueue(0, true) + peer.GetPacketsCountInReliableQueue(0, false);
            }
            return queued;
        }

        /// <summary>
        /// Reliable bytes queued to <paramref name="playerId"/> that are not on the wire yet, or -1
        /// when unknown. LAN counts LiteNetLib's queued packets at the peer's datagram size.
        /// </summary>
        internal long ReliableBacklogBytes(int playerId)
        {
            if (IsSteamSession)
            {
                SteamCoopTransport steam = _steam;
                return steam != null && _steamPeers != null && _steamPeers.TryGetSteamId(playerId, out CSteamID sid)
                    ? steam.PendingReliableBytes(sid) : -1;
            }
            if (!_lanPeers.TryGetPeer(playerId, out NetPeer peer) || peer == null
                || peer.ConnectionState != ConnectionState.Connected)
                return -1;
            long packets = peer.GetPacketsCountInReliableQueue(0, true) + peer.GetPacketsCountInReliableQueue(0, false);
            return packets * peer.GetMaxSinglePacketSize(DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Bytes a second <paramref name="playerId"/>'s link sends at now (Steam), or 0 where the link sets its own pace (LAN).</summary>
        internal int ReliableSendRate(int playerId)
        {
            SteamCoopTransport steam = _steam;
            return IsSteamSession && steam != null && _steamPeers != null && _steamPeers.TryGetSteamId(playerId, out CSteamID sid)
                ? steam.SendRate(sid) : 0;
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
            // The host's own prologue and a solo dream happen on private pads, not in the shared world.
            if (Sync.PersonalPads.BlocksSend(type, _role)) return;
            // Built before the loop: a body writer that itself sends must not run mid-iteration.
            NetWriter packet = BuildPacket(type, writeBody);
            try
            {
                SendFramedToPeers(packet.RawBuffer, packet.Length, method,
                    skipLoadingPeers ? FanOutFilter.SkipLoading : FanOutFilter.All, excludePlayerId);
            }
            finally { ReturnPacket(packet); }
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

        /// <summary>
        /// Host: send to the peers playing in the world (sent in-world PlayerState, not loading).
        /// A peer still on the title, waiting for the host's world, has nothing to apply world
        /// state to; the world package carries it.
        /// </summary>
        public void SendToPeersInWorld(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.ReliableOrdered)
        {
            if (_role != NetworkRole.Host || PeerCount == 0) return;
            if (Sync.PersonalPads.BlocksSend(type, _role)) return;
            NetWriter packet = BuildPacket(type, writeBody);
            try { SendFramedToPeers(packet.RawBuffer, packet.Length, method, FanOutFilter.InWorld, 0); }
            finally { ReturnPacket(packet); }
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
            {
                ModLog.Event(LogCat.Session, "Peer " + playerId + " gameplay-ready (first PlayerState)");
                Sync.PeerNotices.HostInGame(this, playerId);
            }
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
                if (GameScreen.AtTitle)
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
            if (Sync.PersonalPads.BlocksSend(type, _role)) return;
            NetWriter packet = BuildPacket(type, writeBody);
            try { SendFramedToFirstPeer(packet.RawBuffer, packet.Length, method, 0); }
            finally { ReturnPacket(packet); }
        }
    }
}
