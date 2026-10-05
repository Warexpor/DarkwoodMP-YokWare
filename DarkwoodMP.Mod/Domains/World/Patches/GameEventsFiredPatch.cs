using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative GameEvents one-shots:
    /// - Host fires → Broadcast GameEventsFired (pos + name) → clients fire local copy.
    /// - Clients do not run one-shot fires locally (except compressor + apply path).
    /// </summary>
    [HarmonyPatch(typeof(GameEvents), "fire")]
    public static class GameEventsFiredPatch
    {
        /// <summary>
        /// Client: block one-shot world fires when multiplayer is live so only host
        /// runs them and syncs. Compressor is exempt (2.8 convert path).
        /// </summary>
        private static bool Prefix(GameEvents __instance, out bool __state)
        {
            __state = __instance != null && __instance.fired;

            if (__instance == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            // NetworkApplyGuard.IsActive is the durable signal — explicit flag alone can
            // be cleared by nested finally blocks while a guard is still on the stack.
            if (LanNetworkManager.IsApplyingRemoteState || NetworkApplyGuard.IsActive)
                return true;

            if (ModRuntime.Network.Role == NetworkRole.Client)
            {
                // Compressor GameEvents still run on client for convert FX + 2.8 detect.
                if (CompressorSyncHelpers.IsCompressorGameEvents(__instance))
                    return true;
                // multipleFire can re-run (ambient loops); still prefer host for one-shots.
                if (!__instance.multipleFire)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Host-spawned spirit FX: a client rarely has a durable GameEvents at those coords, so
        /// they are never fanned out (broadcasting queued FindObjectsOfType forever, dream-end
        /// stutter) and peers do not replay them.
        /// </summary>
        internal static bool IsHostOnlyFx(string eventName)
        {
            return !string.IsNullOrEmpty(eventName)
                && (eventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || eventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void Postfix(GameEvents __instance, bool __state)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            if (!NetGuard.Host(out var net))
                return;

            // Skip echo while applying a received GameEventsFired. Exception: host
            // DialogOutcome world-only apply runs under ProcessInboundMessage's guard
            // but must still fan out door / story GEs to peers.
            if (LanNetworkManager.IsApplyingRemoteState && !HostApplyGuard.Active)
                return;

            // One-shot: skip if already fired before this call.
            if (__state && !__instance.multipleFire)
                return;

            // multipleFire ambient loops already run on clients (Prefix allows them).
            // Rebroadcasting each tick → FindNearest* on client + Dev log spam = periodic hitches.
            // A player's use or examine is the exception: clients never run those locally.
            if (__instance.multipleFire && !PlayerUseScope.Active)
                return;
            // A night scene's copy: clients replay the scene where they are (ScenarioEventFired).
            if (NightEventAnchor.PlayingScene)
                return;
            // The host's own prologue: its pads exist on its machine only.
            if (PersonalPrologue.IsOnProloguePad(__instance.transform))
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            string eventName = __instance.name ?? "";

            if (IsHostOnlyFx(eventName))
                return;

            // Dream scene can keep ticking one frame after session End — don't fan out. Its
            // events are the ones on the dream pad: a name with "dream_" in it is not one (the
            // church entrance and priest after the church dream, the hideout and oneChance
            // aftermaths, the cellar's dream start were all dropped for every peer).
            bool onDreamPad = DWMPHorde.Sync.DreamSyncManager.IsOnDreamPad(__instance.transform);
            if (onDreamPad
                && !DWMPHorde.Sync.DreamSyncManager.IsDreamActive
                && (Dreams.Instance == null || !Dreams.Instance.dreaming))
                return;

            var firedMsg = new GameEventsFiredMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                EventName = eventName,
                ActorPlayerId = GeFireActorContext.PeekOr(net.LocalPlayerId)
            };
            net.SendGameEventsFired(firedMsg);
            // destroyOnFire schedules Destroy(gameObject) after event delays — gone
            // from late-join FindObjectsOfType scan; keep identity for GameEventsBulk.
            if (__instance.destroyOnFire && net.GameEventHandlers != null)
                net.GameEventHandlers.RecordDestroyedOnFireGameEvent(firedMsg, onDreamPad);
            ModRuntime.LegacyInfo("[GameEventsSync] fired at " + key + " name=" + eventName
                + (__instance.destroyOnFire ? " (destroyOnFire)" : ""));

            // Dialogue door opens run inside delayed GameEvent coroutines — poll & fan-out.
            DialogueDoorAftermath.OnHostGameEventsFired(eventName);

            // isColliderTrigger / setActive prop parity after host GE (lamp vs bell).
            if (DWMPHorde.Sync.DreamSyncManager.IsDreamActive)
                DWMPHorde.Sync.WorldPhysicsSyncService.HostBroadcastDreamPropColliders();
        }
    }
}

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A world saved while an event's later steps were still waiting keeps those steps as a
    /// "saved delayed event" that fires on load (vanilla GameEvents init). A client loading the
    /// host's world had that fire blocked like any client one-shot, and the host had broadcast the
    /// original fire long before: the waiting steps (a door, a light, a scene step) never happened
    /// for the joiner. They run here as a replay of the host's world (no actor: personal steps,
    /// spawns and counters are left out by the replay rules).
    /// </summary>
    [HarmonyPatch(typeof(GameEvents), "fire")]
    public static class SavedDelayedEventClientPatch
    {
        [HarmonyPriority(Priority.First)]
        private static void Prefix(GameEvents __instance, out NetworkApplyGuard __state)
        {
            __state = null;
            var net = ModRuntime.Network;
            if (__instance == null || !__instance.isSavedDelayedEvent || net == null || !net.IsConnected
                || net.Role != NetworkRole.Client || NetworkApplyGuard.IsActive)
                return;
            __state = new NetworkApplyGuard();
        }

        private static void Finalizer(NetworkApplyGuard __state) => __state?.Dispose();
    }
}
