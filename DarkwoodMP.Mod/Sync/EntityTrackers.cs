using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Generic per-type tracker for scene entities that network sync needs to look up by position.
    /// Backs DoorTracker and GeneratorTracker (each generic instantiation keeps its own list).
    /// </summary>
    public static class ListTracker<T> where T : Component
    {
        private static readonly List<T> _items = new List<T>(64);
        private static float _lastCleanupTime;
        private const float CleanupInterval = 30f;

        /// <summary>Returns the tracked list (may contain nulls between cleanups).</summary>
        public static IList<T> GetAll() => _items;

        /// <summary>Registers an instance for tracking.</summary>
        public static void Add(T item)
        {
            if (item == null) return;
            if (!_items.Contains(item))
                _items.Add(item);
        }

        /// <summary>Removes an instance from tracking.</summary>
        public static void Remove(T item)
        {
            if (item == null) return;
            _items.Remove(item);
        }

        /// <summary>Finds a tracked instance within <paramref name="maxDist"/> of the given position.</summary>
        public static T FindByPosition(Vector3 pos, float maxDist = 0.5f)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                T item = _items[i];
                if (item == null) continue;
                if (Vector3.Distance(item.transform.position, pos) < maxDist)
                    return item;
            }
            return null;
        }

        /// <summary>
        /// Null-purge only. Full FindObjectsOfType here would hitch LateUpdate (~45ms)
        /// every 30s; Awake + OnEnable registration covers chunk-spawned instances.
        /// </summary>
        public static void Cleanup()
        {
            float now = Time.time;
            if (now - _lastCleanupTime < CleanupInterval)
                return;
            _lastCleanupTime = now;
            _items.RemoveAll(item => item == null);
        }

        /// <summary>Clears all tracked instances.</summary>
        public static void Clear() { _items.Clear(); }
    }

    /// <summary>Harmony patch: registers doors with the tracker on Awake.</summary>
    [HarmonyPatch(typeof(Door), "Awake")]
    public static class DoorAwakePatch
    {
        private static void Postfix(Door __instance)
        {
            ListTracker<Door>.Add(__instance);
        }
    }

    /// <summary>
    /// Chunk / pool re-enable can skip a second Awake — register on OnEnable too
    /// so the door tracker never needs a scene-wide FOOT rescan.
    /// </summary>
    [HarmonyPatch(typeof(Door), "OnEnable")]
    public static class DoorOnEnablePatch
    {
        private static void Postfix(Door __instance)
        {
            ListTracker<Door>.Add(__instance);
        }
    }

    /// <summary>Harmony patch: registers generators with the tracker on Start.</summary>
    [HarmonyPatch(typeof(Generator), "Start")]
    public static class GeneratorStartPatch
    {
        private static void Postfix(Generator __instance)
        {
            ListTracker<Generator>.Add(__instance);
        }
    }

    /// <summary>Harmony patch: deregisters generators from the tracker on destroy.</summary>
    [HarmonyPatch(typeof(Generator), "OnDestroy")]
    public static class GeneratorDestroyPatch
    {
        private static void Prefix(Generator __instance)
        {
            ListTracker<Generator>.Remove(__instance);
        }
    }
}
