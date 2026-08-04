using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.3 Event triggers / requirements:
    /// Vanilla EventTriggers.OnTriggerEnter/Exit only reacts to Player.Instance.
    /// Remote peers are RemotePlayerProxy — without proxy enter, a client walking
    /// into a volume never fires host GameEvents (4.2 GameEventsFired).
    ///
    /// multipleFire GEs (dream karuzela RotateIt, ambients) are intentionally NOT
    /// broadcast (GameEventsFiredPatch) — they were meant to run locally on each
    /// peer. That only works if:
    ///   1) Client Player.Instance still gets vanilla OnTriggerEnter (do NOT suppress),
    ///   2) Each peer also runs proxy enter for other players' proxies (host body on
    ///      client, client body on host).
    /// One-shots stay host-auth: GameEventsFiredPatch Prefix blocks client one-shot
    /// fire(); host broadcasts; client Apply under NetworkApplyGuard.
    /// </summary>
    internal static class EventTriggersAuth
    {
        internal static bool IsMultiplayerConnected()
        {
            return ModRuntime.Network != null && ModRuntime.Network.IsConnected;
        }

        internal static bool IsHost()
        {
            return IsMultiplayerConnected() && ModRuntime.Network.Role == NetworkRole.Host;
        }

        internal static bool CanFireTriggers(EventTriggers et)
        {
            if (et == null) return false;
            if ((Core.loadingGame || Singleton<SaveManager>.Instance.dontFireTriggers) && !et.canFireWhenLoadingGame)
                return false;
            if (!Core.worldGenFinished())
                return false;
            return true;
        }

        /// <summary>Footstep / SoundArea volumes — local body only (proxy path owns peer steps).</summary>
        internal static bool IsLocalBodyOnlyVolume(string etName)
        {
            if (string.IsNullOrEmpty(etName)) return false;
            return etName.IndexOf("footsteps", System.StringComparison.OrdinalIgnoreCase) >= 0
                || etName.IndexOf("soundarea", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>
    /// After vanilla Player.Instance handling, fire area enter for remote proxies
    /// on every peer (host + client). Host: client walked in. Client: host walked in
    /// (multipleFire world FX like karuzela RotateIt).
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class EventTriggersProxyEnterPatch
    {
        private static void Postfix(EventTriggers __instance, Collider other)
        {
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;
            if (Singleton<OutsideLocations>.Instance != null && Singleton<OutsideLocations>.Instance.loading)
                return;

            // Local Player.Instance enter — host clears proxy threat preference.
            if (other.GetComponentInParent<Player>() != null)
            {
                if (EventTriggersAuth.IsHost())
                    ThreatTriggerContext.NoteHostEnter();
                return;
            }

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            if (EventTriggersAuth.IsLocalBodyOnlyVolume(__instance.name))
                return;

            // Mirror vanilla multi-collider guard: only first "logical" enter counts.
            Vector3 pos = proxy.transform.position;
            int mask = 1 << __instance.gameObject.layer;
            if (__instance.entered != 0 && Helpers.isComponentAtPos(pos, mask, __instance))
                return;

            if (EventTriggersAuth.IsHost())
                ThreatTriggerContext.NoteProxyEnter(proxy);

            __instance.fireEventTrigger(EventTrigger.Type.area);
            __instance.entered++;
            ModRuntime.LegacyInfo(
                $"[EventTriggers] proxy enter area p{proxy.PlayerId} on {__instance.name} entered={__instance.entered}");
        }
    }

    /// <summary>
    /// Proxy exit on every peer — pairs with enter (multipleFire EventTrigger resets on exit).
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class EventTriggersProxyExitPatch
    {
        private static void Postfix(EventTriggers __instance, Collider other)
        {
            if (!EventTriggersAuth.IsMultiplayerConnected()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            if (EventTriggersAuth.IsLocalBodyOnlyVolume(__instance.name))
                return;

            Vector3 pos = proxy.transform.position;
            int mask = 1 << __instance.gameObject.layer;
            // Still overlapping (other collider on same proxy) — ignore.
            if (Helpers.isComponentAtPos(pos, mask, __instance))
                return;

            __instance.exited++;
            if (__instance.exited >= __instance.entered)
            {
                __instance.fireEventTriggerExit(EventTrigger.Type.area);
                ModRuntime.LegacyInfo(
                    $"[EventTriggers] proxy exit area p{proxy.PlayerId} on {__instance.name} exited={__instance.exited}");
            }
        }
    }

    /// <summary>
    /// Host: onInSight / onOutOfSight also consider remote proxies (LOS via Core.canSee).
    /// Vanilla only checks Player.Instance FOV — client-only sight would never fire host events.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "isCurrentlyInSightOfPlayer")]
    public static class EventTriggersProxySightPatch
    {
        private static void Postfix(EventTriggers __instance, ref bool __result)
        {
            if (__result) return;
            if (!EventTriggersAuth.IsHost()) return;
            if (__instance == null) return;

            var net = LanNetworkManager.Instance;
            if (net == null) return;

            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null) continue;
                if (ProxyInSightOf(__instance, proxy.transform))
                {
                    __result = true;
                    return;
                }
            }
        }

        private static bool ProxyInSightOf(EventTriggers et, Transform proxyT)
        {
            if (proxyT == null || et == null) return false;

            Vector3 dest = et.transform.position;
            float dist = Core.trueDistance(proxyT.position, dest);
            if (dist >= 800f)
                return false;

            int radius = (int)et.inSightOfPlayerRadius;
            if (radius > 0)
            {
                if (Player.Instance != null)
                    return Player.Instance.canSee(proxyT, et.transform, radius);
                return Core.canSee(proxyT, et.transform);
            }

            Vector3 toTarget = dest - proxyT.position;
            float halfFov = 55f;
            if (dist > 6f && Vector3.Angle(toTarget, proxyT.up) > halfFov)
                return false;

            return Core.canSee(proxyT, et.transform);
        }
    }
}
