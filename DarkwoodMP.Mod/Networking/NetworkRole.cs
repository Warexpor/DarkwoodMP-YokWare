namespace DWMPHorde.Networking
{
    /// <summary>
    /// Defines the networking role of the current game instance.
    /// </summary>
    public enum NetworkRole
    {
        /// <summary>No network session active — single-player only.</summary>
        Offline,
        /// <summary>Acts as the host/server for a LAN session.</summary>
        Host,
        /// <summary>Connected as a client to a remote host.</summary>
        Client
    }
}
