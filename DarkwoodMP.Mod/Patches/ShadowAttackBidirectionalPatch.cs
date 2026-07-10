using System.Linq;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(ShadowCreature), "spawnMeleeSensor")]
    public static class ShadowAttackBidirectionalPatch
    {
        private const float LightProtectionSyncRange = 150f;

        private static bool Prefix(ShadowCreature __instance)
        {
            // Proxy-driven shadows use ProxyShadowController.SpawnAttackSensor (own light check).
            if (__instance != null && __instance.GetComponent<ProxyShadowController>() != null)
                return false;

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            Player localPlayer = Player.Instance;
            if (localPlayer == null)
                return true;

            // Local player's own protection always applies
            if (localPlayer.isInLight)
                return false;

            // Check ALL remote proxies for light protection within range (3+ safe).
            var net = (LanNetworkManager)ModRuntime.Network;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                if (!net.IsRemotePlayerHasLightProtection(proxy.PlayerId)) continue;

                float dist = Vector3.Distance(localPlayer.transform.position, proxy.transform.position);
                if (dist <= LightProtectionSyncRange)
                    return false;
            }

            return true;
        }
    }
}