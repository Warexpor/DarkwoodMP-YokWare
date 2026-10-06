using System.Collections.Generic;
using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The only patch on <c>DialogueWindow.displayNextBoard</c>. Its checks share state and
    /// depend on order, so they run here in one fixed sequence instead of across separate
    /// patch classes whose relative order Harmony does not guarantee:
    /// <list type="number">
    /// <item>Session gate: single-player runs vanilla untouched.</item>
    /// <item>A pending host world-only drain re-enters the dialogue apply guard for exactly
    /// this call (delayed changePortrait → setPortrait → displayNextBoard boards).</item>
    /// <item>Stale continuation: changePortrait schedules displayNextBoard after silent close
    /// nulls currentDialogue; skip it.</item>
    /// <item>One-shot / chained board gate of a host board apply (consumes the one-shot).</item>
    /// <item>Hand-in arbitration: a board handing over a shared journal item another player
    /// already gave away does not run (<see cref="DialogHandInArbiter"/>).</item>
    /// <item>Client co-op: defer world outcomes to the host (flags / world events / map marks),
    /// clear dialogue-triggered wantToDream, commit linear boards (msg 90). A host replay of a
    /// board that moves its speaker keeps the host where it is (<see cref="DialogPeerTrip"/>).</item>
    /// </list>
    /// The Finalizer balances the drain scope and the defer scope and clears the host input lock
    /// even if displayNextBoard throws.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "displayNextBoard")]
    public static class DialogDisplayNextBoardPatch
    {
        /// <summary>
        /// Per-call state. The board commit is read BEFORE vanilla runs: displayNextBoard's own
        /// outcomes (exit, switch dialogue, close) null dw.npc / replace currentDialogue / reset
        /// currentBoard, so reading them afterwards dropped the commit or sent the wrong index.
        /// </summary>
        internal struct BoardState
        {
            /// <summary>0 = no drain scope, 1 = scope, 2 = scope + pushed GameEventsFired actor.</summary>
            public byte DrainScope;
            public bool Deferred;
            public bool HaveBoard;
            public NpcRef Npc;
            public string DialogueName;
            public int BoardIndex;
            /// <summary>Host's own conversation: journal entries its outcomes remove go to the peers.</summary>
            public bool HostJournalDiff;
            /// <summary>Host replaying a peer's board that moves the speaker (<see cref="DialogPeerTrip"/>).</summary>
            public bool PeerTrip;
            /// <summary>The talking player's own board, reported to listeners (<see cref="DialogMirror"/>): the children before it.</summary>
            public HashSet<int> MirrorBefore;
            public CharacterDialogue.Dialogue MirrorDialogue;
            public int MirrorBoard;
            public int MirrorPortrait;
            public bool MirrorOverlay;
        }

        private static bool InSession()
        {
            var net = ModRuntime.Network;
            return net != null && net.Role != NetworkRole.Offline;
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(DialogueWindow __instance, out BoardState __state)
        {
            __state = default(BoardState);
            if (!InSession())
            {
                DialogOutcomeIndexPatch.ResetCounter();
                return true;
            }

            // A real host conversation is not part of the drain.
            if (DialogHostApplyGuard.DrainPending && !DialogHostApplyGuard.DialogueApplyActive
                && !(Player.Instance != null && Player.Instance.inDialogue))
                __state.DrainScope = DialogHostApplyGuard.BeginDrainScope() ? (byte)2 : (byte)1;

            if (__instance == null || __instance.currentDialogue == null)
                return false;

            // A listener's view: the mirror draws the talking player's boards; vanilla never runs one here.
            if (DialogMirror.SpectatorActive)
                return false;

            if (!DialogHostApplyGuard.ShouldRunDisplayNextBoard())
                return false;

            // A shared journal item this board hands over that another player already gave away.
            if (!DialogHandInArbiter.AllowBoard(__instance))
                return false;

            TryBeginClientDefer(__instance, ref __state);
            TryBeginHostJournalDiff(ref __state);
            if (DialogHostApplyGuard.DialogueApplyActive && ModRuntime.Network.Role == NetworkRole.Host
                && DialogPeerTrip.BoardMovesSpeaker(__instance))
            {
                DialogPeerTrip.Begin();
                __state.PeerTrip = true;
            }
            TryBeginMirror(__instance, ref __state);
            DialogOutcomeIndexPatch.ResetCounter();
            return true;
        }

        private static void TryBeginMirror(DialogueWindow dw, ref BoardState state)
        {
            if (!DialogMirror.OwnerCapturing(dw))
                return;
            state.MirrorBefore = DialogMirror.ChildIds(dw.dialogue);
            state.MirrorDialogue = dw.currentDialogue;
            state.MirrorBoard = Traverse.Create(dw).Field("currentBoard").GetValue<int>() + 1;
            state.MirrorPortrait = DialogMirror.BoardPortraitChange(dw, out int portrait, out bool overlay) ? portrait : -1;
            state.MirrorOverlay = overlay;
        }

        /// <summary>The board just shown goes to whoever listens in (a switch inside it reported its own).</summary>
        private static void TrySendMirror(DialogueWindow dw, BoardState state)
        {
            if (state.MirrorBefore == null || dw == null)
                return;
            if (state.MirrorPortrait >= 0)
                DialogMirror.OwnerSimple(dw, DialogMirrorMessage.KindPortrait, state.MirrorPortrait, state.MirrorOverlay);
            if (!dw.displayingDialogue || dw.currentDialogue == null || dw.currentDialogue != state.MirrorDialogue)
                return;
            if (Traverse.Create(dw).Field("currentBoard").GetValue<int>() != state.MirrorBoard)
                return;
            DialogMirror.OwnerBoard(dw, state.MirrorBefore);
        }

        /// <summary>
        /// Host talking to an NPC itself: a "removeItem" outcome can take a key, note or quest
        /// item out of the shared journal (vanilla displayNextBoard removes it from the journal
        /// dict). A peer's outcome replayed by the host is diffed by <see cref="HostApplyGuard"/>;
        /// the host's own was not, so peers kept the item.
        /// </summary>
        private static void TryBeginHostJournalDiff(ref BoardState state)
        {
            if (LanNetworkManager.IsApplyingRemoteState || HostApplyGuard.Active || DialogHostApplyGuard.DialogueApplyActive)
                return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            JournalSyncHelpers.BeginWorldApplyDiff();
            state.HostJournalDiff = true;
        }

        private static void TryBeginClientDefer(DialogueWindow dw, ref BoardState state)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (!DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false))
                return;

            try
            {
                if (dw.npc != null && dw.currentDialogue != null)
                {
                    state.Npc = NpcRef.Of(dw.npc);
                    state.DialogueName = dw.currentDialogue.fullName ?? "";
                    // The board vanilla is about to display: currentBoard is incremented first.
                    state.BoardIndex = Traverse.Create(dw).Field("currentBoard").GetValue<int>() + 1;
                    state.HaveBoard = true;
                }
            }
            catch { state.HaveBoard = false; }

            DialogClientWorldDefer.Begin();
            state.Deferred = true;
        }

        private static void Postfix(DialogueWindow __instance, BoardState __state)
        {
            if (!InSession()) return;

            // World-only host apply: hide dialogue text / force-finish typewriter so the host
            // never sees peer lines.
            if (DialogHostPresentation.ShouldSuppress)
            {
                try { DialogHostPresentation.HideSpeakerVisuals(__instance); }
                catch { /* ignore */ }
            }

            if (__state.Deferred)
            {
                // Defer End is in the Finalizer (covers a throw before/during Postfix).
                if (__instance != null)
                    __instance.dreamToStart = null;
                var dreams = Dreams.Instance;
                if (dreams != null && dreams.wantToDream && !dreams.dreaming && !dreams.dreamPrepared)
                {
                    if (!DreamSession.IsActive)
                        dreams.wantToDream = false;
                }
            }

            TrySendBoardCommit(__state);
            TrySuppressHostCook(__instance);
            TrySendMirror(__instance, __state);
        }

        /// <summary>
        /// Finalizer, not Postfix: a displayNextBoard throw would otherwise leave the drain scope
        /// open, leave DialogClientWorldDefer.Active sticky (Flags / world events / prepareLocation /
        /// returnToWorld / Map.showElement suppressed forever on the speaking client), or leave the
        /// host unable to walk / look / open the inventory after changePortrait armed
        /// forbidInputs and silent close cancelled the Invoke that clears it.
        /// </summary>
        [HarmonyFinalizer]
        private static void Finalizer(DialogueWindow __instance, BoardState __state)
        {
            if (__state.DrainScope != 0)
                DialogHostApplyGuard.EndDrainScope(__state.DrainScope == 2);

            if (DialogHostPresentation.ShouldSuppress)
            {
                try
                {
                    Core.forbidInputs = false;
                    Core.cantChangeForbidInputs = false;
                    if (__instance != null)
                        __instance.forbidInputs = false;
                }
                catch { /* ignore */ }
            }

            if (__state.Deferred)
                DialogClientWorldDefer.End();

            if (__state.PeerTrip)
                DialogPeerTrip.End();

            if (__state.HostJournalDiff)
            {
                try { JournalSyncHelpers.EndWorldApplyDiffAndBroadcastRemoves(); }
                catch { /* journal UI may be missing */ }
            }
        }

        private static void TrySendBoardCommit(BoardState state)
        {
            // Only boards the Prefix captured on a deferring client are committed.
            if (!state.Deferred || !state.HaveBoard) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (DialogHostApplyGuard.DialogueApplyActive) return;

            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            string name = state.DialogueName;
            if (string.IsNullOrEmpty(name)) return;
            if (DialogBoardCommit.IsRecentDest(name))
                return;

            int boardIdx = state.BoardIndex;
            var msg = new DialogOutcomeSyncMessage
            {
                DecisionIndex = -1,
                DialogueName = name,
                BoardIndex = boardIdx,
                TargetDialogueName = ""
            };
            DialogOutcomeNpc.Stamp(ref msg, state.Npc);
            net.Send(NetMessageType.DialogOutcomeSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] board commit NPC={state.Npc} dialogue={name} board={boardIdx}");
        }

        private static void TrySuppressHostCook(DialogueWindow dw)
        {
            if (dw == null || !DialogHostApplyGuard.DialogueApplyActive) return;
            if (!DialogApplyPolicy.ShouldSuppressCookOnHostRemoteApply(true)) return;
            dw.wantToCook = false;
        }
    }
}
