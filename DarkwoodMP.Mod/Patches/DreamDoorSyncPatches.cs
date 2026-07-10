using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Syncs Door.open() from host to client during dreams. Doors don't have Character
    /// components so they aren't covered by EntityStateBroadcastService. During dreams
    /// the broadcast is paused anyway, so this patch ensures the door state is sent.
    /// </summary>
    [HarmonyPatch(typeof(Door), "open")]
    public static class DoorOpenSyncPatch
    {
        private static void Postfix(Door __instance)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            // Bidirectional sync during active dreams — both host and client
            // can open doors through dialogue, and the other side needs to see it.
            if (!DreamSyncManager.IsDreamActive) return;

            Vector3 pos = __instance.transform.position;

            // Broadcast: host → all clients; client → host (then host open rebroadcasts).
            // Send() only hits the first peer — breaks 3+ when host opens a dream door.
            net.Broadcast(NetMessageType.DoorOpen,
                w => new DoorOpenMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    DoorName = __instance.name
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo($"[DoorSync] sent door open: {__instance.name} at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) role={net.Role}");
        }
    }
}
