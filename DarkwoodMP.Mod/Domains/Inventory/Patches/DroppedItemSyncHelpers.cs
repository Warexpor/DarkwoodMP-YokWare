using System.Linq;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using DWMPHorde.Players;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
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
            // 0.8.63: recipes share type "recipe" — wire craftable + IsRecipe (trade/container parity).
            bool isRecipe = item.isRecipe;
            string wireType = isRecipe ? item.recipeFor : item.type;

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
                ItemType = wireType,
                Amount = amt,
                Durability = dur,
                Ammo = ammo,
                IsRecipe = isRecipe,
                Upgrades = Sync.InvItemUpgradeWire.CollectNames(item),
                ShouldBeActive = item.shouldBeActive
            });
        }

        internal static void SendPickup(Item worldItem)
        {
            ModRuntime.LegacyInfo("[SendPickup] called for " + (worldItem != null ? worldItem.name : "null"));

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;

            // GUID drops use FinishGuidPickupClaim (host-auth) from Postfix.
            var ident = worldItem != null ? worldItem.GetComponent<DroppedItemIdentifier>() : null;
            if (ident != null && !string.IsNullOrEmpty(ident.Id))
                return;

            // Trap rescue / non-claim world remove: broadcast WorldObjectRemoved.
            // Non-trap world uniques use FinishWorldPickupClaim (host-auth) instead.
            if (worldItem != null)
            {
                ResolveWorldPickupClaim(worldItem, out Vector3 pos, out string sendName, out bool isTrap);
                if (!isTrap)
                    return; // caller should use FinishWorldPickupClaim

                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ObjectName = sendName,
                    Mode = WorldObjectRemovedMessage.ModeRemove
                });
                ModRuntime.LegacyInfo("[SendPickup] sent WorldObjectRemoved for " + sendName + " at " + pos
                    + " (trap rescue)");
            }
        }

        /// <summary>
        /// After a successful GUID drop pickup: host-auth claim (mirror FinishWorldPickupClaim).
        /// Host TryConsume + fan Remove; client optimistic + ClaimRequest (deny refunds).
        /// </summary>
        internal static void FinishGuidPickupClaim(
            string guid, string itemType, int amount, float durability, int ammo, int preCount)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (string.IsNullOrEmpty(guid)) return;

            // Lost to an inbound claim/remove that already consumed on this machine.
            if (!WorldObjectSendNetHandlers.TryConsumeDropGuid(guid))
            {
                WorldPickupClaimPending.Refund(itemType, amount, preCount, "guid local consume lost");
                return;
            }

            if (net.Role == NetworkRole.Host)
            {
                var remove = new DroppedItemPickupMessage
                {
                    Guid = guid,
                    Mode = DroppedItemPickupMessage.ModeRemove,
                    ClaimedByPlayerId = net.LocalPlayerId,
                    ItemType = itemType ?? "",
                    Amount = amount,
                    Durability = durability,
                    Ammo = ammo
                };
                net.SendDroppedItemPickup(remove);
                ModRuntime.LegacyInfo("[GuidPickup] host claimed guid=" + guid + " type=" + itemType);
                return;
            }

            WorldPickupClaimPending.RecordGuid(guid, itemType, amount, preCount);
            var claim = new DroppedItemPickupMessage
            {
                Guid = guid,
                Mode = DroppedItemPickupMessage.ModeClaimRequest,
                ClaimedByPlayerId = net.LocalPlayerId,
                ItemType = itemType ?? "",
                Amount = amount,
                Durability = durability,
                Ammo = ammo
            };
            // Client→host only (Forwardable would fan ClaimRequest — host owns grant).
            net.Send(NetMessageType.DroppedItemPickup, w => claim.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[GuidPickup] client ClaimRequest guid=" + guid
                + " type=" + itemType + " x" + amount);
        }

        /// <summary>
        /// After a successful non-GUID world pickup: host-auth claim.
        /// Host TryConsume + fan Remove; client optimistic + ClaimRequest (deny refunds).
        /// </summary>
        internal static void FinishWorldPickupClaim(
            Vector3 pos, string sendName, string itemType, int amount, float durability, int ammo, int preCount)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (string.IsNullOrEmpty(sendName)) return;

            // Lost to an inbound claim/remove that already consumed on this machine.
            if (!WorldPhysicsSyncService.TryConsumeWorldPickup(pos.x, pos.y, pos.z, sendName))
            {
                WorldPickupClaimPending.Refund(itemType, amount, preCount, "local consume lost");
                return;
            }

            if (net.Role == NetworkRole.Host)
            {
                net.SendWorldObjectRemoved(new WorldObjectRemovedMessage
                {
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    ObjectName = sendName,
                    Mode = WorldObjectRemovedMessage.ModeRemove,
                    ClaimedByPlayerId = net.LocalPlayerId,
                    ItemType = itemType ?? "",
                    Amount = amount,
                    Durability = durability,
                    Ammo = ammo
                });
                ModRuntime.LegacyInfo("[WorldPickup] host claimed " + sendName + " at " + pos);
                return;
            }

            // Client: keep optimistic grant; host decides. Pending enables deny refund.
            WorldPickupClaimPending.Record(pos.x, pos.y, pos.z, sendName, itemType, amount, preCount);
            var claim = new WorldObjectRemovedMessage
            {
                PosX = pos.x,
                PosY = pos.y,
                PosZ = pos.z,
                ObjectName = sendName,
                Mode = WorldObjectRemovedMessage.ModeClaimRequest,
                ClaimedByPlayerId = net.LocalPlayerId,
                ItemType = itemType ?? "",
                Amount = amount,
                Durability = durability,
                Ammo = ammo
            };
            net.Send(NetMessageType.WorldObjectRemoved, w => claim.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo("[WorldPickup] client ClaimRequest " + sendName + " at " + pos
                + " type=" + itemType + " x" + amount);
        }

        /// <summary>
        /// Slot InvItemClass meta before getDroppedItem transfer empties it.
        /// (Item.invItem is the InvItem template MonoBehaviour — not the instance.)
        /// </summary>
        internal static void CaptureWorldPickupItemMeta(Item worldItem,
            out string itemType, out int amount, out float durability, out int ammo)
        {
            itemType = "";
            amount = 0;
            durability = 0f;
            ammo = 0;
            if (worldItem == null) return;

            Inventory bag = worldItem.GetComponent<Inventory>();
            if (bag == null || bag.slots == null || bag.slots.Count == 0)
                return;
            InvItemClass inv = bag.slots[0].invItem;
            if (InvItemClass.isNull(inv))
                return;
            itemType = inv.type ?? "";
            amount = inv.amount > 0 ? inv.amount : 1;
            durability = inv.durability;
            if (inv.baseClass != null && inv.baseClass.hasAmmo)
                ammo = inv.ammo;
        }

        /// <summary>Pose + wire name for non-GUID world pickup claim / WorldObjectRemoved.</summary>
        internal static void ResolveWorldPickupClaim(Item worldItem, out Vector3 pos, out string sendName, out bool isTrap)
        {
            pos = worldItem.transform.position;
            sendName = worldItem.name;
            GameObject go = worldItem.gameObject;
            isTrap = TrapNetworkId.IsWorldTrap(go) || TrapNetworkId.IsOccupancyTrap(go)
                || TrapNameHelper.IsTrap(sendName != null ? sendName.ToLowerInvariant() : "");

            // Sprung beartrap isDroppedItem keeps slot type "junk" / "Scrap metal".
            // Always send the trap GO name so peer DestroyObjectByPos frees + matches
            // the trap — never a junk remove that used to eat the trap without a free.
            if (!isTrap)
            {
                Item asItem = worldItem.GetComponent<Item>();
                if (asItem != null && asItem.invItem != null
                    && !string.IsNullOrEmpty(asItem.invItem.type))
                    sendName = asItem.invItem.type;
                else
                {
                    Inventory inv = worldItem.GetComponent<Inventory>();
                    if (inv != null && inv.slots != null && inv.slots.Count > 0
                        && !InvItemClass.isNull(inv.slots[0].invItem)
                        && !string.IsNullOrEmpty(inv.slots[0].invItem.type))
                        sendName = inv.slots[0].invItem.type;
                }
            }
            else if (string.IsNullOrEmpty(sendName) || sendName.IndexOf("scrap", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // refreshName may have rewritten Item.name to the loot display string.
                sendName = go.name;
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
}
