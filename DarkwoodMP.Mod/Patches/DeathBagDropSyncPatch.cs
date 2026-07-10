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
    /// After the death bag is dropped locally, sends a DeathBagSpawnMessage
    /// to the remote peer so it also spawns the bag with matching contents.
    /// </summary>
    [HarmonyPatch(typeof(Player), "dropBody", new[] { typeof(Vector3) })]
    public static class DeathBagDropSyncPatch
    {
        private static void Postfix(Player __instance, object[] __args)
        {
            Vector3 destPos = (Vector3)__args[0];

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            // Don't sync death bags during dream deaths — player keeps their inventory
            if (Sync.FinalDreamsceneManager.IsActive)
                return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return;

            DeathDrop deathDrop = FindDeathDropAt(destPos);
            if (deathDrop == null)
            {
                ModRuntime.Log?.LogWarning("[Death] dropBody Postfix: could not find DeathDrop at " + destPos);
                return;
            }

            Inventory bagInv = deathDrop.GetComponent<Inventory>();
            if (bagInv == null) return;

            bagInv.removeWhenEmpty = true;

            bool inWater = __instance.inWater;
            string bagId = DeathBagNetworkId.GetOrAssignBagId(deathDrop.gameObject, inWater);
            net.RegisterDeathBag(bagId, deathDrop);

            var types = new List<string>();
            var amounts = new List<int>();
            var durabilities = new List<float>();
            var ammos = new List<int>();

            foreach (InvSlot slot in bagInv.slots)
            {
                if (!InvItemClass.isNull(slot.invItem))
                {
                    types.Add(slot.invItem.type);
                    amounts.Add(slot.invItem.amount);
                    durabilities.Add(slot.invItem.durability);
                    ammos.Add(slot.invItem.ammo);
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
                BagId = bagId
            };

            net.Broadcast(NetMessageType.DeathBagSpawn, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);
        }

        private static DeathDrop FindDeathDropAt(Vector3 pos)
        {
            // Use XZ-only distance because dropBody() adjusts Y via
            // Core.getYPos(destPos, PosType.items2), so the spawned bag
            // is at ground level while destPos retains the player's Y.
            Vector2 posXZ = new Vector2(pos.x, pos.z);

            GameObject container = Core.ItemContainer;
            if (container != null)
            {
                foreach (Transform child in container.transform)
                {
                    DeathDrop dd = child.GetComponent<DeathDrop>();
                    if (dd != null)
                    {
                        Vector2 childXZ = new Vector2(child.position.x, child.position.z);
                        if (Vector2.Distance(childXZ, posXZ) < 2f)
                            return dd;
                    }
                }
            }

            DeathDrop[] all = GameObject.FindObjectsOfType<DeathDrop>(true);
            DeathDrop best = null;
            float bestDist = 15f;
            for (int i = 0; i < all.Length; i++)
            {
                DeathDrop dd = all[i];
                if (dd == null) continue;
                Vector2 ddXZ = new Vector2(dd.transform.position.x, dd.transform.position.z);
                float d = Vector2.Distance(ddXZ, posXZ);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = dd;
                }
            }
            if (best != null)
                ModRuntime.LegacyInfo($"[Death] FindDeathDropAt: found via fallback scan ({bestDist:F1}m), container was {(container != null ? "OK" : "NULL")}");
            else
                ModRuntime.LegacyInfo($"[Death] FindDeathDropAt: no DeathDrop within 15m of {pos}");

            return best;
        }
    }
}
