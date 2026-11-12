using DWMPHorde.Logging;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// A banshee overlay stuck on a peer's screen (the lair exit with a scream still on:
    /// the scream's out-of-sight never fired, or its release never arrived, and only a
    /// restart cleared the black). Any far teleport means the screamer is behind us, so
    /// fade the overlay out and let it be destroyed; a scream that is still valid raises
    /// it again through the normal sight and agitation paths.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.teleportTo), new[] { typeof(Vector3), typeof(Quaternion) })]
    public static class BansheeOverlayTeleportFailsafePatch
    {
        private static Vector3 _from; // process-scoped: Prefix/Postfix pair of one call

        /// <summary>Same-area hops (dialogue moves, short carries) leave a live scream alone.</summary>
        private const float ClearDistance = 100f;

        private static void Prefix(Player __instance)
        {
            _from = __instance != null && __instance._transform != null
                ? __instance._transform.position
                : Vector3.zero;
        }

        private static void Postfix(Player __instance)
        {
            if (__instance == null || __instance._transform == null)
                return;
            Vector3 d = __instance._transform.position - _from;
            d.y = 0f;
            if (d.sqrMagnitude < ClearDistance * ClearDistance)
                return;
            try
            {
                UI ui = Singleton<UI>.Instance;
                tk2dBaseSprite overlay = ui != null ? ui.bansheeOverlay : null;
                if (overlay == null || overlay.color.a <= 0.01f)
                    return;
                Core.tweenAlpha(overlay.gameObject, 0f, 0.5f, timeScaleDependent: true);
                ui.wantToRemoveBansheeOverlay();
                ModRuntime.LegacyInfo("[LocationSync] far teleport — stale banshee overlay faded out");
            }
            catch (System.Exception ex)
            {
                ModLog.WarnRate(LogCat.World, "banshee-overlay-failsafe",
                    "[LocationSync] banshee overlay failsafe: " + ex.Message);
            }
        }
    }
}
