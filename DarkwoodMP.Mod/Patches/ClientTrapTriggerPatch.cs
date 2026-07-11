using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client: trap fire → TrapTriggered with TrapNetId so host applies + rebroadcasts.
    /// Host: mint id if needed (switchToTriggered path also broadcasts TrapState).
    /// </summary>
    [HarmonyPatch(typeof(Trigger), "OnAfterTrigger", typeof(Collider), typeof(bool))]
    public static class ClientTrapTriggerPatch
    {
        private static void Prefix(Trigger __instance)
        {
            if (__instance == null) return;
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                return;
            if (net.Role != NetworkRole.Client)
                return;
            if (TraverseHack.ApplyingFromNetwork)
                return;

            Vector3 pos = __instance.transform.position;
            int trapId = TrapNetworkId.GetId(__instance.gameObject);
            // Client may not have an id yet — host will mint on apply.
            net.Send(NetMessageType.TrapTriggered, w => new TrapTriggeredMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                TrapNetId = trapId
            }.Serialize(w), DeliveryMethod.ReliableOrdered);

            ModRuntime.LegacyInfo($"[TrapTrigger] Client sent trap triggered id={trapId} at {pos}");
        }
    }
}
