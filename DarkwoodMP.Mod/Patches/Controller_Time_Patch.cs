using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Patches Darkwood's global Controller singleton to broadcast day/night transitions.
/// Verified game API: Controller.startNightMode(), Controller.endNightMode(),
/// Controller.startDay(), field `day` (int).
/// Only the host sends; clients apply the state via WorldSync.
/// </summary>
public sealed class Controller_Time_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var postfixName = target.Name switch
        {
            "startNightMode" => nameof(NightStartPostfix),
            "endNightMode" => nameof(NightEndPostfix),
            _ => nameof(DayStartPostfix)
        };
        var postfix = new HarmonyMethod(typeof(Controller_Time_Patch).GetMethod(postfixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!);
        baseHarmony.Patch(target, postfix: postfix);
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Controller", "startNightMode");
        yield return ("Controller", "endNightMode");
        yield return ("Controller", "startDay");
    }

    public static void NightStartPostfix(object __instance) => SendState(__instance, true);
    public static void NightEndPostfix(object __instance) => SendState(__instance, false);
    public static void DayStartPostfix(object __instance) => SendState(__instance, false);

    private static void SendState(object controller, bool isNight)
    {
        try
        {
            if (RemoteApply.Active) return;

            var manager = NetworkManager.Instance;
            // Time authority = in-game host, or (on a dedicated server, where
            // nobody is IsHost) the elected lowest-id client. Without this,
            // day/night transitions were broadcast by NOBODY in dedicated mode.
            if (manager == null || !manager.IsConnected || !manager.IsTimeAuthority) return;

            var dayField = controller.GetType().GetField("day",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var day = dayField?.GetValue(controller) is int d ? d : manager.DayNumber;

            manager.DayNumber = day;
            manager.IsNight = isNight;

            var worldSync = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<GameLogic.WorldSync>();
            worldSync?.SyncDayNightChange(day, isNight);
            ModLogger.Msg($"[Controller_Time_Patch] Broadcast day {day}, night={isNight}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Controller_Time_Patch] {ex.Message}");
        }
    }
}
