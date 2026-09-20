using System;
using System.Collections.Generic;
using DWMPHorde;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scene spatial lookups for net apply handlers.
    /// OverlapSphere first; use a short-TTL scene cache only when needed
    /// (never FindObjectsOfTypeAll). Lures may have no collider, so uncached
    /// scene scans are avoided on the client.
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
        /// OverlapSphere only; never use a scene scan. Use when a miss is cheap to ignore
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

        /// <summary>Find an Inventory by position (OverlapSphere + fallback scan + DeathDrop).</summary>
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

            // Fallback: scan all item and death-drop inventories by position; handles containers
            // that are loaded but outside the 1m OverlapSphere radius (e.g. on the
            // host's scene when the client player looted a distant container).
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
            if (best != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[Container] FindInventoryByPos fallback found '{best.name}' at dist={bestDist:F2} from {pos}");
                return best;
            }

            // Final fallback: search DeathDrop objects by position (they may not have
            // a physics collider and the inventory type may be set after initialization)
            DeathDrop[] bags = UnityEngine.Object.FindObjectsOfType<DeathDrop>(true);
            DeathDrop closestBag = null;
            float closestBagDist = 15f;
            foreach (DeathDrop bag in bags)
            {
                if (bag == null) continue;
                float d = Vector3.Distance(bag.transform.position, pos);
                if (d < closestBagDist)
                {
                    closestBagDist = d;
                    closestBag = bag;
                }
            }
            if (closestBag != null)
            {
                Inventory inv = closestBag.GetComponent<Inventory>();
                if (inv != null)
                {
                    ModRuntime.LegacyInfo($"[Container] FindInventoryByPos: found DeathDrop inventory at {closestBagDist:F2}m from {pos}");
                    return inv;
                }
            }

            ModRuntime.LegacyInfo($"[Container] FindInventoryByPos: no inventory at {pos} (1m overlap + {maxDist}m scan + DeathDrop fallback)");
            return null;
        }

        public static Door FindDoorByPos(Vector3 pos) => FindDoorByPosLoose(pos, 2f);

        public static Door FindDoorByPosLoose(Vector3 pos, float radius)
        {
            // Tracker first (tight), then looser match, then physics overlap (B7).
            Door d = ListTracker<Door>.FindByPosition(pos, 0.5f);
            if (d != null) return d;
            d = ListTracker<Door>.FindByPosition(pos, Mathf.Min(1.5f, radius));
            if (d != null) return d;
            d = ListTracker<Door>.FindByPosition(pos, radius);
            if (d != null) return d;

            Collider[] nearby = Physics.OverlapSphere(pos, radius);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Door door = nearby[i].GetComponentInParent<Door>();
                if (door != null) return door;
                // MeleeSensor uses Door.getDoorScript(parent) for Door-tagged colliders
                if (nearby[i].CompareTag("Door") && nearby[i].transform.parent != null)
                {
                    door = Door.getDoorScript(nearby[i].transform.parent);
                    if (door != null) return door;
                }
            }
            return null;
        }

        public static Window FindWindowByPos(Vector3 pos) => FindWindowByPosLoose(pos, 2f);

        public static Window FindWindowByPosLoose(Vector3 pos, float radius)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, radius);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Window w = nearby[i].GetComponentInParent<Window>();
                if (w != null) return w;
            }
            Collider2D[] nearby2d = Physics2D.OverlapCircleAll(pos, radius);
            for (int i = 0; i < nearby2d.Length; i++)
            {
                if (nearby2d[i] == null) continue;
                Window w = nearby2d[i].GetComponentInParent<Window>();
                if (w != null) return w;
            }
            return null;
        }

        /// <summary>Nearest destructible Item by XZ distance (ignore Y drift).</summary>
        public static Item FindDestructibleItemXz(Vector3 pos, float maxDist)
        {
            float maxSq = maxDist * maxDist;
            Item best = null;
            float bestSq = maxSq;

            Collider[] nearby = Physics.OverlapSphere(pos, maxDist);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Item item = nearby[i].GetComponentInParent<Item>();
                if (item == null || !item.destructible) continue;
                float dx = item.transform.position.x - pos.x;
                float dz = item.transform.position.z - pos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = item;
                }
            }

            if (best != null) return best;

            // Collider may be disabled or moved after death; scan destructibles by XZ.
            Item[] all = UnityEngine.Object.FindObjectsOfType<Item>();
            for (int i = 0; i < all.Length; i++)
            {
                Item candidate = all[i];
                if (candidate == null || !candidate.destructible) continue;
                float dx = candidate.transform.position.x - pos.x;
                float dz = candidate.transform.position.z - pos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq < bestSq)
                {
                    bestSq = dSq;
                    best = candidate;
                }
            }
            return best;
        }
    }
}
