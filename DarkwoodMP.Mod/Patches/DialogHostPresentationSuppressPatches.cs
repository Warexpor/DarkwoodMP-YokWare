using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host world-only DialogOutcome replay must not present speaker-only UI:
    /// lookKeyhole_dream changePortrait fades blackScreenTop opaque then schedules
    /// a fade-out after silent close — host stays black forever (0.7.9 soak).
    /// Oven lookAt* / keyhole also re-enable the portrait renderer via delayed
    /// setPortrait after guard ends on dialog Release — sticky suppress covers that.
    /// </summary>
    [HarmonyPatch(typeof(UI), nameof(UI.tweenBlackScreen))]
    public static class DialogHostSuppressBlackScreenPatch
    {
        private static bool Prefix(Color _color)
        {
            if (!DialogHostPresentation.ShouldSuppress) return true;
            // Allow clearing; block fade-to-black from changePortrait / journal note.
            return _color.a < 0.01f;
        }
    }

    [HarmonyPatch(typeof(UI), nameof(UI.tweenBlackScreenTop))]
    public static class DialogHostSuppressBlackScreenTopPatch
    {
        private static bool Prefix(Color _color)
        {
            if (!DialogHostPresentation.ShouldSuppress) return true;
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
            if (!DialogHostPresentation.ShouldSuppress) return;
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
        private static bool Prefix(DialogueWindow __instance)
        {
            if (DialogHostApplyGuard.OneShotBoardActive && !DialogHostApplyGuard.DestDrainActive)
                return false;
            if (!DialogHostPresentation.ShouldSuppress) return true;
            DialogHostPresentation.HideSpeakerVisuals(__instance);
            return true;
        }

        private static void Postfix(DialogueWindow __instance)
        {
            if (!DialogHostPresentation.ShouldSuppress) return;
            DialogHostPresentation.HideSpeakerVisuals(__instance);
        }
    }

    /// <summary>
    /// changePortrait's delayed setPortrait re-enables the portrait renderer after
    /// SilentClose / EndWorldOnly — keep it scrubbed while sticky suppress is armed.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "setPortrait")]
    public static class DialogHostSuppressSetPortraitPatch
    {
        private static void Postfix(DialogueWindow __instance)
        {
            if (!DialogHostPresentation.ShouldSuppress) return;
            DialogHostPresentation.HideSpeakerVisuals(__instance);
        }
    }

    internal static class DialogHostPresentation
    {
        /// <summary>
        /// Survives EndWorldOnly until ScrubAndDisarm — pending changePortrait Invokes
        /// otherwise flash portrait/video on the non-speaker after dialog Release abort.
        /// </summary>
        private static bool _stickySuppress;

        public static bool ShouldSuppress =>
            _stickySuppress || DialogHostApplyGuard.Active;

        public static void ArmStickySuppress()
        {
            _stickySuppress = true;
        }

        public static void ScrubAndDisarm(DialogueWindow dw)
        {
            HideSpeakerVisuals(dw);
            ClearBlackScreens();
            _stickySuppress = false;
        }

        public static void Reset()
        {
            _stickySuppress = false;
        }

        internal static void HideSpeakerVisuals(DialogueWindow dw)
        {
            if (dw == null) return;
            try
            {
                // Oven lookAt* / keyhole: background is the full-screen dialogue backdrop —
                // HideSpeakerVisuals previously left it enabled → host saw peer overlays.
                if (dw.background != null)
                {
                    var br = dw.background.GetComponent<Renderer>();
                    if (br != null) br.enabled = false;
                }
                if (dw.inventoryBackground != null && dw.inventoryBackground.gameObject != null)
                    dw.inventoryBackground.gameObject.SetActive(false);
                if (dw.trading != null && dw.trading.gameObject != null)
                    dw.trading.gameObject.SetActive(false);

                if (dw.dialogue != null)
                {
                    try { dw.dialogue.DestroyChildren(); } catch { /* ignore */ }
                    if (dw.dialogue.gameObject != null)
                        dw.dialogue.gameObject.SetActive(false);
                }
                if (dw.options != null)
                {
                    try { dw.options.DestroyChildren(); } catch { /* ignore */ }
                    if (dw.options.gameObject != null)
                        dw.options.gameObject.SetActive(false);
                }
                if (dw.showItems != null && dw.showItems.gameObject != null)
                    dw.showItems.gameObject.SetActive(false);

                if (dw.portrait != null)
                {
                    try
                    {
                        var vp = dw.portrait.GetComponent<VideoPlayer>();
                        if (vp != null)
                        {
                            if (vp.isPlaying) vp.Stop();
                            vp.clip = null;
                        }
                    }
                    catch { /* ignore */ }

                    var r = dw.portrait.GetComponent<Renderer>();
                    if (r != null) r.enabled = false;
                }
            }
            catch { /* ignore */ }

            try
            {
                if (dw.currentBoardElements != null)
                    dw.currentBoardElements.Clear();
            }
            catch { /* ignore */ }
        }

        internal static void ClearBlackScreens()
        {
            try
            {
                var ui = Singleton<UI>.Instance;
                if (ui == null) return;
                ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0f);
                ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0f);
                ForceClearSpriteAlpha(ui.blackScreen != null ? ui.blackScreen.transform : null);
                ForceClearSpriteAlpha(ui.blackScreenTop != null ? ui.blackScreenTop.transform : null);
            }
            catch { /* ignore */ }
        }

        private static void ForceClearSpriteAlpha(Transform t)
        {
            if (t == null) return;
            var spr = t.GetComponent<tk2dBaseSprite>();
            if (spr == null) return;
            Color c = spr.color;
            c.a = 0f;
            spr.color = c;
        }
    }
}
