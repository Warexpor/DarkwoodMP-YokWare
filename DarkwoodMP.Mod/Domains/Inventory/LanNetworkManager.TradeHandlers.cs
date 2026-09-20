namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin trade façade: delegates to <see cref="TradeNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal TradeNetHandlers TradeHandlers { get; private set; }

        private void HandleTradeSync(TradeSyncMessage msg)
        {
            TradeHandlers.HandleTradeSync(msg);
        }

        private void HandleTradeInventorySync(TradeInventorySyncMessage msg)
        {
            TradeHandlers.HandleTradeInventorySync(msg);
        }

        internal void QueuePendingTradeInventory(TradeInventorySyncMessage msg)
        {
            TradeHandlers.QueuePendingTradeInventory(msg);
        }

        private void TryFlushPendingTradeInventories()
        {
            TradeHandlers.TryFlushPendingTradeInventories();
        }

        internal void SendTradeInventoriesTo(int targetPlayerId)
        {
            TradeHandlers.SendTradeInventoriesTo(targetPlayerId);
        }

        private void HandlePeerHasItem(PeerHasItemMessage msg)
        {
            TradeHandlers.HandlePeerHasItem(msg);
        }
    }
}
