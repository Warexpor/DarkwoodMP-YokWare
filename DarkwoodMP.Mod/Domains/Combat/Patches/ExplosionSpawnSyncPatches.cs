using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class ExplosionSpawnFlagTracker
    {
        public static bool IsInsideSpawnObjects;
        /// <summary>True when spawnObjects() is running for a host-synced ThrownItem (duplicate of client throw).</summary>
        public static bool IsHostSynced;
        /// <summary>The Explodes instance whose onActivate() is currently executing. Set in Prefix, used by AddPrefab Postfix to filter out explosionPrefab.</summary>
        public static Explodes CurrentExplodes;
        /// <summary>Re-entrancy counter: increments on Prefix, decrements on Postfix.
        /// Prevents nested explosions from clearing flags prematurely.</summary>
        public static int ActivationDepth;

        // After local Explodes already ran spawnObjects (local stomp or SpawnExplosionVisual),
        // host may still send ExplosionSpawnObject for the same secondaries; debounce those.
        private static float _localExplodeFxUntil;
        private static Vector3 _localExplodeFxPos;

        public static void NoteLocalExplodeFx(Vector3 pos)
        {
            _localExplodeFxPos = pos;
            _localExplodeFxUntil = Time.time + 0.5f;
        }

        public static bool ShouldSkipExplosionSpawnObject(Vector3 pos)
        {
            if (Time.time >= _localExplodeFxUntil) return false;
            return (pos - _localExplodeFxPos).sqrMagnitude < 4f; // 2 unit radius
        }
    }

    /// <summary>
    /// Prefix/Finalizer on Explodes.onActivate() to set IsInsideSpawnObjects before
    /// spawnObjects() runs, so ExplosionObjectSpawnSyncPatch can intercept the
    /// Core.AddPrefab calls.
    /// </summary>
    [HarmonyPatch(typeof(Explodes), "onActivate", new System.Type[0])]
    public static class ExplosionOnActivatePrefix
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static void Prefix(Explodes __instance)
        {
            ExplosionSpawnFlagTracker.ActivationDepth++;

            var net = ModRuntime.Network;
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[FX] entered role=" + (net?.Role.ToString() ?? "null") + " obj=" + __instance?.name + " hasThrown=" + (__instance.GetComponent<ThrownItem>() != null));

            ExplosionSpawnFlagTracker.CurrentExplodes = __instance;
            ExplosionSpawnFlagTracker.IsInsideSpawnObjects = false;
            ExplosionSpawnFlagTracker.IsHostSynced = false;

            if (net == null || net.Role == NetworkRole.Offline) return;

            if (net.Role == NetworkRole.Host)
            {
                ThrownItem ti = __instance.GetComponent<ThrownItem>();
                if (ti != null && ti.objectThatSpawnedMe != null)
                {
                    bool isProxySpawned = false;
                    foreach (var proxy in net.GetAllProxies())
                    {
                        if (proxy != null && ti.objectThatSpawnedMe == proxy.transform)
                        {
                            isProxySpawned = true;
                            break;
                        }
                    }
                    if (isProxySpawned)
                    {
                        ExplosionSpawnFlagTracker.IsHostSynced = true;
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[FX] IsHostSynced=true");
                    }
                }
            }

            ExplosionSpawnFlagTracker.IsInsideSpawnObjects = true;
        }

        // Finalizer (not Postfix): onActivate can throw after Prefix bumped
        // ActivationDepth / IsInsideSpawnObjects; Postfix would leave depth sticky
        // and host would keep treating AddPrefab as explosion secondaries forever.
        [HarmonyPriority(Priority.Last)]
        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (ExplosionSpawnFlagTracker.ActivationDepth > 0)
                ExplosionSpawnFlagTracker.ActivationDepth--;
            if (ExplosionSpawnFlagTracker.ActivationDepth > 0)
                return; // Still inside a nested explosion — outer Finalizer will clear

            ExplosionSpawnFlagTracker.IsInsideSpawnObjects = false;
            ExplosionSpawnFlagTracker.IsHostSynced = false;
            ExplosionSpawnFlagTracker.CurrentExplodes = null;
        }
    }

    [HarmonyPatch(typeof(Explodes), "explode")]
    public static class ExplosionDamageSkipPatch
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static void Prefix()
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (Sync.WorldPhysicsSyncService._suppressBroadcast) return;
            TraverseHack.IsInsideLocalExplosion = true;
        }

        [HarmonyPriority(Priority.Last)]
        [HarmonyPostfix]
        private static void Postfix()
        {
            // Always clear. Even if Prefix skipped, false is the safe idle state.
            TraverseHack.IsInsideLocalExplosion = false;
        }

        // explode can throw; stuck true mis-routes client hitscan as explosion AOE forever.
        [HarmonyPriority(Priority.Last)]
        [HarmonyFinalizer]
        private static void Finalizer()
        {
            TraverseHack.IsInsideLocalExplosion = false;
        }
    }

    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(Object), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class ExplosionObjectSpawnSyncPatch
    {
        private static void Postfix(ref GameObject __result, object[] __args)
        {
            UnityEngine.Object prefab = (UnityEngine.Object)__args[0];
            Vector3 position = (Vector3)__args[1];
            Quaternion quaternion = (Quaternion)__args[2];

            bool flag = ExplosionSpawnFlagTracker.IsInsideSpawnObjects;
            var log = ModRuntime.Log;
            if (flag && ModRuntime.VerboseLogging)
                log?.LogInfo("[FX] ENTERED flag=true prefab=" + (prefab?.name ?? "null") + " role=" + (ModRuntime.Network?.Role.ToString() ?? "null"));

            if (!flag) return;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (__result == null || prefab == null) return;

            if (ExplosionSpawnFlagTracker.IsHostSynced) return;

            if (ExplosionSpawnFlagTracker.CurrentExplodes != null)
            {
                Object ep = ExplosionSpawnFlagTracker.CurrentExplodes.explosionPrefab;
                if (ep != null && prefab == ep) return;
            }

            // Gas puddles / flamable scatter: GasTrail channel owns layout (host-only).
            // Sending both ExplosionSpawnObject + GasTrail doubles client density ("wild").
            if (GasSyncPolicy.IsGasolineTrailPrefab(prefab))
                return;

            string prefabName = prefab.name;
            if (string.IsNullOrEmpty(prefabName)) return;

            Vector3 euler = quaternion.eulerAngles;
            if (ModRuntime.VerboseLogging)
                log?.LogInfo("[FX] SENDING " + prefabName + " at " + position + " rot=" + euler);
            net.SendExplosionSpawnObject(prefabName, position, euler);
        }
    }
}
