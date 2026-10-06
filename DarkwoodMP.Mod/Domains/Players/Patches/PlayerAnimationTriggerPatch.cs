using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Fires immediately when the local player's torso or legs animator begins playing a new clip.
    /// Sends the clip name to the remote peer so the proxy mirrors the exact animation frame-accurate,
    /// catching transient clips (hit, dodge, attack, item switch, reload, etc.) that the 30 Hz
    /// PlayerState snapshot might miss.
    /// </summary>
    [HarmonyPatch(typeof(tk2dSpriteAnimator), "Play", typeof(string))]
    public static class PlayerAnimationTriggerPatch
    {
        /// <summary>The local player's legs animator, looked up once per Player body.</summary>
        private static Player _legsOwner; // process-scoped: cache keyed by the Player it was read from
        private static tk2dSpriteAnimator _legsAnim; // process-scoped: cache keyed by _legsOwner

        private static void Prefix(tk2dSpriteAnimator __instance, string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;

            Player local = Player.Instance;
            if (local == null) return;

            // Only intercept the local player's own animators. Every creature's processAnims
            // calls Play(string) every frame on the host: compare references first, the legs
            // animator is found once per Player (not a Find + GetComponent per call).
            bool isTorso = __instance == local.torsoAnimator;
            bool isLegs = false;
            if (!isTorso)
            {
                if (!ReferenceEquals(_legsOwner, local))
                {
                    _legsOwner = local;
                    Transform legsT = local.transform.Find("PlayerLegs");
                    _legsAnim = legsT != null ? legsT.GetComponent<tk2dSpriteAnimator>() : null;
                }
                isLegs = _legsAnim != null && __instance == _legsAnim;
            }
            if (!isTorso && !isLegs) return;

            // Skip if the animator is already actively playing the requested clip.
            // doAnims() calls Play() every frame — this filters out redundant calls,
            // but allows re-sending when a non-looping clip finishes and restarts
            // (e.g. double barrel reload loop cycles).
            if (__instance.Playing && __instance.CurrentClip?.name == name)
                return;

            net.SendPlayerAnimation(new PlayerAnimationMessage
            {
                // Only send the clip that actually changed. Sending the other
                // animator's current (stale) clip would override the correct
                // state from the 30Hz snapshot (e.g. "LegsRun" when torso
                // just switched to "BeartrapStart").
                TorsoClip = isTorso ? name : null,
                LegsClip = isLegs ? name : null
            });
            EntitySyncLog.Anim("player",
                "[PlayerAnim] TX " + (isTorso ? "torso=" : "legs=") + name, 0.4f);
        }
    }
}
