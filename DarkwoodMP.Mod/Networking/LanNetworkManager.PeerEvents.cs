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

        private static readonly Dictionary<NetMessageType, ForwardableKind> _forwardableMap = BuildForwardableMap(); // process-scoped: built once from message attributes

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

        private static readonly HashSet<NetMessageType> _hostOnlyTypes = BuildHostOnlySet(); // process-scoped: constant type set

        private static HashSet<NetMessageType> BuildHostOnlySet()
        {
            var set = new HashSet<NetMessageType>();
            foreach (var field in typeof(NetMessageType).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                if (System.Attribute.GetCustomAttribute(field, typeof(HostOnlyAttribute), false) != null)
                    set.Add((NetMessageType)field.GetValue(null));
            }
            return set;
        }

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

            // N-peer: clear claims locally AND fan-out DragSync STOP so remaining peers
            // do not keep a stuck "already being moved" claim after the leaver drops.
            if (playerId > 0)
                PlayerInteractHandlers?.ReleaseDragClaimsForDisconnectedPlayer(
                    playerId, broadcastStop: _role == NetworkRole.Host);

            if (_role == NetworkRole.Host)
            {
                if (playerId > 0)
                {
                    OnHostPeerDisconnectedGameplay(playerId, removeLanSlot: true, reasonTag: "peer disconnect");
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
                // A running migration reconnect retries from TickHostMigrationRetry.
                if (_migrationInProgress)
                    return;
                // Connect-failure reasons only mean "never got in" before the handshake; after it
                // (e.g. PeerNotFound on a host that crashed) the loss is real and migration applies.
                if (!_handshakeComplete && IsConnectFailureReason(disconnectInfo.Reason))
                {
                    OnClientLinkFailed(disconnectInfo.Reason.ToString());
                    return;
                }
                TryBeginHostMigration(disconnectInfo.Reason.ToString());
            }
        }


        /// <summary>
        /// Host-only: shared LAN/Steam disconnect cleanup. Captures outside-location
        /// membership, destroys local proxy, pushes PeerRoster then LocationExit so
        /// remaining peers prune ghosts (N-peer safe).
        /// </summary>
        private void OnHostPeerDisconnectedGameplay(int playerId, bool removeLanSlot, string reasonTag)
        {
            // Capture before Remove so TryLeaveUnoccupied sees remaining occupants only.
            string leftLoc = null;
            _remoteOutsideLocation.TryGetValue(playerId, out leftLoc);

            Sync.NpcDialogueLock.HostReleaseAllForPlayer(this, playerId);
            Sync.DreamForestSpiritAggro.ClearIfOwner(playerId);
            Sync.PeerItemPresence.ClearPlayer(playerId);
            ClearStableClientKey(playerId);
            ClearStickyPlayerPayloads(playerId);

            if (removeLanSlot)
                _peers.Remove(playerId);
            // Steam path already called RemovePeerSlot before this.

            _handshakedPeers.Remove(playerId);
            _rejectedPeers.Remove(playerId);
            // A peer that rebinds to this id later counts from a low sequence again; the old
            // high-water marks would drop its unreliable packets until it overtook them.
            _lastPlayerStateSequence.Remove(playerId);
            _lastPhysicsStateSequence.Remove(playerId);
            _lastReliablePhysicsStateSequence.Remove(playerId);
            bool wasLoadingOnly = _peersLoadingWorld.Contains(playerId)
                && !_peersCoopReconnect.Contains(playerId)
                && (!_awaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
            bool expectedJoinDetach = _peersLoadingWorld.Contains(playerId)
                && !_peersCoopReconnect.Contains(playerId);

            _awaitingLateJoinBulk.Remove(playerId);
            _pendingHeavyLateJoinBulk.Remove(playerId);
            _peersLoadingWorld.Remove(playerId);
            _peersCoopReconnect.Remove(playerId);
            if (_handshakedPeers.Count == 0)
                _handshakeComplete = false;

            // Capture last known pos BEFORE RemovePlayer so LocationExit fan-out
            // carries a real world point (zeros confused living-exit teleport path).
            float lastX = 0f, lastY = 0f, lastZ = 0f;
            if (PlayerPositionManager.TryGetRemote(playerId, out UnityEngine.Vector3 lastPos, out _))
            {
                lastX = lastPos.x; lastY = lastPos.y; lastZ = lastPos.z;
            }

            WorldProxyHandlers.DestroyRemoteProxy(playerId);
            DestroyRemoteFlareLight(playerId);
            DestroyRemoteItemLight(playerId);
            _remotePlayers.Remove(playerId);
            PlayerPositionManager.RemovePlayer(playerId);
            PlayerLightFxHandlers?.ClearPendingPlayerLightsFor(playerId);
            PlayerFXHandlers?.ClearPendingAnimLibrary(playerId);
            Sync.FinalDreamsceneManager.OnRemoteDisconnected(playerId);

            // Roster FIRST so clients mark the peer gone, then LocationExit uses the
            // roster-miss path (DestroyRemoteProxy) instead of teleport/EnsureRemoteProxy.
            BroadcastPeerRoster();
            _peerRosterTimer = 0f;

            // Membership + leave-unoccupied + LocationExit fan-out to remaining peers.
            LocationHandlers?.NotifyRemotePeerDisconnected(playerId, leftLoc, lastX, lastY, lastZ);
            _remoteOutsideLocation.Remove(playerId);

            if (!expectedJoinDetach && !wasLoadingOnly)
            {
                if (DeathStateTracker.OnRemoteDisconnected(playerId))
                    DeathStateTracker.TryResolveNightMorning(reasonTag);
                Patches.MorningHideoutHold.Forget(playerId);
                Patches.MorningHideoutHold.TryEndIfHideoutEmpty();
            }
            else
            {
                // A joiner that detached before entering still took a night-participant id.
                DeathStateTracker.OnRemotePeerGone(playerId);
                ModLog.Event(LogCat.Session,
                    "Peer " + playerId + " detached during join pipeline (expected — offline load or pre-ready)");
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
            ProcessInboundMessage(type, payload, deliveryMethod);
        }
    }
}
