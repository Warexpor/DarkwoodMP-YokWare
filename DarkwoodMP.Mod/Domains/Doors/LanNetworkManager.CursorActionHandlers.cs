namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="CursorActionNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal CursorActionNetHandlers CursorActionHandlers { get; private set; }

        private void HandleActivateCursorAction(ActivateCursorActionMessage msg)
        {
            CursorActionHandlers.HandleActivateCursorAction(msg);
        }

        private void HandleLocationTransport(LocationTransportMessage msg)
        {
            CursorActionHandlers.HandleLocationTransport(msg);
        }

    }
}
