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

        /// <summary>Each hideout a living player is home in, with the bodies there (the host's first).</summary>
        internal static void FillByHideout(LanNetworkManager net, Dictionary<Location, List<GameObject>> into)
        {
            foreach (List<GameObject> l in into.Values)
                l.Clear();
            if (HostHome())
                Add(into, Player.Instance.whereAmI.bigLocation, Player.Instance.gameObject);
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
                if (loc != null && loc.bigLocation != null)
                    loc = loc.bigLocation;
                if (loc == null || !loc.playerBase)
                    continue;
                Add(into, loc, proxy.gameObject);
            }
        }

        private static void Add(Dictionary<Location, List<GameObject>> into, Location home, GameObject body)
        {
            if (!into.TryGetValue(home, out List<GameObject> l))
                into[home] = l = new List<GameObject>(2);
            l.Add(body);
        }
    }

    /// <summary>
    /// Each hideout's night monsters (<c>NightScenario.characters</c>: how many of each kind may be
    /// out at once) counted per hideout. Vanilla has one player in one hideout; one shared count
    /// split the night between two hideouts, so players home in different hideouts met half the
    /// monsters each. A monster killed or gone frees its hideout's slot (vanilla
    /// <c>removeFromNightChars</c>).
    /// </summary>
    internal static class NightHideoutQuota
    {
        private static readonly Dictionary<Location, Dictionary<int, int>> _spawned = new Dictionary<Location, Dictionary<int, int>>(); // reset-in: Reset
        private static readonly Dictionary<int, KeyValuePair<Location, int>> _byCreature = new Dictionary<int, KeyValuePair<Location, int>>(); // reset-in: Reset
        private static readonly Dictionary<Location, float> _credit = new Dictionary<Location, float>(); // reset-in: Reset

        internal static void Reset()
        {
            _spawned.Clear();
            _byCreature.Clear();
            _credit.Clear();
        }

        /// <summary>
        /// How many monsters this hideout may place on this spawner tick. Vanilla places one per tick;
        /// the host's night monster multiplier places that many on average, so a bigger night also
        /// fills at the matching pace.
        /// </summary>
        internal static int TakeSpawnsThisTick(Location home, float mult)
        {
            _credit.TryGetValue(home, out float c);
            c += mult;
            int n = Mathf.FloorToInt(c + 0.0001f);
            _credit[home] = c - n;
            return n;
        }

        internal static int Spawned(Location home, int index)
        {
            return home != null && _spawned.TryGetValue(home, out Dictionary<int, int> d) && d.TryGetValue(index, out int n) ? n : 0;
        }

        internal static void Note(Location home, int index, Character c)
        {
            if (home == null || c == null)
                return;
            if (!_spawned.TryGetValue(home, out Dictionary<int, int> d))
                _spawned[home] = d = new Dictionary<int, int>();
            d[index] = Spawned(home, index) + 1;
            _byCreature[c.GetInstanceID()] = new KeyValuePair<Location, int>(home, index);
        }

        internal static void Free(Character c)
        {
            if (c == null || !_byCreature.TryGetValue(c.GetInstanceID(), out KeyValuePair<Location, int> slot))
                return;
            _byCreature.Remove(c.GetInstanceID());
            if (slot.Key != null && _spawned.TryGetValue(slot.Key, out Dictionary<int, int> d) && d.TryGetValue(slot.Value, out int n))
                d[slot.Value] = Mathf.Max(0, n - 1);
        }
    }

    /// <summary>
    /// Host with peers: the night's monsters come to every hideout someone is home in, each at
    /// vanilla's pace and count, around one of the players home there. Out in the forest nobody gets
    /// these (vanilla), only the worm. With no peer, vanilla runs, unless the host's night monster
    /// multiplier (<see cref="CoopBalance.NightMonsterMultiplier"/>) is above 1: then the same
    /// per-hideout path runs for the host alone. The multiplier raises how many of each kind may be
    /// out at once and the pace they come at; it is read every tick, so a change applies mid-night.
    /// </summary>
    [HarmonyPatch(typeof(CharacterSpawner), "spawnNightChar")]
    public static class NightSpawnFlagPatch
    {
        private static readonly Dictionary<Location, List<GameObject>> _homes = new Dictionary<Location, List<GameObject>>(); // process-scoped: scratch, refilled each call

        [HarmonyPriority(Priority.Last)]
        private static bool Prefix(CharacterSpawner __instance)
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = true;
            // A client spawns nothing (ClientDisableNightSpawnPatch); Harmony still runs this prefix.
            if (ClientWorldHelper.IsClient)
                return false;
            float mult = CoopBalance.NightMonsterMultiplier;
            bool coop = NetGuard.ConnectedHost(out LanNetworkManager net) && PlayerPositionManager.HasRemotePlayer;
            if (!coop && mult <= 1f)
                return true;
            if (!__instance.spawnNocturnalCharacters)
                return false;
            if (coop)
            {
                NightBaseBodies.FillByHideout(net, _homes);
            }
            else
            {
                foreach (List<GameObject> l in _homes.Values)
                    l.Clear();
                if (NightBaseBodies.HostHome())
                {
                    Location hostHome = Player.Instance.whereAmI.bigLocation;
                    if (!_homes.TryGetValue(hostHome, out List<GameObject> l))
                        _homes[hostHome] = l = new List<GameObject>(1);
                    l.Add(Player.Instance.gameObject);
                }
            }
            foreach (KeyValuePair<Location, List<GameObject>> home in _homes)
            {
                if (home.Value.Count == 0)
                    continue;
                int n = NightHideoutQuota.TakeSpawnsThisTick(home.Key, mult);
                for (int k = 0; k < n; k++)
                {
                    if (!SpawnAt(__instance, home.Key, home.Value[Random.Range(0, home.Value.Count)], mult, solo: !coop))
                        break;
                }
            }
            return false;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
        }

        [HarmonyPriority(Priority.Last)]
        private static void Finalizer()
        {
            NightSpawnGetFreeSpotPatch.InsideNightSpawn = false;
        }

        /// <summary>
        /// Vanilla <c>spawnNightChar</c> after its gate, for one hideout, around a body there: the first
        /// kind still under its count gets one more. False when nothing was placed (every kind full).
        /// Solo, vanilla's own per-kind count also counts: monsters vanilla placed before the host
        /// raised the multiplier mid-night are not in the hideout's count.
        /// </summary>
        private static bool SpawnAt(CharacterSpawner spawner, Location home, GameObject anchor, float mult, bool solo)
        {
            var ns = Singleton<NightScenarios>.Instance;
            if (ns == null)
                return false;
            if (ns.currentScenario == null)
                ns.setCurrentScenario();
            if (ns.currentScenario == null)
                return false;
            for (int i = 0; i < ns.currentScenario.characters.Count; i++)
            {
                NightScenario.CharacterToSpawn c = ns.currentScenario.characters[i];
                if (c == null || string.IsNullOrEmpty(c.characterName) || c.amount <= 0
                    || Mathf.Max(NightHideoutQuota.Spawned(home, i), solo ? c.spawned : 0) >= CoopBalance.ScaledNightAmount(c.amount, mult))
                    continue;
                Character character = spawner.spawnCharacterAround(anchor, Vector3.zero, 1500f, c.characterName, nocturnal: true);
                if (character != null)
                {
                    // Vanilla gives them the waypoints of the hideout the player stands in.
                    character.setWaypoints(home.waypoints);
                    if (ChapterAboveOne() && Random.Range(0f, 1f) > 0.5f)
                        character.gameObject.AddComponent<ShadowArmor>();
                    c.spawned++;
                    NightHideoutQuota.Note(home, i, character);
                    ModRuntime.LegacyInfo($"[NightSpawn] {c.characterName} at {home.name}");
                }
                return character != null;
            }
            return false;
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

    /// <summary>A night monster gone (vanilla frees its kind's slot): its hideout's slot frees too.</summary>
    [HarmonyPatch(typeof(CharacterSpawner), nameof(CharacterSpawner.removeFromNightChars))]
    public static class NightHideoutQuotaFreePatch
    {
        private static void Postfix(Character _character) => NightHideoutQuota.Free(_character);
    }

    /// <summary>A new day (vanilla clears the night's counts): the hideouts' counts clear too.</summary>
    [HarmonyPatch(typeof(NightScenarios), nameof(NightScenarios.resetScenarios))]
    public static class NightHideoutQuotaResetPatch
    {
        private static void Postfix() => NightHideoutQuota.Reset();
    }

    [HarmonyPatch(typeof(NightScenario), nameof(NightScenario.clearSpawned))]
    public static class NightHideoutQuotaClearPatch
    {
        private static void Postfix() => NightHideoutQuota.Reset();
    }

    /// <summary>
    /// Set while a night monster is placed: the scripted-spawn redirect leaves it alone, and the
    /// spawn keeps its hideout's waypoints.
    /// </summary>
    public static class NightSpawnGetFreeSpotPatch
    {
        internal static bool InsideNightSpawn; // process-scoped: call-scoped, unwound by NightSpawnFlagPatch's Finalizer
    }
}
