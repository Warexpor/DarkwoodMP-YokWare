using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    internal static class ExplosionSpawnFlagTracker
    {
        public static bool IsInsideSpawnObjects; // process-scoped: call-scoped, unwound by its Finalizer/finally
        /// <summary>The Explodes instance whose onActivate() is currently executing. Set in Prefix, used by AddPrefab Postfix to filter out explosionPrefab.</summary>
        public static Explodes CurrentExplodes; // process-scoped: call-scoped, unwound by its Finalizer/finally
        /// <summary>Re-entrancy counter: increments on Prefix, decrements on Postfix.
        /// Prevents nested explosions from clearing flags prematurely.</summary>
        public static int ActivationDepth; // process-scoped: call-scoped, unwound by its Finalizer/finally

        // After local Explodes already ran spawnObjects (local stomp or SpawnExplosionVisual),
        // host may still send ExplosionSpawnObject for the same secondaries; debounce those.
        private static float _localExplodeFxUntil;
        private static Vector3 _localExplodeFxPos;

        /// <summary>Session end: drop the local-explosion debounce window.</summary>
        public static void Reset()
        {
            _localExplodeFxUntil = 0f;
            _localExplodeFxPos = Vector3.zero;
        }

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
                ModRuntime.LegacyInfo($"[FX] entered role={(net?.Role.ToString() ?? "null")} obj={(__instance?.name)} hasThrown={(__instance.GetComponent<ThrownItem>() != null)}");

            ExplosionSpawnFlagTracker.CurrentExplodes = __instance;
            ExplosionSpawnFlagTracker.IsInsideSpawnObjects = false;

            if (net == null || net.Role == NetworkRole.Offline) return;

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
            ExplosionSpawnFlagTracker.CurrentExplodes = null;
        }
    }

    /// <summary>
    /// Client: a barrel, mushroom or bomb set off here (shot, ignited, stepped on) explodes for
    /// real on the host, which <see cref="ExplosionTriggerPatch"/> asks for. Running vanilla's
    /// blast here too hit every enemy twice (the client's hits went to the host as attacks on top
    /// of the host's own blast) and the client's own player twice (locally and by the host's
    /// DamagePlayer). The client keeps the look and sound only; the host deals the damage, the
    /// effects and the world hits. Also for a blast replayed from the host (a story event):
    /// the host's own blast already hit everyone, clients included (DamagePlayer).
    /// </summary>
    [HarmonyPatch(typeof(Explodes), "explode")]
    public static class ExplosionDamageSkipPatch
    {
        [HarmonyPriority(Priority.Last)]
        [HarmonyPrefix]
        private static bool Prefix(Explodes __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Client || !net.IsConnected) return true;
            if (__instance == null || __instance.effect == null) return true; // vanilla: logs, returns
            if (!string.IsNullOrEmpty(__instance.explodeSound))
                AudioController.Play(__instance.explodeSound, __instance.transform.position);
            if (__instance.destroyOnExplode)
                __instance.gameObject.DestroyMe();
            return false;
        }
    }

    /// <summary>
    /// The thrown item whose landing (<c>ThrownItem.onCollide</c>) is running. Vanilla spawns its
    /// <c>prefabToSpawnOnLand</c> there, after the Explodes activation: the gas bomb's gas cloud
    /// (Gas_flamable) comes from this, not from Explodes.spawnObjects, so the secondary send
    /// below never saw it and the clients (whose copies are muted) had no gas at all.
    /// </summary>
    [HarmonyPatch(typeof(ThrownItem), "onCollide", typeof(Collider), typeof(Vector3))]
    public static class ThrownItemLandScope
    {
        internal static ThrownItem Current; // process-scoped: call-scoped, unwound by the Finalizer

        private static void Prefix(ThrownItem __instance, out ThrownItem __state)
        {
            __state = Current;
            Current = __instance;
        }

        private static void Finalizer(ThrownItem __state)
        {
            Current = __state;
        }
    }

    /// <remarks>Applied from <see cref="CoreAddPrefabObjectPatch"/> (one detour for all features).</remarks>
    public static class ExplosionObjectSpawnSyncPatch
    {
        internal static void OnAddPrefab(GameObject __result, Object prefab, Vector3 position, Quaternion quaternion)
        {
            // Host: a thrown item's land spawn (the gas bomb's gas) reaches the clients as itself.
            ThrownItem landing = ThrownItemLandScope.Current;
            if (landing != null && prefab != null && __result != null
                && prefab == landing.prefabToSpawnOnLand
                && !ExplosionSpawnFlagTracker.IsInsideSpawnObjects
                && !TraverseHack.ApplyingFromNetwork
                && NetGuard.Host(out var landNet)
                && !string.IsNullOrEmpty(prefab.name))
            {
                landNet.SendExplosionSpawnObject(prefab.name, position, quaternion.eulerAngles);
                ModRuntime.LegacyInfo($"[ExplosionSync] sent land spawn {prefab.name} of {landing.name} at {position}");
                return;
            }

            bool flag = ExplosionSpawnFlagTracker.IsInsideSpawnObjects;
            var log = ModRuntime.Log;
            if (flag && ModRuntime.VerboseLogging)
                log?.LogInfo("[FX] ENTERED flag=true prefab=" + (prefab?.name ?? "null") + " role=" + (ModRuntime.Network?.Role.ToString() ?? "null"));

            if (!flag) return;
            if (!NetGuard.Host(out var net)) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (__result == null || prefab == null) return;

            // A peer's throw included: every peer's copy of a throw is muted
            // (MuteThrownCombat drops its secondaries), so only this send puts its fire and debris
            // on the clients, the thrower too.

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
