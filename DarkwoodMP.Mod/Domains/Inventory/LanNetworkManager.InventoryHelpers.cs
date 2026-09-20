namespace DWMPHorde.Networking
{
    /// <summary>
    /// Inventory-side helpers kept on <see cref="LanNetworkManager"/> for existing call sites.
    /// Spatial lookups live on <see cref="Sync.WorldQueryHelper"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal static void SyncItemAmount(Inventory inv, string itemType, int desiredAmount)
        {
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
