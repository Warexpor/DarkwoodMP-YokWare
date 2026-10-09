using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// The chapter-1 village at night (co-op only). Vanilla never had night there while you were
    /// inside (its clock stopped in outside locations); the shared clock now runs, so:
    /// <list type="bullet">
    /// <item>The friendly villagers are away from the "night is coming" warning until morning
    /// (<see cref="VillageNightPolicy"/>). The Musician, the hostile villagers and the ones
    /// already indoors (most of the sick) stay. Entering
    /// near night finds them gone. With players inside, they leave (or come back at dawn) all at
    /// once, and only while nobody sees any of them: each player reports that on
    /// <c>PlayerState.SeesVillager</c>, using vanilla's own "in sight or close" test.</item>
    /// <item>Indoors in the village shelters a player from the night worm like a lit hideout:
    /// the player carries vanilla's shadow ward effect there.</item>
    /// </list>
    /// The host decides; <c>TimeSync.VillagersAway</c> carries it to every peer and late joiner.
    /// (Vanilla Flags only store flags from the game's own database.) It is not saved: after a
    /// load the host settles it again by the same rule. Hiding is SetActive only; the villagers'
    /// saved state (<c>SaveableObject.Active</c>) is untouched, so story-hidden villagers never
    /// come back by this, and a save never loses a villager.
    /// </summary>
    internal static class NightVillage
    {
        internal const string VillageName = "outside_village_ch1_01";
        // Sight is sampled often so a villager never goes in the gap between a look and its report.
        private const float TickInterval = 0.1f;
        private const float CloseDistance = 1000f; // vanilla Character.inSightOrCloseToPlayer

        private static readonly HashSet<GameObject> Hidden = new HashSet<GameObject>();
        private static readonly List<Character> Villagers = new List<Character>(); // reset-in: Reset
        private static Location _villagersOf;
        private const float ArrivalGrace = 3f;
        private static float _nextTick;
        private static float _arrivedAt = -1f;
        private const float WardDuration = 20000000f; // marks the village ward (see RemoveWard)

        /// <summary>Local player is in the village and sees (or stands close to) a villager.</summary>
        internal static bool LocalSeesVillager { get; private set; }

        /// <summary>Friendly villagers are away (host decision, mirrored from TimeSync on clients).</summary>
        internal static bool Away { get; private set; }

        internal static bool IsHidden(GameObject go) => go != null && Hidden.Count > 0 && Hidden.Contains(go);

        /// <summary>Client: host TimeSync value.</summary>
        internal static void SetAway(bool away)
        {
            if (away == Away)
                return;
            Away = away;
            Apply();
        }

        internal static void Reset()
        {
            Away = false;
            Show();
            Hidden.Clear();
            Villagers.Clear();
            _villagersOf = null;
            _nextTick = 0f;
            _arrivedAt = -1f;
            LocalSeesVillager = false;
            RemoveWard();
        }

        internal static void Tick(LanNetworkManager net)
        {
            float now = Time.unscaledTime;
            if (now < _nextTick)
                return;
            _nextTick = now + TickInterval;
            if (net == null || !net.IsConnected || Player.Instance == null || Core.loadingGame)
                return;

            bool inVillage = LocalInVillage();
            if (!inVillage)
                _arrivedAt = -1f;
            else if (_arrivedAt < 0f)
                _arrivedAt = now;
            // Just arrived counts as seeing: the host may learn of the arrival before the first
            // sight report, and nothing may vanish in front of someone walking in.
            LocalSeesVillager = inVillage
                && (now - _arrivedAt < ArrivalGrace || SeesAny(Player.Instance, LiveVillage()));
            UpdateWard(inVillage);
            if (net.Role == NetworkRole.Host)
                HostDecide(net, inVillage);
        }

        /// <summary>Apply <see cref="Away"/> to this peer's copy of the village (change, village entered).</summary>
        internal static void Apply()
        {
            if (Away && NetGuard.Connected(out _))
                Hide();
            else
                Show();
        }

        private static void HostDecide(LanNetworkManager net, bool hostInVillage)
        {
            Controller ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && (dreams.dreaming || dreams.dreamPrepared))
                return;

            bool away = Away;
            bool want = VillageNightPolicy.IsNearNight(ctrl.CurrentTime, (int)ctrl.nightTime, (int)ctrl.dayTime);
            bool occupied = hostInVillage;
            bool anyoneSees = LocalSeesVillager;
            foreach (var kvp in net.RemoteOutsideLocation)
            {
                if (!CoopWorldPresencePolicy.LocationNamesMatch(kvp.Value, VillageName))
                    continue;
                occupied = true;
                if (net.RemotePlayers.TryGetValue(kvp.Key, out RemotePlayerState st) && st.SeesVillager)
                    anyoneSees = true;
            }
            if (!VillageNightPolicy.ShouldFlip(away, want, occupied, anyoneSees))
                return;

            Away = want;
            Apply();
            net.SendTimeSyncTo(-1);
            Logging.ModLog.Event(Logging.LogCat.World, $"[NightVillage] villagers {(want ? "away for the night" : "back for the day")} (occupied={occupied})");
        }

        private static void Hide()
        {
            Location loc = LiveVillage();
            if (loc == null)
                return;
            foreach (Character c in VillagersOf(loc))
            {
                if (c == null || !c.alive || !StoryActive(c))
                    continue;
                Hidden.Add(c.gameObject);
                if (c.gameObject.activeSelf)
                    c.gameObject.SetActive(false);
            }
        }

        private static void Show()
        {
            if (Hidden.Count == 0)
                return;
            foreach (GameObject go in Hidden)
            {
                if (go == null)
                    continue;
                Character c = go.GetComponent<Character>();
                // A story event that removed it while it was away keeps it removed.
                if (c != null && c.alive && StoryActive(c) && !go.activeSelf)
                    go.SetActive(true);
            }
            Hidden.Clear();
        }

        /// <summary>Saved visibility (story / location state), which culling never changes.</summary>
        private static bool StoryActive(Character c)
        {
            SaveableObject so = c.GetComponent<SaveableObject>();
            return so == null || so.Active;
        }

        private static bool LocalInVillage()
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.playerInOutsideLocation
                && CoopWorldPresencePolicy.LocationNamesMatch(ol.currentLocationName ?? "", VillageName);
        }

        internal static bool IsVillage(Location loc) => loc == LiveVillage();

        private static Location LiveVillage()
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null ? LocationEnterExitNetHandlers.ResolveOutsideLocation(ol, VillageName) : null;
        }

        private static List<Character> VillagersOf(Location loc)
        {
            if (loc == _villagersOf)
                return Villagers;
            Villagers.Clear();
            int home = 0;
            bool anyGround = false;
            foreach (Character c in loc.GetComponentsInChildren<Character>(includeInactive: true))
            {
                if (c.faction != Faction.villagerNeutral)
                    continue;
                if (c.name.IndexOf("musician", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                // Already home: the sick lying indoors. All are villagerNeutral, as the walkers are.
                if (Indoors(c.transform.position, out bool hitGround))
                {
                    home++;
                    continue;
                }
                anyGround |= hitGround;
                Villagers.Add(c);
            }
            if (!anyGround && home == 0)
            {
                // The pad's floors are not live here yet (not entered): indoors cannot be told,
                // so hide nobody now; entering the village applies the state again.
                Villagers.Clear();
                return Villagers;
            }
            _villagersOf = loc;
            Logging.ModLog.Event(Logging.LogCat.World, $"[NightVillage] {Villagers.Count} friendly villagers in '{loc.name}' ({home} indoors stay)");
            return Villagers;
        }

        /// <summary>Vanilla <c>Character.inSightOrCloseToPlayer</c>, for away villagers too (their spot).</summary>
        private static bool SeesAny(Player p, Location loc)
        {
            if (loc == null)
                return false;
            Vector3 pos = p._transform.position;
            foreach (Character c in VillagersOf(loc))
            {
                if (c == null || !c.alive || !StoryActive(c))
                    continue;
                Transform t = c.transform;
                if (Core.trueDistance(pos, t.position) < CloseDistance || p.isInSight(t, canBeFarAway: true))
                    return true;
            }
            return false;
        }

        /// <summary>Indoors in the village: vanilla's shadow ward keeps the worm (and immortal shadows) off.</summary>
        private static void UpdateWard(bool inVillage)
        {
            Player p = Player.Instance;
            if (p.effects == null)
                return;
            if (inVillage && Indoors(p._transform.position, out _))
            {
                if (!p.effects.hasEffectType(CharacterEffectType.shadowWard))
                    p.effects.activate(new InvItemEffect { type = CharacterEffectType.shadowWard, duration = WardDuration });
            }
            else
            {
                RemoveWard();
            }
        }

        /// <summary>
        /// Our ward is told apart from the hideout's by its duration, not by a flag: a session
        /// backup restores effects, and a flag-tracked ward restored that way was never removed.
        /// </summary>
        private static void RemoveWard()
        {
            Player p = Player.Instance;
            if (p == null || p.effects == null)
                return;
            CharacterEffect e = p.effects.getEffect(CharacterEffectType.shadowWard);
            if (e != null && e.duration == WardDuration)
                p.effects.deleteThisTypeOfEffect(CharacterEffectType.shadowWard);
        }

        /// <summary>Vanilla <c>CharBase.checkGround</c>: an indoor floor under the player.</summary>
        private static bool Indoors(Vector3 pos, out bool hitGround)
        {
            hitGround = false;
            if (!Physics.Raycast(pos + new Vector3(0f, 300f, 0f), Vector3.down, out RaycastHit hit, 1000f, 2))
                return false;
            Ground g = hit.collider.GetComponent<Ground>();
            hitGround = g != null;
            return g != null && g.isInside;
        }
    }

    /// <summary>The village was entered or (re)spawned on this peer: apply the current state.</summary>
    [HarmonyPatch(typeof(Location), "enter")]
    public static class NightVillageEnterPatch
    {
        private static void Postfix(Location __instance)
        {
            if (NightVillage.Away && __instance != null && NightVillage.IsVillage(__instance))
                NightVillage.Apply();
        }
    }

    /// <summary>
    /// Location activation turns its characters back on (non-NPCs here, the rest through
    /// culling): keep away villagers off in the same frame.
    /// </summary>
    [HarmonyPatch(typeof(Location), "activateCharacters")]
    public static class NightVillageActivatePatch
    {
        private static void Postfix(Location __instance, bool active)
        {
            if (active && NightVillage.Away && __instance != null && NightVillage.IsVillage(__instance))
                NightVillage.Apply();
        }
    }

    /// <summary>WorldGrid culling must not bring an away villager back into view.</summary>
    [HarmonyPatch(typeof(WorldGrid.Cullable), "show")]
    public static class NightVillageCullShowPatch
    {
        private static bool Prefix(WorldGrid.Cullable __instance)
            => !NightVillage.IsHidden(__instance.GO);
    }
}
