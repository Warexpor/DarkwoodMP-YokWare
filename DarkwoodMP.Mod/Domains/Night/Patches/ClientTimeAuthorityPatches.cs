using DWMPHorde.Networking;
using DWMPHorde.Players;
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
    /// Host shared clock. Vanilla <c>FixedUpdate</c> skips <c>CurrentTime++ / refreshTime</c>
    /// while the local player is inside an outside location, so the whole party's clock
    /// stopped whenever the host was in the village, even with peers out in the forest.
    /// When the host is inside but a ready peer reports the open world, run the same step
    /// vanilla would have run (<see cref="CoopTimePolicy.SharedClockRuns"/>).
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class HostSharedClockPatch
    {
        private static readonly AccessTools.FieldRef<Controller, float> LastTimeUpdatedTime =
            AccessTools.FieldRefAccess<Controller, float>("lastTimeUpdatedTime");

        /// <summary>
        /// Where vanilla would run this player's clock: in the world, not inside an outside
        /// location, not dreaming, not loading and past the opening movie. Sent on PlayerState.
        /// </summary>
        internal static bool LocalInOpenWorld()
        {
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame || Core.EnteringDream)
                return false;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || ol.playerInOutsideLocation || ol.loading)
                return false;
            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && (dreams.dreaming || dreams.dreamPrepared))
                return false;
            var wg = Singleton<WorldGenerator>.Instance;
            if (wg != null && wg.playingIntro)
                return false;
            return !PrologueSync.ClientDeferredFirstPlay;
        }

        private static void Prefix(Controller __instance, out bool __state)
        {
            __state = false;
            if (__instance == null || !__instance.DoUpdateTime)
                return;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || !ol.playerInOutsideLocation)
                return;
            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
                return;
            // Same interval gate as vanilla; vanilla updates the stamp, the postfix only steps.
            if (UnityEngine.Time.time - LastTimeUpdatedTime(__instance) < __instance.timeChangeInterval)
                return;
            __state = CoopTimePolicy.SharedClockRuns(true, AnyPeerInOpenWorld(net));
        }

        private static void Postfix(Controller __instance, bool __state)
        {
            if (!__state)
                return;
            __instance.CurrentTime++;
            __instance.refreshTime();
        }

        private static bool AnyPeerInOpenWorld(LanNetworkManager net)
        {
            foreach (int id in net.EnumeratePeerIds())
            {
                if (id == net.LocalPlayerId || !net.IsPeerReadyForGameplay(id))
                    continue;
                if (net.RemotePlayers.TryGetValue(id, out RemotePlayerState st) && st.InOpenWorld)
                    return true;
            }
            return false;
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
    /// <remarks>
    /// The bed's "wait until evening" moves the one clock everyone shares, so in co-op it needs
    /// every living player at the hideout (vanilla: the sleeper is the whole party). Otherwise it
    /// is refused, with a line for whoever used the bed.
    /// </remarks>
    [HarmonyPatch(typeof(Controller), "useTimeSkip")]
    public static class ClientUseTimeSkipSuppressPatch
    {
        internal const string NotEveryoneHomeMessage = "Everyone has to be at the hideout to wait for the evening.";

        private static bool Prefix()
        {
            if (!NetGuard.Connected(out LanNetworkManager net))
                return true;
            bool everyoneHome = CoopTimeSkip.EveryoneHome(net);
            // The host applying a client's bed (cursor action): the client shows its own line.
            if (!everyoneHome && !LanNetworkManager.IsApplyingRemoteState && Player.Instance != null)
                Player.Instance.displayMessage(NotEveryoneHomeMessage);
            if (ClientTimeFixedUpdateSuppressPatch.IsClientRole())
                return false;
            return everyoneHome;
        }
    }

    internal static class CoopTimeSkip
    {
        /// <summary>Every living player stands in a hideout (dead players do not count).</summary>
        internal static bool EveryoneHome(LanNetworkManager net)
        {
            Player p = Player.Instance;
            if (p != null && p.alive && !DeathStateTracker.LocalNightDeath)
            {
                var ol = Singleton<OutsideLocations>.Instance;
                bool home = (ol == null || !ol.playerInOutsideLocation) && p.whereAmI != null
                    && p.whereAmI.bigLocation != null && p.whereAmI.bigLocation.playerBase;
                if (!home)
                    return false;
            }
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy == null || !net.IsPeerReadyForGameplay(proxy.PlayerId))
                    continue;
                CharBase cb = proxy.CachedCharBase;
                if (cb != null && !cb.alive || DeathStateTracker.IsRemoteNightDead(proxy.PlayerId))
                    continue;
                if (!net.RemotePlayers.TryGetValue(proxy.PlayerId, out RemotePlayerState st) || !st.InOpenWorld)
                    return false;
                Location loc = Location.getAtPos(proxy.transform.position);
                if (loc == null || !loc.playerBase)
                    return false;
            }
            return true;
        }
    }
}
