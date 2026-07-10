using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace DarkwoodMP.GameLogic;

/// <summary>
/// Serialises the per-instance state of a dropped item so a dropped weapon
/// keeps its ammo, wear and stat modifiers when mirrored to other players.
///
/// A dropped world <c>Item</c> stores this data in
/// <c>Item.GetComponent&lt;Inventory&gt;().slots[0].invItem</c> (an
/// <see cref="InvItemClass"/>) — verified against
/// <c>Player.spawnDroppedInvItem</c> (writes it via <c>invSlot.createItem</c>)
/// and <c>Item.getDroppedItem</c> (reads exactly that slot back into the
/// player). So we read the state from that slot on the source machine and
/// write it to the same slot on the mirror; whatever the mirror carries flows
/// verbatim to whoever picks it up.
///
/// Durability is stored ABSOLUTE (not a fraction) in the field, so it travels
/// as an absolute value. A value &lt; 0 in the packet is the sentinel for
/// "no per-instance state provided" (thrown flares, item removals, legacy
/// snapshots) and leaves the freshly-created mirror item untouched.
/// </summary>
public static class ItemState
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The <see cref="InvItemClass"/> a dropped world item carries, or null.</summary>
    public static InvItemClass GetSlotItem(Component worldItem)
    {
        if (worldItem == null) return null;
        var inv = worldItem.GetComponent<Inventory>();
        if (inv == null || inv.slots == null || inv.slots.Count == 0) return null;
        return inv.slots[0].invItem;
    }

    /// <summary>Encode an item's stat modifiers as "type|strength|strengthStrength|attach;..." (empty = none).</summary>
    public static string EncodeModifiers(InvItemClass item)
    {
        if (item?.modifiers == null || item.modifiers.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var m in item.modifiers)
        {
            if (m == null) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append((int)m.type).Append('|')
              .Append((int)m.strengthType).Append('|')
              .Append(m.strength.ToString("R", Inv)).Append('|')
              .Append(m.isAttachment ? '1' : '0');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Write transmitted per-instance state onto a mirror item's slot. A
    /// negative <paramref name="durability"/> means the sender carried no
    /// per-instance state, so the mirror is left as the game created it.
    /// </summary>
    public static void Apply(InvItemClass target, float durability, int ammo, int modifierQuality, string modifiers)
    {
        if (target == null || durability < 0f) return;
        try
        {
            target.durability = durability;
            target.ammo = ammo;
            target.modifierQuality = (InvItem.ModifierQuality)modifierQuality;
            ApplyModifiers(target, modifiers);
        }
        catch (Exception ex)
        {
            ModLogger.Warning($"[ItemState] apply failed: {ex.Message}");
        }
    }

    private static void ApplyModifiers(InvItemClass target, string encoded)
    {
        if (target.modifiers == null) target.modifiers = new List<InvItemModifier>();
        // The mirror was just created default (empty modifier list); rebuild it
        // to exactly match the source. An empty field therefore correctly means
        // "no modifiers".
        target.modifiers.Clear();
        if (string.IsNullOrEmpty(encoded)) return;

        foreach (var entry in encoded.Split(';'))
        {
            var f = entry.Split('|');
            if (f.Length != 4) continue;
            if (!int.TryParse(f[0], NumberStyles.Integer, Inv, out var type)) continue;
            if (!int.TryParse(f[1], NumberStyles.Integer, Inv, out var strengthType)) continue;
            float.TryParse(f[2], NumberStyles.Float, Inv, out var strength);
            target.modifiers.Add(new InvItemModifier
            {
                type = (InvItemModifier.Type)type,
                strengthType = (InvItemModifier.Strength)strengthType,
                strength = strength,
                isAttachment = f[3] == "1"
            });
        }
    }
}
