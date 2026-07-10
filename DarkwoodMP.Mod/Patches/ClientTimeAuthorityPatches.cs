using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Audit C1: host is sole day/night clock authority.
    /// Client FixedUpdate must not advance CurrentTime / fire refreshTime edges.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class ClientTimeFixedUpdateSuppressPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network;
            if (net == null || !net.IsConnected)
                return true;
            if (!CoopTimePolicy.ShouldSuppressClientClock(net.IsConnected, net.Role == NetworkRole.Client))
                return true;

            // Skip entire FixedUpdate time tick on client (incl. inventory refresh interval).
            // Host TimeSync + refreshTimeNoLogic keep clock UI / ambient in sync.
            return false;
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
