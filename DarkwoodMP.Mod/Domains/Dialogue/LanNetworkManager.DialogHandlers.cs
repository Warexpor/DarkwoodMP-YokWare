namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin dialog façade: delegates to outcome / NPC-lock NetHandlers.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal DialogOutcomeNetHandlers DialogOutcomeHandlers { get; private set; }
        internal DialogOutcomeApplyNetHandlers DialogOutcomeApplyHandlers { get; private set; }
        internal DialogOutcomeCloseNetHandlers DialogOutcomeCloseHandlers { get; private set; }
        internal DialogNpcLockNetHandlers DialogNpcLockHandlers { get; private set; }

        private void HandleDialogOutcomeSync(DialogOutcomeSyncMessage msg)
        {
            DialogOutcomeHandlers.HandleDialogOutcomeSync(msg);
        }

        private void HandleDialogTreeState(DialogTreeStateMessage msg)
        {
            DialogOutcomeHandlers.HandleDialogTreeState(msg);
        }

        private void HandleDialogNpcLock(DialogNpcLockMessage msg)
        {
            DialogNpcLockHandlers.HandleDialogNpcLock(msg);
        }
    }
}
