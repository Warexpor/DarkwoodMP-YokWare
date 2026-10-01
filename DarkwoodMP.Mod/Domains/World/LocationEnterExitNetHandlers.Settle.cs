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
    /// <summary>Outside-location enter/exit: local settle and return-to-world (split for size).</summary>
    internal sealed partial class LocationEnterExitNetHandlers
    {
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
                // Local loading screen done — retry remote pads deferred while it was up.
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
    }
}
