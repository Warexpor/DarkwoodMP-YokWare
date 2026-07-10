using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Sleep / time skip (v0.4). Day and night TRANSITIONS have been synced since
/// v0.1 (Controller_Time_Patch), but sleeping jumps Controller.gameTime
/// mid-phase - the sleeper's clock would run hours ahead until the next
/// transition. A postfix on Player.onEndSleep broadcasts the absolute clock;
/// the other machine adopts it (forward jumps only, stale packets ignored).
/// </summary>
public sealed class Sleep_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Sleep_Patch).GetMethod(nameof(EndSleepPostfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Player", "onEndSleep");
    }

    public static void EndSleepPostfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            if (__instance is not Player player
                || player.transform.root.name.StartsWith("RemotePlayer_")) return;

            var controller = UnityEngine.Object.FindObjectOfType<Controller>();
            if (controller == null) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            network.SendReliable(new GameTimeSyncPacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                GameTime = controller.gameTime
            });
            ModLogger.Msg($"[Sleep_Patch] Slept - broadcast game time {controller.gameTime:F1}");
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Sleep_Patch] {ex.Message}");
        }
    }
}
