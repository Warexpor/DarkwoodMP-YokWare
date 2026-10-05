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

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            string eventName = __instance.name ?? "";

            if (IsHostOnlyFx(eventName))
                return;

            // Dream scene can keep ticking one frame after session End — don't fan out.
            if (!string.IsNullOrEmpty(eventName)
                && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0
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
                net.GameEventHandlers.RecordDestroyedOnFireGameEvent(firedMsg);
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
