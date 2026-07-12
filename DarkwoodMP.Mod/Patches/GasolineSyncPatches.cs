using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative gasoline trails + fire.
    /// Both peers used to scatter <c>spawnObjects()</c> and bidirectionally sync trails/ignites
    /// → client gas bomb looked "wild", molotov flame cover mismatched, dual fire sim stuttered.
    /// </summary>
    internal static class GasSyncPolicy
    {
        internal static bool IsGasolineTrailPrefab(Object prefab)
        {
            if (prefab == null) return false;
            string n = prefab.name ?? "";
            // Pour puddles only. Do NOT treat Gas_flamable (Explodes secondary) as a trail —
            // that still uses ExplosionSpawnObject so the correct prefab lands on clients.
            return n.IndexOf("GasolineTrail", System.StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("gasolineTrail", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsGasolineTrailPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return path.IndexOf("GasolineTrail", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Client must not invent trails/fire — only apply host network events.</summary>
        internal static bool ClientMustNotMutateWorld()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return false;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState)
                return false;
            if (TraverseHack.GetExplicitFlag())
                return false;
            return true;
        }
    }

    /// <summary>
    /// String AddPrefab path for gasoline trails (pour can + network SpawnGasTrail).
    /// Host only broadcasts. Client local spawns are blocked (host owns layout).
    /// </summary>
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class GasolineTrailSpawnPatch
    {
        private static readonly List<Vector3> _pendingTrails = new List<Vector3>(32);
        private static float _nextFlushTime;
        private const float FlushInterval = 0.08f;
        private const int MaxBatchSize = 12;

        public static void Reset()
        {
            _pendingTrails.Clear();
            _nextFlushTime = 0f;
        }

        /// <summary>Client: skip local trail spawn (prevents wild double scatter).</summary>
        private static bool Prefix(object[] __args)
        {
            string prefab = __args != null && __args.Length > 0 ? __args[0] as string : null;
            if (!GasSyncPolicy.IsGasolineTrailPath(prefab))
                return true;
            if (!GasSyncPolicy.ClientMustNotMutateWorld())
                return true;
            // Host will send GasTrailSpawn / ExplosionSpawnObject with authoritative positions.
            return false;
        }

        private static void Postfix(ref GameObject __result, object[] __args)
        {
            string prefab = (string)__args[0];
            Vector3 position = (Vector3)__args[1];
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role != NetworkRole.Host) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;
            if (TraverseHack.GetExplicitFlag()) return;
            if (__result == null) return;
            if (!GasSyncPolicy.IsGasolineTrailPath(prefab)) return;

            EnqueueHostTrail(position);
        }

        internal static void EnqueueHostTrail(Vector3 position)
        {
            _pendingTrails.Add(position);
            float now = Time.unscaledTime;
            if (now < _nextFlushTime && _pendingTrails.Count < MaxBatchSize)
                return;
            FlushPendingTrails(now);
        }

        private static void FlushPendingTrails(float now)
        {
            if (_pendingTrails.Count == 0) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || ModRuntime.Network.Role != NetworkRole.Host)
            {
                _pendingTrails.Clear();
                return;
            }

            _nextFlushTime = now + FlushInterval;
            // Dedupe near-duplicates in one flush (molotov/gas scatter packs tight).
            for (int i = 0; i < _pendingTrails.Count; i++)
            {
                Vector3 p = _pendingTrails[i];
                bool nearDup = false;
                for (int j = 0; j < i; j++)
                {
                    if ((_pendingTrails[j] - p).sqrMagnitude < 0.25f) // 0.5u
                    {
                        nearDup = true;
                        break;
                    }
                }
                if (nearDup) continue;

                ModRuntime.Network.SendGasTrailSpawn(new GasTrailSpawnMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z
                });
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[GasTrailSync] host flushed " + _pendingTrails.Count + " trails");

            _pendingTrails.Clear();
        }
    }

    /// <summary>
    /// Object AddPrefab overload — gas bomb <c>spawnObjects()</c> uses Object prefab, not string path.
    /// Host relays positions via GasTrail (same channel as string path). Client never local-scatters.
    /// </summary>
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(Object), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class GasolineTrailObjectSpawnPatch
    {
        private static bool Prefix(object[] __args)
        {
            Object prefab = __args != null && __args.Length > 0 ? __args[0] as Object : null;
            if (!GasSyncPolicy.IsGasolineTrailPrefab(prefab))
                return true;
            if (!GasSyncPolicy.ClientMustNotMutateWorld())
                return true;
            return false;
        }

        private static void Postfix(ref GameObject __result, object[] __args)
        {
            Object prefab = (Object)__args[0];
            Vector3 position = (Vector3)__args[1];
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role != NetworkRole.Host) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;
            if (TraverseHack.GetExplicitFlag()) return;
            if (__result == null || prefab == null) return;
            if (!GasSyncPolicy.IsGasolineTrailPrefab(prefab)) return;

            // Prefer GasTrail channel over ExplosionSpawnObject flood for flammable puddles.
            GasolineTrailSpawnPatch.EnqueueHostTrail(position);
        }
    }

    /// <summary>
    /// Host owns liquid fire. Client only starts burning when applying network ignite
    /// (or bulk). Stops dual waitToBurnNeighbors sims fighting each other (stutter + wild cover).
    /// </summary>
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(Liquid), "startBurning")]
    public static class GasIgnitePatch
    {
        private static bool Prefix(Liquid __instance, out bool __state)
        {
            __state = __instance != null && __instance.burning;

            if (GasSyncPolicy.ClientMustNotMutateWorld())
            {
                // Drop local ignite — host GasIgnite / bulk will light the same puddle.
                return false;
            }
            return true;
        }

        private static void Postfix(Liquid __instance, bool __state)
        {
            if (__state) return;
            if (__instance == null || !__instance.burning) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;
            if (TraverseHack.GetExplicitFlag()) return;

            Vector3 pos = __instance.transform.position;
            net.SendGasIgnite(new GasIgniteMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[GasIgniteSync] host sent ignite at " + pos);
        }
    }
}
