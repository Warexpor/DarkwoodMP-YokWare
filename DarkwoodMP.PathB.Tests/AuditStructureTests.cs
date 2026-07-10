using System.Text.RegularExpressions;
using Xunit;

namespace DarkwoodMP.PathB.Tests;

/// <summary>
/// Structural gates that lock audit fixes to shipped Path B sources.
/// These are not flaky e2e runs — they prove the real entry points after 0.9.2.
/// </summary>
public class AuditStructureTests
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
    private static string DocsDir => Path.Combine(RepoRoot, "docs");

    private static string ReadMod(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { ModDir }.Concat(parts).ToArray()));

    [Fact]
    public void AuditReport_Exists_WithRequiredSections()
    {
        var path = Path.Combine(DocsDir, "DARKWOOD_MP_AUDIT.md");
        Assert.True(File.Exists(path), "Missing docs/DARKWOOD_MP_AUDIT.md");
        var text = File.ReadAllText(path);
        foreach (var section in new[]
                 {
                     "Original-game baseline",
                     "Deep mod bug audit",
                     "Story multiplayer edge cases",
                     "Sync contract vs implementation",
                     "Controller.FixedUpdate",
                     "DialogOutcome",
                     "generateChapter",
                     "EventTriggers",
                 })
        {
            Assert.True(text.Contains(section, StringComparison.OrdinalIgnoreCase),
                "Audit report missing section/content: " + section);
        }
    }

    [Fact]
    public void Critical_C1_ClientTimeAuthority_SuppressesFixedUpdateAndUsesNoLogicRefresh()
    {
        // Shipped: Harmony patch on Controller.FixedUpdate + refreshTime for clients.
        var timePatch = ReadMod("Patches", "ClientTimeAuthorityPatches.cs");
        Assert.Contains("Controller", timePatch);
        Assert.Contains("FixedUpdate", timePatch);
        Assert.Contains("CoopTimePolicy.ShouldSuppressClientClock", timePatch);
        Assert.Contains("refreshTimeNoLogic", timePatch);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleTimeSync", handlers);
        Assert.Contains("refreshTimeNoLogic", handlers);
        // Must NOT still dual-fire day-chain via full refreshTime in TimeSync apply.
        Assert.DoesNotContain("ctrl.refreshTime()", handlers);
        Assert.Contains("ShouldUseRefreshTimeNoLogicOnClientSync", handlers);

        var policy = ReadMod("CoopPolicy.cs");
        Assert.Contains("ShouldSuppressClientClock", policy);
    }

    [Fact]
    public void Critical_C2_DialogOutcome_HostWorldOnly_NoPersonalGivePath()
    {
        var dialogPatch = ReadMod("Patches", "DialogOutcomePatch.cs");
        Assert.Contains("DialogOutcomeSync", dialogPatch);
        Assert.Contains("NetworkRole.Client", dialogPatch);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleDialogOutcomeSync", handlers);
        Assert.Contains("DialogHostApplyGuard.BeginWorldOnly", handlers);
        Assert.Contains("displayDialogue", handlers);

        var suppress = ReadMod("Patches", "DialogPersonalSuppressPatches.cs");
        Assert.Contains("DialogHostApplyGuard.SuppressPersonalRewards", suppress);
        Assert.Contains("addItemTypeToPlayer", suppress);
        Assert.Contains("addJournalItem", suppress);
        Assert.Contains("showJournalInfoPopup", suppress);

        var guard = ReadMod("Sync", "DialogHostApplyGuard.cs");
        Assert.Contains("SnapshotPersonalJournal", guard);
        Assert.Contains("RestorePersonalJournal", guard);
        Assert.Contains("itemsDict", guard);
        Assert.Contains("keysDict", guard);
        Assert.Contains("notesDict", guard);

        var lockPatch = ReadMod("Patches", "NpcDialogueLockPatches.cs");
        Assert.Contains("initiateDialogue", lockPatch);
        Assert.Contains("NpcDialogueLock", lockPatch);

        var lockRuntime = ReadMod("Sync", "NpcDialogueLock.cs");
        Assert.Contains("Dictionary<string, Hold>", lockRuntime);
        Assert.Contains("CanAcquireNpcSlot", lockRuntime);
    }

    [Fact]
    public void Critical_C3_Chapter_ResumesNetwork_NotSilentSoloOnly()
    {
        var chapter = ReadMod("Patches", "ChapterProgressionPatches.cs");
        Assert.Contains("generateChapter", chapter);
        Assert.Contains("resumeAfter: true", chapter);
        Assert.Contains("ChapterSessionResume.CaptureForResume", chapter);
        Assert.Contains("StopNetwork", chapter);

        var resume = ReadMod("Sync", "ChapterSessionResume.cs");
        Assert.Contains("CaptureForResume", resume);
        Assert.Contains("StartHost", resume);
        Assert.Contains("ConnectToHost", resume);
        Assert.Contains("ExecuteResume", resume);

        // Credits still permanent stop (documented residual).
        var epilogue = ReadMod("Patches", "EpilogueSyncPatches.cs");
        Assert.Contains("goToCredits", epilogue);
        Assert.Contains("StopNetwork", epilogue);
        Assert.Contains("SceneLoad", epilogue);

        var policy = ReadMod("CoopPolicy.cs");
        Assert.Contains("ShouldStopNetworkPermanently", policy);
        Assert.Contains("credits", policy);
    }

    [Fact]
    public void Critical_C4_WorldGen_ShareOnly_FailLoud_NoChunkSeedClaim()
    {
        var worldGenShare = ReadMod("Patches", "WorldGenSharePatch.cs");
        Assert.Contains("onFinished", worldGenShare);
        Assert.Contains("ScheduleHostShareAfterNewWorld", worldGenShare);

        var share = ReadMod("Networking", "WorldSaveShareService.cs");
        Assert.Contains("WorldSharePolicy.FormatShareFailure", share);
        Assert.Contains("Success = false", share);
        // Missing profile dir must fail-loud (not weak ProgressText alone).
        Assert.Contains("no profile dir", share);
        // Count FormatShareFailure usages — bad profile id, missing dir, no files, save error, client apply.
        var formatHits = System.Text.RegularExpressions.Regex.Matches(share, "WorldSharePolicy\\.FormatShareFailure").Count;
        Assert.True(formatHits >= 4,
            "Expected ≥4 FormatShareFailure sites (profile/dir/files/apply); got " + formatHits);

        // Path B must not claim Yokyy per-chunk InitState seeding in live patches.
        var allPatches = Directory.GetFiles(Path.Combine(ModDir, "Patches"), "*.cs")
            .Select(File.ReadAllText);
        var joined = string.Join("\n", allPatches);
        Assert.DoesNotContain("ChunkGenSeed", joined);
        Assert.DoesNotContain("WorldGenSeed_Patch", joined);
    }

    [Fact]
    public void High_H1_FlagSync_ClientToHost_AndHostBroadcast()
    {
        var flags = ReadMod("Patches", "FlagSyncPatches.cs");
        Assert.Contains("setFlag", flags);
        Assert.Contains("NetworkRole.Client", flags);
        Assert.Contains("net.Send(NetMessageType.FlagSync", flags);
        Assert.Contains("FlagSyncMessage", flags);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleFlagSync", handlers);
        // Host accepts client deltas and fans out.
        Assert.Contains("_role == NetworkRole.Host", handlers);
        Assert.Contains("SendToAllExcept", handlers);
        // Apply must use NetworkApplyGuard so Postfix does not echo/double-fan-out.
        Assert.Contains("ApplyFlagSyncMessage", handlers);
        Assert.Contains("new NetworkApplyGuard()", handlers);
        // setFlag inside ApplyFlagSyncMessage is under the guard block.
        var applyIdx = handlers.IndexOf("private void ApplyFlagSyncMessage", StringComparison.Ordinal);
        Assert.True(applyIdx >= 0, "ApplyFlagSyncMessage method missing");
        var applySlice = handlers.Substring(applyIdx, Math.Min(500, handlers.Length - applyIdx));
        Assert.Contains("NetworkApplyGuard", applySlice);
        Assert.Contains("setFlag", applySlice);
    }

    [Fact]
    public void High_H3_NightDeath_WorldMutationSuppress_Present()
    {
        var night = ReadMod("Patches", "NightDeathPatches.cs");
        Assert.Contains("skipDay", night);
        Assert.Contains("AllDeadAtNight", night);
        Assert.Contains("SaveManager", night);
        Assert.Contains("SpectatorModeController", night);

        var world = ReadMod("Patches", "NightDeathWorldMutationPatches.cs");
        Assert.Contains("transportToHome", world);
        Assert.Contains("respawnAllEnemies", world);
        Assert.Contains("NightDeathPolicy.ShouldSuppressWorldDeathMutations", world);
    }

    [Fact]
    public void Story_EventTriggers_ProxyAndClientSuppress_Present()
    {
        var et = ReadMod("Patches", "EventTriggersProxyPatches.cs");
        Assert.Contains("OnTriggerEnter", et);
        Assert.Contains("RemotePlayerProxy", et);
        Assert.Contains("fireEventTrigger", et);
        Assert.Contains("EventTriggersClientEnterSuppressPatch", et);
    }

    [Fact]
    public void Story_DreamSession_AndFinalDream_Present()
    {
        var dreamPatches = ReadMod("Patches", "DreamSyncPatches.cs");
        Assert.Contains("startDreaming", dreamPatches);
        Assert.Contains("DreamStartRequest", dreamPatches);
        Assert.Contains("playerDeath", dreamPatches);

        var session = ReadMod("Sync", "DreamSession.cs");
        Assert.Contains("TryBegin", session);
        Assert.Contains("ShouldRejectNewConnections", session);

        var final = ReadMod("Sync", "FinalDreamsceneManager.cs");
        Assert.Contains("OnLocalDeathInDream", final);
        Assert.Contains("inEpilogue", final);
    }

    [Fact]
    public void Wire_PathBIsProtocol19_VersionIs09x_Not10()
    {
        var plugin = ReadMod("PluginInfo.cs");
        Assert.Contains("ProtocolVersion = 19", plugin);
        Assert.Contains("Horde", plugin);
        Assert.Contains("0.9.2", plugin);
        Assert.DoesNotContain("Version = \"1.0", plugin);
        Assert.DoesNotContain("Version = \"1.0.0\"", plugin);

        var ironbark = File.ReadAllText(Path.Combine(RepoRoot, "DarkwoodMP.Protocol", "Ironbark.cs"));
        Assert.Contains("Version = 2", ironbark);
        Assert.DoesNotContain("ProtocolVersion = 2", plugin);

        var netTypes = ReadMod("Networking", "Messages", "NetMessageType.cs");
        Assert.Contains("DialogNpcLock = 112", netTypes);
        Assert.Contains("DialogTreeState = 113", netTypes);
    }

    [Fact]
    public void Join_WorldRequest_ClientPull_Present()
    {
        var netTypes = ReadMod("Networking", "Messages", "NetMessageType.cs");
        Assert.Contains("WorldRequest = 114", netTypes);

        var msgs = ReadMod("Networking", "Messages", "WorldSaveShareMessages.cs");
        Assert.Contains("WorldRequestMessage", msgs);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleWorldRequest", handlers);
        Assert.Contains("RequestHostWorld", handlers);
        Assert.Contains("ScheduleHostShareToPlayer", handlers);

        var ui = ReadMod("UI", "MainMenuMultiplayerInject.cs");
        Assert.Contains("RequestHostWorld", ui);
        Assert.Contains("title-wait-10s", ui);
        Assert.Contains("REQUESTING WORLD", ui);

        var share = ReadMod("Networking", "WorldSaveShareService.cs");
        Assert.Contains("IsClientReceivingOrApplying", share);
        // J16: path fix + native Continue + host mute loading peers
        Assert.Contains("updateFilePaths", share);
        Assert.Contains("initLoadGame", share);
        // J17: share → offline load → reconnect (not load while connected)
        Assert.Contains("CaptureForResume", share);
        Assert.Contains("StopNetwork", share);
        Assert.Contains("Join pipeline phase 2", share);

        var lan = ReadMod("Networking", "LanNetworkManager.cs");
        Assert.Contains("MarkPeerLoadingWorld", lan);
        Assert.Contains("skipLoadingPeers", lan);
        Assert.Contains("_peersLoadingWorld", lan);
        Assert.Contains("ClientReportsAlreadyInWorld", lan);
        Assert.Contains("AlreadyInWorld", lan);

        var handshake = ReadMod("Networking", "Messages", "PlayerMessages.cs");
        Assert.Contains("AlreadyInWorld", handshake);

        Assert.Contains("Join pipeline phase 3", handlers);
        Assert.Contains("AlreadyInWorld", handlers);
        Assert.Contains("_peersCoopReconnect", handlers);

        var resume = ReadMod("Sync", "ChapterSessionResume.cs");
        Assert.Contains("IsLocalPlayableForCoopReconnect", resume);
        Assert.Contains("waiting for offline load", resume);
        Assert.Contains("loadingGame", resume);

        // Residuals: dual-box path + H6 deny
        Assert.Contains("SaveRootOverride", ReadMod("Config", "ModConfig.cs"));
        Assert.Contains("get_persistentDataPath", ReadMod("Patches", "PersistentDataPathPatch.cs"));
        Assert.Contains("ContainerTakeDenied", ReadMod("Networking", "Messages", "NetMessageType.cs"));
        Assert.Contains("DenyContainerTake", handlers);
        Assert.Contains("Client blocked new worldgen", ReadMod("Patches", "WorldGenSharePatch.cs"));
    }

    [Fact]
    public void DialogTree_YokyyPort_CloseFlushBulkAndCodec()
    {
        var close = ReadMod("Patches", "DialogTreeSyncPatches.cs");
        Assert.Contains("DialogueWindow", close);
        Assert.Contains("close", close);
        Assert.Contains("__instance.npc != null", close); // real-close guard
        Assert.Contains("TryBroadcastFromNpc", close);

        var sync = ReadMod("Sync", "DialogTreeSync.cs");
        Assert.Contains("ApplyPayload", sync);
        Assert.Contains("NetworkApplyGuard", sync);
        Assert.Contains("SendBulkTo", sync);
        Assert.Contains("alreadyShown", sync);

        var codec = ReadMod("Sync", "DialogTreeWireCodec.cs");
        Assert.Contains("TryDecode", codec);
        Assert.Contains("Encode", codec);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleDialogTreeState", handlers);
        Assert.Contains("DialogTreeSync.SendBulkTo", handlers);
        Assert.Contains("DialogTreeSync.TryBroadcastFromNpc", handlers); // outcome flush
    }

    [Fact]
    public void TodoOpen_Items_StillDocumentedInAuditOrTodo()
    {
        var todo = File.ReadAllText(Path.Combine(DocsDir, "TODO.md"));
        Assert.Contains("Location/landmark placement residual", todo);
        Assert.Contains("Live 2-instance", todo);

        var audit = File.ReadAllText(Path.Combine(DocsDir, "DARKWOOD_MP_AUDIT.md"));
        Assert.Contains("Location/landmark placement residual", audit);
        Assert.Contains("Live 2-instance", audit);
    }
}
