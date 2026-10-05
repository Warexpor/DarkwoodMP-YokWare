using System;
using System.Collections.Generic;
using DWMPHorde;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scene spatial lookups for net apply handlers.
    /// OverlapSphere first; on a miss, the type's scene registry (no scan) or, for
    /// types without one, a short-TTL scene cache (never FindObjectsOfTypeAll).
    /// Lures may have no collider, so uncached scene scans are avoided on the client.
    /// </summary>
    internal static class WorldQueryHelper
    {
        // Dream GE name resolve uses up to ~80m radius; 64 silently truncates NonAlloc
        // hits and forces soft-match / full scene walks (dual-box hitch).
        private static readonly Collider[] OverlapBuf = new Collider[1024];
        private static readonly Collider2D[] Overlap2DBuf = new Collider2D[256];
        private const float SceneScanTtl = 3f;

        /// <summary>
        /// Shared NonAlloc buffer for one-shot world lookups outside
        /// <see cref="WorldPhysicsSyncService"/>. Callers must finish iterating
        /// before another SharedOverlapBuf user runs.
        /// </summary>
        internal static Collider[] SharedOverlapBuf => OverlapBuf;

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
            float bestDistSq = maxDist * maxDist;
            for (int i = 0; i < all.Length; i++)
            {
                T c = all[i];
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

        /// <summary>
        /// Every scene T (inactive included) for soft-match / bulk paths that cannot use
        /// OverlapSphere alone: the type's registry when it has one (<see cref="SceneRegistries"/>),
        /// else a short-TTL scene scan. Prefer <see cref="FindNearest{T}"/> when possible.
        /// </summary>
        public static T[] GetCachedSceneComponents<T>() where T : Component
            => SceneScanCache<T>.Get();

        /// <summary>Drop cached scene arrays (dream load / host migrate / reset).</summary>
        public static void InvalidateSceneScanCache<T>() where T : Component
            => SceneScanCache<T>.Invalidate();

        /// <summary>
        /// Clear TTL caches for types used by hot apply paths. Call on network stop
        /// and dream enter/exit so pad vs overworld clones are not resolved stale.
        /// </summary>
        public static void InvalidateCommonSceneScanCaches()
        {
            InvalidateSceneScanCache<GameEvents>();
            InvalidateSceneScanCache<Door>();
            InvalidateSceneScanCache<Window>();
            InvalidateSceneScanCache<Item>();
            InvalidateSceneScanCache<Inventory>();
            InvalidateSceneScanCache<Generator>();
            InvalidateSceneScanCache<NPC>();
            InvalidateSceneScanCache<Character>();
            InvalidateSceneScanCache<DeathDrop>();
            InvalidateSceneScanCache<Rigidbody>();
            InvalidateSceneScanCache<Padlock>();
            InvalidateSceneScanCache<Locked>();
            InvalidateSceneScanCache<InteractiveItem>();
            InvalidateSceneScanCache<Constructible>();
            InvalidateSceneScanCache<Burn>();
            InvalidateSceneScanCache<Liquid>();
            InvalidateSceneScanCache<ChainParent>();
            InvalidateSceneScanCache<ShadowArmor>();
            InvalidateSceneScanCache<Saw>();
            InvalidateSceneScanCache<Feeder>();
            InvalidateSceneScanCache<Lure>();
            InvalidateSceneScanCache<CustomCursorAction>();
            InvalidateSceneScanCache<Infection>();
            InvalidateSceneScanCache<ExperienceMachine>();
            InvalidateSceneScanCache<MapElement>();
            InvalidateSceneScanCache<CharacterDialogue>();
            InvalidateSceneScanCache<UniqueObject>();
            InvalidateSceneScanCache<CutsceneManager>();
            InvalidateSceneScanCache<JournalNoteReference>();
            InvalidateSceneScanCache<KeyReference>();
            InvalidateSceneScanCache<QuestItemReference>();
            WorldPhysicsSyncService.InvalidateDreamPropColliderCache();
        }

        /// <summary>
        /// Per-T scene array. Types with a <see cref="SceneRegistry{T}"/> read it once the world is
        /// seeded (same objects, inactive included, no scan; invalidating them is a no-op since the
        /// registry is live). Other types, and every type before the seed, scan at most every
        /// <see cref="SceneScanTtl"/> seconds.
        /// </summary>
        private static class SceneScanCache<T> where T : Component
        {
            private static T[] _items = Array.Empty<T>(); // process-scoped: short-TTL scene cache, dropped by InvalidateCommonSceneScanCaches on stop
            private static float _at = -999f; // process-scoped: short-TTL scene cache, dropped by InvalidateCommonSceneScanCaches on stop

            public static T[] Get()
            {
                if (SceneRegistries.Covers<T>())
                    return SceneRegistry<T>.Snapshot();
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

        /// <summary>
        /// Find an Inventory by position. OverlapSphere first and return immediately on hit
        /// — the old path always ran SceneScanCache&lt;Inventory&gt; (FindObjectsOfType ~45ms)
        /// even after overlap succeeded, hitching every container/corpse open.
        /// </summary>
        public static Inventory FindInventoryByPos(Vector3 pos, float maxDist = 2.5f)
        {
            float overlapR = Mathf.Max(maxDist, 1.5f);
            int n = Physics.OverlapSphereNonAlloc(pos, overlapR, OverlapBuf);
            Inventory overlapBest = null;
            float overlapBestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (OverlapBuf[i] == null) continue;
                Inventory inv = OverlapBuf[i].GetComponentInParent<Inventory>();
                if (inv == null)
                    inv = OverlapBuf[i].GetComponentInChildren<Inventory>();
                if (inv == null || (inv.invType != Inventory.InvType.itemInv && inv.invType != Inventory.InvType.deathDrop))
                    continue;
                float d = Vector3.Distance(inv.transform.position, pos);
                if (d < overlapBestD)
                {
                    overlapBestD = d;
                    overlapBest = inv;
                }
            }
            if (overlapBest != null && overlapBestD <= maxDist)
                return overlapBest;

            // Cache miss / no collider: short-TTL scene scan (rate-limited by SceneScanCache).
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

            DeathDrop[] bags = GetCachedSceneComponents<DeathDrop>();
            DeathDrop closestBag = null;
            float closestBagDist = 3f;
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

            ModRuntime.LegacyInfo($"[Container] FindInventoryByPos: no inventory at {pos} (overlap {overlapR:F1}m + scan + DeathDrop)");
            return null;
        }

        /// <summary>
        /// An inactive door / window with an active one of the same type at its spot. Vanilla
        /// scenes keep such twins (border_main_cottageTrailer_01: two DoorSmall1 and two Window_2x1
        /// at one position, one inactive); peers key state by position, so a twin's stale state
        /// would land on the live one. Senders skip it. A lone inactive one (its location switched
        /// off) is not a twin and still counts.
        /// </summary>
        public static bool IsInactiveTwin<T>(T c) where T : Component
            => c != null && !c.gameObject.activeInHierarchy && IsInactiveTwin(c, GetCachedSceneComponents<T>());

        /// <summary><see cref="IsInactiveTwin{T}(T)"/> against a list already in hand (no scene scan).</summary>
        public static bool IsInactiveTwin<T>(T c, IList<T> all) where T : Component
        {
            if (c == null || all == null || c.gameObject.activeInHierarchy)
                return false;
            Vector3 p = c.transform.position;
            for (int i = 0; i < all.Count; i++)
            {
                T o = all[i];
                if (o == null || o == c || !o.gameObject.activeInHierarchy)
                    continue;
                if ((o.transform.position - p).sqrMagnitude < 0.25f)
                    return true;
            }
            return false;
        }

        public static Door FindDoorByPos(Vector3 pos) => FindDoorByPosLoose(pos, 2f);

        public static Door FindDoorByPosLoose(Vector3 pos, float radius)
        {
            // Tracker first (tight), then looser match, then physics overlap. The tracker also
            // lists inactive doors (vanilla keeps inactive twins at a live door's spot): an
            // inactive hit is only the answer when no tier finds an active door.
            Door inactiveHit = null;
            Door d = ListTracker<Door>.FindByPosition(pos, 0.5f);
            if (d != null && d.gameObject.activeInHierarchy) return d;
            inactiveHit = d;
            d = ListTracker<Door>.FindByPosition(pos, Mathf.Min(1.5f, radius));
            if (d != null && d.gameObject.activeInHierarchy) return d;
            inactiveHit = inactiveHit ?? d;
            d = ListTracker<Door>.FindByPosition(pos, radius);
            if (d != null && d.gameObject.activeInHierarchy) return d;
            inactiveHit = inactiveHit ?? d;

            // Physics only reports colliders of active objects.
            int n = Physics.OverlapSphereNonAlloc(pos, radius, OverlapBuf);
            Door best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (OverlapBuf[i] == null) continue;
                Door door = OverlapBuf[i].GetComponentInParent<Door>();
                if (door == null && OverlapBuf[i].CompareTag("Door") && OverlapBuf[i].transform.parent != null)
                    door = Door.getDoorScript(OverlapBuf[i].transform.parent);
                if (door == null) continue;
                float dist = Vector3.Distance(door.transform.position, pos);
                if (dist < bestD)
                {
                    bestD = dist;
                    best = door;
                }
            }
            return best ?? inactiveHit;
        }

        public static Window FindWindowByPos(Vector3 pos) => FindWindowByPosLoose(pos, 2f);

        public static Window FindWindowByPosLoose(Vector3 pos, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(pos, radius, OverlapBuf);
            Window best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (OverlapBuf[i] == null) continue;
                Window w = OverlapBuf[i].GetComponentInParent<Window>();
                if (w == null) continue;
                float d = Vector3.Distance(w.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = w;
                }
            }
            if (best != null) return best;
            int n2 = Physics2D.OverlapCircleNonAlloc(pos, radius, Overlap2DBuf);
            for (int i = 0; i < n2; i++)
            {
                if (Overlap2DBuf[i] == null) continue;
                Window w = Overlap2DBuf[i].GetComponentInParent<Window>();
                if (w == null) continue;
                float d = Vector3.Distance(w.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = w;
                }
            }
            return best;
        }

        /// <summary>Nearest destructible Item by XZ distance (ignore Y drift).</summary>
        public static Item FindDestructibleItemXz(Vector3 pos, float maxDist)
        {
            float maxSq = maxDist * maxDist;
            Item best = null;
            float bestSq = maxSq;

            int nearbyN = Physics.OverlapSphereNonAlloc(pos, maxDist, OverlapBuf);
            for (int i = 0; i < nearbyN; i++)
            {
                if (OverlapBuf[i] == null) continue;
                Item item = OverlapBuf[i].GetComponentInParent<Item>();
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
            Item[] all = GetCachedSceneComponents<Item>();
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
