using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Hooks Inventory.hide() to detect when a death bag is emptied (all items taken).
    /// Sends a DeathBagLooted message so all peers destroy their local copy.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "hide")]
    public static class DeathBagLootPatch
    {
        private static void Prefix(Inventory __instance)
        {
            if (__instance.GetComponent<DeathDrop>() == null) return;
            if (__instance.getAllItems().Count != 0) return;
            if (!__instance.removeWhenEmpty) return;

            // Remote destroy/hide must not re-broadcast Looted (echo loop).
            if (LanNetworkManager.IsApplyingRemoteState) return;

            Vector3 pos = __instance.transform.position;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            string bagId = DeathBagNetworkId.GetBagId(__instance.gameObject);
            if (string.IsNullOrEmpty(bagId))
                bagId = DeathBagNetworkId.GetOrAssignBagId(__instance.gameObject);

            // Already marked looted (duplicate hide / retransmit) — skip send.
            if (!string.IsNullOrEmpty(bagId) && net.IsDeathBagLooted(bagId))
                return;

            // Local registry: treat as gone so late-join / retransmit cannot re-spawn
            if (!string.IsNullOrEmpty(bagId))
            {
                net.RegisterDeathBagLooted(bagId);
                net.UnregisterDeathBag(bagId);
            }

            net.Broadcast(NetMessageType.DeathBagLooted,
                w => new DeathBagLootedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    BagId = bagId
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
        }
    }
}
