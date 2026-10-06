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
            if (HostApplyGuard.Active) return;
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
            if (HostApplyGuard.Active) return;
            if (__instance == null || string.IsNullOrEmpty(__instance.type)) return;
            int amt = __instance.amount;
            PeerItemPresence.SendLocalChange(__instance.type, amt);
        }
    }
    /// <summary>
    /// Hotbar.addItemType does not go through Inventory.addItemTypeToPlayer
    /// (e.g. the compressor turning empty oxygen tanks full). Keep PeerItemPresence live.
    /// </summary>
    [HarmonyPatch(typeof(Inventory), "addItemType", new[] { typeof(string), typeof(int) })]
    public static class PeerHasItemHotbarGivePatch
    {
        private static void Postfix(Inventory __instance, string type)
        {
            if (HostApplyGuard.Active) return;
            if (string.IsNullOrEmpty(type)) return;
            if (Player.Instance == null || Player.Instance.Hotbar == null) return;
            if (!ReferenceEquals(__instance, Player.Instance.Hotbar)) return;
            int amt = 0;
            try { amt = Player.Instance.Hotbar.getItemAmount(type); }
            catch { return; }
            PeerItemPresence.SendLocalChange(type, amt);
        }
    }
}
