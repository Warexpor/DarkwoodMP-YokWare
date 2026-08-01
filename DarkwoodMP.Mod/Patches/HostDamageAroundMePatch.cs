using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// Vanilla <c>waitToDamageAroundMe</c> only damages <see cref="Player.Instance"/>.
    /// When the entity is chasing a remote proxy (or the proxy is nearer), apply the
    /// same falloff hit via HostMeleeSensor / DamagePlayer path (CharBase.getHit on proxy).
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

            Transform victimT = null;
            float victimDist = float.MaxValue;

            if (__instance.target != null)
            {
                var proxyTarget = __instance.target.GetComponent<RemotePlayerProxy>()
                    ?? __instance.target.GetComponentInParent<RemotePlayerProxy>();
                if (proxyTarget != null)
                {
                    victimT = proxyTarget.transform;
                    victimDist = Core.trueDistance(__instance.transform.position, victimT.position);
                }
            }

            if (victimT == null)
            {
                var net = LanNetworkManager.Instance;
                if (net != null)
                {
                    foreach (var proxy in net.GetAllProxies())
                    {
                        if (proxy == null) continue;
                        float d = Core.trueDistance(__instance.transform.position, proxy.transform.position);
                        if (d < victimDist)
                        {
                            victimDist = d;
                            victimT = proxy.transform;
                        }
                    }
                }
            }

            float hostDist = float.MaxValue;
            if (Player.Instance != null)
                hostDist = Core.trueDistance(__instance.transform.position, Player.Instance._transform.position);

            // Host nearer (or only host in range) — vanilla path.
            if (victimT == null || hostDist <= victimDist)
                return true;

            if (victimDist > __instance.aroundMeDamageRange)
                return false; // suppress vanilla host-only tick this cycle

            float num2 = (__instance.aroundMeDamageRange / 2f - victimDist) / __instance.aroundMeDamageRange;
            if (num2 <= 0f)
                return false;

            CharBase cb = victimT.GetComponent<CharBase>();
            if (cb == null || !cb.alive)
                return false;

            // ProxyDamagePatch forwards CharBase.getHit → DamagePlayer to the peer.
            cb.getHit(
                (float)__instance.aroundMeDamage * num2,
                __instance.transform,
                CanCutInHalf: false,
                byPlayer: false,
                canInterrupt: false,
                normalHit: false);
            return false;
        }
    }
}
