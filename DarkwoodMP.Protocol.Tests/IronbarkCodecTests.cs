using DarkwoodMP.Packets;
using LiteNetLib.Utils;
using Xunit;

namespace DarkwoodMP.Protocol.Tests;

public class IronbarkCodecTests
{
    [Fact]
    public void Version_Is_Two()
    {
        Assert.Equal(2, Ironbark.Version);
    }

    [Fact]
    public void Handshake_Rejects_Wrong_Version()
    {
        Assert.NotEqual(1, Ironbark.Version);
        var req = new ConnectRequestPacket { IronbarkVersion = 1, Name = "x" };
        Assert.NotEqual(Ironbark.Version, req.IronbarkVersion);
    }

    [Fact]
    public void MessageId_Frame_RoundTrip_TradeInventory()
    {
        var original = new TradeInventoryPacket
        {
            PlayerId = 2,
            NpcName = "Trader",
            StockCsv = "wood,3;nail,1"
        };
        var bytes = IronbarkRegistry.Encode(original);
        Assert.True(bytes.Length >= 2);

        var reader = new NetDataReader(bytes);
        var msgId = reader.GetUShort();
        Assert.Equal(original.MessageId, msgId);

        var restored = IronbarkRegistry.CreateAndDeserialize(msgId, reader) as TradeInventoryPacket;
        Assert.NotNull(restored);
        Assert.Equal(original.PlayerId, restored!.PlayerId);
        Assert.Equal(original.NpcName, restored.NpcName);
        Assert.Equal(original.StockCsv, restored.StockCsv);
    }

    [Fact]
    public void MessageId_Frame_RoundTrip_Position()
    {
        var original = new PositionUpdatePacket
        {
            PlayerId = 1,
            X = 1.5f, Y = 2.5f, Z = 3.5f
        };
        var bytes = IronbarkRegistry.Encode(original);
        var reader = new NetDataReader(bytes);
        var msgId = reader.GetUShort();
        var restored = IronbarkRegistry.CreateAndDeserialize(msgId, reader) as PositionUpdatePacket;
        Assert.NotNull(restored);
        Assert.Equal(original.X, restored!.X);
        Assert.Equal(original.PlayerId, restored.PlayerId);
    }

    [Fact]
    public void Registry_Critical_For_Chapter_And_World()
    {
        Assert.True(IronbarkRegistry.IsCritical((ushort)PacketType.ChapterTransition));
        Assert.True(IronbarkRegistry.IsCritical((ushort)PacketType.WorldChunk));
        Assert.True(IronbarkRegistry.IsCritical((ushort)PacketType.TradeInventory));
        Assert.False(IronbarkRegistry.IsCritical((ushort)PacketType.PositionUpdate));
    }

    [Fact]
    public void Registry_Backup_No_FanOut()
    {
        Assert.False(IronbarkRegistry.ShouldFanOut((ushort)PacketType.ClientStateBackupChunk));
        Assert.True(IronbarkRegistry.ShouldFanOut((ushort)PacketType.TradeInventory));
    }

    [Fact]
    public void ConnectRequest_Has_Capabilities()
    {
        var req = new ConnectRequestPacket();
        var w = new NetDataWriter();
        req.Serialize(w);
        var raw = new byte[w.Length];
        Buffer.BlockCopy(w.Data, 0, raw, 0, w.Length);
        var r = new NetDataReader(raw);
        var back = new ConnectRequestPacket();
        back.Deserialize(r);
        Assert.Equal(Ironbark.Version, back.IronbarkVersion);
        Assert.Equal(Ironbark.Caps.Local, back.Capabilities);
    }

    [Fact]
    public void PhysicsState_Lane_Registered()
    {
        Assert.True(IronbarkRegistry.TryGet((ushort)PacketType.PhysicsStateBatch, out var e));
        Assert.NotNull(e);
        Assert.Equal(IronbarkReliability.Unreliable, e!.Reliability);
    }

