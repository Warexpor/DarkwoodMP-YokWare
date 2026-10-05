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

            if (!NetGuard.Connected(out var net))
                return;
            // Only client → host. Host choices apply locally; FlagSync carries world flags.
            if (net.Role != NetworkRole.Client)
                return;

            var dw = Singleton<UI>.Instance?.dialogueWindow;
            if (dw == null || dw.npc == null) return;

            string target = __instance.destDialogueName ?? "";
            // Vanilla onPress is a no-op until boardFinished; do not ghost-sync those clicks.
            // (host would advance while client UI stays on the prior node → lookAt* loops).
            string nowName = dw.currentDialogue != null ? (dw.currentDialogue.fullName ?? "") : "";
            if (string.IsNullOrEmpty(target) || !string.Equals(nowName, target, System.StringComparison.Ordinal))
                return;

            var dci = __instance.GetComponent<DialogChoiceIndex>();
            int index = dci != null ? dci.Index : -1;

            int boardIdx = Traverse.Create(dw).Field("currentBoard").GetValue<int>();

            // Prefer target dialogue name; index alone is fragile when requirements differ.
            if (index < 0 && string.IsNullOrEmpty(target)) return;

            string sourceDialogue = __state ?? "";

            DialogBoardCommit.NoteChoiceDest(target);

            var msg = new DialogOutcomeSyncMessage
            {
                DecisionIndex = index,
                DialogueName = sourceDialogue,
                BoardIndex = boardIdx,
                TargetDialogueName = target
            };
            DialogOutcomeNpc.Stamp(ref msg, NpcRef.Of(dw.npc));
            net.Send(NetMessageType.DialogOutcomeSync, w => msg.Serialize(w), DeliveryMethod.ReliableOrdered);

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

    /// <summary>Which NPC a dialogue outcome is for: name plus spot and world (NPC.name is not unique).</summary>
    internal static class DialogOutcomeNpc
    {
        internal static void Stamp(ref DialogOutcomeSyncMessage msg, NpcRef npc)
        {
            msg.NpcName = npc.Name ?? "";
            msg.HasPos = npc.HasPos;
            msg.PosX = npc.Pos.x;
            msg.PosY = npc.Pos.y;
            msg.PosZ = npc.Pos.z;
            msg.Dream = npc.Dream;
        }
    }

    /// <summary>Skip dest-board commits after onPress (host drain applies dest).</summary>
    internal static class DialogBoardCommit
    {
        internal static string LastChoiceDest; // process-scoped: 2 s TickCount window, self-expiring
        internal static int LastChoiceTick; // process-scoped: 2 s TickCount window, self-expiring

        internal static void NoteChoiceDest(string dest)
        {
            LastChoiceDest = dest ?? "";
            LastChoiceTick = System.Environment.TickCount;
        }

        internal static bool IsRecentDest(string dialogueName)
        {
            if (string.IsNullOrEmpty(dialogueName) || string.IsNullOrEmpty(LastChoiceDest))
                return false;
            if (!string.Equals(dialogueName, LastChoiceDest, System.StringComparison.Ordinal))
                return false;
            return System.Environment.TickCount - LastChoiceTick < 2000;
        }
    }
}
