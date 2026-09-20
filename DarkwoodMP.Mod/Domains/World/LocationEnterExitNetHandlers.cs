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

            bool firstEnterThisLoc = !_net.RemoteOutsideLocation.TryGetValue(playerId, out string prev)
                || !string.Equals(prev, locName, StringComparison.OrdinalIgnoreCase);
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
                    && string.Equals(localCanon, locName, StringComparison.OrdinalIgnoreCase);
                bool dreamLoc = Sync.DreamSyncManager.IsDreamLocationName(locName)
                    || (Sync.DreamSyncManager.IsDreamActive
                        && locName.StartsWith("dream_", StringComparison.OrdinalIgnoreCase));
                bool shouldPlace = firstEnterThisLoc || (localSameLoc && !dreamLoc);
                if (shouldPlace)
                    PlaceRemoteProxyInOutsideLocation(playerId, loc, preferLastKnown: true);

                // Host never setGrid for a remote-only pad; wake that location's
                // WorldGrid nodes around remotes so bunker Cullables/AI run.
                if (_net.Role == NetworkRole.Host)
                    TryEnterLocationGridNearRemotes(locName);

                // Peer just got location geometry; re-push sticky lamp/gen state that
                // may have been applied (or dropped) while the grid was unloaded.
                if (_net.Role == NetworkRole.Host && firstEnterThisLoc && playerId != _net.LocalPlayerId)
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
                    ModRuntime.LegacyInfo(
                        $"[LocationSync] dream pad not ready yet, skip createLocation: {locName}");
                    return;
                }

                // Async spawn; ~1 Hz LocationEnter retries until key exists.
                // Keep firstEnter pending by clearing so next successful enter still snaps once.
                if (firstEnterThisLoc)
                    _net.RemoteOutsideLocation.Remove(playerId);
                string createName = locName;
                ModRuntime.LegacyInfo($"[LocationSync] location not spawned, creating: {createName}");
                ol.createLocation(createName);
            }
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
            if (ol == null || !ol.spawnedLocations.ContainsKey(locationName))
                return;
            var loc = ol.spawnedLocations[locationName];
            loc.enter(force: true);

            // Peers known to be in this location
            foreach (var kvp in new List<KeyValuePair<int, string>>(_net.RemoteOutsideLocation))
            {
                if (!string.Equals(kvp.Value, locationName, StringComparison.OrdinalIgnoreCase))
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

        internal void PlaceRemoteProxyInOutsideLocation(int playerId, Location loc, bool preferLastKnown)
        {
            if (playerId <= 0 || loc == null) return;
            _net.EnsureRemoteProxy(playerId);
            if (!_net.RemoteProxies.ContainsKey(playerId))
                return;

            // Active dream: never anchor to a completed *_done instance.
            if (Sync.DreamSyncManager.IsDreamActive
                && loc.gameObject != null
                && loc.gameObject.name.EndsWith("_done", StringComparison.OrdinalIgnoreCase))
            {
                var dreamLoc = Dreams.Instance != null ? Dreams.Instance.dreamLocation : null;
                if (dreamLoc != null)
                    loc = dreamLoc;
                else
                {
                    // No scoped dream Location means the async pad is not ready.
                    // Never use a global name lookup, which can select the
                    // overworld duplicate.
                    return;
                }
            }

            Vector3 pos;
            float rotY = 0f;
            if (preferLastKnown && PlayerPositionManager.TryGetRemote(playerId, out pos, out rotY))
            {
                // Last known must look like it is in this location (not stale world map).
                // Compare XZ only. 3D distance rejected valid host positions when the client
                // playerSpawn Y disagreed (bunker: place at Y=-12k → invisible).
                if (loc.playerSpawn != null)
                {
                    Vector3 spawn = loc.playerSpawn.transform.position;
                    float dx = pos.x - spawn.x;
                    float dz = pos.z - spawn.z;
                    if (dx * dx + dz * dz > 2500f * 2500f)
                        pos = spawn;
                }
            }
            else if (loc.playerSpawn != null)
            {
                pos = loc.playerSpawn.transform.position;
            }
            else
            {
                return;
            }

            var proxy = _net.GetProxy(playerId);
            if (proxy != null)
                proxy.FreezePosition = false;

            _net.TeleportRemoteProxyTo(pos, rotY, playerId);
            string placeName = loc.gameObject != null
                ? Sync.DreamSyncManager.CanonicalDreamLocationName(loc.gameObject.name)
                : loc.name;
            ModRuntime.LegacyInfo(
                $"[LocationSync] placed p{playerId} proxy in '{placeName}' at {pos}");
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
