using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host applying a client's hit on a door, window or crate. Vanilla tells "attacked by the
    /// player" from "attacked" by a <c>Player</c> component on the attacker; the client's stand-in
    /// has none, so scene triggers waiting for the player to attack the object never fired for a
    /// client. The hit runs as that player's action and fires the player's trigger.
    /// </summary>
    internal static class PlayerWorldHitScope
    {
        internal static int Depth; // process-scoped: call-scoped, unwound by Run's finally

        internal static void Run(int playerId, System.Action hit)
        {
            Depth++;
            try
            {
                DialogHostApplyGuard.RunHostWorldFanoutForPlayer(playerId, hit);
            }
            finally
            {
                Depth--;
            }
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo), new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(bool) })]
    public static class PlayerWorldHitTriggerPatch
    {
        private static void Prefix(ref EventTrigger.Type triggerType)
        {
            if (PlayerWorldHitScope.Depth > 0 && triggerType == EventTrigger.Type.onGetAttacked)
                triggerType = EventTrigger.Type.onGetAttackedByPlayer;
        }
    }

    [HarmonyPatch(typeof(Core), nameof(Core.sendTriggerInfo), new[] { typeof(GameObject), typeof(EventTrigger.Type), typeof(string), typeof(bool) })]
    public static class PlayerWorldHitTriggerValuePatch
    {
        private static void Prefix(ref EventTrigger.Type triggerType)
        {
            if (PlayerWorldHitScope.Depth > 0 && triggerType == EventTrigger.Type.onGetAttacked)
                triggerType = EventTrigger.Type.onGetAttackedByPlayer;
        }
    }
}
