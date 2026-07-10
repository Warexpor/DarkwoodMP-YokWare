using System;
using System.Collections.Generic;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Records the original prefab path on dynamically spawned objects.</summary>
    [HarmonyPatch(typeof(Core), "AddPrefab", new[] { typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool) })]
    public static class AddPrefabRecordPathPatch
    {
        private static void Postfix(GameObject __result, object[] __args)
        {
            string prefab = (string)__args[0];

            if (__result == null || string.IsNullOrEmpty(prefab))
                return;
            var comp = __result.GetComponent<PrefabPathComponent>();
            if (comp == null)
                comp = __result.AddComponent<PrefabPathComponent>();
            comp.Path = prefab;
        }
    }
}

namespace DWMPHorde.Sync
{
    /// <summary>Assigns stable IDs to Character instances and tracks them for network sync across their lifetime.</summary>
    public static class CharacterTracker
    {
        private static readonly List<Character> _characters = new List<Character>(64);
        private static readonly Dictionary<Character, short> _stableIdCache = new Dictionary<Character, short>(64);
        private static readonly HashSet<short> _activeIds = new HashSet<short>();
        private static readonly object _lock = new object();
        private static short _nextId = 1;

        /// <summary>
        /// Returns the stable network ID for a character.
        /// Host: assigns a new ID if not yet cached (host is sole allocator).
        /// Client: returns cached host-mapped ID only; never auto-allocates (returns 0 if unmapped).
        /// </summary>
        public static short GetStableId(Character c)
        {
            if (c == null) return 0;
            lock (_lock)
            {
                if (_stableIdCache.TryGetValue(c, out var id))
                    return id;
            }

            // Clients must not mint IDs — host owns the namespace (P1.1).
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
                return 0;

            return AssignId(c);
        }

        /// <summary>Assigns a new unique stable ID to the given character, skipping IDs still in use. Host only.</summary>
        public static short AssignId(Character c)
        {
            if (c == null) return 0;
            lock (_lock)
            {
                if (_stableIdCache.TryGetValue(c, out short existing) && existing != 0)
                    return existing;
                short id = GetCollisionFreeId();
                if (id == 0) return 0;
                _stableIdCache[c] = id;
                _activeIds.Add(id);
                if (!_characters.Contains(c))
                    _characters.Add(c);
                return id;
            }
        }

        /// <summary>
        /// Removes the stable ID mapping for a character without destroying tracking.
        /// Use on clients when a host ID collided with the wrong local entity (never AssignId(c, 0)).
        /// </summary>
        public static void ClearId(Character c)
        {
            if (c == null) return;
            lock (_lock)
            {
                if (_stableIdCache.TryGetValue(c, out short oldId))
                {
                    _activeIds.Remove(oldId);
                    _stableIdCache.Remove(c);
                }
            }
        }

        /// <summary>Returns the stable ID for a character without assigning one (unlike GetStableId).</summary>
        public static bool TryGetStableId(Character c, out short id)
        {
            if (c == null) { id = 0; return false; }
            lock (_lock)
            {
                return _stableIdCache.TryGetValue(c, out id);
            }
        }

