using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Outside-location enter/exit, settle, and remote-proxy placement.</summary>
    internal sealed partial class LocationEnterExitNetHandlers
    {
        private readonly LanNetworkManager _net;

        /// <summary>Min gap between remote-driven createLocation calls (load pressure).</summary>
        private const float CreateLocationMinIntervalSec = 2.5f;
        private static float _nextCreateLocationAllowedAt;
        private static string _createLocationInFlight;
        /// <summary>Peers whose pad was missing; place proxy once ResolveOutsideLocation succeeds.</summary>
        private readonly HashSet<int> _pendingPlaceOnLocationResolve = new HashSet<int>();
        /// <summary>
        /// Soft-reconnect ForceAnnounce while mid ol.loading / loadingGame (playerInOutsideLocation
        /// still false). Retry on settle / Tick flush — do not invent a pad name on the world map.
        /// </summary>
        private string _pendingForceAnnounceReason;
        /// <summary>Remote pads deferred because local OutsideLocations.loading (0.8.39 guard).</summary>
        private readonly HashSet<string> _deferredCreateWhileLocalLoading =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal LocationEnterExitNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void HandleLocationEnter(LocationEnterMessage msg)
        {
            if (string.IsNullOrEmpty(msg.LocationName))
                return;

            int playerId = _net.Role == NetworkRole.Host
                ? _net.CurrentReceivePlayerId
                : (msg.PlayerId > 0 ? msg.PlayerId : _net.CurrentReceivePlayerId);
            if (playerId <= 0 || playerId == _net.LocalPlayerId)
                return;

            // During shared dream, strip vanilla *_done so proxies land on the live pad.
            string locName = msg.LocationName;
            if (Sync.DreamSyncManager.IsDreamActive)
                locName = Sync.DreamSyncManager.CanonicalDreamLocationName(locName);

            ModRuntime.LegacyInfo($"[LocationSync] player {playerId} entered location: {locName}");

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null) return;

            // *_done twin: prev may be "foo_done" while msg is "foo" (or vice versa) —
            // raw Equals treated that as first enter → re-place + light re-push thrash.
            bool firstEnterThisLoc = !_net.RemoteOutsideLocation.TryGetValue(playerId, out string prev)
                || !CoopWorldPresencePolicy.LocationNamesMatch(prev, locName);
            _net.RemoteOutsideLocation[playerId] = locName;

            // Prefer live non-_done location instance when both exist.
            Location loc = ResolveOutsideLocation(ol, locName);
            if (loc != null)
            {
                loc.enter(force: true);

                // Place proxy: prefer last PlayerState (accurate), else playerSpawn on first enter.
                // Dream: first enter only, not every periodic LocationEnter. Repeated snaps
                // to playerSpawn Y and locking them under the pad). Non-dream: also re-place
                // when local just settled in the same location (post-load resync).
                string localCanon = Sync.DreamSyncManager.CanonicalDreamLocationName(
                    ol.currentLocationName ?? "");
                bool localSameLoc = ol.playerInOutsideLocation
                    && CoopWorldPresencePolicy.LocationNamesMatch(localCanon, locName);
                bool dreamLoc = Sync.DreamSyncManager.IsDreamLocationName(locName)
                    || (Sync.DreamSyncManager.IsDreamActive
                        && locName.StartsWith("dream_", StringComparison.OrdinalIgnoreCase));
                bool pendingPlace = _pendingPlaceOnLocationResolve.Remove(playerId);
                // Soft-reconnect destroys proxies but used to keep RemoteOutsideLocation —
                // firstEnterThisLoc stayed false and Place was skipped. Belt: missing proxy.
                bool proxyMissing = !_net.RemoteProxies.TryGetValue(playerId, out var existingProxy)
                    || existingProxy == null;
                bool shouldPlace = firstEnterThisLoc || pendingPlace || (localSameLoc && !dreamLoc)
                    || proxyMissing;
                if (shouldPlace)
                    PlaceRemoteProxyInOutsideLocation(playerId, loc, preferLastKnown: true);

                // Host never setGrid for a remote-only pad; wake that location's
                // WorldGrid nodes around remotes so bunker Cullables/AI run.
                if (_net.Role == NetworkRole.Host)
                    TryEnterLocationGridNearRemotes(locName);

                // Peer just got location geometry; re-push sticky lamp/gen state that
                // may have been applied (or dropped) while the grid was unloaded.
                // Also on pendingPlace / proxyMissing (soft-reconnect re-place) — not on
                // every localSameLoc heartbeat (that would thrash lights at ~1 Hz).
                if (_net.Role == NetworkRole.Host && playerId != _net.LocalPlayerId
                    && (firstEnterThisLoc || pendingPlace || proxyMissing))
                    _net.ResyncWorldLightsForPeer(playerId);
            }
            else
            {
                // Dreams: LoadDreamSceneCoroutine owns the pad. createLocation here races
                // a second bunker (duplicated ambience and lights, wrong slot Y); only wait.
                bool dreamName = locName.StartsWith("dream_", StringComparison.OrdinalIgnoreCase)
                    || Sync.DreamSyncManager.IsDreamLocationName(locName);
                if (dreamName
                    || Sync.DreamSyncManager.IsDreamActive
                    || (Dreams.Instance != null && (Dreams.Instance.dreamPrepared || Dreams.Instance.dreaming)))
                {
                    if (firstEnterThisLoc)
                        _net.RemoteOutsideLocation.Remove(playerId);
                    _pendingPlaceOnLocationResolve.Remove(playerId);
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] dream pad not ready yet, skip createLocation: {locName}");
                    return;
                }

                // Prefer grid wake for remote sim / split-map (CoopWorldPresencePolicy)
                // without stacking vanilla createLocation load+transport pressure when
                // N remotes enter different villages.
                if (_net.Role == NetworkRole.Host)
                    TryEnterLocationGridNearRemotes(locName);

                // Remember to place once the pad exists. Keep RemoteOutsideLocation so
                // CoopWorldPresencePolicy still sees the remote during defer (do not
                // Remove membership on rate-limit / loadingGame / client skip).
                if (firstEnterThisLoc)
                    _pendingPlaceOnLocationResolve.Add(playerId);

                // Clients must not createLocation for a remote peer — that is load+transport
                // of the LOCAL player. Host/local pad entry uses LocationTransport / doors.
                if (_net.Role != NetworkRole.Host)
                {
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] client skip createLocation for remote pad: {locName}");
                    return;
                }

                if (Core.loadingGame)
                {
                    _deferredCreateWhileLocalLoading.Add(locName);
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] skip createLocation (loadingGame): {locName}");
                    return;
                }

                // Soft-reconnect / mid-transfer sticky LocationEnter: OutsideLocations.loading
                // means local is already in prepare/transport. Stacking createLocation here
                // races a second spawn (unloadTextures + grid) during the loading screen.
                if (ol.loading)
                {
                    _deferredCreateWhileLocalLoading.Add(locName);
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] defer createLocation (OutsideLocations.loading): {locName}");
                    return;
                }

                // Host already inside this pad (or about to settle): allow create.
                // Otherwise rate-limit so concurrent remote-only enters prefer grid wake.
                // Note: vanilla createLocation uses transportAfterSpawn:false — it does not
                // yank the host; pressure is load/grid only.
                bool localNeedsPad = ol.playerInOutsideLocation
                    && CoopWorldPresencePolicy.LocationNamesMatch(
                        ol.currentLocationName ?? "", locName);
                if (!localNeedsPad && !TryBeginRemoteLocationCreate(locName))
                {
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] defer createLocation (rate-limit/grid prefer): {locName}");
                    return;
                }

                string createName = locName;
                ModRuntime.LegacyInfo($"[LocationSync] location not spawned, creating: {createName}");
                if (localNeedsPad)
                    NoteRemoteLocationCreate(createName);
                ol.createLocation(createName);
            }
        }


        private static bool TryBeginRemoteLocationCreate(string locName)
        {
            float now = Time.unscaledTime;
            if (!string.IsNullOrEmpty(_createLocationInFlight)
                && string.Equals(_createLocationInFlight, locName, StringComparison.OrdinalIgnoreCase)
                && now < _nextCreateLocationAllowedAt)
            {
                // Same pad already requested; wait for key / retry.
                return false;
            }
            if (now < _nextCreateLocationAllowedAt)
                return false;
            NoteRemoteLocationCreate(locName);
            return true;
        }

        private static void NoteRemoteLocationCreate(string locName)
        {
            _createLocationInFlight = locName;
            _nextCreateLocationAllowedAt = Time.unscaledTime + CreateLocationMinIntervalSec;
        }

        /// <summary>
        /// Pick live location by key; if dict value is a *_done GO during dream, prefer Dreams.dreamLocation.
        /// </summary>
        internal static Location ResolveOutsideLocation(OutsideLocations ol, string locName)
        {
            if (ol == null || string.IsNullOrEmpty(locName)) return null;

            if (Sync.DreamSyncManager.IsDreamActive)
            {
                var dreamLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
                if (dreamLoc != null && dreamLoc.gameObject != null)
                {
                    string dreamCanon = Sync.DreamSyncManager.CanonicalDreamLocationName(
                        dreamLoc.gameObject.name);
                    if (string.Equals(dreamCanon, locName, StringComparison.OrdinalIgnoreCase))
                        return dreamLoc;
                }

                // Do not fall through to spawnedLocations or global name
                // lookup while a dream is active; those keys have overworld
                // twins and first-match selection is not lifecycle-safe.
                return null;
            }

            if (ol.spawnedLocations != null && ol.spawnedLocations.ContainsKey(locName))
            {
                var loc = ol.spawnedLocations[locName];
                if (loc != null
                    && Sync.DreamSyncManager.IsDreamActive
                    && loc.gameObject != null
                    && loc.gameObject.name.EndsWith("_done", StringComparison.OrdinalIgnoreCase))
                {
                    // Live key maps to a renamed done instance; try a non-done object by name.
                    var live = GameObject.Find(locName);
                    if (live != null)
                    {
                        var liveLoc = live.GetComponent<Location>();
                        if (liveLoc != null) return liveLoc;
                    }
                }
                return loc;
            }

            // Dict may still key under *_done while peers send canonical name.
            string doneKey = locName + "_done";
            if (ol.spawnedLocations != null && ol.spawnedLocations.ContainsKey(doneKey)
                && !Sync.DreamSyncManager.IsDreamActive)
                return ol.spawnedLocations[doneKey];

            return null;
        }

        /// <summary>
        /// Local finished OutsideLocations.transportToLocation (bunker/village loading screen).
        /// Re-activate geometry, re-place peer proxies, and announce ourselves so peers re-snap us.
        /// </summary>
        public void OnLocalOutsideLocationSettled(string locationName)
        {
            if (string.IsNullOrEmpty(locationName) || !_net.IsConnected)
                return;

            try
            {
                // Live dream pad; never force peers onto the vanilla *_done rename.
                if (Sync.DreamSyncManager.IsDreamActive)
                    locationName = Sync.DreamSyncManager.CanonicalDreamLocationName(locationName);

                var ol = Singleton<OutsideLocations>.Instance;
                if (ol != null)
                {
                    var settled = ResolveOutsideLocation(ol, locationName);
                    if (settled != null)
                        settled.enter(force: true);
                    else if (ol.spawnedLocations.ContainsKey(locationName))
                        ol.spawnedLocations[locationName].enter(force: true);

                    // Vanilla transportToLocation dreamPrepared branch never sets these
                    // (only the non-dream path does). Without them, the next PlayerState
                    // tick sees !playerInOutsideLocation + previous=true → false LocationExit.
                    ol.playerInOutsideLocation = true;
                    ol.currentLocationName = locationName;
                }

                _net.PreviousInOutsideLocation = true;
                _net.PreviousLocationName = locationName;
                _net.LocationSyncCounter = 0;

                _net.Broadcast(NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = locationName,
                        PlayerId = _net.LocalPlayerId
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);

                ResyncRemoteProxiesForOutsideLocation(locationName);

                if (_net.Role == NetworkRole.Host)
                {
                    foreach (int peerId in _net.EnumeratePeerIds())
                    {
                        if (peerId == _net.LocalPlayerId) continue;
                        SyncExistingLocationsTo(peerId);
                    }
                }

                // Settle announce covers soft-reconnect sticky ForceAnnounce mid ol.loading.
                ClearPendingForceAnnounce("location settle");
                // Local loading screen done — retry remote pads deferred by 0.8.39 guard.
                TryFlushDeferredCreatesWhileLocalLoading();

                ModRuntime.LegacyInfo(
                    $"[LocationSync] local settled in '{locationName}' — proxies resynced, LocationEnter forced");
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "OnLocalOutsideLocationSettled: " + ex.Message);
            }
        }

        /// <summary>Local returned to the world map after an outside location.</summary>
        public void OnLocalReturnedToWorld()
        {
            if (!_net.IsConnected) return;
            try
            {
                ClearPendingForceAnnounce("returned to world");
                _net.PreviousInOutsideLocation = false;
                _net.PreviousLocationName = "";
                // Hard-snap world-side proxies so they are not left at bunker coords
                // while we stand on the map. Skip peers still inside a pad; yanking
                // them to last-known world pos is the 3p "host left, client still in
                // cellar" ghost.
                foreach (var kvp in new List<KeyValuePair<int, RemotePlayerProxy>>(_net.RemoteProxies))
                {
                    if (!CoopWorldPresencePolicy.ShouldSnapRemoteProxyOnLocalWorldReturn(
                        _net.RemoteOutsideLocation.ContainsKey(kvp.Key)))
                        continue;
                    if (PlayerPositionManager.TryGetRemote(kvp.Key, out Vector3 pos, out float rotY))
                        _net.TeleportRemoteProxyTo(pos, rotY, kvp.Key);
                }
                ModRuntime.LegacyInfo("[LocationSync] local returned to world — proxy snap from last known");
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "OnLocalReturnedToWorld: " + ex.Message);
            }
        }

        /// <summary>
        /// Day death respawn: same proxy snap as return-to-world, plus LocationExit so
        /// peers drop us from _net.RemoteOutsideLocation (stale bunker membership).
        /// </summary>
        public void OnLocalReturnedToWorldAfterDeath()
        {
            if (!_net.IsConnected) return;
            try
            {
                bool wasIn = _net.PreviousInOutsideLocation
                    || !string.IsNullOrEmpty(_net.PreviousLocationName);
                OnLocalReturnedToWorld();

                Vector3 pos = Player.Instance != null
                    ? Player.Instance.transform.position
                    : Vector3.zero;
                if (wasIn || pos != Vector3.zero)
                {
                    _net.Broadcast(NetMessageType.LocationExit,
                        w => new LocationExitMessage
                        {
                            PosX = pos.x,
                            PosY = pos.y,
                            PosZ = pos.z,
                            PlayerId = _net.LocalPlayerId
                        }.Serialize(w),
                        DeliveryMethod.ReliableOrdered);
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] death LocationExit pid={_net.LocalPlayerId} at {pos}");
                }
            }
            catch (System.Exception ex)
            {
                ModLog.Warn(LogCat.Session, "OnLocalReturnedToWorldAfterDeath: " + ex.Message);
            }
        }

        private void ResyncRemoteProxiesForOutsideLocation(string locationName)
        {
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null) return;
            // Prefer ResolveOutsideLocation (canonical / *_done / dream-safe) over
            // ContainsKey-only — dict may key under a twin name while peers send canonical.
            Location loc = ResolveOutsideLocation(ol, locationName);
            if (loc == null) return;
            loc.enter(force: true);

            // Peers known to be in this location (name-match strips *_done)
            foreach (var kvp in new List<KeyValuePair<int, string>>(_net.RemoteOutsideLocation))
            {
                if (!CoopWorldPresencePolicy.LocationNamesMatch(kvp.Value, locationName))
                    continue;
                PlaceRemoteProxyInOutsideLocation(kvp.Key, loc, preferLastKnown: true);
            }

            // Also any live peer with a recent position near the location (dict miss after race)
            if (loc.playerSpawn != null)
            {
                Vector3 spawn = loc.playerSpawn.transform.position;
                foreach (int peerId in _net.EnumeratePeerIds())
                {
                    if (peerId == _net.LocalPlayerId) continue;
                    if (_net.RemoteOutsideLocation.ContainsKey(peerId)) continue;
                    if (!PlayerPositionManager.TryGetRemote(peerId, out Vector3 pos, out _))
                        continue;
                    if ((pos - spawn).sqrMagnitude > 2500f * 2500f) continue;
                    _net.RemoteOutsideLocation[peerId] = locationName;
                    PlaceRemoteProxyInOutsideLocation(peerId, loc, preferLastKnown: true);
                }
            }
        }

        /// <summary>
        /// Soft-reconnect tears remote proxies but must not keep sticky membership —
        /// otherwise host SyncExistingLocationsTo LocationEnter sees firstEnter=false and
        /// skips PlaceRemoteProxyInOutsideLocation (proxy stays gone until a real exit/enter).
        /// </summary>
        internal void ClearMembershipForSoftReconnect()
        {
            int n = _net.RemoteOutsideLocation.Count;
            _net.RemoteOutsideLocation.Clear();
            _pendingPlaceOnLocationResolve.Clear();
            // Drop stale sticky from prior session; Handshake OK ForceAnnounce re-queues if needed.
            _pendingForceAnnounceReason = null;
            _deferredCreateWhileLocalLoading.Clear();
            if (n > 0)
                ModRuntime.LegacyInfo(
                    "[LocationSync] soft reconnect cleared RemoteOutsideLocation x" + n
                    + " (proxies destroyed — await LocationEnter re-place)");
        }

        public bool IsAnyRemoteInOutsideLocation(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return false;
            foreach (var kvp in _net.RemoteOutsideLocation)
            {
                if (CoopWorldPresencePolicy.LocationNamesMatch(kvp.Value, locationName))
                    return true;
            }
            return false;
        }

        private void TryLeaveUnoccupiedOutsideLocation(string locName)
        {
            if (_net.Role != NetworkRole.Host || string.IsNullOrEmpty(locName))
                return;
            if (Sync.DreamSyncManager.IsDreamActive
                || Sync.DreamSyncManager.IsDreamLocationName(locName))
                return;
            if (IsAnyRemoteInOutsideLocation(locName))
                return;

            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null) return;
            if (ol.playerInOutsideLocation
                && CoopWorldPresencePolicy.LocationNamesMatch(ol.currentLocationName ?? "", locName))
                return;

            Location loc = ResolveOutsideLocation(ol, locName);
            if (loc != null && loc.entered)
            {
                loc.leave();
                ModRuntime.LegacyInfo(
                    "[LocationSync] last remote left '" + locName + "' — host left location");
            }
        }
    }
}
