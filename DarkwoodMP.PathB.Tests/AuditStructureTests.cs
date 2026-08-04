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

        var present = ReadMod("Patches", "DialogHostPresentationSuppressPatches.cs");
        Assert.Contains("DialogHostApplyGuard.Active", present);
        Assert.Contains("tweenBlackScreen", present);
        Assert.Contains("tweenBlackScreenTop", present);

        var doorSync = ReadMod("Patches", "DreamDoorSyncPatches.cs");
        Assert.Contains("DialogHostApplyGuard.Active", doorSync);
        Assert.Contains("OnHostDialogWorldApplied", doorSync);

        Assert.Contains("HostFireNpcCloseDialogue", handlers);
        Assert.Contains("onCloseDialogue", handlers);
        Assert.Contains("HostDrainWorldOnlyDialogue", handlers);
        Assert.Contains("HostEnsureDialogueDoorOpen", doorSync);
        Assert.Contains("HostFireDreamLeaveDoorGameEvents", handlers);
        Assert.Contains("defer onCloseDialogue", handlers);
        Assert.Contains("aborted world-only drain on dialog Release", handlers);
        Assert.Contains("enterAllNodes", doorSync);
        Assert.Contains("ClientAfterDialogueDoorRoutine", doorSync);
        Assert.Contains("GetDoorsCached", doorSync);
        // Client dialogue Release → HostFire under NetworkApplyGuard must still fan-out GEs.
        Assert.Contains("IsApplyingRemoteState && !DialogHostApplyGuard.Active", handlers);
        Assert.Contains("SendGameEventsFired", handlers);

        var dreamSync = ReadMod("Sync", "DreamSyncManager.cs");
        Assert.Contains("peer dream-entry local Save", dreamSync);
        Assert.Contains("showSavingIndicator: true", dreamSync);
        Assert.Contains("outsideLoc.loading = true", dreamSync);
        Assert.Contains("RemapDreamUniqueObjects", dreamSync);
        Assert.Contains("FinishDreamOutsideLoadFlags", dreamSync);
        Assert.Contains("enterAllNodes", dreamSync);
        Assert.Contains("TryBeginHostOrderedStoryEnd", dreamSync);
        Assert.Contains("NotifyPeersStoryEndBeginning", dreamSync);
        Assert.Contains("Host-ordered story end", dreamSync);
        Assert.Contains("local dead, personal rewards skipped", dreamSync);

        var dreamPatches = ReadMod("Patches", "DreamSyncPatches.cs");
        Assert.Contains("DowngradeSuccessRewardsIfDeadInDream", dreamPatches);
        Assert.Contains("inventory restore only (no success rewards)", dreamPatches);

        var geAuth = ReadMod("Patches", "GameEventDreamAuthorityPatch.cs");
        Assert.Contains("EmptyRoutine", geAuth);
        Assert.Contains("ref IEnumerator __result", geAuth);

        var uoPatch = ReadMod("Patches", "UniqueObjectsDreamPatch.cs");
        Assert.Contains("UniqueObjectsDreamGetPatch", uoPatch);
        Assert.Contains("GetDreamLocationTransform", uoPatch);

        var phys = ReadMod("Sync", "WorldPhysicsSyncService.cs");
        Assert.Contains("HasRecentPushAuthority", phys);
        Assert.Contains("clientLocalFreeBody", phys);
        Assert.Contains("dreamPad", phys);
        Assert.Contains("IsSceneFixedLightItem", phys);

        var audioSup = ReadMod("Patches", "AudioSuppressionPatch.cs");
        Assert.Contains("RemotePlayerProxy", audioSup);

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

        // Regression: guard MUST be a class. Struct + `using (new NetworkApplyGuard())`
        // compiled to initobj (ctor never ran) → client dream GEs fire no-op forever.
        var guard = ReadMod("Networking", "NetworkApplyGuard.cs");
        Assert.Contains("sealed class NetworkApplyGuard", guard);
        Assert.DoesNotContain("struct NetworkApplyGuard", guard);
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
    public void Story_EventTriggers_ProxyEnterOnAllPeers_Present()
    {
        var et = ReadMod("Patches", "EventTriggersProxyPatches.cs");
        Assert.Contains("OnTriggerEnter", et);
        Assert.Contains("RemotePlayerProxy", et);
        Assert.Contains("fireEventTrigger", et);
        // multipleFire (karuzela) runs locally — no client enter suppress; proxy enter on all peers.
        Assert.DoesNotContain("EventTriggersClientEnterSuppressPatch", et);
        Assert.Contains("IsMultiplayerConnected", et);
    }

    [Fact]
    public void Story_DreamSession_AndFinalDream_Present()
    {
        var dreamPatches = ReadMod("Patches", "DreamSyncPatches.cs");
        Assert.Contains("startDreaming", dreamPatches);
        Assert.Contains("DreamStartRequest", dreamPatches);
        Assert.Contains("playerDeath", dreamPatches);
        Assert.Contains("prepareDream", dreamPatches);
        Assert.Contains("DreamChainStart", dreamPatches);
        Assert.Contains("initiateEndDreaming", dreamPatches);
        Assert.Contains("getPreset", dreamPatches);
        Assert.Contains("DreamGetPresetPatch", dreamPatches);
        Assert.Contains("MirrorPoolRemove", dreamPatches);
        Assert.Contains("OutcomeHasTransferToDream", dreamPatches);

        var session = ReadMod("Sync", "DreamSession.cs");
        Assert.Contains("TryBegin", session);
        Assert.Contains("BeginFromHost", session);
        Assert.Contains("UpdateActivePreset", session);
        Assert.Contains("ShouldRejectNewConnections", session);
        Assert.Contains("ApplySnapshot", session);
        Assert.Contains("SetChainedPreset", session);
        Assert.Contains("GetCompletedPresets", session);
        Assert.Contains("MirrorPoolRemove", session);
        Assert.Contains("SetPendingHostPreset", session);

        var final = ReadMod("Sync", "FinalDreamsceneManager.cs");
        Assert.Contains("OnLocalDeathInDream", final);
        Assert.Contains("inEpilogue", final);
        Assert.Contains("GetHandshakedPeerIds", final);
        Assert.Contains("TryHostEndAllDead", final);

        var netTypes = ReadMod("Networking", "Messages", "NetMessageType.cs");
        Assert.Contains("DreamSessionBulk = 120", netTypes);
        Assert.Contains("DreamChainStart = 121", netTypes);

        var dreamMsgs = ReadMod("Networking", "Messages", "DreamMessages.cs");
        Assert.Contains("DreamSessionBulkMessage", dreamMsgs);
        Assert.Contains("DreamChainStartMessage", dreamMsgs);
        Assert.Contains("CompletedPresets", dreamMsgs);
        Assert.Contains("LvlFlags", dreamMsgs);

        var handlers = ReadMod("Networking", "LanNetworkManager.DreamHandlers.cs");
        Assert.Contains("initiateEndDreaming", handlers);
        Assert.Contains("HandleDreamChainStart", handlers);
        Assert.Contains("SendDreamSessionBulkTo", handlers);
        Assert.Contains("MirrorPoolRemove", handlers);
        Assert.Contains("LvlFlags", handlers);
        Assert.Contains("SendDreamEndedRejected", handlers);
        Assert.Contains("IsRejectedOutcome", handlers);
        Assert.Contains("msg.SessionId != DreamSession.SessionId", handlers);
        Assert.Contains("Drop DreamChainStart session", handlers);

        var geDream = ReadMod("Patches", "GameEventDreamAuthorityPatch.cs");
        Assert.Contains("Type.startDream", geDream);
        Assert.Contains("Type.endDream", geDream);
        Assert.Contains("IsApplyingRemoteState", geDream);

        Assert.Contains("DreamWantToSwitchPatch", dreamPatches);
        Assert.Contains("AllowDeathEndPass", dreamPatches);
        Assert.Contains("HasRemoteParticipants", dreamPatches);
        Assert.Contains("BeginStoryEndDefer", dreamPatches);
        Assert.Contains("IsStoryEndDeferPending", dreamPatches);

        var mgr = ReadMod("Sync", "DreamSyncManager.cs");
        Assert.Contains("OnDreamChain", mgr);
        Assert.Contains("ShouldSyncPhysicsObject", mgr);
        Assert.Contains("saveCurrentPlayerState", mgr);
        Assert.Contains("uniqueObjectToTransportToAfterDreamEnd", mgr);
        Assert.Contains("endDivingOut", mgr);
        Assert.Contains("switchingDream = true", mgr);
        Assert.Contains("ForceLocalDreamCleanup", mgr);
        Assert.Contains("BeginStoryEndDefer", mgr);
        Assert.Contains("epilog_part1a_dream", mgr);
        Assert.Contains("outside_roadToHome_01", mgr);
        Assert.Contains("forceSaveStatic", mgr);

        Assert.Contains("AdoptSessionId", session);
        Assert.Contains("BuildRejectedOutcome", session);
        Assert.Contains("IsRejectedOutcome", session);
        // H4: TryBegin must not forever-ban via IsPresetCompleted
        Assert.DoesNotContain("Reject begin — already completed", session);

        Assert.Contains("AllowDeathEndPass", final);
        Assert.Contains("HasRemoteParticipants", final);
        Assert.Contains("initiateEndDreaming", final);
        Assert.Contains("No remotes left — ending shared dream", final);
    }

    // Wire_PathBIsProtocol19 follows.
    [Fact]
    public void Wire_PathBIsProtocol19_VersionIs07x_Not10()
    {
        var plugin = ReadMod("PluginInfo.cs");
        Assert.Contains("ProtocolVersion = 22", plugin);
        Assert.Contains("Horde", plugin);
        Assert.Contains("0.7.46", plugin);
        Assert.DoesNotContain("Version = \"1.0", plugin);
        Assert.DoesNotContain("Version = \"1.0.0\"", plugin);

        var ironbark = File.ReadAllText(Path.Combine(RepoRoot, "DarkwoodMP.Protocol", "Ironbark.cs"));
        Assert.Contains("Version = 2", ironbark);
        // Ironbark Version=2 is separate from PluginInfo ProtocolVersion (20+).

        var netTypes = ReadMod("Networking", "Messages", "NetMessageType.cs");
        Assert.Contains("DialogNpcLock = 112", netTypes);
        Assert.Contains("DialogTreeState = 113", netTypes);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("SendStoredClientBackupTo", handlers);
        Assert.Contains("BeginClientBackupRestoreWait", handlers);
        Assert.Contains("SaveLocalSelfBackupFile(json)", handlers);
        Assert.Contains("restored backup (inv=", handlers);
        Assert.Contains("TrySnapshotClientBackupOnExit", ReadMod("Networking", "LanNetworkManager.cs"));
        Assert.Contains("RestorePosition", ReadMod("Networking", "ClientStateBackup.cs"));
        Assert.Contains("MatchesCurrentCampaign", ReadMod("Networking", "ClientStateBackup.cs"));
        Assert.Contains("IsDreamPadCoordinate", ReadMod("Networking", "ClientStateBackup.cs"));
        Assert.Contains("ResolveOverworldBackupPosition", ReadMod("Networking", "ClientStateBackup.cs"));
        Assert.Contains("MigrateLegacyCampaignIfNeeded", ReadMod("Networking", "ClientStateBackup.cs"));
        Assert.Contains("TryGetPreDreamOverworldPosition", ReadMod("Sync", "DreamSyncManager.cs"));
        Assert.Contains("ResolveActivePresetName", ReadMod("Sync", "DreamSyncManager.cs"));
        Assert.Contains("haveOverworldPosCopy", ReadMod("Sync", "DreamSyncManager.cs"));
        Assert.Contains("PendingDreamGameEventsMaxAge", ReadMod("Networking", "LanNetworkManager.Handlers.cs"));
        Assert.Contains("defer LocationExit", ReadMod("Networking", "LanNetworkManager.Handlers.cs"));
        Assert.Contains("post-endDreaming snap off pad", ReadMod("Patches", "DreamSyncPatches.cs"));
        Assert.Contains("karuzela", ReadMod("Networking", "LanNetworkManager.Handlers.cs"));
        Assert.Contains("HostBroadcastDreamPropColliders", ReadMod("Sync", "WorldPhysicsSyncService.cs"));
        Assert.Contains("DreamPropCollider", ReadMod("Networking", "Messages", "NetMessageType.cs"));
        Assert.Contains("IsSceneFixedLightItem", ReadMod("Sync", "WorldPhysicsSyncService.cs"));
        Assert.Contains("CampaignId", ReadMod("Networking", "CoopWorldCopyMeta.cs"));
        Assert.Contains("MintNewCampaignId", ReadMod("Networking", "CoopWorldCopyMeta.cs"));

        var hostCombat = ReadMod("Patches", "HostCombatPatches.cs");
        Assert.Contains("RemovePooledPrefab(\"Sensors\"", hostCombat);
        Assert.Contains("ProxyHitDebounce", hostCombat);
        Assert.Contains("[ProxyMelee]", hostCombat);

        var hostAi = ReadMod("Patches", "HostAIPatches.cs");
        Assert.Contains("HostAttackPlayerNearestPatch", hostAi);
        Assert.Contains("Sticky: already chasing the host", hostAi);

        var dmgMsg = ReadMod("Networking", "Messages", "PlayerMessages.cs");
        Assert.Contains("public bool NormalHit", dmgMsg);
        Assert.Contains("public bool CanInterrupt", dmgMsg);
        Assert.Contains("normalHit: msg.NormalHit", ReadMod("Networking", "LanNetworkManager.Combat.cs"));
        Assert.Contains("NormalHit = normalHit", ReadMod("Patches", "ProxyDamagePatch.cs"));

        Assert.True(File.Exists(Path.Combine(RepoRoot, "DarkwoodMP.Mod", "Patches", "DreamForestSpiritSpawnPatch.cs")));
        Assert.True(File.Exists(Path.Combine(RepoRoot, "DarkwoodMP.Mod", "Patches", "ThreatTriggerContext.cs")));
        Assert.True(File.Exists(Path.Combine(RepoRoot, "DarkwoodMP.Mod", "Patches", "HostDamageAroundMePatch.cs")));
        Assert.Contains("CampaignId", ReadMod("Networking", "Messages", "WorldSaveShareMessages.cs"));
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
        // J17: share → ENTER WORLD → offline load → reconnect (not load while connected)
        Assert.Contains("CaptureForResume", share);
        Assert.Contains("StopNetwork", share);
        Assert.Contains("Join pipeline phase 2", share);
        Assert.Contains("IsAwaitingEnterWorld", share);
        Assert.Contains("TryBeginEnterWorld", share);
        Assert.Contains("ENTER WORLD", share);

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
    public void Stations_FeederLure_Sleep_WorkbenchLock_Present()
    {
        var netTypes = ReadMod("Networking", "Messages", "NetMessageType.cs");
        Assert.Contains("FeederState = 116", netTypes);
        Assert.Contains("LureState = 117", netTypes);
        Assert.Contains("SleepEndRequest = 118", netTypes);
        Assert.Contains("WorkbenchLock = 119", netTypes);
        Assert.Contains("DreamSessionBulk = 120", netTypes);
        Assert.Contains("DreamChainStart = 121", netTypes);
        Assert.Contains("AfterNightEndRequest = 122", netTypes);
        Assert.Contains("PeerRoster = 123", netTypes);
        Assert.Contains("HostHandoff = 124", netTypes);
        Assert.Contains("ThrowableDespawn = 125", netTypes);
        Assert.Contains("TrapBulk = 126", netTypes);
        Assert.Contains("NightShadowSpawnRequest = 127", netTypes);
        Assert.Contains("DreamPropCollider = 128", netTypes);
        Assert.Contains("VoiceData = 129", netTypes);
        Assert.Contains("ActivateCursorAction = 130", netTypes);
        Assert.Contains("LocationTransport = 131", netTypes);
        Assert.Contains("_Highest = 131", netTypes);
        Assert.Contains("ProtocolVersion = 22", ReadMod("PluginInfo.cs"));

        var worldMsgs = ReadMod("Networking", "Messages", "WorldMessages.cs");
        Assert.Contains("FeederStateMessage", worldMsgs);
        Assert.Contains("LureStateMessage", worldMsgs);

        var syncMsgs = ReadMod("Networking", "Messages", "SyncMessages.cs");
        Assert.Contains("SleepEndRequestMessage", syncMsgs);
        Assert.Contains("AfterNightEndRequestMessage", syncMsgs);
        Assert.Contains("WorkbenchLockMessage", syncMsgs);

        var stations = ReadMod("Sync", "StationSyncPatches.cs");
        Assert.Contains("Feeder", stations);
        Assert.Contains("activate", stations);
        Assert.Contains("Lure", stations);
        Assert.Contains("removeHealth", stations);
        Assert.Contains("QueueLureHealth", stations);
        Assert.Contains("FlushLureOutbox", stations);

        var sleep = ReadMod("Patches", "SleepSyncPatches.cs");
        Assert.Contains("onEndSleep", sleep);
        Assert.Contains("SleepEndRequest", sleep);
        Assert.Contains("SendTimeSyncTo", sleep);

        var wbLock = ReadMod("Sync", "WorkbenchOpenLock.cs");
        Assert.Contains("HostTryGrant", wbLock);
        Assert.Contains("KeyFor", wbLock);

        var wbPatches = ReadMod("Patches", "WorkbenchLockPatches.cs");
        Assert.Contains("Workbench", wbPatches);
        Assert.Contains("open", wbPatches);
        // 0.7.40: exclusive lock parked — patches still present as no-ops.
        Assert.Contains("PARKED", wbPatches);
        Assert.Contains("Inventory", wbPatches);
        Assert.Contains("hide", wbPatches);

        var handlers = ReadMod("Networking", "LanNetworkManager.Handlers.cs");
        Assert.Contains("HandleWorkbenchLock", handlers);
        Assert.Contains("exclusive workbench open disabled", handlers);
        Assert.Contains("HandleFeederState", handlers);
        Assert.Contains("HandleLureState", handlers);
        Assert.Contains("HandleSleepEndRequest", handlers);
        Assert.Contains("HandleAfterNightEndRequest", handlers);
        Assert.Contains("ApplyClientPersonalNewDay", handlers);
        Assert.Contains("HandleWorkbenchLock", handlers);
        Assert.Contains("SendFeederStatesTo", handlers);
        Assert.Contains("SendLureStatesTo", handlers);
        Assert.Contains("SendSawStatesTo(playerId)", handlers);
        // C1: sleep adopt must not call full refreshTime().
        Assert.DoesNotContain("ctrl.refreshTime()", handlers);

        var lan = ReadMod("Networking", "LanNetworkManager.cs");
        Assert.Contains("NetMessageType.FeederState", lan);
        Assert.Contains("NetMessageType.LureState", lan);
        Assert.Contains("NetMessageType.SleepEndRequest", lan);
        Assert.Contains("NetMessageType.AfterNightEndRequest", lan);
        Assert.Contains("NetMessageType.WorkbenchLock", lan);

        var dayNight = ReadMod("Patches", "DayNightTransitionPatches.cs");
        Assert.Contains("endAfterNight", dayNight);
        Assert.Contains("AfterNightEndRequest", dayNight);
        Assert.Contains("startDay", dayNight);
        Assert.Contains("SendTimeSyncTo", dayNight);

        var timeAuth = ReadMod("Patches", "ClientTimeAuthorityPatches.cs");
        Assert.Contains("DoUpdateTime", timeAuth);

        var hostMig = ReadMod("Networking", "HostMigration.cs");
        Assert.Contains("TryBeginHostMigration", hostMig);
        Assert.Contains("PromoteLocalToHost", hostMig);
        Assert.Contains("BroadcastPeerRoster", hostMig);
        Assert.Contains("TryGracefulHostLeave", hostMig);
        Assert.Contains("ReclaimSimulationAuthorityAfterPromote", hostMig);
        Assert.Contains("HostMigrationPolicy.ElectNewHost", hostMig);
        Assert.Contains("PeerRoster", netTypes);
        Assert.Contains("HostHandoff", netTypes);
        Assert.Contains("HostMigrationEnabled", ReadMod("Config", "ModConfig.cs"));
        Assert.Contains("HostMigrationPolicy", ReadMod("CoopPolicy.cs"));
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
        Assert.Contains("Landmark placement full determinism", todo);
        Assert.Contains("Live dual/triple campaign soak", todo);

        var audit = File.ReadAllText(Path.Combine(DocsDir, "DARKWOOD_MP_AUDIT.md"));
        Assert.True(
            audit.Contains("landmark", StringComparison.OrdinalIgnoreCase)
            && audit.Contains("placement", StringComparison.OrdinalIgnoreCase),
            "Audit should still document landmark placement residual");
        Assert.True(
            audit.Contains("campaign soak", StringComparison.OrdinalIgnoreCase)
            || audit.Contains("2-instance", StringComparison.OrdinalIgnoreCase)
            || audit.Contains("dual/triple", StringComparison.OrdinalIgnoreCase),
            "Audit should still document live soak residual");
    }

    [Fact]
    public void DeepReview_2026_07_28_FailClosedGuards_Present()
    {
        var epilogue = ReadMod("Patches", "EpilogueSyncPatches.cs");
        Assert.Contains("HandleSceneLoad", epilogue);
        Assert.Contains("Rejected inbound SceneLoad", epilogue);
        Assert.Contains("_role == NetworkRole.Host", epilogue);
        Assert.Contains("Rejected SceneLoad from non-host", epilogue);

        var combat = ReadMod("Networking", "LanNetworkManager.Combat.cs");
        Assert.Contains("HandleNightDeathState", combat);
        Assert.Contains("AllDeadTrigger", combat);
        Assert.Contains("Rejected peer AllDeadTrigger", combat);
        Assert.Contains("Rejected AllDeadTrigger from non-host", combat);

        var damageRedirect = ReadMod("Patches", "ClientHitscanDamageRedirectPatch.cs");
        Assert.Contains("ClientDamageRedirectPatch", damageRedirect);
        Assert.Contains("Fail closed", damageRedirect);
        Assert.Contains("[DamageRedirect] EXCEPTION in Prefix", damageRedirect);
        Assert.Contains("return false", damageRedirect);

        var trade = ReadMod("Patches", "TradeSyncPatches.cs");
        Assert.Contains("BroadcastNpcInventory", trade);
        Assert.Contains("SendNpcInventoryToHost", trade);
        Assert.Contains("net.Role == NetworkRole.Host", trade);
        Assert.Contains("TradeInventorySync.BroadcastNpcInventory(__instance.npc)", trade);

        var dreamMgr = ReadMod("Sync", "DreamSyncManager.cs");
        Assert.Contains("UnfreezeWorld(bool restoreTime = true)", dreamMgr);
        Assert.Contains("UnfreezeWorld(restoreTime: false)", dreamMgr);

        var dreamHandlers = ReadMod("Networking", "LanNetworkManager.DreamHandlers.cs");
        Assert.Contains("HandleDreamEnded", dreamHandlers);
        Assert.Contains("wasDeadInDream", dreamHandlers);
        Assert.Contains("SendDreamEndedRejected", dreamHandlers);
        Assert.Contains("dead_in_dream", dreamHandlers);
        Assert.Contains("Rejected story end from p", dreamHandlers);
        Assert.Contains("IsRejectedOutcome", dreamHandlers);

        var policy = ReadMod("CoopPolicy.cs");
        Assert.Contains("ShouldResolveMorningOnDisconnect", policy);
        Assert.Contains("leaverWasNightDead", policy);

        var proxyRelay = ReadMod("Players", "ProxyCombatRelay.cs");
        Assert.Contains("TryMarkGetHitRelay", proxyRelay);

        var proxyDamage = ReadMod("Patches", "ProxyDamagePatch.cs");
        Assert.Contains("ProxyCombatRelay.TryMarkGetHitRelay", proxyDamage);

        var wbUpgrade = ReadMod("Patches", "JournalSyncPatches.cs");
        Assert.Contains("WorkbenchUpgradePatch", wbUpgrade);
        Assert.Contains("SendWorkbenchLevelSync", wbUpgrade);
        Assert.Contains("net.Role == NetworkRole.Host", wbUpgrade);
    }
}
