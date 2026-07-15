using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(DialogueWindow), "addDecision")]
    public static class DialogOutcomeIndexPatch
    {
        private static int _nextIndex;

        private static void Postfix(DialogueWindow __instance)
        {
            if (__instance.menuOptions.Count > 0)
            {
                var btn = __instance.menuOptions[__instance.menuOptions.Count - 1];
                if (btn != null && btn.GetComponent<DialogChoiceIndex>() == null)
                {
                    var dci = btn.gameObject.AddComponent<DialogChoiceIndex>();
                    dci.Index = _nextIndex++;
                }
            }
        }

        public static void ResetCounter()
        {
            _nextIndex = 0;
        }
    }

    [HarmonyPatch(typeof(DialogueWindow), "displayNextBoard")]
    public static class DialogOutcomeNextBoardPatch
    {
        private static void Prefix()
        {
            DialogOutcomeIndexPatch.ResetCounter();
        }
    }

    /// <summary>
    /// Client: after choosing a dialogue option, tell the host so story flags /
    /// items / reputation apply on the authoritative machine (even if the host
    /// is not in the same conversation UI).
    /// </summary>
    [HarmonyPatch(typeof(DialogueButton), "onPress")]
    public static class DialogOutcomeSendPatch
    {
        private static void Prefix(DialogueButton __instance, out string __state)
        {
            // Capture source node before vanilla marks it and switches to dest.
            __state = "";
            try
            {
                var dw = Singleton<UI>.Instance?.dialogueWindow;
                if (dw?.currentDialogue != null)
                    __state = dw.currentDialogue.fullName ?? "";
            }
            catch { __state = ""; }
        }

        private static void Postfix(DialogueButton __instance, string __state)
        {
            if (LanNetworkManager.IsApplyingRemoteState) return;

            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return;
            // Only client → host. Host choices apply locally; FlagSync carries world flags.
            if (net.Role != NetworkRole.Client)
                return;

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null || dw.npc == null) return;

            var dci = __instance.GetComponent<DialogChoiceIndex>();
            int index = dci != null ? dci.Index : -1;

            int boardIdx = Traverse.Create(dw).Field("currentBoard").GetValue<int>();
            string target = __instance.destDialogueName ?? "";

            // Prefer target dialogue name; index alone is fragile when requirements differ.
            if (string.IsNullOrEmpty(target) && index < 0) return;

            string sourceDialogue = __state ?? "";

            net.Send(NetMessageType.DialogOutcomeSync,
                w => new DialogOutcomeSyncMessage
                {
                    NpcName = dw.npc.name,
                    DecisionIndex = index,
                    DialogueName = sourceDialogue,
                    BoardIndex = boardIdx,
                    TargetDialogueName = target
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);

            // Tree flush every choice (not only on close) so peers converge mid-conversation.
            try { DialogTreeSync.TryBroadcastFromNpc(dw.npc); }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[DialogTree] post-choice flush: " + ex.Message);
            }

            ModRuntime.LegacyInfo(
                $"[DialogOutcome] Client → host: NPC={dw.npc.name} " +
                $"source={sourceDialogue} board={boardIdx} " +
                $"decision={index} target={target}");
        }
    }

    internal class DialogChoiceIndex : UnityEngine.MonoBehaviour
    {
        public int Index;
    }
}
