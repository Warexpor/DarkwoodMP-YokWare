using System;
using System.Collections.Generic;
using DWMPHorde.Networking;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Assigns stable IDs to Character instances and tracks them for network sync across their lifetime.
    /// Main thread only (Unity callbacks, Update and inbound dispatch), so there is no locking.
    /// </summary>
    public static class CharacterTracker
    {
        private static readonly List<Character> _characters = new List<Character>(64);
        private static readonly Dictionary<Character, short> _stableIdCache = new Dictionary<Character, short>(64);
        /// <summary>Reverse map for O(1) FindByStableId (client LateUpdate walks every driven id every frame).</summary>
        private static readonly Dictionary<short, Character> _byId = new Dictionary<short, Character>(64);
        private static readonly HashSet<short> _activeIds = new HashSet<short>();
        /// <summary>
        /// Host: ids freed by Remove stay unusable for a short grace so GetCollisionFreeId
        /// cannot mint the same short while a client still has deferred Destroy + late
        /// EntityState for the old body (same-name crow/rabbit wrong claim).
        /// </summary>
        private static readonly Dictionary<short, float> _recycleGraceUntil = new Dictionary<short, float>(32);
        private const float RecycleGraceSec = 2.5f;
        private static short _nextId = 1;

        /// <summary>Tracked count at which destroyed entries are swept from the list and every map.</summary>
        private static int _pruneAtCount = PruneBaseCount;
        private const int PruneBaseCount = 512;
        private static readonly List<Character> _pruneKeys = new List<Character>(); // process-scoped: scratch
        private const string CloneSuffix = "(Clone)";

        /// <summary>
        /// Case-insensitive name equality ignoring one trailing "(Clone)" on either side, without allocating.
        /// </summary>
        public static bool BaseNameEquals(string a, string b)
        {
            if (a == null || b == null) return a == b;
            int la = a.EndsWith(CloneSuffix, StringComparison.Ordinal) ? a.Length - CloneSuffix.Length : a.Length;
            int lb = b.EndsWith(CloneSuffix, StringComparison.Ordinal) ? b.Length - CloneSuffix.Length : b.Length;
            return la == lb && string.Compare(a, 0, b, 0, la, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>
        /// Returns the stable network ID for a character.
        /// Host: assigns a new ID if not yet cached (host is sole allocator).
        /// Client: returns cached host-mapped ID only; never auto-allocates (returns 0 if unmapped).
        /// </summary>
        public static short GetStableId(Character c)
        {
            if (c == null) return 0;
            if (_stableIdCache.TryGetValue(c, out var id))
                return id;

            // Only the live Host mints IDs. Offline (join phase-2 load) and Client must not:
            // phase-2 used to mint local 1..N, then phase-3 host ids collided → wrong
            // FindByStableId + purge/respawn thrash (client FPS death after enter world).
            if (!NetGuard.Host(out var net))
                return 0;

            return AssignId(c);
        }

        /// <summary>Assigns a new unique stable ID to the given character, skipping IDs still in use. Host only.</summary>
        public static short AssignId(Character c)
        {
            if (c == null) return 0;
            if (_stableIdCache.TryGetValue(c, out short existing) && existing != 0)
                return existing;
            short id = GetCollisionFreeId();
            if (id == 0) return 0;
            _stableIdCache[c] = id;
            _byId[id] = c;
            _activeIds.Add(id);
            if (!_characters.Contains(c))
                _characters.Add(c);
            MaybePruneDestroyed();
            return id;
        }

        /// <summary>
        /// Removes the stable ID mapping for a character without destroying tracking.
        /// Use on clients when a host ID collided with the wrong local entity (never AssignId(c, 0)).
        /// </summary>
        public static void ClearId(Character c)
        {
            if (c == null) return;
            if (_stableIdCache.TryGetValue(c, out short oldId))
            {
                _activeIds.Remove(oldId);
                _stableIdCache.Remove(c);
                if (_byId.TryGetValue(oldId, out Character mapped) && mapped == c)
                    _byId.Remove(oldId);
            }
        }

        /// <summary>Returns the stable ID for a character without assigning one (unlike GetStableId).</summary>
        public static bool TryGetStableId(Character c, out short id)
        {
            if (c == null) { id = 0; return false; }
            return _stableIdCache.TryGetValue(c, out id);
        }

        /// <summary>
        /// Finds the closest character whose name matches <paramref name="name"/>
        /// and whose position is within <paramref name="radius"/> of <paramref name="pos"/>.
        /// Excludes characters already in the <paramref name="excludeIds"/> set.
        /// Returns null if no match is found.
        /// </summary>
        public static Character FindByPositionAndName(Vector3 pos, string name, float radius, HashSet<short> excludeIds = null)
        {
            return FindNearestNamed(_characters, _characters.Count, pos, name, radius * radius, excludeIds);
        }

        /// <summary>
        /// Same as <see cref="FindByPositionAndName"/> against a pre-copied buffer.
        /// CEI LateUpdate matches many pendings against one <see cref="CopyAll"/>.
        /// </summary>
        public static Character FindByPositionAndNameIn(
            Character[] chars, int count, Vector3 pos, string name, float radius, HashSet<short> excludeIds = null)
        {
            if (chars == null) return null;
            return FindNearestNamed(chars, Math.Min(count, chars.Length), pos, name, radius * radius, excludeIds);
        }

        /// <summary>
        /// Finds the closest character whose name matches <paramref name="name"/>
        /// near <paramref name="pos"/>, with no distance limit.
        /// Excludes characters already in the <paramref name="excludeIds"/> set.
        /// Intended as a fallback when AI divergence makes radius-based search unreliable.
        /// </summary>
        public static Character FindClosestByName(string name, Vector3 pos, HashSet<short> excludeIds = null)
        {
            return FindNearestNamed(_characters, _characters.Count, pos, name, float.MaxValue, excludeIds);
        }

        private static Character FindNearestNamed(
            IList<Character> chars, int count, Vector3 pos, string name, float maxDistSq, HashSet<short> excludeIds)
        {
            if (count <= 0 || string.IsNullOrEmpty(name)) return null;
            Character best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < count; i++)
            {
                Character c = chars[i];
                if (c == null) continue;

                if (excludeIds != null && _stableIdCache.TryGetValue(c, out short sid) && excludeIds.Contains(sid))
                    continue;

                if (!BaseNameEquals(c.name, name))
                    continue;

                Vector3 cp = c.transform.position;
                float dx = cp.x - pos.x;
                float dz = cp.z - pos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < maxDistSq && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Assigns a specific stable ID (from the host) to the given character on a client.</summary>
        public static void AssignId(Character c, short id)
        {
            if (c == null) return;
            if (id == 0)
            {
                ClearId(c);
                return;
            }

            // Free the character's old ID before assigning a new one to prevent stale-ID leaks
            if (_stableIdCache.TryGetValue(c, out short oldId))
            {
                _activeIds.Remove(oldId);
                if (_byId.TryGetValue(oldId, out Character mapped) && mapped == c)
                    _byId.Remove(oldId);
            }

            // If another character (live or destroyed) already holds this host id, clear it first.
            if (_byId.TryGetValue(id, out Character conflict) && !ReferenceEquals(conflict, c))
            {
                if (!ReferenceEquals(conflict, null))
                    _stableIdCache.Remove(conflict);
                _activeIds.Remove(id);
                _byId.Remove(id);
            }

            _stableIdCache[c] = id;
            _byId[id] = c;
            _activeIds.Add(id);
            if (!_characters.Contains(c))
                _characters.Add(c);

            MaybePruneDestroyed();
        }

        /// <summary>
        /// Max-cap safety: once the tracked set grows past a threshold, sweep destroyed
        /// characters from the list and all id maps together. The threshold then moves to
        /// twice the live count, so the O(n) sweep is amortized instead of running on every call.
        /// </summary>
        private static void MaybePruneDestroyed()
        {
            if (_characters.Count < _pruneAtCount && _stableIdCache.Count < _pruneAtCount)
                return;
            PruneDestroyed();
            _pruneAtCount = Math.Max(PruneBaseCount, Math.Max(_characters.Count, _stableIdCache.Count) * 2);
        }

        private static void PruneDestroyed()
        {
            for (int i = _characters.Count - 1; i >= 0; i--)
            {
                if (_characters[i] == null)
                    _characters.RemoveAt(i);
            }

            _pruneKeys.Clear();
            foreach (var kv in _stableIdCache)
            {
                if (kv.Key == null)
                    _pruneKeys.Add(kv.Key);
            }
            for (int i = 0; i < _pruneKeys.Count; i++)
            {
                Character dead = _pruneKeys[i];
                if (_stableIdCache.TryGetValue(dead, out short sid))
                {
                    _activeIds.Remove(sid);
                    if (_byId.TryGetValue(sid, out Character mapped) && ReferenceEquals(mapped, dead))
                        _byId.Remove(sid);
                    HoldRecycledId(sid);
                }
                _stableIdCache.Remove(dead);
            }
            _pruneKeys.Clear();
        }

        /// <summary>Finds a character by its stable network ID.</summary>
        public static Character FindByStableId(short id)
        {
            if (id == 0) return null;
            if (_byId.TryGetValue(id, out Character c) && c != null)
                return c;
            // Stale reverse entry or pre-map list; fall back once and repair.
            for (int i = 0; i < _characters.Count; i++)
            {
                Character ch = _characters[i];
                if (ch != null && _stableIdCache.TryGetValue(ch, out short sid) && sid == id)
                {
                    _byId[id] = ch;
                    return ch;
                }
            }
            _byId.Remove(id);
            return null;
        }

        private static Character[] _copyBuf = new Character[64]; // process-scoped: scratch

        /// <summary>
        /// Copy tracked characters into a reusable buffer. Returns count.
        /// Buffer contents valid until the next CopyAll call.
        /// Hot path (entity broadcast 10 Hz) must not allocate ToArray every tick.
        /// </summary>
        public static int CopyAll(out Character[] buffer)
        {
            int n = _characters.Count;
            if (_copyBuf.Length < n)
                _copyBuf = new Character[Math.Max(n, _copyBuf.Length * 2)];
            for (int i = 0; i < n; i++)
                _copyBuf[i] = _characters[i];
            buffer = _copyBuf;
            return n;
        }

        /// <summary>Gets the number of currently tracked characters.</summary>
        public static int Count => _characters.Count;

        /// <summary>
        /// Registers a character for tracking.
        /// Host assigns an ID immediately; client only lists the character until host maps an ID.
        /// </summary>
        public static void Add(Character c)
        {
            if (c == null) return;
            if (!_characters.Contains(c))
                _characters.Add(c);

            if (_stableIdCache.ContainsKey(c))
                return;

            // Host-only mint. Offline join load + Client: list without id until AssignId(c, hostId).
            if (!NetGuard.Host(out var net))
                return;

            short id = GetCollisionFreeId();
            if (id != 0)
            {
                _stableIdCache[c] = id;
                _byId[id] = c;
                _activeIds.Add(id);
            }
            MaybePruneDestroyed();
        }

        /// <summary>
        /// Next free positive id. The counter wraps from short.MaxValue back to 1 (never 0 or
        /// negative) and skips ids in use or in recycle grace; 0 only when none is free.
        /// </summary>
        private static short GetCollisionFreeId()
        {
            PurgeExpiredRecycleGrace();
            if (_activeIds.Count + _recycleGraceUntil.Count >= short.MaxValue)
                PruneDestroyed();
            for (int attempts = 0; attempts < short.MaxValue; attempts++)
            {
                if (_nextId <= 0)
                    _nextId = 1;
                short id = _nextId;
                _nextId = id == short.MaxValue ? (short)1 : (short)(id + 1);
                if (!_activeIds.Contains(id) && !_recycleGraceUntil.ContainsKey(id))
                    return id;
            }
            return 0;
        }

        private static void PurgeExpiredRecycleGrace()
        {
            if (_recycleGraceUntil.Count == 0) return;
            float now = Time.unscaledTime;
            // Copy keys — cannot mutate during foreach.
            _recycleGraceScratch.Clear();
            foreach (var kv in _recycleGraceUntil)
            {
                if (kv.Value <= now)
                    _recycleGraceScratch.Add(kv.Key);
            }
            for (int i = 0; i < _recycleGraceScratch.Count; i++)
                _recycleGraceUntil.Remove(_recycleGraceScratch[i]);
        }

        private static readonly List<short> _recycleGraceScratch = new List<short>(16); // process-scoped: scratch

        private static void HoldRecycledId(short sid)
        {
            if (sid == 0) return;
            _recycleGraceUntil[sid] = Time.unscaledTime + RecycleGraceSec;
        }

        /// <summary>Removes a character from tracking, freeing its stable ID for reuse after grace.</summary>
        public static void Remove(Character c)
        {
            // A destroyed character (Unity == null) must still leave the maps.
            if (ReferenceEquals(c, null)) return;
            if (_stableIdCache.TryGetValue(c, out short sid))
            {
                _activeIds.Remove(sid);
                if (_byId.TryGetValue(sid, out Character mapped) && ReferenceEquals(mapped, c))
                    _byId.Remove(sid);
                HoldRecycledId(sid);
            }
            _characters.Remove(c);
            _stableIdCache.Remove(c);
        }

        /// <summary>Clears all tracked characters and resets the ID counter.</summary>
        public static void Clear()
        {
            _characters.Clear();
            _stableIdCache.Clear();
            _byId.Clear();
            _activeIds.Clear();
            _recycleGraceUntil.Clear();
            _nextId = 1;
            _pruneAtCount = PruneBaseCount;
        }

        /// <summary>
        /// Network-stop / promote safe reset: drop ID maps and null refs, keep live characters in
        /// the list, then rescan the scene so the host can remint IDs without a combat gap.
        /// </summary>
        public static void ResetForNetworkStop()
        {
            // Drop destroyed refs
            for (int i = _characters.Count - 1; i >= 0; i--)
            {
                if (_characters[i] == null)
                    _characters.RemoveAt(i);
            }
            _stableIdCache.Clear();
            _byId.Clear();
            _activeIds.Clear();
            _recycleGraceUntil.Clear();
            _nextId = 1;
            _pruneAtCount = PruneBaseCount;

            // Rescan scene characters (includes ones that never hit Start while offline).
            // Already-listed live characters get an id here too when we are the host.
            for (int i = 0; i < _characters.Count; i++)
                Add(_characters[i]);
            Character[] scene = WorldQueryHelper.GetCachedSceneComponents<Character>();
            if (scene != null)
            {
                for (int i = 0; i < scene.Length; i++)
                    Add(scene[i]);
            }

            ModRuntime.LegacyInfo($"[CharacterTracker] ResetForNetworkStop: tracked={Count}");
        }
    }
}
