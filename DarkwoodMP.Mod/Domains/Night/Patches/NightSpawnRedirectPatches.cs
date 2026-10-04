using System.Collections;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Harmony;
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

    [HarmonyPatch(typeof(CharacterSpawner), "spawnForestSpirit")]
    public static class ForestSpiritRedirectPatch
    {
        // spawnForestSpirit is IEnumerator; return false without __result → StartCoroutine(null).
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(CharacterSpawner __instance, ref IEnumerator __result)
        {
            if (!ShouldRedirect())
                return true;

            var net = ModRuntime.Network;
            if (net == null) return true;

            var farProxies = NightSpawnFarProxies.Fill(net, Player.Instance.transform.position);
            if (farProxies.Count == 0) return true;

            // Same coin as the other night redirects. Always stealing the
            // spirit meant the host never saw it when the party was split.
            if (Random.value >= 0.5f)
                return true;

            RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
            Transform proxyT = target.transform;

            Vector3 vector = Random.onUnitSphere * 300f;
            vector.y = 0f;
            Vector3 destPosition = proxyT.position + vector;

            Core.AddPooledPrefab("FX", "ForestSpirit_fastSpawnEff", destPosition, Quaternion.identity);
            __instance.StartCoroutine(DelayedSpawnForestSpirit(destPosition));

            __result = HarmonyCoroutineUtil.Empty();
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
            if (ModRuntime.Network == null) return false;
            return NightSpawnFarProxies.Fill(ModRuntime.Network, Player.Instance.transform.position).Count > 0;
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

            var net = ModRuntime.Network;
            if (net == null) return;

            var farProxies = NightSpawnFarProxies.Fill(net, Player.Instance.transform.position);
            if (farProxies.Count == 0) return;

            if (Random.value < 0.5f)
            {
                RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
                __args[0] = target.gameObject;
                ModRuntime.LegacyInfo($"[NightSpawnRedirect] spawnCharacterAround → proxy P{target.PlayerId} at {target.transform.position}");
            }
        }

        /// <summary>
        /// Vanilla copies host <c>whereAmI.bigLocation.waypoints</c> — event dogs then
        /// patrol toward the host macro map. Clear for temp MP spawns.
        /// </summary>
        private static void Postfix(Character __result)
        {
            if (__result == null || !__result.temporarySpawned) return;
            if (ModRuntime.Network?.Role != NetworkRole.Host) return;
            if (!PlayerPositionManager.HasRemotePlayer) return;
            if (__result.waypoints != null)
                __result.waypoints.Clear();
        }
    }

    // ─── NightWorm redirect (post-spawn reposition) ───────────────────

    /// <remarks>Applied from <see cref="CoreAddPrefabStringPatch"/> (one detour for all features).</remarks>
    public static class NightWormPostSpawnPatch
    {
        internal static void OnAddPrefab(GameObject __result, string prefab)
        {
            if (__result == null || prefab != "characters/fakechars/NightWorms_01")
                return;
            if (HardNightPartySpawn.Placing)
                return;
            if (ModRuntime.Network?.Role != NetworkRole.Host)
                return;
            if (!PlayerPositionManager.HasRemotePlayer)
                return;

            if (Player.Instance == null) return;

            var net = ModRuntime.Network;
            if (net == null) return;

            var farProxies = NightSpawnFarProxies.Fill(net, Player.Instance.transform.position);
            if (farProxies.Count == 0) return;
            if (Random.value > 0.5f) return;

            RemotePlayerProxy target = farProxies[Random.Range(0, farProxies.Count)];
            Transform proxyT = target.transform;

            Vector3 newPos = Core.randomPosAround(proxyT.position, 1500f, 2000f, canBeInside: true, mustBeInsideGraph: false);
            __result.transform.position = newPos;

            ModRuntime.LegacyInfo($"[NightWormRedirect] moved worm to proxy area ({newPos.x:F0},{newPos.z:F0})");
        }
    }

    /// <summary>
    /// Hard-night worm: vanilla gates on the host body only. Pick one living
    /// player without shadow ward (host or proxy) and attack that body.
    /// </summary>
    public static class HardNightPartySpawn
    {
        public static bool Placing { get; private set; }

        private struct Body
        {
            public Vector3 Pos;
            public Transform Attack;
        }

        public static IEnumerator WormLoop(CharacterSpawner spawner)
        {
            var wait = new WaitForSeconds(5f);
            while (spawner != null)
            {
                yield return wait;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl == null || !ctrl.isHardNight || Core.isDay())
                    continue;
                if (Singleton<Dreams>.Instance != null && Singleton<Dreams>.Instance.dreaming)
                    continue;
                if (!TryPickUnwardedBody(out Body body))
                    continue;

                Vector3 position = Core.randomPosAround(body.Pos, 1500f, 2000f, canBeInside: true, mustBeInsideGraph: false);
                GameObject go;
                Placing = true;
                try
                {
                    go = Core.AddPrefab(
                        "characters/fakechars/NightWorms_01",
                        position,
                        Quaternion.Euler(90f, Random.Range(0, 360), 0f),
                        null);
                }
                finally
                {
                    Placing = false;
                }

                if (go == null) continue;
                Character component = go.GetComponent<Character>();
                if (component != null && body.Attack != null)
                    component.attackCharacter(body.Attack);
                if (spawner.nocturnalCharacters != null)
                    spawner.nocturnalCharacters.Add(go);
            }
        }

        private static bool TryPickUnwardedBody(out Body picked)
        {
            picked = default;
            var choices = new List<Body>();

            Player host = Player.Instance;
            if (host != null && HostEligible(host))
            {
                Transform t = host._transform != null ? host._transform : host.transform;
                choices.Add(new Body { Pos = t.position, Attack = t });
            }

            var net = ModRuntime.Network;
            if (net != null)
            {
                foreach (RemotePlayerProxy proxy in net.GetAllProxies())
                {
                    if (proxy == null || proxy.RemoteHasShadowWard) continue;
                    if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId)) continue;
                    // The worm hunts the open world; a peer inside a location pad is out of reach.
                    if (!net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) || !st.InOpenWorld) continue;
                    CharBase cb = proxy.CachedCharBase;
                    if (cb != null && !cb.alive) continue;
                    choices.Add(new Body { Pos = proxy.transform.position, Attack = proxy.transform });
                }
            }

            if (choices.Count == 0) return false;
            picked = choices[Random.Range(0, choices.Count)];
            return true;
        }

        private static bool HostEligible(Player host)
        {
            if (host.ignoreNightSickness) return false;
            if (!HostSharedClockPatch.LocalInOpenWorld()) return false;
            if (DeathStateTracker.LocalNightDeath) return false;
            if (host.effects != null && host.effects.hasEffectType(CharacterEffectType.shadowWard))
                return false;
            CharBase cb = host.GetComponent<CharBase>();
            return cb == null || cb.alive;
        }
    }
}
