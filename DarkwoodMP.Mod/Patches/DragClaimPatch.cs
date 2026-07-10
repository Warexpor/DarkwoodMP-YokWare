using DWMPHorde.Audio;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Prevents a player from starting to drag an object that is already
    /// being dragged by another player (drag claim system).
    ///
    /// Item.startDragging() calls Player.Instance.startDragging(this)
    /// which sets Player.dragging = true and attaches the hinge joint.
    /// We block startDragging() early so the local player never enters
    /// the drag state for a claimed object.
    /// </summary>
    [HarmonyPatch(typeof(Item), "startDragging")]
    public static class DragClaimStartPatch
    {
        private static bool Prefix(Item __instance)
        {
            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net == null) return true;

            if (Player.Instance != null && Player.Instance.dragging && Player.Instance.itemBeingDragged == __instance)
                return true;

            string objName = __instance.gameObject.name;

            if (net.IsDragClaimedByOther(objName, net.LocalPlayerId) ||
                net._remoteDragItemNames.Contains(objName))
            {
                Player.Instance?.displayMessage("This object is already being moved by another player");
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Releases the drag claim when the local player stops dragging an object.
    /// Called from Item.stopDragging().
    /// Also force-stops native scrape: residual velocity after hinge release is
    /// the main reason scrape hangs ~1s after the player lets go.
    /// </summary>
    [HarmonyPatch(typeof(Item), "stopDragging")]
    public static class DragClaimStopPatch
    {
        private static void Postfix(Item __instance)
        {
            if (__instance != null)
                ItemMovingSoundHelper.ForceStop(__instance.gameObject);

            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net == null) return;

            string objName = __instance.gameObject.name;
            // Only clear if WE own the claim (don't clear a remote player's claim)
            if (net._dragClaims.TryGetValue(objName, out int claimerId) && claimerId == net.LocalPlayerId)
                net._dragClaims.Remove(objName);
        }
    }
}
