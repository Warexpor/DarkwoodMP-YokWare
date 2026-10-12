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
            /// <summary>Loot-share extra the take put in the bag on top of the stack (refunded with it).</summary>
            public int ShareExtra;
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
            _pendingEntityStates.Clear();
            _pendingPadStates.Clear();
        }

        /// <summary>
        /// A dream pad's container contents sent while this client's copy of the pad was still
        /// spawning (the host fans them out as its own pad settles, which beats a peer's load).
        /// They were dropped ("no inventory"), and the client kept the empty containers its own
        /// skipped roll left until the player opened one. Applied once the pad has loaded.
        /// </summary>
        private readonly List<KeyValuePair<float, ContainerStateSyncMessage>> _pendingPadStates =
            new List<KeyValuePair<float, ContainerStateSyncMessage>>();

        private void QueuePadState(ContainerStateSyncMessage msg)
        {
            for (int i = _pendingPadStates.Count - 1; i >= 0; i--)
            {
                ContainerStateSyncMessage p = _pendingPadStates[i].Value;
                if (Mathf.Abs(p.PosX - msg.PosX) < 0.2f && Mathf.Abs(p.PosY - msg.PosY) < 0.2f
                    && Mathf.Abs(p.PosZ - msg.PosZ) < 0.2f && p.SaveId == msg.SaveId)
                    _pendingPadStates.RemoveAt(i);
            }
            _pendingPadStates.Add(new KeyValuePair<float, ContainerStateSyncMessage>(Time.unscaledTime, msg));
            ModRuntime.LegacyInfo($"[Container] state for ({msg.PosX:F1},{msg.PosZ:F1}) held until the dream pad has loaded");
        }

        /// <summary>The dream pad finished loading here: fill the containers whose state came early.</summary>
        internal void FlushPendingPadStates()
        {
            if (_pendingPadStates.Count == 0 || _net.Role != NetworkRole.Client)
                return;
            var batch = new List<KeyValuePair<float, ContainerStateSyncMessage>>(_pendingPadStates);
            _pendingPadStates.Clear();
            for (int i = 0; i < batch.Count; i++)
            {
                ContainerStateSyncMessage msg = batch[i].Value;
                if (WorldQueryHelper.FindInventory(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.SaveId) != null)
                    HandleContainerStateSync(msg);
                else
                    ModRuntime.LegacyInfo($"[Container] held pad state at ({msg.PosX:F1},{msg.PosZ:F1}): no container after the load; dropped");
            }
        }

        /// <summary>A pad that never finished loading (a cancelled entry) leaves nothing to fill.</summary>
        private void ExpirePendingPadStates(float now)
        {
            for (int i = _pendingPadStates.Count - 1; i >= 0; i--)
            {
                if (now - _pendingPadStates[i].Key > PendingEntityStateMaxAge)
                    _pendingPadStates.RemoveAt(i);
            }
        }

        private const float PendingEntityStateMaxAge = 30f;

        /// <summary>
        /// A creature's inventory sent before the creature reached this client (the host rolls a
        /// night spawn's loot as it spawns; the entity follows on the next snapshot), by host id.
        /// </summary>
        private readonly Dictionary<int, KeyValuePair<float, ContainerStateSyncMessage>> _pendingEntityStates =
            new Dictionary<int, KeyValuePair<float, ContainerStateSyncMessage>>();

        private readonly List<int> _entityStateScratch = new List<int>();

        /// <summary>Client tick: fill a waiting creature's inventory once the creature is here.</summary>
        internal void TryFlushPendingEntityStates()
        {
            if (_net.Role != NetworkRole.Client)
                return;
            float now = Time.unscaledTime;
            if (_pendingPadStates.Count > 0)
                ExpirePendingPadStates(now);
            if (_pendingEntityStates.Count == 0)
                return;
            _entityStateScratch.Clear();
            _entityStateScratch.AddRange(_pendingEntityStates.Keys);
            foreach (int hash in _entityStateScratch)
            {
                var entry = _pendingEntityStates[hash];
                if (CharacterTracker.FindByStableId((short)hash) != null)
                {
                    _pendingEntityStates.Remove(hash);
                    HandleContainerStateSync(entry.Value);
                }
                else if (now - entry.Key > PendingEntityStateMaxAge)
                {
                    _pendingEntityStates.Remove(hash);
                    // A character this client never maps to the host's id (a story NPC standing at
                    // its own spot) is still found where it stands; a container is not one.
                    ContainerStateSyncMessage msg = entry.Value;
                    Inventory at = WorldQueryHelper.FindInventory(new Vector3(msg.PosX, msg.PosY, msg.PosZ), msg.SaveId);
                    if (at != null && at.GetComponent<Character>() != null)
                    {
                        msg.EntityHash = 0;
                        HandleContainerStateSync(msg);
                    }
                    else
                        ModRuntime.LegacyInfo($"[Container] entity {hash} never arrived; its inventory state dropped");
                }
            }
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

        /// <summary>The loot share added <paramref name="extra"/> on top of this take (ItemDoublePickupPatch).</summary>
        internal void AddPendingTakeShareExtra(Vector3 pos, int slotIdx, int extra)
        {
            string key = $"{pos.x:F2}_{pos.y:F2}_{pos.z:F2}_{slotIdx}";
            if (extra <= 0 || !_pendingTakePreCounts.TryGetValue(key, out PendingTake take))
                return;
            take.ShareExtra += extra;
            _pendingTakePreCounts[key] = take;
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
            // On the title or mid-load there is no container to fill: the world package carries the
            // contents, and opening one asks the host again.
            if (!LanNetworkManager.ClientCanApplyWorldBulk()) return;

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
                    // Not here yet: wait for it. Its inventory is its own, and the position fallback
                    // filled whatever container it stood beside (a wardrobe, a death bag).
                    _pendingEntityStates[msg.EntityHash] = new KeyValuePair<float, ContainerStateSyncMessage>(Time.unscaledTime, msg);
                    ModRuntime.LegacyInfo($"[Container] HandleContainerStateSync: entity {msg.EntityHash} not here yet — waiting for it");
                    return;
                }
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            // Hot path: client just opened this container — avoid FindInventoryByPos scan.
            if (inv == null && Player.Instance != null)
            {
                Inventory opened = Player.Instance.openedItemInventory2 ?? Player.Instance.openedItemInventory;
                if (opened != null && msg.SaveId > 0 && WorldQueryHelper.ContainerSaveId(opened) == msg.SaveId)
                    inv = opened;
                else if (opened != null)
                {
                    float odx = opened.transform.position.x - pos.x;
                    float odz = opened.transform.position.z - pos.z;
                    if (odx * odx + odz * odz < 2.5f * 2.5f)
                        inv = opened;
                }
            }
            if (inv == null)
            {
                inv = WorldQueryHelper.FindInventory(pos, msg.SaveId);
                if (inv == null && DreamSyncManager.IsDreamPadLoadingFor(pos))
                {
                    QueuePadState(msg);
                    return;
                }
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
            // Keyed by this game's own spot for it (where its takes were recorded): the host's
            // spot for a slid wreck is not this one.
            Vector3 localPos = inv.transform.position;
            string containerKey = $"{localPos.x:F2}_{localPos.y:F2}_{localPos.z:F2}";
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
            Sync.DesyncCheck.NoteContainerSynced(inv);

            // Do not play open_drawer here. Local Item.openInventory already
            // played it, and state sync is silent.
        }
    }
}
