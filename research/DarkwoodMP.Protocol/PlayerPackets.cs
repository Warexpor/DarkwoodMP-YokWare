using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class PositionUpdatePacket : Packet
{
    public override PacketType Type => PacketType.PositionUpdate;
    public int PlayerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rx { get; set; }
    public float Ry { get; set; }
    public float Rz { get; set; }
    public float Rw { get; set; }
    // Darkwood's legs are a separate object with their own rotation
    // (movement direction, independent of the body/aim direction)
    public float LegsRx { get; set; }
    public float LegsRy { get; set; }
    public float LegsRz { get; set; }
    public float LegsRw { get; set; } = 1f;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Rx);
        writer.Put(Ry);
        writer.Put(Rz);
        writer.Put(Rw);
        writer.Put(LegsRx);
        writer.Put(LegsRy);
        writer.Put(LegsRz);
        writer.Put(LegsRw);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Rx = reader.GetFloat(); Ry = reader.GetFloat(); Rz = reader.GetFloat(); Rw = reader.GetFloat();
        LegsRx = reader.GetFloat(); LegsRy = reader.GetFloat(); LegsRz = reader.GetFloat(); LegsRw = reader.GetFloat();
    }
}

public class HealthUpdatePacket : Packet
{
    public override PacketType Type => PacketType.HealthUpdate;
    public int PlayerId { get; set; }     // who dealt/caused damage
    public string TargetEntityId { get; set; } = "";  // instance ID if target is an environmental object
    public float Health { get; set; }
    public bool IsDead { get; set; }

    // Field ORDER is the wire contract: PlayerId, TargetEntityId, Health,
    // IsDead. A historical server-side copy with a different order read the
    // string's length prefix out of the float bytes and threw on join.
    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(TargetEntityId);
        writer.Put(Health);
        writer.Put(IsDead);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        TargetEntityId = reader.GetString();
        Health = reader.GetFloat();
        IsDead = reader.GetBool();
    }
}

/// <summary>
/// Current animation clips of a player's torso and legs animators.
/// The torso clip encodes the held item and attack poses in Darkwood,
/// so syncing clips shows weapons, tools and swings on remote players.
/// </summary>
public class PlayerAnimPacket : Packet
{
    public override PacketType Type => PacketType.PlayerAnim;
    public int PlayerId { get; set; }
    public string Torso { get; set; } = "";
    public string Legs { get; set; } = "";
    // tk2d animation library names. Darkwood swaps the torso animator's
    // Library per held item, so this is what makes weapons visible remotely.
    public string TorsoLib { get; set; } = "";
    public string LegsLib { get; set; } = "";
    // The game pauses/resumes the legs animator instead of switching clips
    // (standing vs walking), so play state and speed must be synced too
    public bool TorsoPlaying { get; set; } = true;
    public bool LegsPlaying { get; set; } = true;
    public float TorsoFps { get; set; }
    public float LegsFps { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Torso);
        writer.Put(Legs);
        writer.Put(TorsoLib);
        writer.Put(LegsLib);
        writer.Put(TorsoPlaying);
        writer.Put(LegsPlaying);
        writer.Put(TorsoFps);
        writer.Put(LegsFps);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Torso = reader.GetString();
        Legs = reader.GetString();
        TorsoLib = reader.GetString();
        LegsLib = reader.GetString();
        TorsoPlaying = reader.GetBool();
        LegsPlaying = reader.GetBool();
        TorsoFps = reader.GetFloat();
        LegsFps = reader.GetFloat();
    }
}

public class InventoryUpdatePacket : Packet
{
    public override PacketType Type => PacketType.InventoryUpdate;
    public int PlayerId { get; set; }
    public string ItemId { get; set; } = "";
    public int Quantity { get; set; }
    public int Slot { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ItemId);
        writer.Put(Quantity);
        writer.Put(Slot);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ItemId = reader.GetString();
        Quantity = reader.GetInt();
        Slot = reader.GetInt();
    }
}
