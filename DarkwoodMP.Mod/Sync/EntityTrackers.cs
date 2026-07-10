using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>Tracks all Door instances for network sync. Periodically rescans for dynamically spawned doors.</summary>
    public static class DoorTracker
    {
        private static readonly List<Door> _doors = new List<Door>(64);
        private static float _lastCleanupTime;
        private const float CleanupInterval = 30f;

        /// <summary>Returns the tracked door list (may contain nulls between cleanups).</summary>
        public static IList<Door> GetAll() => _doors;

        /// <summary>Registers a door for tracking.</summary>
        public static void Add(Door d)
        {
            if (d == null) return;
            if (!_doors.Contains(d))
                _doors.Add(d);
        }

        /// <summary>Finds a tracked door within <paramref name="maxDist"/> of the given position.</summary>
        public static Door FindByPosition(Vector3 pos, float maxDist = 0.5f)
        {
            for (int i = 0; i < _doors.Count; i++)
            {
                Door d = _doors[i];
                if (d == null) continue;
                if (Vector3.Distance(d.transform.position, pos) < maxDist)
                    return d;
            }
            return null;
        }

        /// <summary>Removes null entries and rescans for dynamically spawned doors (runs at most every 30s).</summary>
        public static void Cleanup()
        {
            float now = Time.time;
            if (now - _lastCleanupTime < CleanupInterval)
                return;
            _lastCleanupTime = now;
            _doors.RemoveAll(d => d == null);

            // Periodically re-scan for Door instances that were spawned dynamically
            // (e.g. by world-grid chunk loading) after the Awake patch ran.
            Door[] all = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < all.Length && i < 256; i++)
            {
                Door d = all[i];
                if (d != null && !_doors.Contains(d))
                    _doors.Add(d);
            }
        }

        /// <summary>Clears all tracked doors.</summary>
        public static void Clear() { _doors.Clear(); }
    }

    /// <summary>Tracks all Generator instances for network sync.</summary>
    public static class GeneratorTracker
    {
        private static readonly List<Generator> _generators = new List<Generator>(16);

        /// <summary>Returns the tracked generator list.</summary>
        public static IList<Generator> GetAll() => _generators;

        /// <summary>Registers a generator for tracking.</summary>
        public static void Add(Generator g)
        {
            if (g == null) return;
            if (!_generators.Contains(g))
                _generators.Add(g);
        }

        /// <summary>Removes a generator from tracking.</summary>
        public static void Remove(Generator g)
        {
            if (g == null) return;
            _generators.Remove(g);
        }

        /// <summary>Finds a tracked generator within <paramref name="maxDist"/> of the given position.</summary>
        public static Generator FindByPosition(Vector3 pos, float maxDist = 0.5f)
        {
            for (int i = 0; i < _generators.Count; i++)
            {
                Generator g = _generators[i];
                if (g == null) continue;
                if (Vector3.Distance(g.transform.position, pos) < maxDist)
                    return g;
            }
            return null;
        }

        /// <summary>Clears all tracked generators.</summary>
        public static void Clear() { _generators.Clear(); }
    }

    /// <summary>Harmony patch: registers doors with the tracker on Awake.</summary>
    [HarmonyPatch(typeof(Door), "Awake")]
    public static class DoorAwakePatch
    {
        private static void Postfix(Door __instance)
        {
            DoorTracker.Add(__instance);
        }
    }

    /// <summary>Harmony patch: registers generators with the tracker on Start.</summary>
    [HarmonyPatch(typeof(Generator), "Start")]
    public static class GeneratorStartPatch
    {
        private static void Postfix(Generator __instance)
        {
            GeneratorTracker.Add(__instance);
        }
    }

    /// <summary>Harmony patch: deregisters generators from the tracker on destroy.</summary>
    [HarmonyPatch(typeof(Generator), "OnDestroy")]
    public static class GeneratorDestroyPatch
    {
        private static void Prefix(Generator __instance)
        {
            GeneratorTracker.Remove(__instance);
        }
    }
}
