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
    public void PluginInfo_IsYokWarePathB_Protocol25()
    {
        var text = File.ReadAllText(Path.Combine(ModDir, "Bootstrap", "PluginInfo.cs"));
        Assert.Contains("com.yokware.branch", text);
        Assert.Contains("YokWare Branch", text);
        Assert.Contains("ProtocolVersion = 25", text);
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
        Assert.Contains("InsideTrapPlacement = false", trap);
        Assert.Contains("private static void Finalizer()", trap);

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
        Assert.Contains("BarricadeSyncHelpers.EndGetHit(id)", barr);
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
        var defer = File.ReadAllText(Path.Combine(ModDir, "Domains", "Dialogue", "Patches", "DialogClientWorldDeferPatches.cs"));
        Assert.Contains("DialogClientWorldDeferBoardPatch", defer);
        Assert.Contains("[HarmonyFinalizer]", defer);
        Assert.Contains("DialogClientWorldDefer.End()", defer);
        Assert.Contains("private static void Finalizer(bool __state)", defer);

        var pickup = File.ReadAllText(Path.Combine(ModDir, "Domains", "Inventory", "Patches", "DroppedItemSyncPatches.cs"));
        Assert.Contains("PlayerPickupDroppedItemPatch", pickup);
        Assert.Contains("[HarmonyFinalizer]", pickup);
        Assert.Contains("private static void Finalizer(Item __instance, PrefixState __state)", pickup);
        Assert.Contains("TrapPickupGuard.End(__instance)", pickup);
        Assert.Contains("WorldPickupWireGuard.End()", pickup);
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
}
