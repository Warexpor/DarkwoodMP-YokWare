using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Counted suppression for Core.pause / Core.unpause while multiplayer UI is open.
    /// Host and clients must not freeze Time.timeScale independently (asymmetric world).
    /// Map / journal / padlock / dialogue / leveling / skill menus / interactive item UI.
    /// FreezeTracker (dreams, multiplayer freezes) still pauses intentionally.
    /// </summary>
    internal static class PauseSuppression
    {
        internal static int SuppressPause;
        internal static int SuppressUnpause;

        /// <summary>True when co-op is live — not offline with a dormant network component.</summary>
        internal static bool MultiplayerActive =>
            ModRuntime.Network != null && ModRuntime.Network.IsConnected;

        public static void Reset()
        {
            SuppressPause = 0;
            SuppressUnpause = 0;
        }

        internal static void BeginNoPause()
        {
            if (MultiplayerActive)
                SuppressPause++;
        }

        internal static void EndNoPause()
        {
            if (MultiplayerActive && SuppressPause > 0)
                SuppressPause--;
        }

        internal static void BeginNoUnpause()
        {
            if (MultiplayerActive)
                SuppressUnpause++;
        }

        internal static void EndNoUnpause()
        {
            if (MultiplayerActive && SuppressUnpause > 0)
                SuppressUnpause--;
        }
    }

    /// <summary>Blocks Core.pause during multiplayer non-blocking UI.</summary>
    [HarmonyPatch(typeof(Core), "pause")]
    internal static class CorePauseMultiplayerPatch
    {
        private static bool Prefix()
        {
            if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressPause > 0)
                return false;
            return true;
        }
    }

    /// <summary>Blocks Core.unpause during multiplayer UI; re-pauses if FreezeTracker is active.</summary>
    [HarmonyPatch(typeof(Core), "unpause")]
    internal static class CoreUnpauseMultiplayerPatch
    {
        private static bool Prefix()
        {
            if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressUnpause > 0)
                return false;
            return true;
        }

        private static void Postfix()
        {
            if (!PauseSuppression.MultiplayerActive)
                return;
            if (FreezeTracker.IsFrozen && !Core.Paused)
                Core.pause(keepMusicAndEnviromental: true);
        }
    }

    // ---- Map ----

    [HarmonyPatch(typeof(Map), "open")]
    internal static class MapOpenNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(Map), "close")]
    internal static class MapCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Journal ----

    [HarmonyPatch(typeof(Journal), "open")]
    internal static class JournalOpenNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(Journal), "close")]
    internal static class JournalCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    [HarmonyPatch(typeof(Journal), "showNote")]
    internal static class JournalShowNoteNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(Journal), "hideNote")]
    internal static class JournalHideNoteNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Padlock ----

    [HarmonyPatch(typeof(Padlock), "activate")]
    internal static class PadlockActivateNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(Padlock), "deactivate")]
    internal static class PadlockDeactivateNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Dialogue (SetDialogue is private; Harmony still patches it) ----

    [HarmonyPatch(typeof(DialogueWindow), "SetDialogue")]
    internal static class DialogueSetDialogueNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    /// <summary>Dialogue close path calls Core.unpause — keep world running if still in freeze sources only.</summary>
    [HarmonyPatch(typeof(DialogueWindow), "close")]
    internal static class DialogueCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Leveling / skill menus ----

    /// <summary>
    /// LevelingMenu.show starts a coroutine that later calls Core.pause().
    /// Hold SuppressPause from show until hide so the delayed pause is blocked.
    /// </summary>
    [HarmonyPatch(typeof(LevelingMenu), "show")]
    internal static class LevelingMenuShowNoPausePatch
    {
        private static void Prefix()
        {
            if (PauseSuppression.MultiplayerActive)
                PauseSuppression.SuppressPause++;
        }
    }

    [HarmonyPatch(typeof(LevelingMenu), "hide")]
    internal static class LevelingMenuHideNoUnpausePatch
    {
        private static void Prefix()
        {
            PauseSuppression.BeginNoUnpause();
            // Release hold from show
            if (PauseSuppression.MultiplayerActive && PauseSuppression.SuppressPause > 0)
                PauseSuppression.SuppressPause--;
        }

        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    [HarmonyPatch(typeof(SkillPointsMenu), "open")]
    internal static class SkillPointsMenuOpenNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(SkillPointsMenu), "close")]
    internal static class SkillPointsMenuCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    [HarmonyPatch(typeof(SkillSlotsMenu), "open")]
    internal static class SkillSlotsMenuOpenNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(SkillSlotsMenu), "close")]
    internal static class SkillSlotsMenuCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Interactive item menus (compressor UI, etc.) ----

    [HarmonyPatch(typeof(InteractiveItem), "open")]
    internal static class InteractiveItemOpenNoPausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    [HarmonyPatch(typeof(InteractiveItem), "close")]
    internal static class InteractiveItemCloseNoUnpausePatch
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }
}
