using System.Collections;
using DWMPHorde.Harmony;
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
        private static bool Prefix(GameEvent __instance, GameObject thisGO, ref IEnumerator __result)
        {
            if (__instance == null) return true;
            if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                return true;

            if (__instance.type == GameEvent.Type.endDream && !__instance.disabled)
            {
                // An exit a player sets off with its own body wakes that player alone
                // (DreamExit); on every other machine the step does nothing, and the exit is
                // there again for the next player.
                if (Sync.DreamExit.IsPersonalEnd(thisGO))
                {
                    Sync.DreamExit.Rearm(thisGO);
                    if (GameEventPersonalActorPatch.ShouldSuppressPersonal() || !Sync.DreamExit.LocalCanLeave())
                    {
                        __result = HarmonyCoroutineUtil.Empty();
                        return false;
                    }
                    Sync.DreamExit.NoteLocalExitStep();
                    return true;
                }
                // An ending that is everyone's, fired on a host that already left the dream:
                // vanilla's step needs the host to be dreaming.
                var hostNet = ModRuntime.Network;
                if (hostNet.Role == NetworkRole.Host && Sync.DreamExit.HostHostsFromOutside
                    && Sync.DreamSession.IsActive && Sync.DreamSyncManager.IsOnDreamPad(thisGO != null ? thisGO.transform : null))
                {
                    string outcome = __instance.Value ?? "";
                    int actor = Sync.GeFireActorContext.PeekOr(0);
                    Singleton<Controller>.Instance.waitFramesAndRun(
                        () => Sync.DreamExit.HostEndFromOutside(hostNet, outcome, partyEnding: true, actor: actor), 1);
                    __result = HarmonyCoroutineUtil.Empty();
                    return false;
                }
            }

            if (!LanNetworkManager.IsApplyingRemoteState)
                return true;
            if (ModRuntime.Network.Role != NetworkRole.Client)
                return true;

            if (__instance.type == GameEvent.Type.startDream
                || __instance.type == GameEvent.Type.endDream)
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Client skip GE {__instance.type} under NetworkApplyGuard (host Dream* authority)");
                __result = HarmonyCoroutineUtil.Empty();
                return false;
            }

            return true;
        }
    }
}
