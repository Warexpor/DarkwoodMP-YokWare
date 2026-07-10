using BepInEx.Configuration;
using BepInEx.Logging;
using DWMPHorde.Config;
using DWMPHorde.DebugTools;
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
        /// <summary>BepInEx logger, shared across all systems.</summary>
        public static ManualLogSource Log;

        /// <summary>The LAN network manager singleton (host or client).</summary>
        public static LanNetworkManager Network { get; private set; }

        /// <summary>When true, high-frequency debug logs are emitted (per-frame state, etc.).</summary>
        public static bool VerboseLogging { get; set; }

        /// <summary>
        /// Legacy uncategorized LogInfo: silent on Public/Support presets.
        /// Prefer ModLog.Event/Trace with a LogCat. Enabled only for Dev/Trace presets.
        /// </summary>
        public static void LegacyInfo(string message)
        {
            if (Log == null || message == null) return;
            var preset = Logging.ModLog.CurrentPreset;
            if (preset != Logging.LogPreset.Dev && preset != Logging.LogPreset.Trace)
                return;
            Log.LogInfo(message);
        }

        private static bool _running;
        private static Harmony _harmony;

        /// <summary>
        /// Called by <see cref="DWMPHordeEntry.Awake"/> once on plugin load.
        /// Binds config, applies all Harmony patches, and boots the runtime GameObject.
        /// </summary>
        public static void Start(ManualLogSource log, ConfigFile config)
        {
            Log = log;

            try
            {
                ModConfig.Bind(config);

                ModLog.Init(Log);
                NetworkResetRegistry.Register(ModLog.ResetRateLimits);

                _harmony = new Harmony(PluginInfo.Guid);
                _harmony.PatchAll();

                // Register all static network resets so they fire when the
                // network stops, regardless of where StopNetwork is called.
                NetworkResetRegistry.Register(FlagSyncBoolPatch.Reset);
                NetworkResetRegistry.Register(FlagSyncIntPatch.Reset);
                NetworkResetRegistry.Register(DeathStateTracker.Reset);
                NetworkResetRegistry.Register(ClientEntityInterpolationService.Reset);
                NetworkResetRegistry.Register(WorldPhysicsSyncService.Reset);
                NetworkResetRegistry.Register(DreamSyncManager.OnDisconnected);
                NetworkResetRegistry.Register(PlayerPositionManager.Clear);
                NetworkResetRegistry.Register(LanNetworkManager.ResetConsumedDropGuids);
                NetworkResetRegistry.Register(EntityStateBroadcastService.Stop);
                NetworkResetRegistry.Register(MeleeSensorDeduplicatePatch.Reset);
                NetworkResetRegistry.Register(ClientMeleeSensorPatch.Reset);
                NetworkResetRegistry.Register(ItemDoublePickupPatch.Reset);
                NetworkResetRegistry.Register(NamedNpcScalePatch.Reset);
                NetworkResetRegistry.Register(FreezeTracker.Reset);
                NetworkResetRegistry.Register(FinalDreamsceneManager.OnDisconnected);
                NetworkResetRegistry.Register(NetworkApplyGuard.ResetDepth);
                NetworkResetRegistry.Register(MultiplayerMapManager.Reset);
                NetworkResetRegistry.Register(CharacterTracker.ResetForNetworkStop);
                NetworkResetRegistry.Register(DreamSession.ResetIncludingCompletions);
                NetworkResetRegistry.Register(LanNetworkManager.ResetSceneLoadState);
                NetworkResetRegistry.Register(CutsceneSyncHelpers.Reset);
                NetworkResetRegistry.Register(ChapterTransitionHelpers.Reset);
                NetworkResetRegistry.Register(Audio.MovingObjectSoundService.Reset);
                NetworkResetRegistry.Register(Audio.ItemMovingSoundHelper.ResetSuppress);
                NetworkResetRegistry.Register(Audio.LocalAudioService.ResetRateLimits);
                NetworkResetRegistry.Register(Audio.LocalAudioService.ResetClipCache);
                NetworkResetRegistry.Register(DreamAudioPlayer.Cleanup);
                NetworkResetRegistry.Register(HostSnifferUpdatePatch.Reset);
                NetworkResetRegistry.Register(BarricadeSyncHelpers.Reset);
                // Session maps that previously leaked across reconnects (0.5 audit)
                NetworkResetRegistry.Register(DoorTracker.Clear);
                NetworkResetRegistry.Register(GeneratorTracker.Clear);
                NetworkResetRegistry.Register(HostCheckStuffPatch.Reset);
                NetworkResetRegistry.Register(GasolineTrailSpawnPatch.Reset);
                NetworkResetRegistry.Register(ThrowableSyncPatch.Reset);
                NetworkResetRegistry.Register(TradeSyncAcceptPatch.Reset);
                NetworkResetRegistry.Register(ClientSaveBridge.Reset);
                NetworkResetRegistry.Register(DroppedItemIdentifier.ClearRegistry);
                NetworkResetRegistry.Register(DialogOutcomeIndexPatch.ResetCounter);
                NetworkResetRegistry.Register(PauseSuppression.Reset);

                ModLog.BannerSessionStart();

                EnsureRunning();
            }
            catch (System.Exception ex)
            {
                Log?.LogError("ModRuntime.Start failed: " + ex);
                ModLog.Error(LogCat.Core, "ModRuntime.Start failed", ex);
            }
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

            Network = root.AddComponent<LanNetworkManager>();
            if (ModConfig.EnableDebugTools != null && ModConfig.EnableDebugTools.Value)
                root.AddComponent<EntitySpawnerUI>();

            MultiplayerMenu.EnsureExists();
            Spectator.SpectatorModeController.EnsureExists();
            ManualSaveGUI.EnsureExists();
        }

        /// <summary>Stop the network and unpatch Harmony (called on mod unload).</summary>
        public static void Stop()
        {
            Network?.StopNetwork();
            _harmony?.UnpatchSelf();
            _running = false;
        }
    }
}
