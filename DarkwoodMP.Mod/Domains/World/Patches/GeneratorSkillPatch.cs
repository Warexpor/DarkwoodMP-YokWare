using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Generators burn fuel on the host only (clients' clocks don't run), at the rate of the host's
    /// Electrician skill (vanilla <c>Player.electricityModifier</c>). A client who took the skill got
    /// nothing from it. The shared generator now runs at the best rate any player has.
    /// </summary>
    [HarmonyPatch(typeof(Generator), nameof(Generator.drainFuel))]
    public static class GeneratorSkillPatch
    {
        private const float ElectricianModifier = 0.5f; // vanilla PlayerSkills.Electrician

        private static void Prefix(out float __state)
        {
            __state = -1f;
            Player host = Player.Instance;
            if (host == null || !NetGuard.Host(out LanNetworkManager net))
                return;
            float best = host.electricityModifier;
            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy != null && proxy.RemoteSkills.Contains("electrician") && ElectricianModifier < best)
                    best = ElectricianModifier;
            }
            if (best == host.electricityModifier)
                return;
            __state = host.electricityModifier;
            host.electricityModifier = best;
        }

        private static void Finalizer(float __state)
        {
            if (__state >= 0f && Player.Instance != null)
                Player.Instance.electricityModifier = __state;
        }
    }
}
