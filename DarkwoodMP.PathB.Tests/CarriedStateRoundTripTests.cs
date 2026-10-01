using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Item and NPC state that rides on shared messages must survive the wire with its values
/// (workbench upgrades, flashlight-on, empty magazine / zero durability, NPC reputation trailers).
/// </summary>
public class CarriedStateRoundTripTests
{
    private static T RoundTrip<T>(Action<NetWriter> write, Func<NetReader, T> read)
    {
        var w = new NetWriter();
        write(w);
        var r = new NetReader(w.CopyData());
        T back = read(r);
        Assert.Equal(0, r.AvailableBytes);
        return back;
    }

    [Fact]
    public void DroppedItemSpawn_CarriesUpgradesFlashlightAndEmptyMag()
    {
        var msg = new DroppedItemSpawnMessage
        {
            Guid = "g1", PrefabPath = "Items/Pistol", ItemType = "pistol", Amount = 1,
            Durability = 0f, Ammo = 0, IsRecipe = false,
            Upgrades = new[] { "scope", "extendedMag" }, ShouldBeActive = true,
        };
        var back = RoundTrip(msg.Serialize, DroppedItemSpawnMessage.Deserialize);
        Assert.Equal(msg.Upgrades, back.Upgrades);
        Assert.True(back.ShouldBeActive);
        Assert.Equal(0, back.Ammo);
        Assert.Equal(0f, back.Durability);
    }

    [Fact]
    public void ContainerItem_CarriesUpgradesRecipeAndFlashlight()
    {
        var msg = new ContainerItemMessage
        {
            Action = ContainerAction.PlaceItem, SlotIndex = 3, ItemType = "flashlight", Amount = 1,
            Durability = 12.5f, Ammo = 0, IsRecipe = true,
            Upgrades = new[] { "battery" }, ShouldBeActive = true,
        };
        var back = RoundTrip(msg.Serialize, ContainerItemMessage.Deserialize);
        Assert.Equal(msg.Upgrades, back.Upgrades);
        Assert.True(back.IsRecipe);
        Assert.True(back.ShouldBeActive);
        Assert.Equal(12.5f, back.Durability);
    }

    [Fact]
    public void TradeInventorySync_CarriesPerEntryUpgradesAndFlashlight()
    {
        var msg = new TradeInventorySyncMessage
        {
            NpcName = "trader", ItemCount = 2,
            ItemTypes = new[] { "pistol", "flashlight" },
            Amounts = new[] { 0, 1 },
            IsRecipe = new[] { false, false },
            Durabilities = new[] { 0f, 30f },
            Upgrades = new[] { new[] { "scope" }, Array.Empty<string>() },
            ShouldBeActive = new[] { false, true },
        };
        var back = RoundTrip(msg.Serialize, TradeInventorySyncMessage.Deserialize);
        Assert.Equal(new[] { 0, 1 }, back.Amounts);
        Assert.Equal(new[] { 0f, 30f }, back.Durabilities);
        Assert.Equal(new[] { "scope" }, back.Upgrades[0]);
        Assert.Equal(new[] { false, true }, back.ShouldBeActive);
    }

    [Fact]
    public void ReputationSync_CarriesEveryNpcStateTrailer()
    {
        var msg = new ReputationSyncMessage
        {
            NpcName = "wolfman", Reputation = -3,
            HasAttackedId = true, AttackedId = 41,
            HasDead = true, Dead = true, DeadId = 7,
            HasPortrait = true, PortraitType = 2, ApplyDialoguePortrait = true,
            PosX = 1f, PosY = 2f, PosZ = 3f,
            HasAnimLibrary = true, AnimLibraryName = "wolfman_hurt",
        };
        var back = RoundTrip(msg.Serialize, ReputationSyncMessage.Deserialize);
        Assert.Equal(-3, back.Reputation);
        Assert.Equal(41, back.AttackedId);
        Assert.True(back.Dead);
        Assert.Equal(7, back.DeadId);
        Assert.Equal(2, back.PortraitType);
        Assert.True(back.ApplyDialoguePortrait);
        Assert.Equal("wolfman_hurt", back.AnimLibraryName);
    }
}
