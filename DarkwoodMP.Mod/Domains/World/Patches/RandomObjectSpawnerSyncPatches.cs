using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative RandomObjectSpawner (world loot / NPC RNG).
    /// Vanilla: Awake queues on WorldGenerator (randomGeneration) or calls
    /// spawnObject; spawnObject rolls probability + picks a prefab, then
    /// tryToSpawn (Character enableComponents / Item via AddPrefab + addToSaveable).
    /// Independent client rolls diverge which objects appear.
    /// Clients Prefix-skip spawnObject; Offline + Host run vanilla.
    /// Pre-handshake Offline may still spawn on both machines — world-share /
    /// campaign load reconciles; once Role is Client, skips.
    /// Observation (no new message):
    /// - Characters: host EntityStateBroadcast + client pending match /
    ///   SpawnEntityLocally (PrefabPath on snapshot).
    /// - Items / saveables: typically Awake/worldgen before peers — ride
    ///   WorldSaveShare / save load. Object AddPrefab is not the string-path
    ///   PhysicsSpawnSync hook; mid-session re-spawn is gated by
    ///   alreadySpawnedObjects.
    /// </summary>
    [HarmonyPatch(typeof(RandomObjectSpawner), "spawnObject")]
    public static class RandomObjectSpawnerSpawnObjectPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[RandomObjectSpawner] client skipped spawnObject (host-authoritative)");
                return false;
            }
            return true;
        }
    }
}
