using System;
using System.Collections.Generic;
using DWMPHorde.Logging;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client-side optimistic pickup claim (non-GUID world + GUID drops). Records
    /// pre-count so a host ClaimDeny / lost Remove can refund only the surplus
    /// granted by the race. A recipe's live item type is always "recipe", so the
    /// recipe it teaches (<c>recipeFor</c>) rides along and is what the count and the
    /// removal match on.
    /// </summary>
    internal static class WorldPickupClaimPending
    {
        private struct Entry
        {
            public string ItemType;
            public string RecipeFor;
            public int Amount;
            public int PreCount;
        }

        private static readonly Dictionary<Sync.WorldPhysicsSyncService.PosNameKey, Entry> _pending =
            new Dictionary<Sync.WorldPhysicsSyncService.PosNameKey, Entry>(8);
        private static readonly Dictionary<string, Entry> _pendingGuid = new Dictionary<string, Entry>(8);

        private static Sync.WorldPhysicsSyncService.PosNameKey Key(float x, float y, float z, string objectName)
        {
            return Sync.WorldPhysicsSyncService.MakePosNameKey(x, y, z, objectName);
        }

        internal static void Record(float x, float y, float z, string objectName,
            string itemType, int amount, int preCount, string recipeFor = null)
        {
            if (string.IsNullOrEmpty(objectName) || string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            _pending[Key(x, y, z, objectName)] = new Entry
            {
                ItemType = itemType,
                RecipeFor = recipeFor,
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
            out string itemType, out int amount, out int preCount, out string recipeFor)
        {
            var k = Key(x, y, z, objectName);
            if (_pending.TryGetValue(k, out Entry e))
            {
                _pending.Remove(k);
                itemType = e.ItemType;
                recipeFor = e.RecipeFor;
                amount = e.Amount;
                preCount = e.PreCount;
                return true;
            }
            itemType = null;
            recipeFor = null;
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

        internal static void RecordGuid(string guid, string itemType, int amount, int preCount,
            string recipeFor = null)
        {
            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            _pendingGuid[guid] = new Entry
            {
                ItemType = itemType,
                RecipeFor = recipeFor,
                Amount = amount,
                PreCount = preCount
            };
        }

        internal static void ClearGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return;
            _pendingGuid.Remove(guid);
        }

        internal static bool TryTakeGuid(string guid, out string itemType, out int amount,
            out int preCount, out string recipeFor)
        {
            if (!string.IsNullOrEmpty(guid) && _pendingGuid.TryGetValue(guid, out Entry e))
            {
                _pendingGuid.Remove(guid);
                itemType = e.ItemType;
                recipeFor = e.RecipeFor;
                amount = e.Amount;
                preCount = e.PreCount;
                return true;
            }
            itemType = null;
            recipeFor = null;
            amount = 0;
            preCount = -1;
            return false;
        }

        internal static void TryRefundIfPendingGuid(string guid, string reason)
        {
            if (!TryTakeGuid(guid, out string type, out int amt, out int pre, out string recipeFor))
                return;
            Refund(type, amt, pre, reason, recipeFor);
        }

        /// <summary>
        /// Remove surplus of itemType from the local player bag (container deny parity).
        /// <paramref name="recipeFor"/> non-empty: the item is that recipe (live type "recipe").
        /// </summary>
        internal static void Refund(string itemType, int amount, int preCount, string reason,
            string recipeFor = null)
        {
            if (string.IsNullOrEmpty(itemType) || amount <= 0)
                return;
            try
            {
                Inventory pinv = Player.Instance != null ? Player.Instance.Inventory : null;
                if (pinv == null || pinv.slots == null)
                    return;

                bool isRecipe = !string.IsNullOrEmpty(recipeFor);
                string wireType = isRecipe ? recipeFor : itemType;
                int totalNow = ContainerSyncHelpers.CountPlayerItem(wireType, isRecipe);
                int toRemove = preCount >= 0
                    ? Math.Max(0, totalNow - preCount)
                    : amount;
                if (toRemove <= 0)
                {
                    ModLog.Warn(LogCat.World,
                        "[WorldPickup] refund skip (" + reason + "): nothing to remove type="
                        + wireType + " totalNow=" + totalNow + " pre=" + preCount);
                    return;
                }

                int removed = ContainerSyncHelpers.RemoveGrantedFromPlayer(
                    wireType, isRecipe, Math.Min(toRemove, totalNow), -1f, 0);

                ModLog.Event(LogCat.World,
                    "[WorldPickup] refunded " + (isRecipe ? "recipe:" : "") + wireType + " x" + removed
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
            if (!TryTake(x, y, z, objectName, out string type, out int amt, out int pre, out string recipeFor))
                return;
            Refund(type, amt, pre, reason, recipeFor);
        }
    }
}
