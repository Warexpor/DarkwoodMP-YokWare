using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Syncs inventory-item drops and pickups between host and client.
    /// Each dropped item gets a GUID so that when either player picks it up
    /// the remote copy is destroyed too.
    /// </summary>
    internal static class DroppedItemSyncHelpers
    {
        internal static void SendDrop(Transform spawned, InvItemClass item, string prefabPath)
        {
            if (spawned == null) { ModRuntime.LegacyInfo("[SendDrop] spawned is null"); return; }
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) { ModRuntime.LegacyInfo("[SendDrop] net not connected"); return; }
            if (LanNetworkManager.IsApplyingRemoteState) { ModRuntime.LegacyInfo("[SendDrop] applying remote state"); return; }

            string guid = System.Guid.NewGuid().ToString("N");
            ModRuntime.LegacyInfo("[SendDrop] adding identifier guid=" + guid + " to " + spawned.name);

            var ident = spawned.gameObject.AddComponent<DroppedItemIdentifier>();
            ident.Id = guid;
            DroppedItemIdentifier.Register(ident);

            Vector3 pos = spawned.position;
            Vector3 euler = spawned.eulerAngles;

            int amt = item.amount;
            float dur = item.durability;
            int ammo = 0;
            if (item.baseClass != null && item.baseClass.hasAmmo)
                ammo = item.ammo;

            net.SendDroppedItemSpawn(new DroppedItemSpawnMessage
            {
                Guid = guid,
                PrefabPath = prefabPath,
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                RotX = euler.x,
                RotY = euler.y,
                RotZ = euler.z,
                ItemType = item.type,
                Amount = amt,
                Durability = dur,
                Ammo = ammo
            });
        }

        internal static void SendPickup(Item worldItem)
        {
            ModRuntime.LegacyInfo("[SendPickup] called for " + (worldItem != null ? worldItem.name : "null"));

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            // GUID-based dropped item: send pickup by GUID
            var ident = worldItem != null ? worldItem.GetComponent<DroppedItemIdentifier>() : null;
            if (ident != null && !string.IsNullOrEmpty(ident.Id))
            {
                ModRuntime.LegacyInfo("[SendPickup] sending pickup for guid=" + ident.Id);
                net.SendDroppedItemPickup(new DroppedItemPickupMessage { Guid = ident.Id });
                return;
            }

            // World-placed item (no GUID): broadcast WorldObjectRemoved so the
            // remote peer destroys their copy too.
            if (worldItem != null)
            {
                Vector3 pos = worldItem.transform.position;
                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ObjectName = worldItem.name
                });
                ModRuntime.LegacyInfo("[SendPickup] sent WorldObjectRemoved for " + worldItem.name + " at " + pos);
            }
        }

        internal static InvItemClass GetItemFromSpawned(Transform t)
        {
            Inventory inv = t.GetComponent<Inventory>();
            if (inv == null || inv.slots == null || inv.slots.Count == 0) return null;
            InvItemClass item = inv.slots[0].invItem;
            if (InvItemClass.isNull(item)) return null;
            return item;
        }
    }

    /// <summary>
    /// Intercepts Inventory-slot drop — Player.spawnDroppedInvItem(InvItemClass).
    /// Adds a GUID and sends a spawn message to the remote peer.
    /// </summary>
    [HarmonyPatch(typeof(Player), "spawnDroppedInvItem", typeof(InvItemClass))]
    public static class PlayerDropItemPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance, Transform __result)
        {
            if (__result == null) return;
            InvItemClass item = DroppedItemSyncHelpers.GetItemFromSpawned(__result);
            if (item == null) return;
            string prefab = __instance.inWater ? "Items/DroppedItem_water" : "Items/DroppedItem";
            DroppedItemSyncHelpers.SendDrop(__result, item, prefab);
        }
    }

    /// <summary>
    /// Intercepts the alternate drop path — Player.spawnDroppedInvItemm(bool, string, int).
    /// </summary>
    [HarmonyPatch(typeof(Player), "spawnDroppedInvItemm", typeof(bool), typeof(string), typeof(int))]
    public static class PlayerDropItemmPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player __instance, Transform __result)
        {
            if (__result == null) return;
            InvItemClass item = DroppedItemSyncHelpers.GetItemFromSpawned(__result);
            if (item == null) return;
            string prefab = __instance.inWater ? "Items/DroppedItem_water" : "Items/DroppedItem";
            DroppedItemSyncHelpers.SendDrop(__result, item, prefab);
        }
    }

    /// <summary>
    /// When a player picks up a networked dropped item, notify the remote peer
    /// to destroy its copy.
    /// </summary>
    [HarmonyPatch(typeof(Item), "getDroppedItem")]
    public static class PlayerPickupDroppedItemPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Item __instance)
        {
            ModRuntime.LegacyInfo("[PickupPrefix] getDroppedItem called on " + __instance.name);

            // Block pickup when a remote player is trapped in this bear trap
            if (ModRuntime.Network is LanNetworkManager net && net.HasAnyTrappedPlayer)
            {
                string name = __instance.name.ToLowerInvariant();
                if (TrapNameHelper.IsTrap(name))
                {
                    ModRuntime.LegacyInfo("[PickupPrefix] blocked pickup of \""
                        + __instance.name + "\" — remote player still trapped");
                    return false;
                }
            }

            // Already taken by peer (or us) — destroy ghost, do not grant item again.
            var ident = __instance.GetComponent<DroppedItemIdentifier>();
            if (ident != null && !string.IsNullOrEmpty(ident.Id)
                && LanNetworkManager.IsDropGuidConsumed(ident.Id))
            {
                ModRuntime.LegacyInfo("[PickupPrefix] guid already consumed: " + ident.Id);
                UnityEngine.Object.Destroy(__instance.gameObject);
                return false;
            }

            DroppedItemSyncHelpers.SendPickup(__instance);
            return true;
        }
    }

    /// <summary>
    /// Helper for identifying trap GameObjects by name.
    /// </summary>
    internal static class TrapNameHelper
    {
        public static bool IsTrap(string name)
        {
            return name.Contains("trap") || name.Contains("bear") || name.Contains("snap") || name.Contains("animal");
        }
    }
}
