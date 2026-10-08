using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Unit tests drive shipped pure policy helpers (no Unity).
/// Other tests pin message contracts and handler wiring.
/// </summary>
public class CoopPolicyTests
{
    [Fact]
    public void PausePolicy_WorldPausesOnlyWhenEveryPlayerIsInTheMenu()
    {
        Assert.True(CoopPausePolicy.WorldPaused(hostInMenu: true, peers: 0, peersInMenu: 0));
        Assert.True(CoopPausePolicy.WorldPaused(hostInMenu: true, peers: 2, peersInMenu: 2));
        Assert.False(CoopPausePolicy.WorldPaused(hostInMenu: true, peers: 2, peersInMenu: 1));
        Assert.False(CoopPausePolicy.WorldPaused(hostInMenu: false, peers: 2, peersInMenu: 2));
        Assert.False(CoopPausePolicy.WorldPaused(hostInMenu: false, peers: 0, peersInMenu: 0));
    }

    [Fact]
    public void MorningVisitor_GoesToTheFullestHideout_TieToTheHost()
    {
        // Most players wins, even over the host's hideout.
        Assert.Equal(1, MorningVisitorPolicy.Pick(new[] { 1, 2 }, new[] { 1, 2 }, hostIndex: 0));
        // A tie goes to the host's hideout.
        Assert.Equal(1, MorningVisitorPolicy.Pick(new[] { 1, 1 }, new[] { 2, 1 }, hostIndex: 1));
        Assert.Equal(0, MorningVisitorPolicy.Pick(new[] { 1, 1, 1 }, new[] { 1, 2, 3 }, hostIndex: 0));
        // Host not home: the tie goes to the lowest player id.
        Assert.Equal(1, MorningVisitorPolicy.Pick(new[] { 1, 1 }, new[] { 4, 3 }, hostIndex: -1));
        // A tie the host is not part of: lowest id among the fullest.
        Assert.Equal(2, MorningVisitorPolicy.Pick(new[] { 1, 2, 2 }, new[] { 1, 5, 3 }, hostIndex: 0));
        Assert.Equal(-1, MorningVisitorPolicy.Pick(new int[0], new int[0], hostIndex: -1));
    }

