using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Redirects a portion of night-time enemy spawns to occur around a
    /// remote proxy instead of always around the host player. This ensures
    /// clients see enemies near their character.
    ///
    /// Mechanism: a Prefix on getFreeSpotAround checks a per-frame flag
    /// that is active only during spawnNightChar. When set, ~50% of spawns
    /// use a random far proxy's position as the origin instead of the host player's.
    ///
    /// Because getFreeSpotAround runs before the indoor/outdoor ground
    /// verification, redirected spawns still respect map boundaries.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "getFreeSpotAround", new[] { typeof(GameObject), typeof(float), typeof(bool), typeof(int) })]
    public static class NightSpawnGetFreeSpotPatch
    {
        internal static bool InsideNightSpawn;

        [HarmonyPriority(Priority.First)]
        private static void Prefix(CharacterSpawner __instance, object[] __args)
        {
            if (!InsideNightSpawn)
                return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            if (Player.Instance == null) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            // Collect all far proxies
            var farProxies = net.GetAllProxies()
                .Where(p => p != null && Vector3.Distance(p.transform.position, Player.Instance.transform.position) >= 1000f)
                .ToList();
            if (farProxies.Count == 0) return;

            // ~50% chance: spawn around a random far proxy
            if (Random.value < 0.5f)
            {
                RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
                __args[0] = target.gameObject;
                ModRuntime.LegacyInfo($"[NightSpawnRedirect] getFreeSpotAround → proxy P{target.PlayerId} at {target.transform.position}");
            }
        }
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(CharacterSpawner), "spawnNightChar")]
    public static class NightSpawnFlagPatch
    {
        private static void Prefix()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = true;
            if (ModRuntime.VerboseLogging && Player.Instance != null && LanNetworkManager.Instance != null)
            {
                var net = LanNetworkManager.Instance;
                int proxyCount = net.GetAllProxies().Count();
                var farCount = net.GetAllProxies()
                    .Count(p => p != null && Vector3.Distance(p.transform.position, Player.Instance.transform.position) >= 1000f);
                ModRuntime.LegacyInfo($"[NightSpawn] spawnNightChar started; proxies={proxyCount} far(>1000f)={farCount}");
            }
        }

        private static void Postfix()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
        }

        private static void Finalizer()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
        }
    }
}