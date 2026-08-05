using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class GameStateSyncPacket : Packet
{
    public override PacketType Type => PacketType.GameStateSync;
    public int DayNumber { get; set; }
    public bool IsNight { get; set; }
    public float TimeOfDay { get; set; }
    public float GameTime { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(DayNumber);
        writer.Put(IsNight);
        writer.Put(TimeOfDay);
        writer.Put(GameTime);
    }

    public override void Deserialize(NetDataReader reader)
    {
        DayNumber = reader.GetInt();
        IsNight = reader.GetBool();
        TimeOfDay = reader.GetFloat();
        GameTime = reader.GetFloat();
    }
}

public class DayNightUpdatePacket : Packet
{
    public override PacketType Type => PacketType.DayNightUpdate;
    public bool IsNight { get; set; }
    public int DayNumber { get; set; }
    public float TimeRemaining { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(IsNight);
        writer.Put(DayNumber);
        writer.Put(TimeRemaining);
    }

    public override void Deserialize(NetDataReader reader)
    {
        IsNight = reader.GetBool();
        DayNumber = reader.GetInt();
        TimeRemaining = reader.GetFloat();
    }
}

public class EventTriggerPacket : Packet
{
    public override PacketType Type => PacketType.EventTrigger;
    public string EventName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Duration { get; set; }
    public int Severity { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(EventName);
        writer.Put(X);
        writer.Put(Y);
        writer.Put(Z);
        writer.Put(Duration);
        writer.Put(Severity);
    }

    public override void Deserialize(NetDataReader reader)
    {
        EventName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Duration = reader.GetFloat();
        Severity = reader.GetInt();
    }
}
