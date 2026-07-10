using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Client never picks its own night scenario — host pushes via ScenarioSync /
    /// ScenarioStateSync and we assign currentScenario directly.
    /// </summary>
    [HarmonyPatch(typeof(NightScenarios), "setCurrentScenario")]
    public static class ClientScenarioBlockPatch
    {
        private static bool Prefix()
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected && net.Role == NetworkRole.Client)
                return false;
            return true;
        }
    }

    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(NightScenarios), "setCurrentScenario")]
    public static class HostScenarioSyncPatch
    {
        private static void Postfix(NightScenarios __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role != NetworkRole.Host || !net.IsConnected)
                return;
            if (LanNetworkManager.IsApplyingRemoteState)
                return;
            if (__instance.currentScenario == null)
                return;

            net.SendScenarioSync(new ScenarioSyncMessage
            {
                ScenarioName = __instance.currentScenario.name
            });
        }
    }
}
