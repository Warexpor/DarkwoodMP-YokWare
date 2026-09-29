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
    public void TimePolicy_ClientConnected_SuppressesClock()
    {
        // Also gates Controller.useTimeSkip on connected clients (beds / wait-until-evening).
        Assert.True(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: true));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: false));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: false, isClient: true));
        Assert.True(CoopTimePolicy.ShouldUseRefreshTimeNoLogicOnClientSync);
        Assert.True(CoopTimePolicy.LiveStepCrossedMinute(1068, 1071, 1070));
        Assert.False(CoopTimePolicy.LiveStepCrossedMinute(100, 500, 1070));
        Assert.True(CoopTimePolicy.LiveStepCrossedMinute(1438, 2, 0));
        Assert.True(CoopTimePolicy.ShouldClearFedToday(100, 102, 1, 2, 480));
        Assert.False(CoopTimePolicy.ShouldClearFedToday(100, 102, 2, 2, 480));
        Assert.False(PartyRequirementPolicy.HaveItem(partyHas: true, activeModifier: false));
        Assert.True(PartyRequirementPolicy.HaveItem(partyHas: false, activeModifier: false));
        Assert.True(PartyRequirementPolicy.HaveItem(partyHas: true, activeModifier: true));
        Assert.True(PermadeathPolicy.ClientUsesSharedDeath(true, PermadeathPolicy.Nightmare, 3));
        Assert.True(PermadeathPolicy.ClientUsesSharedDeath(true, PermadeathPolicy.Hard, 1));
        Assert.False(PermadeathPolicy.ClientUsesSharedDeath(true, PermadeathPolicy.Hard, 2));
        Assert.False(PermadeathPolicy.ClientUsesSharedDeath(false, PermadeathPolicy.Nightmare, 0));
        Assert.False(PermadeathPolicy.ClientUsesSharedDeath(true, PermadeathPolicy.Normal, 0));
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
    [InlineData("transportToOutsideLoc", true)]
    [InlineData("returnToWorld", true)]
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

    [Fact]
    public void DialogPolicy_NightTraderReputation_IsPerPlayer()
    {
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("NightTrader"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("TheThree"));
        Assert.True(DialogApplyPolicy.IsPerPlayerReputationNpcName("NightTrader(Clone)"));
        Assert.False(DialogApplyPolicy.IsPerPlayerReputationNpcName("wolfman"));
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

        // Legacy helper: different NPC does not block
        Assert.True(NpcDialogueLockPolicy.CanAcquire("wolfman", 1, expire, "doctor", 2, now));
        Assert.False(NpcDialogueLockPolicy.CanAcquire("wolfman", 1, expire, "wolfman", 2, now));
    }

    [Fact]
    public void NpcLock_MultiNpc_ParallelHolds_SameNpcStillDenied()
    {
        // Simulates Dictionary multi-NPC: P1 wolfman + P2 doctor both held;
        // P2 must NOT steal wolfman (single-slot overwrite bug).
        var owners = new Dictionary<string, int>(StringComparer.Ordinal);
        var expires = new Dictionary<string, float>(StringComparer.Ordinal);
        float now = 50f;

        Assert.True(NpcDialogueLockPolicy.SimulateMultiNpcAcquire(owners, expires, "wolfman", 1, now));
        Assert.True(NpcDialogueLockPolicy.SimulateMultiNpcAcquire(owners, expires, "doctor", 2, now));
        Assert.Equal(2, owners.Count);
        Assert.Equal(1, owners["wolfman"]);
        Assert.Equal(2, owners["doctor"]);

        // P2 tries wolfman while P1 still holds it; deny and leave the map unchanged.
        Assert.False(NpcDialogueLockPolicy.SimulateMultiNpcAcquire(owners, expires, "wolfman", 2, now));
        Assert.Equal(1, owners["wolfman"]);
        Assert.Equal(2, owners["doctor"]);

        // P1 renews wolfman successfully.
        Assert.True(NpcDialogueLockPolicy.SimulateMultiNpcAcquire(owners, expires, "wolfman", 1, now));
        Assert.Equal(1, owners["wolfman"]);
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
    public void NightDeath_DisconnectPolicy_AliveLeaverNoRemotes_DoesNotResolve()
    {
        Assert.False(NightDeathPolicy.ShouldResolveMorningOnDisconnect(
            localNightDead: true,
            leaverWasNightDead: false,
            remainingRemoteCount: 0,
            remainingRemoteDeadCount: 0));
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
    public void DreamResolution_DisablesGlobalNamesDuringDream()
    {
        Assert.False(DreamResolutionPolicy.CanUseGlobalNameFallback(dreamActive: true));
        Assert.True(DreamResolutionPolicy.CanUseGlobalNameFallback(dreamActive: false));
    }
}
