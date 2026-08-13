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
        private static bool Prefix(DialogueWindow __instance, out bool __state)
        {
            __state = false;
            if (!DialogHostApplyGuard.ShouldRunDisplayNextBoard())
                return false;

            if (__instance == null) return true;
            if (LanNetworkManager.IsApplyingRemoteState) return true;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            if (!DialogApplyPolicy.ShouldDeferWorldOnClient(true, true, false))
                return true;

            DialogClientWorldDefer.Begin();
            __state = true;
            return true;
        }

        private static void Postfix(DialogueWindow __instance, bool __state)
        {
            if (__state)
            {
                try
                {
                    if (__instance != null)
                        __instance.dreamToStart = null;
                    var dreams = Dreams.Instance;
                    if (dreams != null && dreams.wantToDream && !dreams.dreaming && !dreams.dreamPrepared)
                    {
                        if (!DreamSession.IsActive)
                            dreams.wantToDream = false;
                    }
                }
                finally
                {
                    DialogClientWorldDefer.End();
                }
            }

            TrySendBoardCommit(__instance);
            TrySuppressHostCook(__instance);
        }

        private static void TrySendBoardCommit(DialogueWindow dw)
        {
            if (dw == null || dw.npc == null || dw.currentDialogue == null) return;
            if (LanNetworkManager.IsApplyingRemoteState) return;
            if (DialogHostApplyGuard.Active) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return;

            string name = dw.currentDialogue.fullName ?? "";
            if (string.IsNullOrEmpty(name)) return;
            if (DialogBoardCommit.IsRecentDest(name))
                return;

            int boardIdx = Traverse.Create(dw).Field("currentBoard").GetValue<int>();
            net.Send(NetMessageType.DialogOutcomeSync,
                w => new DialogOutcomeSyncMessage
                {
                    NpcName = dw.npc.name,
                    DecisionIndex = -1,
                    DialogueName = name,
                    BoardIndex = boardIdx,
                    TargetDialogueName = ""
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo(
                    $"[DialogOutcome] board commit NPC={dw.npc.name} dialogue={name} board={boardIdx}");
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
