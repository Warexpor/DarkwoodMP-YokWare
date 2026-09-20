using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// LiteNetLib peer connect/disconnect/error/receive and forwardable map
    /// (split from main for ownership).
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        private enum ForwardableKind { None, Direct, Player }

        private static readonly Dictionary<NetMessageType, ForwardableKind> _forwardableMap = BuildForwardableMap();

        private static Dictionary<NetMessageType, ForwardableKind> BuildForwardableMap()
        {
            var map = new Dictionary<NetMessageType, ForwardableKind>();
            foreach (var field in typeof(NetMessageType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var value = (NetMessageType)field.GetValue(null);
                if (System.Attribute.GetCustomAttribute(field, typeof(ForwardableAttribute), false) != null)
                    map[value] = ForwardableKind.Direct;
                else if (System.Attribute.GetCustomAttribute(field, typeof(ForwardablePlayerAttribute), false) != null)
                    map[value] = ForwardableKind.Player;
            }
            return map;
        }

        /// <summary>True while the current OnNetworkReceive is forwarding a
        /// RemotePlayerForwardMessage's inner payload to prevent re-forwarding.</summary>
        private bool _isForwardedMessage;

        /// <summary>Get the PlayerId for a given NetPeer, or -1 if unknown.</summary>
        private int GetPlayerId(NetPeer peer)
        {
            foreach (var kvp in _peers)
            {
                if (kvp.Value == peer)
                    return kvp.Key;
            }
            return -1;
        }

        public void OnPeerConnected(NetPeer peer)
        {
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = _nextPlayerId++;
                _peers[playerId] = peer;
                // Keep _handshakeComplete set when additional peers join; that
                // froze PlayerState/drag traffic for every already-ready client.
                // Only block gameplay until the first peer completes handshake.
                if (_handshakedPeers.Count == 0)
                    _handshakeComplete = false;
                StatusText = $"Player {playerId} connected";
                ModLog.Event(LogCat.Network, $"Player {playerId} connected (peers={_peers.Count}, ready={_handshakedPeers.Count})");
                CompleteHostPeerJoin(playerId);
            }
            else
            {
                _peers[1] = peer; // Host is always player 1 for client
                StatusText = "Connected to host";
                ModLog.Event(LogCat.Network, "Connected to host");
                CompleteClientPeerJoin();
            }
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            int playerId = GetPlayerId(peer);
            ModLog.Event(LogCat.Network, $"Player {playerId} disconnected: " + disconnectInfo.Reason);

            // Clear drag claims from the disconnected player so their objects become free
            var toRemove = new List<string>();
            foreach (var kv in _dragClaims)
            {
                if (kv.Value == playerId)
                    toRemove.Add(kv.Key);
            }
            foreach (string key in toRemove)
                _dragClaims.Remove(key);

            // Clean up drag tracking for items this player was dragging
            foreach (string key in toRemove)
            {
                RemoveRemoteDragIds(key);
                ReleaseRemoteDragKinematic(key);
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(key);
            }

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    // Free workbench / craft locks held by the leaver.
                    Sync.WorkbenchOpenLock.HostReleaseAllForPlayer(this, playerId);
                    _peers.Remove(playerId);
                    _handshakedPeers.Remove(playerId);
                    bool wasLoadingOnly = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId)
                        && (!_awaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
                    // Phase-2 expected leave: client disconnects after share to load offline.
                    bool expectedJoinDetach = _peersLoadingWorld.Contains(playerId)
                        && !_peersCoopReconnect.Contains(playerId);

                    _awaitingLateJoinBulk.Remove(playerId); // Dictionary.Remove
                    _pendingHeavyLateJoinBulk.Remove(playerId);
                    _peersLoadingWorld.Remove(playerId);
                    _peersCoopReconnect.Remove(playerId);
                    if (_handshakedPeers.Count == 0)
                        _handshakeComplete = false;
                    WorldProxyHandlers.DestroyRemoteProxy(playerId);
                    DestroyRemoteFlareLight(playerId);
                    DestroyRemoteItemLight(playerId);
                    _remotePlayers.Remove(playerId);
                    PlayerPositionManager.RemovePlayer(playerId);
                    _remoteOutsideLocation.Remove(playerId);
                    Sync.FinalDreamsceneManager.OnRemoteDisconnected(playerId);
                    // Don't treat transfer-link teardown / pre-PlayerState leave as night death.
                    if (!expectedJoinDetach && !wasLoadingOnly)
                    {
                        if (DeathStateTracker.OnRemoteDisconnected(playerId))
                            DeathStateTracker.TryResolveNightMorning("peer disconnect");
                    }
                    else
                    {
                        ModLog.Event(LogCat.Session,
                            "Peer " + playerId + " detached during join pipeline (expected — offline load or pre-ready)");
                    }
                    StatusText = $"Player {playerId} left ({_peers.Count} remaining, ready={_handshakedPeers.Count})";
                }
            }
            else
            {
                // Client lost the only peer (host). Grant host to elect if mid-coop play.
                // Intentional StopNetwork / join offline-load sets _suppressHostMigration.
                if (_suppressHostMigration)
                {
                    // Already tearing or intentional; do not nest StopNetwork.
                    return;
                }
                TryBeginHostMigration(disconnectInfo.Reason.ToString());
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            ModLog.Error(LogCat.Network, "Network error: " + socketError);
            StatusText = "Error: " + socketError;
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            OnNetworkReceiveBody(peer, reader, channelNumber, deliveryMethod);
        }

        private void OnNetworkReceiveBody(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (!reader.TryGetByte(out byte messageType))
                return;

            var type = (NetMessageType)messageType;
            byte[] payload = reader.GetRemainingBytes();

            // Track which peer sent this message so handlers can look up PlayerId
            _currentReceivePeer = peer;
            _currentReceivePlayerId = GetPlayerId(peer);
            if (IsConnected)
                ClientPerfProbe.NotePacketRx(type);
            ProcessInboundMessage(type, payload);
        }
    }
}
