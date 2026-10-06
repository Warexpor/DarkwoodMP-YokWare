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
        private int GetPlayerId(NetPeer peer) => _lanPeers.IdOf(peer);

        public void OnPeerConnected(NetPeer peer)
        {
            int playerId;
            if (_role == NetworkRole.Host)
            {
                playerId = _nextPlayerId++;
                _lanPeers.Set(playerId, peer);
                // Keep _session.Link.HandshakeComplete set when additional peers join; that
                // froze PlayerState/drag traffic for every already-ready client.
                // Only block gameplay until the first peer completes handshake.
                if (_session.Link.Handshaked.Count == 0)
                    _session.Link.HandshakeComplete = false;
                StatusText = $"Player {playerId} connected";
                ModLog.Event(LogCat.Network, $"Player {playerId} connected (peers={_lanPeers.Count}, ready={_session.Link.Handshaked.Count})");
                CompleteHostPeerJoin(playerId);
            }
            else
            {
                _lanPeers.Set(1, peer); // Host is always player 1 for client
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
                    StatusText = $"Player {playerId} left ({_lanPeers.Count} remaining, ready={_session.Link.Handshaked.Count})";
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
                if (!_session.Link.HandshakeComplete && IsConnectFailureReason(disconnectInfo.Reason))
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
            _session.RemoteOutsideLocation.TryGetValue(playerId, out leftLoc);

            // Before the presence and key records below are cleared.
            Sync.QuestItemHandoff.Leaving leaving = Sync.QuestItemHandoff.Capture(this, playerId);
            Sync.NpcDialogueLock.HostReleaseAllForPlayer(this, playerId);
            Sync.DreamForestSpiritAggro.ClearIfOwner(playerId);
            Sync.PeerItemPresence.ClearPlayer(playerId);
            ClearStableClientKey(playerId);
            ClearStickyPlayerPayloads(playerId);

            if (removeLanSlot)
                _lanPeers.Remove(playerId);
            // Steam path already called RemovePeerSlot before this.

            _session.Link.Handshaked.Remove(playerId);
            _session.Link.Rejected.Remove(playerId);
            // A peer that rebinds to this id later counts from a low sequence again; the old
            // high-water marks would drop its unreliable packets until it overtook them.
            _session.Link.LastPlayerStateSequence.Remove(playerId);
            _session.Link.LastPhysicsStateSequence.Remove(playerId);
            _session.Link.LastReliablePhysicsStateSequence.Remove(playerId);
            bool wasLoadingOnly = _session.Link.LoadingWorld.Contains(playerId)
                && !_session.Link.CoopReconnect.Contains(playerId)
                && (!_session.Link.AwaitingLateJoinBulk.TryGetValue(playerId, out float seen) || seen <= 0f);
            bool expectedJoinDetach = _session.Link.LoadingWorld.Contains(playerId)
                && !_session.Link.CoopReconnect.Contains(playerId);

            _session.Link.AwaitingLateJoinBulk.Remove(playerId);
            _session.Link.PendingHeavyLateJoinBulk.Remove(playerId);
            _session.Link.LoadingWorld.Remove(playerId);
            _session.Link.CoopReconnect.Remove(playerId);
            if (_session.Link.Handshaked.Count == 0)
                _session.Link.HandshakeComplete = false;

            // Capture last known pos BEFORE RemovePlayer so LocationExit fan-out
            // carries a real world point (zeros confused living-exit teleport path).
            float lastX = 0f, lastY = 0f, lastZ = 0f;
            if (PlayerPositionManager.TryGetRemote(playerId, out UnityEngine.Vector3 lastPos, out _))
            {
                lastX = lastPos.x; lastY = lastPos.y; lastZ = lastPos.z;
            }

            WorldProxyLifecycleHandlers.DestroyRemoteProxy(playerId);
            DestroyRemoteFlareLight(playerId);
            DestroyRemoteItemLight(playerId);
            _remotePlayers.Remove(playerId);
            PlayerPositionManager.RemovePlayer(playerId);
            PlayerLightFxApplyHandlers?.ClearPendingPlayerLightsFor(playerId);
            PlayerFXHandlers?.ClearPendingAnimLibrary(playerId);
            Sync.FinalDreamsceneManager.OnRemoteDisconnected(playerId);

            // Roster FIRST so clients mark the peer gone, then LocationExit uses the
            // roster-miss path (DestroyRemoteProxy) instead of teleport/EnsureRemoteProxy.
            BroadcastPeerRoster();
            _peerRosterTimer = 0f;

            // Membership + leave-unoccupied + LocationExit fan-out to remaining peers.
            LocationEnterExitHandlers?.NotifyRemotePeerDisconnected(playerId, leftLoc, lastX, lastY, lastZ);
            _session.RemoteOutsideLocation.Remove(playerId);

            if (!expectedJoinDetach && !wasLoadingOnly)
            {
                Sync.QuestItemHandoff.HostPeerLeft(leaving, new UnityEngine.Vector3(lastX, lastY, lastZ));
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
