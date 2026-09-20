namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin container façade: delegates to <see cref="ContainerNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal ContainerNetHandlers ContainerHandlers { get; private set; }
        internal ContainerLootNetHandlers ContainerLootHandlers { get; private set; }
        internal ContainerDeathDropNetHandlers ContainerDeathDropHandlers { get; private set; }
        internal ContainerPendingNetHandlers ContainerPendingHandlers { get; private set; }

        private void HandleHideoutUpgrade(HideoutUpgradeMessage msg)
        {
            ContainerHandlers.HandleHideoutUpgrade(msg);
        }

        private void HandleContainerItem(ContainerItemMessage msg)
        {
            ContainerHandlers.HandleContainerItem(msg);
        }

        private void HandleContainerTakeDenied(ContainerTakeDeniedMessage msg)
        {
            ContainerHandlers.HandleContainerTakeDenied(msg);
        }

        private void HandleContainerStateRequest(ContainerStateRequestMessage msg)
        {
            ContainerHandlers.HandleContainerStateRequest(msg);
        }

        private void HandleContainerStateSync(ContainerStateSyncMessage msg)
        {
            ContainerHandlers.HandleContainerStateSync(msg);
        }

        private void HandleReputationSync(ReputationSyncMessage msg)
        {
            ContainerHandlers.HandleReputationSync(msg);
        }
    }
}
