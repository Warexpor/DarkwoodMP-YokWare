using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client co-op: wrap displayNextBoard so world outcomes are host-only.
    /// Clears dialogue-triggered wantToDream so DreamStartRequest is not raced
    /// with DialogOutcome host prepare. Also commits linear/source boards (msg 90).
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "displayNextBoard")]
    public static class DialogClientWorldDeferBoardPatch
    {
        /// <summary>
        /// What the board commit must describe, read BEFORE vanilla runs. displayNextBoard's own
        /// outcomes (exit, switch dialogue, close) null dw.npc / replace currentDialogue /
        /// reset currentBoard, so reading them afterwards dropped the commit or sent the wrong
        /// board index.
        /// </summary>
        internal struct BoardState
        {
            public bool Deferred;
            public bool HaveBoard;
            public string NpcName;
            public string DialogueName;
            public int BoardIndex;
        }

        private static bool Prefix(DialogueWindow __instance, out BoardState __state)
        {
            __state = default(BoardState);
            if (!DialogHostApplyGuard.ShouldRunDisplayNextBoard())
                return false;

            if (__instance == null) return true;
            if (LanNetworkManager.IsApplyingRemoteState) return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            if (!DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false))
                return true;

            try
            {
                if (__instance.npc != null && __instance.currentDialogue != null)
                {
                    __state.NpcName = __instance.npc.name;
                    __state.DialogueName = __instance.currentDialogue.fullName ?? "";
                    // The board vanilla is about to display: currentBoard is incremented first.
                    __state.BoardIndex =
                        Traverse.Create(__instance).Field("currentBoard").GetValue<int>() + 1;
                    __state.HaveBoard = true;
                }
            }
            catch { __state.HaveBoard = false; }

            DialogClientWorldDefer.Begin();
            __state.Deferred = true;
            return true;
        }

        private static void Postfix(DialogueWindow __instance, BoardState __state)
        {
            if (__state.Deferred)
            {
                // End moved to Finalizer (covers throw before/during Postfix).
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

        // Finalizer (not Postfix): displayNextBoard throw after Prefix Begin leaves
        // DialogClientWorldDefer.Active sticky → Flags/world events/prepareLocation/
        // returnToWorld/Map.showElement suppressed forever on the speaking client.
        [HarmonyFinalizer]
        private static void Finalizer(BoardState __state)
        {
            if (__state.Deferred)
                DialogClientWorldDefer.End();
        }

        private static void TrySendBoardCommit(BoardState state)
        {
            // Only boards the Prefix captured on a deferring client (matches the old gate, which
            // read Role/Defer in the Postfix) are committed.
            if (!state.Deferred || !state.HaveBoard) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (DialogHostApplyGuard.Active) return;

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
            if (dw == null || !DialogHostApplyGuard.Active) return;
            if (!DialogApplyPolicy.ShouldSuppressCookOnHostRemoteApply(true)) return;
            dw.wantToCook = false;
        }
    }

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(bool))]
    public static class DialogDeferFlagBoolPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Flags), "setFlag", typeof(string), typeof(int))]
    public static class DialogDeferFlagIntPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Events), "fireWorldEvent")]
    public static class DialogDeferFireWorldEventPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    /// <summary>Dialogue transport outcomes must not start location load on client.</summary>
    [HarmonyPatch(typeof(OutsideLocations), "prepareLocation")]
    public static class DialogDeferPrepareLocationPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(OutsideLocations), "returnToWorld")]
    public static class DialogDeferReturnToWorldPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Map), "showElement", typeof(string))]
    public static class DialogDeferMarkOnMapPatch
    {
        private static bool Prefix()
        {
            if (!DialogClientWorldDefer.Active)
                return true;
            return false;
        }
    }
}
