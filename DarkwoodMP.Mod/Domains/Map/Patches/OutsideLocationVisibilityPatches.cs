using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Patches
{
    /// <summary>
    /// After OutsideLocations loading screens (bunker, village, doctor house, etc.),
    /// re-place remote player proxies and re-announce location membership so peers
    /// can see each other instead of lingering on pre-load world positions.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.transportToLocation))]
    public static class OutsideLocationTransportSettledPatch
    {
        private static void Postfix(string locationName)
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;
                net.LocationEnterExitHandlers.OnLocalOutsideLocationSettled(locationName);
                // Virgin pad just registered — drop stale Padlock/Locked/GE/Interactive/
                // Constructible/Chain/Burn/Trigger scans so pending flush and host
                // first-enter SoftMatch see pad children (not a 3s TTL miss).
                WorldQueryHelper.InvalidateSceneScanCache<Padlock>();
                WorldQueryHelper.InvalidateSceneScanCache<Locked>();
                WorldQueryHelper.InvalidateSceneScanCache<GameEvents>();
                WorldQueryHelper.InvalidateSceneScanCache<InteractiveItem>();
                WorldQueryHelper.InvalidateSceneScanCache<Constructible>();
                WorldQueryHelper.InvalidateSceneScanCache<ChainParent>();
                WorldQueryHelper.InvalidateSceneScanCache<Burn>();
                WorldQueryHelper.InvalidateSceneScanCache<Trigger>();
                WorldQueryHelper.InvalidateSceneScanCache<ShadowArmor>();
                WorldQueryHelper.InvalidateSceneScanCache<Saw>();
                WorldQueryHelper.InvalidateSceneScanCache<Feeder>();
                WorldQueryHelper.InvalidateSceneScanCache<Lure>();
                if (net.LockHandlers != null)
                {
                    net.LockHandlers.TryFlushPendingLocks();
                    net.LockHandlers.TryFlushPendingConstructibles();
                }
                if (net.GameEventHandlers != null)
                    net.GameEventHandlers.TryFlushPendingGameEvents();
                if (net.ChainHandlers != null)
                    net.ChainHandlers.TryFlushPendingChainStates();
                if (net.WorldBurnHandlers != null)
                    net.WorldBurnHandlers.TryFlushPending();
                if (net.ShadowArmorHandlers != null)
                    net.ShadowArmorHandlers.TryFlushPending();
                if (net.StationHandlers != null)
                {
                    net.StationHandlers.TryFlushPendingSawStates();
                    net.StationHandlers.TryFlushPendingFeederStates();
                    net.StationHandlers.TryFlushPendingLureStates();
                }
                WorldPhysicsSyncService.TryFlushPendingLights();
                Sync.TrapNetworkId.FlushPending(
                    (p, n) => WorldPhysicsSyncService.FindTrapByPos(p, n),
                    (go, trig, silent) =>
                        WorldPhysicsSyncService.ApplyTrapState(go, trig, silentDisarm: silent));
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] transport settle hook: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Returning to the world map: snap proxies to last known so they are not stuck
    /// at bunker coordinates while the local player is on the forest grid.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.returningOnTeleportedPlayer))]
    public static class OutsideLocationReturnToWorldPatch
    {
        private static void Postfix()
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;
                net.LocationEnterExitHandlers.OnLocalReturnedToWorld();
                WorldPhysicsSyncService.TryFlushPendingLights();
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] return-to-world hook: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Day-death vanilla order: transportToHome then onPlayerDeath (setGrid World +
    /// leaveAllLocations) but never currentGrid.leave() or refreshPosition, unlike
    /// returnToWorld. Client respawned at hideout on a stale outside-location grid →
    /// blackness. Mirror returningOnTeleportedPlayer grid hygiene + LocationExit.
    /// </summary>
    [HarmonyPatch(typeof(OutsideLocations), nameof(OutsideLocations.onPlayerDeath))]
    public static class OutsideLocationDeathGridHygienePatch
    {
        private static void Postfix(OutsideLocations __instance)
        {
            try
            {
                if (!(ModRuntime.Network is LanNetworkManager net) || !net.IsConnected)
                    return;

                EnsureWorldGridAtPlayer(__instance);

                if (Player.Instance != null && Player.Instance.whereAmI != null)
                    Player.Instance.whereAmI.checkWhereAmI();

                net.LocationEnterExitHandlers.OnLocalReturnedToWorldAfterDeath();
                WorldPhysicsSyncService.TryFlushPendingLights();
                ModRuntime.LegacyInfo(
                    "[LocationSync] death grid hygiene — World grid + refresh + LocationExit");
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] death grid hygiene: " + ex.Message);
            }
        }

        internal static void EnsureWorldGridAtPlayer(OutsideLocations ol)
        {
            var wg = Singleton<WorldGrid>.Instance;
            if (wg == null) return;

            if (wg.currentGrid != null
                && !string.Equals(wg.currentGrid.name, "World", System.StringComparison.OrdinalIgnoreCase))
            {
                try { wg.currentGrid.leave(); }
                catch { /* grid may already be tearing down */ }
            }

            wg.setGrid("World");

            if (Player.Instance != null)
            {
                wg.refreshPosition(
                    Player.Instance.transform.position, instant: true, force: true);
            }

            if (ol != null)
            {
                ol.playerInOutsideLocation = false;
                ol.currentLocationName = "";
            }
        }
    }

    /// <summary>
    /// Belt: after transportToHome teleport, force World refresh when MP connected
    /// (covers stale grid even when playerInOutsideLocation was already false).
    /// Night-death suppress path skips transportToHome (a Prefix returned false): Postfixes
    /// still run then, so this one checks <c>__runOriginal</c> and does nothing for it.
    /// </summary>
    [HarmonyPatch(typeof(Player), "transportToHome")]
    public static class DayDeathTransportHomeGridPatch
    {
        private static void Postfix(bool __runOriginal)
        {
            if (!__runOriginal)
                return;
            try
            {
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                    return;
                OutsideLocationDeathGridHygienePatch.EnsureWorldGridAtPlayer(
                    Singleton<OutsideLocations>.Instance);
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogWarning("[LocationSync] transportToHome grid: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Host <c>leaveAllLocations</c> / return-to-world force-leaves every pad, and walking out of
    /// a building leaves it. Skip that while a remote player is still inside, so their geometry,
    /// NPCs and colliders stay simulated. Membership (LocationEnter) is checked before stand-in
    /// positions and does not need a fresh PlayerState: a stalled remote kept its pad only while
    /// its last state was under 3 s old. A held building is left later, once the last remote is
    /// out (<see cref="TickHost"/>), so its exit events still fire; a held pad is left by
    /// LocationExit (TryLeaveUnoccupiedOutsideLocation).
    /// </summary>
    [HarmonyPatch(typeof(Location), nameof(Location.leave))]
    public static class HostLocationLeaveKeepRemotePatch
    {
        private static readonly System.Collections.Generic.HashSet<Location> _held = new System.Collections.Generic.HashSet<Location>(); // reset-in: Reset
        private static readonly System.Collections.Generic.List<Location> _scratch = new System.Collections.Generic.List<Location>(); // process-scoped: scratch buffer, cleared before each use
        private static float _nextCheckAt; // reset-in: Reset
        private static bool _releasing; // process-scoped: call-scoped, unwound by its finally

        /// <summary>Registered with NetworkResetRegistry.</summary>
        public static void Reset()
        {
            _held.Clear();
            _nextCheckAt = 0f;
        }

        private static bool Prefix(Location __instance)
        {
            if (__instance == null || _releasing) return true;
            if (!NetGuard.ConnectedHost(out var net)) return true;
            if (DreamSyncManager.IsDreamActive)
                return true;
            if (!IsRemoteInside(net, __instance))
                return true;
            if (!__instance.isOutsideLocation)
                _held.Add(__instance);
            ModRuntime.LegacyInfo(
                $"[LocationSync] skip Location.leave — remote still inside {(__instance.gameObject != null ? __instance.gameObject.name : __instance.name)}");
            return false;
        }

        private static bool IsRemoteInside(LanNetworkManager net, Location loc)
        {
            string n = loc.gameObject != null ? loc.gameObject.name : loc.name;
            if (net.LocationEnterExitHandlers != null
                && net.LocationEnterExitHandlers.IsAnyRemoteInOutsideLocation(n))
                return true;
            foreach (RemotePlayerProxy proxy in net.GetAllProxies())
            {
                if (proxy != null && Location.getAtPos(proxy.transform.position) == loc)
                    return true;
            }
            return false;
        }

        /// <summary>Host, ~1 Hz: leave a held building once no remote (and not the host) is in it.</summary>
        internal static void TickHost()
        {
            if (_held.Count == 0 || Time.unscaledTime < _nextCheckAt)
                return;
            _nextCheckAt = Time.unscaledTime + 1f;
            if (!NetGuard.ConnectedHost(out var net))
            {
                _held.Clear();
                return;
            }
            _scratch.Clear();
            _scratch.AddRange(_held);
            Player host = Player.Instance;
            for (int i = 0; i < _scratch.Count; i++)
            {
                Location loc = _scratch[i];
                if (loc == null || !loc.entered)
                {
                    _held.Remove(loc);
                    continue;
                }
                // The host walked back in: its own next exit leaves it.
                if (host != null && Location.getAtPos(host._transform.position) == loc)
                {
                    _held.Remove(loc);
                    continue;
                }
                if (IsRemoteInside(net, loc))
                    continue;
                _held.Remove(loc);
                _releasing = true;
                try { DialogHostApplyGuard.RunHostWorldFanout(() => loc.leave()); }
                finally { _releasing = false; }
                ModRuntime.LegacyInfo("[LocationSync] last remote left building — host left " + loc.name);
            }
        }
    }
}
