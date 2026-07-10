using DWMPHorde.Players;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// When any remote proxy opens inventory, destroys their held item to prevent
    /// visual desyncs and duplicate item references on the host side.
    /// </summary>
    [HarmonyPatch(typeof(Player), "getIntoInventory")]
    public static class GetIntoInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            if (!PlayerControlRouter.HasSecond)
                return;

            Player proxy = PlayerControlRouter.GetProxyByInstance(__instance);
            if (proxy == null)
                return;

            ModRuntime.LegacyInfo($"[getIntoInventory Patch] proxy {proxy.name} called. heldItem={__instance.heldItem}, invOpen={__instance.Inventory?.open}");

            if (__instance.heldItem != null)
            {
                UnityEngine.Object.Destroy(__instance.heldItem);
                __instance.heldItem = null;
            }
        }
    }

    /// <summary>
    /// Logs remote proxy inventory toggle attempts for debugging inventory-state desyncs.
    /// </summary>
    [HarmonyPatch(typeof(Player), "initiateOpenCloseInventory", new System.Type[0])]
    public static class InitiateOpenCloseInventoryNoParamPatch
    {
        private static void Prefix(Player __instance)
        {
            if (!PlayerControlRouter.HasSecond)
                return;
            Player proxy = PlayerControlRouter.GetProxyByInstance(__instance);
            if (proxy == null)
                return;
            ModRuntime.LegacyInfo($"[Proxy {proxy.name} initiateOpenCloseInventory()] entered. wantToInv={__instance.wantToInventory}, gotInv={__instance.gotInventory}, gettingInv={__instance.gettingInventory}, hidingInv={__instance.hidingInventory}");
        }
    }

    /// <summary>
    /// Logs remote proxy inventory close events.
    /// </summary>
    [HarmonyPatch(typeof(Player), "closeInventory")]
    public static class CloseInventoryPatch
    {
        private static void Prefix(Player __instance)
        {
            if (!PlayerControlRouter.HasSecond)
                return;
            Player proxy = PlayerControlRouter.GetProxyByInstance(__instance);
            if (proxy == null)
                return;
            ModRuntime.LegacyInfo($"[Proxy {proxy.name} closeInventory] called. open={__instance.Inventory?.open}, craftOpen={__instance.Crafting?.open}");
        }
    }
}