using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative night mushroom / worm spawn.
    /// Vanilla: <see cref="Location.spawnWorm"/> picks a distant
    /// <see cref="WormsSpawner"/> and calls <c>spawn()</c> (AddPrefab nightMushroom).
    /// Independent client rolls diverge which chunk gets the mushroom.
    /// Clients skip; host owns placement. Peers observe via world item /
    /// chunk saveables and existing physics/item sync — no new message.
    /// </summary>
    [HarmonyPatch(typeof(Location), "spawnWorm")]
    public static class LocationSpawnWormPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[WormsSpawner] client skipped Location.spawnWorm (host-authoritative)");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(WormsSpawner), "spawn")]
    public static class WormsSpawnerSpawnPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[WormsSpawner] client skipped WormsSpawner.spawn (host-authoritative)");
                return false;
            }
            return true;
        }
    }
}
