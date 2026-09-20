namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: dialog apply/drain + close/helpers siblings. Awake wires them.
    /// </summary>
    internal sealed class DialogOutcomeNetHandlers
    {
        private readonly DialogOutcomeApplyNetHandlers _apply;
        private readonly DialogOutcomeCloseNetHandlers _close;

        internal DialogOutcomeNetHandlers(
            DialogOutcomeApplyNetHandlers apply,
            DialogOutcomeCloseNetHandlers close)
        {
            _apply = apply ?? throw new System.ArgumentNullException(nameof(apply));
            _close = close ?? throw new System.ArgumentNullException(nameof(close));
        }

        internal void HandleDialogOutcomeSync(DialogOutcomeSyncMessage msg) =>
            _apply.HandleDialogOutcomeSync(msg);

        internal void HandleDialogTreeState(DialogTreeStateMessage msg) =>
            _apply.HandleDialogTreeState(msg);

        internal void AbortWorldOnlyDrainForRelease() =>
            _apply.AbortWorldOnlyDrainForRelease();

        internal void HostFireNpcCloseDialogue(string npcName) =>
            _close.HostFireNpcCloseDialogue(npcName);

        internal static string StripCloneSuffix(string name) =>
            DialogOutcomeCloseNetHandlers.StripCloneSuffix(name);
    }
}
