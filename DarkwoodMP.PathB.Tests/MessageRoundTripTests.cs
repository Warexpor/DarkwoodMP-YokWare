using DWMPHorde.Networking;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Write -> Read round trips for the Unity-free wire structs added in protocol 26/27
/// (ids 140-145 and their neighbours). A field written but not read (or the reverse) shifts
/// every later field on the wire, so each test also asserts the reader consumed the payload.
/// </summary>
public class MessageRoundTripTests
{
    private static byte[] Bytes(Action<NetWriter> write)
    {
        var w = new NetWriter();
        write(w);
        return w.CopyData();
    }

    [Theory]
    [InlineData(ChapterShareAckMessage.StatusCommitted, "")]
    [InlineData(ChapterShareAckMessage.StatusFailed, "disk full")]
    [InlineData(ChapterShareAckMessage.StatusNotInWorld, "")]
    [InlineData(ChapterShareAckMessage.StatusReceiving, "")]
    public void ChapterShareAck_RoundTrips(byte status, string reason)
    {
        var msg = new ChapterShareAckMessage { ChapterId = 2, Status = status, Reason = reason, SharePass = 7 };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = ChapterShareAckMessage.Deserialize(r);

        Assert.Equal(2, back.ChapterId);
        Assert.Equal(status, back.Status);
        Assert.Equal(reason, back.Reason);
        Assert.Equal(7, back.SharePass);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void ChapterShareAck_WithoutSharePassTrailerReadsZero()
    {
        var w = new NetWriter();
        w.Put(3);
        w.Put(ChapterShareAckMessage.StatusCommitted);
        w.Put("");
        var back = ChapterShareAckMessage.Deserialize(new NetReader(w.CopyData()));
        Assert.Equal(3, back.ChapterId);
        Assert.Equal(0, back.SharePass);
    }

    [Fact]
    public void ChapterShareAck_NullReasonBecomesEmpty()
    {
        var back = ChapterShareAckMessage.Deserialize(
            new NetReader(Bytes(new ChapterShareAckMessage { ChapterId = 1, Reason = null }.Serialize)));
        Assert.Equal(string.Empty, back.Reason);
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(false, "Your world does not match the host's.")]
    public void ChapterLoadGo_RoundTrips(bool proceed, string reason)
    {
        var msg = new ChapterLoadGoMessage { ChapterId = 2, Proceed = proceed, Reason = reason };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = ChapterLoadGoMessage.Deserialize(r);

        Assert.Equal(2, back.ChapterId);
        Assert.Equal(proceed, back.Proceed);
        Assert.Equal(reason, back.Reason);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void ChapterTransition_AckRequiredIsAnOptionalTrailer()
    {
        var full = new ChapterTransitionMessage
        {
            ChapterId = 2, LoadChapterSave = true, ExpectWorldShare = true, AckRequired = true
        };
        var back = ChapterTransitionMessage.Deserialize(new NetReader(Bytes(full.Serialize)));
        Assert.Equal(2, back.ChapterId);
        Assert.True(back.LoadChapterSave);
        Assert.True(back.ExpectWorldShare);
        Assert.True(back.AckRequired);

        // A pre-handshake sender stops before the trailing byte: still readable, AckRequired false.
        byte[] legacy = Bytes(w => { w.Put(2); w.Put(true); w.Put(true); });
        var old = ChapterTransitionMessage.Deserialize(new NetReader(legacy));
        Assert.Equal(2, old.ChapterId);
        Assert.False(old.AckRequired);
    }

    [Fact]
    public void LocationPadSlotRequest_RoundTrips()
    {
        var msg = new LocationPadSlotRequestMessage { LocationName = "outside_hunterCabin_01" };
        var r = new NetReader(Bytes(msg.Serialize));
        Assert.Equal("outside_hunterCabin_01", LocationPadSlotRequestMessage.Deserialize(r).LocationName);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void LocationPadSlotSync_RoundTripsEntriesIncludingNoFreeSlot()
    {
        var msg = new LocationPadSlotSyncMessage
        {
            NextSlot = 3,
            Entries = new[]
            {
                new LocationPadSlotEntry { LocationName = "outside_a", Slot = 0, YawDeg = 90 },
                new LocationPadSlotEntry { LocationName = "outside_b", Slot = 2, YawDeg = 270 },
                new LocationPadSlotEntry { LocationName = "outside_c", Slot = -1, YawDeg = 0 },
            }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = LocationPadSlotSyncMessage.Deserialize(r);

        Assert.Equal(3, back.NextSlot);
        Assert.Equal(3, back.Entries.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(msg.Entries[i].LocationName, back.Entries[i].LocationName);
            Assert.Equal(msg.Entries[i].Slot, back.Entries[i].Slot);
            Assert.Equal(msg.Entries[i].YawDeg, back.Entries[i].YawDeg);
        }
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void LocationPadSlotSync_NullEntriesIsEmpty_AndHostileCountIsRejected()
    {
        var empty = LocationPadSlotSyncMessage.Deserialize(
            new NetReader(Bytes(new LocationPadSlotSyncMessage { NextSlot = 1, Entries = null }.Serialize)));
        Assert.Equal(1, empty.NextSlot);
        Assert.Empty(empty.Entries);

        byte[] hostile = Bytes(w => { w.Put(0); w.Put(100000); });
        Assert.Throws<InvalidDataException>(() => LocationPadSlotSyncMessage.Deserialize(new NetReader(hostile)));
    }

    [Fact]
    public void NightDeathRelease_RoundTrips()
    {
        var r = new NetReader(Bytes(new NightDeathReleaseMessage { Day = 7 }.Serialize));
        Assert.Equal(7, NightDeathReleaseMessage.Deserialize(r).Day);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void MorningReward_RoundTrips()
    {
        var msg = new MorningRewardMessage
        {
            Day = 4, TraderName = "NightTrader", Reputation = 12, Saturation = 10f, ShowTraderHelp = true
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = MorningRewardMessage.Deserialize(r);

        Assert.Equal(4, back.Day);
        Assert.Equal("NightTrader", back.TraderName);
        Assert.Equal(12, back.Reputation);
        Assert.Equal(10f, back.Saturation);
        Assert.True(back.ShowTraderHelp);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Theory]
    [InlineData(true, (byte)1, true, 4)]
    [InlineData(false, (byte)0, false, 1)]
    public void SessionSettings_RoundTrips(bool friendlyFire, byte lootShare, bool doubleItems, int party)
    {
        var msg = new SessionSettingsMessage
        {
            FriendlyFireEnabled = friendlyFire, LootShare = lootShare,
            DoubleItemsEnabled = doubleItems, PartyMultiplier = party
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = SessionSettingsMessage.Deserialize(r);

        Assert.Equal(friendlyFire, back.FriendlyFireEnabled);
        Assert.Equal(lootShare, back.LootShare);
        Assert.Equal(doubleItems, back.DoubleItemsEnabled);
        Assert.Equal(party, back.PartyMultiplier);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void SessionSettings_TruncatedPayloadThrowsInvalidData()
    {
        byte[] full = Bytes(new SessionSettingsMessage { PartyMultiplier = 3 }.Serialize);
        Assert.Throws<InvalidDataException>(
            () => SessionSettingsMessage.Deserialize(new NetReader(full.Take(full.Length - 2).ToArray())));
    }

    [Fact]
    public void TruncatedPayloads_ThrowInvalidData_NotGarbage()
    {
        byte[] full = Bytes(new MorningRewardMessage { Day = 1, TraderName = "t", Reputation = 1 }.Serialize);
        byte[] cut = full.Take(full.Length - 3).ToArray();
        Assert.Throws<InvalidDataException>(() => MorningRewardMessage.Deserialize(new NetReader(cut)));
        Assert.Throws<InvalidDataException>(() => NightDeathReleaseMessage.Deserialize(new NetReader(Array.Empty<byte>())));
    }

    [Fact]
    public void NightDeathState_RoundTripsPartyWipeFlag()
    {
        var msg = new NightDeathStateMessage { IsDead = true, AllDeadTrigger = false, PartyWipe = true };
        var back = NightDeathStateMessage.Deserialize(new NetReader(Bytes(msg.Serialize)));
        Assert.True(back.IsDead);
        Assert.False(back.AllDeadTrigger);
        Assert.True(back.PartyWipe);
    }

    [Fact]
    public void DreamStarted_RoundTripsWithAndWithoutTrailer()
    {
        var msg = new DreamStartedMessage
        {
            PresetName = "dream_bunker", LocPosX = -75000f, LocPosY = 1f, LocPosZ = 20f,
            SessionId = 5, LvlFlags = 3, CompletedPresets = new[] { "a", "b" }
        };
        var back = DreamStartedMessage.Deserialize(new NetReader(Bytes(msg.Serialize)));
        Assert.Equal("dream_bunker", back.PresetName);
        Assert.Equal(-75000f, back.LocPosX);
        Assert.Equal(5, back.SessionId);
        Assert.Equal((byte)3, back.LvlFlags);
        Assert.Equal(new[] { "a", "b" }, back.CompletedPresets);

        // Older sender: position only, no session/completions trailer.
        byte[] legacy = Bytes(w => { w.Put("d"); w.Put(1f); w.Put(2f); w.Put(3f); });
        var old = DreamStartedMessage.Deserialize(new NetReader(legacy));
        Assert.Equal("d", old.PresetName);
        Assert.Equal(0, old.SessionId);
        Assert.Empty(old.CompletedPresets);
    }

    private static SensorEffectWire[] SampleEffects() => new[]
    {
        new SensorEffectWire
        {
            Type = 9, TimeElapsed = 0.5f, Duration = 12f, Modifier = 3f, Interval = 1.5f,
            StopsBleeding = false, StopsPoison = true, HasPoisonOverlay = true, ActivateSound = "poison_hit"
        },
        new SensorEffectWire { Type = 10, Duration = 4f, Modifier = 1f, ActivateSound = "" }
    };

    private static void AssertSampleEffects(SensorEffectWire[] back)
    {
        Assert.NotNull(back);
        Assert.Equal(2, back.Length);
        Assert.Equal((byte)9, back[0].Type);
        Assert.Equal(0.5f, back[0].TimeElapsed);
        Assert.Equal(12f, back[0].Duration);
        Assert.Equal(3f, back[0].Modifier);
        Assert.Equal(1.5f, back[0].Interval);
        Assert.False(back[0].StopsBleeding);
        Assert.True(back[0].StopsPoison);
        Assert.True(back[0].HasPoisonOverlay);
        Assert.Equal("poison_hit", back[0].ActivateSound);
        Assert.Equal((byte)10, back[1].Type);
        Assert.Equal(4f, back[1].Duration);
        Assert.Equal("", back[1].ActivateSound);
    }

    [Fact]
    public void PlayerAttack_RoundTripsSensorEffects()
    {
        var msg = new PlayerAttackMessage
        {
            TargetNameHash = 7, Damage = 22, AttackerPosX = 1f, AttackerPosY = 2f, AttackerPosZ = 3f,
            TargetName = "wolf", TargetPosX = 4f, TargetPosY = 5f, TargetPosZ = 6f, CanCutInHalf = true,
            Effects = SampleEffects()
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PlayerAttackMessage.Deserialize(r);

        Assert.Equal((short)7, back.TargetNameHash);
        Assert.Equal(22, back.Damage);
        Assert.Equal("wolf", back.TargetName);
        Assert.True(back.CanCutInHalf);
        AssertSampleEffects(back.Effects);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void PlayerAttack_WithoutEffectsRoundTripsAndLegacyPayloadHasNone()
    {
        var msg = new PlayerAttackMessage { TargetNameHash = 1, Damage = 5, TargetName = "x" };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PlayerAttackMessage.Deserialize(r);
        Assert.Null(back.Effects);
        Assert.Equal(0, r.AvailableBytes);

        // Pre-trailer sender: payload ends right after CanCutInHalf.
        byte[] legacy = Bytes(w =>
        {
            w.Put((short)3); w.Put(9);
            w.Put(1f); w.Put(2f); w.Put(3f);
            w.Put("rat");
            w.Put(4f); w.Put(5f); w.Put(6f);
            w.Put(false);
        });
        var old = PlayerAttackMessage.Deserialize(new NetReader(legacy));
        Assert.Equal("rat", old.TargetName);
        Assert.Null(old.Effects);
    }

    [Fact]
    public void DamagePlayer_RoundTripsSensorEffects()
    {
        var msg = new DamagePlayerMessage
        {
            Damage = 30, AttackerPosX = 1f, AttackerPosY = 2f, AttackerPosZ = 3f,
            CanCutInHalf = false, ShowRedScreen = true, NormalHit = false, CanInterrupt = false,
            Effects = SampleEffects()
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = DamagePlayerMessage.Deserialize(r);

        Assert.Equal(30, back.Damage);
        Assert.True(back.ShowRedScreen);
        Assert.False(back.NormalHit);
        Assert.False(back.CanInterrupt);
        AssertSampleEffects(back.Effects);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void DamagePlayer_OlderPayloadsKeepDefaultsAndNoEffects()
    {
        // Oldest sender: no NormalHit / CanInterrupt / effects trailers.
        byte[] oldest = Bytes(w =>
        {
            w.Put(10); w.Put(1f); w.Put(2f); w.Put(3f); w.Put(false); w.Put(true);
        });
        var a = DamagePlayerMessage.Deserialize(new NetReader(oldest));
        Assert.Equal(10, a.Damage);
        Assert.True(a.NormalHit);
        Assert.True(a.CanInterrupt);
        Assert.Null(a.Effects);

        // Sender that predates only the effects trailer.
        byte[] noEffects = Bytes(w =>
        {
            w.Put(10); w.Put(1f); w.Put(2f); w.Put(3f); w.Put(false); w.Put(true); w.Put(false); w.Put(false);
        });
        var b = DamagePlayerMessage.Deserialize(new NetReader(noEffects));
        Assert.False(b.NormalHit);
        Assert.False(b.CanInterrupt);
        Assert.Null(b.Effects);
    }

    [Fact]
    public void FriendlyFire_RoundTripsSensorEffects()
    {
        var msg = new FriendlyFireMessage
        {
            Damage = 18, AttackerPosX = 1f, AttackerPosY = 2f, AttackerPosZ = 3f,
            CanCutInHalf = true, AttackerPlayerId = 2, VictimPlayerId = 3, Effects = SampleEffects()
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = FriendlyFireMessage.Deserialize(r);

        Assert.Equal(18, back.Damage);
        Assert.Equal(2, back.AttackerPlayerId);
        Assert.Equal(3, back.VictimPlayerId);
        AssertSampleEffects(back.Effects);
        Assert.Equal(0, r.AvailableBytes);

        // Pre-trailer sender: payload ends after VictimPlayerId.
        byte[] legacy = Bytes(w =>
        {
            w.Put(5); w.Put(1f); w.Put(2f); w.Put(3f); w.Put(false); w.Put(4); w.Put(5);
        });
        var old = FriendlyFireMessage.Deserialize(new NetReader(legacy));
        Assert.Equal(5, old.VictimPlayerId);
        Assert.Null(old.Effects);
    }

    [Fact]
    public void SensorEffects_ListIsCappedAndTruncatedPayloadThrows()
    {
        var many = new SensorEffectWire[SensorEffectWire.MaxEffects + 4];
        for (int i = 0; i < many.Length; i++) many[i] = new SensorEffectWire { Type = (byte)i, Duration = 1f };
        var back = SensorEffectWire.ReadList(new NetReader(Bytes(w => SensorEffectWire.WriteList(w, many))));
        Assert.Equal(SensorEffectWire.MaxEffects, back.Length);

        byte[] full = Bytes(w => SensorEffectWire.WriteList(w, SampleEffects()));
        byte[] cut = full.Take(full.Length - 2).ToArray();
        Assert.Throws<InvalidDataException>(() => SensorEffectWire.ReadList(new NetReader(cut)));
    }
}
