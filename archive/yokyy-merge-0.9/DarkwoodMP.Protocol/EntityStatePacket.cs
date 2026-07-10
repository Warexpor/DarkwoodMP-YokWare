using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>One entity's authoritative snapshot within an EntityStatePacket.</summary>
public struct EntitySnapshot
{
    public short Id;            // stable CharacterTracker id
    public float X, Y, Z;
    public float RotY;
    public string Clip;
    public short ClipFrame;     // -1 = none
    public bool Alive;
    public byte HealthPct;      // 0-100
    public string Name;         // entity name (fallback match / spawn)
    public string PrefabPath;   // "Characters/..." for spawning phantoms

    public void Serialize(NetDataWriter w)
    {
        w.Put(Id);
        w.Put(X); w.Put(Y); w.Put(Z);
        w.Put(RotY);
        w.Put(Clip ?? "");
        w.Put(ClipFrame);
        w.Put(Alive);
        w.Put(HealthPct);
        w.Put(Name ?? "");
        w.Put(PrefabPath ?? "");
    }

    public static EntitySnapshot Deserialize(NetDataReader r) => new EntitySnapshot
    {
        Id = r.GetShort(),
        X = r.GetFloat(), Y = r.GetFloat(), Z = r.GetFloat(),
        RotY = r.GetFloat(),
        Clip = r.GetString(),
        ClipFrame = r.GetShort(),
        Alive = r.GetBool(),
        HealthPct = r.GetByte(),
        Name = r.GetString(),
        PrefabPath = r.GetString()
    };
}

/// <summary>
/// Authority -&gt; others: a batch of entity snapshots (unreliable, ~10Hz). The
/// authority simulates all enemies; receivers mirror them by stable id.
/// </summary>
public class EntityStatePacket : Packet
{
    public override PacketType Type => PacketType.EntityState;
    public EntitySnapshot[] Entities { get; set; } = Array.Empty<EntitySnapshot>();

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Entities.Length);
        for (var i = 0; i < Entities.Length; i++)
            Entities[i].Serialize(writer);
    }

    public override void Deserialize(NetDataReader reader)
    {
        var count = reader.GetInt();
        if (count < 0 || count > 4096) count = 0;
        Entities = new EntitySnapshot[count];
        for (var i = 0; i < count; i++)
            Entities[i] = EntitySnapshot.Deserialize(reader);
    }
}
