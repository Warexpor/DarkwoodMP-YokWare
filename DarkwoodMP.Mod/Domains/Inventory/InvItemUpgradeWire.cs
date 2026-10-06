using System.Collections.Generic;
using DWMPHorde.Networking;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Shared InvItemClass.upgrades (workbench ItemUpgrade names) on the wire.
    /// Mirrors ClientStateBackup MakeItemEntry / ApplyItemUpgrades for death-bag,
    /// ground drop, and container Place/StateSync so peer copies keep damage/dur
    /// modifiers when an upgraded item moves between players.
    /// Wire: byte count (0–32) + count length-prefixed strings.
    /// </summary>
    internal static class InvItemUpgradeWire
    {
        internal const int MaxUpgrades = 32;

        internal static string[] CollectNames(InvItemClass item)
        {
            if (InvItemClass.isNull(item) || item.upgrades == null || item.upgrades.Count == 0)
                return null;
            var list = new List<string>(item.upgrades.Count);
            for (int i = 0; i < item.upgrades.Count; i++)
            {
                ItemUpgrade up = item.upgrades[i];
                if (up != null && !string.IsNullOrEmpty(up.name))
                    list.Add(up.name);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        internal static void Apply(InvItemClass item, string[] names)
        {
            if (InvItemClass.isNull(item) || names == null || names.Length == 0)
                return;
            ItemsDatabase db = Singleton<ItemsDatabase>.Instance;
            if (db == null) return;

            if (item.upgrades == null)
                item.upgrades = new List<ItemUpgrade>();
            else
                item.upgrades.Clear();

            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];
                if (string.IsNullOrEmpty(name)) continue;
                ItemUpgrade upgrade = db.getUpgrade(name);
                if (upgrade != null)
                    item.upgrades.Add(upgrade);
            }
        }

        /// <summary>Always writes at least the count byte (0 when null/empty).</summary>
        internal static void Write(NetWriter w, string[] names)
        {
            int n = names != null ? names.Length : 0;
            if (n > MaxUpgrades) n = MaxUpgrades;
            if (n < 0) n = 0;
            w.Put((byte)n);
            for (int i = 0; i < n; i++)
                w.Put(names[i] ?? "");
        }

        /// <summary>Reads what <see cref="Write"/> wrote; null for an empty list.</summary>
        internal static string[] Read(NetReader r)
        {
            int n = r.GetByte();
            if (n <= 0) return null;
            if (n > MaxUpgrades)
                throw new System.IO.InvalidDataException("upgrade list of " + n + " exceeds " + MaxUpgrades);
            var names = new string[n];
            for (int i = 0; i < n; i++)
                names[i] = r.GetString();
            return names;
        }

        /// <summary>Per-entry trailer after a multi-item IsRecipe block.</summary>
        internal static void WriteMany(NetWriter w, string[][] perItem, int itemCount)
        {
            for (int i = 0; i < itemCount; i++)
            {
                string[] names = perItem != null && i < perItem.Length ? perItem[i] : null;
                Write(w, names);
            }
        }

        /// <summary>Reads what <see cref="WriteMany"/> wrote for <paramref name="itemCount"/> items.</summary>
        internal static string[][] ReadMany(NetReader r, int itemCount)
        {
            if (itemCount <= 0)
                return null;
            var all = new string[itemCount][];
            for (int i = 0; i < itemCount; i++)
                all[i] = Read(r);
            return all;
        }
    }
}
