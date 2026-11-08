using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Location.SaveState.loadValues replaces the authored spawn with its saved object ID.
    /// If that ID cannot be resolved, restore the same direct child vanilla's
    /// Location.initializeStuff uses, before transportToLocation dereferences it.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.transportToLocation))]
    public static class OutsideLocationSpawnRepairPatch
    {
        private static void Prefix(OutsideLocations __instance, string locationName)
        {
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected
                || __instance == null || __instance.spawnedLocations == null
                || string.IsNullOrEmpty(locationName)
                || !__instance.spawnedLocations.TryGetValue(locationName, out Location location)
                || location == null || location.playerSpawn != null)
                return;

            Transform spawn = location.transform.Find("PlayerSpawn");
            if (spawn == null)
            {
                ModLog.Warn(LogCat.World, "[LocationSync] missing player spawn and authored PlayerSpawn child for '"
                    + locationName + "'");
                return;
            }

            location.playerSpawn = spawn.gameObject;
            ModLog.Event(LogCat.World, "[LocationSync] restored authored player spawn for '" + locationName + "'");
        }
    }
}
