using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Clients must not run autonomous rain/fog schedule from Rain.onUpdateTime —
    /// host WeatherSync owns start/stop/fog. Local lightning while raining is allowed.
    /// </summary>
    [HarmonyPatch(typeof(Rain), "onUpdateTime")]
    public static class RainClientScheduleSuppressPatch
    {
        private static bool Prefix(Rain __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;

            // Visual-only lightning while host says it is raining
            try
            {
                if (__instance.Raining
                    && Player.Instance != null
                    && Player.Instance.whereAmI != null
                    && !Player.Instance.whereAmI.inUndergroundLocation
                    && Core.time == __instance.lightningTime
                    && Singleton<CamMain>.Instance != null
                    && Singleton<CamMain>.Instance.lightning != null)
                {
                    Singleton<CamMain>.Instance.lightning.strike();
                    // Keep host lightningTime until next WeatherSync (do not re-randomize)
                }
            }
            catch
            {
                // Cam/player not ready
            }

            return false; // skip vanilla schedule (rain start/stop/fog by local day timer)
        }
    }

    /// <summary>Sends the host's weather state whenever rain starts, stops, or fog toggles.</summary>
    [HarmonyPatch(typeof(Rain), "startRain")]
    public static class RainStartPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "stopRain")]
    public static class RainStopPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "startFog")]
    public static class FogStartPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "stopFog")]
    public static class FogStopPatch
    {
        private static void Postfix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host) return;
            net.SendWeatherSync();
        }
    }
}
