using DWMPHorde.Networking;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Workbench exclusive-lock stub. Wire message ID 119 remains; grant/broadcast paths removed.
    /// </summary>
    public static class WorkbenchOpenLock
    {
        public static void Reset()
        {
            // Feature disabled; no lock map to clear.
        }

        /// <summary>Host disconnect hook — no-op while exclusive lock is disabled.</summary>
        public static void HostReleaseAllForPlayer(LanNetworkManager net, int playerId)
        {
            // Feature disabled.
        }
    }
}
