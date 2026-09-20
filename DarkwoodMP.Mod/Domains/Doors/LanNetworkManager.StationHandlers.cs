namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="StationNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal StationNetHandlers StationHandlers { get; private set; }

        private void HandleSawState(SawStateMessage msg)
        {
            StationHandlers.HandleSawState(msg);
        }

        private void TryFlushPendingSawStates()
        {
            StationHandlers.TryFlushPendingSawStates();
        }

        internal void SendSawStatesTo(int targetPlayerId)
        {
            StationHandlers.SendSawStatesTo(targetPlayerId);
        }

        private void HandleFeederState(FeederStateMessage msg)
        {
            StationHandlers.HandleFeederState(msg);
        }

        private void TryFlushPendingFeederStates()
        {
            StationHandlers.TryFlushPendingFeederStates();
        }

        internal void SendFeederStatesTo(int targetPlayerId)
        {
            StationHandlers.SendFeederStatesTo(targetPlayerId);
        }

        private void HandleLureState(LureStateMessage msg)
        {
            StationHandlers.HandleLureState(msg);
        }

        private void TryFlushPendingLureStates()
        {
            StationHandlers.TryFlushPendingLureStates();
        }

        internal void SendLureStatesTo(int targetPlayerId)
        {
            StationHandlers.SendLureStatesTo(targetPlayerId);
        }
    }
}
