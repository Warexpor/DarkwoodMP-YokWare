using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client-side optimistic pickup claim (non-GUID world + GUID drops). Records
    /// pre-count so a host ClaimDeny / lost Remove can refund only the surplus
    /// granted by the race.
    /// </summary>
    internal static class WorldPickupClaimPending
    {
        private struct Entry
        {
            public string ItemType;
            public int Amount;
            public int PreCount;
        }

        private static readonly Dictionary<int, Entry> _pending = new Dictionary<int, Entry>(8);
        private static readonly Dictionary<string, Entry> _pendingGuid = new Dictionary<string, Entry>(8);

        private static int Key(float x, float y, float z, string objectName)
        {
            return Sync.WorldPhysicsSyncService.MakePosNameKey(x, y, z, objectName);
        }

        internal static void Record(float x, float y, float z, string objectName,
            string itemType, int amount, int preCount)
        {
            if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            _pending[Key(x, y, z, objectName)] = new Entry
            {
                ItemType = itemType,
                Amount = amount,
                PreCount = preCount
            };
        }

        internal static void Clear(float x, float y, float z, string objectName)
        {
            _pending.Remove(Key(x, y, z, objectName));
        }

        /// <summary>Take pending once; returns false if none.</summary>
        internal static bool TryTake(float x, float y, float z, string objectName,
            out string itemType, out int amount, out int preCount)
        {
            int k = Key(x, y, z, objectName);
            if (_pending.TryGetValue(k, out Entry e))
            {
                _pending.Remove(k);
                itemType = e.ItemType;
                amount = e.Amount;
                preCount = e.PreCount;
                return true;
            }
            itemType = null;
            amount = 0;
            preCount = -1;
            return false;
        }

        internal static void Reset()
        {
            _pending.Clear();
            _pendingGuid.Clear();
        }

        // --- GUID drop claim pending (DroppedItemPickup host-auth) ---

        internal static void RecordGuid(string guid, string itemType, int amount, int preCount)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            _pendingGuid[guid] = new Entry
            {
                ItemType = itemType,
                Amount = amount,
                PreCount = preCount
            };
        }

        internal static void ClearGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            _pendingGuid.Remove(guid);
        }

        internal static bool TryTakeGuid(string guid, out string itemType, out int amount, out int preCount)
        {
            if (!string.IsNullOrEmpty(guid) && _pendingGuid.TryGetValue(guid, out Entry e))
            {
                _pendingGuid.Remove(guid);
                itemType = e.ItemType;
                amount = e.Amount;
                preCount = e.PreCount;
                return true;
            }
            itemType = null;
            amount = 0;
            preCount = -1;
            return false;
        }

        internal static void TryRefundIfPendingGuid(string guid, string reason)
        {
            if (!TryTakeGuid(guid, out string type, out int amt, out int pre))
                return;
            Refund(type, amt, pre, reason);
        }

        /// <summary>
        /// Remove surplus of itemType from the local player bag (container deny parity).
        /// </summary>
        internal static void Refund(string itemType, int amount, int preCount, string reason)
        {
            if (string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            try
            {
                Inventory pinv = Player.Instance != null ? Player.Instance.Inventory : null;
                if (pinv == null || pinv.slots == null)
                    return;

                int totalNow = ContainerSyncHelpers.CountPlayerItemType(itemType);
                int toRemove = preCount >= 0
                    ? Math.Max(0, totalNow - preCount)
                    : amount;
                if (toRemove <= 0)
                {
                    ModLog.Warn(LogCat.World,
                        "[WorldPickup] refund skip (" + reason + "): nothing to remove type="
                        + itemType + " totalNow=" + totalNow + " pre=" + preCount);
                    return;
                }

                int left = Math.Min(toRemove, totalNow);
                for (int i = pinv.slots.Count - 1; i >= 0 && left > 0; i--)
                {
                    InvSlot s = pinv.slots[i];
                    if (InvItemClass.isNull(s.invItem)) continue;
                    if (!string.Equals(s.invItem.type, itemType, StringComparison.Ordinal))
                        continue;
                    if (s.invItem.amount <= left)
                    {
                        left -= s.invItem.amount;
                        s.removeItem();
                    }
                    else
                    {
                        s.invItem.removeAmount(left);
                        left = 0;
                    }
                }

                ModLog.Event(LogCat.World,
                    "[WorldPickup] refunded " + itemType + " x" + (toRemove - left)
                    + " (" + reason + ")");

                if (Player.Instance != null)
                {
                    PersonalFlavorHud.BeginBypass();
                    try { Player.Instance.displayMessage("Already taken…"); }
                    finally { PersonalFlavorHud.EndBypass(); }
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn(LogCat.World, "[WorldPickup] refund failed: " + ex.Message);
            }
        }

        /// <summary>Lost race: consume pending + refund once.</summary>
        internal static void TryRefundIfPending(float x, float y, float z, string objectName, string reason)
        {
            if (!TryTake(x, y, z, objectName, out string type, out int amt, out int pre))
                return;
            Refund(type, amt, pre, reason);
        }
    }
}
