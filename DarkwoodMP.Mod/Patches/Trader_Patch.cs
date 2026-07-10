using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Trade absolute model C (Horde TradeSyncPatches):
/// - success-only absolute stock after acceptTrade
/// - restock: time-authority only; clients skip randomizeTraderInv
/// - join bulk via TradeInventorySync
/// </summary>
public sealed class Trader_Patch : IPatch
{
    private static readonly Dictionary<string, int> _buyTraySnapshot = new Dictionary<string, int>();

    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        if (target.DeclaringType!.Name == "NPC")
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Trader_Patch).GetMethod(nameof(RestockPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Trader_Patch).GetMethod(nameof(RestockPostfix), statics)!));
        }
        else
        {
            baseHarmony.Patch(target,
                prefix: new HarmonyMethod(typeof(Trader_Patch).GetMethod(nameof(AcceptPrefix), statics)!),
                postfix: new HarmonyMethod(typeof(Trader_Patch).GetMethod(nameof(AcceptPostfix), statics)!));
        }
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("NPC", "randomizeTraderInv");
        yield return ("DialogueWindow", "acceptTrade");
    }

    /// <summary>Non-authority clients must not independently restock.</summary>
    public static bool RestockPrefix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return true;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected) return true;
            if (!manager.IsTimeAuthority)
                return false;
            return true;
        }
        catch { return true; }
    }

    public static void RestockPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected || !manager.IsTimeAuthority) return;
            if (__instance is not NPC npc || !npc.trader) return;
            TradeInventorySync.BroadcastNpcInventory(npc);
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Trader_Patch] RestockPostfix: {ex.Message}");
        }
    }

    public static void AcceptPrefix(object __instance)
    {
        _buyTraySnapshot.Clear();
        try
        {
            if (__instance is not DialogueWindow dw || dw.exchangeTrader == null) return;
            var all = dw.exchangeTrader.getAllItems();
            for (int i = 0; i < all.Count; i++)
            {
                if (InvItemClass.isNull(all[i])) continue;
                var type = all[i].type;
                if (string.IsNullOrEmpty(type)) continue;
                if (_buyTraySnapshot.ContainsKey(type))
                    _buyTraySnapshot[type] += all[i].amount;
                else
                    _buyTraySnapshot[type] = all[i].amount;
            }
        }
        catch { }
    }

    public static void AcceptPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active)
            {
                _buyTraySnapshot.Clear();
                return;
            }
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected)
            {
                _buyTraySnapshot.Clear();
                return;
            }
            if (__instance is not DialogueWindow window || window.npc == null)
            {
                _buyTraySnapshot.Clear();
                return;
            }

            // Vanilla success empties both trays
            bool buyEmpty = true;
            if (window.exchangeTrader != null)
            {
                var rem = window.exchangeTrader.getAllItems();
                buyEmpty = rem == null || rem.Count == 0;
            }
            bool sellEmpty = true;
            if (window.exchangePlayer != null)
            {
                var rem = window.exchangePlayer.getAllItems();
                sellEmpty = rem == null || rem.Count == 0;
            }
            _buyTraySnapshot.Clear();
            if (!buyEmpty || !sellEmpty)
                return; // failed trade

            TradeInventorySync.BroadcastNpcInventory(window.npc);
            ModLogger.Msg($"[Trader_Patch] Trade success — absolute stock '{window.npc.name}'");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Trader_Patch] AcceptPostfix: {ex.Message}");
            _buyTraySnapshot.Clear();
        }
    }
}
