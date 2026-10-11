namespace DWMPHorde.Sync
{
    /// <summary>Shared inventory mutation helpers for net apply paths.</summary>
    internal static class InventorySyncUtil
    {
        internal static void SyncItemAmount(Inventory inv, string itemType, int desiredAmount)
        {
            if (inv == null || string.IsNullOrEmpty(itemType))
                return;
            // The whole stock, every stack of the type: getItem is the first stack only, and a
            // station holding the type in several slots lost the others' count. The difference is
            // applied the way vanilla consumes and adds (first stack first, addItemType stacking),
            // so machines that started from the same layout end on the same one.
            int diff = desiredAmount - inv.getItemAmount(itemType);
            if (diff > 0)
                inv.addItemType(itemType, diff);
            else if (diff < 0)
                inv.removeItemAmount(itemType, -diff);
        }
    }
}
