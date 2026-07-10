using DWMPHorde.Players;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Patches to make the game's inventory and AI systems aware of multiple
    /// Player / Character instances (host player + remote proxy).
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "Start")]
    public static class InventoryStartPatch
    {
        /// <summary>Sanitize inventory slots on secondary player clones.</summary>
        private static void Postfix(Inventory __instance)
        {
            Player player = __instance.GetComponent<Player>();
            if (player == null || player.GetComponent<CoopPlayerMarker>() == null)
                return;

            CoopPlayerBootstrap.SanitizeInventorySlots(player);
        }
    }
}
