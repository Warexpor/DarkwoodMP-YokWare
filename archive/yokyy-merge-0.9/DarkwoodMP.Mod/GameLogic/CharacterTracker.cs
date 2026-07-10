using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Registry of all live Character instances with stable cross-machine IDs.
/// Maintained by Harmony hooks on Character.Start/OnDestroy (CharacterRegistry_Patch)
/// so nothing has to FindObjectsOfType every frame.
///
/// The authority (host / elected time-authority) assigns IDs; other machines
/// receive the authority's IDs and bind them to their matching local Character
/// (matched by name + position, since world-download gives every machine the
/// exact same world). Ported/adapted from the BepInEx mod's CharacterTracker.
/// </summary>
public static class CharacterTracker
{
    private static readonly List<Character> _characters = new(64);
    private static readonly Dictionary<Character, short> _idByChar = new(64);
    private static readonly HashSet<short> _activeIds = new();
    private static readonly object _lock = new();
    private static short _nextId = 1;

    public static Character[] GetAll()
    {
        lock (_lock) return _characters.ToArray();
    }

    public static int Count { get { lock (_lock) return _characters.Count; } }

    /// <summary>
    /// Register a character. Deliberately does NOT assign an id: only the
    /// authority mints ids (lazily, via GetStableId when broadcasting), and
    /// clients bind the authority's ids by position+name from snapshots. If both
    /// sides auto-assigned, their id spaces would collide and two same-named
    /// enemies could bind to each other.
    /// </summary>
    public static void Add(Character c)
    {
        if (c == null) return;
        lock (_lock)
        {
            if (!_characters.Contains(c)) _characters.Add(c);
        }
    }

    public static void Remove(Character c)
    {
        if (c == null) return;
        lock (_lock)
        {
            if (_idByChar.TryGetValue(c, out var id)) _activeIds.Remove(id);
            _characters.Remove(c);
            _idByChar.Remove(c);
        }
    }

    /// <summary>Stable ID for a character, assigning one if needed (authority side).</summary>
    public static short GetStableId(Character c)
    {
        if (c == null) return 0;
        lock (_lock)
        {
            if (_idByChar.TryGetValue(c, out var id)) return id;
            var newId = NextFreeId();
            if (newId == 0) return 0;
            _idByChar[c] = newId;
            _activeIds.Add(newId);
            if (!_characters.Contains(c)) _characters.Add(c);
            return newId;
        }
    }

    public static bool TryGetStableId(Character c, out short id)
    {
        if (c == null) { id = 0; return false; }
        lock (_lock) return _idByChar.TryGetValue(c, out id);
    }

    /// <summary>Bind a specific ID (from the authority) to a local character; 0 clears it.</summary>
    public static void AssignId(Character c, short id)
    {
        if (c == null) return;
        lock (_lock)
        {
            if (_idByChar.TryGetValue(c, out var old)) _activeIds.Remove(old);
            if (id == 0) { _idByChar.Remove(c); return; }
            _idByChar[c] = id;
            _activeIds.Add(id);
            if (!_characters.Contains(c)) _characters.Add(c);
            if (_activeIds.Count > 2000) Clear();
        }
    }

    public static Character FindByStableId(short id)
    {
        if (id == 0) return null;
        lock (_lock)
        {
            for (var i = 0; i < _characters.Count; i++)
                if (_characters[i] != null && _idByChar.TryGetValue(_characters[i], out var sid) && sid == id)
                    return _characters[i];
        }
        return null;
    }

    /// <summary>Nearest same-named character within radius, skipping already-bound IDs.</summary>
    public static Character FindByPositionAndName(Vector3 pos, string name, float radius, HashSet<short> excludeIds = null)
    {
        var searchName = Strip(name);
        var radiusSq = radius * radius;
        Character best = null;
        var bestSq = float.MaxValue;
        lock (_lock)
        {
            for (var i = 0; i < _characters.Count; i++)
            {
                var c = _characters[i];
                if (c == null) continue;
                if (excludeIds != null && _idByChar.TryGetValue(c, out var sid) && excludeIds.Contains(sid)) continue;
                if (!string.Equals(Strip(c.name), searchName, StringComparison.OrdinalIgnoreCase)) continue;
                var d = (c.transform.position - pos).sqrMagnitude;
                if (d < radiusSq && d < bestSq) { bestSq = d; best = c; }
            }
        }
        return best;
    }

    /// <summary>
    /// Nearest same-named character ANYWHERE (no radius), skipping already-bound
    /// ids. Used so a client binds its own local enemy (which may have drifted
    /// from the authority's position while its AI was frozen) instead of spawning
    /// a duplicate phantom.
    /// </summary>
    public static Character FindClosestByName(string name, Vector3 pos, HashSet<short> excludeIds = null)
    {
        var searchName = Strip(name);
        Character best = null;
        var bestSq = float.MaxValue;
        lock (_lock)
        {
            for (var i = 0; i < _characters.Count; i++)
            {
                var c = _characters[i];
                if (c == null) continue;
                if (excludeIds != null && _idByChar.TryGetValue(c, out var sid) && excludeIds.Contains(sid)) continue;
                if (!string.Equals(Strip(c.name), searchName, StringComparison.OrdinalIgnoreCase)) continue;
                var d = (c.transform.position - pos).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = c; }
            }
        }
        return best;
    }

    private static short NextFreeId()
    {
        var safety = 0;
        short id;
        do
        {
            id = _nextId++;
            if (++safety > short.MaxValue) return 0;
        } while (id == 0 || _activeIds.Contains(id));
        return id;
    }

    private static string Strip(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return name.EndsWith("(Clone)") ? name.Substring(0, name.Length - 7) : name;
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _characters.Clear();
            _idByChar.Clear();
            _activeIds.Clear();
            _nextId = 1;
        }
    }
}
