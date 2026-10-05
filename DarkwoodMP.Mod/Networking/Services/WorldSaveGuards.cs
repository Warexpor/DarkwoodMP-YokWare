using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// When the local world may be written to the profile. The manual F3 save and every automatic
    /// host save (leave checkpoint, SaveSync apply) ask the same question here.
    /// </summary>
    internal static class WorldSaveGuards
    {
        private static bool _quitHooked; // process-scoped: one-time Application.quitting hook

        /// <summary>The application is shutting down: scene objects are being torn down under us.</summary>
        internal static bool IsQuitting { get; private set; }

        /// <summary>Subscribe once to <see cref="Application.quitting"/>. Safe to call repeatedly.</summary>
        internal static void EnsureQuitHook()
        {
            if (_quitHooked)
                return;
            _quitHooked = true;
            try { Application.quitting += NoteQuitting; }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Save, "Application.quitting hook failed: " + ex.Message);
            }
        }

        /// <summary>Also fed by a MonoBehaviour OnApplicationQuit, which runs before any OnDestroy.</summary>
        internal static void NoteQuitting()
        {
            if (IsQuitting)
                return;
            IsQuitting = true;
            ModLog.Event(LogCat.Save, "Application quitting — automatic world saves disabled");
        }

        /// <summary>
        /// Why the world must not be saved right now (any save, manual or automatic), or null.
        /// A partial night death or a running dream would persist a state the session will undo.
        /// </summary>
        internal static string GetWorldSaveBlockReason()
        {
            if (DeathStateTracker.LocalNightDeath && !DeathStateTracker.AllDeadAtNight)
                return "partial night death";
            if (NightDeathSavePatch.IsHeld())
                return "night death held";
            if (DreamSession.IsActive || DreamSyncManager.IsLocalDreamActive)
                return "dream session";
            // Vanilla never saves in the prologue: a save there loads as a broken prologue.
            if (PersonalPrologue.LocalInPrologue)
                return "prologue";
            return null;
        }

        /// <summary>
        /// Why an automatic host save (leave checkpoint, SaveSync apply) must not run, or null.
        /// Adds the cases where only the user, never the mod, may decide to write the slot.
        /// </summary>
        internal static string GetAutomaticHostSaveBlockReason(LanNetworkManager net)
        {
            if (IsQuitting)
                return "application quitting";
            if (net != null && net.IsPromotedHost)
                return "promoted host (survivor world is not authoritative)";
            if (ChapterTransitionHelpers.IsChapterTransitionActive)
                return "chapter transition";
            return GetWorldSaveBlockReason();
        }
    }
}

namespace DWMPHorde.Patches
{
    internal static partial class ChapterTransitionHelpers
    {
        /// <summary>
        /// A chapter change is in flight: the host already wrote the new chapter save and is sharing
        /// it, collecting acks, or tearing the scene. The live scene is the outgoing chapter.
        /// </summary>
        internal static bool IsChapterTransitionActive =>
            _chapterLoadPending || _hostAckCollecting || _hostCommitWaitRunning;
    }
}
