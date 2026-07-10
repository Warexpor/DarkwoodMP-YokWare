using System.Linq;
using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Blocks left-click interactions with bear traps when a remote player
    /// is currently trapped. Intercepts at Player.itemDefaultAction() — the
    /// entry point for ALL left-click world interactions — before any per-method
    /// dispatch (activate / getDroppedItem / attemptToDisarm) is reached.
    /// </summary>
    [HarmonyPatch(typeof(Player), "itemDefaultAction")]
    public static class BearTrapLeftClickGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (ModRuntime.Network is LanNetworkManager net)
            {
                if (!net.HasAnyTrappedPlayer)
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

                ModRuntime.LegacyInfo("[Trap] blocked left-click on \""
                    + item.name + "\" — remote player still trapped");
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Blocks context-menu (right-click → icon selection) interactions with
    /// bear traps when a remote player is currently trapped.
    /// InputScript.HandleIconSelectionFromContextMenu() is the single dispatch
    /// point for ALL context-menu icon selections, so intercepting here catches
    /// PickUp, Disarm, and any other action the menu offers on a trap.
    /// </summary>
    [HarmonyPatch(typeof(InputScript), "HandleIconSelectionFromContextMenu")]
    public static class BearTrapContextMenuGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (ModRuntime.Network is LanNetworkManager net)
            {
                if (!net.HasAnyTrappedPlayer)
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

                ModRuntime.LegacyInfo("[Trap] blocked context-menu on \""
                    + item.name + "\" — remote player still trapped");
                return false;
            }
            return true;
        }
    }
}
