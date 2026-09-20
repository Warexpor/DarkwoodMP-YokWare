namespace DWMPHorde.Sync
{
    /// <summary>Shared inventory mutation helpers for net apply paths.</summary>
    internal static class InventorySyncUtil
    {
        internal static void SyncItemAmount(Inventory inv, string itemType, int desiredAmount)
        {
            if (inv == null || string.IsNullOrEmpty(itemType))
                return;
            InvItemClass item = inv.getItem(itemType);
            int currentAmount = InvItemClass.isNull(item) ? 0 : item.amount;
            if (currentAmount == desiredAmount) return;
            if (currentAmount > 0)
                item.removeAmount(currentAmount);
            if (desiredAmount > 0)
                inv.addItemType(itemType, desiredAmount);
        }
    }
}