        /// <summary>
        /// Finds the closest character whose name matches <paramref name="name"/>
        /// and whose position is within <paramref name="radius"/> of <paramref name="pos"/>.
        /// Excludes characters already in the <paramref name="excludeIds"/> set.
        /// Returns null if no match is found.
        /// </summary>
        public static Character FindByPositionAndName(Vector3 pos, string name, float radius, HashSet<short> excludeIds = null)
        {
            float radiusSq = radius * radius;
            Character best = null;
            float bestDistSq = float.MaxValue;

            // Normalise the search name: strip "(Clone)" suffix
            string searchName = name;
            if (searchName.EndsWith("(Clone)"))
                searchName = searchName.Substring(0, searchName.Length - 7);

            lock (_lock)
            {
                for (int i = 0; i < _characters.Count; i++)
                {
                    Character c = _characters[i];
                    if (c == null) continue;

                    // Skip if excluded
                    if (excludeIds != null && _stableIdCache.TryGetValue(c, out short sid) && excludeIds.Contains(sid))
                        continue;

                    // Name must match (allow both with and without "(Clone)")
                    string cName = c.name;
                    if (!string.Equals(cName, searchName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(cName, searchName + "(Clone)", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(cName + "(Clone)", searchName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float dSq = (c.transform.position - pos).sqrMagnitude;
                    if (dSq < radiusSq && dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = c;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// Finds the closest character whose name matches <paramref name="name"/>
        /// near <paramref name="pos"/>, with no distance limit.
        /// Excludes characters already in the <paramref name="excludeIds"/> set.
        /// Intended as a fallback when AI divergence makes radius-based search unreliable.
        /// </summary>
        public static Character FindClosestByName(string name, Vector3 pos, HashSet<short> excludeIds = null)
        {
            string searchName = name;
            if (searchName.EndsWith("(Clone)"))
                searchName = searchName.Substring(0, searchName.Length - 7);

            Character best = null;
            float bestDistSq = float.MaxValue;

            lock (_lock)
            {
                for (int i = 0; i < _characters.Count; i++)
                {
                    Character c = _characters[i];
                    if (c == null) continue;

                    if (excludeIds != null && _stableIdCache.TryGetValue(c, out short sid) && excludeIds.Contains(sid))
                        continue;

                    string cName = c.name;
                    if (cName.EndsWith("(Clone)"))
                        cName = cName.Substring(0, cName.Length - 7);
                    if (!string.Equals(cName, searchName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    float dSq = (c.transform.position - pos).sqrMagnitude;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = c;
                    }
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
            lock (_lock)
            {
                // Free the character's old ID before assigning a new one to prevent stale-ID leaks
                if (_stableIdCache.TryGetValue(c, out short oldId))
                    _activeIds.Remove(oldId);

                // If another character already holds this host id, clear it first
                Character conflict = null;
                for (int i = 0; i < _characters.Count; i++)
                {
                    Character other = _characters[i];
                    if (other != null && other != c && _stableIdCache.TryGetValue(other, out short sid) && sid == id)
                    {
                        conflict = other;
                        break;
                    }
                }
                if (conflict != null)
                {
                    _stableIdCache.Remove(conflict);
                    _activeIds.Remove(id);
                }

                _stableIdCache[c] = id;
                _activeIds.Add(id);
                if (!_characters.Contains(c))
                    _characters.Add(c);

                // Max-cap safety: prevent unbounded _activeIds growth without full wipe mid-fight
                if (_activeIds.Count > 1000)
                {
                    // Drop null entries only
                    for (int i = _characters.Count - 1; i >= 0; i--)
                    {
                        if (_characters[i] == null)
                            _characters.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>Finds a character by its stable network ID.</summary>
        public static Character FindByStableId(short id)
        {
            lock (_lock)
            {
                for (int i = 0; i < _characters.Count; i++)
                {
                    if (_characters[i] != null && _stableIdCache.TryGetValue(_characters[i], out short sid) && sid == id)
                        return _characters[i];
                }
            }
            return null;
        }

        /// <summary>Returns a snapshot array of all tracked characters.</summary>
        public static Character[] GetAll()
        {
            lock (_lock)
            {
                return _characters.ToArray();
            }
        }

        /// <summary>Gets the number of currently tracked characters.</summary>
        public static int Count
        {
            get { lock (_lock) { return _characters.Count; } }
        }

        /// <summary>
        /// Registers a character for tracking.
        /// Host assigns an ID immediately; client only lists the character until host maps an ID.
        /// </summary>
        public static void Add(Character c)
        {
            if (c == null) return;
            lock (_lock)
            {
                if (!_characters.Contains(c))
                    _characters.Add(c);

                if (_stableIdCache.ContainsKey(c))
                    return;

                var net = ModRuntime.Network;
                bool isClient = net != null && net.Role == NetworkRole.Client;
                if (isClient)
                    return; // wait for host id via AssignId(c, hostId)

                short id = GetCollisionFreeId();
                if (id != 0)
                {
                    _stableIdCache[c] = id;
                    _activeIds.Add(id);
                }
            }
        }

        private static short GetCollisionFreeId()
        {
            short id;
            int safety = 0;
            do
            {
                id = _nextId++;
                if (++safety > short.MaxValue)
                    return 0;
            } while (id == 0 || _activeIds.Contains(id));
            return id;
        }

        /// <summary>Removes a character from tracking, freeing its stable ID for reuse.</summary>
        public static void Remove(Character c)
        {
            if (c == null) return;
            lock (_lock)
            {
                if (_stableIdCache.TryGetValue(c, out short sid))
                    _activeIds.Remove(sid);
                _characters.Remove(c);
                _stableIdCache.Remove(c);
            }
        }

        /// <summary>Clears all tracked characters and resets the ID counter.</summary>
        public static void Clear()
        {
            lock (_lock)
            {
                _characters.Clear();
                _stableIdCache.Clear();
                _activeIds.Clear();
                _nextId = 1;
            }
        }

        /// <summary>
        /// Network-stop safe reset: drop ID maps and null refs, keep live characters in the list,
        /// then rescan the scene so host can remint IDs without a combat gap (polish P0.2).
        /// </summary>
        public static void ResetForNetworkStop()
        {
            lock (_lock)
            {
                // Drop destroyed refs
                for (int i = _characters.Count - 1; i >= 0; i--)
                {
                    if (_characters[i] == null)
                        _characters.RemoveAt(i);
                }
                _stableIdCache.Clear();
                _activeIds.Clear();
                _nextId = 1;
            }

            // Rescan scene characters (includes ones that never hit Start while offline)
            Character[] scene = UnityEngine.Object.FindObjectsOfType<Character>(true);
            if (scene != null)
            {
                for (int i = 0; i < scene.Length; i++)
                    Add(scene[i]);
            }

            ModRuntime.LegacyInfo($"[CharacterTracker] ResetForNetworkStop: tracked={Count}");
        }
    }

    /// <summary>Harmony patch: registers characters with the tracker on Start.</summary>
    [HarmonyPatch(typeof(Character), "Start")]
    public static class CharacterStartPatch
    {
        private static void Postfix(Character __instance)
        {
            CharacterTracker.Add(__instance);
        }
    }

    /// <summary>Harmony patch: deregisters characters from the tracker on destroy.</summary>
    [HarmonyPatch(typeof(Character), "OnDestroy")]
    public static class CharacterDestroyPatch
    {
        private static void Prefix(Character __instance)
        {
            CharacterTracker.Remove(__instance);
        }
    }
}
