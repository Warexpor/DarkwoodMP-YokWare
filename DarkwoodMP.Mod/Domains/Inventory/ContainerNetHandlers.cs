namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: container loot / death-drop / pending siblings. Awake wires them.
    /// </summary>
    internal sealed class ContainerNetHandlers
    {
        private readonly ContainerLootNetHandlers _loot;
        private readonly ContainerDeathDropNetHandlers _deathDrop;
        private readonly ContainerPendingNetHandlers _pending;

        internal ContainerNetHandlers(
            ContainerLootNetHandlers loot,
            ContainerDeathDropNetHandlers deathDrop,
            ContainerPendingNetHandlers pending)
        {
            _loot = loot ?? throw new System.ArgumentNullException(nameof(loot));
            _deathDrop = deathDrop ?? throw new System.ArgumentNullException(nameof(deathDrop));
            _pending = pending ?? throw new System.ArgumentNullException(nameof(pending));
        }

        internal void ClearPendingContainerState() => _pending.ClearPendingContainerState();

        internal void RecordPendingContainerRemove(UnityEngine.Vector3 pos, int slotIdx) =>
            _pending.RecordPendingContainerRemove(pos, slotIdx);

        internal void RecordPendingTakePreCount(UnityEngine.Vector3 pos, int slotIdx, int preCount) =>
            _pending.RecordPendingTakePreCount(pos, slotIdx, preCount);

        internal void ClearPendingTakePreCount(UnityEngine.Vector3 pos, int slotIdx) =>
            _pending.ClearPendingTakePreCount(pos, slotIdx);

        internal void HandleHideoutUpgrade(HideoutUpgradeMessage msg) =>
            _loot.HandleHideoutUpgrade(msg);

        internal void HandleContainerItem(ContainerItemMessage msg) =>
            _loot.HandleContainerItem(msg);

        internal void HandleContainerTakeDenied(ContainerTakeDeniedMessage msg) =>
            _loot.HandleContainerTakeDenied(msg);

        internal void HandleContainerStateRequest(ContainerStateRequestMessage msg) =>
            _deathDrop.HandleContainerStateRequest(msg);

        internal void HandleContainerStateSync(ContainerStateSyncMessage msg) =>
            _pending.HandleContainerStateSync(msg);

        internal void HandleReputationSync(ReputationSyncMessage msg) =>
            _loot.HandleReputationSync(msg);
    }
}
