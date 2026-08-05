using System;
using LiteNetLib.Utils;

namespace DarkwoodMP.Packets;

/// <summary>
/// Absolute trader stock (Ironbark wave B). Replaces ActionEvent "tradeinv:".
/// </summary>
public class TradeInventoryPacket : Packet
{
    public override PacketType Type => PacketType.TradeInventory;
    public int PlayerId { get; set; }
    public string NpcName { get; set; } = "";
    /// <summary>type,qty;type,qty;…</summary>
    public string StockCsv { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(NpcName ?? "");
        writer.Put(StockCsv ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        NpcName = reader.GetString();
        StockCsv = reader.GetString();
    }
}

/// <summary>
/// Client progression backup chunk (Ironbark wave B). Replaces "cbackupch:".
/// </summary>
public class ClientStateBackupChunkPacket : Packet
{
    public override PacketType Type => PacketType.ClientStateBackupChunk;
    public int PlayerId { get; set; }
    public int Index { get; set; }
    public int Total { get; set; }
    /// <summary>Base64 fragment of JSON (chunked for MTU).</summary>
    public string Part { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Index);
        writer.Put(Total);
        writer.Put(Part ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Index = reader.GetInt();
        Total = reader.GetInt();
        Part = reader.GetString();
    }
}

/// <summary>
/// Authority save beat (Ironbark wave A). Replaces ActionEvent "savebeat".
/// </summary>
public class SaveBeatPacket : Packet
{
    public override PacketType Type => PacketType.SaveBeat;
    public int PlayerId { get; set; }
    public long UtcTicks { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(UtcTicks);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        UtcTicks = reader.GetLong();
    }
}

// --- Ironbark wave C ---

/// <summary>Player entered an outside location (basement/bunker). Replaces "locenter:".</summary>
public class LocationEnterPacket : Packet
{
    public override PacketType Type => PacketType.LocationEnter;
    public int PlayerId { get; set; }
    /// <summary>Whose location state this is (usually same as sender).</summary>
    public int TargetPlayerId { get; set; }
    public string LocName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(TargetPlayerId);
        writer.Put(LocName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        TargetPlayerId = reader.GetInt();
        LocName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Player left an outside location. Replaces "locexit:".</summary>
public class LocationExitPacket : Packet
{
    public override PacketType Type => PacketType.LocationExit;
    public int PlayerId { get; set; }
    public int TargetPlayerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(TargetPlayerId);
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        TargetPlayerId = reader.GetInt();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Map marker place/remove. Replaces "mapmark:" / "mapunmark:".</summary>
public class MapMarkerPacket : Packet
{
    public override PacketType Type => PacketType.MapMarker;
    public int PlayerId { get; set; }
    public bool Remove { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Remove);
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Remove = reader.GetBool();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Map element discovered. Replaces "mapdisc:".</summary>
public class MapDiscoverPacket : Packet
{
    public override PacketType Type => PacketType.MapDiscover;
    public int PlayerId { get; set; }
    public string ElementName { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ElementName ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ElementName = reader.GetString();
    }
}

/// <summary>Shared story NPC reputation. Replaces "rep:".</summary>
public class ReputationPacket : Packet
{
    public override PacketType Type => PacketType.Reputation;
    public int PlayerId { get; set; }
    public string NpcName { get; set; } = "";
    public int Value { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(NpcName ?? "");
        writer.Put(Value);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        NpcName = reader.GetString();
        Value = reader.GetInt();
    }
}

/// <summary>Join-bulk reputation snapshot. Replaces "repb:".</summary>
public class ReputationBulkPacket : Packet
{
    public override PacketType Type => PacketType.ReputationBulk;
    public int PlayerId { get; set; }
    /// <summary>name:value;name:value;…</summary>
    public string Payload { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Payload ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Payload = reader.GetString();
    }
}

/// <summary>Hideout oven / experience machine state. Replaces "hideout:".</summary>
public class HideoutStatePacket : Packet
{
    public override PacketType Type => PacketType.HideoutState;
    public int PlayerId { get; set; }
    public string ComponentId { get; set; } = "";
    public bool Enabled { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ComponentId ?? "");
        writer.Put(Enabled);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ComponentId = reader.GetString();
        Enabled = reader.GetBool();
    }
}

/// <summary>Journal entry / item / reference. Replaces journal: / journalitem: / journalref:.</summary>
public class JournalSyncPacket : Packet
{
    public const byte KindEntry = 0;
    public const byte KindItem = 1;
    public const byte KindRef = 2;

    public override PacketType Type => PacketType.JournalSync;
    public int PlayerId { get; set; }
    public byte Kind { get; set; }
    /// <summary>Entry/item type, or ref id.</summary>
    public string Payload { get; set; } = "";
    /// <summary>Ref class name when Kind=Ref; otherwise empty.</summary>
    public string RefClass { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Kind);
        writer.Put(Payload ?? "");
        writer.Put(RefClass ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Kind = reader.GetByte();
        Payload = reader.GetString();
        RefClass = reader.GetString();
    }
}

// --- Ironbark wave D ---

/// <summary>Auth prepares shared dream preset. Replaces "dream:id:preset".</summary>
public class DreamPreparePacket : Packet
{
    public override PacketType Type => PacketType.DreamPrepare;
    public int PlayerId { get; set; }
    public int DreamId { get; set; }
    public string PresetName { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(DreamId);
        writer.Put(PresetName ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        DreamId = reader.GetInt();
        PresetName = reader.GetString();
    }
}

/// <summary>Auth started dreaming. Replaces "dreamstart:preset:x,y,z".</summary>
public class DreamStartPacket : Packet
{
    public override PacketType Type => PacketType.DreamStart;
    public int PlayerId { get; set; }
    public string PresetName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(PresetName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        PresetName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Peer finished loading into dream. Replaces "dreamentered".</summary>
public class DreamEnteredPacket : Packet
{
    public override PacketType Type => PacketType.DreamEntered;
    public int PlayerId { get; set; }

    public override void Serialize(NetDataWriter writer) => writer.Put(PlayerId);
    public override void Deserialize(NetDataReader reader) => PlayerId = reader.GetInt();
}

/// <summary>Auth ended dream. Replaces "dreamend:preset".</summary>
public class DreamEndPacket : Packet
{
    public override PacketType Type => PacketType.DreamEnd;
    public int PlayerId { get; set; }
    public string PresetName { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(PresetName ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        PresetName = reader.GetString();
    }
}

/// <summary>Dream SFX at world position. Replaces "dreamaudio:". Unreliable.</summary>
public class DreamAudioPacket : Packet
{
    public override PacketType Type => PacketType.DreamAudio;
    public int PlayerId { get; set; }
    public string AudioId { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(AudioId ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        AudioId = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Door open during dream. Replaces "dreamdoor:".</summary>
public class DreamDoorPacket : Packet
{
    public override PacketType Type => PacketType.DreamDoor;
    public int PlayerId { get; set; }
    public string DoorName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(DoorName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        DoorName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Final dreamscene death. Replaces "fddeath:".</summary>
public class FinalDreamDeathPacket : Packet
{
    public override PacketType Type => PacketType.FinalDreamDeath;
    public int PlayerId { get; set; }

    public override void Serialize(NetDataWriter writer) => writer.Put(PlayerId);
    public override void Deserialize(NetDataReader reader) => PlayerId = reader.GetInt();
}

/// <summary>Cutscene begin/end. Replaces "cutscene:".</summary>
public class CutsceneSyncPacket : Packet
{
    public override PacketType Type => PacketType.CutsceneSync;
    public int PlayerId { get; set; }
    public bool Begin { get; set; }
    public string Name { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Begin);
        writer.Put(Name ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Begin = reader.GetBool();
        Name = reader.GetString();
    }
}

/// <summary>Auth coordinated chapter load. Replaces "chaptergen:". Critical.</summary>
public class ChapterTransitionPacket : Packet
{
    public override PacketType Type => PacketType.ChapterTransition;
    public int PlayerId { get; set; }
    public int ChapterId { get; set; }
    public bool LoadChapterSave { get; set; }
    public bool GenerateSave { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ChapterId);
        writer.Put(LoadChapterSave);
        writer.Put(GenerateSave);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ChapterId = reader.GetInt();
        LoadChapterSave = reader.GetBool();
        GenerateSave = reader.GetBool();
    }
}

/// <summary>Soft chapter notice (partner entered ch2). Replaces "chapter2".</summary>
public class ChapterNotifyPacket : Packet
{
    public override PacketType Type => PacketType.ChapterNotify;
    public int PlayerId { get; set; }
    public int ChapterId { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ChapterId);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ChapterId = reader.GetInt();
    }
}

/// <summary>Coordinated scene load (credits). Replaces "sceneload:". Critical.</summary>
public class SceneLoadPacket : Packet
{
    public override PacketType Type => PacketType.SceneLoad;
    public int PlayerId { get; set; }
    public string SceneName { get; set; } = "";
    public float DelaySeconds { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(SceneName ?? "");
        writer.Put(DelaySeconds);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        SceneName = reader.GetString();
        DelaySeconds = reader.GetFloat();
    }
}

/// <summary>Client → auth examine request. Replaces "examinereq:". Forward=None.</summary>
public class ExamineRequestPacket : Packet
{
    public override PacketType Type => PacketType.ExamineRequest;
    public int PlayerId { get; set; }
    public string ObjectName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ObjectName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ObjectName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Auth examine state fan-out. Replaces "examinest:".</summary>
public class ExamineStatePacket : Packet
{
    public override PacketType Type => PacketType.ExamineState;
    public int PlayerId { get; set; }
    public string ObjectName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool Examined { get; set; }
    public int DescriptionPool { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ObjectName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
        writer.Put(Examined);
        writer.Put(DescriptionPool);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ObjectName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Examined = reader.GetBool();
        DescriptionPool = reader.GetInt();
    }
}

// --- Ironbark wave E ---

/// <summary>Absolute container contents. Replaces "container:".</summary>
public class ContainerStatePacket : Packet
{
    public override PacketType Type => PacketType.ContainerState;
    public int PlayerId { get; set; }
    public string ContainerId { get; set; } = "";
    /// <summary>type,qty;type,qty;…</summary>
    public string PayloadCsv { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ContainerId ?? "");
        writer.Put(PayloadCsv ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ContainerId = reader.GetString();
        PayloadCsv = reader.GetString();
    }
}

/// <summary>Client → auth open-snapshot request. Replaces "containerreq:".</summary>
public class ContainerRequestPacket : Packet
{
    public override PacketType Type => PacketType.ContainerRequest;
    public int PlayerId { get; set; }
    public string ContainerId { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ContainerId ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ContainerId = reader.GetString();
    }
}

/// <summary>Death bag spawn with absolute loot. Replaces "deathdrop:".</summary>
public class DeathDropSpawnPacket : Packet
{
    public override PacketType Type => PacketType.DeathDropSpawn;
    public int PlayerId { get; set; }
    public string Prefab { get; set; } = "";
    public string Uid { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string PayloadCsv { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Prefab ?? "");
        writer.Put(Uid ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
        writer.Put(PayloadCsv ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Prefab = reader.GetString();
        Uid = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        PayloadCsv = reader.GetString();
    }
}

/// <summary>Door/window barricade. Replaces doorbar/doorunbar/winbar/winunbar.</summary>
public class BarricadeStatePacket : Packet
{
    public const byte KindDoor = 0;
    public const byte KindWindow = 1;

    public override PacketType Type => PacketType.BarricadeState;
    public int PlayerId { get; set; }
    public byte Kind { get; set; }
    public string ObjectId { get; set; } = "";
    public bool Barricaded { get; set; }
    public int Health { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Kind);
        writer.Put(ObjectId ?? "");
        writer.Put(Barricaded);
        writer.Put(Health);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Kind = reader.GetByte();
        ObjectId = reader.GetString();
        Barricaded = reader.GetBool();
        Health = reader.GetInt();
    }
}

/// <summary>Placed world build piece. Replaces "placed:".</summary>
public class BuildPlacedPacket : Packet
{
    public override PacketType Type => PacketType.BuildPlaced;
    public int PlayerId { get; set; }
    public string ItemType { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rx { get; set; }
    public float Ry { get; set; }
    public float Rz { get; set; }
    public float Rw { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ItemType ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
        writer.Put(Rx); writer.Put(Ry); writer.Put(Rz); writer.Put(Rw);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ItemType = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
        Rx = reader.GetFloat(); Ry = reader.GetFloat(); Rz = reader.GetFloat(); Rw = reader.GetFloat();
    }
}

/// <summary>Finished constructible option. Replaces "construct:".</summary>
public class BuildConstructPacket : Packet
{
    public override PacketType Type => PacketType.BuildConstruct;
    public int PlayerId { get; set; }
    public string ObjectId { get; set; } = "";
    public int Option { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ObjectId ?? "");
        writer.Put(Option);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ObjectId = reader.GetString();
        Option = reader.GetInt();
    }
}

/// <summary>Vault/jump collision gate. Replaces "vault:".</summary>
public class VaultStatePacket : Packet
{
    public override PacketType Type => PacketType.VaultState;
    public int PlayerId { get; set; }
    public bool Vaulting { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Vaulting);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Vaulting = reader.GetBool();
    }
}

/// <summary>Harvest/trap/barrel destroy. Replaces "wobjgone:".</summary>
public class WorldObjectGonePacket : Packet
{
    public override PacketType Type => PacketType.WorldObjectGone;
    public int PlayerId { get; set; }
    public string ObjectName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(ObjectName ?? "");
        writer.Put(X); writer.Put(Y); writer.Put(Z);
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        ObjectName = reader.GetString();
        X = reader.GetFloat(); Y = reader.GetFloat(); Z = reader.GetFloat();
    }
}

/// <summary>Exclusive interaction lock. Replaces "ilock:" / "iunlock:".</summary>
public class InteractionLockSyncPacket : Packet
{
    public override PacketType Type => PacketType.InteractionLockSync;
    public int PlayerId { get; set; }
    /// <summary>True = lock claim, false = unlock.</summary>
    public bool Locked { get; set; }
    /// <summary>Kind char: C container, N npc, W workbench, D drag.</summary>
    public byte KindChar { get; set; }
    public string ObjectId { get; set; } = "";

    public override void Serialize(NetDataWriter writer)
    {
        writer.Put(PlayerId);
        writer.Put(Locked);
        writer.Put(KindChar);
        writer.Put(ObjectId ?? "");
    }

    public override void Deserialize(NetDataReader reader)
    {
        PlayerId = reader.GetInt();
        Locked = reader.GetBool();
        KindChar = reader.GetByte();
        ObjectId = reader.GetString();
    }
}
