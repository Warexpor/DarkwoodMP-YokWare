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
    /// Host shared clock. Vanilla <c>FixedUpdate</c> skips <c>CurrentTime++ / refreshTime</c>
    /// while the local player is inside an outside location, dying, or under a personal
    /// <c>timeFreeze</c> (the Wolf's trap), so the whole party's clock stopped whenever the
    /// host was in the village or dying, even with peers out in the forest, while a peer's
    /// own freezes stopped nothing. The clock steps while any player counts
    /// (<see cref="CoopTimePolicy.SharedClockSteps"/>): the host adds the step vanilla
    /// skipped, or holds the one it would take for a host who no longer counts (dead until
    /// morning) when nobody else does either.
    /// </summary>
    [HarmonyPatch(typeof(Controller), "FixedUpdate")]
    public static class HostSharedClockPatch
    {
        private static readonly AccessTools.FieldRef<Controller, float> LastTimeUpdatedTime =
            AccessTools.FieldRefAccess<Controller, float>("lastTimeUpdatedTime");

        private const byte None = 0, Step = 1, Held = 2;

        /// <summary>
        /// Where vanilla would run this player's clock: in the world, not inside an outside
        /// location, not dreaming, not loading and past the opening movie. Sent on PlayerState.
        /// </summary>
        internal static bool LocalInOpenWorld()
        {
            if (Player.Instance == null || GameScreen.AtTitle || Core.loadingGame || Core.EnteringDream)
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
            return !Sync.PersonalPrologue.LocalInPrologue;
        }

        /// <summary>
        /// A freeze vanilla puts on this one player: dying (Player.die stops the clock until the
        /// respawn), dead until morning, or a <c>timeFreeze</c> effect that is not the morning's
        /// (the Wolf's trap). Sent on PlayerState as <c>ClockHeld</c>.
        /// </summary>
        internal static bool LocalPersonalHold()
        {
            Player p = Player.Instance;
            if (p == null)
                return false;
            if (p.dying || !p.alive || DeathStateTracker.LocalNightDeath)
                return true;
            Controller ctrl = Singleton<Controller>.Instance;
            return ctrl != null && !ctrl.isAfterNight && p.effects != null
                && p.effects.hasEffectType(CharacterEffectType.timeFreeze);
        }

        private static void Prefix(Controller __instance, out byte __state)
        {
            __state = None;
            if (__instance == null)
                return;
            var net = ModRuntime.Network;
            if (net == null || net.Role != NetworkRole.Host)
                return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null)
                return;
            var dreams = Singleton<Dreams>.Instance;
            if (dreams != null && dreams.dreaming)
                return;
            // Same interval gate as vanilla; vanilla updates the stamp, the postfix only steps.
            if (UnityEngine.Time.time - LastTimeUpdatedTime(__instance) < __instance.timeChangeInterval)
                return;
            bool personal = LocalPersonalHold();
            // DoUpdateTime off for a reason that is not this player's own (a load, a scripted
            // world freeze) holds everyone, as do the morning and the day-1 prologue wait.
            bool worldHeld = PrologueDayOneHoldPatch.Holding || __instance.isAfterNight || Core.loadingGame
                || (!__instance.DoUpdateTime && !personal);
            if (worldHeld)
                return;
            bool vanillaSteps = __instance.DoUpdateTime && !ol.playerInOutsideLocation;
            bool hostCounts = !personal && LocalInOpenWorld();
            bool steps = CoopTimePolicy.SharedClockSteps(false, hostCounts, AnyPeerCounts(net));
            if (steps && !vanillaSteps)
            {
                __state = Step;
            }
            else if (!steps && vanillaSteps)
            {
                // Vanilla would step for a host who is dead until morning, spectating.
                __instance.DoUpdateTime = false;
                __state = Held;
            }
        }

        private static void Postfix(Controller __instance, byte __state)
        {
            if (__state == Step)
            {
                __instance.CurrentTime++;
                __instance.refreshTime();
            }
            var net = ModRuntime.Network;
            if (net != null && net.Role == NetworkRole.Host)
                DeathStateTracker.HostCheckClockLeftNight(__instance);
        }

        /// <summary>Hand back the step held above, also when FixedUpdate throws.</summary>
        private static System.Exception Finalizer(Controller __instance, byte __state, System.Exception __exception)
        {
            if (__state == Held && __instance != null)
                __instance.DoUpdateTime = true;
            return __exception;
        }

        private static bool AnyPeerCounts(LanNetworkManager net)
        {
            foreach (int id in net.EnumeratePeerIds())
            {
                if (id == net.LocalPlayerId || !net.IsPeerReadyForGameplay(id))
                    continue;
                if (net.RemotePlayers.TryGetValue(id, out RemotePlayerState st) && st.InOpenWorld && !st.ClockHeld)
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
    [HarmonyPatch(typeof(Controller), "useTimeSkip")]
    public static class ClientUseTimeSkipSuppressPatch
    {
        private static bool Prefix()
        {
            return !ClientTimeFixedUpdateSuppressPatch.IsClientRole();
        }
    }
}
