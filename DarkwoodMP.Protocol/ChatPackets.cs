using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class ChatMessagePacket : Packet
{
    public override PacketType Type => PacketType.ChatMessage;
    public int SenderId { get; set; }
    public string SenderName { get; set; } = "Player";
    public string Message { get; set; } = "";
    public float Timestamp { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(SenderId);
        writer.Put(SenderName);
        writer.Put(Message);
        writer.Put(Timestamp);
    }

    public override void Deserialize(NetDataReader reader)
    {
        SenderId = reader.GetInt();
        SenderName = reader.GetString();
        Message = reader.GetString();
        Timestamp = reader.GetFloat();
    }
}

public class SystemMessagePacket : Packet
{
    public override PacketType Type => PacketType.SystemMessage;
    public string Message { get; set; } = "";
    public int SenderId { get; set; } = -1;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Message);
        writer.Put(SenderId);
    }

    public override void Deserialize(NetDataReader reader)
    {
        Message = reader.GetString();
        SenderId = reader.GetInt();
    }
}

public class ServerCommandPacket : Packet
{
    public override PacketType Type => PacketType.ServerCommand;
    public string Command { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Command);
        writer.Put(Args.Length);
        foreach (var arg in Args)
            writer.Put(arg);
    }

    public override void Deserialize(NetDataReader reader)
    {
        Command = reader.GetString();
        int count = reader.GetInt();
        Args = new string[count];
        for (int i = 0; i < count; i++)
            Args[i] = reader.GetString();
    }
}
