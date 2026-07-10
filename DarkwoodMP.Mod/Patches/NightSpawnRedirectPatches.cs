using System.Collections.Generic;
using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Redirects Forest Spirit to also spawn around a remote proxy when
    /// it is far from the host, so clients experience these night events
    /// near their position.
    /// </summary>

    internal static class NightSpawnConstants
    {
        /// <summary>Minimum distance from host for a proxy to be considered "far" for night spawn redirection.</summary>
        public const float FarProxyMinDist = 1000f;
    }

    // ─── Forest Spirit redirect ────────────────────────────────────────

    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(CharacterSpawner), "spawnForestSpirit")]
    public static class ForestSpiritRedirectPatch
    {
        private static bool Prefix(CharacterSpawner __instance)
        {
            if (!ShouldRedirect())
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null) return true;

            // Pick a random far proxy to receive the forest spirit
            var farProxies = GetFarProxies(net);
            if (farProxies.Count == 0) return true;

            RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
            Transform proxyT = target.transform;

            Vector3 vector = Random.onUnitSphere * 300f;
            vector.y = 0f;
            Vector3 destPosition = proxyT.position + vector;

            Core.AddPooledPrefab("FX", "ForestSpirit_fastSpawnEff", destPosition, Quaternion.identity);
            __instance.StartCoroutine(DelayedSpawnForestSpirit(destPosition));

            return false;
        }

        private static System.Collections.IEnumerator DelayedSpawnForestSpirit(Vector3 pos)
        {
            yield return new WaitForSeconds(Random.Range(7f, 9f));
            Core.AddPrefab("Characters/ForestSpirit2", pos, Quaternion.Euler(90f, 0f, 0f), null);
        }

        private static bool ShouldRedirect()
        {
            if (ModRuntime.Network?.Role != NetworkRole.Host) return false;
            if (!PlayerPositionManager.HasRemotePlayer) return false;
            if (Player.Instance == null) return false;
            if (LanNetworkManager.Instance == null) return false;
            return GetFarProxies(LanNetworkManager.Instance).Count > 0;
        }

        private static List<RemotePlayerProxy> GetFarProxies(LanNetworkManager net)
        {
            Vector3 hostPos = Player.Instance.transform.position;
            return net.GetAllProxies()
                .Where(p => p != null && Vector3.Distance(p.transform.position, hostPos) >= NightSpawnConstants.FarProxyMinDist)
                .ToList();
        }
    }

    // ─── spawnCharacterAround redirect (covers spawnRedneck, etc.) ────

    [HarmonyPatch(typeof(CharacterSpawner), "spawnCharacterAround")]
    public static class SpawnCharacterAroundRedirectPatch
    {
        private static void Prefix(object[] __args)
        {
            if (ModRuntime.Network?.Role != NetworkRole.Host) return;
            if (NightSpawnGetFreeSpotPatch.InsideNightSpawn) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;

            if (Player.Instance == null) return;

            GameObject destGO = (GameObject)__args[0];
            if (destGO != Player.Instance.gameObject) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            // Redirect to a random far proxy with 50% chance
            var farProxies = net.GetAllProxies()
                .Where(p => p != null && Vector3.Distance(p.transform.position, Player.Instance.transform.position) >= NightSpawnConstants.FarProxyMinDist)
                .ToList();
            if (farProxies.Count == 0) return;

            if (Random.value < 0.5f)
            {
                RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
                __args[0] = target.gameObject;
                ModRuntime.LegacyInfo($"[NightSpawnRedirect] spawnCharacterAround → proxy P{target.PlayerId} at {target.transform.position}");
            }
        }
    }

    // ─── NightWorm redirect (post-spawn reposition) ───────────────────

    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class NightWormPostSpawnPatch
    {
        private static void Postfix(GameObject __result, string prefab)
        {
            if (__result == null || prefab != "characters/fakechars/NightWorms_01")
                return;
            if (ModRuntime.Network?.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            if (Player.Instance == null) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            var farProxies = net.GetAllProxies()
                .Where(p => p != null && Vector3.Distance(p.transform.position, Player.Instance.transform.position) >= NightSpawnConstants.FarProxyMinDist)
                .ToList();
            if (farProxies.Count == 0) return;
            if (Random.value > 0.5f) return;

            RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
            Transform proxyT = target.transform;

            Vector3 newPos = Core.randomPosAround(proxyT.position, 1500f, 2000f, canBeInside: true, mustBeInsideGraph: false);
            __result.transform.position = newPos;

            ModRuntime.LegacyInfo($"[NightWormRedirect] moved worm to proxy area ({newPos.x:F0},{newPos.z:F0})");
        }
    }
}