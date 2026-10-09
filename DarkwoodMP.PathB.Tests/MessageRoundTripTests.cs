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
    public void ChapterShareAck_TruncatedPayloadIsRejected()
    {
        // Every peer runs the same protocol (the handshake refuses others), so a short packet is
        // malformed, never an older layout.
        var w = new NetWriter();
        w.Put(3);
        w.Put(ChapterShareAckMessage.StatusCommitted);
        w.Put("");
        Assert.Throws<System.IO.InvalidDataException>(
            () => ChapterShareAckMessage.Deserialize(new NetReader(w.CopyData())));
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
    public void ChapterTransition_RoundTrips()
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
    public void DialogNpcLock_RoundTrips_WithSpot()
    {
        var msg = new DialogNpcLockMessage
        {
            NpcName = "oven", OwnerPlayerId = 3, Granted = true, Release = false, IsRequest = true,
            Dream = true, Renewal = true, HasPos = true, PosX = -208f, PosY = 1f, PosZ = 176f
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = DialogNpcLockMessage.Deserialize(r);

        Assert.Equal("oven", back.NpcName);
        Assert.Equal(3, back.OwnerPlayerId);
        Assert.True(back.Granted);
        Assert.False(back.Release);
        Assert.True(back.IsRequest);
        Assert.True(back.Dream);
        Assert.True(back.Renewal);
        Assert.True(back.HasPos);
        Assert.Equal(-208f, back.PosX);
        Assert.Equal(1f, back.PosY);
        Assert.Equal(176f, back.PosZ);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void DialogOutcomeSync_RoundTrips_WithSpot()
    {
        var msg = new DialogOutcomeSyncMessage
        {
            NpcName = "doctor", DecisionIndex = 2, DialogueName = "welcome_opening", BoardIndex = 1,
            TargetDialogueName = "lookAtOven", HasPos = true, PosX = 350f, PosY = 0f, PosZ = -1809f,
            Dream = false
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = DialogOutcomeSyncMessage.Deserialize(r);

        Assert.Equal("doctor", back.NpcName);
        Assert.Equal(2, back.DecisionIndex);
        Assert.Equal("welcome_opening", back.DialogueName);
        Assert.Equal(1, back.BoardIndex);
        Assert.Equal("lookAtOven", back.TargetDialogueName);
        Assert.True(back.HasPos);
        Assert.Equal(350f, back.PosX);
        Assert.Equal(-1809f, back.PosZ);
        Assert.False(back.Dream);
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
    public void DreamStarted_RoundTrips()
    {
        var msg = new DreamStartedMessage
        {
            PresetName = "dream_bunker", LocPosX = -75000f, LocPosY = 1f, LocPosZ = 20f,
            SessionId = 5, LvlFlags = 3, CompletedPresets = new[] { "a", "b" }, EntryTransition = true
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = DreamStartedMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.True(back.EntryTransition);
        Assert.Equal("dream_bunker", back.PresetName);
        Assert.Equal(-75000f, back.LocPosX);
        Assert.Equal(5, back.SessionId);
        Assert.Equal((byte)3, back.LvlFlags);
        Assert.Equal(new[] { "a", "b" }, back.CompletedPresets);
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
    public void PlayerAttack_WithoutEffectsRoundTrips()
    {
        var msg = new PlayerAttackMessage { TargetNameHash = 1, Damage = 5, TargetName = "x" };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PlayerAttackMessage.Deserialize(r);
        Assert.Null(back.Effects);
        Assert.Equal(0, r.AvailableBytes);
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
    public void DamagePlayer_CarriesShadowHit()
    {
        var msg = new DamagePlayerMessage { Damage = 7, ShadowHit = true };
        var back = DamagePlayerMessage.Deserialize(new NetReader(Bytes(msg.Serialize)));
        Assert.True(back.ShadowHit);
        Assert.Equal(7, back.Damage);
    }

    [Fact]
    public void EnemyAttack_MeleeOmitsFlightFields_ThrownCarriesThem()
    {
        var melee = new EnemyAttackMessage
        {
            EntityId = 12, Kind = EnemyAttackMessage.KindMelee, Name = "MeleeSensor_dog", HostTime = 33.5f,
            PosX = 1f, PosY = 2f, PosZ = 3f, RotY = 90f, Damage = 22.5f, Clip = "Attack1", ClipFrame = 4
        };
        byte[] meleeBytes = Bytes(melee.Serialize);
        var r = new NetReader(meleeBytes);
        var backMelee = EnemyAttackMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("MeleeSensor_dog", backMelee.Name);
        Assert.Equal(22.5f, backMelee.Damage);
        Assert.Equal((short)4, backMelee.ClipFrame);
        Assert.False(backMelee.Thrown);

        var thrown = melee;
        thrown.Kind = EnemyAttackMessage.KindRangedSensor;
        thrown.Thrown = true;
        thrown.VelX = 5f; thrown.LandZ = 9f; thrown.FlyTime = 0.7f; thrown.Drag = 2f;
        byte[] thrownBytes = Bytes(thrown.Serialize);
        Assert.Equal(meleeBytes.Length + 8 * 4, thrownBytes.Length);
        var backThrown = EnemyAttackMessage.Deserialize(new NetReader(thrownBytes));
        Assert.True(backThrown.Thrown);
        Assert.Equal(5f, backThrown.VelX);
        Assert.Equal(9f, backThrown.LandZ);
        Assert.Equal(0.7f, backThrown.FlyTime);
        Assert.Equal(2f, backThrown.Drag);
    }

    [Fact]
    public void EntitySnapshot_DescriptorTravelsOnlyWhenFlagged()
    {
        var bare = new EntitySnapshotNet
        {
            Index = 3, Clip = "Walk", Loop = 2, EntityName = "dog_01", PrefabPath = "Characters/dog_01", SaveId = 4711
        };
        var withDesc = bare;
        withDesc.HasDescriptor = true;
        byte[] a = Bytes(bare.Serialize);
        byte[] b = Bytes(withDesc.Serialize);
        Assert.True(b.Length > a.Length);
        var backBare = EntitySnapshotNet.Deserialize(new NetReader(a));
        Assert.Null(backBare.EntityName);
        Assert.Equal(0, backBare.SaveId);
        Assert.Equal(2, backBare.Loop);
        var backDesc = EntitySnapshotNet.Deserialize(new NetReader(b));
        Assert.Equal("dog_01", backDesc.EntityName);
        Assert.Equal("Characters/dog_01", backDesc.PrefabPath);
        Assert.Equal(4711, backDesc.SaveId);

        var batch = new EntityStateMessage { Sequence = 9, HostTime = 12.25f, Entities = new[] { withDesc, bare } };
        var r = new NetReader(Bytes(batch.Serialize));
        var backBatch = EntityStateMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal(12.25f, backBatch.HostTime);
        Assert.Equal(2, backBatch.Entities.Length);
    }

    [Fact]
    public void EntitySnapshot_AnimationAndFlightFlags_AndPassThroughClip_RoundTrip()
    {
        var snap = new EntitySnapshotNet
        {
            Index = 12, Clip = "RotateLeft_End", ClipFrame = 3, Alive = true, HealthPct = 80,
            Flags2 = EntitySnapshotNet.Flag2Animating | EntitySnapshotNet.Flag2InFlight
                | EntitySnapshotNet.Flag2Diving | EntitySnapshotNet.Flag2PrevClip,
            PrevClip = "RotateLeft_Loop", PrevClipAgeMs = 40000
        };
        var r = new NetReader(Bytes(snap.Serialize));
        var back = EntitySnapshotNet.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.True(back.Animating);
        Assert.True(back.InFlight);
        Assert.True(back.Diving);
        Assert.True(back.HasPrevClip);
        Assert.Equal("RotateLeft_Loop", back.PrevClip);
        Assert.Equal((ushort)40000, back.PrevClipAgeMs);
        Assert.Equal((short)3, back.ClipFrame);

        // Without the pass-through bit nothing of it travels.
        snap.Flags2 = EntitySnapshotNet.Flag2Animating;
        var r2 = new NetReader(Bytes(snap.Serialize));
        var plain = EntitySnapshotNet.Deserialize(r2);
        Assert.Equal(0, r2.AvailableBytes);
        Assert.False(plain.HasPrevClip);
        Assert.Null(plain.PrevClip);
    }

    [Fact]
    public void EnemyHitConfirm_RoundTrips()
    {
        var msg = new EnemyHitConfirmMessage { EntityId = 5, Kind = 2, Damage = 30, HitX = 1f, HitY = 2f, HitZ = 3f };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = EnemyHitConfirmMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal(30, back.Damage);
        Assert.Equal((byte)2, back.Kind);
        Assert.Equal(3f, back.HitZ);
    }

    [Fact]
    public void PlayerAudio_RoundTrips()
    {
        var msg = new PlayerAudioMessage
        {
            SoundId = "door_hit_metal", Volume = 0.5f, PosX = 1f, PosY = 2f, PosZ = 3f, StickToSender = false
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PlayerAudioMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("door_hit_metal", back.SoundId);
        Assert.Equal(0.5f, back.Volume);
        Assert.Equal(3f, back.PosZ);
        Assert.False(back.StickToSender);
    }

    [Fact]
    public void EntitySound_RoundTrips()
    {
        var msg = new EntitySoundMessage
        {
            HostId = 12, Kind = EntitySoundKind.GetHit, SoundId = "dog_getHit", Volume = 0.75f, AttackerId = 3
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = EntitySoundMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal((short)12, back.HostId);
        Assert.Equal(EntitySoundKind.GetHit, back.Kind);
        Assert.Equal("dog_getHit", back.SoundId);
        Assert.Equal(0.75f, back.Volume);
        Assert.Equal(3, back.AttackerId);
    }

    [Fact]
    public void TradeCommit_RoundTrips()
    {
        var msg = new TradeCommitMessage
        {
            NpcName = "trader_01", PosX = 1f, PosY = 2f, PosZ = 3f, InDream = false, Denied = true,
            Bought = new[] { new TradeEntry { Type = "pistol", Count = 1, Durability = 0.5f, Ammo = 3, Active = false, Upgrades = new[] { "u1" } } },
            Sold = new[] { new TradeEntry { Type = "planks", IsRecipe = false, Count = 4, Durability = 1f } }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = TradeCommitMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("trader_01", back.NpcName);
        Assert.True(back.Denied);
        Assert.Single(back.Bought);
        Assert.Equal(3, back.Bought[0].Ammo);
        Assert.Equal("u1", back.Bought[0].Upgrades[0]);
        Assert.Equal(4, back.Sold[0].Count);
        Assert.Null(back.Sold[0].Upgrades);
    }

    [Fact]
    public void PlayerSpecial_RoundTrips()
    {
        var msg = new PlayerSpecialMessage { Method = "special_onKillNightTrader" };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PlayerSpecialMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("special_onKillNightTrader", back.Method);
    }

    [Fact]
    public void DialogMirror_RoundTripsABoard()
    {
        var msg = new DialogMirrorMessage
        {
            Kind = DialogMirrorMessage.KindBoard,
            OwnerId = 3,
            NpcName = "wolf",
            HasPos = true,
            PosX = 1f, PosY = 2f, PosZ = 3f,
            Index = 2,
            Flag = true,
            OffsetX = 4f, OffsetY = 5f,
            DialogueName = "item_key_baba_sister",
            Elements = new[]
            {
                new DialogMirrorElement { Kind = DialogMirrorElement.KindText, Text = "line", Z = -40f, A = 1f, WriteSpeed = 0.025f, Interval = 0.5f, Menu = -1 },
                new DialogMirrorElement { Kind = DialogMirrorElement.KindDecisionBtn, Text = "nod", Target = "x_yes", Menu = 0 }
            }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = DialogMirrorMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("item_key_baba_sister", back.DialogueName);
        Assert.Equal(2, back.Elements.Length);
        Assert.Equal("x_yes", back.Elements[1].Target);
        Assert.Equal((short)0, back.Elements[1].Menu);
        Assert.Equal(0.025f, back.Elements[0].WriteSpeed);
    }

    [Fact]
    public void DialogMirror_RejectsTooManyElements()
    {
        var w = new NetWriter();
        new DialogMirrorMessage { Kind = DialogMirrorMessage.KindOptions }.Serialize(w);
        byte[] bytes = w.CopyData();
        bytes[bytes.Length - 1] = DialogMirrorMessage.MaxElements + 1;
        Assert.Throws<InvalidDataException>(() => DialogMirrorMessage.Deserialize(new NetReader(bytes)));
    }

    [Fact]
    public void CosmeticState_RoundTripsMovers()
    {
        var msg = new CosmeticStateMessage
        {
            Kind = CosmeticStateMessage.KindMovers,
            MoverNames = new[] { "bench_church_a_01", "rock_movable_01" },
            MoverX = new[] { 1200.5f, -75010f },
            MoverZ = new[] { 300f, 42.25f },
            MoverKeys = new[] { 123456, -98765 }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = CosmeticStateMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal(new[] { "bench_church_a_01", "rock_movable_01" }, back.MoverNames);
        Assert.Equal(-75010f, back.MoverX[1]);
        Assert.Equal(42.25f, back.MoverZ[1]);
        Assert.Equal(-98765, back.MoverKeys[1]);
    }

    [Fact]
    public void CosmeticState_RoundTripsDecks()
    {
        var msg = new CosmeticStateMessage
        {
            Kind = CosmeticStateMessage.KindDecks,
            DeckNames = new[] { "corpses", "empty" },
            DeckLines = new[] { new[] { "corpse_desc_01", "corpse_desc_03" }, new string[0] }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = CosmeticStateMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("corpses", back.DeckNames[0]);
        Assert.Equal(new[] { "corpse_desc_01", "corpse_desc_03" }, back.DeckLines[0]);
        Assert.Empty(back.DeckLines[1]);
    }

    [Fact]
    public void CosmeticState_RejectsTooManyMovers()
    {
        var w = new NetWriter();
        w.Put(CosmeticStateMessage.KindMovers);
        w.Put(CosmeticStateMessage.MaxEntries + 1);
        Assert.Throws<InvalidDataException>(() => CosmeticStateMessage.Deserialize(new NetReader(w.CopyData())));
    }

    [Fact]
    public void ExamineObject_RoundTripsAPoolDraw()
    {
        var msg = new ExamineObjectMessage
        {
            Action = ExamineObjectMessage.ActionState,
            PosX = 1f, PosY = 2f, PosZ = 3f,
            ObjectName = "corpse_01",
            Examined = true,
            DisplayedDescriptionPool = true,
            HasDraw = true,
            DrawPool = "corpses",
            DrawLine = "corpse_desc_02",
            DrawRefreshed = true,
            DrawnBy = 3
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = ExamineObjectMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.True(back.HasDraw);
        Assert.Equal("corpses", back.DrawPool);
        Assert.Equal("corpse_desc_02", back.DrawLine);
        Assert.True(back.DrawRefreshed);
        Assert.Equal(3, back.DrawnBy);

        var plain = new ExamineObjectMessage { Action = ExamineObjectMessage.ActionRequest, ObjectName = "x" };
        r = new NetReader(Bytes(plain.Serialize));
        Assert.False(ExamineObjectMessage.Deserialize(r).HasDraw);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void EntitySnapshot_DescriptorCarriesTheLookKey()
    {
        var e = new EntitySnapshotNet
        {
            Index = 7,
            Clip = "idle",
            HasDescriptor = true,
            EntityName = "Dog",
            PrefabPath = "Characters/Dog",
            SaveId = 55,
            LookKey = -424242
        };
        var r = new NetReader(Bytes(e.Serialize));
        var back = EntitySnapshotNet.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal(-424242, back.LookKey);
        Assert.Equal(55, back.SaveId);
    }

    [Fact]
    public void PorterTransport_RoundTrips()
    {
        var msg = new PorterTransportMessage { Source = "hideout_2", Dest = "hideout_1" };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = PorterTransportMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal("hideout_2", back.Source);
        Assert.Equal("hideout_1", back.Dest);
    }

    [Fact]
    public void BansheeAgitation_RoundTrips()
    {
        var msg = new BansheeAgitationMessage { HostId = 7, VictimId = 2, Agitated = true, Overlay = true };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = BansheeAgitationMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.Equal((short)7, back.HostId);
        Assert.Equal(2, back.VictimId);
        Assert.True(back.Agitated);
        Assert.True(back.Overlay);
    }

    [Fact]
    public void LightState_RoundTripsSwitched()
    {
        var msg = new LightStateMessage
        {
            PosX = 1f, PosY = 2f, PosZ = 3f, IsOn = true, ItemName = "lamp", ItemType = "lamp_oil", Switched = true
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = LightStateMessage.Deserialize(r);
        Assert.Equal(0, r.AvailableBytes);
        Assert.True(back.IsOn);
        Assert.True(back.Switched);
        Assert.Equal("lamp_oil", back.ItemType);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JournalItem_RoundTripsDreamFlag(bool inDream)
    {
        // Whether the page is a dream's travels with it: the receiver's own dream state said
        // nothing about where the sender found it.
        var msg = new JournalItemMessage { Kind = JournalItemKind.JournalEntry, Type = "act1_entry1", InDream = inDream };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = JournalItemMessage.Deserialize(r);

        Assert.Equal(JournalItemKind.JournalEntry, back.Kind);
        Assert.Equal("act1_entry1", back.Type);
        Assert.Equal(inDream, back.InDream);
        Assert.Equal(0, r.AvailableBytes);
    }

    private static MapPinWire SamplePin(int id) => new MapPinWire
    {
        Id = id, Kind = 3, Color = 2, Chapter = 1, X = -1234.5f, Z = 678.25f, Day = 4,
        OwnerId = 2, OwnerTag = "a1b2c3d4e5f6", OwnerName = "Anna", Label = "meat here"
    };

    private static void AssertPin(MapPinWire a, MapPinWire b)
    {
        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Kind, b.Kind);
        Assert.Equal(a.Color, b.Color);
        Assert.Equal(a.Chapter, b.Chapter);
        Assert.Equal(a.X, b.X);
        Assert.Equal(a.Z, b.Z);
        Assert.Equal(a.Day, b.Day);
        Assert.Equal(a.OwnerId, b.OwnerId);
        Assert.Equal(a.OwnerTag, b.OwnerTag);
        Assert.Equal(a.OwnerName, b.OwnerName);
        Assert.Equal(a.Label, b.Label);
    }

    [Fact]
    public void MapPinRequest_RoundTrips()
    {
        var msg = new MapPinRequestMessage
        {
            Op = (byte)MapPinOp.SetLabel, PinId = 9, Kind = 1, Chapter = 2, X = 10f, Z = -20f,
            Label = "trader", OwnerName = "Bo"
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = MapPinRequestMessage.Deserialize(r);
        Assert.Equal(msg.Op, back.Op);
        Assert.Equal(9, back.PinId);
        Assert.Equal(1, back.Kind);
        Assert.Equal(2, back.Chapter);
        Assert.Equal(10f, back.X);
        Assert.Equal(-20f, back.Z);
        Assert.Equal("trader", back.Label);
        Assert.Equal("Bo", back.OwnerName);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void MapPinEvent_RoundTrips()
    {
        var msg = new MapPinEventMessage { Op = (byte)MapPinOp.Put, Pin = SamplePin(5) };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = MapPinEventMessage.Deserialize(r);
        Assert.Equal((byte)MapPinOp.Put, back.Op);
        AssertPin(msg.Pin, back.Pin);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void MapStateSync_RoundTripsPinsAndDiscoveries()
    {
        var msg = new MapStateSyncMessage
        {
            PinCount = 2,
            Pins = new[] { SamplePin(1), SamplePin(2) },
            DiscoveryCount = 2,
            DiscoveryElementNames = new[] { "big_hideout_03", "med_wolf_01" }
        };
        var r = new NetReader(Bytes(msg.Serialize));
        var back = MapStateSyncMessage.Deserialize(r);
        Assert.Equal(2, back.PinCount);
        AssertPin(msg.Pins[0], back.Pins[0]);
        AssertPin(msg.Pins[1], back.Pins[1]);
        Assert.Equal(msg.DiscoveryElementNames, back.DiscoveryElementNames);
        Assert.Equal(0, r.AvailableBytes);
    }

    [Fact]
    public void VoiceData_SliceAndSerializeShareOneLayout()
    {
        byte[] capture = { 9, 8, 7, 6, 5, 4 };
        var r = new NetReader(Bytes(w => VoiceDataMessage.WriteSlice(w, 3, 65535, VoiceDataMessage.FlagWalkie, 200, capture, 4)));
        var back = VoiceDataMessage.Deserialize(r);

        Assert.Equal(3, back.PlayerId);
        Assert.Equal((ushort)65535, back.Seq);
        Assert.Equal(VoiceDataMessage.FlagWalkie, back.Flags);
        Assert.Equal((byte)200, back.Level);
        Assert.Equal(new byte[] { 9, 8, 7, 6 }, back.Data);
        Assert.Equal(0, r.AvailableBytes);

        var again = new NetReader(Bytes(back.Serialize));
        Assert.Equal((byte)200, VoiceDataMessage.Deserialize(again).Level);
        Assert.Equal(0, again.AvailableBytes);
    }
}
