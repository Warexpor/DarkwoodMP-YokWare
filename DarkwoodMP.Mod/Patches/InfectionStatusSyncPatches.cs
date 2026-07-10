using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.10 Infection ground splat + player status flags.
    /// Infection.spread uses Object AddPrefab (not string path) so EntitySpawn never fired;
    /// clients would miss host-side infection growth. Host-only spread + explicit spawn path.
    /// </summary>
    internal static class InfectionSyncHelpers
    {
        internal const string InfectionPrefabPath = "Traps/infection_splat";

        internal static bool IsMultiplayerConnected()
        {
            return ModRuntime.Network != null && ModRuntime.Network.IsConnected;
        }

        internal static bool IsHost()
        {
            return IsMultiplayerConnected() && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static void BroadcastInfectionSpawn(Vector3 pos)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            net.Broadcast(NetMessageType.EntitySpawn,
                w => new EntitySpawnMessage
                {
                    PrefabPath = InfectionPrefabPath,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = 90f,
                    RotY = 0f,
                    RotZ = 0f
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo($"[InfectionSync] spawn at {pos}");
        }
    }

    /// <summary>Clients do not autonomously spread infection — host is authority.</summary>
    [HarmonyPatch(typeof(Infection), "waitToSpread")]
    public static class InfectionWaitToSpreadPatch
    {
        private static bool Prefix(Infection __instance)
        {
            if (!InfectionSyncHelpers.IsMultiplayerConnected())
                return true;
            // Host (or offline) spreads; clients only receive EntitySpawn.
            if (ModRuntime.Network.Role == NetworkRole.Client)
                return false;
            return true;
        }
    }

    /// <summary>
    /// After host spreads a new splat via Object AddPrefab, push string-path EntitySpawn
    /// so clients spawn the same prefab (spread path never hit Core string AddPrefab patch).
    /// </summary>
    [HarmonyPatch(typeof(Infection), "spawnInfection")]
    public static class InfectionSpawnInfectionPatch
    {
        private static void Postfix(Infection __instance, Vector3 position)
        {
            if (__instance == null) return;
            if (!InfectionSyncHelpers.IsHost()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            Vector3 pos = Core.getYPos(position, PosType.low1);
            InfectionSyncHelpers.BroadcastInfectionSpawn(pos);
        }
    }

    /// <summary>
    /// Host infection disappear (fade) — remove matching client splat by position.
    /// </summary>
    [HarmonyPatch(typeof(Infection), "disappear")]
    public static class InfectionDisappearPatch
    {
        private static void Postfix(Infection __instance)
        {
            if (__instance == null) return;
            if (!InfectionSyncHelpers.IsMultiplayerConnected()) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (ModRuntime.Network.Role != NetworkRole.Host) return;

            Vector3 pos = __instance.transform.position;
            var net = LanNetworkManager.Instance;
            if (net == null) return;

            net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                ObjectName = "infection_splat"
            });

            ModRuntime.LegacyInfo($"[InfectionSync] disappear at {pos}");
        }
    }
}
