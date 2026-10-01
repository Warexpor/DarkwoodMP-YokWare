using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla Flier dive always calls Player.Instance.getHit(5).
    /// When the bird is diving a remote body, damage that body instead.
    /// </summary>
    [HarmonyPatch(typeof(Flier), "Update")]
    public static class HostFlierDivePatch
    {
        private static bool Prefix(Flier __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return true;

            Character character = __instance.GetComponent<Character>();
            if (character == null || !character.alive || !__instance.diving || character.target == null)
                return true;

            Player host = Player.Instance;
            if (host != null
                && (character.target == host.transform || character.target == host._transform))
                return true;

            if (!__instance.alignedWithTargetDuringDive)
            {
                if (Helpers.absAngleBetween(character.target.position, __instance.transform.position, __instance.transform.up) < 10f)
                    __instance.alignedWithTargetDuringDive = true;
            }
            else if (Helpers.absAngleBetween(character.target.position, __instance.transform.position, __instance.transform.up) > 30f)
            {
                __instance.runAway();
                return false;
            }

            if (character.target.isInside())
            {
                __instance.runAway();
                return false;
            }

            if (Core.trueDistance(__instance.transform.position, character.target.position) < 30f)
            {
                __instance.runAway();
                CharBase body = character.target.GetComponent<CharBase>();
                if (body == null)
                    body = character.target.GetComponentInParent<CharBase>();
                if (body != null)
                {
                    // Same args as Player.getHit(float): no attacker, interrupt, normal hit.
                    body.getHit(5f, null, CanCutInHalf: false, byPlayer: false, canInterrupt: true);
                }
            }

            return false;
        }
    }
}
