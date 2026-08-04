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

        private static void Postfix(GameEvents __instance, bool __state)
        {
            if (__instance == null) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;

            var net = LanNetworkManager.Instance;
            if (net == null || net.Role != NetworkRole.Host)
                return;

            // Skip echo while applying a received GameEventsFired. Exception: host
            // DialogOutcome world-only apply runs under ProcessInboundMessage's guard
            // but must still fan out door / story GEs to peers.
            if (LanNetworkManager.IsApplyingRemoteState && !DialogHostApplyGuard.Active)
                return;

            // One-shot: skip if already fired before this call.
            if (__state && !__instance.multipleFire)
                return;

            // multipleFire ambient loops already run on clients (Prefix allows them).
            // Rebroadcasting each tick → FindNearest* on client + Dev log spam = periodic hitches.
            if (__instance.multipleFire)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            string eventName = __instance.name ?? "";

            // Host-spawned spirit FX — client rarely has a durable GameEvents at those
            // coords; broadcasting queued FindObjectsOfType forever (dream-end stutter).
            if (eventName.IndexOf("def_glow", System.StringComparison.OrdinalIgnoreCase) >= 0
                || eventName.IndexOf("def_shadow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return;

            // Dream scene can keep ticking one frame after session End — don't fan out.
            if (!string.IsNullOrEmpty(eventName)
                && eventName.IndexOf("dream_", System.StringComparison.OrdinalIgnoreCase) >= 0
                && !DWMPHorde.Sync.DreamSyncManager.IsDreamActive
                && (Dreams.Instance == null || !Dreams.Instance.dreaming))
                return;

            net.SendGameEventsFired(new GameEventsFiredMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                EventName = eventName
            });
            ModRuntime.LegacyInfo("[GameEventsSync] fired at " + key + " name=" + eventName);

            // Dialogue door opens run inside delayed GameEvent coroutines — poll & fan-out.
            DialogueDoorAftermath.OnHostGameEventsFired(eventName);

            // isColliderTrigger / setActive prop parity after host GE (lamp vs bell).
            if (DWMPHorde.Sync.DreamSyncManager.IsDreamActive)
                DWMPHorde.Sync.WorldPhysicsSyncService.HostBroadcastDreamPropColliders();
        }
    }
}
