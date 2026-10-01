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
    /// <item>Client co-op: defer world outcomes to the host (flags / world events / location
    /// load), clear dialogue-triggered wantToDream, commit linear boards (msg 90).</item>
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
            public string NpcName;
            public string DialogueName;
            public int BoardIndex;
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

            if (!DialogHostApplyGuard.ShouldRunDisplayNextBoard())
                return false;

            TryBeginClientDefer(__instance, ref __state);
            DialogOutcomeIndexPatch.ResetCounter();
            return true;
        }

        private static void TryBeginClientDefer(DialogueWindow dw, ref BoardState state)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;
            if (!DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false))
                return;

            try
            {
                if (dw.npc != null && dw.currentDialogue != null)
                {
                    state.NpcName = dw.npc.name;
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
        }

        private static void TrySendBoardCommit(BoardState state)
        {
            // Only boards the Prefix captured on a deferring client are committed.
            if (!state.Deferred || !state.HaveBoard) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (DialogHostApplyGuard.DialogueApplyActive) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            string name = state.DialogueName;
            if (string.IsNullOrEmpty(name)) return;
            if (DialogBoardCommit.IsRecentDest(name))
                return;

            string npcName = state.NpcName;
            int boardIdx = state.BoardIndex;
            net.Send(NetMessageType.DialogOutcomeSync,
                w => new DialogOutcomeSyncMessage
                {
                    NpcName = npcName,
                    DecisionIndex = -1,
                    DialogueName = name,
                    BoardIndex = boardIdx,
                    TargetDialogueName = ""
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] board commit NPC={npcName} dialogue={name} board={boardIdx}");
        }

        private static void TrySuppressHostCook(DialogueWindow dw)
        {
            if (dw == null || !DialogHostApplyGuard.DialogueApplyActive) return;
            if (!DialogApplyPolicy.ShouldSuppressCookOnHostRemoteApply(true)) return;
            dw.wantToCook = false;
        }
    }
}