    [Fact]
    public void Caps_Local_Omits_PhysicsState_Until_Emitted()
    {
        Assert.Equal(0u, Ironbark.Caps.Local & Ironbark.Caps.PhysicsState);
        Assert.NotEqual(0u, Ironbark.Caps.Local & Ironbark.Caps.SpectateFull);
        Assert.NotEqual(0u, Ironbark.Caps.Local & Ironbark.Caps.ClientBackup);
    }

    [Fact]
    public void MessageId_Frame_RoundTrip_EntitySpawn()
    {
        var original = new EntitySpawnPacket
        {
            PlayerId = 1,
            EntityId = 42,
            EntityType = "dog",
            PrefabPath = "Characters/dog",
            X = 1f, Y = 2f, Z = 3f, RotY = 90f
        };
        var bytes = IronbarkRegistry.Encode(original);
        var reader = new NetDataReader(bytes);
        var msgId = reader.GetUShort();
        Assert.Equal((ushort)PacketType.EntitySpawn, msgId);
        var restored = IronbarkRegistry.CreateAndDeserialize(msgId, reader) as EntitySpawnPacket;
        Assert.NotNull(restored);
        Assert.Equal(original.EntityId, restored!.EntityId);
        Assert.Equal(original.EntityType, restored.EntityType);
        Assert.Equal(original.PrefabPath, restored.PrefabPath);
        Assert.Equal(original.X, restored.X);
    }

    [Fact]
    public void MessageId_Frame_RoundTrip_EntityBurning()
    {
        var original = new EntityBurningPacket
        {
            PlayerId = 0,
            EntityId = 7,
            IsBurning = true,
            BurnTime = 12.5f,
            Modifier = 1f,
            Interval = 0.5f
        };
        var bytes = IronbarkRegistry.Encode(original);
        var reader = new NetDataReader(bytes);
        var msgId = reader.GetUShort();
        Assert.Equal((ushort)PacketType.EntityBurning, msgId);
        var restored = IronbarkRegistry.CreateAndDeserialize(msgId, reader) as EntityBurningPacket;
        Assert.NotNull(restored);
        Assert.Equal(original.EntityId, restored!.EntityId);
        Assert.True(restored.IsBurning);
        Assert.Equal(original.BurnTime, restored.BurnTime);
    }

    [Fact]
    public void MessageId_Frame_RoundTrip_ExplosionSpawnObject()
    {
        var original = new ExplosionSpawnObjectPacket
        {
            PlayerId = 3,
            PrefabName = "FireCloud",
            X = 10f, Y = 0f, Z = -4f,
            Rx = 90f, Ry = 0f, Rz = 0f
        };
        var bytes = IronbarkRegistry.Encode(original);
        var reader = new NetDataReader(bytes);
        var msgId = reader.GetUShort();
        Assert.Equal((ushort)PacketType.ExplosionSpawnObject, msgId);
        var restored = IronbarkRegistry.CreateAndDeserialize(msgId, reader) as ExplosionSpawnObjectPacket;
        Assert.NotNull(restored);
        Assert.Equal(original.PrefabName, restored!.PrefabName);
        Assert.Equal(original.X, restored.X);
        Assert.Equal(original.Rz, restored.Rz);
    }

    [Fact]
    public void Gap_Messages_Registered_Reliable()
    {
        Assert.True(IronbarkRegistry.IsCritical((ushort)PacketType.EntitySpawn) == false);
        Assert.Equal(IronbarkReliability.Reliable, IronbarkMeta.Reliability(PacketType.EntitySpawn));
        Assert.Equal(IronbarkReliability.Reliable, IronbarkMeta.Reliability(PacketType.EntityBurning));
        Assert.Equal(IronbarkReliability.Reliable, IronbarkMeta.Reliability(PacketType.ExplosionSpawnObject));
        Assert.True(IronbarkRegistry.TryGet((ushort)PacketType.PlayerBurning, out _));
        Assert.True(IronbarkRegistry.TryGet((ushort)PacketType.LiquidStopBurn, out _));
    }
}
