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

            // We own this grab: kill residual MOS from prior remote motion so native
            // ItemSounds is the only scrape (client was hearing MOS + native = 2×).
            ItemMovingSoundHelper.ClearRemoteScrape(objName);
            MovingObjectSoundService.StopImmediate(objName);
            return true;
        }
    }

    /// <summary>
    /// Releases the drag claim when the local player stops dragging an object.
    /// Called from Item.stopDragging().
    /// Force-stops native scrape immediately and notifies peers the same frame
    /// (do not wait for the 30 Hz PlayerState loop — that was the observer lag vs body-push).
    /// </summary>
    [HarmonyPatch(typeof(Item), "stopDragging")]
    public static class DragClaimStopPatch
    {
        private static void Postfix(Item __instance)
        {
            if (__instance == null) return;

            string objName = __instance.gameObject.name;
            ItemMovingSoundHelper.ForceStop(__instance.gameObject);

            // Drop any host-echo interp lock so the free body is physical again.
            Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(__instance.gameObject);
            Rigidbody rb = __instance.GetComponent<Rigidbody>();
            if (rb != null && rb.isKinematic)
                rb.isKinematic = false;

            var net = ModRuntime.Network as Networking.LanNetworkManager;
            if (net == null || !net.IsConnected) return;

            // Only broadcast end if WE own the claim (don't clear a remote player's claim).
            if (net._dragClaims.TryGetValue(objName, out int claimerId) && claimerId != net.LocalPlayerId)
                return;

            net.NotifyLocalDragEnded(objName);
        }
    }
}
