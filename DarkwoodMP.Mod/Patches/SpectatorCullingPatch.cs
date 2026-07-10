using DWMPHorde.Spectator;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Redirects WorldGrid.refreshPosition to use the spectated target's position
    /// instead of the player's position when spectator mode is active.
    /// This keeps world objects (cullables, locations) loaded around the camera
    /// rather than around the immobilized local player.
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
    }
}
