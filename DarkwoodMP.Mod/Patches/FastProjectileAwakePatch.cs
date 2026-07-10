using DWMPHorde.Networking;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Increases the raycast distance in FastProjectile so the bullet's forward
    /// detection sweep can reach the remote proxy (the default distance is only
    /// ~collider-radius, which is too short for reliable proxy hits).
    /// Applied to all projectiles when networked — safe because the proxy's
    /// CharBase.getHit is a no-op, so enemy bullets are harmless to the proxy.
    /// </summary>
    [HarmonyPatch(typeof(FastProjectile), "Awake")]
    public static class FastProjectileAwakePatch
    {
        /// <summary>
        /// Minimum raycast distance in world units.
        /// Bullets move ~14 units per physics tick at 700 u/s,
        /// so 15f guarantees the sweep covers the next tick's travel.
        /// </summary>
        private const float MinDistance = 15f;

        private static void Postfix(FastProjectile __instance)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || net.Role == NetworkRole.Offline)
                return;

            // Use Traverse to access the private 'distance' field
            var distField = Traverse.Create(__instance).Field("distance");
            float current = distField.GetValue<float>();
            if (current < MinDistance)
            {
                distField.SetValue(MinDistance);
                ModRuntime.LegacyInfo("[FastProjectileAwake] increased distance from " + current + " to " + MinDistance);
            }
        }
    }
}
