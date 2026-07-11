using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Distance-cull world SFX so far-away networked sounds don't spam.
    /// Must NEVER touch menu music / global music / ambience.
    /// </summary>
    [HarmonyPatch(typeof(AudioController), "_PlayAsSound")]
    public static class AudioSuppressionPatch
    {
        private static bool Prefix(string audioID, float volume, Vector3 worldPosition, Transform parentObj, ref AudioObject __result)
        {
            return AudioSuppressionLogic.AllowSound(audioID, worldPosition, parentObj, ref __result);
        }
    }

    /// <summary>
    /// Music and ambience are global — never distance-cull.
    /// (Previously shared SuppressFarAudio and killed menu/BGM tracks.)
    /// </summary>
    [HarmonyPatch(typeof(AudioController), "_PlayAsMusicOrAmbienceSound")]
    public static class AudioAmbienceSuppressionPatch
    {
        private static bool Prefix()
        {
            return true;
        }
    }

    internal static class AudioSuppressionLogic
    {
        /// <summary>
        /// Returns true to allow play, false to suppress.
        /// </summary>
        internal static bool AllowSound(string audioID, Vector3 worldPosition, Transform parentObj, ref AudioObject __result)
        {
            // Global / menu / BGM — never distance-cull (main menu uses Play→_PlayAsSound).
            if (IsNeverCullSound(audioID))
                return true;

            // Title / main menu — no distance culling at all.
            try
            {
                if (Core.mainMenu)
                    return true;
            }
            catch
            {
                // Core not ready
            }

            // Single-player / not connected: do not interfere with vanilla audio.
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;

            Vector3 pos = worldPosition;
            if (pos == Vector3.zero && parentObj != null)
                pos = parentObj.position;

            // 2D / unknown origin — let through
            if (pos == Vector3.zero)
                return true;

            // No local avatar and not actively spectating — cannot judge distance.
            // IMPORTANT: SpectatorModeController.Instance always exists (EnsureExists at boot).
            // Only use it when actually spectating.
            bool hasPlayer = Player.Instance != null;
            bool spectating = SpectatorModeController.Instance != null
                && SpectatorModeController.Instance.IsSpectating;
            if (!hasPlayer && !spectating)
                return true;

            // Spectator: mute SFX parented to the local player body (get-up / corpse).
            // Body is teleported under the follow target so distance cull would wrongly allow it.
            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating && Player.Instance != null)
            {
                Transform localT = Player.Instance.transform;
                if (parentObj != null
                    && (parentObj == localT || parentObj.IsChildOf(localT)))
                {
                    __result = null;
                    return false;
                }
            }

            // Spectator: listen pos is follow target (LocalAudioService.GetListenPosition).
            if (LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return true;

            __result = null;
            return false;
        }

        /// <summary>Menu BGM, UI, and playlist music must never be distance-culled in co-op.</summary>
        internal static bool IsNeverCullSound(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return false;

            // Main menu theme(s) — DW1 is vanilla menu; DW* covers sequels/variants.
            if (audioID.StartsWith("DW", System.StringComparison.OrdinalIgnoreCase)
                && audioID.Length <= 4)
                return true;

            if (audioID.StartsWith("UI_", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (audioID.StartsWith("Music", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (audioID.IndexOf("menu", System.StringComparison.OrdinalIgnoreCase) >= 0
                && audioID.IndexOf("music", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (audioID.Equals("menuMusic", System.StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
