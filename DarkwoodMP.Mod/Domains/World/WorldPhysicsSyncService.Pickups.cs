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
        /// <summary>
        /// Finds a world object (mushroom, exp item, shiny stone, etc.) by position and destroys it.
        /// Used when the remote peer reports that they harvested/picked up the object.
        /// </summary>
        public static void DestroyObjectByPos(Vector3 pos, string objectName)
        {
            // AudioObject removal requests are ephemeral sound effects, not actual traps
            if (!string.IsNullOrEmpty(objectName) && objectName.ToLowerInvariant().Contains("audioobject"))
                return;

            // Debounce before scene queries to avoid duplicate removal work.
            // from disarm was repeatedly scanning and then throwing on DestroyImmediate+name.
            int posKey = MakePosNameKey(pos.x, pos.y, pos.z, objectName);
            float now = Time.time;
            if (_destroyDebounce.TryGetValue(posKey, out float lastDestroy)
                && (now - lastDestroy) < DestroyDebounceTime)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[ObjectDestroy] debounced duplicate at " + pos);
                return;
            }

            string needle = string.IsNullOrEmpty(objectName) ? null : objectName.ToLowerInvariant();
            GameObject best = null;
            float bestDistSq = float.MaxValue;
            const float overlapR = 8f;
            const float scanR = 12f;
            float overlapSq = overlapR * overlapR;
            float scanSq = scanR * scanR;

            // 1) OverlapSphere: any nearby root matching name/type OR harvest keywords.
            int nearbyN = OverlapNear(pos, overlapR);
            for (int i = 0; i < nearbyN; i++)
            {
                Collider col = _overlap3D[i];
                if (col == null) continue;
                GameObject root = col.gameObject;
                if (root == null) continue;
                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && rb.gameObject != null) root = rb.gameObject;

                // Prefer Item / itemInv Inventory roots for world pickups (shiny stone, etc.).
                Item item = col.GetComponentInParent<Item>();
                if (item != null && item.gameObject != null) root = item.gameObject;
                else
                {
                    Inventory inv = col.GetComponentInParent<Inventory>();
                    if (inv != null && inv.invType == Inventory.InvType.itemInv && inv.gameObject != null)
                        root = inv.gameObject;
                }

                if (root == null) continue;
                if (!ShouldDestroyWorldPickup(root, needle))
                    continue;

                float dSq = XzDistSq(root.transform.position, pos);
                if (dSq < bestDistSq && dSq <= overlapSq)
                {
                    bestDistSq = dSq;
                    best = root;
                }
            }

            // 2) Scene scan by display name / invItem.type near pos (no collider items).
            // Skip the scene-wide search for known trap names after the overlap
            // query. A missing trap has already been removed.
            bool trapNeedle = needle != null
                && (needle.Contains("trap") || needle.Contains("bear") || needle.Contains("snap"));
            if (best == null && needle != null && !trapNeedle)
            {
                Item[] items = WorldQueryHelper.GetCachedSceneComponents<Item>();
                for (int i = 0; i < items.Length; i++)
                {
                    Item it = items[i];
                    if (it == null) continue;
                    GameObject go = it.gameObject;
                    if (go == null || !go.scene.IsValid()) continue;
                    string itemType = it.invItem != null ? it.invItem.type : null;
                    if (!NameOrItemTypeMatches(go, itemType, needle)) continue;
                    float dSq = XzDistSq(go.transform.position, pos);
                    if (dSq > scanSq) continue;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = go;
                    }
                }

                if (best == null)
                {
                    Inventory[] invs = WorldQueryHelper.GetCachedSceneComponents<Inventory>();
                    for (int i = 0; i < invs.Length; i++)
                    {
                        Inventory inv = invs[i];
                        if (inv == null || inv.invType != Inventory.InvType.itemInv) continue;
                        GameObject go = inv.gameObject;
                        if (go == null || !go.scene.IsValid()) continue;
                        string slotType = FirstSlotType(inv);
                        bool nameOk = NameOrItemTypeMatches(go, slotType, needle);
                        if (!nameOk)
                            continue;
                        float dSq = XzDistSq(go.transform.position, pos);
                        if (dSq > scanSq) continue;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            best = go;
                        }
                    }
                }
            }

            if (best == null)
            {
                // Still claim debounce so follow-up removes of an already-gone trap skip the scan.
                _destroyDebounce[posKey] = now;
                ModRuntime.LegacyInfo("[ObjectDestroy] miss name=\"" + (objectName ?? "") + "\" at " + pos);
                return;
            }

            _destroyDebounce[posKey] = now;

            string destroyedName = objectName;
            try
            {
                if (best != null)
                    destroyedName = best.name;
            }
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
            ModRuntime.LegacyInfo("[ObjectDestroy] destroyed \"" + (destroyedName ?? "") + "\" at " + pos
                + " d=" + Mathf.Sqrt(bestDistSq).ToString("F1"));
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

        private static bool NameOrItemTypeMatches(GameObject go, string itemType, string needleLower)
        {
            if (go == null || string.IsNullOrEmpty(needleLower)) return false;
            string n;
            try { n = go.name.ToLowerInvariant(); }
            catch { return false; }
            string bare = n.Replace("(clone)", "").Trim();
            if (n == needleLower || bare == needleLower)
                return true;
            if (needleLower.Length >= 4 && n.Contains(needleLower))
                return true;
            if (bare.Length >= 4 && needleLower.Contains(bare))
                return true;
            if (!string.IsNullOrEmpty(itemType)
                && itemType.Equals(needleLower, System.StringComparison.OrdinalIgnoreCase))
                return true;
            // Display name "Scrap metal" vs type scrap_metal / scrapMetal
            if (!string.IsNullOrEmpty(itemType))
            {
                string t = itemType.ToLowerInvariant();
                string tSpaced = t.Replace('_', ' ');
                string needleSpaced = needleLower.Replace('_', ' ');
                if (n.Contains(tSpaced) || needleSpaced.Contains(tSpaced) || tSpaced.Contains(needleSpaced))
                    return true;
                // Localized display: Language.Get(type + "_name") == "Scrap metal"
                try
                {
                    string display = Language.Get(itemType + "_name", "Items");
                    if (!string.IsNullOrEmpty(display)
                        && display.Equals(needleLower, System.StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (!string.IsNullOrEmpty(display)
                        && display.ToLowerInvariant() == needleSpaced)
                        return true;
                }
                catch { /* Language table may not be ready */ }
            }
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

        private static bool ShouldDestroyWorldPickup(GameObject root, string needleLower)
        {
            if (root == null) return false;
            string rootName;
            try { rootName = root.name.ToLowerInvariant(); }
            catch { return false; }
            if (rootName.Contains("audioobject"))
                return false;

            if (needleLower == null) return false;

            // Sprung beartraps keep an Item/Inventory whose slot type is often "junk"
            // (display "Scrap metal"). A junk WorldObjectRemoved must not eat the trap GO
            // — that vanished the trap without a co-op free / grant path.
            bool trapGo = TrapNetworkId.IsWorldTrap(root) || TrapNetworkId.IsOccupancyTrap(root)
                || rootName.Contains("trap") || rootName.Contains("bear") || rootName.Contains("snap");
            if (trapGo && !NeedleLooksLikeTrap(needleLower))
                return false;

            Item item = root.GetComponent<Item>() ?? root.GetComponentInParent<Item>();
            if (item != null)
            {
                string t = item.invItem != null ? item.invItem.type : null;
                if (NameOrItemTypeMatches(item.gameObject, t, needleLower))
                    return true;
            }

            Inventory inv = root.GetComponent<Inventory>() ?? root.GetComponentInParent<Inventory>();
            if (inv != null && inv.invType == Inventory.InvType.itemInv
                && NameOrItemTypeMatches(inv.gameObject, FirstSlotType(inv), needleLower))
                return true;

            return NameOrItemTypeMatches(root, null, needleLower);
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
