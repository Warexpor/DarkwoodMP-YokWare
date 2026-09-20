using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using Steamworks;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Pending remove/take tracking + client container state-sync apply (dupe prevention).
    /// </summary>
    internal sealed class ContainerPendingNetHandlers
    {
        private readonly LanNetworkManager _net;

        private readonly Dictionary<string, HashSet<int>> _pendingContainerRemoves =
            new Dictionary<string, HashSet<int>>();
        private readonly Dictionary<string, int> _pendingTakePreCounts =
            new Dictionary<string, int>();

        internal ContainerPendingNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearPendingContainerState()
        {
            _pendingContainerRemoves.Clear();
            _pendingTakePreCounts.Clear();
        }

        /// <summary>
        /// Records a pending RemoveItem/TakeItem so HandleContainerStateSync
        /// won't re-add the item to this slot (infinite loot dupe prevention).
        /// </summary>
        internal void RecordPendingContainerRemove(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}";
            if (!_pendingContainerRemoves.TryGetValue(key, out var set))
            {
                set = new HashSet<int>();
                _pendingContainerRemoves[key] = set;
            }
            set.Add(slotIdx);
        }

        /// <summary>
        /// Records the player inventory count of an item type before a container take
        /// was sent. Used by HandleContainerTakeDenied for a precise refund.
        /// </summary>
        internal void RecordPendingTakePreCount(Vector3 pos, int slotIdx, int preCount)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            _pendingTakePreCounts[key] = preCount;
        }

        /// <summary>Removes a pending take pre-count entry after it's consumed or stale.</summary>
        internal void ClearPendingTakePreCount(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            _pendingTakePreCounts.Remove(key);
        }


        /// <summary>Consume a pending take pre-count (same semantics as the former inline dict access).</summary>
        internal void ConsumePendingTakePreCount(string preKey, out int preTakeCount)
        {
            _pendingTakePreCounts.TryGetValue(preKey, out preTakeCount);
            _pendingTakePreCounts.Remove(preKey);
        }

        /// <summary>
        /// Host->Client: full container state snapshot.
        /// Clears all slots on the client container and recreates them from
        /// the host's authoritative slot data.
        /// </summary>
        internal void HandleContainerStateSync(ContainerStateSyncMessage msg)
        {
            if (_net.Role != NetworkRole.Client) return;

            ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: hash={msg.EntityHash} pos=({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) slotCount={msg.SlotCount}");

            // Try exact entity hash lookup first
            Inventory inv = null;
            if (msg.EntityHash > 0)
            {
                Character c = CharacterTracker.FindByStableId((short)msg.EntityHash);
                if (c != null)
                {
                    inv = c.GetComponent<Inventory>();
                    if (inv == null)
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash {msg.EntityHash} found '{c.name}' but no Inventory");
                    else
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash OK: '{c.name}' invType={inv.invType}");
                }
                else
                {
                    ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity hash {msg.EntityHash} not found, falling back to position");
                }
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            if (inv == null)
            {
                inv = WorldQueryHelper.FindInventoryByPos(pos);
                if (inv == null)
                {
                    ModRuntime.Log?.LogWarning($"[Container] HandleContainerStateSync: no inventory at ({msg.PosX:F1},{msg.PosY:F1},{msg.PosZ:F1}) hash={msg.EntityHash}");
                    return;
                }
                ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: found by pos: '{inv.name}' invType={inv.invType}");
            }

            // Log current client state before overwriting
            int beforeCount = 0;
            for (int i = 0; i < inv.slots.Count; i++)
                if (!InvItemClass.isNull(inv.slots[i].invItem)) beforeCount++;
            ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: client had {beforeCount} items, host says {msg.SlotCount} items — overwriting");

            // Check for pending local removes (dupe prevention): if the player
            // already took items from this container before the sync arrived,
            // don't re-add them.
            string containerKey = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}";
            _pendingContainerRemoves.TryGetValue(containerKey, out var pendingSlots);

            // Clear all slots on the client container
            foreach (var slot in inv.slots)
            {
                if (!InvItemClass.isNull(slot.invItem))
                    slot.removeItem();
            }

            // Recreate slots from host data, skipping slots with pending removes
            for (int i = 0; i < msg.SlotCount; i++)
            {
                var entry = msg.Slots[i];
                if (entry.SlotIndex >= inv.slots.Count || string.IsNullOrEmpty(entry.ItemType))
                    continue;

                // Skip slots that the player has pending local removal for
                if (pendingSlots != null && pendingSlots.Contains(entry.SlotIndex))
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: skipping slot {entry.SlotIndex} ({entry.ItemType}) — pending local removal");
                    continue;
                }

                inv.slots[entry.SlotIndex].createItem(entry.ItemType, entry.Amount,
                    entry.Durability > 0f ? entry.Durability : 1f);
                if (entry.Ammo > 0)
                {
                    var item = inv.slots[entry.SlotIndex].invItem;
                    if (!InvItemClass.isNull(item))
                        item.ammo = entry.Ammo;
                }
            }

            // Clean up pending tracking for this container. The items we skipped
            // are already in the player's inventory; the RemoveItem is in transit
            // and the host will process it shortly. On the next open (next sync),
            // the host's state will correctly reflect the removed items.
            if (pendingSlots != null)
                _pendingContainerRemoves.Remove(containerKey);

            // Clear pending take pre-counts because the state sync is now
            // authoritative.
            _pendingTakePreCounts.Clear();

            // Do not play open_drawer here. Local Item.openInventory already
            // played it, and state sync is silent.
        }
    }
}
