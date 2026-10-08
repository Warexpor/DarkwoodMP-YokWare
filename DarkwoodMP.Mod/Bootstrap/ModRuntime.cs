using DWMPHorde.Config;
using DWMPHorde.Harmony;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde
{
    /// <summary>
    /// Entry-point runtime for the Darkwood Multiplayer mod.
    /// Owns the Harmony patcher, the network manager, and the in-game UI.
    /// </summary>
    public static class ModRuntime
    {
        /// <summary>Loader-agnostic logger, shared across all systems.</summary>
        public static IModLogger Log;

        /// <summary>The LAN network manager singleton (host or client).</summary>
        /// <summary>The one network manager (null outside a running mod). The manager registers itself in Awake.</summary>
        public static LanNetworkManager Network { get; private set; }

        internal static void AttachNetwork(LanNetworkManager net) => Network = net;

        /// <summary>When true, high-frequency debug logs are emitted (per-frame state, etc.).</summary>
        public static bool VerboseLogging { get; set; }

        /// <summary>
        /// Legacy high-frequency dumps (entity/physics/container).
        /// <b>Dev or Trace</b>. Trace is the max-capture preset (Legacy + Verbose gates).
        /// Support/Public stay quiet — Physics/HostEntitySync can be multi-MB in minutes.
        /// </summary>
        public static void LegacyInfo(string message)
        {
            if (message == null || !LegacyLoggingEnabled) return;
            // Rate-limit: same message prefix at most ~2/sec
            Logging.ModLog.LegacyRateLimited(message);
        }

        /// <summary>
        /// Interpolated-string overload: with the preset below Dev the string is never built, so
        /// the many hot-path <c>LegacyInfo($"...")</c> calls cost one check instead of an allocation.
        /// </summary>
        public static void LegacyInfo(Logging.LegacyLogHandler message)
        {
            if (message.Enabled)
                Logging.ModLog.LegacyRateLimited(message.ToStringAndClear());
        }

        /// <summary>Legacy dumps are on (Dev or Trace preset and a logger bound).</summary>
        public static bool LegacyLoggingEnabled
        {
            get
            {
                if (Log == null) return false;
                var preset = Logging.ModLog.CurrentPreset;
                return preset == Logging.LogPreset.Dev || preset == Logging.LogPreset.Trace;
            }
        }

        private static bool _running;
        private static HarmonyLib.Harmony _harmony;
        private static GameObject _runtimeRoot;
        private static readonly System.Collections.Generic.List<PatchFailure> _patchFailures =
            new System.Collections.Generic.List<PatchFailure>(); // process-scoped

        /// <summary>Patch classes that failed to apply at startup (critical and optional).</summary>
        public static System.Collections.Generic.IReadOnlyList<PatchFailure> PatchFailures => _patchFailures;

        /// <summary>
        /// False until patching ran, and whenever a critical (non-<see cref="OptionalPatchAttribute"/>)
        /// patch class failed. Host/Join must refuse to start a session in that state.
        /// </summary>
        public static bool PatchingHealthy { get; private set; }

        /// <summary>Short status line for menus/logs; empty when every patch applied.</summary>
        public static string PatchStatusText { get; private set; } = "";

        /// <summary>
        /// Session-start gate. Returns false (with a user-facing reason) when critical
        /// patches are missing, so hosting/joining with a half-patched game is refused.
        /// </summary>
        public static bool CanStartSession(out string reason)
        {
            if (PatchingHealthy)
            {
                reason = null;
                return true;
            }
            reason = string.IsNullOrEmpty(PatchStatusText)
                ? "Multiplayer disabled: game patches were not applied (see log)."
                : PatchStatusText;
            ModLog.Warn(LogCat.Core, "Session start refused: " + reason);
            return false;
        }

        /// <summary>
        /// Called by loader entry once on plugin load.
        /// Binds config, registers the session resets, applies all Harmony patches, and boots
        /// the runtime GameObject. Each stage is guarded separately: a failed patch pass must
        /// not leave the reset registry half-filled, and a read-only cfg must not stop patching.
        /// </summary>
        public static void Start(IModLogger log, ModConfigStore config)
        {
            Log = log;

            try
            {
                ModConfig.Bind(config);
                // Allow dual-box path override to re-resolve after config bind.
                PersistentDataPathPatch.ResetCache();

                ModLog.Init(Log);
            }
            catch (System.Exception ex)
            {
                Log?.LogError("FATAL: ModRuntime.Start config/log init failed — mod not started: " + ex);
                return;
            }

            // Resets first: patches may fire the moment PatchAll returns (InputScript.Update),
            // and a network stop must clear every static even if a later stage throws.
            try
            {
                RegisterNetworkResets();
            }
            catch (System.Exception ex)
            {
                Log?.LogError("ModRuntime.Start reset registration failed: " + ex);
                ModLog.Error(LogCat.Core, "ModRuntime.Start reset registration failed", ex);
            }

            try
            {
                _harmony = new HarmonyLib.Harmony(PluginInfo.Guid);
                ApplyPatches();
            }
            catch (System.Exception ex)
            {
                PatchingHealthy = false;
                PatchStatusText = "Multiplayer disabled: Harmony patching crashed (see log).";
                Log?.LogError("FATAL: Harmony patching failed: " + ex);
                ModLog.Error(LogCat.Core, "FATAL: Harmony patching failed", ex);
            }

            try
            {
                ChapterSessionResume.EnsureSceneHook();
                SceneRegistries.Install();

                ModLog.BannerSessionStart();

                EnsureRunning();
            }
            catch (System.Exception ex)
            {
                Log?.LogError("ModRuntime.Start boot failed: " + ex);
                ModLog.Error(LogCat.Core, "ModRuntime.Start boot failed", ex);
            }
        }

        /// <summary>
        /// Patches one class at a time (a single PatchAll stops at the first bad class and
        /// leaves every later one unapplied) and records which classes failed.
        /// </summary>
        private static void ApplyPatches()
        {
            _patchFailures.Clear();
            PatchingHealthy = false;
            var failures = PatchApplier.ApplyAll(_harmony, typeof(ModRuntime).Assembly, out int applied);
            _patchFailures.AddRange(failures);
            int critical = 0;
            for (int i = 0; i < failures.Count; i++)
                if (!failures[i].Optional) critical++;
            PatchingHealthy = critical == 0;
            if (critical > 0)
            {
                PatchStatusText = "Multiplayer disabled: " + critical + " critical game patch(es) failed"
                    + " (mod/game version mismatch?). See log.";
                Log?.LogError("FATAL: " + PatchStatusText + " Hosting and joining are blocked.");
            }
            else if (failures.Count > 0)
                PatchStatusText = failures.Count + " cosmetic patch(es) failed; multiplayer still allowed.";
            else
                PatchStatusText = "";
            ModLog.Event(LogCat.Core, "Harmony: " + applied + " patch classes applied, "
                + critical + " critical / " + (failures.Count - critical) + " optional failures");
        }

        private static void RegisterNetworkResets()
        {
            NetworkResetRegistry.Register(ModLog.ResetRateLimits);

            // Register all static network resets so they fire when the
            // network stops, regardless of where StopNetwork is called.
            NetworkResetRegistry.Register(FlagSyncBoolPatch.Reset);
            NetworkResetRegistry.Register(FlagSyncIntPatch.Reset);
            NetworkResetRegistry.Register(DeathStateTracker.ResetSession);
            NetworkResetRegistry.Register(SharedPermadeathDeath.Reset);
            SharedPermadeathDeath.Install();
            NetworkResetRegistry.Register(ClientEntityInterpolationService.Reset);
            NetworkResetRegistry.Register(EnemyAttackNetHandlers.Reset);
            NetworkResetRegistry.Register(DefenderAttackContext.Reset);
            NetworkResetRegistry.Register(WorldPhysicsSyncService.Reset);
            NetworkResetRegistry.Register(WorldQueryHelper.InvalidateCommonSceneScanCaches);
            NetworkResetRegistry.Register(DreamSyncManager.OnNetworkStopped);
            NetworkResetRegistry.Register(PlayerPositionManager.Clear);
            NetworkResetRegistry.Register(() =>
                ModRuntime.Network?.PlayerFXHandlers?.ResetConsumedDropGuids());
            NetworkResetRegistry.Register(EntityStateBroadcastService.Stop);
            NetworkResetRegistry.Register(MeleeSensorDeduplicatePatch.Reset);
            NetworkResetRegistry.Register(HostMeleeSensorPatch.Reset);
            NetworkResetRegistry.Register(ThreatTriggerContext.Reset);
            NetworkResetRegistry.Register(EventTriggersProxyOccupancy.Reset);
            NetworkResetRegistry.Register(MorningHideoutHold.Reset);
            NetworkResetRegistry.Register(DreamForestSpiritAggro.Reset);
            NetworkResetRegistry.Register(ItemDoublePickupPatch.Reset);
            NetworkResetRegistry.Register(WorldPickupClaimPending.Reset);
            NetworkResetRegistry.Register(NamedNpcScalePatch.Reset);
            NetworkResetRegistry.Register(FinalDreamsceneManager.OnDisconnected);
            NetworkResetRegistry.Register(NetworkApplyGuard.ResetDepth);
            NetworkResetRegistry.Register(PersonalFlavorHud.Reset);
            NetworkResetRegistry.Register(MultiplayerMapManager.Reset);
            NetworkResetRegistry.Register(CharacterTracker.ResetForNetworkStop);
            NetworkResetRegistry.Register(DreamSession.ResetIncludingCompletions);
            NetworkResetRegistry.Register(EpilogueNetHandlers.ResetSceneLoadState);
            NetworkResetRegistry.Register(CutsceneSyncHelpers.Reset);
            NetworkResetRegistry.Register(PersonalPrologue.Reset);
            NetworkResetRegistry.Register(ChapterTransitionHelpers.Reset);
            NetworkResetRegistry.Register(Audio.MovingObjectSoundService.Reset);
            NetworkResetRegistry.Register(Audio.ItemMovingSoundHelper.ResetSuppress);
            NetworkResetRegistry.Register(ItemSoundsUpdateSuppressPatch.Reset);
            NetworkResetRegistry.Register(Audio.LocalAudioService.ResetRateLimits);
            NetworkResetRegistry.Register(Audio.LocalAudioService.ResetPeerHearGates);
            NetworkResetRegistry.Register(Audio.VoiceChatService.Reset);
            NetworkResetRegistry.Register(Patches.DialogueDoorAftermath.Reset);
            NetworkResetRegistry.Register(HostSnifferUpdatePatch.Reset);
            NetworkResetRegistry.Register(PlayerTargetArbiter.Reset);
            NetworkResetRegistry.Register(BarricadeSyncHelpers.Reset);
            // Clear session maps so they cannot leak across reconnects.
            NetworkResetRegistry.Register(ListTracker<Door>.Clear);
            NetworkResetRegistry.Register(ListTracker<Generator>.Clear);
            NetworkResetRegistry.Register(HostCheckStuffPatch.Reset);
            NetworkResetRegistry.Register(GasolineTrailSpawnPatch.Reset);
            NetworkResetRegistry.Register(FastProjectileSweepPatch.Reset);
            NetworkResetRegistry.Register(TradeSyncAcceptPatch.Reset);
            NetworkResetRegistry.Register(ClientSaveBridge.Reset);
            NetworkResetRegistry.Register(DroppedItemIdentifier.ClearRegistry);
            NetworkResetRegistry.Register(DialogOutcomeIndexPatch.ResetCounter);
            NetworkResetRegistry.Register(PauseSuppression.Reset);
            NetworkResetRegistry.Register(DialogHostApplyGuard.Reset);
            NetworkResetRegistry.Register(DialogHostPresentation.Reset);
            NetworkResetRegistry.Register(DialogClientWorldDefer.Reset);
            NetworkResetRegistry.Register(NpcDialogueLock.Reset);
            NetworkResetRegistry.Register(PeerItemPresence.Reset);
            NetworkResetRegistry.Register(OxygenTankParty.Reset);
            NetworkResetRegistry.Register(QuestItemHandoff.Reset);
            NetworkResetRegistry.Register(DialogHandInArbiter.Reset);
            NetworkResetRegistry.Register(DialogMirror.Reset);
            NetworkResetRegistry.Register(DWMPHorde.Patches.DialogPeerTrip.Reset);
            NetworkResetRegistry.Register(StationSyncHelpers.Reset);
            // Chapter resume pending must survive StopNetwork during chapter tear;
            // do NOT register ChapterSessionResume.Reset on network stop.

            // --- Reset registrations ---
            NetworkResetRegistry.Register(ChatHud.Reset);
            NetworkResetRegistry.Register(HostCheckFrequenciesPostfix.Reset);
            NetworkResetRegistry.Register(NightShadowsRateLimit.Reset);
            NetworkResetRegistry.Register(NightShadowsThresholdPatch.Reset);
            NetworkResetRegistry.Register(SessionSettings.ResetToLocal);
            NetworkResetRegistry.Register(PlayerAnimLibraryPatch.Reset);
            NetworkResetRegistry.Register(ClientRandomEventGate.Reset);
            NetworkResetRegistry.Register(BansheeVictims.Reset);
            NetworkResetRegistry.Register(HostLocationLeaveKeepRemotePatch.Reset);
            NetworkResetRegistry.Register(BirdAreaPresence.Reset);
            NetworkResetRegistry.Register(LocationEnterExitNetHandlers.Reset);
            NetworkResetRegistry.Register(ClientAIConditionalHelper.Reset);
            NetworkResetRegistry.Register(NetLogThrottle.Reset);
            NetworkResetRegistry.Register(() =>
                Network?.DialogOutcomeApplyHandlers?.ClearPendingApply());

            // Session caches / pending state in patch classes (StaticStateResetTests guards these).
            NetworkResetRegistry.Register(CanSeeComponentCache.Reset);
            NetworkResetRegistry.Register(HostGridOccupancy.ResetCaches);
            NetworkResetRegistry.Register(NpcAttackedIdSync.ResetPendingVisuals);
            NetworkResetRegistry.Register(WorldBurnSyncHelpers.Reset);
            NetworkResetRegistry.Register(ThrownItemCombatDespawnSyncPatch.Reset);
            NetworkResetRegistry.Register(ChapterTransitionHelpers.ResetSharePasses);
            NetworkResetRegistry.Register(ClientTimeFixedUpdateSuppressPatch.Reset);
            NetworkResetRegistry.Register(HitscanBloodPatch.Reset);
            NetworkResetRegistry.Register(ExplosionSpawnFlagTracker.Reset);
            NetworkResetRegistry.Register(LightStateHelper.ResetTxSignature);
            NetworkResetRegistry.Register(MorningRewardFanOutPatch.Reset);
            NetworkResetRegistry.Register(HostAwayMorning.Reset);
            NetworkResetRegistry.Register(PadWeather.Reset);
            NetworkResetRegistry.Register(NightVillage.Reset);
            NetworkResetRegistry.Register(LocalBearTrap.Reset);
            NetworkResetRegistry.Register(DreamRetry.Reset);
            NetworkResetRegistry.Register(MenuShield.Reset);
            NetworkResetRegistry.Register(PauseMenuSync.Reset);
            NetworkResetRegistry.Register(PauseMenuNoInputPatch.Reset);
            NetworkResetRegistry.Register(StackedLightProbe.Reset);
            NetworkResetRegistry.Register(BackgroundFrameRate.Reset);
            NetworkResetRegistry.Register(DescriptionDeck.Reset);
            NetworkResetRegistry.Register(DesyncCheck.Reset);
            NetworkResetRegistry.Register(PerPlayerTransportOneShots.Reset);
            NetworkResetRegistry.Register(WorkbenchUndo.Reset);
            NetworkResetRegistry.Register(TraderRestockDefer.Reset);
            NetworkResetRegistry.Register(CombatMusicSync.Reset);
            NetworkResetRegistry.Register(TrapLedger.Reset);
            NetworkResetRegistry.Register(SightViewers.Reset);
            NetworkResetRegistry.Register(ClientOwnTrapTriggers.Reset);
            NetworkResetRegistry.Register(() => (Network as LanNetworkManager)?.WorldFxHandlers?.ClearHeldClaims());
            NetworkResetRegistry.Register(ContainerDropItemPatch.Reset);
            NetworkResetRegistry.Register(EpilogueNetHandlers.EpilogueCredits.Reset);
            // A host lost mid-share must not leave the client black and locked.
            NetworkResetRegistry.Register(ChapterWaitScreen.Release);
            NetworkResetRegistry.Register(NightEventAnchor.Reset);
            NetworkResetRegistry.Register(OutsidePadSlots.Reset);
            NetworkResetRegistry.Register(PlayerControlRouter.Reset);
            NetworkResetRegistry.Register(ResetStaticSessionFlags);
        }

        /// <summary>Session flags on LanNetworkManager that no handler clears on stop.</summary>
        private static void ResetStaticSessionFlags()
        {
            // A share/save coroutine torn down mid-run would otherwise block every later save.
            LanNetworkManager.RemoteSaveInProgress = false;
        }

        /// <summary>
        /// Ensure the persistent runtime GameObject exists with all required
        /// components (network manager, menu).
        /// </summary>
        public static void EnsureRunning()
        {
            if (_running)
                return;

            _running = true;

            GameObject root = new GameObject("DWMPHorde_Runtime");
            Object.DontDestroyOnLoad(root);
            _runtimeRoot = root;

            Network = root.AddComponent<LanNetworkManager>();
            // Entity spawner is a separate plugin: YokWare.EntitySpawner.
            root.AddComponent<CursorConfineFocusGuard>();

            MultiplayerMenu.EnsureExists();
            ChatHud.EnsureExists();
            Spectator.SpectatorModeController.EnsureExists();
            ManualSaveGUI.EnsureExists();
            JoinWorldSlotPicker.EnsureExists();
        }

        /// <summary>
        /// Stop the network, unpatch Harmony and destroy the runtime GameObject (called on mod
        /// unload). Without the destroy a re-start would leave a second LanNetworkManager ticking.
        /// </summary>
        public static void Stop()
        {
            Network?.StopNetwork();
            UiInputLock.ReleaseAll();
            _harmony?.UnpatchSelf();
            if (_runtimeRoot != null)
                Object.Destroy(_runtimeRoot);
            _runtimeRoot = null;
            Network = null;
            _running = false;
        }
    }
}
