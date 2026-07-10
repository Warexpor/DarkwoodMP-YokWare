using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// 4.3 Event triggers / requirements:
    /// Vanilla EventTriggers.OnTriggerEnter/Exit only reacts to Player.Instance.
    /// Remote peers are represented as RemotePlayerProxy on the host — without this
    /// patch, a client walking into an area volume never fires host GameEvents
    /// (and thus never reaches 4.2 GameEventsFired sync).
    ///
    /// Host: also treat proxy colliders as player enter/exit (same entered/exited counters).
    /// Client: skip local volume enter/exit when multiplayer is live (host is authority;
    /// one-shots already blocked in 4.2; avoids double multipleFire FX).
    /// Sight: host isCurrentlyInSightOfPlayer also true if any proxy has LOS.
    /// Requirements stay host-side Flags/world state (FlagSync + GameEvents cover most).
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
    }

    /// <summary>
    /// Client: host owns EventTriggers volume enter/exit. Skip entire method so
    /// client walking into a zone does not run local fireEventTrigger (4.2 already
    /// blocks one-shot GameEvents; this also prevents multipleFire double-FX).
    /// Host/offline: run vanilla.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class EventTriggersClientEnterSuppressPatch
    {
        private static bool Prefix()
        {
            if (!EventTriggersAuth.IsMultiplayerConnected())
                return true;
            if (ModRuntime.Network.Role == NetworkRole.Host)
                return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(EventTriggers), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class EventTriggersClientExitSuppressPatch
    {
        private static bool Prefix()
        {
            if (!EventTriggersAuth.IsMultiplayerConnected())
                return true;
            if (ModRuntime.Network.Role == NetworkRole.Host)
                return true;
            return false;
        }
    }

    /// <summary>
    /// Host: after vanilla Player.Instance handling, fire area enter for proxies.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerEnter", new[] { typeof(Collider) })]
    public static class EventTriggersProxyEnterPatch
    {
        private static void Postfix(EventTriggers __instance, Collider other)
        {
            if (!EventTriggersAuth.IsHost()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;
            if (Singleton<OutsideLocations>.Instance != null && Singleton<OutsideLocations>.Instance.loading)
                return;

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

            // Mirror vanilla multi-collider guard: only first "logical" enter counts.
            Vector3 pos = proxy.transform.position;
            int mask = 1 << __instance.gameObject.layer;
            if (__instance.entered != 0 && Helpers.isComponentAtPos(pos, mask, __instance))
                return;

            __instance.fireEventTrigger(EventTrigger.Type.area);
            __instance.entered++;
            ModRuntime.LegacyInfo(
                $"[EventTriggers] proxy enter area p{proxy.PlayerId} on {__instance.name} entered={__instance.entered}");
        }
    }

    /// <summary>
    /// Host: after vanilla Player.Instance handling, fire area exit for proxies.
    /// </summary>
    [HarmonyPatch(typeof(EventTriggers), "OnTriggerExit", new[] { typeof(Collider) })]
    public static class EventTriggersProxyExitPatch
    {
        private static void Postfix(EventTriggers __instance, Collider other)
        {
            if (!EventTriggersAuth.IsHost()) return;
            if (__instance == null || other == null) return;
            if (!__instance.reactsToPlayer) return;
            if (!EventTriggersAuth.CanFireTriggers(__instance)) return;

            RemotePlayerProxy proxy = other.GetComponentInParent<RemotePlayerProxy>();
            if (proxy == null) return;

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
                // Match vanilla radius path: Player.canSee(from, to, radius).
                if (Player.Instance != null)
                    return Player.Instance.canSee(proxyT, et.transform, radius);
                return Core.canSee(proxyT, et.transform);
            }

            // Approximate FOV: proxy faces along transform.up (same as player body).
            // Allow close proximity without facing; farther needs ~half-FOV and LOS.
            Vector3 toTarget = dest - proxyT.position;
            float halfFov = 55f;
            if (dist > 6f && Vector3.Angle(toTarget, proxyT.up) > halfFov)
                return false;

            return Core.canSee(proxyT, et.transform);
        }
    }
}
