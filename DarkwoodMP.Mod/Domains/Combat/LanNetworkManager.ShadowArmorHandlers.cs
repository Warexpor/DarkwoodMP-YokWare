namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="ShadowArmorNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal ShadowArmorNetHandlers ShadowArmorHandlers { get; private set; }

        private void HandleShadowArmorState(ShadowArmorStateMessage msg)
        {
            ShadowArmorHandlers.HandleShadowArmorState(msg);
        }

        private void TryFlushPendingShadowArmorStates()
        {
            ShadowArmorHandlers.TryFlushPending();
        }

        internal void SendShadowArmorStatesTo(int targetPlayerId)
        {
            ShadowArmorHandlers.SendShadowArmorStatesTo(targetPlayerId);
        }
    }
}
