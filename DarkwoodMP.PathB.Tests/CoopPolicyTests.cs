using DWMPHorde;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Unit tests drive shipped pure policy helpers (no Unity).
/// Structural tests in AuditStructureTests pin the Harmony/handler wiring.
/// </summary>
public class CoopPolicyTests
{
    [Fact]
    public void TimePolicy_ClientConnected_SuppressesClock()
    {
        Assert.True(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: true));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: true, isClient: false));
        Assert.False(CoopTimePolicy.ShouldSuppressClientClock(isConnected: false, isClient: true));
        Assert.True(CoopTimePolicy.ShouldUseRefreshTimeNoLogicOnClientSync);
    }

    [Theory]
    [InlineData("giveItem", true)]
    [InlineData("removeItem", true)]
    [InlineData("giveJournalItem", true)]
    [InlineData("addJournalEntry", true)]
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

        // P2 tries wolfman while P1 still holds it — deny; map unchanged.
        Assert.False(NpcDialogueLockPolicy.SimulateMultiNpcAcquire(owners, expires, "wolfman", 2, now));
        Assert.Equal(1, owners["wolfman"]);
        Assert.Equal(2, owners["doctor"]);

        // P1 renews wolfman — ok
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
    }
}
