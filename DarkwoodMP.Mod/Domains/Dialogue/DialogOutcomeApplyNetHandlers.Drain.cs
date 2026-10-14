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
            // A close that is still held back (its own outcome queued) defers itself again.
            if (_pendingCloses.Count > 0)
            {
                NpcRef[] pending = _pendingCloses.ToArray();
                _pendingCloses.Clear();
                for (int i = 0; i < pending.Length; i++)
                    _close.HostFireNpcCloseDialogue(pending[i]);
            }
        }

        /// <summary>
        /// Keep DialogHostApplyGuard up while changePortrait / multi-board chains finish,
        /// force-advancing stalled WritingText boards, then silent-close.
        /// </summary>
        internal System.Collections.IEnumerator HostDrainWorldOnlyDialogue(DialogueWindow dw, NPC npc)
        {
            float deadline = Time.realtimeSinceStartup + 8f;
            _drainRush = false;
            int lastBoard = int.MinValue;
            int stallTicks = 0;
            bool hostTookWindow = false;
            try
            {
                // Past the reading time the rest is played out at once, not dropped: a close at
                // the deadline lost every board still to come and its outcomes.
                while (dw != null
                    && DialogHostApplyGuard.DrainPending
                    && dw.displayingDialogue
                    && dw.currentDialogue != null
                    && Time.realtimeSinceStartup < deadline + 5f)
                {
                    if (Time.realtimeSinceStartup >= deadline)
                        _drainRush = true;
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
                        if (stallTicks >= (_drainRush ? 1 : 25))
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
                _drainRush = false;
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
        /// The talk this drain replays ended on its speaker (a Release, or the speaker left): the
        /// rest of its boards play out now instead of at the reading pace, then the drain closes
        /// and the NPC's close events run (they wait in <see cref="_pendingCloses"/>). Waiting the
        /// reading pace held the dream door shut for seconds after the talk; stopping the drain
        /// instead dropped every later board's outcomes: the overworld bunker key's second board
        /// (door21_door_event, endOfChapter1) never ran, so a client's key turned in the lock and
        /// the door stayed shut. Only the NPC and peer whose drain it is: another peer's Release
        /// (or an NPC of the same name elsewhere, or in the other world) must not rush it.
        /// </summary>
        internal void RushWorldOnlyDrainForRelease(NpcRef npc, int ownerPlayerId)
        {
            if (_dialogWorldDrainCo == null) return;
            if (_drainNpc == null || !npc.Matches(_drainNpc))
                return;
            if (_drainOwnerId > 0 && ownerPlayerId > 0 && _drainOwnerId != ownerPlayerId)
                return;
            _drainRush = true;
            ModRuntime.LegacyInfo("[DialogOutcome] talk released: world-only drain plays its last boards now");
        }
    }
}
