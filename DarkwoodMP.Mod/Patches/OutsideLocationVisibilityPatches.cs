using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

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
                WorldPhysicsSyncService.TryFlushPendingLights();
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
                WorldPhysicsSyncService.TryFlushPendingLights();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] return-to-world hook: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Day-death vanilla order: transportToHome then onPlayerDeath (setGrid World +
    /// leaveAllLocations) but NEVER currentGrid.leave() / refreshPosition — unlike
    /// returnToWorld. Client respawned at hideout on a stale outside-location grid →
    /// blackness. Mirror returningOnTeleportedPlayer grid hygiene + LocationExit.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.onPlayerDeath))]
    public static class OutsideLocationDeathGridHygienePatch
    {
        private static void Postfix(OutsideLocations __instance)
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;

                EnsureWorldGridAtPlayer(__instance);

                if (Player.Instance != null && Player.Instance.whereAmI != null)
                    Player.Instance.whereAmI.checkWhereAmI();

                net.OnLocalReturnedToWorldAfterDeath();
                WorldPhysicsSyncService.TryFlushPendingLights();
                ModRuntime.LegacyInfo(
                    "[LocationSync] death grid hygiene — World grid + refresh + LocationExit");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] death grid hygiene: " + ex.Message);
            }
        }

        internal static void EnsureWorldGridAtPlayer(OutsideLocations ol)
        {
            var wg = Singleton<WorldGrid>.Instance;
            if (wg == null) return;

            if (wg.currentGrid != null
                && !string.Equals(wg.currentGrid.name, "World", System.StringComparison.OrdinalIgnoreCase))
            {
                try { wg.currentGrid.leave(); }
                catch { /* grid may already be tearing down */ }
            }

            wg.setGrid("World");

            if (Player.Instance != null)
            {
                wg.refreshPosition(
                    Player.Instance.transform.position, instant: true, force: true);
            }

            if (ol != null)
            {
                ol.playerInOutsideLocation = false;
                ol.currentLocationName = "";
            }
        }
    }

    /// <summary>
    /// Belt: after transportToHome teleport, force World refresh when MP connected
    /// (covers stale grid even when playerInOutsideLocation was already false).
    /// Night-death suppress path skips transportToHome — this Postfix simply won't run.
    /// </summary>
    [HarmonyPatch(typeof(Player), "transportToHome")]
    public static class DayDeathTransportHomeGridPatch
    {
        private static void Postfix()
        {
            try
            {
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                    return;
                OutsideLocationDeathGridHygienePatch.EnsureWorldGridAtPlayer(
                    Singleton<OutsideLocations>.Instance);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] transportToHome grid: " + ex.Message);
            }
        }
    }
}
