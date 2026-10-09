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
            var net = ModRuntime.Network;
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
            if (!NetGuard.Host(out var net)) return;
            net.SendWeatherSync();
        }
    }

    /// <summary>
    /// Host new day: vanilla rolls whether it rains today and the fog schedule. Nothing was sent
    /// until rain or fog next changed, so clients spent the day on yesterday's roll.
    /// </summary>
    [HarmonyPatch(typeof(Rain), "onNewDay")]
    public static class RainNewDaySyncPatch
    {
        private static void Postfix()
        {
            if (!NetGuard.Host(out var net)) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "stopRain")]
    public static class RainStopPatch
    {
        private static void Postfix()
        {
            if (!NetGuard.Host(out var net)) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "startFog")]
    public static class FogStartPatch
    {
        private static void Postfix()
        {
            if (!NetGuard.Host(out var net)) return;
            net.SendWeatherSync();
        }
    }

    [HarmonyPatch(typeof(Rain), "stopFog")]
    public static class FogStopPatch
    {
        private static void Postfix()
        {
            if (!NetGuard.Host(out var net)) return;
            net.SendWeatherSync();
        }
    }

    /// <summary>
    /// Weather inside an outside location. Vanilla hides the rain on entry
    /// (<c>Rain.hide</c>) and shows it on return (<c>unhide</c>); its schedule never ran in
    /// there because the clock stopped. With the shared clock it runs: rain could start in full
    /// view inside a village, and inside an underground pad it could not start or stop at all
    /// (vanilla gates both on <c>inUndergroundLocation</c>), so the host in a bunker left the
    /// players outside without rain. While the local player is in a pad, rain keeps its state
    /// and schedule but stays hidden, and shows on return.
    /// </summary>
    internal static class PadWeather
    {
        private static readonly AccessTools.FieldRef<Rain, bool> RainingField =
            AccessTools.FieldRefAccess<Rain, bool>("raining");
        private static readonly System.Action<Rain> FadeInClouds =
            AccessTools.MethodDelegate<System.Action<Rain>>(AccessTools.Method(typeof(Rain), "fadeInClouds"));

        /// <summary>Rain started out of sight; vanilla unhide alone does not bring it up.</summary>
        private static bool _showOnReturn;

        internal static void Reset() => _showOnReturn = false;

        internal static bool LocalInPad()
        {
            var ol = Singleton<OutsideLocations>.Instance;
            return ol != null && ol.playerInOutsideLocation;
        }

        /// <summary>The state half of vanilla <c>startRain</c>, with no clouds, overlay, drops or sound.</summary>
        internal static void StartHidden(Rain r)
        {
            if (r.Raining)
                return;
            RainingField(r) = true;
            r.timeToStart = Core.time;
            r.lightningTime = r.timeToStart + Random.Range(10, 50);
            _showOnReturn = true;
        }

        /// <summary>After vanilla <c>returningOnTeleportedPlayer</c> (its unhide already ran).</summary>
        internal static void ShowAfterReturn()
        {
            if (!_showOnReturn)
                return;
            _showOnReturn = false;
            Rain r = Singleton<Rain>.Instance;
            if (r == null || !r.Raining)
                return;
            FadeInClouds(r);
            r.resumeRain();
        }
    }

    /// <summary>
    /// Host inside a pad: vanilla <c>Rain.onUpdateTime</c> without its local presentation (see
    /// <see cref="PadWeather"/>). Runs after <see cref="RainHostLightningSyncPatch"/>'s prefix, so
    /// an advanced bolt time is still sent to the peers as a strike.
    /// </summary>
    [HarmonyPatch(typeof(Rain), "onUpdateTime")]
    public static class RainHostInPadPatch
    {
        [HarmonyPriority(Priority.Low)]
        private static bool Prefix(Rain __instance)
        {
            if (!Application.isPlaying || Player.Instance == null || !PadWeather.LocalInPad())
                return true;
            if (!NetGuard.ConnectedHost(out var net))
                return true;
            Rain r = __instance;
            Controller ctrl = Singleton<Controller>.Instance;

            // Vanilla startRain's first-night gate.
            if (Core.time == r.timeToStart && r.rainToday && !r.Raining
                && (!Core.randomGeneration
                    || (ctrl.day != 1 && Singleton<NightScenarios>.Instance.scenarioId != 1)))
            {
                PadWeather.StartHidden(r);
                net.SendWeatherSync();
            }
            if (r.Raining)
            {
                if (Core.time == r.lightningTime)
                    r.lightningTime = Core.time + Random.Range(10, 50);
                if (Core.time - r.timeToStart > r.duration)
                    r.Raining = false;
            }
            if (ctrl.CurrentTime != 0 && ctrl.CurrentTime != 1)
            {
                if (!r.fogFadedInToday && ctrl.isCurrentTime(r.timeToFadeInFog))
                    r.startFog();
                if (!r.fogFadedOutToday && ctrl.isCurrentTime(r.timeToFadeOutFog))
                    r.stopFog();
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(OutsideLocations), "returningOnTeleportedPlayer")]
    public static class RainShowOnReturnPatch
    {
        private static void Postfix() => PadWeather.ShowAfterReturn();
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
            var net = ModRuntime.Network;
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
