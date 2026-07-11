using DWMPHorde.Config;
using System.Collections.Generic;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Co-op loot share: multiplies hideout fuels and barricade mats on pickup into personal inventory.
    /// Does not multiply uniques, player-placed stacks, or ground drops with DroppedItemIdentifier.
    /// Scale: <see cref="CoopBalance.GetPartyMultiplier"/>.
    /// </summary>
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch]
    public static class ItemDoublePickupPatch
    {
        private static readonly HashSet<string> PlayerPlacedContainerKeys = new HashSet<string>();
        private static bool _disarmInProgress;

        public static void Reset()
        {
            PlayerPlacedContainerKeys.Clear();
            _disarmInProgress = false;
        }

        /// <summary>Hideout fuels + barricade mats (policy A). Types also live in CoopBalance.</summary>
        private static bool IsScaledLootType(string type)
        {
            return CoopBalance.IsScaledLootType(type);
        }

        private static bool IsExpItem(InvItem invItem)
        {
            return invItem != null && (invItem.isExpItem || IsScaledLootType(invItem.type));
        }

        private static bool IsExpItemClass(InvItemClass invItemClass)
        {
            return invItemClass != null && invItemClass.baseClass != null &&
                (invItemClass.baseClass.isExpItem || IsScaledLootType(invItemClass.type));
        }

        private static string MakeContainerKey(Vector3 pos, int slotIdx)
        {
            return $"{pos.x:F2}:{pos.y:F2}:{pos.z:F2}:{slotIdx}";
        }

        public static void MarkContainerSlotPlayerPlaced(Vector3 pos, int slotIdx)
        {
            PlayerPlacedContainerKeys.Add(MakeContainerKey(pos, slotIdx));
        }

        private static int GetItemMultiplier()
        {
            return CoopBalance.GetPartyMultiplier();
        }

        private static bool IsPlayerPlacedSlot(InvSlot slot)
        {
            if (slot?.inventory == null) return false;
            if (slot.inventory.invType != Inventory.InvType.itemInv) return false;
            int idx = slot.inventory.slots.IndexOf(slot);
            if (idx < 0) return false;
            return PlayerPlacedContainerKeys.Contains(MakeContainerKey(slot.inventory.transform.position, idx));
        }

        private static void Log(string msg)
        {
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[ItemDouble] {msg}");
        }

        [HarmonyPatch(typeof(ItemsDatabase), "Awake")]
        [HarmonyPostfix]
        private static void DumpItemTypes()
        {
            var db = Singleton<ItemsDatabase>.Instance;
            if (db == null) return;
            Log("=== Upgrade-relevant item dump ===");
            foreach (var kvp in db.itemsDict)
            {
                InvItem prefab = db.getItem(kvp.Key, instantiate: false);
                if (prefab == null) continue;
                bool mutatedCat = prefab.categories != null && prefab.categories.Contains(InvItem.Category.mutated);
                bool isUpgrade = prefab.isExpItem || mutatedCat;
                bool isDefense = IsScaledLootType(prefab.type);
                if (isUpgrade || isDefense)
                {
                    string cats = prefab.categories != null ? string.Join(",", prefab.categories) : "null";
                    Log($"  type='{prefab.type}', isExpItem={prefab.isExpItem}, scaledLoot={isDefense}, categories=[{cats}]");
                }
            }
            Log("=== End dump ===");
        }

        [HarmonyPatch(typeof(Inventory), "addItemTypeToPlayer")]
        [HarmonyPrefix]
        private static void OnAddItemTypeToPlayer(Inventory __instance, string type, int amount, bool dropIfNoRoom)
        {
            Log($"addItemTypeToPlayer called: type='{type}', amount={amount}, disarmFlag={_disarmInProgress}");
        }

        [HarmonyPatch(typeof(Inventory), "addItemTypeToPlayer")]
        [HarmonyPrefix]
        private static void OnAddItemTypeToPlayer_DoDouble(string type, ref int amount)
        {
            if (Config.ModConfig.GetLootShareMode() == Config.LootShareMode.Off) return;
            if (!_disarmInProgress)
                return;
            _disarmInProgress = false;
            int mult = GetItemMultiplier();
            Log($"  --> DOUBLING from {amount} to {amount * mult}");
            amount *= mult;
        }

        [HarmonyPatch(typeof(Item), "disarm")]
        [HarmonyPrefix]
        private static void OnDisarm(Item __instance)
        {
            if (__instance.invItem == null)
            {
                Log("OnDisarm: invItem is NULL");
                return;
            }
            Log($"OnDisarm: type='{__instance.invItem.type}', isExpItem={__instance.invItem.isExpItem}, invItemAmount={__instance.invItemAmount}");
            if (!IsExpItem(__instance.invItem))
            {
                Log("  --> NOT an exp item (base class), skipping");
                return;
            }
            Log("  --> WILL double via addItemTypeToPlayer");
            _disarmInProgress = true;
        }

        /// <summary>
        /// Pending personal-only loot share. Never mutate container stack amount —
        /// that desynced RemoveItem (world removes real count) and re-doubled on
        /// re-take after place (logs: nails 11→22→44).
        /// </summary>
        private struct PendingShare
        {
            public bool Active;
            public string Type;
            public int BaseAmount;
        }

        private static PendingShare _pendingShare;

        private static bool TryArmShare(InvSlot slot, string path, out PendingShare share)
        {
            share = default;
            if (Config.ModConfig.GetLootShareMode() == Config.LootShareMode.Off)
                return false;
            if (InvItemClass.isNull(slot?.invItem))
            {
                Log(path + ": invItem is null");
                return false;
            }
            Log($"{path}: type='{slot.invItem.type}', isExpItem={slot.invItem.baseClass?.isExpItem}, amount={slot.invItem.amount}");
            if (!IsExpItemClass(slot.invItem))
            {
                Log("  --> NOT scaled loot, skipping");
                return false;
            }
            if (slot.inventory != null && slot.inventory.gameObject.GetComponent<DroppedItemIdentifier>() != null)
            {
                Log("  --> blocked by DroppedItemIdentifier");
                return false;
            }
            if (IsPlayerPlacedSlot(slot))
            {
                Log("  --> blocked by PlayerPlacedSlot");
                return false;
            }
            // Don't share when reorganizing own bag / cursor.
            if (slot.inventory != null && Player.Instance != null
                && slot.inventory == Player.Instance.Inventory)
            {
                Log("  --> blocked: player's own inventory");
                return false;
            }
            int mult = GetItemMultiplier();
            if (mult <= 1)
            {
                Log("  --> mult=1, skip");
                return false;
            }
            share = new PendingShare
            {
                Active = true,
                Type = slot.invItem.type,
                BaseAmount = slot.invItem.amount
            };
            Log($"  --> will add personal extra x{mult - 1} of {share.BaseAmount} (container amount unchanged)");
            return true;
        }

        private static void ApplyPendingShare(ref PendingShare share, string path)
        {
            if (!share.Active) return;
            string type = share.Type;
            int baseAmt = share.BaseAmount;
            share = default;
            if (string.IsNullOrEmpty(type) || baseAmt <= 0) return;
            Player player = Player.Instance;
            if (player?.Inventory == null) return;
            int extra = baseAmt * (GetItemMultiplier() - 1);
            if (extra <= 0) return;
            Log($"{path}: adding {extra} personal extra of '{type}' (base was {baseAmt})");
            player.Inventory.addItemType(type, extra);
        }

        [HarmonyPatch(typeof(InvSlot), "transferItemAllToPlayer")]
        [HarmonyPrefix]
        private static void OnTransferAllToPlayerPrefix(InvSlot __instance)
        {
            // Never multiply the container stack — only arm personal bonus.
            TryArmShare(__instance, "OnTransferAllToPlayer", out _pendingShare);
        }

        [HarmonyPatch(typeof(InvSlot), "transferItemAllToPlayer")]
        [HarmonyPostfix]
        private static void OnTransferAllToPlayerPostfix(InvSlot __instance)
        {
            ApplyPendingShare(ref _pendingShare, "OnTransferAllToPlayerPostfix");
        }

        [HarmonyPatch(typeof(InvSlot), "grabItem")]
        [HarmonyPrefix]
        private static void OnGrabItemPrefix(InvSlot __instance)
        {
            // grabItem moves full stack to cursor — same personal-only share rule.
            TryArmShare(__instance, "OnGrabItem", out _pendingShare);
        }

        [HarmonyPatch(typeof(InvSlot), "grabItem")]
        [HarmonyPostfix]
        private static void OnGrabItemPostfix(InvSlot __instance)
        {
            // Cursor holds the real stack; add personal extras into player inv.
            ApplyPendingShare(ref _pendingShare, "OnGrabItemPostfix");
        }

        [HarmonyPatch(typeof(InvSlot), "transferItemToPlayer")]
        [HarmonyPrefix]
        private static void OnTransferToPlayerPrefix(InvSlot __instance)
        {
            // Single unit take: base amount is 1 (not full stack).
            _pendingShare = default;
            if (Config.ModConfig.GetLootShareMode() == Config.LootShareMode.Off) return;
            if (InvItemClass.isNull(__instance.invItem)) return;
            if (!IsExpItemClass(__instance.invItem)) return;
            if (IsPlayerPlacedSlot(__instance)) return;
            if (__instance.inventory != null
                && __instance.inventory.gameObject.GetComponent<DroppedItemIdentifier>() != null)
                return;
            if (__instance.inventory != null && Player.Instance != null
                && __instance.inventory == Player.Instance.Inventory)
                return;
            if (GetItemMultiplier() <= 1) return;
            _pendingShare = new PendingShare
            {
                Active = true,
                Type = __instance.invItem.type,
                BaseAmount = 1
            };
            Log($"OnTransferToPlayerPrefix: will add personal extra of '{_pendingShare.Type}' x1");
        }

        [HarmonyPatch(typeof(InvSlot), "transferItemToPlayer")]
        [HarmonyPostfix]
        private static void OnTransferToPlayerPostfix(InvSlot __instance)
        {
            // Capture type before clearing pending — slot may be empty after last unit.
            ApplyPendingShare(ref _pendingShare, "OnTransferToPlayerPostfix");
        }

        [HarmonyPatch(typeof(InvSlot), "placeItem")]
        [HarmonyPostfix]
        private static void OnPlaceItem(InvSlot __instance)
        {
            if (__instance.inventory == null || __instance.inventory.invType != Inventory.InvType.itemInv) return;
            int idx = __instance.inventory.slots.IndexOf(__instance);
            if (idx >= 0)
                MarkContainerSlotPlayerPlaced(__instance.inventory.transform.position, idx);
        }

        [HarmonyPatch(typeof(InvSlot), "controllerPlaceItem")]
        [HarmonyPostfix]
        private static void OnControllerPlaceItem(InvSlot __instance)
        {
            if (__instance.inventory == null || __instance.inventory.invType != Inventory.InvType.itemInv) return;
            int idx = __instance.inventory.slots.IndexOf(__instance);
            if (idx >= 0)
                MarkContainerSlotPlayerPlaced(__instance.inventory.transform.position, idx);
        }
    }
}
