using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Wire ID contract for protocol 24 — compiled enum, not source greps.
/// </summary>
public class NetMessageContractTests
{
    [Fact]
    public void Protocol24_StableMessageIds()
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
        Assert.Equal(133, (byte)NetMessageType._Highest);
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
