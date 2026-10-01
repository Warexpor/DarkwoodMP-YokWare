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
    /// <remarks>Applied from <see cref="CoreAddPrefabStringPatch"/> (one detour for all features).</remarks>
    public static class GasolineTrailSpawnPatch
    {
        private static readonly List<Vector3> _pendingTrails = new List<Vector3>(32);
        private static float _nextFlushTime;
        private static bool _flushScheduled;
        private const float FlushInterval = 0.08f;
        private const int MaxBatchSize = 12;

        public static void Reset()
        {
            _pendingTrails.Clear();
            _nextFlushTime = 0f;
            _flushScheduled = false;
        }

        /// <summary>
        /// Client ground pour (<c>Player.waitToSpillLiquid</c>): do not invent host-world
        /// trails here. Ask the host via existing <see cref="NetMessageType.GasTrailSpawn"/>
        /// (Forwardable), and place a local visual so the pourer sees puddles without waiting.
        /// Host scatter / molotov secondaries stay host-owned (Object overload Prefix).
        /// </summary>
        internal static bool AllowSpawn(string prefab, Vector3 position)
        {
            if (!GasSyncPolicy.IsGasolineTrailPath(prefab))
                return true;
            if (!GasSyncPolicy.ClientMustNotMutateWorld())
                return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected)
            {
                net.SendGasTrailSpawn(new GasTrailSpawnMessage
                {
                    PosX = position.x,
                    PosY = position.y,
                    PosZ = position.z
                });
            }

            // Local visual for the pourer (ExplicitFlag so this Prefix does not re-enter).
            // Host + peers place the same trail from GasTrailSpawn / Forwardable.
            Sync.WorldPhysicsSyncService.SpawnGasTrail(position);
            return false;
        }

        internal static void OnAddPrefab(GameObject __result, string prefab, Vector3 position)
        {
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
            var ctrl = Singleton<Controller>.Instance;
            if (now < _nextFlushTime && _pendingTrails.Count < MaxBatchSize && ctrl != null)
            {
                // Rate-limited: the tail of a pour must still go out when no further trail
                // spawns follow, so a timer flushes whatever is pending once the interval passed.
                if (!_flushScheduled)
                {
                    _flushScheduled = true;
                    ctrl.Invoke(FlushOnTimer, Mathf.Max(0.01f, _nextFlushTime - now), timeScaleDependent: false);
                }
                return;
            }
            FlushPendingTrails(now);
        }

        private static void FlushOnTimer()
        {
            _flushScheduled = false;
            FlushPendingTrails(Time.unscaledTime);
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
    /// <remarks>Applied from <see cref="CoreAddPrefabObjectPatch"/> (one detour for all features).</remarks>
    public static class GasolineTrailObjectSpawnPatch
    {
        internal static bool AllowSpawn(Object prefab)
        {
            if (!GasSyncPolicy.IsGasolineTrailPrefab(prefab))
                return true;
            if (!GasSyncPolicy.ClientMustNotMutateWorld())
                return true;
            return false;
        }

        internal static void OnAddPrefab(GameObject __result, Object prefab, Vector3 position)
        {
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
    /// Host owns liquid fire authority. Client must not run waitToBurnNeighbors as a
    /// second sim, but a client torch / flaming melee / Burn trigger that would
    /// startBurning locally must ask the host via existing GasIgnite (Forwardable).
    /// </summary>
    [HarmonyPatch(typeof(Liquid), "startBurning")]
    public static class GasIgnitePatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(Liquid __instance, out bool __state)
        {
            __state = __instance != null && __instance.burning;

            if (!GasSyncPolicy.ClientMustNotMutateWorld())
                return true;
            if (__instance == null || __instance.burning)
                return false;

            Vector3 pos = __instance.transform.position;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected)
            {
                net.SendGasIgnite(new GasIgniteMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z
                });
            }

            // Local visual for the igniter (ExplicitFlag so this Prefix does not re-enter).
            // Host + peers light the same puddle from GasIgnite / Forwardable.
            Sync.WorldPhysicsSyncService.IgniteGasAtPos(pos);
            return false;
        }

        [HarmonyPriority(Priority.First)]
        private static void Postfix(Liquid __instance, bool __state)
        {
            if (__state) return;
            if (__instance == null || !__instance.burning) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected) return;

            // Host owns fire spread: it reports every puddle that ignites, so a client puddle
            // lit from the network must not also spread to neighbours on its own timer
            // (vanilla startBurning schedules waitToBurnNeighbors).
            if (net.Role == NetworkRole.Client)
            {
                __instance.stopRoutine("waitToBurnNeighbors");
                return;
            }
            if (net.Role != NetworkRole.Host) return;
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
