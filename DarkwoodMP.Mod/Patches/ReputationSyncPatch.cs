using DWMPHorde.Networking;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Reputation model C (hybrid):
    /// - Story / village NPCs: shared, live <see cref="ReputationSync"/> + join bulk.
    /// - Morning traders (<see cref="Character.isNightTrader"/>): per-player — no live/bulk overwrite.
    /// </summary>
    [HarmonyPatch(typeof(NPC), "set_reputation", new[] { typeof(int) })]
    public static class ReputationSyncPatch
    {
        private static bool Prefix(NPC __instance, object[] __args)
        {
            int value = (int)__args[0];

            if (__instance == null)
                return true;

            // Client board: do not mutate shared NPC reputation (host applies once).
            if (DWMPHorde.Sync.DialogClientWorldDefer.Active
                && DialogApplyPolicy.ShouldDeferSharedReputation(
                    ReputationSyncUtil.IsPerPlayerReputationNpc(__instance)))
                return false;

            if (LanNetworkManager.IsApplyingRemoteState
                && !DWMPHorde.Sync.DialogHostApplyGuard.Active)
                return true;

            var net = LanNetworkManager.Instance;
            if (net == null || !net.IsConnected)
                return true;

            if (ReputationSyncUtil.IsPerPlayerReputationNpc(__instance))
                return true;

            string npcName = __instance.name;
            if (string.IsNullOrEmpty(npcName))
                return true;

            ModRuntime.LegacyInfo($"[RepSync] broadcasting shared rep '{npcName}': {value}");

            var msg = new ReputationSyncMessage
            {
                NpcName = npcName,
                Reputation = value
            };

            net.Broadcast(NetMessageType.ReputationSync, w => msg.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            return true;
        }
    }

    /// <summary>Shared helpers for model-C reputation filtering.</summary>
    internal static class ReputationSyncUtil
    {
        /// <summary>
        /// True for morning hideout traders whose standing must stay per-player.
        /// Prefers <see cref="Character.isNightTrader"/>; name fallbacks if the GO is unloaded.
        /// </summary>
        public static bool IsPerPlayerReputationNpc(NPC npc)
        {
            if (npc == null) return false;
            Character ch = npc.GetComponent<Character>();
            if (ch != null)
                return ch.isNightTrader;
            return IsPerPlayerReputationNpcName(npc.name);
        }

        public static bool IsPerPlayerReputationNpcName(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return false;

            // Prefer live Character flag when the NPC is in the scene.
            NPC[] all = Object.FindObjectsOfType<NPC>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i].name != npcName) continue;
                Character ch = all[i].GetComponent<Character>();
                if (ch != null)
                    return ch.isNightTrader;
            }

            return DialogApplyPolicy.IsPerPlayerReputationNpcName(npcName);
        }
    }
}
