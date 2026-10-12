using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client: a scripted push (<c>MoveMe</c>) does not move a shared world body here. The
    /// poltergeist night events (<c>CEvent_weird_moveMeWiggle_*_vortex</c>) put a <c>Vortex</c> in
    /// the hideout that gives every nearby item a MoveMe push each half second, a random one in
    /// wiggle mode; GameEvents moveMe steps do the same. The client replays the scene, so the
    /// furniture was shoved on every machine at once, each its own way: the client sent the moves
    /// of its copy as its own pushes, the host's copy followed them, and the two simulations
    /// fought over each piece (jitter, snapping). The host's copy runs the event (also in a
    /// peer's hideout far from the host: nodes near a remote stay active and the physics scan
    /// covers every player), and its motion reaches the client through PhysicsState as any push.
    /// The push is dropped before it does anything; particles and sounds still play here.
    /// A body on the client's own personal pad (its prologue, a solo dream) is its own world and keeps vanilla.
    /// </summary>
    [HarmonyPatch(typeof(MoveMe), "Update")]
    internal static class MoveMeClientPatch
    {
        private static bool Prefix(MoveMe __instance)
        {
            if (!NetGuard.Connected(out LanNetworkManager net) || net.Role != NetworkRole.Client)
                return true;
            if (__instance == null || __instance.GetComponent<Rigidbody>() == null
                || __instance.GetComponent<Item>() == null)
                return true;
            if (PersonalPads.Contains(__instance.transform))
                return true;
            Object.Destroy(__instance);
            return false;
        }
    }
}
