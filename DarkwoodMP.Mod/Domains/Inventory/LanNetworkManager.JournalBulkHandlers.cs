namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="JournalNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal JournalNetHandlers JournalHandlers { get; private set; }

        private void HandleOxygenTankStash(OxygenTankStashMessage msg)
        {
            JournalHandlers.HandleOxygenTankStash(msg);
        }

        private void HandleCompressorTankConvert(CompressorTankConvertMessage msg)
        {
            JournalHandlers.HandleCompressorTankConvert(msg);
        }

        private void HandleJournalBulkSync(JournalBulkSyncMessage msg)
        {
            JournalHandlers.HandleJournalBulkSync(msg);
        }

        private void TryFlushPendingJournal()
        {
            JournalHandlers.TryFlushPendingJournal();
        }

        private void SendJournalBulkSync() => SendJournalBulkSyncTo(-1);

        private void SendJournalBulkSyncTo(int targetPlayerId)
        {
            JournalHandlers.SendJournalBulkSyncTo(targetPlayerId);
        }

        private void HandleWorkbenchLock(WorkbenchLockMessage msg)
        {
            JournalHandlers.HandleWorkbenchLock(msg);
        }

        private void HandleWorkbenchLevel(WorkbenchLevelMessage msg)
        {
            JournalHandlers.HandleWorkbenchLevel(msg);
        }

        private void HandleJournalItem(JournalItemMessage msg)
        {
            JournalHandlers.HandleJournalItem(msg);
        }

        private void ApplyWorkbenchLevel(int level)
        {
            JournalHandlers.ApplyWorkbenchLevel(level);
        }

        private void HandleVaultState(VaultStateMessage msg)
        {
            JournalHandlers.HandleVaultState(msg);
        }
    }
}
