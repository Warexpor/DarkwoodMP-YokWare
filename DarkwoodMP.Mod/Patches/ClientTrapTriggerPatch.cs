using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// On the client, intercepts Trigger.OnAfterTrigger (called when a trap fires)
    /// and sends a TrapTriggeredMessage to the host so the host's copy of the trap
    /// gets its triggered state updated. Triggered traps are then broadcast back
    /// to both sides by WorldPhysicsSyncService.SyncTraps().
    /// </summary>
    [HarmonyPatch(typeof(Trigger), "OnAfterTrigger", typeof(Collider), typeof(bool))]
    public static class ClientTrapTriggerPatch
    {
        private static void Prefix(Trigger __instance)
        {
            if (__instance == null) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || net.Role != NetworkRole.Client)
                return;

            Vector3 pos = __instance.transform.position;
            net.Send(NetMessageType.TrapTriggered, w => new TrapTriggeredMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo($"[TrapTrigger] Client sent trap triggered at {pos}");
        }
    }
}
