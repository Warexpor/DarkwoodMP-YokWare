using System;
using System.Collections.Generic;
using System.Reflection;
using DarkwoodMP.GameLogic;
using DarkwoodMP.Network;
using DarkwoodMP.Packets;
using HarmonyLib;

namespace DarkwoodMP.Patches;

/// <summary>
/// Broadcasts rain/fog/lightning state so every machine shows the same weather.
/// Weather is global world-state, so - like day/night and the authority clock -
/// only the elected time authority broadcasts it; everyone else applies it via
/// WeatherSync. Fires on the game's own Rain.startRain/stopRain/startFog/stopFog.
/// </summary>
public sealed class Weather_Patch : IPatch
{
    public (HarmonyLib.Harmony harmony, MethodInfo method) Apply(HarmonyLib.Harmony baseHarmony, MethodInfo target)
    {
        var statics = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        baseHarmony.Patch(target,
            postfix: new HarmonyMethod(typeof(Weather_Patch).GetMethod(nameof(Postfix), statics)!));
        return (baseHarmony, target);
    }

    public IEnumerable<(string typeName, string methodName)> TargetMethods()
    {
        yield return ("Rain", "startRain");
        yield return ("Rain", "stopRain");
        yield return ("Rain", "startFog");
        yield return ("Rain", "stopFog");
    }

    public static void Postfix(object __instance)
    {
        try
        {
            if (RemoteApply.Active) return;
            var manager = NetworkManager.Instance;
            if (manager == null || !manager.IsConnected || !manager.IsTimeAuthority) return;
            if (__instance is not Rain rain) return;

            var network = DarkwoodMP.DependencyInjection.ServiceLocator.Resolve<NetworkLayer>();
            if (network == null || !network.IsConnected) return;

            var payload = WeatherSync.Encode(rain);
            if (payload.StartsWith("weather:", StringComparison.Ordinal))
                payload = payload.Substring("weather:".Length);
            network.SendReliable(new WeatherStatePacket
            {
                PlayerId = Math.Max(network.LocalClientId, 0),
                Payload = payload
            });
        }
        catch (Exception ex)
        {
            ModLogger.Error($"[Weather_Patch] {ex.Message}");
        }
    }
}
