using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>
/// Client/Server: "send me the current world." Sent by a client that connected
/// from the main menu and is waiting to download the host's/server's world.
/// Also used server-&gt;client (RequesterId -1) to ask a client to upload its
/// world to seed the server's cache.
/// </summary>
public class WorldRequestPacket : Packet
{
    public override PacketType Type => PacketType.WorldRequest;
    public int RequesterId { get; set; }

    public override void Serialize(NetDataWriter writer) => writer.Put(RequesterId);
    public override void Deserialize(NetDataReader reader) => RequesterId = reader.GetInt();
}

/// <summary>
/// Sender-&gt;downloader: announces an incoming world transfer - the profile
/// metadata plus a manifest of the files (name + size + chunk count) that will
/// follow as WorldChunk packets.
/// </summary>
public class WorldOfferPacket : Packet
{
    public override PacketType Type => PacketType.WorldOffer;
    public int TransferId { get; set; }

    // GameProfile metadata needed to load the transferred save
    public int Chapter { get; set; }
    public int Day { get; set; }
    public int Difficulty { get; set; }
    public int MajorVersion { get; set; }
    public int MinorVersion { get; set; }
    public int RCVersion { get; set; }

    public FileEntry[] Files { get; set; } = Array.Empty<FileEntry>();

    public struct FileEntry
    {
        public string Name;
        public int TotalSize;
        public int ChunkCount;
    }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(TransferId);
        writer.Put(Chapter);
        writer.Put(Day);
        writer.Put(Difficulty);
        writer.Put(MajorVersion);
        writer.Put(MinorVersion);
        writer.Put(RCVersion);
        writer.Put(Files.Length);
        foreach (var f in Files)
        {
            writer.Put(f.Name ?? "");
            writer.Put(f.TotalSize);
            writer.Put(f.ChunkCount);
        }
    }

    public override void Deserialize(NetDataReader reader)
    {
        TransferId = reader.GetInt();
        Chapter = reader.GetInt();
        Day = reader.GetInt();
        Difficulty = reader.GetInt();
        MajorVersion = reader.GetInt();
        MinorVersion = reader.GetInt();
        RCVersion = reader.GetInt();
        var count = reader.GetInt();
        if (count < 0 || count > 64) count = 0;
        Files = new FileEntry[count];
        for (var i = 0; i < count; i++)
            Files[i] = new FileEntry
            {
                Name = reader.GetString(),
                TotalSize = reader.GetInt(),
                ChunkCount = reader.GetInt()
            };
    }
}

/// <summary>Sender-&gt;downloader: one chunk of one file in a transfer.</summary>
public class WorldChunkPacket : Packet
{
    public override PacketType Type => PacketType.WorldChunk;
    public int TransferId { get; set; }
    public int FileIndex { get; set; }
    public int ChunkIndex { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(TransferId);
        writer.Put(FileIndex);
        writer.Put(ChunkIndex);
        writer.PutBytesWithLength(Data ?? Array.Empty<byte>());
    }

    public override void Deserialize(NetDataReader reader)
    {
        TransferId = reader.GetInt();
        FileIndex = reader.GetInt();
        ChunkIndex = reader.GetInt();
        Data = reader.GetBytesWithLength() ?? Array.Empty<byte>();
    }
}

/// <summary>Sender-&gt;downloader: all chunks sent. Terminal marker for a transfer.</summary>
public class WorldEndPacket : Packet
{
    public override PacketType Type => PacketType.WorldEnd;
    public int TransferId { get; set; }

    public override void Serialize(NetDataWriter writer) => writer.Put(TransferId);
    public override void Deserialize(NetDataReader reader) => TransferId = reader.GetInt();
}
