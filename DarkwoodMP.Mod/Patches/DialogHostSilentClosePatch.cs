using System;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host world-only dialog apply must not run vanilla DialogueWindow.close side effects:
    /// black-screen fade + autosave → SaveSync (Saving UI) for every peer.
    /// Client choice spam was closing after each displayDialogue; host's own talk never does.
    /// Still honours startDream (wantToDream + dreamToStart) without fade/save.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "close")]
    public static class DialogHostSilentClosePatch
    {
        private static bool Prefix(DialogueWindow __instance)
        {
            if (!DialogHostApplyGuard.Active)
                return true;
            if (__instance == null)
                return false;

            try
            {
                SilentCloseAfterWorldApply(__instance);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DialogOutcome] silent close failed: " + ex.Message);
            }

            return false;
        }

        /// <summary>
        /// Tear down apply state without fade, Save, forbidInputs, or onCloseDialogue triggers.
        /// </summary>
        internal static void SilentCloseAfterWorldApply(DialogueWindow dw)
        {
            if (dw == null) return;

            // startDream outcome sets wantToDream + dreamToStart then calls close().
            // Vanilla schedules prepareDream from close — keep that without fade/save.
            try
            {
                var dreams = Dreams.Instance;
                if (dreams != null && dreams.wantToDream && !string.IsNullOrEmpty(dw.dreamToStart)
                    && !dreams.dreaming && !dreams.dreamPrepared)
                {
                    string preset = dw.dreamToStart;
                    dw.dreamToStart = "";
                    if (Singleton<Controller>.Instance != null)
                    {
                        Singleton<Controller>.Instance.Invoke(delegate
                        {
                            if (Dreams.Instance != null && !Dreams.Instance.dreaming)
                                Dreams.Instance.StartCoroutine(Dreams.Instance.prepareDream(preset));
                        }, 0.1f, timeScaleDependent: false);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogOutcome] silent dream handoff: " + ex.Message);
            }

            dw.displayingDialogue = false;
            dw.currentDialogue = null;
            dw.dreamToStart = "";
            dw.wantToCook = false;
            dw.dontSaveOnExit = true;
            dw.dontTweenBlackScreenWhenExiting = true;
            dw.tweening = false;

            // changePortrait sets forbidInputs + schedules black fade; clear both so the
            // non-speaker host is not left locked/black after world-only apply.
            try
            {
                Core.forbidInputs = false;
                Core.cantChangeForbidInputs = false;
                var ui = Singleton<UI>.Instance;
                if (ui != null)
                {
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0f);
                    ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0f);
                }
            }
            catch { /* non-fatal */ }

            try
            {
                if (dw.currentBoardElements != null)
                    dw.currentBoardElements.Clear();
            }
            catch { /* ignore */ }

            // Only clear NPC pointer if host is not in a real conversation UI.
            // World-only apply never went through initiateDialogue/onTweenOpen.
            bool hostReallyTalking = Player.Instance != null && Player.Instance.inDialogue && dw.opened;
            if (!hostReallyTalking)
            {
                dw.npc = null;
                if (Player.Instance != null && Player.Instance.talkedToNPC != null)
                    Player.Instance.talkedToNPC = null;

                if (dw.gameObject != null && dw.gameObject.activeSelf && !dw.opened)
                    dw.gameObject.SetActive(false);
            }

            ModRuntime.LegacyInfo("[DialogOutcome] silent host close (no fade/save/SaveSync)");
        }
    }
}
