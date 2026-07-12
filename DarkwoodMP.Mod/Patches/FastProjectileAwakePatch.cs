using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op FastProjectile hygiene:
    /// 1) Sweep distance tracks actual velocity (covers one physics tick of travel for proxy hits).
    /// 2) Never leave a permanent long ray on a stalled pellet (old Awake MinDistance=15 made
    ///    frozen-in-air pellets into 15u kill beams — walk into them = ghost damage).
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

        public static void Reset()
        {
            _spawnedAt.Clear();
            _stallSince.Clear();
        }

        private static bool Prefix(FastProjectile __instance)
        {
            if (__instance == null || !__instance.active)
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline)
                return true;

            int id = __instance.GetInstanceID();
            float now = Time.unscaledTime;
            if (!_spawnedAt.ContainsKey(id))
                _spawnedAt[id] = now;

            Rigidbody rb = __instance.GetComponent<Rigidbody>();
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
                // Stalled: collider-scale only — no floating 15u death ray.
                float extents = 0.5f;
                try
                {
                    Collider col = __instance.GetComponent<Collider>();
                    if (col != null)
                        extents = Mathf.Max(0.15f, col.bounds.extents.y * 2f);
                }
                catch { /* ignore */ }
                dist = extents;

                if (!_stallSince.ContainsKey(id))
                    _stallSince[id] = now;
            }

            Traverse.Create(__instance).Field("distance").SetValue(dist);

            // ThrownItem keeps its own lifetime; only cull Bullet pellets / FX projectiles.
            ThrownItem thrown = __instance.GetComponent<ThrownItem>();
            if (thrown != null)
            {
                TraverseHack.IsInsideFastProjectileRaycast = true;
                return true;
            }

            Bullet bullet = __instance.GetComponent<Bullet>();
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

        private static void Postfix()
        {
            TraverseHack.IsInsideFastProjectileRaycast = false;
        }

        private static void DespawnProjectile(FastProjectile fp)
        {
            if (fp == null) return;
            int id = fp.GetInstanceID();
            _spawnedAt.Remove(id);
            _stallSince.Remove(id);
            fp.active = false;

            try
            {
                Core.RemovePooledPrefab(fp.transform);
            }
            catch
            {
                // Non-pooled AddPrefab path — RemovePooledPrefab may Destroy already.
            }

            if (fp != null && fp.gameObject != null)
                Object.Destroy(fp.gameObject);
        }
    }

    /// <summary>
    /// Legacy Awake hook kept so older code paths / docs still compile.
    /// Sweep distance is owned by <see cref="FastProjectileSweepPatch"/> every FixedUpdate;
    /// Awake no longer installs a permanent 15u ray (that was the freeze kill-beam).
    /// </summary>
    [HarmonyPatch(typeof(FastProjectile), "Awake")]
    public static class FastProjectileAwakePatch
    {
        private static void Postfix(FastProjectile __instance)
        {
            // no-op under network: FixedUpdate patch sets distance from velocity.
            // Offline: leave vanilla collider-based distance alone.
        }
    }
}
