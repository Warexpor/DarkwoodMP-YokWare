namespace DWMPHorde.Networking
{
    /// <summary>
    /// Thin map façade: delegates to <see cref="MapNetHandlers"/>.
    /// </summary>
    public sealed partial class LanNetworkManager
    {
        internal MapNetHandlers MapHandlers { get; private set; }

        private void HandleMapMarker(MapMarkerMessage msg)
        {
            MapHandlers.HandleMapMarker(msg);
        }

        private void HandleMapMarkerRemove(MapMarkerRemoveMessage msg)
        {
            MapHandlers.HandleMapMarkerRemove(msg);
        }

        private void HandleMapElementDiscovered(MapElementDiscoveredMessage msg)
        {
            MapHandlers.HandleMapElementDiscovered(msg);
        }
    }
}
