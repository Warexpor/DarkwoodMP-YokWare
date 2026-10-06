using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The one place that decides which player body (the host or a stand-in) a host creature
    /// targets when several are around. Every mod path that points a creature at a player goes
    /// through here, and the arbiter runs after vanilla's own target writers (the sight check
    /// <c>canSeeEnemy</c> and the closer-enemy check). The decision itself is
    /// <see cref="PlayerTargetPolicy"/>; this class feeds it the bodies, distances and what the
    /// creature senses, keeps a little per-creature memory (when its target was last sensed, when it
    /// last switched), and logs every switch between two player bodies (<c>[TargetSwitch]</c>,
    /// AI trace). Targets that are not players (other factions, doors, windows, lures) stay
    /// vanilla's. Without remote players nothing here runs.
    /// </summary>
    internal static class PlayerTargetArbiter
    {
        private sealed class State
        {
            /// <summary>The target as the arbiter last saw it (finds writers that bypass it).</summary>
            public Transform Last;
            /// <summary>The player body <see cref="LastSensed"/> belongs to.</summary>
            public Transform SensedBody;
            public float LastSensed = -999f;
            public float LastSwitch = -999f;
            /// <summary>0 not checked yet, 1 the bunker dream forest spirit, -1 anything else.</summary>
            public sbyte Spirit;
        }

        private const int MaxStates = 4096;
        private static readonly Dictionary<int, State> _states = new Dictionary<int, State>();

        // Scratch rebuilt on every call (host first when present, then every stand-in).
        private static readonly List<Transform> _bodies = new List<Transform>(4); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<CharBase> _bodyCbs = new List<CharBase>(4); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<int> _bodyIds = new List<int>(4); // process-scoped: scratch buffer, cleared before each use (-1 = host)
        private static PlayerTargetCandidate[] _cands = new PlayerTargetCandidate[4]; // process-scoped: scratch buffer, grown on demand

        // Call-scoped tag of the mod path inside Commit (restored in its finally).
        private static string _source; // process-scoped: call-scoped, restored by Commit's finally
        private static PlayerTargetReason _reason; // process-scoped: call-scoped, restored by Commit's finally

        /// <summary>Registered with NetworkResetRegistry.</summary>
        internal static void Reset()
        {
            _states.Clear();
        }

        /// <summary>The mod path running <see cref="Commit"/> right now, or null.</summary>
        internal static string Source => _source;

        // ---------------------------------------------------------------- bodies

        /// <summary>
        /// The host's body. <c>Player._transform</c> is the same transform, except while
        /// <see cref="HostBodySwap"/> points it at a stand-in for one vanilla call, so it is not used.
        /// </summary>
        internal static bool IsHostBody(Transform t)
        {
            if (t == null)
                return false;
            Player host = Player.Instance;
            return host != null && t == host.transform;
        }

        /// <summary>The host player or a remote player's stand-in.</summary>
        internal static bool IsPlayerBody(Transform t)
        {
            if (t == null)
                return false;
            return IsHostBody(t) || CanSeeComponentCache.IsProxy(t);
        }

        private static int CollectBodies()
        {
            _bodies.Clear();
            _bodyCbs.Clear();
            _bodyIds.Clear();
            Player host = Player.Instance;
            if (host != null)
            {
                _bodies.Add(host.transform);
                _bodyCbs.Add(CanSeeComponentCache.HostCharBase());
                _bodyIds.Add(-1);
            }
            var net = ModRuntime.Network;
            if (net != null)
            {
                foreach (RemotePlayerProxy proxy in net.GetAllProxies())
                {
                    if (proxy == null)
                        continue;
                    _bodies.Add(proxy.transform);
                    _bodyCbs.Add(proxy.CachedCharBase);
                    _bodyIds.Add(proxy.PlayerId);
                }
            }
            int n = _bodies.Count;
            if (_cands.Length < n)
                _cands = new PlayerTargetCandidate[n * 2];
            return n;
        }

        /// <summary>Alive, visible to AI, not ignored, not a night corpse.</summary>
        private static bool BodyValid(int i)
        {
            CharBase cb = _bodyCbs[i];
            if (cb == null || !cb.alive || cb.invisible || cb.ignoreMe)
                return false;
            int id = _bodyIds[i];
            return id < 0 ? !DeathStateTracker.LocalNightDeath : !DeathStateTracker.IsRemoteNightDead(id);
        }

        private static int IndexOf(Transform t)
        {
            if (t == null)
                return -1;
            for (int i = 0; i < _bodies.Count; i++)
            {
                if (_bodies[i] == t)
                    return i;
            }
            return -1;
        }

        private static State StateOf(Character c)
        {
            int id = c.GetInstanceID();
            if (!_states.TryGetValue(id, out State s))
            {
                if (_states.Count >= MaxStates)
                    _states.Clear();
                s = new State();
                _states[id] = s;
            }
            return s;
        }

        /// <summary>The bunker dream forest spirit stays on the player who triggered it.</summary>
        private static int PinnedIndex(Character c, State s)
        {
            if (s.Spirit == 0)
                s.Spirit = DreamForestSpiritAggro.IsBunkerDreamSpirit(c) ? (sbyte)1 : (sbyte)-1;
            if (s.Spirit < 0)
                return -1;
            return IndexOf(DreamForestSpiritAggro.TryGetStickyTarget());
        }

        private static float SinceSensed(State s, Transform current)
            => s.SensedBody == current && current != null ? Time.time - s.LastSensed : float.MaxValue;

        // ---------------------------------------------------------------- decisions

        /// <summary>
        /// Which player body <paramref name="c"/> should be after, among those in its sight list
        /// (<c>charactersInSight</c>, stand-ins the mod sensed included). <paramref name="current"/>
        /// is its player target before the writer ran (any other value counts as none). Null: leave
        /// the target alone. <paramref name="chosenSensed"/>: the chosen body is in sight now.
        /// </summary>
        internal static Transform Choose(Character c, Transform current, bool switchWindow,
            out PlayerTargetReason reason, out bool chosenSensed)
        {
            reason = PlayerTargetReason.None;
            chosenSensed = false;
            if (c == null)
                return null;
            int n = CollectBodies();
            Vector3 from = c.transform.position;
            for (int i = 0; i < n; i++)
            {
                bool valid = BodyValid(i);
                _cands[i] = new PlayerTargetCandidate
                {
                    Valid = valid,
                    Distance = Core.trueDistance(from, _bodies[i].position),
                    Sensed = valid && c.charactersInSight.Contains(_bodyCbs[i]),
                };
            }
            State s = StateOf(c);
            int cur = IndexOf(current);
            bool held = cur >= 0 && PlayerTargetPolicy.CurrentHeld(_cands[cur].Valid, _cands[cur].Sensed, SinceSensed(s, current));
            int idx = PlayerTargetPolicy.Choose(_cands, n, cur, held, PinnedIndex(c, s), switchWindow,
                Time.time - s.LastSwitch, out reason);
            if (idx < 0)
                return null;
            chosenSensed = _cands[idx].Sensed;
            return _bodies[idx];
        }

        /// <summary>
        /// "The player" for a vanilla call that names <c>Player.Instance</c> without sensing anyone
        /// (<c>attackPlayer</c>, scripted activities, the ward and constant-attack checks): the body
        /// the creature is bound to, else the player body it is already after, else the nearest
        /// living one. One player: the host, as vanilla.
        /// </summary>
        internal static Transform ScriptedPick(Character c)
        {
            if (c == null)
                return NearestValid(Vector3.zero);
            int n = CollectBodies();
            State s = StateOf(c);
            int pinned = PinnedIndex(c, s);
            if (pinned >= 0 && BodyValid(pinned))
                return _bodies[pinned];
            int cur = IndexOf(c.target);
            if (cur >= 0 && BodyValid(cur))
                return _bodies[cur];
            return NearestOf(c.transform.position, n);
        }

        /// <summary>The nearest living player body that AI can target, or null.</summary>
        internal static Transform NearestValid(Vector3 from) => NearestOf(from, CollectBodies());

        private static Transform NearestOf(Vector3 from, int n)
        {
            Transform best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (!BodyValid(i))
                    continue;
                float d = Core.trueDistance(from, _bodies[i].position);
                if (d < bestD)
                {
                    bestD = d;
                    best = _bodies[i];
                }
            }
            return best;
        }

        /// <summary>
        /// The body a sniffer starts sniffing: among the bodies inside its smell radius, the one it is
        /// already after, else the nearest. False when nobody is inside it. <paramref name="playerId"/>
        /// is -1 for the host.
        /// </summary>
        internal static bool PickSmelled(Character c, float radius, out int playerId)
        {
            playerId = 0;
            int n = CollectBodies();
            Vector3 from = c.transform.position;
            for (int i = 0; i < n; i++)
            {
                bool valid = BodyValid(i);
                float d = Core.trueDistance(from, _bodies[i].position);
                _cands[i] = new PlayerTargetCandidate { Valid = valid, Distance = d, Sensed = valid && d < radius };
            }
            int cur = IndexOf(c.target);
            bool held = cur >= 0 && _cands[cur].Sensed;
            State s = StateOf(c);
            int idx = PlayerTargetPolicy.Choose(_cands, n, cur, held, PinnedIndex(c, s), false,
                Time.time - s.LastSwitch, out _);
            if (idx < 0 || !_cands[idx].Sensed)
                return false;
            playerId = _bodyIds[idx];
            return true;
        }

        /// <summary>
        /// The player a banshee screams at: the one it is after while that one still sees it, else
        /// the nearest one who sees it, else the one it is after, else the nearest living one.
        /// </summary>
        internal static Transform PickViewer(Character banshee)
        {
            if (banshee == null)
                return null;
            int n = CollectBodies();
            Vector3 from = banshee.transform.position;
            for (int i = 0; i < n; i++)
            {
                bool valid = BodyValid(i);
                _cands[i] = new PlayerTargetCandidate
                {
                    Valid = valid,
                    Distance = Core.trueDistance(from, _bodies[i].position),
                    Sensed = valid && HostPlayerIdentity.BodySees(_bodies[i], _bodyIds[i] < 0, banshee.transform, canBeFarAway: true),
                };
            }
            int cur = IndexOf(banshee.target);
            bool held = cur >= 0 && _cands[cur].Sensed;
            State s = StateOf(banshee);
            int idx = PlayerTargetPolicy.Choose(_cands, n, cur, held, PinnedIndex(banshee, s), false,
                Time.time - s.LastSwitch, out _);
            return idx >= 0 ? _bodies[idx] : NearestOf(from, n);
        }

        // ---------------------------------------------------------------- writes and bookkeeping

        /// <summary>
        /// Vanilla <c>attackCharacter(body)</c> for a mod path, tagged so the switch trace names it.
        /// </summary>
        internal static void Commit(Character c, Transform body, string source,
            PlayerTargetReason reason = PlayerTargetReason.None)
        {
            if (c == null || body == null)
                return;
            string prevSource = _source;
            PlayerTargetReason prevReason = _reason;
            _source = source;
            _reason = reason;
            try
            {
                c.attackCharacter(body);
            }
            finally
            {
                _source = prevSource;
                _reason = prevReason;
            }
        }

        /// <summary>A plain target write (vanilla's sight check style), recorded like any other.</summary>
        internal static void SetTarget(Character c, Transform body, string source,
            PlayerTargetReason reason = PlayerTargetReason.None)
        {
            if (c == null)
                return;
            Transform before = c.target;
            c.target = body;
            Observe(c, before, body, source, reason);
        }

        /// <summary>Called from the attackCharacter patch with the tag of the running Commit.</summary>
        internal static void ObserveAttack(Character c, Transform before)
            => Observe(c, before, c.target, _source ?? "attackCharacter", _source != null ? _reason : PlayerTargetReason.None);

        /// <summary>
        /// Records a target change. A new player target starts its held grace now; a change from one
        /// player body to another is a switch (timed for the switch interval and traced).
        /// </summary>
        internal static void Observe(Character c, Transform before, Transform after, string source,
            PlayerTargetReason reason = PlayerTargetReason.None)
        {
            if (c == null)
                return;
            State s = StateOf(c);
            if (after != null && after != before && IsPlayerBody(after))
            {
                s.SensedBody = after;
                s.LastSensed = Time.time;
                if (before != null && IsPlayerBody(before))
                {
                    s.LastSwitch = Time.time;
                    TraceSwitch(c, before, after, source, reason);
                }
            }
            s.Last = after;
        }

        /// <summary>
        /// Before a sight check: a target that changed since the arbiter last saw it was written by a
        /// path that bypasses it. Traced (when between two player bodies) and adopted.
        /// </summary>
        internal static void CheckUnrouted(Character c)
        {
            State s = StateOf(c);
            if (s.Last != c.target)
                Observe(c, s.Last, c.target, "unrouted (between sight checks)");
        }

        /// <summary>The creature sensed its player target this sight check: the held grace restarts.</summary>
        internal static void NoteSensed(Character c, Transform body)
        {
            State s = StateOf(c);
            s.SensedBody = body;
            s.LastSensed = Time.time;
        }

        // ---------------------------------------------------------------- trace

        internal static string BodyName(Transform t)
        {
            if (t == null)
                return "none";
            if (IsHostBody(t))
                return "host";
            RemotePlayerProxy p = t.GetComponent<RemotePlayerProxy>();
            return p != null ? "p" + p.PlayerId : t.name;
        }

        private static void TraceSwitch(Character c, Transform from, Transform to, string source, PlayerTargetReason reason)
        {
            if (!ModLog.IsTrace(LogCat.AI))
                return;
            Vector3 at = c.transform.position;
            ModLog.TraceRate(LogCat.AI, "tgtswitch:" + c.GetInstanceID(), () =>
                $"[TargetSwitch] {c.name} {BodyName(from)}→{BodyName(to)} "
                + $"d={Core.trueDistance(at, from.position):F0}→{Core.trueDistance(at, to.position):F0} "
                + $"beh={c.behaviour} via {source}"
                + (reason != PlayerTargetReason.None ? " (" + reason + ")" : ""), 0.25f);
        }

        /// <summary>Vanilla wanted another player body and the arbiter kept this one (rate-limited).</summary>
        internal static void TraceHold(Character c, Transform kept, Transform vanillaPick, string where, PlayerTargetReason reason)
        {
            if (!ModLog.IsTrace(LogCat.AI) || kept == null || vanillaPick == null)
                return;
            Vector3 at = c.transform.position;
            ModLog.TraceRate(LogCat.AI, "tgthold:" + c.GetInstanceID(), () =>
                $"[TargetHold] {c.name} on {BodyName(kept)} (d={Core.trueDistance(at, kept.position):F0}) "
                + $"over {where}'s {BodyName(vanillaPick)} (d={Core.trueDistance(at, vanillaPick.position):F0}) "
                + $"beh={c.behaviour} ({reason})", 2f);
        }
    }
}
