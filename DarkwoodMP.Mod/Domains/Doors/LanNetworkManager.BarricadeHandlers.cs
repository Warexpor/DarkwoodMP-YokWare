namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="BarricadeNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal BarricadeNetHandlers BarricadeHandlers { get; private set; }

        private void HandleBarricadeEvent(BarricadeEventMessage msg)
        {
            BarricadeHandlers.HandleBarricadeEvent(msg);
        }

        private void TryFlushPendingBarricadeEvents()
        {
            BarricadeHandlers.TryFlushPendingBarricadeEvents();
        }

        private int SendBarricadeDoorsTo(int targetPlayerId, int maxSend = 512)
        {
            return BarricadeHandlers.SendBarricadeDoorsTo(targetPlayerId, maxSend);
        }

        private int SendBarricadeWindowsTo(int targetPlayerId, int maxSend = 512)
        {
            return BarricadeHandlers.SendBarricadeWindowsTo(targetPlayerId, maxSend);
        }

        private int SendBarricadeItemsTo(int targetPlayerId, int maxSend = 512, int maxItems = 256)
        {
            return BarricadeHandlers.SendBarricadeItemsTo(targetPlayerId, maxSend, maxItems);
        }

        private void SendBarricadeStateTo(int targetPlayerId)
        {
            BarricadeHandlers.SendBarricadeStateTo(targetPlayerId);
        }
    }
}
