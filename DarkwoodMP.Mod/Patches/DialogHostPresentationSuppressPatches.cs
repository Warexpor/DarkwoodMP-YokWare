using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host world-only DialogOutcome replay must not present speaker-only UI:
    /// lookKeyhole_dream changePortrait fades blackScreenTop opaque then schedules
    /// a fade-out after silent close — host stays black forever (0.7.9 soak).
    /// </summary>
    [HarmonyPatch(typeof(UI), nameof(UI.tweenBlackScreen))]
    public static class DialogHostSuppressBlackScreenPatch
    {
        private static bool Prefix(Color _color)
        {
            if (!DialogHostApplyGuard.Active) return true;
            // Allow clearing; block fade-to-black from changePortrait / journal note.
            return _color.a < 0.01f;
        }
    }

    [HarmonyPatch(typeof(UI), nameof(UI.tweenBlackScreenTop))]
    public static class DialogHostSuppressBlackScreenTopPatch
    {
        private static bool Prefix(Color _color)
        {
            if (!DialogHostApplyGuard.Active) return true;
            return _color.a < 0.01f;
        }
    }

    /// <summary>
    /// changePortrait schedules displayNextBoard after silent close nulls currentDialogue.
    /// Block that stale continuation (and any other null-dialogue board advance).
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "displayNextBoard")]
    public static class DialogHostStaleBoardGuardPatch
    {
        private static bool Prefix(DialogueWindow __instance)
        {
            if (__instance == null) return false;
            if (__instance.currentDialogue == null)
                return false;
            return true;
        }

        /// <summary>
        /// World-only host apply: changePortrait sets Core.forbidInputs and relies on a
        /// delayed Invoke to clear it. Silent-close / inactive DialogueWindow cancels that
        /// Invoke → host stuck unable to walk/look/inv. Clear immediately after each board.
        /// </summary>
        private static void Postfix(DialogueWindow __instance)
        {
            if (!DialogHostApplyGuard.Active) return;
            try
            {
                Core.forbidInputs = false;
                Core.cantChangeForbidInputs = false;
                if (__instance != null)
                    __instance.forbidInputs = false;
            }
            catch { /* ignore */ }
        }
    }
}
