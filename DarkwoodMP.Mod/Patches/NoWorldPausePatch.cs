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

    // ---- UI open/show paths: hold pause suppression for the whole menu ----

    [HarmonyPatch(typeof(Map), "open")]
    [HarmonyPatch(typeof(Journal), "open")]
    [HarmonyPatch(typeof(Journal), "showNote")]
    [HarmonyPatch(typeof(Padlock), "activate")]
    [HarmonyPatch(typeof(DialogueWindow), "SetDialogue")]
    [HarmonyPatch(typeof(SkillPointsMenu), "open")]
    [HarmonyPatch(typeof(SkillSlotsMenu), "open")]
    [HarmonyPatch(typeof(InteractiveItem), "open")]
    internal static class UiOpenNoPausePatches
    {
        private static void Prefix() => PauseSuppression.BeginNoPause();
        private static void Postfix() => PauseSuppression.EndNoPause();
    }

    // ---- UI close/hide paths: hold unpause suppression ----

    [HarmonyPatch(typeof(Map), "close")]
    [HarmonyPatch(typeof(Journal), "close")]
    [HarmonyPatch(typeof(Journal), "hideNote")]
    [HarmonyPatch(typeof(Padlock), "deactivate")]
    [HarmonyPatch(typeof(DialogueWindow), "close")]
    [HarmonyPatch(typeof(SkillPointsMenu), "close")]
    [HarmonyPatch(typeof(SkillSlotsMenu), "close")]
    [HarmonyPatch(typeof(InteractiveItem), "close")]
    internal static class UiCloseNoUnpausePatches
    {
        private static void Prefix() => PauseSuppression.BeginNoUnpause();
        private static void Postfix() => PauseSuppression.EndNoUnpause();
    }

    // ---- Leveling / skill menus (delayed coroutine pause; different body) ----

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
}