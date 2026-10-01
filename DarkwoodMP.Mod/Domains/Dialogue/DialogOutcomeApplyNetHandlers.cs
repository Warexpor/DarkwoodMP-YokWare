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
    /// <summary>
    /// Dialog outcome apply / world-only drain (host displayDialogue + one-shot boards).
    /// </summary>
    internal sealed partial class DialogOutcomeApplyNetHandlers
    {
        private readonly LanNetworkManager _net;
        private DialogOutcomeCloseNetHandlers _close;

        private Coroutine _dialogWorldDrainCo;
        private string _pendingCloseDialogueNpc;
        // Whose drain is running, so another peer's Release / apply cannot abort it.
        private string _drainNpcName;
        private int _drainOwnerId;

        // Outcomes that arrived while the host's dialogue window was busy with another NPC.
        private sealed class DeferredApply
        {
            public DialogOutcomeSyncMessage Msg;
            public int From;
            public float QueuedAt;
        }

        /// <summary>A queued outcome for this NPC is still waiting (its close must wait too).</summary>
        internal bool HasDeferredApplyFor(string npcName)
        {
            for (int i = 0; i < _deferredApplies.Count; i++)
                if (string.Equals(_deferredApplies[i].Msg.NpcName, npcName, StringComparison.Ordinal))
                    return true;
            return false;
        }
        private readonly System.Collections.Generic.List<DeferredApply> _deferredApplies =
            new System.Collections.Generic.List<DeferredApply>();
        private Coroutine _deferredApplyCo;
        // Drain start / finish counters (see the StartCoroutine site).
        private int _drainGeneration;
        private int _drainDoneGeneration;

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

        /// <summary>
        /// The one shared DialogueWindow cannot replay a peer's node while the host is mid-talk
        /// with a different NPC (rebinding dw.npc / displayDialogue wiped the host's own window and
        /// the silent close then swallowed its real close), nor preempt another peer's pending drain.
        /// </summary>
        private bool ApplyBusyFor(DialogueWindow dw, NPC npc, string npcName)
        {
            if (dw == null) return false;
            if (Player.Instance != null && Player.Instance.inDialogue && dw.opened
                && dw.npc != null && (npc == null || dw.npc != npc))
                return true;
            if (_dialogWorldDrainCo != null && !string.IsNullOrEmpty(_drainNpcName)
                && !string.Equals(_drainNpcName, npcName, StringComparison.Ordinal))
                return true;
            return false;
        }

        /// <summary>
        /// Session end: drop queued outcomes, stop the drain / replay coroutines and forget the pending
        /// close, so nothing from this session replays into (or closes a dialogue of) the next one.
        /// </summary>
        internal void ClearPendingApply()
        {
            if (_deferredApplyCo != null)
            {
                try { _net.StopCoroutine(_deferredApplyCo); } catch { /* ignore */ }
                _deferredApplyCo = null;
            }
            _deferredApplies.Clear();
            if (_dialogWorldDrainCo != null)
            {
                try { _net.StopCoroutine(_dialogWorldDrainCo); } catch { /* ignore */ }
                _dialogWorldDrainCo = null;
            }
            _drainNpcName = null;
            _drainOwnerId = 0;
            _pendingCloseDialogueNpc = null;
        }

        /// <summary>A dropped queued outcome may have been holding back that NPC's close: run it now.</summary>
        private void ReplayDeferredCloseIfIdle(string npcName)
        {
            if (_dialogWorldDrainCo != null || HasDeferredApplyFor(npcName))
                return;
            if (!string.Equals(_pendingCloseDialogueNpc, npcName, StringComparison.Ordinal))
                return;
            _pendingCloseDialogueNpc = null;
            _close.HostFireNpcCloseDialogue(npcName);
        }

        /// <summary>A queued outcome older than this is stale (the conversation has moved on).</summary>
        private const float DeferredApplyMaxAgeSec = 15f;

        private void DeferApply(DialogOutcomeSyncMessage msg)
        {
            _deferredApplies.Add(new DeferredApply
            {
                Msg = msg,
                From = GeFireActorContext.PeekOr(_net.CurrentReceivePlayerId),
                QueuedAt = Time.realtimeSinceStartup
            });
            ModRuntime.LegacyInfo(
                $"[DialogOutcome] host window busy — deferring apply NPC={msg.NpcName} "
                + $"dialogue={msg.DialogueName} target={msg.TargetDialogueName}");
            if (_deferredApplyCo == null)
                _deferredApplyCo = _net.StartCoroutine(DrainDeferredApplies());
        }

        private System.Collections.IEnumerator DrainDeferredApplies()
        {
            float deadline = Time.realtimeSinceStartup + 120f;
            try
            {
                while (_deferredApplies.Count > 0 && Time.realtimeSinceStartup < deadline)
                {
                    DeferredApply next = _deferredApplies[0];
                    var dw = Singleton<UI>.Instance?.dialogueWindow;
                    NPC npc = DialogOutcomeCloseNetHandlers.FindNpcByName(next.Msg.NpcName);
                    // Stale: too old, or the host has since opened its own talk with that NPC (replaying
                    // now would display a node over the host's live window).
                    bool hostTalkingToIt = dw != null && npc != null && Player.Instance != null
                        && Player.Instance.inDialogue && dw.opened && dw.npc == npc;
                    if (Time.realtimeSinceStartup - next.QueuedAt > DeferredApplyMaxAgeSec || hostTalkingToIt)
                    {
                        _deferredApplies.RemoveAt(0);
                        ModRuntime.Log?.LogWarning(
                            "[DialogOutcome] dropped stale deferred outcome NPC=" + next.Msg.NpcName
                            + (hostTalkingToIt ? " (host now talking to it)" : " (too old)"));
                        ReplayDeferredCloseIfIdle(next.Msg.NpcName);
                        continue;
                    }
                    if (dw != null && ApplyBusyFor(dw, npc, next.Msg.NpcName))
                    {
                        yield return new WaitForSecondsRealtime(0.25f);
                        continue;
                    }

                    _deferredApplies.RemoveAt(0);
                    // Outside the inbound packet: stamp the sender as the GE actor explicitly.
                    bool pushed = next.From > 0 && GeFireActorContext.Depth == 0;
                    if (pushed) GeFireActorContext.Push(next.From);
                    try { HandleDialogOutcomeSync(next.Msg); }
                    catch (Exception ex)
                    {
                        ModRuntime.Log?.LogWarning("[DialogOutcome] deferred apply failed: " + ex.Message);
                    }
                    finally { if (pushed) GeFireActorContext.Pop(); }
                }

                if (_deferredApplies.Count > 0)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DialogOutcome] dropped " + _deferredApplies.Count
                        + " deferred outcome(s) — host window stayed busy");
                    _deferredApplies.Clear();
                }
            }
            finally
            {
                _deferredApplyCo = null;
            }
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
                    ModLog.WarnRate(LogCat.World, "dlg-apply-npc-miss:" + msg.NpcName,
                        $"[DialogOutcome] NPC '{msg.NpcName}' not found for target={msg.TargetDialogueName}");
                    return;
                }

                if (ApplyBusyFor(dw, npc, msg.NpcName))
                {
                    DeferApply(msg);
                    return;
                }

                // Decided on instance identity BEFORE any rebind: a name compare after
                // `dw.npc = npc` is always true, and two NPCs can share a name across worlds.
                bool hostWasInThisTalk = Player.Instance != null
                    && Player.Instance.inDialogue
                    && dw.opened
                    && dw.npc == npc;

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
                    _drainNpcName = null;
                    DialogHostApplyGuard.EndDrain();
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
                try
                {
                    // Inside the try: a throw here must still reach the catch's EndWorldOnly.
                    DialogHostApplyGuard.ClearChainedDisplayBlock();
                    DialogHostApplyGuard.DestDrainActive = true;
                    if (!hostWasInThisTalk)
                        DWMPHorde.Patches.DialogHostPresentation.ArmStickySuppress();

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
                        // Multi-board / portrait chain: delayed boards finish over the next
                        // frames. The guard is not held across the wait; each board advance and
                        // the final close re-enter it for just their synchronous body.
                        _drainNpcName = msg.NpcName;
                        _drainOwnerId = GeFireActorContext.PeekOr(_net.CurrentReceivePlayerId);
                        DialogHostApplyGuard.BeginDrain(_drainOwnerId);
                        // The drain can finish inside StartCoroutine (first slice exits the loop);
                        // its finally already ran, so a finished handle must not be stored as live.
                        int drainGen = ++_drainGeneration;
                        Coroutine co = _net.StartCoroutine(HostDrainWorldOnlyDialogue(dw, npc));
                        if (_drainDoneGeneration != drainGen)
                            _dialogWorldDrainCo = co;
                        DialogHostApplyGuard.EndWorldOnly();
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
                ModLog.WarnRate(LogCat.World, "dlg-board-npc-miss:" + msg.NpcName,
                    $"[DialogOutcome] NPC '{msg.NpcName}' not found for board apply");
                return;
            }

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null) return;

            CharacterDialogue.Dialogue dialogue = null;
            try { dialogue = npc.characterDialogue.getDialogue(msg.DialogueName); }
            catch { dialogue = null; }
            if (dialogue == null)
            {
                ModLog.WarnRate(LogCat.World, "dlg-board-dialogue-miss:" + msg.NpcName + ":" + msg.DialogueName,
                    $"[DialogOutcome] dialogue '{msg.DialogueName}' not found on {msg.NpcName}");
                return;
            }

            if (ApplyBusyFor(dw, npc, msg.NpcName))
            {
                DeferApply(msg);
                return;
            }

            // Identity check first; only then bind. Rebinding first made this always true and the
            // silent close that follows tore down the host's own conversation.
            bool hostWasInThisTalk = Player.Instance != null
                && Player.Instance.inDialogue
                && dw.opened
                && dw.npc == npc;

            dw.npc = npc;
            if (Player.Instance != null)
                Player.Instance.talkedToNPC = npc;

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
