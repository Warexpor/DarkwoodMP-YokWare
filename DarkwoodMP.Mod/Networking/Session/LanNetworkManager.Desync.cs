using LiteNetLib;

namespace DWMPHorde.Networking
{
    /// <summary>Desync check transport glue (<see cref="Sync.DesyncCheck"/> owns the logic).</summary>
    public sealed partial class LanNetworkManager
    {
        /// <summary>
        /// Nothing in the link is mid-transfer: no world share, no migration, the handshake done.
        /// A check during one of those would compare a world that is still arriving.
        /// </summary>
        internal bool DesyncLinkQuiet =>
            IsConnected && _session.Link.HandshakeComplete && !_migrationInProgress
            && (_worldSaveShare == null || !_worldSaveShare.IsBusy);

        /// <summary>Host: this peer is in the world with its join bulk delivered.</summary>
        internal bool DesyncPeerSettled(int playerId)
        {
            LinkState l = _session.Link;
            return l.Handshaked.Contains(playerId) && !l.Rejected.Contains(playerId)
                && !l.LoadingWorld.Contains(playerId)
                && !l.AwaitingLateJoinBulk.ContainsKey(playerId)
                && !l.PendingHeavyLateJoinBulk.ContainsKey(playerId);
        }

        internal void SendDesyncDigest(int playerId, DesyncDigestMessage msg)
            => SendToPlayer(playerId, NetMessageType.DesyncDigest, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

        internal void SendDesyncDetail(int playerId, DesyncDetailMessage msg)
            => SendToPlayer(playerId, NetMessageType.DesyncDetail, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

        internal void SendDesyncDetailRequest(DesyncDetailRequestMessage msg)
            => Send(NetMessageType.DesyncDetailRequest, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

        internal void SendDesyncReport(DesyncReportMessage msg)
            => Send(NetMessageType.DesyncReport, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
    }
}