    [Fact]
    public void TimePolicy_ClientConnected_SuppressesClock()
    {
        // Also gates Controller.useTimeSkip on connected clients (beds / wait-until-evening).
        Assert.True(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: true));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: false));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: false, isClient: true));
        Assert.True(CoopTimePolicy.ShouldUseRefreshTimeNoLogicOnClientSync);
        // Shared clock: stops only when the host and every peer are inside.
        Assert.True(CoopTimePolicy.SharedClockRuns(hostInOutsideLocation: false, anyRemoteInOpenWorld: false));
        Assert.True(CoopTimePolicy.SharedClockRuns(hostInOutsideLocation: true, anyRemoteInOpenWorld: true));
        Assert.False(CoopTimePolicy.SharedClockRuns(hostInOutsideLocation: true, anyRemoteInOpenWorld: false));
        // Village at night: away from the night warning until morning; flips unseen only.
        Assert.True(VillageNightPolicy.IsNearNight(970, 1100, 360));
        Assert.True(VillageNightPolicy.IsNearNight(100, 1100, 360));
        Assert.False(VillageNightPolicy.IsNearNight(969, 1100, 360));
        Assert.False(VillageNightPolicy.IsNearNight(360, 1100, 360));
        Assert.True(VillageNightPolicy.ShouldFlip(false, true, occupied: false, anyoneSees: true));
        Assert.False(VillageNightPolicy.ShouldFlip(false, true, occupied: true, anyoneSees: true));
        Assert.True(VillageNightPolicy.ShouldFlip(false, true, occupied: true, anyoneSees: false));
        Assert.False(VillageNightPolicy.ShouldFlip(true, true, occupied: false, anyoneSees: false));
        Assert.True(CoopTimePolicy.LiveStepCrossedMinute(1068, 1071, 1070));
        Assert.False(CoopTimePolicy.LiveStepCrossedMinute(100, 500, 1070));
        Assert.True(CoopTimePolicy.LiveStepCrossedMinute(1438, 2, 0));
        Assert.False(PartyRequirementPolicy.HaveItem(partyHas: true, activeModifier: false));
        Assert.True(PartyRequirementPolicy.HaveItem(partyHas: false, activeModifier: false));
        Assert.True(PartyRequirementPolicy.HaveItem(partyHas: true, activeModifier: true));
        Assert.True(PermadeathPolicy.UsesSharedDeath(true, PermadeathPolicy.Hard, 1));
        Assert.False(PermadeathPolicy.UsesSharedDeath(true, PermadeathPolicy.Hard, 2));
        Assert.False(PermadeathPolicy.UsesSharedDeath(false, PermadeathPolicy.Nightmare, 0));
        Assert.False(PermadeathPolicy.UsesSharedDeath(true, PermadeathPolicy.Normal, 0));
        Assert.True(PermadeathPolicy.UsesSharedDeath(true, PermadeathPolicy.Nightmare, 3));
        Assert.False(PermadeathPolicy.UsesSharedDeath(false, PermadeathPolicy.Nightmare, 3));
        Assert.True(PermadeathPolicy.PartyWipeEndsRun(true, 2, 2));
        Assert.False(PermadeathPolicy.PartyWipeEndsRun(true, 2, 1));
        Assert.False(PermadeathPolicy.PartyWipeEndsRun(false, 1, 1));
        Assert.True(PermadeathPolicy.PartyWipeEndsRun(true, 0, 0));
        Assert.True(ClientWorldSavePolicy.ShouldBlockConnectedClientWorldSave(true, false));
        Assert.False(ClientWorldSavePolicy.ShouldBlockConnectedClientWorldSave(true, true));
        Assert.False(ClientWorldSavePolicy.ShouldBlockConnectedClientWorldSave(false, false));
        Assert.True(DreamOutcomePolicy.IsFailureCleanup("rejected:prepare_failed"));
        Assert.True(DreamOutcomePolicy.IsFailureCleanup("disconnected"));
        Assert.False(DreamOutcomePolicy.ShouldMarkCompletedOnEnd("rejected:no_session"));
        Assert.True(DreamOutcomePolicy.ShouldMarkCompletedOnEnd("allDead"));
        Assert.True(DreamOutcomePolicy.IsNonRewardOutcome("allDead"));
        Assert.False(DreamOutcomePolicy.IsFailureCleanup("allDead"));
    }

    [Theory]
    [InlineData("giveItem", true)]
    [InlineData("removeItem", true)]
    [InlineData("giveJournalItem", false)]
    [InlineData("addJournalEntry", false)]
    [InlineData("worldFlag", false)]
    [InlineData("fireWorldEvent", false)]
    [InlineData("modifyReputation", false)]
    [InlineData("", false)]
    public void DialogPolicy_PersonalRewardClassification(string type, bool personal)
    {
        Assert.Equal(personal, DialogApplyPolicy.IsPersonalRewardType(type));
        Assert.True(DialogApplyPolicy.ShouldSuppressPersonalInventoryMutation(true));
        Assert.False(DialogApplyPolicy.ShouldSuppressPersonalInventoryMutation(false));
    }

    [Theory]
    [InlineData("giveJournalItem", true)]
    [InlineData("addJournalEntry", true)]
    [InlineData("giveItem", false)]
    [InlineData("modifyReputation", false)]
    public void DialogPolicy_WorldJournalClassification(string type, bool journal)
    {
        Assert.Equal(journal, DialogApplyPolicy.IsWorldJournalOutcomeType(type));
    }

    [Theory]
    [InlineData("worldFlag", true)]
    [InlineData("fireWorldEvent", true)]
    [InlineData("startDream", true)]
    [InlineData("endDream", true)]
    [InlineData("transportToOutsideLoc", false)]
    [InlineData("returnToWorld", false)]
    [InlineData("modifyReputation", true)]
    [InlineData("markOnMap", true)]
    [InlineData("enableDialogue", true)]
    [InlineData("setDontWantToTalk", true)]
    [InlineData("giveItem", false)]
    [InlineData("giveJournalItem", false)]
    [InlineData("cook", false)]
    public void DialogApplyPolicy_WorldAuthOutcomeTypes(string type, bool world)
    {
        Assert.Equal(world, DialogApplyPolicy.IsWorldAuthOutcomeType(type));
        Assert.True(DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false));
        Assert.False(DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, true)); // applying remote
        Assert.False(DialogApplyPolicy.ShouldDeferWorldOnClient(true, false, false)); // host
    }

    [Theory]
    [InlineData("transportToOutsideLoc", true)]
    [InlineData("returnToWorld", true)]
    [InlineData("startDream", false)]
    [InlineData("worldFlag", false)]
    public void DialogPolicy_TripsBelongToTheSpeaker(string type, bool trip)
    {
        Assert.Equal(trip, DialogApplyPolicy.IsSpeakerTripOutcomeType(type));
    }

    [Fact]
    public void DialogPolicy_TraderReputation_IsPerPlayer()
    {
        // NPC.name values from the vanilla data (every Character with isNightTrader: 1).
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("nightTrader"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("theThree"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("soldier_underground"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("NightTrader"));
        // Traders whose standing is only currency.
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("wolfman"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("piotrek"));
        // The Doctor's chapter 2 story state lives in his reputation: shared.
        Assert.False(DialogApplyPolicy.IsPerPlayerReputationNpcName("doctor"));
        Assert.False(DialogApplyPolicy.IsPerPlayerReputationNpcName("soldier"));
        Assert.False(DialogApplyPolicy.IsPerPlayerReputationNpcName(null));
        Assert.True(DialogApplyPolicy.ShouldDeferSharedReputation(isNightTrader: false));
        Assert.False(DialogApplyPolicy.ShouldDeferSharedReputation(isNightTrader: true));
        Assert.True(DialogApplyPolicy.ShouldSuppressCookOnHostRemoteApply(true));
        Assert.False(DialogApplyPolicy.ShouldSuppressCookOnHostRemoteApply(false));
    }

    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, false)]
    [InlineData(0, 2, false)]
    [InlineData(-1, 2, false)]
    public void GameEventPersonalPolicy_ActorOnlyOnApply(int actorId, int localId, bool run)
    {
        Assert.Equal(run, GameEventPersonalPolicy.ShouldRunPersonalEffectsOnApply(actorId, localId));
    }

    // Every Player function vanilla scenes call with targetUniqueObjects "player" (AssetRipper
    // export scan): world ones run on every peer, the rest only on the scene's own player.
    [Theory]
    [InlineData("tryToSpawnShadow", true)]
    [InlineData("pauseShadows", true)]
    [InlineData("unpauseShadows", true)]
    [InlineData("removeShadows", true)]
    [InlineData("shadowLightsTurnOff", true)]
    [InlineData("special_refreshWorldEvents", true)]
    [InlineData("special_hideDoctorsAct2", true)]
    [InlineData("special_teleportMaciek", true)]
    [InlineData("special_spawnDreamForestSpirit", true)]
    [InlineData("wolf_stealSister", true)]
    [InlineData("spawnWolfInCurrentHideout", true)]
    [InlineData("diveIn", false)]
    [InlineData("diveOut", false)]
    [InlineData("fakeDeathAni", false)]
    [InlineData("lieDown", false)]
    [InlineData("pauseAnimation", false)]
    [InlineData("resumeAnimation", false)]
    [InlineData("special_petDog", false)]
    [InlineData("special_drainAllTableLegDurability", false)]
    [InlineData("special_getUpFromBed", false)]
    [InlineData("special_changeClothes", false)]
    [InlineData("special_removeClothes", false)]
    [InlineData("special_addFlamethrower", false)]
    [InlineData(null, false)]
    public void StoryPolicy_WorldPlayerFunctions(string fn, bool world)
    {
        Assert.Equal(world, CoopStoryPolicy.IsWorldPlayerFunction(fn));
    }

    [Fact]
    public void LootPolicy_DisarmDouble_IsTypeScoped()
    {
        // Regression: the disarm double must only fire for the exact item disarmed.
        // A global "in progress" bool wrongly doubled any pickup arriving in-flight.
        Assert.True(LootPolicy.ShouldDoubleDisarm("gasoline", "gasoline"));
        // Different item arriving while a disarm is pending must NOT be doubled.
        Assert.False(LootPolicy.ShouldDoubleDisarm("gasoline", "nails"));
        // No item armed -> nothing doubles.
        Assert.False(LootPolicy.ShouldDoubleDisarm(null, "gasoline"));
        Assert.False(LootPolicy.ShouldDoubleDisarm("", "gasoline"));
    }

    [Fact]
    public void NpcLock_SameNpc_DifferentOwner_DeniedWhileHeld()
    {
        float now = 100f;
        float expire = 190f;
        // Per-slot API
        Assert.True(NpcDialogueLockPolicy.CanAcquireNpcSlot(-1, 0, 1, now));
        Assert.True(NpcDialogueLockPolicy.CanAcquireNpcSlot(1, expire, 1, now)); // renew
        Assert.False(NpcDialogueLockPolicy.CanAcquireNpcSlot(1, expire, 2, now)); // other owner
        Assert.True(NpcDialogueLockPolicy.CanAcquireNpcSlot(1, now - 1f, 2, now)); // expired

        Assert.True(NpcDialogueLockPolicy.IsNpcSlotHeldBy(1, expire, 1, now));
        Assert.False(NpcDialogueLockPolicy.IsNpcSlotHeldBy(1, expire, 2, now));
        Assert.False(NpcDialogueLockPolicy.IsNpcSlotHeldBy(1, now - 1f, 1, now)); // expired
    }

    [Fact]
    public void NpcLock_SameName_DifferentPlaces_AreDifferentNpcs()
    {
        // Two hideouts' ovens (chapter 1 data: big_hideout_03 vs med_cottage_tree_01 placed apart).
        Assert.False(NpcDialogueLockPolicy.IsSameNpc("oven", true, -208f, 176f, "oven", true, 2800f, -1500f));
        // Same oven, two peers' views (sync jitter).
        Assert.True(NpcDialogueLockPolicy.IsSameNpc("oven", true, -208f, 176f, "Oven", true, -205f, 180f));
        // A walking wolfman, still the same one.
        Assert.True(NpcDialogueLockPolicy.IsSameNpc("wolfman", true, 0f, 0f, "wolfman", true, 120f, 90f));
        // The dream-pad twin sits ~70000 away from the overworld one.
        Assert.False(NpcDialogueLockPolicy.IsSameNpc("door_underground", true, -75000f, 300f, "door_underground", true, -6342f, 300f));
        // No spot (older peer): the name decides.
        Assert.True(NpcDialogueLockPolicy.IsSameNpc("doctor", false, 0f, 0f, "doctor", true, 5000f, 5000f));
        Assert.False(NpcDialogueLockPolicy.IsSameNpc("doctor", false, 0f, 0f, "musician", false, 0f, 0f));
        Assert.False(NpcDialogueLockPolicy.IsSameNpc(null, true, 0f, 0f, "oven", true, 0f, 0f));
    }

    [Theory]
    [InlineData("player_inFirstHideout", true)]
    [InlineData("player_atDoctorHouse", true)]
    [InlineData("player_enteringRoadToHomeFromRadioTower", true)]
    [InlineData("player_shownMapPopup", true)]
    [InlineData("player_shownActiveSkillPopup", true)]
    [InlineData("player_firstActiveSkillObtained", true)]
    [InlineData("player_shownSecondaryAttackPopup", true)]
    [InlineData("player_firstDrainReloadableItem", true)]
    [InlineData("player_firstOvenInteraction", true)]
    [InlineData("player_survivedNight", true)]
    [InlineData("player_diedDuringNight", true)]
    [InlineData("player_diedAtLeastOneTime", true)]
    [InlineData("player_transportingFromCh1", false)]
    [InlineData("player_hadNightGift", false)]
    [InlineData("player_hadRadioNight", false)]
    [InlineData("player_hadVillageCellarDream", false)]
    [InlineData("player_unlockedHideout_2", false)]
    [InlineData("player_enoughWeirdEventsSeen", false)]
    [InlineData("player_returnedToRoomFromCrater", false)]
    [InlineData("doctor_killed", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void PerPlayerFlags_FromVanillaSettersAndReaders(string flag, bool perPlayer)
    {
        Assert.Equal(perPlayer, PerPlayerFlagPolicy.IsPerPlayer(flag));
    }

    [Fact]
    public void PerPlayerFlags_PersistedAreNotSpatial_AndChapter2SetIsPersisted()
    {
        foreach (string f in PerPlayerFlagPolicy.PersistedFlags)
        {
            Assert.True(PerPlayerFlagPolicy.IsPerPlayer(f));
            Assert.False(PerPlayerFlagPolicy.IsSpatial(f));
        }
        foreach (string f in PerPlayerFlagPolicy.Chapter2Flags)
            Assert.Contains(f, PerPlayerFlagPolicy.PersistedFlags);
        // Callers get copies; the policy lists cannot be edited from outside.
        PerPlayerFlagPolicy.PersistedFlags[0] = "x";
        Assert.NotEqual("x", PerPlayerFlagPolicy.PersistedFlags[0]);
    }

    [Fact]
    public void SurvivedNight_OnlyWhenNotDownThatNight()
    {
        Assert.True(PerPlayerFlagPolicy.SurvivedNight(nightDay: 4, localNightDeathDay: -1));
        Assert.True(PerPlayerFlagPolicy.SurvivedNight(nightDay: 4, localNightDeathDay: 3)); // an older night
        Assert.False(PerPlayerFlagPolicy.SurvivedNight(nightDay: 4, localNightDeathDay: 4));
    }

    [Fact]
    public void HostMigration_ElectsLowestSurvivorId()
    {
        Assert.Equal(2, HostMigrationPolicy.ElectNewHost(new[] { 5, 2, 9 }));
        Assert.Equal(3, HostMigrationPolicy.ElectNewHost(new[] { 3 }));
        Assert.Equal(-1, HostMigrationPolicy.ElectNewHost(Array.Empty<int>()));
        Assert.Equal(-1, HostMigrationPolicy.ElectNewHost(new[] { 0, -1 }));
        Assert.True(HostMigrationPolicy.IsLocalElected(2, 2));
        Assert.False(HostMigrationPolicy.IsLocalElected(3, 2));
        Assert.True(HostMigrationPolicy.ShouldAttemptMigration(
            featureEnabled: true, isClient: true, mainMenu: false, hasPlayableWorld: true, migrationAlreadyRunning: false));
        Assert.False(HostMigrationPolicy.ShouldAttemptMigration(
            featureEnabled: true, isClient: true, mainMenu: true, hasPlayableWorld: true, migrationAlreadyRunning: false));
        Assert.False(HostMigrationPolicy.ShouldAttemptMigration(
            featureEnabled: false, isClient: true, mainMenu: false, hasPlayableWorld: true, migrationAlreadyRunning: false));
    }

    [Fact]
    public void NightDeath_Partial_SuppressesWorldMutations()
    {
        Assert.True(NightDeathPolicy.ShouldSuppressWorldDeathMutations(true, true, false));
        Assert.False(NightDeathPolicy.ShouldSuppressWorldDeathMutations(true, true, true)); // all dead
        Assert.False(NightDeathPolicy.ShouldSuppressWorldDeathMutations(true, false, false));
        Assert.False(NightDeathPolicy.ShouldSuppressWorldDeathMutations(false, true, false));
    }

    [Fact]
    public void NightDeath_DisconnectPolicy_AliveLeaverNoRemotes_Resolves()
    {
        // Host night-dead, last living peer leaves: the host is a lone dead player and
        // must resolve the morning like vanilla solo death instead of spectating nobody.
        Assert.True(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: true,
            leaverWasNightDead: false,
            remainingRemoteCount: 0,
            remainingRemoteDeadCount: 0));
    }

    [Fact]
    public void NightDeath_DisconnectPolicy_AliveLeaverRemainingAllDead_Resolves()
    {
        Assert.True(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: true,
            leaverWasNightDead: false,
            remainingRemoteCount: 2,
            remainingRemoteDeadCount: 2));
    }

    [Fact]
    public void NightDeath_DisconnectPolicy_DeadLeaverNoRemotes_Resolves()
    {
        Assert.True(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: true,
            leaverWasNightDead: true,
            remainingRemoteCount: 0,
            remainingRemoteDeadCount: 0));
    }

    [Fact]
    public void NightDeath_DisconnectPolicy_RemainingNotAllDead_DoesNotResolve()
    {
        Assert.False(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: true,
            leaverWasNightDead: false,
            remainingRemoteCount: 2,
            remainingRemoteDeadCount: 1));
    }

    [Fact]
    public void NightDeath_DisconnectPolicy_LocalNotNightDead_DoesNotResolve()
    {
        Assert.False(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: false,
            leaverWasNightDead: true,
            remainingRemoteCount: 0,
            remainingRemoteDeadCount: 0));
        Assert.False(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: false,
            leaverWasNightDead: false,
            remainingRemoteCount: 1,
            remainingRemoteDeadCount: 1));
    }

    [Fact]
    public void ChapterSession_CreditsPermanent_ChapterResumes()
    {
        Assert.True(ChapterSessionPolicy.ShouldAutoResumeNetworkAfterChapter);
        Assert.True(ChapterSessionPolicy.ShouldStopNetworkPermanently("credits"));
        Assert.False(ChapterSessionPolicy.ShouldStopNetworkPermanently("chapter2"));
        Assert.False(ChapterSessionPolicy.ShouldStopNetworkPermanently("chapter1"));
    }

    [Fact]
    public void WorldShare_FailureText_IsLoud()
    {
        string msg = WorldSharePolicy.FormatShareFailure("no files");
        Assert.Contains("WORLD SHARE FAILED", msg);
        Assert.Contains("no files", msg);
        Assert.Contains("different forests", msg);
        Assert.True(WorldSharePolicy.IsShareFailureTerminal);
        Assert.True(WorldSharePolicy.IsShareFailureMessage(msg));
        Assert.False(WorldSharePolicy.IsShareFailureMessage("Receiving host world 50%"));
    }

    [Fact]
    public void WorldShare_WrongSaveText_IsDistinctFromShareFailure()
    {
        string msg = WorldSharePolicy.FormatWrongSave("campaign mismatch");
        Assert.StartsWith("WRONG SAVE:", msg);
        Assert.Contains("campaign mismatch", msg);
        Assert.True(WorldSharePolicy.IsWrongSaveMessage(msg));
        Assert.True(WorldSharePolicy.IsWrongSaveMessage("prefix WRONG SAVE: slot"));
        Assert.False(WorldSharePolicy.IsWrongSaveMessage("Receiving host world 50%"));
        Assert.False(WorldSharePolicy.IsShareFailureMessage(msg));
        Assert.False(WorldSharePolicy.IsWrongSaveMessage(
            WorldSharePolicy.FormatShareFailure("no files")));
    }

    [Fact]
    public void WorldPresence_KeepsRemoteBubbleEvenOnForceLeave()
    {
        Assert.True(CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true, true));
        Assert.False(CoopWorldPresencePolicy.ShouldKeepNodeForRemote(true, false));
        Assert.False(CoopWorldPresencePolicy.ShouldKeepNodeForRemote(false, true));
        Assert.True(CoopWorldPresencePolicy.ShouldKeepLocationForRemote(true, true));
        Assert.False(CoopWorldPresencePolicy.ShouldKeepLocationForRemote(true, false));
        Assert.True(CoopWorldPresencePolicy.LocationNamesMatch(
            "outside_doctor_house_01", "outside_doctor_house_01_done"));
        Assert.False(CoopWorldPresencePolicy.LocationNamesMatch(
            "outside_doctor_house_01", "outside_bunker_ch1_01"));
        Assert.False(CoopWorldPresencePolicy.ShouldSnapRemoteProxyOnLocalWorldReturn(true));
        Assert.True(CoopWorldPresencePolicy.ShouldSnapRemoteProxyOnLocalWorldReturn(false));
    }

    [Fact]
    public void NightDeath_SessionRemoteCount_UsesMaxOfProxyAndHandshake()
    {
        Assert.Equal(2, NightDeathPolicy.SessionRemoteCount(proxyCount: 1, handshakedRemoteCount: 2));
        Assert.Equal(2, NightDeathPolicy.SessionRemoteCount(proxyCount: 2, handshakedRemoteCount: 1));
        Assert.Equal(0, NightDeathPolicy.SessionRemoteCount(proxyCount: 0, handshakedRemoteCount: 0));
    }

    [Fact]
    public void SnapshotSequence_RejectsDuplicatesAndOlderPackets()
    {
        Assert.True(SnapshotSequencePolicy.IsNewer(10, 0, hasLast: false));
        Assert.True(SnapshotSequencePolicy.IsNewer(11, 10, hasLast: true));
        Assert.False(SnapshotSequencePolicy.IsNewer(10, 10, hasLast: true));
        Assert.False(SnapshotSequencePolicy.IsNewer(9, 10, hasLast: true));
    }

    [Fact]
    public void SnapshotSequence_HandlesUnsignedWraparound()
    {
        Assert.True(SnapshotSequencePolicy.IsNewer(1, uint.MaxValue, hasLast: true));
        Assert.False(SnapshotSequencePolicy.IsNewer(uint.MaxValue, 1, hasLast: true));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void CombatAuthority_ValidatesPlayerIdentity(int playerId, bool valid)
    {
        Assert.Equal(valid, CombatAuthorityPolicy.IsValidPlayerId(playerId));
    }

    [Fact]
    public void CombatAuthority_RejectsMalformedPositionsAndUnknownActions()
    {
        Assert.False(CombatAuthorityPolicy.IsFinitePosition(float.NaN, 0f, 0f));
        Assert.False(CombatAuthorityPolicy.IsFinitePosition(0f, float.PositiveInfinity, 0f));
        Assert.True(CombatAuthorityPolicy.IsValidMeleeTargetType(2));
        Assert.False(CombatAuthorityPolicy.IsValidMeleeTargetType(3));
        Assert.True(CombatAuthorityPolicy.IsWithinRange(0f, 0f, 0f, 3f, 0f, 4f, 5f));
        Assert.False(CombatAuthorityPolicy.IsWithinRange(0f, 0f, 0f, 6f, 0f, 0f, 5f));
    }

    [Fact]
    public void ComponentGuards_DoNotRequireCharacterLookup()
    {
        Assert.True(AiSuppressionPolicy.ShouldSuppressClientComponent(
            isClient: true, isRemotePlayer: false, isLocalPlayer: false));
        Assert.False(AiSuppressionPolicy.ShouldSuppressClientComponent(
            isClient: true, isRemotePlayer: true, isLocalPlayer: false));
        Assert.False(AiSuppressionPolicy.ShouldSuppressClientComponent(
            isClient: false, isRemotePlayer: false, isLocalPlayer: false));
    }

    [Fact]
    public void AttackBudget_AllowsBurst_CapsSustainedRate()
    {
        float atk = 4f, dmg = 400f;
        // Burst within the bucket.
        for (int i = 0; i < 4; i++)
            Assert.True(CombatAuthorityPolicy.TryConsumeAttackBudget(
                ref atk, ref dmg, 0f, 100, 2f, 4f, 200f, 400f));
        // Empty: no time passed.
        Assert.False(CombatAuthorityPolicy.TryConsumeAttackBudget(
            ref atk, ref dmg, 0f, 1, 2f, 4f, 200f, 400f));
        // Half a second refills one attack and 100 damage.
        Assert.True(CombatAuthorityPolicy.TryConsumeAttackBudget(
            ref atk, ref dmg, 0.5f, 100, 2f, 4f, 200f, 400f));
        Assert.False(CombatAuthorityPolicy.TryConsumeAttackBudget(
            ref atk, ref dmg, 0f, 1, 2f, 4f, 200f, 400f));
        // Damage short: rejected without taking an attack token.
        float a2 = 4f, d2 = 50f;
        Assert.False(CombatAuthorityPolicy.TryConsumeAttackBudget(
            ref a2, ref d2, 0f, 100, 2f, 4f, 200f, 400f));
        Assert.Equal(4f, a2);
        // Refill never exceeds the burst size.
        float a3 = 0f, d3 = 0f;
        Assert.True(CombatAuthorityPolicy.TryConsumeAttackBudget(
            ref a3, ref d3, 1000f, 400, 2f, 4f, 200f, 400f));
        Assert.Equal(3f, a3);
        Assert.Equal(0f, d3);
    }

    [Fact]
    public void ImpactFx_AllowsOnlySenderPrefabs()
    {
        Assert.True(ImpactFxPolicy.IsAllowedBulletImpact("FX", "bullet_hit_1"));
        Assert.True(ImpactFxPolicy.IsAllowedBulletImpact("FX", "Shotsplat1"));
        Assert.True(ImpactFxPolicy.IsAllowedBulletImpact("", "FX/Bloodsplats/Shotsplat_stay"));
        Assert.True(ImpactFxPolicy.IsAllowedBulletImpact(null, "FX/Bloodsplats/Shotsplat"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("FX", "Explosion"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("Characters", "bullet_hit_1"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("", "Characters/Wolfman"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("", "FX/Bloodsplats/../../Characters/Dog"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("", "FX/Bloodsplats/"));
        Assert.False(ImpactFxPolicy.IsAllowedBulletImpact("", ""));
    }
}
