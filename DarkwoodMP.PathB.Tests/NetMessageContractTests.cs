using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Wire ID contract for the current protocol, checked against the compiled enum.
/// </summary>
public class NetMessageContractTests
{
    [Fact]
    public void StableMessageIds()
    {
        Assert.Equal(111, (byte)NetMessageType.ChatMessage);
        Assert.Equal(112, (byte)NetMessageType.DialogNpcLock);
        Assert.Equal(113, (byte)NetMessageType.DialogTreeState);
        Assert.Equal(114, (byte)NetMessageType.WorldRequest);
        Assert.Equal(115, (byte)NetMessageType.ContainerTakeDenied);
        Assert.Equal(116, (byte)NetMessageType.FeederState);
        Assert.Equal(117, (byte)NetMessageType.LureState);
        Assert.Equal(118, (byte)NetMessageType.SleepEndRequest);
        Assert.Equal(119, (byte)NetMessageType.WorkbenchLock);
        Assert.Equal(120, (byte)NetMessageType.DreamSessionBulk);
        Assert.Equal(121, (byte)NetMessageType.DreamChainStart);
        Assert.Equal(122, (byte)NetMessageType.AfterNightEndRequest);
        Assert.Equal(123, (byte)NetMessageType.PeerRoster);
        Assert.Equal(124, (byte)NetMessageType.HostHandoff);
        Assert.Equal(125, (byte)NetMessageType.ThrowableDespawn);
        Assert.Equal(126, (byte)NetMessageType.TrapBulk);
        Assert.Equal(127, (byte)NetMessageType.NightShadowSpawnRequest);
        Assert.Equal(128, (byte)NetMessageType.DreamPropCollider);
        Assert.Equal(129, (byte)NetMessageType.VoiceData);
        Assert.Equal(130, (byte)NetMessageType.ActivateCursorAction);
        Assert.Equal(131, (byte)NetMessageType.LocationTransport);
        Assert.Equal(132, (byte)NetMessageType.EntityDespawn);
        Assert.Equal(133, (byte)NetMessageType.PeerHasItem);
        Assert.Equal(134, (byte)NetMessageType.ChainState);
        Assert.Equal(135, (byte)NetMessageType.ShadowArmorState);
        Assert.Equal(136, (byte)NetMessageType.GameEventsBulk);
        Assert.Equal(137, (byte)NetMessageType.WorldBurnState);
        Assert.Equal(138, (byte)NetMessageType.ScenarioStateBulk);
        Assert.Equal(139, (byte)NetMessageType.HostWorldReady);
        Assert.Equal(140, (byte)NetMessageType.ChapterShareAck);
        Assert.Equal(141, (byte)NetMessageType.ChapterLoadGo);
        Assert.Equal(142, (byte)NetMessageType.LocationPadSlotRequest);
        Assert.Equal(143, (byte)NetMessageType.LocationPadSlotSync);
        Assert.Equal(144, (byte)NetMessageType.NightDeathRelease);
        Assert.Equal(145, (byte)NetMessageType.MorningReward);
        // 146 (SessionSettings) is the highest id; resolved by name so a rename fails with a clear message.
        Assert.True(Enum.TryParse("SessionSettings", out NetMessageType sessionSettings),
            "NetMessageType.SessionSettings (146) is missing");
        Assert.Equal(146, (byte)sessionSettings);
        Assert.Equal(146, (byte)NetMessageType._Highest);
    }

    [Fact]
    public void CoreCombatAndWorldIds_Unchanged()
    {
        Assert.Equal(1, (byte)NetMessageType.Handshake);
        Assert.Equal(8, (byte)NetMessageType.PlayerAttack);
        Assert.Equal(9, (byte)NetMessageType.DamagePlayer);
        Assert.Equal(21, (byte)NetMessageType.TimeSync);
        Assert.Equal(48, (byte)NetMessageType.FlagSync);
        Assert.Equal(90, (byte)NetMessageType.DialogOutcomeSync);
        Assert.Equal(103, (byte)NetMessageType.WorldSaveBegin);
    }
}
