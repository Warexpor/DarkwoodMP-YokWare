using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    [HarmonyPatch(typeof(Inventory), "addItemTypeToPlayer")]
    public static class PeerHasItemGivePatch
    {
        private static void Postfix(object[] __args)
        {
            if (DialogHostApplyGuard.Active) return;
            if (__args == null || __args.Length < 1) return;
            string type = __args[0] as string;
            if (string.IsNullOrEmpty(type)) return;
            if (Player.Instance == null || Player.Instance.Inventory == null) return;
            InvItemClass item = Player.Instance.Inventory.getItemInPlayer(type);
            int amt = item != null ? item.amount : 0;
            PeerItemPresence.SendLocalChange(type, amt);
        }
    }

    [HarmonyPatch(typeof(InvItemClass), "removeAmount")]
    public static class PeerHasItemRemovePatch
    {
        private static void Postfix(InvItemClass __instance)
        {
            if (DialogHostApplyGuard.Active) return;
            if (__instance == null || string.IsNullOrEmpty(__instance.type)) return;
            int amt = __instance.amount;
            PeerItemPresence.SendLocalChange(__instance.type, amt);
        }
    }
}
