using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative ObjectPoolSpawner (biome prop place via controller).
    /// Vanilla: Awake registers with ObjectPoolSpawnerController when
    /// randomGeneration; controller later calls spawnObject(name) → tryToSpawn
    /// (RNG radius + AddPrefab GameObject overload + addToSaveable).
    /// Awake registration stays on all peers so host worldgen path still finds
    /// the spawner list; only spawn methods are client-skipped.
    /// Independent client rolls would diverge biome prop placement.
    /// Clients Prefix-skip spawnObject + tryToSpawn; Offline + Host keep vanilla.
    /// Observation (no new message): Characters / saveables ride
    /// EntityStateBroadcast / WorldSaveShare — GameObject AddPrefab is not the
    /// string-path PhysicsSpawnSync hook.
    /// </summary>
    [HarmonyPatch(typeof(ObjectPoolSpawner), "spawnObject")]
    public static class ObjectPoolSpawnerSpawnObjectPatch
    {
        private static bool Prefix(ObjectPoolSpawner __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[ObjectPoolSpawner] client skipped spawnObject (host-authoritative) on "
                        + (__instance != null ? __instance.name : "?"));
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Belt if anything invoked tryToSpawn outside spawnObject (private today).
    /// </summary>
    [HarmonyPatch(typeof(ObjectPoolSpawner), "tryToSpawn")]
    public static class ObjectPoolSpawnerTryToSpawnPatch
    {
        private static bool Prefix(ObjectPoolSpawner __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[ObjectPoolSpawner] client skipped tryToSpawn (host-authoritative) on "
                        + (__instance != null ? __instance.name : "?"));
                return false;
            }
            return true;
        }
    }
}
