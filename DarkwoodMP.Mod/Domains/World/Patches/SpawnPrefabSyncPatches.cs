using DWMPHorde.Logging;
using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host-authoritative SpawnPrefab (scene placeholder → AddPrefab then Destroy).
    /// Vanilla: Start calls Core.AddPrefab(GameObject prefab, …) then Destroy(self).
    /// Independent client Start would duplicate the placed prefab on both peers.
    /// Clients Prefix-skip Start; Offline + Host keep vanilla.
    /// Observation (no new message): Characters / saveables ride existing
    /// EntityStateBroadcast / WorldSaveShare. GameObject-overload AddPrefab is
    /// not the string-path PhysicsSpawnSync hook (same family as
    /// RandomObjectSpawner / ObjectSpawner).
    /// </summary>
    [HarmonyPatch(typeof(SpawnPrefab), "Start")]
    public static class SpawnPrefabStartPatch
    {
        private static bool Prefix(SpawnPrefab __instance)
        {
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Client)
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[SpawnPrefab] client skipped Start (host-authoritative) on "
                        + (__instance != null ? __instance.name : "?"));
                return false;
            }
            return true;
        }
    }
}
