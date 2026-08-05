using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class HeartbeatPacket : Packet
{
    public override PacketType Type => PacketType.Heartbeat;
    public long Timestamp { get; set; }
    public int PlayerCount { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Timestamp);
        writer.Put(PlayerCount);
    }

    public override void Deserialize(NetDataReader reader)
    {
        Timestamp = reader.GetLong();
        PlayerCount = reader.GetInt();
    }
}

public class HeartbeatAckPacket : Packet
{
    public override PacketType Type => PacketType.HeartbeatAck;
    public long ClientId { get; set; }
    public long Timestamp { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(ClientId);
        writer.Put(Timestamp);
    }

    public override void Deserialize(NetDataReader reader)
    {
        ClientId = reader.GetLong();
        Timestamp = reader.GetLong();
    }
}
