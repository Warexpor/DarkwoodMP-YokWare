using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using PathologicalGames;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op FastProjectile hygiene:
    /// 1) Sweep distance tracks actual velocity (covers one physics tick of travel for proxy hits).
    /// 2) Never leave a permanent long ray on a stalled pellet (old Awake MinDistance=15 made
    ///    frozen-in-air pellets into long kill beams, causing ghost damage).
    /// 3) Despawn stalled / over-age bullets. Player spawnBullet uses AddPrefab (not pool),
    ///    so vanilla Bullet.OnSpawned WaitForDeath often never runs.
    /// </summary>
    [HarmonyPatch(typeof(FastProjectile), "FixedUpdate")]
    public static class FastProjectileSweepPatch
    {
        /// <summary>Pad beyond one-tick travel so thin proxy colliders are not skipped.</summary>
        private const float SweepPad = 2f;
        private const float MaxSweep = 20f;
        private const float MinSweepMoving = 0.5f;
        /// <summary>Below this speed the pellet is treated as stalled (no long kill-beam).</summary>
        private const float StallSpeed = 20f;
        /// <summary>Unscaled seconds nearly-still before force despawn.</summary>
        private const float StallDespawnSec = 0.35f;
        /// <summary>Hard max age (unscaled) when Bullet.longevity is missing / never started.</summary>
        private const float FallbackMaxAgeSec = 2.5f;

        private static readonly Dictionary<int, float> _spawnedAt =
            new Dictionary<int, float>(64);
        private static readonly Dictionary<int, float> _stallSince =
            new Dictionary<int, float>(64);

        /// <summary>Components looked up once per projectile spawn instead of every FixedUpdate.</summary>
        private struct Parts
        {
            public Rigidbody Body;
            public Collider Collider;
            public ThrownItem Thrown;
            public Bullet Bullet;
        }

        private const int MaxTrackedParts = 2048;
        private static readonly Dictionary<int, Parts> _parts = new Dictionary<int, Parts>(64);

        private static readonly AccessTools.FieldRef<FastProjectile, float> SweepDistance =
            AccessTools.FieldRefAccess<FastProjectile, float>("distance");

        public static void Reset()
        {
            _spawnedAt.Clear();
            _stallSince.Clear();
            _parts.Clear();
        }

        /// <summary>Remove tracking for a projectile id (called on despawn/destroy).</summary>
        public static void ResetEntry(int id)
        {
            _spawnedAt.Remove(id);
            _stallSince.Remove(id);
            _parts.Remove(id);
        }

        private static Parts PartsOf(FastProjectile fp, int id)
        {
            if (_parts.TryGetValue(id, out Parts parts))
                return parts;
            // Non-pooled projectiles destroyed without onCollide never call ResetEntry.
            if (_parts.Count >= MaxTrackedParts)
                _parts.Clear();
            parts = new Parts
            {
                Body = fp.GetComponent<Rigidbody>(),
                Collider = fp.GetComponent<Collider>(),
                Thrown = fp.GetComponent<ThrownItem>(),
                Bullet = fp.GetComponent<Bullet>()
            };
            _parts[id] = parts;
            return parts;
        }

        private static bool Prefix(FastProjectile __instance)
        {
            if (__instance == null || !__instance.active)
                return true;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;

            int id = __instance.GetInstanceID();
            float now = Time.unscaledTime;
            if (!_spawnedAt.ContainsKey(id))
            {
                _spawnedAt[id] = now;
                // Fresh projectile; never inherit stale stall state from a recycled
                // instance id (a previous projectile destroyed outside onCollide/sweep).
                _stallSince.Remove(id);
            }

            Parts parts = PartsOf(__instance, id);
            Rigidbody rb = parts.Body;
            float speed = rb != null ? rb.velocity.magnitude : 0f;

            // Velocity-scaled sweep: reliable while flying, tiny when stopped.
            float dist;
            if (speed >= StallSpeed)
            {
                dist = Mathf.Clamp(speed * Time.fixedDeltaTime + SweepPad, MinSweepMoving, MaxSweep);
                _stallSince.Remove(id);
            }
            else
            {
                // Stalled: collider-scale only; no floating death ray.
                float extents = 0.5f;
                try
                {
                    Collider col = parts.Collider;
                    if (col != null)
                        extents = Mathf.Max(0.15f, col.bounds.extents.y * 2f);
                }
                catch { /* ignore */ }
                dist = extents;

                if (!_stallSince.ContainsKey(id))
                    _stallSince[id] = now;
            }

            SweepDistance(__instance) = dist;

            // ThrownItem keeps its own lifetime; only cull Bullet pellets / FX projectiles.
            ThrownItem thrown = parts.Thrown;
            if (thrown != null)
            {
                TraverseHack.IsInsideFastProjectileRaycast = true;
                return true;
            }

            Bullet bullet = parts.Bullet;
            float maxAge = FallbackMaxAgeSec;
            if (bullet != null && bullet.longevity > 0.05f)
                maxAge = Mathf.Max(bullet.longevity + 0.25f, 0.5f);

            bool agedOut = now - _spawnedAt[id] >= maxAge;
            bool stalledOut = _stallSince.TryGetValue(id, out float stallT)
                && now - stallT >= StallDespawnSec;

            if (agedOut || stalledOut)
            {
                if (ModRuntime.VerboseLogging)
                {
                    ModRuntime.LegacyInfo(
                        "[Projectile] despawn "
                        + __instance.name
                        + (agedOut ? " age" : " stall")
                        + " speed=" + speed.ToString("F1")
                        + " age=" + (now - _spawnedAt[id]).ToString("F2"));
                }
                DespawnProjectile(__instance);
                return false;
            }

            TraverseHack.IsInsideFastProjectileRaycast = true;
            return true;
        }

        // FixedUpdate can throw; stuck true makes HitscanImpactSyncPatch skip forever.
        private static void Finalizer()
        {
            TraverseHack.IsInsideFastProjectileRaycast = false;
        }

        private static void DespawnProjectile(FastProjectile fp)
        {
            if (fp == null) return;
            ResetEntry(fp.GetInstanceID());

            // Only an instance vanilla is about to Destroy needs `active` cleared (Destroy is
            // deferred, FixedUpdate could still sweep once). A pooled instance keeps its serialized
            // active=true: nothing re-arms it on the next spawn, so clearing it would make every
            // reused bullet permanently harmless.
            Transform t = fp.transform;
            bool pooled = PoolManager.Pools["FX"].IsSpawned(t) || PoolManager.Pools["UI"].IsSpawned(t);
            if (!pooled)
                fp.active = false;

            // Vanilla RemovePooledPrefab returns a pooled instance to its pool and Destroys only an
            // instance no pool owns. Destroying a pooled one afterwards would leave a dead entry in
            // the PrefabPool (MissingReferenceException on the next SpawnInstance).
            Core.RemovePooledPrefab(fp.transform);
        }
    }

    /// <summary>
    /// Pooled projectiles are reused under the same instance id: vanilla Bullet.WaitForDeath
    /// despawns them without passing through FastProjectile.onCollide, so a stale spawn time /
    /// stall timer would age the NEXT bullet out on its first tick. OnSpawned is the pool's spawn
    /// callback; start every spawn with clean sweep state.
    /// </summary>
    [HarmonyPatch(typeof(Bullet), "OnSpawned")]
    public static class FastProjectileSpawnResetPatch
    {
        private static void Prefix(Bullet __instance)
        {
            if (__instance == null) return;
            FastProjectile fp = __instance.GetComponent<FastProjectile>();
            if (fp != null)
                FastProjectileSweepPatch.ResetEntry(fp.GetInstanceID());
        }
    }

    /// <summary>
    /// Clean up sweep tracking when a projectile hits (vanilla collision/despawn path),
    /// so GetInstanceID entries never leak and a recycled id is not misread as aged/stalled.
    /// FastProjectile declares no OnDestroy, so onCollide is the hook for its normal death.
    /// </summary>
    [HarmonyPatch(typeof(FastProjectile), "onCollide")]
    public static class FastProjectileCleanupPatch
    {
        private static void Postfix(FastProjectile __instance)
        {
            if (__instance == null) return;
            int id = __instance.GetInstanceID();
            FastProjectileSweepPatch.ResetEntry(id);
        }
    }
}
