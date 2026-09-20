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
    /// Late-join reuses EntitySpawn (86) — no new message id.
    /// </summary>
    internal static class InfectionSyncHelpers
    {
        internal const string InfectionPrefabPath = "Traps/infection_splat";
        internal const int MaxLateJoinInfectionSpawns = 256;

        internal static bool IsMultiplayerConnected()
        {
            return ModRuntime.Network != null && ModRuntime.Network.IsConnected;
        }

        internal static bool IsHost()
        {
            return IsMultiplayerConnected() && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static EntitySpawnMessage BuildSpawnMessage(Vector3 pos)
        {
            return new EntitySpawnMessage
            {
                PrefabPath = InfectionPrefabPath,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotX = 90f,
                RotY = 0f,
                RotZ = 0f
            };
        }

        internal static void BroadcastInfectionSpawn(Vector3 pos)
        {
            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var msg = BuildSpawnMessage(pos);
            net.Broadcast(NetMessageType.EntitySpawn,
                w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo($"[InfectionSync] spawn at {pos}");
        }

        /// <summary>
        /// Host: push living Infection splats to a joiner via existing EntitySpawn apply path.
        /// Mirrors <c>SendGasStateTo</c> bulk style (cap 256; skip disappearing).
        /// </summary>
        internal static void SendInfectionStatesTo(LanNetworkManager net, int targetPlayerId)
        {
            if (net == null || net.Role != NetworkRole.Host) return;

            Infection[] all = Object.FindObjectsOfType<Infection>(true);
            int sent = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Infection infection = all[i];
                if (infection == null || infection.disappearing) continue;
                if (sent >= MaxLateJoinInfectionSpawns) break;

                Vector3 pos = infection.transform.position;
                var msg = BuildSpawnMessage(pos);
                net.SendBulkOrAll(NetMessageType.EntitySpawn, w => msg.Serialize(w), targetPlayerId);
                sent++;
            }

            ModRuntime.LegacyInfo(targetPlayerId > 0
                ? $"[BulkSync] Sent {sent} infection splat(s) to player {targetPlayerId}"
                : $"[BulkSync] Sent {sent} infection splat(s) to all clients");
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
