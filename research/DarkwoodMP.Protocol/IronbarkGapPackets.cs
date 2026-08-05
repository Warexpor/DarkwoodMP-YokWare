using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>Gap-closure wave G — burn, explosion FX, reliable entity spawn.</summary>

/// <summary>Host/authority → peers: entity (Character) burn start/stop.</summary>
public class EntityBurningPacket : Packet
{
    public override PacketType Type => PacketType.EntityBurning;
    public int PlayerId { get; set; }
    public short EntityId { get; set; }
    public bool IsBurning { get; set; }
    public float BurnTime { get; set; }
    public float Modifier { get; set; }
    public float Interval { get; set; }

    public override void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(EntityId);
        w.Put(IsBurning);
        w.Put(BurnTime);
        w.Put(Modifier);
        w.Put(Interval);
    }

    public override void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        EntityId = r.GetShort();
        IsBurning = r.GetBool();
        BurnTime = r.GetFloat();
        Modifier = r.GetFloat();
        Interval = r.GetFloat();
    }
}

/// <summary>Player (or proxy) burn start/stop — visual on remote clone.</summary>
public class PlayerBurningPacket : Packet
{
    public override PacketType Type => PacketType.PlayerBurning;
    public int PlayerId { get; set; }
    public bool IsBurning { get; set; }
    public float BurnTime { get; set; }

    public override void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(IsBurning);
        w.Put(BurnTime);
    }

    public override void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        IsBurning = r.GetBool();
        BurnTime = r.GetFloat();
    }
}

/// <summary>Secondary objects spawned during Explodes (not the main explosion prefab).</summary>
public class ExplosionSpawnObjectPacket : Packet
{
    public override PacketType Type => PacketType.ExplosionSpawnObject;
    public int PlayerId { get; set; }
    public string PrefabName { get; set; } = "";
    public float X, Y, Z, Rx, Ry, Rz;

    public override void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(PrefabName ?? "");
        w.Put(X); w.Put(Y); w.Put(Z);
        w.Put(Rx); w.Put(Ry); w.Put(Rz);
    }

    public override void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        PrefabName = r.GetString();
        X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
        Rx = r.GetFloat(); Ry = r.GetFloat(); Rz = r.GetFloat();
    }
}

/// <summary>Reliable one-shot dynamic enemy spawn (replaces ActionEvent espawn).</summary>
public class EntitySpawnPacket : Packet
{
    public override PacketType Type => PacketType.EntitySpawn;
    public int PlayerId { get; set; }
    public short EntityId { get; set; }
    public string EntityType { get; set; } = "";
    public string PrefabPath { get; set; } = "";
    public float X, Y, Z, RotY;

    public override void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(EntityId);
        w.Put(EntityType ?? "");
        w.Put(PrefabPath ?? "");
        w.Put(X); w.Put(Y); w.Put(Z);
        w.Put(RotY);
    }

    public override void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        EntityId = r.GetShort();
        EntityType = r.GetString();
        PrefabPath = r.GetString();
        X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
        RotY = r.GetFloat();
    }
}

/// <summary>Gasoline/liquid fire extinguish at position.</summary>
public class LiquidStopBurnPacket : Packet
{
    public override PacketType Type => PacketType.LiquidStopBurn;
    public int PlayerId { get; set; }
    public float X, Y, Z;

    public override void Serialize(NetDataWriter w)
    {
        w.Put(PlayerId);
        w.Put(X); w.Put(Y); w.Put(Z);
    }

    public override void Deserialize(NetDataReader r)
    {
        PlayerId = r.GetInt();
        X = r.GetFloat(); Y = r.GetFloat(); Z = r.GetFloat();
    }
}
