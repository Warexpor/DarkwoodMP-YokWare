using System;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scene spatial lookups for net apply handlers.
    /// OverlapSphere first; scene FOOT only via a short TTL cache (never FindObjectsOfTypeAll).
    /// Client stutters: Lure has no own collider → every uncached FOOT was ~50ms main-thread.
    /// </summary>
    internal static class WorldQueryHelper
    {
        private static readonly Collider[] OverlapBuf = new Collider[64];
        private const float SceneScanTtl = 3f;

        /// <summary>Nearest live scene T within maxDist of pos.</summary>
        public static T FindNearest<T>(Vector3 pos, float maxDist) where T : Component
        {
            T best = FindNearestInOverlap<T>(pos, maxDist, name: null);
            if (best != null)
                return best;
            return FindNearestInCachedScan<T>(pos, maxDist, name: null);
        }

        /// <summary>Nearest scene T whose name matches at pos.</summary>
        public static T FindNearestByName<T>(Vector3 pos, string name, float maxDist) where T : Component
        {
            T best = FindNearestInOverlap<T>(pos, maxDist, name);
            if (best != null)
                return best;
            return FindNearestInCachedScan<T>(pos, maxDist, name);
        }

        /// <summary>
        /// OverlapSphere only — never FOOT. Use when a miss is cheap to ignore
        /// (far stations outside client interest).
        /// </summary>
        public static T FindNearestNearbyOnly<T>(Vector3 pos, float maxDist) where T : Component
        {
            return FindNearestInOverlap<T>(pos, maxDist, name: null);
        }

        private static T FindNearestInOverlap<T>(Vector3 pos, float maxDist, string name) where T : Component
        {
            T best = null;
            float bestDistSq = maxDist * maxDist;
            int n = Physics.OverlapSphereNonAlloc(pos, maxDist, OverlapBuf);
            for (int i = 0; i < n; i++)
            {
                Collider col = OverlapBuf[i];
                if (col == null) continue;
                T c = col.GetComponentInParent<T>();
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                if (name != null && !c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                float dSq = (c.transform.position - pos).sqrMagnitude;
                if (dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = c;
                }
            }
            return best;
        }

        private static T FindNearestInCachedScan<T>(Vector3 pos, float maxDist, string name) where T : Component
        {
            T[] all = SceneScanCache<T>.Get();
            T best = null;
            float bestDist = maxDist;
            for (int i = 0; i < all.Length; i++)
            {
                T c = all[i];
                if (c == null || !c.gameObject.scene.IsValid()) continue;
                if (name != null && !c.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Per-T scene array, refreshed at most every <see cref="SceneScanTtl"/> seconds.</summary>
        private static class SceneScanCache<T> where T : Component
        {
            private static T[] _items = Array.Empty<T>();
            private static float _at = -999f;

            public static T[] Get()
            {
                float now = Time.unscaledTime;
                if (_items.Length == 0 && _at < 0f || now - _at >= SceneScanTtl)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _items = UnityEngine.Object.FindObjectsOfType<T>(true) ?? Array.Empty<T>();
                    sw.Stop();
                    Logging.ClientPerfProbe.NoteFindObjectsOfType(typeof(T).Name, sw.Elapsed.TotalMilliseconds);
                    _at = now;
                }
                return _items;
            }

            public static void Invalidate()
            {
                _items = Array.Empty<T>();
                _at = -999f;
            }
        }

        /// <summary>Find an Inventory by position (OverlapSphere + fallback scan).</summary>
        public static Inventory FindInventoryByPos(Vector3 pos, float maxDist = 10f)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, 1f, OverlapBuf);
            for (int i = 0; i < n; i++)
            {
                if (OverlapBuf[i] == null) continue;
                Inventory inv = OverlapBuf[i].GetComponentInParent<Inventory>();
                if (inv != null && (inv.invType == Inventory.InvType.itemInv || inv.invType == Inventory.InvType.deathDrop))
                    return inv;
            }

            Inventory best = null;
            float bestDist = maxDist;
            Inventory[] all = SceneScanCache<Inventory>.Get();
            for (int i = 0; i < all.Length; i++)
            {
                Inventory inv = all[i];
                if (inv == null || (inv.invType != Inventory.InvType.itemInv && inv.invType != Inventory.InvType.deathDrop))
                    continue;
                float d = Vector3.Distance(inv.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = inv;
                }
            }
            return best;
        }
    }
}
