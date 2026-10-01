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
        /// <summary>What an optimistic client take granted, for a precise deny refund.</summary>
        internal struct PendingTake
        {
            public int PreCount;
            public bool IsRecipe;
            public string ItemType;
            /// <summary>&lt; 0 when unknown.</summary>
            public float Durability;
            public int Ammo;
            /// <summary>Time.realtimeSinceStartup when the take was sent.</summary>
            public float RecordedAt;
        }

        private readonly Dictionary<string, PendingTake> _pendingTakePreCounts =
            new Dictionary<string, PendingTake>();
        private readonly List<string> _preCountScratch = new List<string>();

        /// <summary>A take whose deny could still be in flight keeps its record through a state sync.</summary>
        private const float PendingTakeInFlightSeconds = 5f;
        /// <summary>Unanswered take records are dropped after this long.</summary>
        private const float PendingTakeMaxAgeSeconds = 60f;

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
        /// Drops the pending local-remove mark for one slot. A host deny means the host still
        /// holds (or never lost) that slot, so the snapshot that follows must show it.
        /// </summary>
        internal void ClearPendingContainerRemove(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}";
            if (!_pendingContainerRemoves.TryGetValue(key, out var set))
                return;
            set.Remove(slotIdx);
            if (set.Count == 0)
                _pendingContainerRemoves.Remove(key);
        }

        /// <summary>
        /// Records the player inventory count of an item type before a container take
        /// was sent. Used by HandleContainerTakeDenied for a precise refund.
        /// </summary>
        internal void RecordPendingTakePreCount(Vector3 pos, int slotIdx, int preCount,
            bool isRecipe = false, string itemType = null, float durability = -1f, int ammo = 0)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            float now = Time.realtimeSinceStartup;
            PrunePendingTakes(null, now, PendingTakeMaxAgeSeconds);
            _pendingTakePreCounts[key] = new PendingTake
            {
                PreCount = preCount,
                IsRecipe = isRecipe,
                ItemType = itemType,
                Durability = durability,
                Ammo = ammo,
                RecordedAt = now
            };
        }

        /// <summary>
        /// Drops take records at least <paramref name="minAge"/> old; with a container key prefix
        /// only that container's slots are considered.
        /// </summary>
        private void PrunePendingTakes(string containerPrefix, float now, float minAge)
        {
            if (_pendingTakePreCounts.Count == 0) return;
            _preCountScratch.Clear();
            foreach (var kv in _pendingTakePreCounts)
            {
                if (containerPrefix != null && !kv.Key.StartsWith(containerPrefix, StringComparison.Ordinal))
                    continue;
                if (now - kv.Value.RecordedAt >= minAge)
                    _preCountScratch.Add(kv.Key);
            }
            for (int i = 0; i < _preCountScratch.Count; i++)
                _pendingTakePreCounts.Remove(_preCountScratch[i]);
            _preCountScratch.Clear();
        }

        /// <summary>Removes a pending take pre-count entry after it's consumed or stale.</summary>
        internal void ClearPendingTakePreCount(Vector3 pos, int slotIdx)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            _pendingTakePreCounts.Remove(key);
        }


        /// <summary>Consume the pending take for a denied claim; false when none was recorded.</summary>
        internal bool ConsumePendingTake(string preKey, out PendingTake take)
        {
            bool found = _pendingTakePreCounts.TryGetValue(preKey, out take);
            _pendingTakePreCounts.Remove(preKey);
            if (!found)
                take = new PendingTake { PreCount = -1, Durability = -1f };
            return found;
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
            // Hot path: client just opened this container — avoid FindInventoryByPos scan.
            if (inv == null && Player.Instance != null)
            {
                Inventory opened = Player.Instance.openedItemInventory2 ?? Player.Instance.openedItemInventory;
                if (opened != null)
                {
                    float odx = opened.transform.position.x - pos.x;
                    float odz = opened.transform.position.z - pos.z;
                    if (odx * odx + odz * odz < 2.5f * 2.5f)
                        inv = opened;
                }
            }
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

                // Durability on the wire is absolute (vanilla InvItemClass.durability).
                // createItem's float arg is a 0..1 multiplier — always pass 1f then assign.
                InvItemClass created = inv.slots[entry.SlotIndex].createItem(
                    entry.ItemType, entry.Amount, 1f,
                    InvItem.ModifierQuality.none, entry.IsRecipe);
                if (!InvItemClass.isNull(created))
                {
                    Sync.InvItemTransferApply.ApplyMeta(
                        created, entry.Durability, entry.Ammo, entry.ShouldBeActive);
                    Sync.InvItemUpgradeWire.Apply(created, entry.Upgrades);
                }
            }

            // Clean up pending tracking for this container. The items we skipped
            // are already in the player's inventory; the RemoveItem is in transit
            // and the host will process it shortly. On the next open (next sync),
            // the host's state will correctly reflect the removed items.
            if (pendingSlots != null)
                _pendingContainerRemoves.Remove(containerKey);

            // Drop this container's settled take records only. Other containers' takes, and a
            // take here whose deny may still be in flight, keep theirs so the refund stays exact.
            PrunePendingTakes(containerKey + "_", Time.realtimeSinceStartup, PendingTakeInFlightSeconds);

            // Do not play open_drawer here. Local Item.openInventory already
            // played it, and state sync is silent.
        }
    }
}
