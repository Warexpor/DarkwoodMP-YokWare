namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin façade: delegates to <see cref="DoorNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal DoorNetHandlers DoorHandlers { get; private set; }

        private void HandleDoorOpen(DoorOpenMessage msg)
        {
            DoorHandlers.HandleDoorOpen(msg);
        }

    }
}
