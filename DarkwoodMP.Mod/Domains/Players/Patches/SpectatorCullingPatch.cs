using DWMPHorde.Spectator;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Redirects WorldGrid.refreshPosition to use the spectated target's position
    /// instead of the player's position when spectator mode is active.
    /// This keeps world objects (cullables, locations) loaded around the camera
    /// rather than around the immobilized local player. A living spectator's body stays
    /// where it stands and can be hurt there, so the nodes around it stay awake too: put
    /// to sleep, the creatures by it stopped (the host's) or never showed their blows (a
    /// client's copies), and the body could not be hit at all.
    /// </summary>
    [HarmonyPatch(typeof(WorldGrid), "refreshPosition", new[] { typeof(Vector3), typeof(bool), typeof(bool) })]
    public static class SpectatorCullingPatch
    {
        private static void Prefix(object[] __args)
        {
            var spec = SpectatorModeController.Instance;
            if (spec == null || !spec.IsSpectating)
                return;

            Vector3? targetPos = spec.FollowTargetPosition;
            if (targetPos.HasValue)
                __args[0] = targetPos.Value;
        }

        private static void Postfix(WorldGrid __instance)
        {
            Vector3? body = SpectatorModeController.Instance?.WatcherBodyPosition;
            if (__instance == null || !body.HasValue)
                return;
            float range = GameplayConstants.EntityActivationRange;
            if (__instance.grids != null)
            {
                for (int g = 0; g < __instance.grids.Count; g++)
                    EnterNear(__instance.grids[g], body.Value, range);
            }
            else
                EnterNear(__instance.currentGrid, body.Value, range);
        }

        private static void EnterNear(WorldGrid.Grid grid, Vector3 pos, float range)
        {
            if (grid == null || grid.nodes == null)
                return;
            var nodes = grid.nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                Vector2 np = nodes[i].position;
                if (Mathf.Abs(pos.x - np.x) <= range && Mathf.Abs(pos.z - np.y) <= range)
                    nodes[i].enter(false);
            }
        }

        /// <summary>A node by a living spectator's body is not put to sleep (see the class summary).</summary>
        internal static bool KeepsNode(WorldGrid.Node node)
        {
            Vector3? body = SpectatorModeController.Instance?.WatcherBodyPosition;
            if (!body.HasValue || node == null)
                return false;
            float range = GameplayConstants.EntityActivationRange;
            return Mathf.Abs(body.Value.x - node.position.x) <= range && Mathf.Abs(body.Value.z - node.position.y) <= range;
        }
    }
}
