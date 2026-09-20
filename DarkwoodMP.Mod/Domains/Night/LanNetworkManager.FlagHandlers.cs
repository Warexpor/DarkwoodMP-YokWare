namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="FlagNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal FlagNetHandlers FlagHandlers { get; private set; }

        private void HandleFlagSync(FlagSyncMessage msg)
        {
            FlagHandlers.HandleFlagSync(msg);
        }

        private void ApplyFlagSyncMessage(FlagSyncMessage msg)
        {
            FlagHandlers.ApplyFlagSyncMessage(msg);
        }

        internal void SendFlagBulkSync() => SendFlagBulkSyncTo(-1);

        internal void SendFlagBulkSyncTo(int targetPlayerId)
        {
            FlagHandlers.SendFlagBulkSyncTo(targetPlayerId);
        }

        private void HandleFlagBulkSync(FlagBulkSyncMessage msg)
        {
            FlagHandlers.HandleFlagBulkSync(msg);
        }

        private void TryFlushPendingFlags()
        {
            FlagHandlers.TryFlushPendingFlags();
        }
    }
}
