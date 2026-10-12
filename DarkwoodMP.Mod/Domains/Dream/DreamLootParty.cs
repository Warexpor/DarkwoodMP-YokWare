using System.Collections.Generic;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// A party dream's own container loot is every dreamer's. Vanilla lays out one for the one
    /// dreamer (the church ruins wardrobe's axe against its chompers); in a party dream the first
    /// taker left the others with nothing to fight with. The host keeps one copy in the slot for
    /// each live dreamer who has not taken theirs yet: after a take it puts the taken stack back
    /// and the normal container sync shows it to everyone. A client taking from the same slot a
    /// second time is refused. The dream bag is thrown away on waking, so nothing is multiplied
    /// into the world. Journal items stay single (they are the party's story items), as do
    /// stacks a player put in and the loot of bodies. The dream's ground items are the same rule
    /// done locally (<see cref="DroppedItemSyncHelpers.IsDreamScenePickup"/>).
    /// </summary>
    internal static class DreamLootParty
    {
        // Slot (inventory instance + index) → the players who took their copy from it.
        private static readonly Dictionary<long, HashSet<int>> Takers = new Dictionary<long, HashSet<int>>(); // reset-in: Reset

        public static void Reset() => Takers.Clear();

        private static long Key(Inventory inv, int slotIdx) => ((long)inv.GetInstanceID() << 16) ^ (uint)slotIdx;

        /// <summary>A slot of the party dream's own containers whose item every dreamer gets.</summary>
        private static bool Eligible(Inventory inv, int slotIdx, string itemType)
        {
            if (inv == null || inv.slots == null || slotIdx < 0 || slotIdx >= inv.slots.Count)
                return false;
            if (!DreamSyncManager.IsDreamActive || inv.invType != Inventory.InvType.itemInv)
                return false;
            if (!DreamSyncManager.IsOnDreamPad(inv.transform) || PersonalPads.Contains(inv.transform))
                return false;
            if (string.IsNullOrEmpty(itemType) || IsJournalItem(itemType))
                return false;
            // A player's own drop on the ground, or a stack a player put in.
            var ident = inv.GetComponent<DroppedItemIdentifier>();
            if (ident != null && !string.IsNullOrEmpty(ident.Id))
                return false;
            return !ItemDoublePickupPatch.IsPlayerPlacedSlot(inv.slots[slotIdx]);
        }

        private static bool IsJournalItem(string type)
        {
            JournalDatabase db = Singleton<JournalDatabase>.Instance;
            if (db == null)
                return false;
            try
            {
                return db.getKey(type) != null || db.getNote(type) != null || db.getItem(type) != null;
            }
            catch { return false; }
        }

        /// <summary>Host: this client already took its copy from the slot (before the take is applied).</summary>
        internal static bool IsRepeatTake(Inventory inv, int slotIdx, string itemType, int playerId)
        {
            if (!Eligible(inv, slotIdx, itemType))
                return false;
            return Takers.TryGetValue(Key(inv, slotIdx), out var set) && set.Contains(playerId);
        }

        /// <summary>
        /// Host, after a take from the slot was applied here (its own or a client's): note the taker
        /// and put the stack back while a live dreamer still has none.
        /// </summary>
        internal static void HostAfterTake(Inventory inv, int slotIdx, string itemType, int amount, float durability,
            int ammo, bool isRecipe, string[] upgrades, bool shouldBeActive, int playerId)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host || amount <= 0)
                return;
            if (!Eligible(inv, slotIdx, itemType))
                return;
            long key = Key(inv, slotIdx);
            if (!Takers.TryGetValue(key, out var set))
                Takers[key] = set = new HashSet<int>();
            set.Add(playerId);

            int waiting = 0;
            if (FinalDreamsceneManager.LocalIsLiveDreamer() && !set.Contains(net.LocalPlayerId))
                waiting++;
            foreach (int id in DreamSyncManager.RemoteDreamParticipantIds())
            {
                if (id != net.LocalPlayerId && FinalDreamsceneManager.IsLiveRemoteDreamer(id) && !set.Contains(id))
                    waiting++;
            }
            if (waiting == 0)
                return;

            // Outside the apply guard of the client's take (a send inside it is dropped), after
            // its relay: the others see the take, then the copy put back.
            Singleton<Controller>.Instance.waitFramesAndRun(delegate
            {
                if (inv == null || slotIdx >= inv.slots.Count || !DreamSyncManager.IsDreamActive)
                    return;
                InvSlot slot = inv.slots[slotIdx];
                if (InvItemClass.isNull(slot.invItem))
                {
                    InvItemClass created = slot.createItem(itemType, amount, 1f, InvItem.ModifierQuality.none, isRecipe);
                    if (InvItemClass.isNull(created))
                        return;
                    InvItemTransferApply.ApplyMeta(created, durability, ammo, shouldBeActive);
                    InvItemUpgradeWire.Apply(created, upgrades);
                }
                else if (ContainerSyncHelpers.ItemTypeMatchesWire(slot.invItem, itemType, isRecipe))
                {
                    slot.invItem.amount += amount;
                    slot.invItem.refresh();
                }
                else
                {
                    return;
                }
                ContainerSyncHelpers.SendContainerAction(ContainerAction.PlaceItem, inv.transform.position, slotIdx,
                    itemType, amount, durability, ammo, isPlayerPlaced: false, isRecipe: isRecipe,
                    upgrades: upgrades, shouldBeActive: shouldBeActive);
                ModLog.Event(LogCat.Dream, "[DreamLoot] " + itemType + " x" + amount + " back in " + inv.name
                    + " slot " + slotIdx + ": " + waiting + " dreamer(s) still without theirs");
            }, 1);
        }
    }
}
