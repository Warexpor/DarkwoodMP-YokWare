using System;
using DWMPHorde;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Dialog outcome apply: host world-only drain and release abort (split for size).</summary>
    internal sealed partial class DialogOutcomeApplyNetHandlers
    {
        internal void HostFinishDialogWorldApply(NPC npc)
        {
            try { DWMPHorde.Patches.DialogueDoorAftermath.OnHostDialogWorldApplied(); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogOutcome] door poll: " + ex.Message);
            }

            // force: still under ProcessInboundMessage NetworkApplyGuard after world-only End.
            try { DWMPHorde.Sync.DialogTreeSync.TryBroadcastFromNpc(npc, force: true); }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogOutcome] tree flush: " + ex.Message);
            }

            // Client often exits while lookKeyhole drain is still running; replay close afterward.
            if (_pendingCloseDialogueNpc.IsValid)
            {
                NpcRef pending = _pendingCloseDialogueNpc;
                _pendingCloseDialogueNpc = default;
                _close.HostFireNpcCloseDialogue(pending);
            }
        }

        /// <summary>
        /// Keep DialogHostApplyGuard up while changePortrait / multi-board chains finish,
        /// force-advancing stalled WritingText boards, then silent-close.
        /// </summary>
        internal System.Collections.IEnumerator HostDrainWorldOnlyDialogue(DialogueWindow dw, NPC npc)
        {
            float deadline = Time.realtimeSinceStartup + 8f;
            int lastBoard = int.MinValue;
            int stallTicks = 0;
            bool hostTookWindow = false;
            try
            {
                while (dw != null
                    && DialogHostApplyGuard.DrainPending
                    && dw.displayingDialogue
                    && dw.currentDialogue != null
                    && Time.realtimeSinceStartup < deadline)
                {
                    // The host opened its own conversation with another NPC on the shared window: stop
                    // driving it (no force-advance, no close), the window is the host's now.
                    if (HostOwnsWindow(dw, npc))
                    {
                        hostTookWindow = true;
                        break;
                    }
                    int board = -1;
                    try { board = Traverse.Create(dw).Field("currentBoard").GetValue<int>(); }
                    catch { board = -1; }

                    if (board == lastBoard)
                    {
                        stallTicks++;
                        // changePortrait waits ~1.5s + setPortrait; WritingText can stall forever
                        // with no host UI clicks. Force advance after a short stall on the same board.
                        if (stallTicks >= 25)
                        {
                            try
                            {
                                Core.forbidInputs = false;
                                if (dw.currentDialogue == null || dw.npc == null)
                                    break;
                                // displayNextBoard is private, so use Traverse only while dialogue is live
                                // (bypassing guard on null caused listen_dream NRE + stuck inputs).
                                bool pushed = DialogHostApplyGuard.BeginDrainScope();
                                try { Traverse.Create(dw).Method("displayNextBoard").GetValue(); }
                                finally { DialogHostApplyGuard.EndDrainScope(pushed); }
                            }
                            catch (Exception ex)
                            {
                                if (ModRuntime.VerboseLogging)
                                    ModRuntime.Log?.LogWarning("[DialogOutcome] drain advance: " + ex.Message);
                                break;
                            }
                            stallTicks = 0;
                            lastBoard = int.MinValue;
                        }
                    }
                    else
                    {
                        lastBoard = board;
                        stallTicks = 0;
                    }

                    yield return new WaitForSecondsRealtime(0.1f);
                }

                if (dw != null && DialogHostApplyGuard.DrainPending && !hostTookWindow && !HostOwnsWindow(dw, npc))
                {
                    bool pushed = DialogHostApplyGuard.BeginDrainScope();
                    try
                    {
                        if (dw.displayingDialogue || dw.npc != null)
                            dw.close(); // DialogHostSilentClosePatch while the scope is Active
                        else
                            DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                    }
                    finally { DialogHostApplyGuard.EndDrainScope(pushed); }
                }
            }
            finally
            {
                _drainDoneGeneration = _drainGeneration;
                _dialogWorldDrainCo = null;
                _drainNpc = null;
                // The drain holds no guard scope across its waits (each advance / close
                // re-entered and left it); an enclosing caller's scope is not ours to end.
                DialogHostApplyGuard.EndDrain();
                // The input lock belongs to the host's own conversation if it has one.
                if (!(dw != null && HostOwnsWindow(dw, npc)))
                {
                    try
                    {
                        Core.forbidInputs = false;
                        Core.cantChangeForbidInputs = false;
                        if (dw != null)
                            dw.forbidInputs = false;
                    }
                    catch { /* ignore */ }
                }
                HostFinishDialogWorldApply(npc);
                ModRuntime.LegacyInfo("[DialogOutcome] world-only drain finished"
                    + (hostTookWindow ? " (host opened its own dialogue)" : ""));
            }
        }

        private static bool HostOwnsWindow(DialogueWindow dw, NPC drainNpc)
        {
            try
            {
                return Player.Instance != null && Player.Instance.inDialogue && dw.opened
                    && dw.npc != null && dw.npc != drainNpc;
            }
            catch { return false; }
        }

        /// <summary>
        /// Abort lookKeyhole world-only drain on dialog Release so leave-door GE runs now.
        /// Only the NPC and peer whose drain it is: another peer's Release (or an NPC of the same
        /// name elsewhere, or in the other world) must not cut a drain that is not theirs.
        /// </summary>
        internal void AbortWorldOnlyDrainForRelease(NpcRef npc, int ownerPlayerId)
        {
            if (_dialogWorldDrainCo == null) return;
            if (_drainNpc == null || !npc.Matches(_drainNpc))
                return;
            if (_drainOwnerId > 0 && ownerPlayerId > 0 && _drainOwnerId != ownerPlayerId)
                return;

            try { _net.StopCoroutine(_dialogWorldDrainCo); } catch { /* ignore */ }
            _dialogWorldDrainCo = null;
            _drainNpc = null;
            // No guard scope to unwind: the drain never holds one across its waits, and the
            // caller (a Release handler) may be inside its own scope.
            DialogHostApplyGuard.EndDrain();
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw != null && (dw.displayingDialogue || dw.npc != null))
                    DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                else
                    DWMPHorde.Patches.DialogHostPresentation.ScrubAndDisarm(dw);
            }
            catch { /* ignore */ }
            _pendingCloseDialogueNpc = default;
            ModRuntime.LegacyInfo(
                "[DialogOutcome] aborted world-only drain on dialog Release (open door now)");
        }
    }
}
