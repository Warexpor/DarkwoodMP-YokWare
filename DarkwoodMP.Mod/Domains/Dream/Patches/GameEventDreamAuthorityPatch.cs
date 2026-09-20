using System.Collections;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A client applying host GameEventsFired must not locally run
    /// startDream or endDream. DreamStarted and DreamEnded own those
    /// transitions; other GameEvent types still fire.
    /// GameEvent.fire is an IEnumerator. A Prefix return false without __result yields
    /// StartCoroutine(null) → "routine is null" and breaks the client mid-exit.
    /// </summary>
    [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
    public static class GameEventDreamAuthorityPatch
    {
        private static bool Prefix(GameEvent __instance, ref IEnumerator __result)
        {
            if (__instance == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;
            if (!LanNetworkManager.IsApplyingRemoteState)
                return true;
            if (ModRuntime.Network.Role != NetworkRole.Client)
                return true;

            if (__instance.type == GameEvent.Type.startDream
                || __instance.type == GameEvent.Type.endDream)
            {
                ModRuntime.LegacyInfo(
                    "[DreamSync] Client skip GE " + __instance.type
                    + " under NetworkApplyGuard (host Dream* authority)");
                __result = EmptyRoutine();
                return false;
            }

            return true;
        }

        private static IEnumerator EmptyRoutine()
        {
            yield break;
        }
    }
}
