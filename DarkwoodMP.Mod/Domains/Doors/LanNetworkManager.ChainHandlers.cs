namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="ChainNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal ChainNetHandlers ChainHandlers { get; private set; }

        private void HandleChainState(ChainStateMessage msg)
        {
            ChainHandlers.HandleChainState(msg);
        }

        private void TryFlushPendingChainStates()
        {
            ChainHandlers.TryFlushPendingChainStates();
        }

        internal void SendChainStatesTo(int targetPlayerId)
        {
            ChainHandlers.SendChainStatesTo(targetPlayerId);
        }
    }
}
