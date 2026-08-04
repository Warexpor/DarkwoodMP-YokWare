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
        /// Also hide dialogue text / force-finish typewriter so host never sees peer lines.
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
                DialogHostPresentation.HideSpeakerVisuals(__instance);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>
    /// World-only displayDialogue turns on the dialogue text root — hide it so peer
    /// boards never appear on the non-speaker host (flags/GE still run).
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), nameof(DialogueWindow.displayDialogue))]
    public static class DialogHostSuppressDialogueTextPatch
    {
        private static void Postfix(DialogueWindow __instance)
        {
            if (!DialogHostApplyGuard.Active) return;
            DialogHostPresentation.HideSpeakerVisuals(__instance);
        }
    }

    internal static class DialogHostPresentation
    {
        internal static void HideSpeakerVisuals(DialogueWindow dw)
        {
            if (dw == null) return;
            try
            {
                if (dw.dialogue != null && dw.dialogue.gameObject != null)
                    dw.dialogue.gameObject.SetActive(false);
                if (dw.options != null && dw.options.gameObject != null)
                    dw.options.gameObject.SetActive(false);
                if (dw.showItems != null && dw.showItems.gameObject != null)
                    dw.showItems.gameObject.SetActive(false);
                if (dw.portrait != null)
                {
                    var r = dw.portrait.GetComponent<Renderer>();
                    if (r != null) r.enabled = false;
                }
            }
            catch { /* ignore */ }

            // Typewriter on inactive parents won't callback — force-finish so board drain moves.
            try
            {
                if (dw.currentBoardElements == null) return;
                for (int i = 0; i < dw.currentBoardElements.Count; i++)
                {
                    Transform t = dw.currentBoardElements[i];
                    if (t == null) continue;
                    var wt = t.GetComponent<WritingText>();
                    if (wt != null)
                        wt.forceFinish();
                }
            }
            catch { /* ignore */ }
        }
    }
}
