using DWMPHorde;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Party map board and map discovery handlers.</summary>
    internal sealed class MapNetHandlers
    {
        private readonly LanNetworkManager _net;

        internal MapNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        /// <summary>
        /// Host: a client's change to the party map board. The sender is the socket's, never one
        /// the body claims; the host decides and answers everyone with MapPinEvent.
        /// </summary>
        internal void HandleMapPinRequest(MapPinRequestMessage msg)
        {
            if (_net.Role != NetworkRole.Host)
                return;
            int sender = _net.CurrentReceivePlayerId;
            if (sender <= 0 || sender == _net.LocalPlayerId)
                return;
            MapPinBoard.HostApply(_net, sender, msg);
        }

        /// <summary>Client: one change to the party map board from the host.</summary>
        internal void HandleMapPinEvent(MapPinEventMessage msg)
        {
            if (_net.Role != NetworkRole.Client)
                return;
            MapPinBoard.Apply(msg);
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
