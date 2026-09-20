using DWMPHorde.Networking;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Host is sole authority for morning freeze end (leave hideout / endAfterNight).
    /// Client must not destroy traders or bump time; request host, then TimeSync clears freeze.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "endAfterNight")]
    public static class EndAfterNightCoopPatch
    {
        private static bool Prefix(Controller __instance, bool byKillingTrader)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected)
                return true;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return true;

            // Client: never run SP world end (trader destroy + CurrentTime++ + refreshTime).
            // Host will end once and TimeSync IsAfterNight=false for everyone.
            if (net.Role == NetworkRole.Client)
            {
                if (__instance != null && __instance.isAfterNight)
                {
                    net.Send(NetMessageType.AfterNightEndRequest,
                        w => new AfterNightEndRequestMessage().Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo("[DayNight] client endAfterNight → AfterNightEndRequest (host-auth)");
                }
                return false;
            }

            return true;
        }

        private static void Postfix(Controller __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;

            // Immediate — 2s periodic TimeSync left peers frozen after host left hideout.
            net.SendTimeSyncTo(-1);
            ModRuntime.LegacyInfo("[DayNight] host endAfterNight → TimeSync");
        }
    }

    /// <summary>Host day-chain edges: flush TimeSync so peers do not lag up to 2s.</summary>
    [HarmonyPatch(typeof(Controller), "startDay")]
    public static class StartDayTimeSyncPatch
    {
        private static void Postfix()
        {
            FlushHostTime("startDay");
        }

        internal static void FlushHostTime(string reason)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (LanNetworkManager.IsApplyingRemoteState || TraverseHack.ApplyingFromNetwork)
                return;
            net.SendTimeSyncTo(-1);
            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DayNight] host " + reason + " → TimeSync");
        }
    }

    [HarmonyPatch(typeof(Controller), "startAfterNight")]
    public static class StartAfterNightTimeSyncPatch
    {
        private static void Postfix()
        {
            StartDayTimeSyncPatch.FlushHostTime("startAfterNight");
        }
    }

    [HarmonyPatch(typeof(Controller), "skipDay")]
    public static class SkipDayTimeSyncPatch
    {
        private static void Postfix()
        {
            StartDayTimeSyncPatch.FlushHostTime("skipDay");
        }
    }

    /// <summary>
    /// Host startBeforeDay is a full-screen fade + invuln. Clients only need a soft
    /// invuln window so they are not shredded while host runs the 5s blackout sequence
    /// before TimeSync morning arrives (no dual fade / dual karma).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "startBeforeDay")]
    public static class StartBeforeDayClientSoftPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;

            // Should not run on client (clock suppressed) — belt if something calls it.
            if (Player.Instance != null)
                Player.Instance.invulnerable = true;
            ModRuntime.LegacyInfo("[DayNight] client startBeforeDay suppressed (soft invuln only)");
            return false;
        }
    }
}
