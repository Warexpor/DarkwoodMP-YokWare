using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// After OutsideLocations loading screens (bunker, village, doctor house, etc.),
    /// re-place remote player proxies and re-announce location membership so peers
    /// can see each other instead of lingering on pre-load world positions.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.transportToLocation))]
    public static class OutsideLocationTransportSettledPatch
    {
        private static void Postfix(string locationName)
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;
                net.OnLocalOutsideLocationSettled(locationName);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] transport settle hook: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Returning to the world map: snap proxies to last known so they are not stuck
    /// at bunker coordinates while the local player is on the forest grid.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.returningOnTeleportedPlayer))]
    public static class OutsideLocationReturnToWorldPatch
    {
        private static void Postfix()
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;
                net.OnLocalReturnedToWorld();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] return-to-world hook: " + ex.Message);
            }
        }
    }
}
