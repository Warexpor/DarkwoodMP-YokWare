using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>waitToDamageAroundMe</c> only damages <see cref="Player.Instance"/>.
    /// With remotes present this is an AoE aura: every living body in falloff range
    /// takes the same vanilla formula; proxy hits relay via <see cref="ProxyDamagePatch"/>.
    /// </summary>
    [HarmonyPatch(typeof(Character), "waitToDamageAroundMe")]
    public static class HostDamageAroundMePatch
    {
        private static bool Prefix(Character __instance)
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return true;
            if (!PlayerPositionManager.HasRemotePlayer)
                return true;
            if (__instance == null || !__instance.damagesAroundMe)
                return true;

            float range = __instance.aroundMeDamageRange;
            if (range <= 0f)
                return false;

            Transform attacker = __instance.transform;
            int aroundDmg = __instance.aroundMeDamage;

            // Host body — same formula and shake/noise as vanilla.
            Player host = Player.Instance;
            if (host != null && host._transform != null)
            {
                float hostDist = Core.trueDistance(host._transform.position, attacker.position);
                if (hostDist <= range)
                {
                    float hostFalloff = (range / 2f - hostDist) / range;
                    if (hostFalloff > 0f)
                    {
                        host.getHit(
                            (float)aroundDmg * hostFalloff,
                            attacker,
                            CanCutInHalf: false,
                            byPlayer: false,
                            canInterrupt: false,
                            normalHit: false);
                    }
                    Singleton<CamMain>.Instance.shake(0.3f, (float)aroundDmg * hostFalloff);
                    Singleton<UI>.Instance.tweenNoise(
                        Mathf.Clamp(range / 4f / hostDist, 0.2f, 0.6f));
                }
            }

            // Every remote in falloff range (explosion-style multi-body), not just nearest.
            var net = LanNetworkManager.Instance;
            if (net == null)
                return false;

            foreach (var proxy in net.GetAllProxies())
            {
                if (proxy == null)
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb == null || !cb.alive)
                    continue;
                if (DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    continue;

                float dist = Core.trueDistance(attacker.position, proxy.transform.position);
                if (dist > range)
                    continue;

                float falloff = (range / 2f - dist) / range;
                if (falloff <= 0f)
                    continue;

                // ProxyDamagePatch forwards CharBase.getHit → DamagePlayer to the peer.
                cb.getHit(
                    (float)aroundDmg * falloff,
                    attacker,
                    CanCutInHalf: false,
                    byPlayer: false,
                    canInterrupt: false,
                    normalHit: false);
            }

            return false;
        }
    }
}
