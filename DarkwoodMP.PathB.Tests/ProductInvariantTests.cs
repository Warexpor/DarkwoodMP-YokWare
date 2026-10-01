using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Thin product gates for Path B. Prefer real unit tests for behavior;
/// these only lock high-signal ship invariants that must not quietly regress.
/// </summary>
public class ProductInvariantTests
{
    private static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DarkwoodMP.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate repo root (DarkwoodMP.sln).");
        }
    }

    private static string ModDir => Path.Combine(RepoRoot, "DarkwoodMP.Mod");

    [Fact]
    public void SharedInvItemUpgrades_WireTrailerPresent()
    {
        // Batch 30: death-bag / drop / container carry workbench upgrade names.
        var wire = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "InvItemUpgradeWire.cs"));
        Assert.Contains("InvItemUpgradeWire", wire);
        Assert.Contains("TryReadMany", wire);

        var drop = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "WorldMessages.cs"));
        Assert.Contains("DroppedItemSpawnMessage", drop);
        Assert.Contains("InvItemUpgradeWire.Write", drop);
        Assert.Contains("ItemUpgrades", drop);

        var cont = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "ContainerMessages.cs"));
        Assert.Contains("InvItemUpgradeWire.Write", cont);
        Assert.Contains("public string[] Upgrades", cont);
    }

    [Fact]
    public void SharedInvItemShouldBeActive_AndTransferApplyPresent()
    {
        // Batch 31: flashlight on + empty-mag/0-dur apply after createItem.
        var apply = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "InvItemTransferApply.cs"));
        Assert.Contains("ApplyMeta", apply);
        Assert.Contains("shouldBeActive", apply);
        Assert.Contains("hasAmmo", apply);

        var drop = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "WorldMessages.cs"));
        Assert.Contains("ShouldBeActive", drop);
        Assert.Contains("msg.ShouldBeActive = r.GetBool()", drop);

        var cont = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "ContainerMessages.cs"));
        Assert.Contains("ShouldBeActive", cont);
        Assert.Contains("Slots[i].ShouldBeActive", cont);

        var droppedRx = File.ReadAllText(Path.Combine(ModDir, "Domains", "Players", "PlayerFXNetHandlers.DroppedItems.cs"));
        Assert.Contains("InvItemTransferApply.ApplyMeta", droppedRx);
    }

    [Fact]
    public void TradeInventorySync_EmptyMagAndZeroDurParity()
    {
        // Batch 32: TradeInventorySync keeps ammo=0 firearms and assigns abs dur 0.
        var trade = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "TradeSyncPatches.cs"));
        Assert.Contains("hasAmmo", trade);
        Assert.Contains("!hasAmmo) continue", trade);
        Assert.Contains("InvItemTransferApply.ApplyMeta", trade);
        Assert.Contains("created.durability = absDur", trade);
        Assert.Contains("created.ammo = amount", trade);
        Assert.DoesNotContain("if (absDur > 0f)", trade);
    }

    [Fact]
    public void TradeInventorySync_UpgradesAndShouldBeActiveTrailer()
    {
        // Batch 33: player-sold upgraded / flashlight-on stock on TradeInventorySync.
        var msg = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "SyncMessages.cs"));
        Assert.Contains("public struct TradeInventorySyncMessage", msg);
        Assert.Contains("public string[][] Upgrades", msg);
        Assert.Contains("public bool[] ShouldBeActive", msg);
        Assert.Contains("InvItemUpgradeWire.Write", msg);
        Assert.Contains("InvItemUpgradeWire.TryRead", msg);

        var trade = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "TradeSyncPatches.cs"));
        Assert.Contains("InvItemUpgradeWire.CollectNames", trade);
        Assert.Contains("InvItemUpgradeWire.Apply", trade);
        Assert.Contains("shouldBeActive", trade);
    }

    [Fact]
    public void PorterWhistle_UsesItemSpawnSentinel_NoNewMessageType()
    {
        // 0.8.102: client Location.spawnPorter → ItemSpawn "porterWhistle" → host
        // Events/porterSpawner. Must not add a NetMessageType (protocol 25).
        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "PorterSpawnerSyncPatches.cs"));
        Assert.Contains("PorterWhistleItemSpawnType", patch);
        Assert.Contains("LocationSpawnPorterClientPatch", patch);
        Assert.Contains("TryApplyPorterWhistleOnHost", patch);
        Assert.Contains("SendItemSpawn", patch);

        var handlers = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "WorldPhysicsNetHandlers.cs"));
        Assert.Contains("PorterWhistleItemSpawnType", handlers);
        Assert.Contains("TryApplyPorterWhistleOnHost", handlers);

        var netTypes = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "NetMessageType.cs"));
        Assert.DoesNotContain("PorterWhistle", netTypes);
        Assert.Contains("_Highest = 146", netTypes);
    }

    [Fact]
    public void ClientGasIgnite_ReusesGasIgnite_NoNewMessageType()
    {
        // 0.8.104: client Liquid.startBurning → GasIgnite → host IgniteGasAtPos.
        // Must not add a NetMessageType (protocol 25).
        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "Doors", "Patches", "GasolineSyncPatches.cs"));
        Assert.Contains("GasIgnitePatch", patch);
        Assert.Contains("SendGasIgnite", patch);
        Assert.Contains("IgniteGasAtPos", patch);
        Assert.Contains("ClientMustNotMutateWorld", patch);

        var handlers = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "CombatFxGasBurnNetHandlers.cs"));
        Assert.Contains("host adopted client ignite", handlers);
        Assert.Contains("MaxPlayerAttackRange", handlers);

        var netTypes = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "NetMessageType.cs"));
        Assert.Contains("GasIgnite = 29", netTypes);
        Assert.Contains("_Highest = 146", netTypes);
    }

    [Fact]
    public void ClientInfectionDisappear_ReusesWorldObjectRemoved_NoNewMessageType()
    {
        // 0.8.105: client Infection.disappear → WorldObjectRemoved infection_splat.
        // Disappear must not Host-gate (client flaming melee clears locally otherwise).
        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "Players", "Patches", "InfectionStatusSyncPatches.cs"));
        Assert.Contains("InfectionDisappearPatch", patch);
        Assert.Contains("SendWorldObjectRemoved", patch);
        Assert.Contains("infection_splat", patch);
        Assert.Contains("client→host", patch);

        int disappearIdx = patch.IndexOf("InfectionDisappearPatch", StringComparison.Ordinal);
        Assert.True(disappearIdx >= 0);
        string disappearBody = patch.Substring(disappearIdx);
        int nextClass = disappearBody.IndexOf("public static class ", 1, StringComparison.Ordinal);
        if (nextClass > 0)
            disappearBody = disappearBody.Substring(0, nextClass);
        Assert.DoesNotContain("Role != NetworkRole.Host) return", disappearBody);

        var netTypes = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "NetMessageType.cs"));
        Assert.Contains("WorldObjectRemoved = 23", netTypes);
        Assert.Contains("_Highest = 146", netTypes);
    }

    [Fact]
    public void ReputationSync_CarriesAttackedIdTrailer()
    {
        // 0.8.93: host NPCState.attackedID on live + bulk (wolfman death / onlyOneInstance).
        var msg = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "SyncMessages.cs"));
        Assert.Contains("HasAttackedId", msg);
        Assert.Contains("AttackedIds", msg);
        Assert.Contains("hasAttackedIdTrailer", msg);

        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "NpcAttackedIdSync.cs"));
        Assert.Contains("BroadcastFromHost", patch);
        Assert.Contains("RemapAttackedIdIfNeeded", patch);
    }

    [Fact]
    public void ReputationSync_CarriesDeadIdTrailer()
    {
        // 0.8.94: host NPCState.dead + deadID on live + bulk (wolfman / onlyOneInstance / npcStateIsDead).
        var msg = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "SyncMessages.cs"));
        Assert.Contains("HasDead", msg);
        Assert.Contains("DeadIds", msg);
        Assert.Contains("hasDeadIdTrailer", msg);

        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "NpcAttackedIdSync.cs"));
        Assert.Contains("ApplyDead", patch);
        Assert.Contains("RemapDeadIdIfNeeded", patch);
        Assert.Contains("NpcDeadStateHostFanPatch", patch);
    }

    [Fact]
    public void GameEventReputation_FansReputationSyncFromHost()
    {
        // 0.8.95: CharacterModify.reputation writes Flags directly (bypasses set_reputation).
        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "ReputationSyncPatch.cs"));
        Assert.Contains("GameEventReputationHostFanPatch", patch);
        Assert.Contains("CharacterModify.reputation", patch);
        Assert.Contains("BroadcastFromHost", patch);
        Assert.Contains("hasDead: true", patch);
    }

    [Fact]
    public void ReputationSync_CarriesPortraitTrailer()
    {
        // 0.8.96: GameEvent CharacterModify.portraitType → live ReputationSync trailer.
        // 0.8.115: same fields on late-join ReputationBulkSync.
        var msg = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "SyncMessages.cs"));
        Assert.Contains("HasPortrait", msg);
        Assert.Contains("ApplyDialoguePortrait", msg);
        Assert.Contains("PortraitType", msg);
        Assert.Contains("hasPortraitTrailer", msg);

        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "GameEventPortraitHostFanPatch.cs"));
        Assert.Contains("GameEventPortraitHostFanPatch", patch);
        Assert.Contains("CharacterModify.portraitType", patch);
        Assert.Contains("hasPortrait: true", patch);

        var apply = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "NpcAttackedIdSync.cs"));
        var visuals = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "NpcAttackedIdSync.Visuals.cs"));
        Assert.Contains("ApplyPortrait", visuals);
        Assert.Contains("FindNpcByNameNear", visuals);
        Assert.Contains("FillBulkVisualTrailers", visuals);
        Assert.Contains("FlushPendingVisualsIfNeeded", visuals);
        Assert.Contains("BroadcastFromHost", apply);
    }

    [Fact]
    public void ReputationSync_CarriesAnimLibraryTrailer()
    {
        // 0.8.97: GameEvent CharacterModify.animationLibraryOverride → live ReputationSync trailer.
        // 0.8.115: same fields on late-join ReputationBulkSync.
        var msg = File.ReadAllText(Path.Combine(ModDir, "Networking", "Messages", "SyncMessages.cs"));
        Assert.Contains("HasAnimLibrary", msg);
        Assert.Contains("AnimLibraryName", msg);
        Assert.Contains("hasAnimLibraryTrailer", msg);

        var patch = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "GameEventAnimLibraryHostFanPatch.cs"));
        Assert.Contains("GameEventAnimLibraryHostFanPatch", patch);
        Assert.Contains("CharacterModify.animationLibraryOverride", patch);
        Assert.Contains("hasAnimLibrary: true", patch);

        var visuals = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "NpcAttackedIdSync.Visuals.cs"));
        Assert.Contains("ApplyAnimLibrary", visuals);
        Assert.Contains("animationLibraryOverride", visuals);
        Assert.Contains("FindNpcByNameNear", visuals);
        Assert.Contains("ApplyBulkVisualTrailers", visuals);
    }

    [Fact]
    public void PluginInfo_IsYokWarePathB()
    {
        var text = File.ReadAllText(Path.Combine(ModDir, "Bootstrap", "PluginInfo.cs"));
        Assert.Contains("com.yokware.branch", text);
        Assert.Contains("YokWare Branch", text);
        Assert.Contains("ProtocolVersion = 27", text);
        Assert.Contains("Horde", text);

        var versionMatch = Regex.Match(text, @"Version\s*=\s*""(0\.8\.[^""]+)""");
        Assert.True(versionMatch.Success, "PluginInfo.Version must be 0.8.x");
    }


    [Fact]
    public void HarmonyFlagStash_UsesFinalizerRestore()
    {
        // Batch 37/38: Prefix mutates / sets sticky flags; Postfix-only restore leaves
        // bad state when the original throws (Harmony skips Postfix).
        var checkStuff = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "HostAIPatches.Perception.cs"));
        Assert.Contains("HostCheckStuffPatch", checkStuff);
        Assert.Contains("private static void Finalizer(Character __instance)", checkStuff);
        Assert.Contains("temporarySpawned", checkStuff);

        var projectile = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "ClientProjectileDamagePatch.cs"));
        Assert.Contains("IsInsidePlayerBulletCollision = false", projectile);
        Assert.Contains("Finalizer", projectile);

        var sounds = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "EntitySoundSyncPatches.cs"));
        Assert.Contains("[HarmonyFinalizer]", sounds);
        Assert.Contains("InsideEscapingLoop = false", sounds);
        Assert.Contains("InsideCharacterSounds = false", sounds);

        // Batch 38
        var explode = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "ExplosionSpawnSyncPatches.cs"));
        Assert.Contains("[HarmonyFinalizer]", explode);
        Assert.Contains("IsInsideSpawnObjects = false", explode);
        Assert.Contains("IsInsideLocalExplosion = false", explode);

        var fastProj = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "FastProjectileAwakePatch.cs"));
        Assert.Contains("IsInsideFastProjectileRaycast = false", fastProj);
        Assert.Contains("Finalizer", fastProj);

        var banshee = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "HostAIPatches.Targeting.cs"));
        Assert.Contains("SuppressHostScreamForward = false", banshee);
        Assert.Contains("private static void Finalizer()", banshee);

        var trap = File.ReadAllText(Path.Combine(ModDir, "Domains", "Doors", "DoorSyncPatches.cs"));
        // The Finalizer restores InsideTrapPlacement (to false, or to the nested caller's prior value).
        Assert.Matches(@"static void Finalizer\([^)]*\)\s*\{\s*InsideTrapPlacement = ", trap);

        var pause = File.ReadAllText(Path.Combine(ModDir, "Patches", "NoWorldPausePatch.cs"));
        Assert.Contains("UiOpenNoPausePatches", pause);
        Assert.Contains("Finalizer() => PauseSuppression.EndNoPause()", pause);
        Assert.Contains("Finalizer() => PauseSuppression.EndNoUnpause()", pause);

        // Batch 41 / 0.8.72: Door/Window getHit BeginGetHit + stash — End only in
        // Postfix left sticky IsInsideGetHit (suppress destroyBarricade forever).
        var barr = File.ReadAllText(Path.Combine(ModDir, "Domains", "Doors", "Patches", "BarricadeSyncPatches.cs"));
        Assert.Contains("DoorGetHitPatch", barr);
        Assert.Contains("WindowGetHitPatch", barr);
        Assert.Contains("_getHitDepth", barr);
        Assert.Contains("BarricadeSyncHelpers.EndGetHit(", barr);
        Assert.Contains("[HarmonyFinalizer]", barr);
        Assert.Contains("private static void Finalizer(Door __instance)", barr);
        Assert.Contains("private static void Finalizer(Window __instance)", barr);

        // Batch 41 / 0.8.73: Player/Core.displayMessage Postfix-hide (not Prefix-null).
        var flavor = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "ExaminableSyncPatches.cs"));
        Assert.Contains("HideCharacterMessage", flavor);
        Assert.Contains("ExaminableHostHudSuppressPatch", flavor);
        Assert.Contains("PersonalFlavorHud.HideCharacterMessage(__result)", flavor);
        Assert.Contains("private static void Postfix(CharacterMessage __result)", flavor);

        // Batch 44 / 0.8.74: DialogClientWorldDefer + pickup guards — End only in
        // Postfix left sticky Active / WireGuard / TrapPickupGuard on throw.
        // displayNextBoard has exactly one patch class; defer End / drain scope / forbidInputs
        // clear all run in its Finalizer.
        var defer = File.ReadAllText(Path.Combine(ModDir, "Domains", "Dialogue", "Patches", "DialogDisplayNextBoardPatch.cs"));
        Assert.Contains("DialogDisplayNextBoardPatch", defer);
        Assert.Contains("[HarmonyFinalizer]", defer);
        Assert.Contains("DialogClientWorldDefer.End()", defer);
        Assert.Matches(@"static void Finalizer\([^)]*\)", defer);

        var pickup = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "DroppedItemSyncPatches.cs"));
        Assert.Contains("PlayerPickupDroppedItemPatch", pickup);
        Assert.Contains("[HarmonyFinalizer]", pickup);
        Assert.Contains("private static void Finalizer(Item __instance, PrefixState __state)", pickup);
        Assert.Contains("TrapPickupGuard.End(__instance)", pickup);
        Assert.Contains("WorldPickupWireGuard.End()", pickup);

        // Batch 45 / 0.8.75: SilentDisarmDepth + host forbidInputs — End/clear only in
        // Postfix left sticky IsSilentDisarm / unable-to-walk on throw.
        var disarm = File.ReadAllText(Path.Combine(ModDir, "Domains", "Doors", "Patches", "TrapDisarmHarvestSync.cs"));
        Assert.Contains("ItemDisarmSilentTrapPatch", disarm);
        Assert.Contains("[HarmonyFinalizer]", disarm);
        Assert.Contains("private static void Finalizer()", disarm);
        Assert.Contains("SilentDisarmDepth--", disarm);

        Assert.Contains("private static void Finalizer(DialogueWindow __instance, BoardState __state)", defer);
        Assert.Contains("Core.forbidInputs = false", defer);
        Assert.Contains("DialogHostApplyGuard.EndDrainScope(", defer);
        var dialoguePatches = Directory.GetFiles(Path.Combine(ModDir, "Domains", "Dialogue", "Patches"), "*.cs");
        int nextBoardHooks = 0;
        foreach (var f in dialoguePatches)
            nextBoardHooks += System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(f), @"HarmonyPatch\(typeof\(DialogueWindow\), ""displayNextBoard""\)").Count;
        Assert.Equal(1, nextBoardHooks);

        // Batch 46 / 0.8.76: loot-share disarm arm + trap-place pending — clear only in
        // Postfix left sticky _disarmType / _pendingShares / _pendingType on throw.
        var lootShare = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "ItemDoublePickupPatch.cs"));
        Assert.Contains("OnDisarmFinalizer", lootShare);
        Assert.Contains("[HarmonyFinalizer]", lootShare);
        Assert.Contains("_disarmType = null", lootShare);
        Assert.Contains("OnTransferAllToPlayerFinalizer", lootShare);
        Assert.Contains("OnGrabItemFinalizer", lootShare);
        Assert.Contains("OnTransferToPlayerFinalizer", lootShare);
        Assert.Contains("_pendingShares.Remove(__instance)", lootShare);

        var trapPlace = File.ReadAllText(Path.Combine(ModDir, "Domains", "Doors", "DoorSyncPatches.cs"));
        Assert.Contains("TrapPlacementPatch", trapPlace);
        // Placement capture is cleared in the Finalizer (statics) or travels in __state (re-entrancy-safe).
        Assert.True(trapPlace.Contains("_pendingType = null") || trapPlace.Contains("PrevInside"),
            "TrapPlacementPatch must not leave placement state behind when progressBarCompleted throws");
        Assert.Matches(@"static void Finalizer\([^)]*\)\s*\{\s*InsideTrapPlacement = ", trapPlace);
    }

    [Fact]
    public void HarmonyCoroutineSuppress_SetsEmptyResult()
    {
        // Batch 36: Prefix return false on IEnumerator without __result → StartCoroutine(null).
        var util = File.ReadAllText(Path.Combine(ModDir, "Harmony", "HarmonyCoroutineUtil.cs"));
        Assert.Contains("HarmonyCoroutineUtil", util);
        Assert.Contains("Empty()", util);

        var prepare = File.ReadAllText(Path.Combine(ModDir, "Domains", "Dream", "Patches", "DreamSyncPatches.cs"));
        Assert.Contains("ref System.Collections.IEnumerator __result", prepare);
        Assert.Contains("HarmonyCoroutineUtil.Empty()", prepare);

        var death = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "ClientDeathPatches.cs"));
        Assert.Contains("ref IEnumerator __result", death);
        Assert.Contains("HarmonyCoroutineUtil.Empty()", death);

        var worm = File.ReadAllText(Path.Combine(ModDir, "Domains", "World", "Patches", "ClientWorldPatches.cs"));
        Assert.Contains("waitToSpawnWorm", worm);
        Assert.Contains("HarmonyCoroutineUtil.Empty()", worm);
    }

    [Fact]
    public void ShippedMod_HasHordeHostClientCombatAuthority()
    {
        var required = new[]
        {
            "Domains/Combat/Patches/ClientHitscanDamageRedirectPatch.cs",
            "Domains/Combat/Patches/ClientCombatPatches.cs",
            "Domains/Combat/Patches/HostCombatPatches.cs",
            "Domains/Combat/Patches/ClientAIDisablePatches.cs",
            "Domains/World/Patches/BirdAreaSyncPatches.cs",
            "Networking/Services/EntityStateBroadcastService.cs",
            "Networking/Services/ClientEntityInterpolationService.cs",
            "Networking/NetworkRole.cs",
            "Domains/Combat/Patches/AudioSuppressionPatch.cs",
        };
        foreach (var rel in required)
        {
            var path = Path.Combine(ModDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), "Missing Horde authority surface: " + rel);
        }

        var redirect = File.ReadAllText(Path.Combine(ModDir, "Domains", "Combat", "Patches", "ClientHitscanDamageRedirectPatch.cs"));
        Assert.Contains("NetworkRole.Client", redirect);
        Assert.Contains("PlayerAttack", redirect);
        Assert.Contains("return false", redirect);
    }

    [Fact]
    public void ShippedMod_HasNoYokyyActionEventCombatPath()
    {
        var csFiles = Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(csFiles);
        var hits = new List<string>();
        foreach (var f in csFiles)
        {
            var text = File.ReadAllText(f);
            if (text.Contains("ActionEventPacket") || Regex.IsMatch(text, @"ActionName\s*=\s*\$?""pvp:"))
                hits.Add(Path.GetRelativePath(ModDir, f));
        }
        Assert.True(hits.Count == 0,
            "Yokyy ActionEvent combat remnants in shipped mod: " + string.Join(", ", hits));
    }

    [Fact]
    public void YokyyCore_RemovedFromShipTree_PathBEntryOnly()
    {
        var archiveRoot = Path.Combine(RepoRoot, "archive", "yokyy-merge-0.9");
        Assert.False(Directory.Exists(archiveRoot),
            "Frozen Path A tree must stay out of the public ship path.");

        var entry = Path.Combine(ModDir, "Bootstrap", "DWMPEntry.cs");
        Assert.True(File.Exists(entry));
        Assert.Contains("BepInPlugin", File.ReadAllText(entry));
        Assert.False(File.Exists(Path.Combine(ModDir, "ModMain.cs")),
            "Yokyy ModMain.cs must not be the shipped entry under DarkwoodMP.Mod");
    }

    [Fact]
    public void NetworkApplyGuard_IsSealedClass_NotStruct()
    {
        // struct + `using (new NetworkApplyGuard())` compiled to initobj (ctor never ran).
        var guard = File.ReadAllText(Path.Combine(ModDir, "Networking", "Services", "NetworkApplyGuard.cs"));
        Assert.Contains("sealed class NetworkApplyGuard", guard);
        Assert.DoesNotContain("struct NetworkApplyGuard", guard);
    }

    [Fact]
    public void LanForward_UsesPutRaw_NotLengthPrefixedRewrap()
    {
        // Host forward path lives in inbound dispatch partial (not the LAN manager shell).
        var lan = File.ReadAllText(Path.Combine(ModDir, "Networking", "Dispatch", "LanNetworkManager.Dispatch.cs"));
        Assert.Contains("PutRaw(payload)", lan);
        Assert.DoesNotContain("w => w.Put(payload)", lan);
    }

    [Fact]
    public void HotPath_NoAllocatingOverlapSphere()
    {
        var hits = new List<string>();
        foreach (var f in Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            // Allocating overload: Physics.OverlapSphere( — NonAlloc is OK.
            if (text.Contains("Physics.OverlapSphere(") && !text.Contains("OverlapSphereNonAlloc"))
            {
                // Allow comments / docs that mention the banned API.
                foreach (var line in text.Split('\n'))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("///"))
                        continue;
                    if (t.Contains("Physics.OverlapSphere(") && !t.Contains("OverlapSphereNonAlloc"))
                        hits.Add(Path.GetRelativePath(ModDir, f) + ": " + t.Trim());
                }
            }
        }
        Assert.True(hits.Count == 0,
            "Allocating Physics.OverlapSphere still present:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void CharacterTracker_HasNoAllocatingGetAll()
    {
        var tracker = File.ReadAllText(Path.Combine(ModDir, "Domains", "Players", "CharacterTracker.cs"));
        Assert.DoesNotContain("public static Character[] GetAll()", tracker);
        var hits = new List<string>();
        foreach (var f in Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            if (text.Contains("CharacterTracker.GetAll("))
                hits.Add(Path.GetRelativePath(ModDir, f));
        }
        Assert.True(hits.Count == 0, "CharacterTracker.GetAll call sites remain: " + string.Join(", ", hits));
    }

    [Fact]
    public void NonMessageHubs_StayUnder500Lines()
    {
        var oversized = new List<string>();
        foreach (var f in Directory.GetFiles(ModDir, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}Networking{Path.DirectorySeparatorChar}Messages{Path.DirectorySeparatorChar}"))
                continue;
            int lines = File.ReadAllLines(f).Length;
            if (lines >= 500)
                oversized.Add(Path.GetRelativePath(ModDir, f) + " (" + lines + ")");
        }
        Assert.True(oversized.Count == 0,
            "Non-message hubs ≥500 lines:\n" + string.Join("\n", oversized));
    }

    [Fact]
    public void Domains_NoRetiredLanNetworkManagerFacades()
    {
        var domains = Path.Combine(ModDir, "Domains");
        var hits = Directory.GetFiles(domains, "LanNetworkManager.*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(ModDir, f))
            .Where(rel => !rel.EndsWith("WorldTickFields.cs", StringComparison.Ordinal))
            .ToList();
        Assert.True(hits.Count == 0,
            "Retired Domains LanNetworkManager façades still present: " + string.Join(", ", hits));
    }

    [Fact]
    public void ClientBackupRestore_RepublishesPeerItemPresence()
    {
        // Batch 49: soft reconnect ClearPlayer + RestoreItems via createItem never hit
        // addItemType* Harmony — haveItem EventTriggers softlock until republish.
        var restore = File.ReadAllText(Path.Combine(ModDir, "Networking", "Services", "ClientStateBackup.Restore.cs"));
        Assert.Contains("PeerItemPresence.SendFullLocalInventory", restore);

        var handshake = File.ReadAllText(Path.Combine(ModDir, "Networking", "Session", "LanNetworkManager.SessionHandlers.cs"));
        Assert.Contains("PeerItemPresence.SendFullLocalInventory", handshake);
        Assert.Contains("phase3 reconnect", handshake);
    }
}
