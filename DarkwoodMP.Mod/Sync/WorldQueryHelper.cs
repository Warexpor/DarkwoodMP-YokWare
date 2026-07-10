using System;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Generic spatial-lookup helpers that replace the repetitive
    /// FindObjectsOfTypeAll + distance-check pattern used in many handlers.
    /// </summary>
    internal static class WorldQueryHelper
    {
        /// <summary>Find the nearest T within maxDist of pos.</summary>
        public static T FindNearest<T>(Vector3 pos, float maxDist) where T : Component
        {
            T best = null;
            float bestDist = maxDist;
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < all.Length; i++)
            {
                T c = all[i];
                if (c == null) continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Find the nearest T whose name matches at pos.</summary>
        public static T FindNearestByName<T>(Vector3 pos, string name, float maxDist) where T : Component
        {
            T best = null;
            float bestDist = maxDist;
            T[] all = Resources.FindObjectsOfTypeAll<T>();
            for (int i = 0; i < all.Length; i++)
            {
                T c = all[i];
                if (c == null) continue;
                if (!c.name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                float d = Vector3.Distance(c.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Find an Inventory by position (OverlapSphere + fallback scan).</summary>
        public static Inventory FindInventoryByPos(Vector3 pos, float maxDist = 10f)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, 1f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Inventory inv = nearby[i].GetComponentInParent<Inventory>();
                if (inv != null && (inv.invType == Inventory.InvType.itemInv || inv.invType == Inventory.InvType.deathDrop))
                    return inv;
            }

            // Fallback: full scan for distant or unsynced inventories
            Inventory best = null;
            float bestDist = maxDist;
            Inventory[] all = UnityEngine.Object.FindObjectsOfType<Inventory>();
            for (int i = 0; i < all.Length; i++)
            {
                Inventory inv = all[i];
                if (inv == null || (inv.invType != Inventory.InvType.itemInv && inv.invType != Inventory.InvType.deathDrop))
                    continue;
                float d = Vector3.Distance(inv.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = inv;
                }
            }
            return best;
        }
    }
}
