using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When the local player switches items (which changes the torso animation library
    /// to e.g. "PlayerPistolAnims" or "PlayerMeleeAnims"), relays the RESOLVED library name
    /// to the remote peer so the proxy's torso animator shows the correct weapon sprites.
    /// Reads the library after all transformations (changedClothes, isAntagonist, naked)
    /// so the proxy loads the exact same animation asset.
    /// </summary>
    [HarmonyPatch(typeof(Player), "switchAniLibrary")]
    public static class PlayerAnimLibraryPatch
    {
        private static void Postfix(Player __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role == NetworkRole.Offline) return;
            if (TraverseHack.ApplyingFromNetwork) return;
            if (__instance.torsoAnimator == null || __instance.torsoAnimator.Library == null) return;

            string libName = __instance.torsoAnimator.Library.name;
            if (string.IsNullOrEmpty(libName)) return;

            net.SendPlayerAnimLibrary(new PlayerAnimLibraryMessage
            {
                LibraryName = libName
            });

            ModRuntime.Log?.LogDebug("[AnimLib] sent library: " + libName);
        }
    }
}
