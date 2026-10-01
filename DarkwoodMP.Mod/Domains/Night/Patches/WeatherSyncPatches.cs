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

            // Client clock does not run Rain.onUpdateTime, so this prefix never
            // used to see the strike. Lightning is sent with the weather packet.
            return false;
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

    /// <summary>
    /// Host lightning is a one-frame clock check. Peers do not run that clock,
    /// so the flash and thunder have to be sent when they happen.
    /// </summary>
    [HarmonyPatch(typeof(Rain), "onUpdateTime")]
    public static class RainHostLightningSyncPatch
    {
        private static float _lightningBefore; // process-scoped: Prefix/Postfix pair of one call
        private static bool _wasRaining; // process-scoped: Prefix/Postfix pair of one call

        private static void Prefix(Rain __instance)
        {
            _lightningBefore = __instance != null ? __instance.lightningTime : 0f;
            _wasRaining = __instance != null && __instance.Raining;
        }

        private static void Postfix(Rain __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected || __instance == null)
                return;

            byte strike = 0;
            // A real bolt advances lightningTime from the current clock.
            // Rain starting also rewrites lightningTime; that is not a bolt.
            if (_wasRaining && _lightningBefore == Core.time && __instance.lightningTime != _lightningBefore)
                strike = 1;
            else if (__instance.Raining
                && Core.time == __instance.lightningTime
                && Player.Instance != null
                && Player.Instance.whereAmI != null
                && Player.Instance.whereAmI.inUndergroundLocation)
            {
                // Host is indoors, so vanilla skipped the flash. Someone outside
                // still needs it, and the next bolt has to move forward.
                __instance.lightningTime = Core.time + UnityEngine.Random.Range(10, 50);
                strike = 1;
            }
            else if (__instance.preRainLightning && __instance.rainToday
                && Core.time == __instance.preRainLightningTime
                && Singleton<Controller>.Instance != null
                && Singleton<Controller>.Instance.day != 1
                && (Singleton<NightScenarios>.Instance == null || Singleton<NightScenarios>.Instance.scenarioId != 1))
                strike = 2;
            if (strike == 0)
                return;

            net.SendWeatherSyncWithStrike(strike);
        }
    }
}
