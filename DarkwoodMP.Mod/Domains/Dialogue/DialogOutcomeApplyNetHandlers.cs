using System;
using DWMPHorde;
using DWMPHorde.Patches;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Dialog outcome apply / world-only drain (host displayDialogue + one-shot boards).
    /// </summary>
    internal sealed class DialogOutcomeApplyNetHandlers
    {
        private readonly LanNetworkManager _net;
        private DialogOutcomeCloseNetHandlers _close;

        private Coroutine _dialogWorldDrainCo;
        private string _pendingCloseDialogueNpc;

        internal DialogOutcomeApplyNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new ArgumentNullException(nameof(net));
        }

        /// <summary>Wire close sibling after both are constructed (Awake order).</summary>
        internal void BindClose(DialogOutcomeCloseNetHandlers close)
        {
            _close = close ?? throw new ArgumentNullException(nameof(close));
        }

        internal bool IsWorldDrainActive => _dialogWorldDrainCo != null;

        internal void DeferCloseUntilDrainDone(string npcName)
        {
            _pendingCloseDialogueNpc = npcName;
        }

        internal void HandleDialogOutcomeSync(DialogOutcomeSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.NpcName) || _net.Role != NetworkRole.Host) return;

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null) return;

            // Linear / source board commit (no dest). Decision-board world outcomes live here.
            if (string.IsNullOrEmpty(msg.TargetDialogueName))
            {
                HostOneShotApplyBoard(msg);
                return;
            }

            // Path A: dest dialogue node (reliable for solo-client conversations).
            if (!string.IsNullOrEmpty(msg.TargetDialogueName))
            {
                NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(msg.NpcName);
                if (npc == null)
                {
                    ModRuntime.Log?.LogWarning($"[DialogOutcome] NPC '{msg.NpcName}' not found for target={msg.TargetDialogueName}");
                    return;
                }

                bool hostWasInThisTalk = Player.Instance != null
                    && Player.Instance.inDialogue
                    && dw.opened
                    && dw.npc != null
                    && dw.npc.name == msg.NpcName;

                // Vanilla onPress marks source node alreadyShown before switching.
                if (!string.IsNullOrEmpty(msg.DialogueName) && npc.characterDialogue != null)
                {
                    try
                    {
                        var source = npc.characterDialogue.getDialogue(msg.DialogueName);
                        if (source != null)
                        {
                            source.alreadyShown = true;
                            source.gossipShown = true;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.Log?.LogWarning("[DialogOutcome] mark source: " + ex.Message);
                    }
                }

                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] Host world-only displayDialogue target={msg.TargetDialogueName} " +
                    $"source={msg.DialogueName} NPC={msg.NpcName} (wasInTalk={hostWasInThisTalk})");

                // Guard must stay active through displayDialogue + teardown: vanilla close
                // (and startDream close inside displayNextBoard) black-fades + Save → SaveSync.
                // lookKeyhole_dream changePortrait chains displayNextBoard after 1.5s. Do not
                // silent-close immediately or later boards / flags never run (door stays shut).
                if (_dialogWorldDrainCo != null)
                {
                    try { _net.StopCoroutine(_dialogWorldDrainCo); } catch { /* ignore */ }
                    _dialogWorldDrainCo = null;
                    while (DialogHostApplyGuard.Active)
                        DialogHostApplyGuard.EndWorldOnly();
                    // Scrub leftover oven/keyhole backdrop before the next world-only apply.
                    // SilentClose nulls dw.npc — re-bind after scrub (lookAtBottle→lookAtPot NRE).
                    try
                    {
                        DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                    }
                    catch { /* ignore */ }
                }

                // Bind AFTER drain scrub — SilentCloseAfterWorldApply clears dw.npc.
                dw.npc = npc;
                if (Player.Instance != null)
                    Player.Instance.talkedToNPC = npc;
                if (dw.npc == null || dw.npc.characterDialogue == null)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DialogOutcome] world-only displayDialogue aborted — npc/characterDialogue null"
                        + " target=" + msg.TargetDialogueName + " NPC=" + msg.NpcName);
                    HostFinishDialogWorldApply(npc);
                    return;
                }

                DialogHostApplyGuard.BeginWorldOnly();
                DialogHostApplyGuard.ClearChainedDisplayBlock();
                DialogHostApplyGuard.DestDrainActive = true;
                if (!hostWasInThisTalk)
                    DWMPHorde.Patches.DialogHostPresentation.ArmStickySuppress();
                try
                {
                    // World-only apply needs an active DialogueWindow (lookKeyhole boards
                    // StartCoroutine setPortrait on an inactive object can leave
                    // Core.forbidInputs set.
                    if (dw.gameObject != null && !dw.gameObject.activeSelf)
                        dw.gameObject.SetActive(true);

                    dw.displayDialogue(msg.TargetDialogueName);

                    if (hostWasInThisTalk)
                    {
                        DialogHostApplyGuard.EndWorldOnly();
                    }
                    else if (Core.forbidInputs || (dw.displayingDialogue && dw.currentDialogue != null
                             && dw.currentDialogue.boards != null && dw.currentDialogue.boards.Count > 1))
                    {
                        // Multi-board / portrait chain: keep guard up until drain finishes.
                        _dialogWorldDrainCo = _net.StartCoroutine(
                            HostDrainWorldOnlyDialogue(dw, npc));
                        return;
                    }
                    else
                    {
                        if (dw.displayingDialogue || dw.npc != null)
                            dw.close(); // DialogHostSilentClosePatch while guard Active
                        else
                            DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                        DialogHostApplyGuard.EndWorldOnly();
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DialogOutcome] world-only displayDialogue failed: " + ex.Message
                        + " target=" + msg.TargetDialogueName
                        + " NPC=" + msg.NpcName
                        + " npcBound=" + (dw != null && dw.npc != null));
                    try
                    {
                        DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                    }
                    catch { /* ignore */ }
                    try
                    {
                        Core.forbidInputs = false;
                        Core.cantChangeForbidInputs = false;
                    }
                    catch { /* ignore */ }
                    DialogHostApplyGuard.EndWorldOnly();
                    HostFinishDialogWorldApply(npc);
                    return;
                }

                HostFinishDialogWorldApply(npc);
                return;
            }

            // Path B: legacy index click when host UI matches exactly (also world-only).
            if (dw.npc == null || dw.npc.name != msg.NpcName) return;
            if (dw.currentDialogue == null || dw.currentDialogue.fullName != msg.DialogueName) return;

            int currentBoard = Traverse.Create(dw).Field("currentBoard").GetValue<int>();
            if (currentBoard != msg.BoardIndex) return;
            if (msg.DecisionIndex < 0 || msg.DecisionIndex >= dw.menuOptions.Count) return;

            var btn = dw.menuOptions[msg.DecisionIndex];
            if (btn != null)
            {
                ModRuntime.LegacyInfo($"[DialogOutcome] Host world-only click decision index={msg.DecisionIndex} NPC={msg.NpcName}");
                DialogHostApplyGuard.BeginWorldOnly();
                DWMPHorde.Patches.DialogHostPresentation.ArmStickySuppress();
                try
                {
                    btn.getClicked();
                }
                finally
                {
                    DialogHostApplyGuard.EndWorldOnly();
                    var dwUi = Singleton<UI>.Instance?.dialogueWindow;
                    if (dwUi == null || !dwUi.displayingDialogue)
                        DWMPHorde.Patches.DialogHostPresentation.ScrubAndDisarm(dwUi);
                }
            }
        }

        internal void HostOneShotApplyBoard(DialogOutcomeSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.DialogueName) || msg.BoardIndex < 0)
                return;

            NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(msg.NpcName);
            if (npc == null || npc.characterDialogue == null)
            {
                ModRuntime.Log?.LogWarning($"[DialogOutcome] NPC '{msg.NpcName}' not found for board apply");
                return;
            }

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null) return;

            CharacterDialogue.Dialogue dialogue = null;
            try { dialogue = npc.characterDialogue.getDialogue(msg.DialogueName); }
            catch { dialogue = null; }
            if (dialogue == null)
            {
                ModRuntime.Log?.LogWarning(
                    $"[DialogOutcome] dialogue '{msg.DialogueName}' not found on {msg.NpcName}");
                return;
            }

            dw.npc = npc;
            if (Player.Instance != null)
                Player.Instance.talkedToNPC = npc;

            bool hostWasInThisTalk = Player.Instance != null
                && Player.Instance.inDialogue
                && dw.opened
                && dw.npc != null
                && dw.npc.name == msg.NpcName;

            DialogHostApplyGuard.BeginWorldOnly();
            DialogHostApplyGuard.BeginOneShotBoard();
            if (!hostWasInThisTalk)
                DWMPHorde.Patches.DialogHostPresentation.ArmStickySuppress();
            try
            {
                if (dw.gameObject != null && !dw.gameObject.activeSelf)
                    dw.gameObject.SetActive(true);

                dw.currentDialogue = dialogue;
                Traverse.Create(dw).Field("currentBoard").SetValue(msg.BoardIndex - 1);
                Traverse.Create(dw).Method("displayNextBoard").GetValue();

                if (!hostWasInThisTalk)
                    DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DialogOutcome] one-shot board apply failed: " + ex.Message);
            }
            finally
            {
                DialogHostApplyGuard.EndOneShotBoard();
                DialogHostApplyGuard.EndWorldOnly();
                if (!hostWasInThisTalk)
                    DWMPHorde.Patches.DialogHostPresentation.ScrubAndDisarm(dw);
            }

            HostFinishDialogWorldApply(npc);
            ModRuntime.LegacyInfo(
                $"[DialogOutcome] one-shot board NPC={msg.NpcName} dialogue={msg.DialogueName} board={msg.BoardIndex}");
        }

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
            if (!string.IsNullOrEmpty(_pendingCloseDialogueNpc))
            {
                string pending = _pendingCloseDialogueNpc;
                _pendingCloseDialogueNpc = null;
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
            try
            {
                while (dw != null
                    && DialogHostApplyGuard.Active
                    && dw.displayingDialogue
                    && dw.currentDialogue != null
                    && Time.realtimeSinceStartup < deadline)
                {
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
                                Traverse.Create(dw).Method("displayNextBoard").GetValue();
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

                if (dw != null && DialogHostApplyGuard.Active)
                {
                    if (dw.displayingDialogue || dw.npc != null)
                        dw.close();
                    else
                        DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                }
            }
            finally
            {
                _dialogWorldDrainCo = null;
                while (DialogHostApplyGuard.Active)
                    DialogHostApplyGuard.EndWorldOnly();
                try
                {
                    Core.forbidInputs = false;
                    Core.cantChangeForbidInputs = false;
                    if (dw != null)
                        dw.forbidInputs = false;
                }
                catch { /* ignore */ }
                HostFinishDialogWorldApply(npc);
                ModRuntime.LegacyInfo("[DialogOutcome] world-only drain finished");
            }
        }

        /// <summary>
        /// Abort lookKeyhole world-only drain on dialog Release so leave-door GE runs now.
        /// </summary>
        internal void AbortWorldOnlyDrainForRelease()
        {
            if (_dialogWorldDrainCo == null) return;

            try { _net.StopCoroutine(_dialogWorldDrainCo); } catch { /* ignore */ }
            _dialogWorldDrainCo = null;
            while (DialogHostApplyGuard.Active)
                DialogHostApplyGuard.EndWorldOnly();
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw != null && (dw.displayingDialogue || dw.npc != null))
                    DWMPHorde.Patches.DialogHostSilentClosePatch.SilentCloseAfterWorldApply(dw);
                else
                    DWMPHorde.Patches.DialogHostPresentation.ScrubAndDisarm(dw);
            }
            catch { /* ignore */ }
            _pendingCloseDialogueNpc = null;
            ModRuntime.LegacyInfo(
                "[DialogOutcome] aborted world-only drain on dialog Release (open door now)");
        }

        internal void HandleDialogTreeState(DialogTreeStateMessage msg)
        {
            if (string.IsNullOrEmpty(msg.Payload)) return;

            DWMPHorde.Sync.DialogTreeSync.ApplyPayload(msg.Payload);

            // Host: fan-out to other peers after apply.
            if (_net.Role == NetworkRole.Host)
            {
                int from = _net.CurrentReceivePlayerId;
                if (from > 0)
                {
                    _net.SendToAllExcept(from, NetMessageType.DialogTreeState,
                        w => msg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);
                }
            }
        }
    }
}
