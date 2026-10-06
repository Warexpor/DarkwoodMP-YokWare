namespace DWMPHorde.Sync
{
    /// <summary>
    /// Absolute durability + magazine ammo + shouldBeActive after peer createItem(…, 1f, …).
    /// createItem maps Amount→ammo for hasAmmo firearms; always overwrite ammo when
    /// hasAmmo so empty mags (Ammo=0 on wire) stay empty instead of Amount=1.
    /// Durability on the wire is absolute — assign even when 0 (broken item).
    /// shouldBeActive mirrors ClientStateBackup for flashlight on/off.
    /// </summary>
    internal static class InvItemTransferApply
    {
        internal static void ApplyMeta(InvItemClass item, float durability, int ammo, bool shouldBeActive)
        {
            if (InvItemClass.isNull(item))
                return;
            item.durability = durability;
            if (item.baseClass != null && item.baseClass.hasAmmo)
                item.ammo = ammo;
            item.shouldBeActive = shouldBeActive;
        }
    }
}
