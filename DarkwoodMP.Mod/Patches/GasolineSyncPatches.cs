using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Intercepts GasolineTrail spawns via Core.AddPrefab(string) and relays the
    /// position to the remote peer so both sides have matching gasoline puddles.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(Core), "AddPrefab", typeof(string), typeof(Vector3), typeof(Quaternion), typeof(GameObject), typeof(bool))]
    public static class GasolineTrailSpawnPatch
    {
        private static readonly List<Vector3> _pendingTrails = new List<Vector3>(32);
        private static float _nextFlushTime;
        private const float FlushInterval = 0.05f;
        private const int MaxBatchSize = 16;

        public static void Reset()
        {
            _pendingTrails.Clear();
            _nextFlushTime = 0f;
        }

        private static void Postfix(ref GameObject __result, object[] __args)
        {
            string prefab = (string)__args[0];
            Vector3 position = (Vector3)__args[1];
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected) return;
            if (ModRuntime.Network.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;
            if (__result == null) return;
            if (prefab != "Items/GasolineTrail") return;

            _pendingTrails.Add(position);

            float now = Time.unscaledTime;
            if (now < _nextFlushTime && _pendingTrails.Count < MaxBatchSize)
                return;

            FlushPendingTrails(now);
        }

        private static void FlushPendingTrails(float now)
        {
            if (_pendingTrails.Count == 0) return;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
            {
                _pendingTrails.Clear();
                return;
            }

            _nextFlushTime = now + FlushInterval;
            for (int i = 0; i < _pendingTrails.Count; i++)
            {
                Vector3 p = _pendingTrails[i];
                ModRuntime.Network.SendGasTrailSpawn(new GasTrailSpawnMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z
                });
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[GasTrailSync] flushed " + _pendingTrails.Count + " trails");

            _pendingTrails.Clear();
        }
    }

    /// <summary>
    /// When a Liquid (gasoline puddle) starts burning on either peer, relays the
    /// ignition position to the other side so both see the fire.
    /// Suppressed during ApplyingFromNetwork to avoid infinite relay loops.
    /// </summary>
    [HarmonyPatch(typeof(Liquid), "startBurning")]
    public static class GasIgnitePatch
    {
        [HarmonyPrefix]
        private static void Prefix(Liquid __instance, out bool __state)
        {
            __state = __instance.burning;
        }

        private static void Postfix(Liquid __instance, bool __state)
        {
            if (__state) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork || LanNetworkManager.IsApplyingRemoteState) return;

            Vector3 pos = __instance.transform.position;
            net.SendGasIgnite(new GasIgniteMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            });
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[GasIgniteSync] sent ignite at " + pos);
        }
    }
}