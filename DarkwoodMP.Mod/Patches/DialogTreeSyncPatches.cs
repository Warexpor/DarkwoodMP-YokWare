using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Yokyy Dialogue_Patch port: on real DialogueWindow.close, snapshot and
    /// broadcast CharacterDialogue consumed-node state so peers share the tree.
    /// close() early-returns for exit dialogue without nulling npc — only flush
    /// when Prefix captured NPC and Postfix sees npc cleared.
    /// </summary>
    [HarmonyPatch(typeof(DialogueWindow), "close")]
    public static class DialogTreeSyncClosePatch
    {
        private static void Prefix(DialogueWindow __instance, ref NPC __state)
        {
            __state = __instance != null ? __instance.npc : null;
        }

        private static void Postfix(DialogueWindow __instance, NPC __state)
        {
            if (__state == null) return;
            // Exit-dialogue early return leaves npc set — not a finished conversation.
            if (__instance != null && __instance.npc != null) return;

            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;

            DialogTreeSync.TryBroadcastFromNpc(__state);
        }
    }
}
