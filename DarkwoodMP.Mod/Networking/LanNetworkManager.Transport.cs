using System;
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
        private static byte[] _hotPacketBuf = Array.Empty<byte>();

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
            if (length <= 0) return;
            if (_role == NetworkRole.Host)
            {
                if (PeerCount == 0) return;
                foreach (int peerId in EnumeratePeerIds())
                {
                    if (excludePlayerId > 0 && peerId == excludePlayerId)
                        continue;
                    if (skipLoadingPeers && _peersLoadingWorld.Contains(peerId))
                        continue;
                    SendRawToPlayer(peerId, data, length, method);
                }
            }
            else
            {
                foreach (int peerId in EnumeratePeerIds())
                {
                    if (excludePlayerId > 0 && peerId == excludePlayerId)
                        continue;
                    SendRawToPlayer(peerId, data, length, method);
                    return;
                }
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
            if (IsSteamSession)
            {
                SendSteamToPlayer(playerId, data, length, method);
                return;
            }
            if (!_peers.TryGetValue(playerId, out NetPeer peer))
                return;
            peer.Send(data, 0, length, method);
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
            foreach (int peerId in EnumeratePeerIds())
            {
                if (!IsPeerReadyForGameplay(peerId))
                    continue;
                SendRawToPlayer(peerId, data, length, method);
            }
        }

        /// <summary>Send a message to all connected peers.</summary>
        /// <param name="skipLoadingPeers">
        /// When true, skip peers in <see cref="_peersLoadingWorld"/> (title join / LoadScene).
        /// World share must pass false (default) so targeted broadcast resends still land.
        /// </param>
        public void SendToAll(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (PeerCount == 0) return;
            byte[] data = null;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (skipLoadingPeers && _peersLoadingWorld.Contains(peerId))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                SendRawToPlayer(peerId, data, method);
            }
        }

        /// <summary>Send a message to all peers except one.</summary>
        public void SendToAllExcept(int excludePlayerId, NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable, bool skipLoadingPeers = false)
        {
            if (PeerCount == 0) return;
            byte[] data = null;
            foreach (int peerId in EnumeratePeerIds())
            {
                if (peerId == excludePlayerId) continue;
                if (skipLoadingPeers && _peersLoadingWorld.Contains(peerId))
                    continue;
                if (data == null)
                    data = BuildPacket(type, writeBody);
                SendRawToPlayer(peerId, data, method);
            }
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
            if (_peersLoadingWorld.Add(playerId))
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
                    if (excludeCoopReconnect && _peersCoopReconnect.Contains(id))
                        continue;
                    MarkPeerLoadingWorld(id);
                }
            }
        }

        /// <summary>Host: peer reconnected with AlreadyInWorld (soft join pipeline phase 3).</summary>
        public bool IsCoopReconnectPeer(int playerId)
        {
            return playerId > 1 && _peersCoopReconnect.Contains(playerId);
        }

        /// <summary>Host: joiner sent its first in-world PlayerState.</summary>
        public void MarkPeerGameplayReady(int playerId)
        {
            if (_role != NetworkRole.Host || playerId <= 1)
                return;
            if (_peersLoadingWorld.Remove(playerId))
                ModLog.Event(LogCat.Session, "Peer " + playerId + " gameplay-ready (first PlayerState)");
        }

        /// <summary>Host: true if peer should receive high-rate gameplay packets.</summary>
        public bool IsPeerReadyForGameplay(int playerId)
        {
            return playerId > 0 && !_peersLoadingWorld.Contains(playerId);
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

        /// <summary>Legacy send to first connected peer (backward compat during migration).</summary>
        public void Send(NetMessageType type, Action<NetWriter> writeBody,
            DeliveryMethod method = DeliveryMethod.Unreliable)
        {
            foreach (int peerId in EnumeratePeerIds())
            {
                SendRawToPlayer(peerId, BuildPacket(type, writeBody), method);
                return; // Send to first peer only
            }
        }
    }
}
