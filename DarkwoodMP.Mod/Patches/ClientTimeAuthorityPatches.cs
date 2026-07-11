using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Audit C1: host is sole day/night clock authority.
    /// Client must not advance CurrentTime / fire refreshTime edges, but must still
    /// run FixedUpdate inventory refresh (hotbar durability timers, etc.).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class ClientTimeFixedUpdateSuppressPatch
    {
        private static bool _forcedDoUpdateTimeOff;

        private static void Prefix(Controller __instance)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected
                || !CoopTimePolicy.ShouldSuppressClientClock(net.IsConnected, net.Role == NetworkRole.Client))
            {
                // Restore if we left co-op while forced off.
                if (_forcedDoUpdateTimeOff && __instance != null)
                {
                    __instance.DoUpdateTime = true;
                    _forcedDoUpdateTimeOff = false;
                }
                return;
            }

            if (__instance == null) return;
            // Keep refreshActiveItemsInInventories; only block CurrentTime++ / refreshTime.
            if (__instance.DoUpdateTime)
            {
                __instance.DoUpdateTime = false;
                _forcedDoUpdateTimeOff = true;
            }
        }
    }

    /// <summary>
    /// Belt-and-suspenders: if anything still calls refreshTime on a connected client
    /// (TimeSync used to; other systems might), strip day-chain edge handlers and only
    /// run ambient/clock UI. Host path unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "refreshTime")]
    public static class ClientRefreshTimeNoEdgesPatch
    {
        private static bool Prefix(Controller __instance, bool afterGameLoad)
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;
            if (!CoopTimePolicy.ShouldSuppressClientClock(net.IsConnected, net.Role == NetworkRole.Client))
                return true;
            // Applying remote TimeSync or any client-side refreshTime: no startDay/etc.
            try
            {
                __instance.refreshTimeNoLogic();
            }
            catch (System.Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning("[TimeAuth] refreshTimeNoLogic: " + ex.Message);
            }
            return false;
        }
    }
}
