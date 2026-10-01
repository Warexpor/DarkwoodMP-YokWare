using DWMPHorde.Networking;
using HarmonyLib;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// The host is the sole day/night clock authority.
    /// Client must not advance CurrentTime / fire refreshTime edges, but must still
    /// run FixedUpdate inventory refresh (hotbar durability timers, etc.).
    /// Gated on role (not peer count) so a brief host drop does not run the local clock;
    /// the overridden value is restored once, on session end (<see cref="Reset"/>).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class ClientTimeFixedUpdateSuppressPatch
    {
        /// <summary>Controller whose DoUpdateTime we forced off (vanilla wanted it on).</summary>
        private static Controller _forcedOn;

        internal static bool IsClientRole()
        {
            var net = ModRuntime.Network;
            // Session = role set (not PeerCount>0): a peer-less moment is still the host's clock.
            return net != null && CoopTimePolicy.ShouldSuppressClientClock(
                net.Role != NetworkRole.Offline, net.Role == NetworkRole.Client);
        }

        /// <summary>Session end: hand the clock back to vanilla exactly once.</summary>
        public static void Reset()
        {
            Controller ctrl = _forcedOn;
            _forcedOn = null;
            if (ctrl != null)
                ctrl.DoUpdateTime = true;
        }

        private static void Prefix(Controller __instance)
        {
            if (__instance == null) return;
            if (!IsClientRole())
            {
                if (_forcedOn == null) return;
                // Promoted to host: migration already reclaimed the clock, never write here.
                // Dropped to Offline without StopNetwork (failed promote): restore once now.
                if (ModRuntime.Network != null && ModRuntime.Network.Role == NetworkRole.Host)
                    _forcedOn = null;
                else
                    Reset();
                return;
            }

            // Keep refreshActiveItemsInInventories; only block CurrentTime++ / refreshTime.
            if (__instance.DoUpdateTime)
            {
                __instance.DoUpdateTime = false;
                _forcedOn = __instance;
            }
        }
    }

    /// <summary>
    /// Belt-and-suspenders: if anything still calls refreshTime on a client
    /// (TimeSync used to; other systems might), strip day-chain edge handlers and only
    /// run ambient/clock UI. Host path unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "refreshTime")]
    public static class ClientRefreshTimeNoEdgesPatch
    {
        private static bool Prefix(Controller __instance, bool afterGameLoad)
        {
            if (!ClientTimeFixedUpdateSuppressPatch.IsClientRole())
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

    /// <summary>
    /// Item.activate still continues after client defers onActivate, so the client
    /// would call useTimeSkip locally. Host adopts via ActivateCursorAction + TimeSync.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "useTimeSkip")]
    public static class ClientUseTimeSkipSuppressPatch
    {
        private static bool Prefix()
        {
            return !ClientTimeFixedUpdateSuppressPatch.IsClientRole();
        }
    }
}
