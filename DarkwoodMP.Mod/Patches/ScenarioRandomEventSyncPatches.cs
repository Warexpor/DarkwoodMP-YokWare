using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    internal static class ScenarioPendingEventState
    {
        public static int PendingEventIndex = -1;
        public static NightScenario PendingScenario;
    }

    /// <summary>
    /// Client: only host-authoritative night custom events fire.
    /// When host sends ScenarioEventFired, pending index forces that event's
    /// frequencyMet true once; all other client auto-fires are blocked.
    /// </summary>
    [HarmonyPatch(typeof(CustomEvent), "frequencyMet")]
    public static class FrequencyMetRedirectPatch
    {
        private static bool Prefix(CustomEvent __instance, ref bool __result)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            bool isClient = net != null && net.IsConnected && net.Role == NetworkRole.Client;

            if (ScenarioPendingEventState.PendingEventIndex < 0 || ScenarioPendingEventState.PendingScenario == null)
            {
                // No host-driven event waiting: clients must not auto-fire night events.
                if (isClient)
                {
                    __result = false;
                    return false;
                }
                return true;
            }

            var ps = ScenarioPendingEventState.PendingScenario;
            int idx = ScenarioPendingEventState.PendingEventIndex;

            if (ps.currentEvent == __instance)
            {
                ScenarioPendingEventState.PendingEventIndex = -1;
                ScenarioPendingEventState.PendingScenario = null;
                __result = false;
                return false;
            }

            bool isPending = idx < ps.customEventAndInts.Count
                && ps.customEventAndInts[idx].customEvent == __instance;

            if (isPending)
            {
                ScenarioPendingEventState.PendingEventIndex = -1;
                ScenarioPendingEventState.PendingScenario = null;
                __result = true;
                return false;
            }

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(NightScenario), "checkFrequencies")]
    public static class HostCheckFrequenciesPostfix
    {
        private static void Postfix(NightScenario __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            if (__instance.currentEvent == null)
                return;

            for (int i = 0; i < __instance.customEventAndInts.Count; i++)
            {
                var cei = __instance.customEventAndInts[i];
                if (cei.customEvent == __instance.currentEvent && cei.timeToStart == 0)
                {
                    net.SendScenarioEventFired(__instance.nightId, i);
                    return;
                }
            }
        }
    }
}
