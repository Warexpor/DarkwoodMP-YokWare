using DWMPHorde;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Map marker / discovery handlers composed for 0.8.</summary>
    internal sealed class MapNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal MapNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// The host takes the socket-derived sender: the embedded id is client-written, so a peer
        /// could place or delete markers as someone else. A client trusts the host's relayed id
        /// (the host stamps the original sender into forwarded packets).
        /// </summary>
        private int ResolveSender(int embeddedPlayerId)
        {
            if (_net.Role == NetworkRole.Host)
                return _net.CurrentReceivePlayerId;
            return embeddedPlayerId > 0 ? embeddedPlayerId : _net.CurrentReceivePlayerId;
        }

        internal void HandleMapMarker(MapMarkerMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int playerId = ResolveSender(msg.PlayerId);
            if (playerId <= 0) return;
            if (playerId == _net.LocalPlayerId) return; // never treat own marker as remote
            MultiplayerMapManager.AddRemoteMarker(playerId, pos);
            ModRuntime.LegacyInfo($"[MapMarker] player {playerId} marker at {pos:F1}");
        }

        internal void HandleMapMarkerRemove(MapMarkerRemoveMessage msg)
        {
            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            int playerId = ResolveSender(msg.PlayerId);
            if (playerId <= 0) return;
            if (playerId == _net.LocalPlayerId) return;
            MultiplayerMapManager.RemoveRemoteMarker(playerId, pos);
            ModRuntime.LegacyInfo($"[MapMarker] player {playerId} marker removed at {pos:F1}");
        }

        /// <summary>
        /// Handles a MapElement discovery notification from the remote peer.
        /// Both host and client can discover locations, so this is bidirectional.
        /// </summary>
        internal void HandleMapElementDiscovered(MapElementDiscoveredMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ElementName)) return;
            MultiplayerMapManager.OnRemoteElementDiscovered(msg.ElementName,
                msg.HasPos ? new Vector3(msg.PosX, 0f, msg.PosZ) : (Vector3?)null);
        }
    }
}
