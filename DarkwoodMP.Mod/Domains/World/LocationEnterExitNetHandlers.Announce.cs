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
    /// <summary>Soft-reconnect ForceAnnounce sticky + deferred create + proxy place.</summary>
    internal sealed partial class LocationEnterExitNetHandlers
    {
        /// <summary>
        /// Phase-3 AlreadyInWorld: announce local pad immediately (do not wait ~1 Hz heartbeat).
        /// Host cleared our membership on the brief disconnect; sticky Tick alone can lag a second.
        /// Mid ol.loading / loadingGame: queue sticky retry until settle (TryFlush / settle hook).
        /// </summary>
        internal void ForceAnnounceLocalOutsideLocationEnter(string reason)
        {
            if (!_net.IsConnected)
            {
                _pendingForceAnnounceReason = null;
                return;
            }

            if (TrySendForcedLocationEnter(reason ?? "force"))
                return;

            // Not ready — sticky only when mid-load or we still believe we are on a pad.
            // World-map soft reconnect must remain a silent no-op (no forever pending).
            var ol = Singleton<OutsideLocations>.Instance;
            bool midLoad = Core.loadingGame || (ol != null && ol.loading);
            bool hadPad = _net.PreviousInOutsideLocation
                || !string.IsNullOrEmpty(_net.PreviousLocationName);
            if (!midLoad && !hadPad)
                return;

            string tag = reason ?? "force";
            if (_pendingForceAnnounceReason == null)
            {
                ModRuntime.LegacyInfo(
                    $"[LocationSync] ForceAnnounce queued ({tag}) — await settle (loadingGame={Core.loadingGame} ol.loading={(ol != null && ol.loading)} inPad={(ol != null && ol.playerInOutsideLocation)})");
            }
            _pendingForceAnnounceReason = tag;
        }

        /// <summary>True while a soft-reconnect LocationEnter announce is waiting for settle.</summary>
        internal bool HasPendingForceAnnounce => !string.IsNullOrEmpty(_pendingForceAnnounceReason);

        /// <summary>
        /// Tick / settle: fire queued ForceAnnounce once playerInOutsideLocation is true.
        /// Also retry remote createLocation deferred while local ol.loading.
        /// </summary>
        internal void TryFlushPendingForceAnnounce()
        {
            if (!_net.IsConnected)
            {
                _pendingForceAnnounceReason = null;
                _deferredCreateWhileLocalLoading.Clear();
                return;
            }

            if (!string.IsNullOrEmpty(_pendingForceAnnounceReason))
            {
                string reason = _pendingForceAnnounceReason;
                if (TrySendForcedLocationEnter(reason + " sticky"))
                    _pendingForceAnnounceReason = null;
            }

            TryFlushDeferredCreatesWhileLocalLoading();
        }

        /// <summary>Settle / return-to-world already announced or left — drop sticky queue.</summary>
        internal void ClearPendingForceAnnounce(string why)
        {
            if (string.IsNullOrEmpty(_pendingForceAnnounceReason)) return;
            ModRuntime.LegacyInfo(
                $"[LocationSync] ForceAnnounce pending cleared ({why}): {_pendingForceAnnounceReason}");
            _pendingForceAnnounceReason = null;
        }

        private bool TrySendForcedLocationEnter(string reason)
        {
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || !ol.playerInOutsideLocation) return false;
            // Still mid OutsideLocations load screen — wait for transport settle.
            if (ol.loading) return false;
            if (Core.loadingGame) return false;
            string locName = ol.currentLocationName ?? "";
            if (string.IsNullOrEmpty(locName)) return false;

            bool dreamLocActive = Sync.DreamSyncManager.IsDreamActive;
            string txName = dreamLocActive
                ? Sync.DreamSyncManager.CanonicalDreamLocationName(locName)
                : locName;

            _net.LocationSyncCounter = 0;
            _net.PreviousInOutsideLocation = true;
            _net.PreviousLocationName = locName;
            _pendingForceAnnounceReason = null;

            int pid = _net.LocalPlayerId;
            _net.BroadcastHot(NetMessageType.LocationEnter,
                w => new LocationEnterMessage
                {
                    LocationName = txName,
                    PlayerId = pid
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[LocationSync] forced LocationEnter ({reason}): {txName} pid={pid}");
            return true;
        }

        private void TryFlushDeferredCreatesWhileLocalLoading()
        {
            if (_deferredCreateWhileLocalLoading.Count == 0) return;
            if (_net.Role != NetworkRole.Host)
            {
                _deferredCreateWhileLocalLoading.Clear();
                return;
            }
            if (Core.loadingGame) return;
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol == null || ol.loading) return;

            var keys = new List<string>(_deferredCreateWhileLocalLoading);
            _deferredCreateWhileLocalLoading.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                string locName = keys[i];
                if (string.IsNullOrEmpty(locName)) continue;

                // Dream pads: LoadDreamSceneCoroutine owns spawn — never createLocation.
                if (locName.StartsWith("dream_", StringComparison.OrdinalIgnoreCase)
                    || Sync.DreamSyncManager.IsDreamLocationName(locName)
                    || Sync.DreamSyncManager.IsDreamActive
                    || (Dreams.Instance != null
                        && (Dreams.Instance.dreamPrepared || Dreams.Instance.dreaming)))
                    continue;

                Location existing = ResolveOutsideLocation(ol, locName);
                if (existing != null)
                {
                    // Pad appeared during local load — place any peers waiting on resolve.
                    foreach (int peerId in new List<int>(_pendingPlaceOnLocationResolve))
                    {
                        if (!_net.RemoteOutsideLocation.TryGetValue(peerId, out string peerLoc)
                            || !CoopWorldPresencePolicy.LocationNamesMatch(peerLoc, locName))
                            continue;
                        _pendingPlaceOnLocationResolve.Remove(peerId);
                        PlaceRemoteProxyInOutsideLocation(peerId, existing, preferLastKnown: true);
                    }
                    continue;
                }

                TryEnterLocationGridNearRemotes(locName);

                bool localNeedsPad = ol.playerInOutsideLocation
                    && CoopWorldPresencePolicy.LocationNamesMatch(
                        ol.currentLocationName ?? "", locName);
                if (!localNeedsPad && !TryBeginRemoteLocationCreate(locName))
                {
                    // Rate-limit still hot — keep deferred for next Tick.
                    _deferredCreateWhileLocalLoading.Add(locName);
                    continue;
                }

                ModRuntime.LegacyInfo(
                    $"[LocationSync] flush deferred createLocation after local loading: {locName}");
                if (localNeedsPad)
                    NoteRemoteLocationCreate(locName);
                ol.createLocation(locName);
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

    }
}
