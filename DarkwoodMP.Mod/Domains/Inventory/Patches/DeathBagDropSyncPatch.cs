using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Postfix on Player.dropBody().
    /// Captures the bag created by vanilla and adds the night body's remaining gear before syncing it.
    /// </summary>
    [HarmonyPatch(typeof(Player), "dropBody", new[] { typeof(Vector3) })]
    public static class DeathBagDropSyncPatch
    {
        private static void Prefix(out int __state)
        {
            __state = NetGuard.Connected(out _) && Core.ItemContainer != null
                ? Core.ItemContainer.transform.childCount : -1;
        }

        private static void Postfix(Player __instance, int __state, bool __runOriginal)
        {
            if (!__runOriginal || __state < 0 || !NetGuard.Connected(out var net)
                || Core.ItemContainer == null || Core.ItemContainer.transform.childCount <= __state)
                return;

            // Vanilla appends the bag to ItemContainer before filling it and creating the map marker.
            DeathDrop deathDrop = Core.ItemContainer.transform.GetChild(__state).GetComponent<DeathDrop>();
            if (deathDrop == null)
                return;

            if (NightBodyBag.Applies())
            {
                NightBodyBag.Dropped = true;
                Players.PlayerCorpses.ClaimNewBag(deathDrop);
                NightBodyBag.MoveRest(__instance, deathDrop);
            }

            if (LanNetworkManager.IsApplyingRemoteState || Sync.FinalDreamsceneManager.IsActive)
                return;
            bool inWater = __instance.inWater;
            string bagId = DeathBagNetworkId.GetOrAssignBagId(deathDrop.gameObject, inWater);
            net.RegisterDeathBag(bagId, deathDrop);
            net.StartCoroutine(SendNextFrame(net, deathDrop, bagId, inWater));
        }

        private static System.Collections.IEnumerator SendNextFrame(
            LanNetworkManager net, DeathDrop deathDrop, string bagId, bool inWater)
        {
            yield return null;
            if (net == null || !net.IsConnected || deathDrop == null || net.IsDeathBagLooted(bagId))
                yield break;

            Inventory bagInv = deathDrop.GetComponent<Inventory>();
            if (bagInv == null) yield break;

            bagInv.removeWhenEmpty = true;

            var types = new List<string>();
            var amounts = new List<int>();
            var durabilities = new List<float>();
            var ammos = new List<int>();
            var recipes = new List<bool>();
            var upgrades = new List<string[]>();
            var actives = new List<bool>();

            foreach (InvSlot slot in bagInv.slots)
            {
                if (!InvItemClass.isNull(slot.invItem))
                {
                    // recipes share type "recipe" — wire craftable + IsRecipe.
                    bool isRecipe = slot.invItem.isRecipe;
                    types.Add(isRecipe ? slot.invItem.recipeFor : slot.invItem.type);
                    amounts.Add(slot.invItem.amount);
                    durabilities.Add(slot.invItem.durability);
                    ammos.Add(slot.invItem.ammo);
                    recipes.Add(isRecipe);
                    upgrades.Add(Sync.InvItemUpgradeWire.CollectNames(slot.invItem));
                    actives.Add(slot.invItem.shouldBeActive);
                }
            }

            // Prefer ground-snapped transform pos so remote Y matches after getYPos.
            Vector3 bagPos = deathDrop.transform.position;

            ModRuntime.LegacyInfo($"[Death] Syncing death bag id={bagId} at {bagPos} with {types.Count} items");

            var msg = new DeathBagSpawnMessage
            {
                PosX = bagPos.x,
                PosY = bagPos.y,
                PosZ = bagPos.z,
                InWater = inWater,
                ExpAmount = deathDrop.expAmount,
                ItemCount = types.Count,
                ItemTypes = types.ToArray(),
                ItemAmounts = amounts.ToArray(),
                ItemDurabilities = durabilities.ToArray(),
                ItemAmmos = ammos.ToArray(),
                BagId = bagId,
                IsRecipe = recipes.ToArray(),
                ItemUpgrades = upgrades.ToArray(),
                ShouldBeActive = actives.ToArray(),
                HasMarker = deathDrop.additionalMapMarker != null,
                MarkerX = deathDrop.additionalMapMarker != null ? deathDrop.additionalMapMarker.transform.position.x : 0f,
                MarkerY = deathDrop.additionalMapMarker != null ? deathDrop.additionalMapMarker.transform.position.y : 0f,
                MarkerZ = deathDrop.additionalMapMarker != null ? deathDrop.additionalMapMarker.transform.position.z : 0f
            };

            net.Broadcast(NetMessageType.DeathBagSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

    }
}
