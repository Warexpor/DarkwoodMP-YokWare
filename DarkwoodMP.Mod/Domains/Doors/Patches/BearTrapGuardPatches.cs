using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Blocks left-click interactions with an occupied bear trap only (per-trap, not global).
    /// </summary>
    [HarmonyPatch(typeof(Player), "itemDefaultAction")]
    public static class BearTrapLeftClickGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                return true;

            Player player = Player.Instance;
            if (player == null || player.selectedObject == null)
                return true;

            Item item = player.selectedObject.GetComponent<Item>();
            if (item == null)
                return true;

            string name = item.name.ToLowerInvariant();
            if (!TrapNameHelper.IsTrap(name))
                return true;

            if (!net.IsTrapOccupied(item.gameObject))
                return true;

            ModRuntime.LegacyInfo("[Trap] blocked left-click on \""
                + item.name + "\" — player still trapped in this trap");
            return false;
        }
    }

    /// <summary>
    /// Blocks context-menu interactions with an occupied bear trap only.
    /// </summary>
    [HarmonyPatch(typeof(InputScript), "HandleIconSelectionFromContextMenu")]
    public static class BearTrapContextMenuGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                return true;

            var itemMenu = Singleton<ItemMenu>.Instance;
            if (itemMenu == null || itemMenu.selectedObject == null)
                return true;

            Item item = itemMenu.selectedObject.GetComponent<Item>();
            if (item == null)
                return true;

            string name = item.name.ToLowerInvariant();
            if (!TrapNameHelper.IsTrap(name))
                return true;

            if (!net.IsTrapOccupied(item.gameObject))
                return true;

            ModRuntime.LegacyInfo("[Trap] blocked context-menu on \""
                + item.name + "\" — player still trapped in this trap");
            return false;
        }
    }
}
