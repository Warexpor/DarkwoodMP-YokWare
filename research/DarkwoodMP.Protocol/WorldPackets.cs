using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class EnemyUpdatePacket : Packet
{
    public override PacketType Type => PacketType.EnemyUpdate;
    public string EnemyId { get; set; } = "";
    public string EnemyType { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rx { get; set; }
    public float Ry { get; set; }
    public float Rz { get; set; }
    public float Rw { get; set; }
    public string State { get; set; } = "";
    public float Health { get; set; }
    public bool IsAlive { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(EnemyId);
        writer.Put(EnemyType);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Rx);
        writer.Put(Ry);
        writer.Put(Rz);
        writer.Put(Rw);
        writer.Put(State);
        writer.Put(Health);
        writer.Put(IsAlive);
    }

    public override void Deserialize(NetDataReader reader)
    {
        EnemyId = reader.GetString();
        EnemyType = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Rx = reader.GetFloat(); Ry = reader.GetFloat(); Rz = reader.GetFloat(); Rw = reader.GetFloat();
        State = reader.GetString();
        Health = reader.GetFloat();
        IsAlive = reader.GetBool();
    }
}

public class DoorStatePacket : Packet
{
    public override PacketType Type => PacketType.DoorState;
    public string DoorId { get; set; } = "";
    public bool IsOpen { get; set; }
    public int PlayerId { get; set; }
    // Position of whoever opened/closed the door - Door.open() uses it to
    // decide which way the door swings, so it must match on all clients
    public float OpenerX { get; set; }
    public float OpenerY { get; set; }
    public float OpenerZ { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(DoorId);
        writer.Put(IsOpen);
        writer.Put(PlayerId);
        writer.Put(OpenerX);
        writer.Put(OpenerY);
        writer.Put(OpenerZ);
    }

    public override void Deserialize(NetDataReader reader)
    {
        DoorId = reader.GetString();
        IsOpen = reader.GetBool();
        PlayerId = reader.GetInt();
        OpenerX = reader.GetFloat();
        OpenerY = reader.GetFloat();
        OpenerZ = reader.GetFloat();
    }
}

public class PickupStatePacket : Packet
{
    public override PacketType Type => PacketType.PickupState;
    public string PickupId { get; set; } = "";
    public string ItemType { get; set; } = "";   // inventory type (for spawning drops)
    public string ItemName { get; set; } = "";   // GameObject name (for matching removals)
    public int Amount { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool Spawned { get; set; }            // true = item dropped, false = item picked up/destroyed

    // Per-instance item state so a dropped weapon keeps its wear/ammo/mods on
    // other players. Durability is absolute; < 0 = "no state" (throws, removals).
    public float Durability { get; set; } = -1f;
    public int Ammo { get; set; }
    public int ModifierQuality { get; set; }
    public string Modifiers { get; set; } = "";  // "type|strength|strengthKind|attach;..." (empty = none)

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PickupId);
        writer.Put(ItemType);
        writer.Put(ItemName);
        writer.Put(Amount);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Spawned);
        writer.Put(Durability);
        writer.Put(Ammo);
        writer.Put(ModifierQuality);
        writer.Put(Modifiers);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PickupId = reader.GetString();
        ItemType = reader.GetString();
        ItemName = reader.GetString();
        Amount = reader.GetInt();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Spawned = reader.GetBool();
        Durability = reader.GetFloat();
        Ammo = reader.GetInt();
        ModifierQuality = reader.GetInt();
        Modifiers = reader.GetString();
    }
}

public class EnvironmentEffectPacket : Packet
{
    public override PacketType Type => PacketType.EnvironmentEffect;
    public string EffectType { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Intensity { get; set; }
    public float Duration { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(EffectType);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Intensity);
        writer.Put(Duration);
    }

    public override void Deserialize(NetDataReader reader)
    {
        EffectType = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Intensity = reader.GetFloat();
        Duration = reader.GetFloat();
    }
}

public class DamageUpdatePacket : Packet
{
    public override PacketType Type => PacketType.DamageUpdate;
    public string ObjectId { get; set; } = "";
    public int PlayerId { get; set; }
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public bool IsDestroyed { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(ObjectId);
        writer.Put(PlayerId);
        writer.Put(Health);
        writer.Put(MaxHealth);
        writer.Put(IsDestroyed);
    }

    public override void Deserialize(NetDataReader reader)
    {
        ObjectId = reader.GetString();
        PlayerId = reader.GetInt();
        Health = reader.GetFloat();
        MaxHealth = reader.GetFloat();
        IsDestroyed = reader.GetBool();
    }
}

public class ObjectMovePacket : Packet
{
    public override PacketType Type => PacketType.ObjectMove;
    public string ObjectId { get; set; } = "";
    public int PlayerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rx { get; set; }
    public float Ry { get; set; }
    public float Rz { get; set; }
    public float Rw { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(ObjectId);
        writer.Put(PlayerId);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Rx);
        writer.Put(Ry);
        writer.Put(Rz);
        writer.Put(Rw);
    }

    public override void Deserialize(NetDataReader reader)
    {
        ObjectId = reader.GetString();
        PlayerId = reader.GetInt();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Rx = reader.GetFloat(); Ry = reader.GetFloat(); Rz = reader.GetFloat(); Rw = reader.GetFloat();
    }
}

public class InteractiveStatePacket : Packet
{
    public override PacketType Type => PacketType.InteractiveState;
    public string ObjectId { get; set; } = "";
    public string ObjectType { get; set; } = "";
    public bool IsActive { get; set; }
    public int PlayerId { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(ObjectId);
        writer.Put(ObjectType);
        writer.Put(IsActive);
        writer.Put(PlayerId);
    }

    public override void Deserialize(NetDataReader reader)
    {
        ObjectId = reader.GetString();
        ObjectType = reader.GetString();
        IsActive = reader.GetBool();
        PlayerId = reader.GetInt();
    }
}
