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
            if (net != null && net.Role == NetworkRole.Client && !OnRuntimePad(__instance))
            {
                if (ModRuntime.VerboseLogging)
                    ModLog.Event(LogCat.World,
                        "[SpawnPrefab] client skipped Start (host-authoritative) on "
                        + (__instance != null ? __instance.name : "?"));
                return false;
            }
            return true;
        }

        /// <summary>
        /// A placeholder in a pad scene this game loads itself at run time (a dream, an outside
        /// location such as a cellar or a bunker, a prologue pad), not in the world the client got
        /// from the host's save. Every game builds that pad from the same scene, and the spawn is
        /// the same prefab at the same spot, so each spawns its own. Skipped, the client's pad had
        /// none of them: the bunker dream's tree roots were missing on both clients and the host's
        /// state for them ("ContainerStateSync ... no inventory") found nothing.
        /// </summary>
        private static bool OnRuntimePad(SpawnPrefab sp)
        {
            if (sp == null || Core.loadingGame)
                return false;
            foreach (Location loc in sp.GetComponentsInParent<Location>(true))
            {
                if (loc != null && (loc.isOutsideLocation || loc.isPrologueLocation || loc.isEpilogueLocation))
                    return true;
            }
            return false;
        }
    }
}
