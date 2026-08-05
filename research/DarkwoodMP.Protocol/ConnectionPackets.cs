using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

public class ConnectRequestPacket : Packet
{
    public override PacketType Type => PacketType.ConnectRequest;
    public string Name { get; set; } = "Player";
    public string Password { get; set; } = "";
    /// <summary>Display / product string only — not the wire contract.</summary>
    public string Version { get; set; } = "0.9";
    /// <summary>Ironbark Protocol version (strict equality).</summary>
    public int IronbarkVersion { get; set; } = Ironbark.Version;
    /// <summary>Capability bitset (Ironbark v2).</summary>
    public uint Capabilities { get; set; } = Ironbark.Caps.Local;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Name);
        writer.Put(Password);
        writer.Put(Version);
        writer.Put(IronbarkVersion);
        writer.Put(Capabilities);
    }

    public override void Deserialize(NetDataReader reader)
    {
        Name = reader.GetString();
        Password = reader.GetString();
        Version = reader.GetString();
        // Missing field (pre-Ironbark peer) → 0 → hard reject
        IronbarkVersion = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
        Capabilities = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0u;
    }
}

public class ConnectResponsePacket : Packet
{
    public override PacketType Type => PacketType.ConnectResponse;
    public int ClientId { get; set; } = -1;
    public bool Accepted { get; set; }
    public string Message { get; set; } = "";
    /// <summary>Server/host Ironbark version (echo for logging).</summary>
    public int IronbarkVersion { get; set; } = Ironbark.Version;
    /// <summary>Host/server capability bitset (Ironbark v2).</summary>
    public uint Capabilities { get; set; } = Ironbark.Caps.Local;

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(ClientId);
        writer.Put(Accepted);
        writer.Put(Message);
        writer.Put(IronbarkVersion);
        writer.Put(Capabilities);
    }

    public override void Deserialize(NetDataReader reader)
    {
        ClientId = reader.GetInt();
        Accepted = reader.GetBool();
        Message = reader.GetString();
        IronbarkVersion = reader.AvailableBytes >= 4 ? reader.GetInt() : 0;
        Capabilities = reader.AvailableBytes >= 4 ? reader.GetUInt() : 0u;
    }
}

public class PlayerJoinedPacket : Packet
{
    public override PacketType Type => PacketType.PlayerJoined;
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "Player";
    public float SpawnX { get; set; }
    public float SpawnY { get; set; }
    public float SpawnZ { get; set; }
    public float SpawnRx { get; set; }
    public float SpawnRy { get; set; }
    public float SpawnRz { get; set; }
    public float SpawnRw { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(PlayerName);
        writer.Put(SpawnX);
        writer.Put(SpawnY);
        writer.Put(SpawnZ);
        writer.Put(SpawnRx);
        writer.Put(SpawnRy);
        writer.Put(SpawnRz);
        writer.Put(SpawnRw);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        PlayerName = reader.GetString();
        SpawnX = reader.GetFloat();
        SpawnY = reader.GetFloat();
        SpawnZ = reader.GetFloat();
        SpawnRx = reader.GetFloat();
        SpawnRy = reader.GetFloat();
        SpawnRz = reader.GetFloat();
        SpawnRw = reader.GetFloat();
    }
}

public class GameStateRequestPacket : Packet
{
    public override PacketType Type => PacketType.GameStateRequest;

    public override void Serialize(NetDataWriter writer) { }
    public override void Deserialize(NetDataReader reader) { }
}

public class PlayerLeftPacket : Packet
{
    public override PacketType Type => PacketType.PlayerLeft;
    public int PlayerId { get; set; }
    public string PlayerName { get; set; } = "Player";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(PlayerName);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        PlayerName = reader.GetString();
    }
}

public class PlayerListPacket : Packet
{
    public override PacketType Type => PacketType.PlayerList;
    public PlayerInfo[] Players { get; set; } = Array.Empty<PlayerInfo>();

    public class PlayerInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public PlayerInfo() { }
        public PlayerInfo(int id, string name) { Id = id; Name = name; }
    }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(Players.Length);
        foreach (var p in Players)
        {
            writer.Put(p.Id);
            writer.Put(p.Name);
        }
    }

    public override void Deserialize(NetDataReader reader)
    {
        int count = reader.GetInt();
        Players = new PlayerInfo[count];
        for (int i = 0; i < count; i++)
            Players[i] = new PlayerInfo(reader.GetInt(), reader.GetString());
    }
}
