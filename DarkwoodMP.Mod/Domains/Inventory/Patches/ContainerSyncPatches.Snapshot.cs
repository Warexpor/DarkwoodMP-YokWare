using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>Slot state snapshot for change detection.</summary>
    internal struct SlotSnapshot
    {
        public int Index;
        public string Type;
        public int Amount;
        public float Durability;
        public int Ammo;
        public bool IsRecipe;
        public string[] Upgrades;
        public bool ShouldBeActive;
    }

    /// <summary>
    /// Shared helper for taking inventory snapshots and sending diffs.
    /// </summary>
    internal static class ContainerSnapshotHelper
    {
        internal static Dictionary<int, SlotSnapshot> TakeSnapshot(Inventory inv)
        {
            var dict = new Dictionary<int, SlotSnapshot>();
            for (int i = 0; i < inv.slots.Count; i++)
            {
                var slot = inv.slots[i];
                if (!InvItemClass.isNull(slot.invItem))
                {
                    bool isRecipe = slot.invItem.isRecipe;
                    dict[i] = new SlotSnapshot
                    {
                        Index = i,
                        Type = isRecipe ? slot.invItem.recipeFor : slot.invItem.type,
                        Amount = slot.invItem.amount,
                        Durability = slot.invItem.durability,
                        Ammo = slot.invItem.ammo,
                        IsRecipe = isRecipe,
                        Upgrades = Sync.InvItemUpgradeWire.CollectNames(slot.invItem),
                        ShouldBeActive = slot.invItem.shouldBeActive
                    };
                }
            }
            return dict;
        }

        internal static void SendDiff(Inventory inv, Dictionary<int, SlotSnapshot> before)
        {
            if (before == null || inv == null) return;
            var after = TakeSnapshot(inv);
            Vector3 pos = inv.transform.position;

            foreach (var kv in after)
            {
                if (before.TryGetValue(kv.Key, out var prev))
                {
                    if (kv.Value.Type == prev.Type && kv.Value.Amount > prev.Amount)
                        ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount - prev.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true, isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                }
                else
                {
                    ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount, kv.Value.Durability, kv.Value.Ammo, isPlayerPlaced: true, isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                }
            }
        }

        /// <summary>
        /// Full before/after sync for craft (and similar) mutations that both
        /// remove ingredients from and optionally stack products into a shared
        /// pile. Place-only <see cref="SendDiff"/> misses ingredient consume.
        /// </summary>
        internal static void SendFullDiff(Inventory inv, Dictionary<int, SlotSnapshot> before)
        {
            if (before == null || inv == null) return;
            var after = TakeSnapshot(inv);
            Vector3 pos = inv.transform.position;

            foreach (var kv in before)
            {
                if (!after.TryGetValue(kv.Key, out var now))
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    continue;
                }

                if (kv.Value.Type != now.Type || kv.Value.IsRecipe != now.IsRecipe)
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.PlaceItem, pos, kv.Key, now.Type, now.Amount,
                        now.Durability, now.Ammo, isPlayerPlaced: true, isRecipe: now.IsRecipe,
                        upgrades: now.Upgrades, shouldBeActive: now.ShouldBeActive);
                    continue;
                }

                if (now.Amount < kv.Value.Amount)
                {
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type,
                        kv.Value.Amount - now.Amount, kv.Value.Durability, kv.Value.Ammo,
                        isRecipe: kv.Value.IsRecipe, upgrades: kv.Value.Upgrades,
                        shouldBeActive: kv.Value.ShouldBeActive);
                }
                else if (now.Amount == kv.Value.Amount
                    && System.Math.Abs(now.Durability - kv.Value.Durability) > 0.001f)
                {
                    // Durability-only drain (recipe durabilityAmount): rewrite slot.
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.RemoveItem, pos, kv.Key, kv.Value.Type, kv.Value.Amount,
                        kv.Value.Durability, kv.Value.Ammo, isRecipe: kv.Value.IsRecipe,
                        upgrades: kv.Value.Upgrades, shouldBeActive: kv.Value.ShouldBeActive);
                    ContainerSyncHelpers.SendContainerAction(
                        ContainerAction.PlaceItem, pos, kv.Key, now.Type, now.Amount,
                        now.Durability, now.Ammo, isPlayerPlaced: true, isRecipe: now.IsRecipe,
                        upgrades: now.Upgrades, shouldBeActive: now.ShouldBeActive);
                }
            }

            SendDiff(inv, before);
        }
    }

    /// <summary>Per-invocation snapshot state for transfer-to-opened-inventory patches.</summary>
    internal struct ContainerSnapshotState
    {
        public bool Active;
        public Dictionary<int, SlotSnapshot> Snapshot;
    }
}
