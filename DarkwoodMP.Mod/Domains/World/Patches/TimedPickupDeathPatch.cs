using DWMPHorde.Players;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A pickup that lives for a while and vanishes (vanilla <c>WaitAndDie</c>: the piskle nest on
    /// the map) ran out on each machine's own clock: the host's nest was gone minutes before a
    /// client's ("DESYNC Pickups nest_piskle_01 ... host=&lt;none&gt;"), so the client saw an egg the
    /// host no longer had (or, the other way round, lost one the host still offered). The host's
    /// clock decides: its pickup's end is sent as a removal, and a client's copy waits for it.
    /// </summary>
    [HarmonyPatch(typeof(WaitAndDie), nameof(WaitAndDie.onDeath))]
    public static class TimedPickupDeathPatch
    {
        private static bool Prefix(WaitAndDie __instance)
        {
            if (__instance == null || !NetGuard.Connected(out LanNetworkManager net))
                return true;
            if (net.Role == NetworkRole.Client)
                // Still holding a pickup: the host's end of it removes the pickup, then this ends.
                return LanNetworkManager.IsApplyingRemoteState || FirstPickup(__instance.gameObject) == null;
            foreach (Item it in __instance.GetComponentsInChildren<Item>())
            {
                if (it == null || !it.isDroppedItem || it.GetComponent<DroppedItemIdentifier>() != null)
                    continue;
                Vector3 p = it.transform.position;
                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
                {
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    ObjectName = it.gameObject.name,
                    Mode = WorldObjectRemovedMessage.ModeRemove
                });
                ModRuntime.LegacyInfo($"[TimedPickup] {it.gameObject.name} at {p} ran out → removed for peers");
            }
            return true;
        }

        private static Item FirstPickup(GameObject root)
        {
            foreach (Item it in root.GetComponentsInChildren<Item>())
                if (it != null && it.isDroppedItem && it.GetComponent<DroppedItemIdentifier>() == null)
                    return it;
            return null;
        }
    }
}
