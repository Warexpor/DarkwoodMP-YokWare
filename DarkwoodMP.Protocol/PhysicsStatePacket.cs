using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>
/// Ironbark v2 PhysicsState lane (minimal batch).
/// Free-body positions for movable/physics objects — absolute snapshots.
/// Reserved product depth: full rigidbody parity is a follow-up.
/// </summary>
public class PhysicsStateBatchPacket : Packet
{
    public override PacketType Type => PacketType.PhysicsStateBatch;
    public int PlayerId { get; set; }
    public int Count { get; set; }
    /// <summary>Packed: for each entry idHash:int, x,y,z,rx,ry,rz,rw floats.</summary>
    public byte[] Blob { get; set; } = System.Array.Empty<byte>();

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Count);
        writer.PutBytesWithLength(Blob ?? System.Array.Empty<byte>());
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Count = reader.GetInt();
        Blob = reader.GetBytesWithLength();
    }
}
