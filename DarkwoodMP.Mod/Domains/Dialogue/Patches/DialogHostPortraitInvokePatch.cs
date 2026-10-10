using System;
using System.Reflection;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A changePortrait outcome (oven lookAt*, lookKeyhole) hands its portrait swap and the next
    /// board to a 1.5 s <c>Controller.Invoke</c> that reads the window's current <c>npc</c> when it
    /// runs. On the host replaying a peer's board that window is usually closed by then (silent
    /// close nulls <c>npc</c>: a NullReferenceException in the callback), or the host's own talk
    /// has opened it on another NPC, which then got the oven's portrait and a board step. Scheduled
    /// inside a host world-only apply, the callback now runs only while that apply's drain still
    /// holds the window on the same NPC; otherwise only its lasting part is kept, the portrait type
    /// on the NPC the board belonged to.
    /// </summary>
    [HarmonyPatch(typeof(Controller), nameof(Controller.Invoke), new[] { typeof(Action), typeof(float), typeof(bool) })]
    public static class DialogHostPortraitInvokePatch
    {
        private static void Prefix(ref Action act)
        {
            if (act == null || !DialogHostApplyGuard.DialogueApplyActive)
                return;
            MethodInfo m = act.Method;
            object closure = act.Target;
            if (m == null || closure == null || closure.GetType().DeclaringType != typeof(DialogueWindow)
                || m.Name.IndexOf("displayNextBoard", StringComparison.Ordinal) < 0)
                return;
            var outcome = AccessTools.Field(closure.GetType(), "outcome")?.GetValue(closure)
                as CharacterDialogue.Dialogue.Board.Outcome;
            if (outcome == null
                || (outcome.type != CharacterDialogue.Dialogue.Board.Outcome.Type.changePortrait
                    && outcome.type != CharacterDialogue.Dialogue.Board.Outcome.Type.changePortraitWithOverlayAnim))
                return;
            DialogueWindow dw = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
            NPC owner = dw != null ? dw.npc : null;
            Action original = act;
            act = () =>
            {
                DialogueWindow w = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.dialogueWindow : null;
                if (w != null && owner != null && w.npc == owner && DialogHostApplyGuard.DrainPending)
                {
                    original();
                    return;
                }
                if (owner != null)
                {
                    owner.portraitType = outcome.portraitType;
                    if (owner.characterDialogue != null)
                        owner.characterDialogue.portraitType = outcome.portraitType;
                }
                // The callback also gave inputs back 1.1 s after its board; nothing else will.
                if (Player.Instance != null && !Player.Instance.inDialogue)
                    Core.forbidInputs = false;
            };
        }
    }
}
