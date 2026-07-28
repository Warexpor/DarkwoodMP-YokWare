using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// C1: Client applying host GameEventsFired must not locally run startDream/endDream —
    /// host DreamStarted / DreamEnded own that authority. Other GE types still fire.
    /// </summary>
    [HarmonyPatch(typeof(GameEvent), nameof(GameEvent.fire))]
    public static class GameEventDreamAuthorityPatch
    {
        private static bool Prefix(GameEvent __instance)
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
                return false;
            }

            return true;
        }
    }
}
