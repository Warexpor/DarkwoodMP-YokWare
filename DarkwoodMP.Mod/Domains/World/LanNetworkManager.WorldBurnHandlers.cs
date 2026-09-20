namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="WorldBurnNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal WorldBurnNetHandlers WorldBurnHandlers { get; private set; }

        private void HandleWorldBurnState(WorldBurnStateMessage msg)
        {
            WorldBurnHandlers.HandleWorldBurnState(msg);
        }

        private void TryFlushPendingWorldBurnStates()
        {
            WorldBurnHandlers.TryFlushPending();
        }

        internal void SendWorldBurnStatesTo(int targetPlayerId)
        {
            WorldBurnHandlers.SendWorldBurnStatesTo(targetPlayerId);
        }
    }
}
