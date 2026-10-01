using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// World pickup destroy helpers (split from Apply for ownership).
    /// </summary>
    public static partial class WorldPhysicsSyncService
    {
        /// <summary>Max XZ distance between the reported pose and this peer's copy.</summary>
        private const float DestroyMatchRadius = 2f;

        /// <summary>
        /// Finds a world object (mushroom, exp item, shiny stone, trap, etc.) at the reported
        /// position by exact normalized name or item type and destroys it. Used when the remote
        /// peer reports that they harvested/picked up the object. Returns false on a miss.
        /// </summary>
        public static bool DestroyObjectByPos(Vector3 pos, string objectName)
        {
            return DestroyObjectByPos(pos, objectName, useDebounce: true);
        }

        /// <summary>
        /// Host claim check: destroy the host's copy of a claimed world pickup. On a miss the
        /// scene item cache is refreshed once (it can lag a fresh spawn) before giving up.
        /// </summary>
        internal static bool TryDestroyClaimedWorldPickup(Vector3 pos, string objectName)
        {
            if (DestroyObjectByPos(pos, objectName, useDebounce: false))
                return true;
            WorldQueryHelper.InvalidateSceneScanCache<Item>();
            return DestroyObjectByPos(pos, objectName, useDebounce: false);
        }

        private static bool DestroyObjectByPos(Vector3 pos, string objectName, bool useDebounce)
        {
            // AudioObject removal requests are ephemeral sound effects, not actual traps
            if (!string.IsNullOrEmpty(objectName)
                && objectName.IndexOf("audioobject", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            string needle = NormalizeObjectName(objectName);
            if (needle.Length == 0)
                return false;

            // Debounce before scene queries to avoid duplicate removal work.
            // from disarm was repeatedly scanning and then throwing on DestroyImmediate+name.
            PosNameKey posKey = MakePosNameKey(pos.x, pos.y, pos.z, objectName);
            float now = Time.time;
            if (useDebounce)
            {
                if (_s.DestroyDebounce.TryGetValue(posKey, out float lastDestroy)
                    && (now - lastDestroy) < DestroyDebounceTime)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[ObjectDestroy] debounced duplicate at {pos}");
                    return false;
                }
                PruneDestroyDebounce(now);
            }

            GameObject best = null;
            float matchSq = DestroyMatchRadius * DestroyMatchRadius;
            float bestDistSq = float.MaxValue;

            // 1) OverlapSphere: nearby roots whose name or item type matches exactly.
            int nearbyN = OverlapNear(pos, DestroyMatchRadius);
            for (int i = 0; i < nearbyN; i++)
            {
                Collider col = _overlap3D[i];
                if (col == null) continue;
                GameObject root = col.gameObject;
                if (root == null) continue;
                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && rb.gameObject != null) root = rb.gameObject;

                Item item = col.GetComponentInParent<Item>();
                if (item != null && item.gameObject != null) root = item.gameObject;

                if (root == null) continue;
                float dSq = XzDistSq(root.transform.position, pos);
                if (dSq > matchSq || dSq >= bestDistSq) continue;
                if (!ShouldDestroyWorldPickup(root, needle))
                    continue;
                bestDistSq = dSq;
                best = root;
            }

            // 2) Scene scan for collider-less / culled items near pos. Skip it for known trap
            // names after the overlap query: a missing trap has already been removed.
            if (best == null && !NeedleLooksLikeTrap(needle))
            {
                Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    if (it == null) continue;
                    GameObject go = it.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    float dSq = XzDistSq(go.transform.position, pos);
                    if (dSq > matchSq || dSq >= bestDistSq) continue;
                    if (!ShouldDestroyWorldPickup(go, needle)) continue;
                    bestDistSq = dSq;
                    best = go;
                }
            }

            if (useDebounce)
                _s.DestroyDebounce[posKey] = now;
            if (best == null)
            {
                // Debounce stays claimed so follow-up removes of an already-gone object skip the scan.
                ModRuntime.LegacyInfo($"[ObjectDestroy] miss name=\"{(objectName ?? "")}\" at {pos}");
                return false;
            }

            string destroyedName = objectName;
            try { destroyedName = best.name; }
            catch { /* destroyed Unity object */ }

            RemoveObjectFromInterpolation(best);
            try
            {
                if (best.transform != null)
                    Core.RemovePooledPrefab(best.transform);
            }
            catch { /* ignore */ }
            try
            {
                TraverseHack.ApplyingFromNetwork = true;
                // Co-op rescue: free anyone still flagged inBearTrap near this destroy pose.
                ReleaseLocalBearTrapIfNear(best.transform.position);

                UnityEngine.Object.DestroyImmediate(best);
            }
            finally { TraverseHack.ApplyingFromNetwork = false; }
            ModRuntime.LegacyInfo($"[ObjectDestroy] destroyed \"{(destroyedName ?? "")}\" at {pos} d={Mathf.Sqrt(bestDistSq).ToString("F1")}");
            return true;
        }

        private static void PruneDestroyDebounce(float now)
        {
            if (_s.DestroyDebounce.Count <= 64) return;
            _destroyDebounceStaleKeys.Clear();
            foreach (var kv in _s.DestroyDebounce)
            {
                if (now - kv.Value >= DestroyDebounceTime || kv.Value > now)
                    _destroyDebounceStaleKeys.Add(kv.Key);
            }
            for (int i = 0; i < _destroyDebounceStaleKeys.Count; i++)
                _s.DestroyDebounce.Remove(_destroyDebounceStaleKeys[i]);
            _destroyDebounceStaleKeys.Clear();
        }

        private static float XzDistSq(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static string FirstSlotType(Inventory inv)
        {
            if (inv?.slots == null || inv.slots.Count == 0) return null;
            InvItemClass c = inv.slots[0].invItem;
            return InvItemClass.isNull(c) ? null : c.type;
        }

        /// <summary>
        /// Lower-case name without Unity's "(Clone)" markers or a trailing " (N)" duplicate index,
        /// so "Mushroom_exp(Clone)", "mushroom_exp (2)" and "mushroom_exp" compare equal.
        /// </summary>
        internal static string NormalizeObjectName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string n = name.Trim().ToLowerInvariant();
            int clone;
            while ((clone = n.IndexOf("(clone)", StringComparison.Ordinal)) >= 0)
                n = n.Remove(clone, 7);
            n = n.Trim();
            // Strip one trailing " (N)" index.
            if (n.Length > 3 && n[n.Length - 1] == ')')
            {
                int open = n.LastIndexOf('(');
                if (open > 0 && open < n.Length - 2)
                {
                    bool digits = true;
                    for (int i = open + 1; i < n.Length - 1; i++)
                    {
                        if (n[i] < '0' || n[i] > '9') { digits = false; break; }
                    }
                    if (digits)
                        n = n.Substring(0, open).Trim();
                }
            }
            return n;
        }

        /// <summary>Exact match on normalized GO name, item type, or the item's display name.</summary>
        private static bool NameOrItemTypeMatches(GameObject go, string itemType, string needleNorm)
        {
            if (go == null || string.IsNullOrEmpty(needleNorm)) return false;
            string n;
            try { n = NormalizeObjectName(go.name); }
            catch { return false; }
            if (n == needleNorm)
                return true;
            if (string.IsNullOrEmpty(itemType))
                return false;
            if (itemType.Equals(needleNorm, StringComparison.OrdinalIgnoreCase))
                return true;
            // Display name "Scrap metal" vs type scrap_metal.
            string needleSpaced = needleNorm.Replace('_', ' ');
            if (itemType.Replace('_', ' ').Equals(needleSpaced, StringComparison.OrdinalIgnoreCase))
                return true;
            try
            {
                string display = Language.Get(itemType + "_name", "Items");
                if (!string.IsNullOrEmpty(display)
                    && (display.Equals(needleNorm, StringComparison.OrdinalIgnoreCase)
                        || display.Equals(needleSpaced, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch { /* Language table may not be ready */ }
            return false;
        }

        private static bool NeedleLooksLikeTrap(string needleLower)
        {
            if (string.IsNullOrEmpty(needleLower)) return false;
            return needleLower.Contains("trap") || needleLower.Contains("bear")
                || needleLower.Contains("snap") || needleLower.Contains("animal")
                || needleLower.Contains("mushroom") || needleLower.Contains("brokenglass")
                || needleLower.Contains("broken_glass");
        }

        /// <summary>
        /// True for a world pickup / harvestable / trap whose name or type matches exactly.
        /// itemInv containers (wardrobes, chests, desks, piles) only qualify when they are a
        /// real dropped-item pickup.
        /// </summary>
        private static bool ShouldDestroyWorldPickup(GameObject root, string needleNorm)
        {
            if (root == null || string.IsNullOrEmpty(needleNorm)) return false;
            string rootName;
            try { rootName = root.name.ToLowerInvariant(); }
            catch { return false; }
            if (rootName.Contains("audioobject"))
                return false;

            // Sprung beartraps keep an Item/Inventory whose slot type is often "junk"
            // (display "Scrap metal"). A junk WorldObjectRemoved must not eat the trap GO
            // — that vanished the trap without a co-op free / grant path.
            bool trapGo = TrapNetworkId.IsWorldTrap(root) || TrapNetworkId.IsOccupancyTrap(root)
                || rootName.Contains("trap") || rootName.Contains("bear") || rootName.Contains("snap");
            if (trapGo && !NeedleLooksLikeTrap(needleNorm))
                return false;

            Item item = root.GetComponent<Item>();
            Inventory inv = root.GetComponent<Inventory>();
            if (inv != null && inv.invType == Inventory.InvType.itemInv)
            {
                if (item == null || !item.isDroppedItem)
                    return false;
                return NameOrItemTypeMatches(root, item.invItem != null ? item.invItem.type : null, needleNorm)
                    || NameOrItemTypeMatches(root, FirstSlotType(inv), needleNorm);
            }

            if (item != null)
                return NameOrItemTypeMatches(root, item.invItem != null ? item.invItem.type : null, needleNorm);

            return NameOrItemTypeMatches(root, null, needleNorm);
        }

        /// <summary>
        /// After a remote peer emptied an itemInv <b>world pickup</b> (shiny stone etc.),
        /// destroy the visual GO if slots are empty.
        /// Furniture containers also use <c>itemInv</c>; destroy only dropped pickups.
        /// Vanilla only auto-destroys emptied <see cref="Item.isDroppedItem"/> pickups.
        /// </summary>
        public static void DestroyEmptyItemInvAt(Vector3 pos)
        {
            Inventory inv = WorldQueryHelper.FindInventoryByPos(pos, 3f);
            if (inv == null || inv.invType != Inventory.InvType.itemInv) return;

            // Wardrobes / chests / desks share itemInv with ground pickups. Only
            // destroy emptied dropped-item pickups (getDroppedItem parity).
            Item item = inv.GetComponent<Item>() ?? inv.GetComponentInParent<Item>();
            if (item == null || !item.isDroppedItem)
                return;

            if (inv.slots != null)
            {
                for (int i = 0; i < inv.slots.Count; i++)
                {
                    if (inv.slots[i] != null && !InvItemClass.isNull(inv.slots[i].invItem))
                        return; // still has loot
                }
            }
            DestroyObjectByPos(inv.transform.position, inv.name);
        }
    }
}
