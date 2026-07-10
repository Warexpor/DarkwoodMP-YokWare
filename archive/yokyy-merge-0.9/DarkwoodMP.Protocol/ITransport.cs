namespace DarkwoodMP.Packets;

/// <summary>
/// Ironbark v2 transport abstraction. Default impl: hop reliability on LiteNetLib 1.3.5 Utils.
/// NetManager backend optional if metrics fail (not required for v2).
/// </summary>
public enum TransportDelivery : byte
{
    Unreliable = 0,
    Reliable = 1,
    Critical = 2,
}

public interface ITransport
{
    bool IsConnected { get; }
    int LocalClientId { get; }
    void Broadcast(Packet packet, TransportDelivery delivery = TransportDelivery.Reliable);
    void SendToPlayer(int playerId, Packet packet, TransportDelivery delivery = TransportDelivery.Reliable);
}
