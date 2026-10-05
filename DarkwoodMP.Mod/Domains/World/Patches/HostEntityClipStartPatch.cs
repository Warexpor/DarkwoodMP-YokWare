using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host: every tk2d clip start goes through this overload (Play, PlayFromFrame, PlayFrom).
    /// A creature body switching to another clip is logged so a clip it starts and leaves between
    /// two snapshots (a turn's loop between its start and end, 50-100 ms) still reaches the
    /// clients; the snapshot only samples the clip at send time. Vanilla calls Play every frame
    /// with the clip already running, so the early outs come first.
    /// </summary>
    [HarmonyPatch(typeof(tk2dSpriteAnimator), nameof(tk2dSpriteAnimator.Play),
        typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float))]
    public static class HostEntityClipStartPatch
    {
        private static void Prefix(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip clip)
        {
            if (!EntityStateBroadcastService.ClipTrackingOn || clip == null)
                return;
            // Same clip (running, or a finished once clip vanilla restarts): not a change.
            if (ReferenceEquals(__instance.CurrentClip, clip))
                return;
            EntityStateBroadcastService.NoteClipStart(__instance, clip.name);
        }
    }
}
