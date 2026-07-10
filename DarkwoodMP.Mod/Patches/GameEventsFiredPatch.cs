using DWMPHorde.Networking;
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
            if (LanNetworkManager.IsApplyingRemoteState)
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

            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            // One-shot: skip if already fired before this call.
            // multipleFire: still broadcast every successful fire (__state may be true).
            if (__state && !__instance.multipleFire)
                return;

            Vector3 p = __instance.transform.position;
            Vector3 key = new Vector3(
                Mathf.Round(p.x * 10f) / 10f,
                Mathf.Round(p.y * 10f) / 10f,
                Mathf.Round(p.z * 10f) / 10f);

            net.SendGameEventsFired(new GameEventsFiredMessage
            {
                PosX = key.x,
                PosY = key.y,
                PosZ = key.z,
                EventName = __instance.name ?? ""
            });
            ModRuntime.LegacyInfo("[GameEventsSync] fired at " + key + " name=" + __instance.name);
        }
    }
}
