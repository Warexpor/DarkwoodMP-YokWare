using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Bodies at home at night. Vanilla <c>CharacterSpawner.spawnNightChar</c> spawns the night's
    /// monsters around the local player, and only while that player stands in a hideout
    /// (<c>whereAmI.bigLocation.playerBase</c>, not in a location pad, not dreaming). In co-op every
    /// living player at home counts: the host by vanilla's own test, a peer by the hideout under
    /// its body.
    /// </summary>
    internal static class NightBaseBodies
    {
        private static readonly List<GameObject> Buf = new List<GameObject>(4); // process-scoped: scratch buffer, cleared before each use

        internal static bool HostHome()
        {
            Player p = Player.Instance;
            if (p == null || !p.alive || DeathStateTracker.LocalNightDeath)
                return false;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol != null && ol.playerInOutsideLocation)
                return false;
            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
                return false;
            return p.whereAmI != null && p.whereAmI.bigLocation != null && p.whereAmI.bigLocation.playerBase;
        }

        /// <summary>The host (when home) and every living ready peer standing in a hideout.</summary>
        internal static List<GameObject> Fill(LanNetworkManager net, bool hostHome, out Location peerBase)
        {
            Buf.Clear();
            peerBase = null;
            if (hostHome)
                Buf.Add(Player.Instance.gameObject);
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null || !net.IsPeerReadyForGameplay(proxy.PlayerId))
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive || DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    continue;
                if (!net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) || !st.InOpenWorld)
                    continue;
                Location loc = Location.getAtPos(proxy.transform.position);
                if (loc == null || !loc.playerBase)
                    continue;
                Buf.Add(proxy.gameObject);
                if (peerBase == null)
                    peerBase = loc;
            }
            return Buf;
        }
    }

    /// <summary>
    /// Host: the night's monsters come to whoever is home. With the host home, vanilla runs and
    /// each spawn picks its spot around one of the bodies at home (host or peer). With only peers
    /// home, the same spawn step runs around a peer there (vanilla's gate would have skipped it,
    /// giving a free night). Out in the forest nobody gets these (vanilla), only the worm.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnNightChar")]
    public static class NightSpawnFlagPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(CharacterSpawner __instance)
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = true;
            NightSpawnGetFreeSpotPatch.Anchor = null;
            if (!NetGuard.ConnectedHost(out LanNetworkManager net))
                return true;
            bool hostHome = NightBaseBodies.HostHome();
            List<GameObject> bodies = NightBaseBodies.Fill(net, hostHome, out Location peerBase);
            if (bodies.Count == 0)
                return true;
            GameObject anchor = bodies[Random.Range(0, bodies.Count)];
            if (hostHome)
            {
                NightSpawnGetFreeSpotPatch.Anchor = anchor;
                return true;
            }
            SpawnAround(__instance, anchor, peerBase);
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
            NightSpawnGetFreeSpotPatch.Anchor = null;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Finalizer()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
            NightSpawnGetFreeSpotPatch.Anchor = null;
        }

        /// <summary>Vanilla <c>spawnNightChar</c> after its gate, around a peer at home.</summary>
        private static void SpawnAround(CharacterSpawner spawner, GameObject anchor, Location home)
        {
            if (!spawner.spawnNocturnalCharacters)
                return;
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
                return;
            if (ns.currentScenario == null)
                ns.setCurrentScenario();
            if (ns.currentScenario == null)
                return;
            for (int i = 0; i < ns.currentScenario.characters.Count; i++)
            {
                NightScenario.CharacterToSpawn c = ns.currentScenario.characters[i];
                if (c == null || string.IsNullOrEmpty(c.characterName) || c.amount <= 0 || c.spawned >= c.amount)
                    continue;
                Character character = spawner.spawnCharacterAround(anchor, Vector3.zero, 1500f, c.characterName, nocturnal: true);
                if (character != null)
                {
                    // Vanilla gives them the waypoints of the hideout the player stands in.
                    if (home != null)
                        character.setWaypoints(home.waypoints);
                    if (ChapterAboveOne() && Random.Range(0f, 1f) > 0.5f)
                        character.gameObject.AddComponent<ShadowArmor>();
                    c.spawned++;
                    ModRuntime.LegacyInfo($"[NightSpawn] {c.characterName} at the hideout of a peer (host away)");
                }
                break;
            }
        }

        private static bool ChapterAboveOne()
        {
            if (Core.randomGeneration)
                return Singleton<WorldGenerator>.Instance != null && Singleton<WorldGenerator>.Instance.chapterID > 1;
            GameObject scene = GameObject.Find(Helpers.GetSceneName());
            Location loc = scene != null ? scene.GetComponent<Location>() : null;
            return loc != null && loc.chapterId > 1;
        }
    }

    /// <summary>During <c>spawnNightChar</c>: the spot is picked around the chosen body at home.</summary>
    [HarmonyPatch(typeof(CharacterSpawner), "getFreeSpotAround", new[] { typeof(GameObject), typeof(float), typeof(bool), typeof(int) })]
    public static class NightSpawnGetFreeSpotPatch
    {
        internal static bool InsideNightSpawn; // process-scoped: call-scoped, unwound by its Finalizer/finally
        internal static GameObject Anchor; // process-scoped: call-scoped, unwound with InsideNightSpawn

        [HarmonyPriority(Priority.First)]
        private static void Prefix(object[] __args)
        {
            if (!InsideNightSpawn || Anchor == null || Player.Instance == null)
                return;
            if ((GameObject)__args[0] == Player.Instance.gameObject && Anchor != Player.Instance.gameObject)
                __args[0] = Anchor;
        }
    }
}
