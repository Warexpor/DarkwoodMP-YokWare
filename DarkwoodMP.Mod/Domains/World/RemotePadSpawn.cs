using System;
using System.Collections;
using DWMPHorde.Harmony;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Host: spawn an outside location (cellar, bunker, house pad) a remote player entered, as
    /// vanilla prepareLocation spawns one, minus the host's own transport. Vanilla
    /// createLocation (spawnLocation without transport) is a world-gen helper, not a game path:
    /// <list type="bullet">
    /// <item>without <c>OutsideLocations.loading</c> every CullableObject registers in Awake on
    /// the grid the host stands on (World), and addObjectsToGrid then skips them, so the pad's
    /// props and doors sat in World nodes;</item>
    /// <item>addObjectsToGrid leaves <c>WorldGrid.currentGrid</c> on the pad, so the host's own
    /// world stopped streaming and its next location trip mis-saved its return point;</item>
    /// <item>its A* graph was never scanned, so the pad's AI had no paths.</item>
    /// </list>
    /// </summary>
    internal static class RemotePadSpawn
    {
        /// <summary>Bumped by every local prepareLocation: a local trip owns <c>loading</c> and the grid from then on.</summary>
        private static int _localPrepares; // process-scoped: monotonic counter

        internal static void NoteLocalPrepare() => _localPrepares++;

        internal static void Spawn(OutsideLocations ol, string locationName)
        {
            var controller = Singleton<Controller>.Instance;
            if (ol == null || controller == null || string.IsNullOrEmpty(locationName))
                return;
            controller.StartCoroutine(SpawnRoutine(ol, locationName));
        }

        private static IEnumerator SpawnRoutine(OutsideLocations ol, string locationName)
        {
            WorldGrid wg = Singleton<WorldGrid>.Instance;
            string gridBefore = wg != null && wg.currentGrid != null ? wg.currentGrid.name : null;
            bool loadingBefore = ol.loading;
            int preparesBefore = _localPrepares;
            ol.loading = true;
            try
            {
                yield return ol.StartCoroutine(ol.spawnLocation(locationName, transportAfterSpawn: false));
            }
            finally
            {
                bool localTrip = _localPrepares != preparesBefore;
                if (!localTrip)
                {
                    if (!loadingBefore)
                        ol.loading = false;
                    if (wg != null && !string.IsNullOrEmpty(gridBefore))
                        wg.setGrid(gridBefore);
                }
            }

            ScanGraph(locationName);
            // Door, GameEvents, InventoryRandom and the pad's characters queued their init
            // while loading was set (vanilla onSpawnedLocation runs the same pass).
            try
            {
                var gen = Singleton<WorldGenerator>.Instance;
                if (gen != null && gen.finished)
                    gen.initComponents(_logMessages: false);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] remote pad init: " + ex.Message);
            }
            ModRuntime.LegacyInfo("[LocationSync] remote pad spawned: " + locationName);
        }

        /// <summary>Vanilla spawnLocation's transport branch: scan only this pad's graph.</summary>
        private static void ScanGraph(string locationName)
        {
            try
            {
                if (AstarPath.active == null || AstarPath.active.astarData == null)
                    return;
                var graph = AstarPath.active.astarData.GetGraph(locationName);
                if (graph == null)
                    return; // preset with noPathfinding
                int index = Array.IndexOf(AstarPath.active.graphs, graph);
                if (index >= 0)
                    AstarPath.active.ScanLoop(null, 1 << index);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] remote pad graph scan: " + ex.Message);
            }
        }
    }

    /// <summary>A local location trip (or dream prepare) takes over <c>OutsideLocations.loading</c> and the grid.</summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(OutsideLocations), "prepareLocation")]
    public static class RemotePadSpawnLocalPreparePatch
    {
        private static void Prefix() => RemotePadSpawn.NoteLocalPrepare();
    }
}
