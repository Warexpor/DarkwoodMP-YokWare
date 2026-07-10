using DWMPHorde.Players;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Patches
{
    public static class InventorySafety
    {
        private static readonly List<Player> _playerBuffer = new List<Player>();
        private static readonly List<Inventory> _invBuffer = new List<Inventory>();

        public static void HealSlot(InvItemClass item)
        {
            if (item == null) return;

            _playerBuffer.Clear();
            CoopPlayerRegistry.GetAllPlayers(_playerBuffer);

            foreach (Player player in _playerBuffer)
            {
                if (player == null) continue;

                player.GetComponents(_invBuffer);
                foreach (Inventory inv in _invBuffer)
                {
                    if (inv == null || inv.slots == null) continue;
                    for (int i = 0; i < inv.slots.Count; i++)
                    {
                        InvSlot s = inv.slots[i];
                        if (s == null) continue;
                        if (s.invItem == item)
                        {
                            s.inventory = inv;
                            Traverse.Create(item).Field("_slot").SetValue(s);
                            if (item.baseClass == null)
                                item.assignClass();
                            return;
                        }
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(InvItemClass), "slot", MethodType.Getter)]
    public static class InvItemClassSlotAutoHeal
    {
        private static void Postfix(InvItemClass __instance, ref InvSlot __result)
        {
            if (__result != null || __instance == null) return;

            InventorySafety.HealSlot(__instance);
            __result = Traverse.Create(__instance).Field("_slot").GetValue<InvSlot>();
        }
    }

    [HarmonyPatch(typeof(Inventory), "show")]
    public static class InventoryShowSlotsSafe
    {
        private static void Prefix(Inventory __instance)
        {
            if (PlayerControlRouter.HasSecond)
            {
                Player owner = __instance.GetComponent<Player>();
                int invTypeVal = (int)__instance.invType;
                if (owner != null)
                {
                    Player proxy = PlayerControlRouter.GetProxyByInstance(owner);
                    if (proxy != null)
                    {
                        if (invTypeVal == 40)
                            ModRuntime.LegacyInfo($"[Proxy {proxy.name} Crafting.show] thisUI={__instance.thisUI}, open={__instance.open}, slots={__instance.slots?.Count}, isWorkbench={__instance.isWorkbench}, pos=({__instance.position.x},{__instance.position.y})");
                        if (invTypeVal != 40)
                            ModRuntime.LegacyInfo($"[Proxy {proxy.name} InvType{invTypeVal}.show] thisUI={__instance.thisUI}, open={__instance.open}, slots={__instance.slots?.Count}");
                    }
                }
            }
            if (__instance == null || __instance.slots == null) return;

            for (int i = 0; i < __instance.slots.Count; i++)
            {
                InvSlot slot = __instance.slots[i];
                if (slot == null) continue;

                if (slot.inventory == null)
                    slot.inventory = __instance;

                if (!InvItemClass.isNull(slot.invItem))
                {
                    if (slot.invItem.baseClass == null)
                        slot.invItem.assignClass();
                    if (slot.invItem.slot == null)
                        InventorySafety.HealSlot(slot.invItem);
                }
            }
        }
    }
}
