namespace DWMPHorde.Networking
{
    /// <summary>
    /// The session checks every sync patch starts with, in one place: fetch the network manager
    /// and test the state the caller needs.
    /// </summary>
    internal static class NetGuard
    {
        /// <summary>A session is up with at least one peer.</summary>
        internal static bool Connected(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            return net != null && net.IsConnected;
        }

        /// <summary>Local role is Host (peers may or may not be connected).</summary>
        internal static bool Host(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            return net != null && net.Role == NetworkRole.Host;
        }

        /// <summary>Local role is Host and at least one peer is connected.</summary>
        internal static bool ConnectedHost(out LanNetworkManager net)
        {
            net = ModRuntime.Network;
            return net != null && net.IsConnected && net.Role == NetworkRole.Host;
        }
    }
}
