using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client: night events start only from the host's <c>ScenarioEventFired</c>. Vanilla
    /// <c>checkFrequencies</c> starts one (a fixed-time one, or a frequency roll with its own random
    /// chance) only while no event runs; the client lets it run then just to end its current event on
    /// the shared clock. The old way (a pending index that made the next local <c>frequencyMet</c>
    /// true) stayed stuck while the client stood outside a location or its own event had not ended,
    /// and still passed the event through the client's own random chance roll, so the event could
    /// start late, at a wrong time, or not at all.
    /// </summary>
    [HarmonyPatch(typeof(NightScenario), "checkFrequencies")]
    public static class ClientNightEventStartBlockPatch
    {
        private static bool Prefix(NightScenario __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Client)
                return true;
            return __instance != null && __instance.currentEvent != null;
        }
    }

    [HarmonyPatch(typeof(NightScenario), "checkFrequencies")]
    public static class HostCheckFrequenciesPostfix
    {
        // Host was re-broadcasting the same event index every tick while currentEvent
        // stayed set → client ScenarioEventFired spam + GameEvents queue thrash (fps~3).
        private static NightScenario _lastSentScenario;
        private static int _lastSentEventIndex = int.MinValue;

        /// <summary>Session boundary: a new world's first event must not match the old world's last one.</summary>
        public static void Reset()
        {
            _lastSentScenario = null;
            _lastSentEventIndex = int.MinValue;
        }

        private static void Postfix(NightScenario __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;

            if (__instance.currentEvent == null)
            {
                // The event ended: a later repeat of the same (night, event) is a new firing.
                Reset();
                return;
            }

            for (int i = 0; i < __instance.customEventAndInts.Count; i++)
            {
                var cei = __instance.customEventAndInts[i];
                if (cei.customEvent == __instance.currentEvent)
                {
                    // By scenario, not nightId: two nights share an id.
                    if (__instance == _lastSentScenario && i == _lastSentEventIndex)
                        return;
                    _lastSentScenario = __instance;
                    _lastSentEventIndex = i;
                    net.SendScenarioEventFired(__instance.name, __instance.nightId, i, NightEventAnchor.TakeFiredAnchors());
                    return;
                }
            }
        }
    }
}
