using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative ObjectSpawner (interval / loop prefab place).
    /// Vanilla: Start → private spawnObject; rolls randomOffset into destPosition,
    /// AddPooledPrefab or AddPrefab, advances currentId, optionally re-enters when
    /// loop. Independent client RNG offsets diverge placements.
    /// Clients Prefix-skip spawnObject; Offline + Host run vanilla.
    /// Observation (no new message): spawned Characters / Items follow the same
    /// EntityStateBroadcast / WorldSaveShare paths as RandomObjectSpawner.
    /// </summary>
    [HarmonyPatch(typeof(ObjectSpawner), "spawnObject")]
    public static class ObjectSpawnerSpawnObjectPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[ObjectSpawner] client skipped spawnObject (host-authoritative)");
                return false;
            }
            return true;
        }
    }
}
