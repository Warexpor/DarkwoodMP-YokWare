using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative RandomSpawnArea (interval prefab near Player).
    /// Vanilla: Start loops spawnPrefab; rolls a prefab and AddPrefab around
    /// <c>Player.Instance</c> when inside radius. Independent client rolls /
    /// local Player distance diverge spawns. Clients Prefix-skip spawnPrefab;
    /// Offline + Host keep vanilla. Observation: spawned Characters/Items ride
    /// existing entity / WorldSaveShare paths — no new message. Host-only is
    /// enough for ownership (Player.Instance distance is host-local).
    /// </summary>
    [HarmonyPatch(typeof(RandomSpawnArea), "spawnPrefab")]
    public static class RandomSpawnAreaSpawnPrefabPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[RandomSpawnArea] client skipped spawnPrefab (host-authoritative)");
                return false;
            }
            return true;
        }
    }
}
