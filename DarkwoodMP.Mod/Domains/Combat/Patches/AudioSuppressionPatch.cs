using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Spectator;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;
using DWMPHorde.Harmony;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Distance-cull world SFX so far-away networked sounds don't spam.
    /// Must NEVER touch menu music / global music / ambience.
    /// </summary>
    [OptionalPatch]
    [HarmonyPatch(typeof(AudioController), "_PlayAsSound")]
    public static class AudioSuppressionPatch
    {
        private static bool Prefix(string audioID, float volume, Vector3 worldPosition, Transform parentObj, ref AudioObject __result)
        {
            return AudioSuppressionLogic.AllowSound(audioID, worldPosition, parentObj, ref __result);
        }
    }

    /// <summary>
    /// Music and ambience are global; never distance-cull them.
    /// (Previously shared SuppressFarAudio and killed menu/BGM tracks.)
    /// </summary>
    internal static class AudioSuppressionLogic
    {
        /// <summary>
        /// Returns true to allow play, false to suppress.
        /// </summary>
        internal static bool AllowSound(string audioID, Vector3 worldPosition, Transform parentObj, ref AudioObject __result)
        {
            // Global / menu / BGM; never distance-cull (main menu uses Play -> _PlayAsSound).
            if (IsNeverCullSound(audioID))
                return true;

            // Title / main menu — no distance culling at all.
            try
            {
                if (GameScreen.AtTitle)
                    return true;
            }
            catch
            {
                // Core not ready
            }

            // Single-player / not connected: do not interfere with vanilla audio.
            if (!NetGuard.Connected(out var net))
                return true;

            Vector3 pos = worldPosition;
            if (pos == Vector3.zero && parentObj != null)
                pos = parentObj.position;

            // 2D or unknown origin; let through.
            if (pos == Vector3.zero)
                return true;

            // No local avatar and not actively spectating; cannot judge distance.
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

            // Each sound is culled only where vanilla would not reach this listener. The host
            // keeps areas around remote players awake, so far sounds there are not played.
            if (LocalAudioService.IsSpatialLoop(audioID))
                return true;

            // Peer proxy SFX: the stand-in's position, XZ + exit band so spatial rolloff can fade
            // without Play flicker at the edge.
            bool peerSound = parentObj != null
                && parentObj.GetComponentInParent<DWMPHorde.Players.RemotePlayerProxy>() != null;
            if (peerSound)
                pos = parentObj.position;
            // Spectator: listen pos is follow target (LocalAudioService.GetListenPosition).
            // A world sound: a 3D one within its own carry; a fully 2D one (a redneck's idle mutter,
            // a dog's whimper) from anything vanilla keeps awake around the player, at full volume.
            // Capping 2D at the peer range muted these past 650 units, which vanilla never does.
            // A peer's own sound keeps the peer range.
            bool audible = peerSound
                ? LocalAudioService.IsNearListenerPeerBand(pos, LocalAudioService.AudibleRange(audioID))
                : LocalAudioService.WorldSoundAudible(audioID, pos);
            if (audible)
                return true;

            LogCreatureCull(audioID, pos, LocalAudioService.AudibleRange(audioID), parentObj);
            __result = null;
            return false;
        }

        /// <summary>A creature's sound refused here (the play call returns null): rate-limited trace.</summary>
        private static void LogCreatureCull(string audioID, Vector3 pos, float range, Transform parentObj)
        {
            if (parentObj == null || !EntitySyncLog.On)
                return;
            Character c = parentObj.GetComponentInParent<Character>();
            if (c == null)
                return;
            EntitySyncLog.Reaction("cull:" + audioID,
                () => "[AudioCull] " + audioID + " on " + (c.name ?? "") + " d="
                    + LocalAudioService.DistanceToListenerXz(pos).ToString("F0")
                    + " range=" + range.ToString("F0"), 2f);
        }

        /// <summary>Menu BGM, UI, and playlist music must never be distance-culled in co-op.</summary>
        internal static bool IsNeverCullSound(string audioID)
        {
            if (string.IsNullOrEmpty(audioID))
                return false;

            // Main menu themes. DW1 is the vanilla menu; DW* covers sequels and variants.
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
