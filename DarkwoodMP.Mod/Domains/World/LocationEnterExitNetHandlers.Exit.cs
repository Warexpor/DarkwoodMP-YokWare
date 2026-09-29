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

        internal void HandleLocationExit(LocationExitMessage msg)
        {
            int playerId = _net.Role == NetworkRole.Host
                ? _net.CurrentReceivePlayerId
                : (msg.PlayerId > 0 ? msg.PlayerId : _net.CurrentReceivePlayerId);
            if (playerId <= 0)
                return;

            string leftLoc = null;
            _net.RemoteOutsideLocation.TryGetValue(playerId, out leftLoc);
            _net.RemoteOutsideLocation.Remove(playerId);
            _pendingPlaceOnLocationResolve.Remove(playerId);

            // ALWAYS run host leave-unoccupied after Remove — even when proxy spawn is
            // deferred (loadingGame). Previously an early return skipped this and left
            // bunkers active with zero remotes until a later exit.
            if (_net.Role == NetworkRole.Host)
                TryLeaveUnoccupiedOutsideLocation(leftLoc);

            // Disconnect fan-out: peer already gone from roster → destroy proxy, do NOT
            // Teleport (EnsureRemoteProxy would recreate a frozen ghost). Living peers
            // who walked out remain in roster and teleport below.
            // Skip while roster is still empty (pre-first-gossip) so a normal LocationExit
            // before PeerRoster arrives does not false-destroy.
            if (playerId != _net.LocalPlayerId
                && _net.PeerRosterCount > 0
                && !_net.IsPlayerListedInPeerRoster(playerId))
            {
                _net.WorldProxyHandlers.DestroyRemoteProxy(playerId);
                ModRuntime.LegacyInfo(
                    $"[LocationSync] player {playerId} left session → destroyed proxy (was loc={leftLoc ?? "-"})");
                return;
            }

            // During join load proxies are torn down or not spawnable; teleporting caused an NRE
            // on destroyed dict entries. First live PlayerState will place them.
            if (!LanNetworkManager.CanSpawnRemoteProxies())
            {
                ModRuntime.LegacyInfo(
                    $"[LocationSync] defer LocationExit teleport p{playerId} (world not ready for proxies)");
                return;
            }

            // Do not call leaveAllLocations(); it deactivates locations the local player
            // may still be inside (2p/3+ desync / blackout). Last occupant: leave
            // only that pad so exit events fire once nobody remains.
            Vector3 worldPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            _net.TeleportRemoteProxyTo(worldPos, playerId: playerId);
            ModRuntime.LegacyInfo($"[LocationSync] player {playerId} exited → proxy at {worldPos}");
        }

        /// <summary>
        /// Host: peer disconnected while (possibly) inside an OutsideLocation.
        /// Clears membership, leaves unoccupied pads, and fans LocationExit to remaining
        /// peers so they destroy the proxy + drop RemoteOutsideLocation (N-peer safe).
        /// </summary>
        internal void NotifyRemotePeerDisconnected(
            int playerId, string leftLoc, float posX = 0f, float posY = 0f, float posZ = 0f)
        {
            if (playerId <= 0) return;

            if (!string.IsNullOrEmpty(leftLoc)
                || _net.RemoteOutsideLocation.ContainsKey(playerId))
            {
                if (string.IsNullOrEmpty(leftLoc))
                    _net.RemoteOutsideLocation.TryGetValue(playerId, out leftLoc);
                _net.RemoteOutsideLocation.Remove(playerId);
                _pendingPlaceOnLocationResolve.Remove(playerId);
            }

            if (_net.Role == NetworkRole.Host)
                TryLeaveUnoccupiedOutsideLocation(leftLoc);

            if (_net.Role != NetworkRole.Host || !_net.IsConnected)
                return;

            // Prefer caller-captured pos (before RemovePlayer). Fallback to live lookup.
            float px = posX, py = posY, pz = posZ;
            if (px == 0f && py == 0f && pz == 0f
                && PlayerPositionManager.TryGetRemote(playerId, out Vector3 pos, out _))
            {
                px = pos.x; py = pos.y; pz = pos.z;
            }

            int pid = playerId;
            float bx = px, by = py, bz = pz;
            _net.Broadcast(NetMessageType.LocationExit,
                w => new LocationExitMessage
                {
                    PosX = bx,
                    PosY = by,
                    PosZ = bz,
                    PlayerId = pid
                }.Serialize(w),
                DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                $"[LocationSync] disconnect LocationExit fan-out p{playerId} loc={leftLoc ?? "-"}");
        }

        private static void TryEnterLocationGridNearRemotes(string locName)
        {
            if (string.IsNullOrEmpty(locName)) return;
            var wg = Singleton<WorldGrid>.Instance;
            if (wg == null) return;
            string gridName = Core.getTrueLocationName(locName);
            WorldGrid.Grid grid = wg.getGrid(gridName);
            if (grid == null && !string.Equals(gridName, locName, StringComparison.Ordinal))
                grid = wg.getGrid(locName);
            if (grid == null) return;
            HostWorldGridProxyCullPatch.EnterNodesNearRemotes(
                grid, GameplayConstants.EntityActivationRange);
        }

        /// <summary>
        /// Late join: push which remotes (and host) are inside OutsideLocations so geometry
        /// is active and proxies can be placed. PlayerState then refines position.
        /// </summary>
        internal void SyncExistingLocationsTo(int targetPlayerId)
        {
            if (_net.Role != NetworkRole.Host || targetPlayerId <= 0) return;

            int sent = 0;

            // Host's own outside location
            var ol = Singleton<OutsideLocations>.Instance;
            if (ol != null && ol.playerInOutsideLocation
                && !string.IsNullOrEmpty(ol.currentLocationName))
            {
                string hostLoc = ol.currentLocationName;
                // Host id is LocalPlayerId (normally 1); never hardcode — migration
                // may promote a non-1 host.
                int hostPid = _net.LocalPlayerId;
                _net.SendToPlayer(targetPlayerId, NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = hostLoc,
                        PlayerId = hostPid
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            foreach (var kvp in _net.RemoteOutsideLocation)
            {
                if (kvp.Key == targetPlayerId) continue;
                if (string.IsNullOrEmpty(kvp.Value)) continue;
                int pid = kvp.Key;
                string name = kvp.Value;
                _net.SendToPlayer(targetPlayerId, NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = name,
                        PlayerId = pid
                    }.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                sent++;
            }

            if (sent > 0)
                ModLog.Event(LogCat.Session, $"[BulkSync] LocationEnter x{sent} → p{targetPlayerId}");
        }

        /// <summary>
        /// Vanilla enter events run in Location.OnActivated, which only the local
        /// visitor calls. A client visit never reached the host. Fire once, when
        /// the first party member enters and the host is not already inside.
        /// </summary>
        private void TryFireRemoteLocationEnterEvents(OutsideLocations ol, Location loc, string locName, int playerId)
        {
            if (loc == null || ol == null) return;
            if (Core.loadingGame) return;
            if (ol.playerInOutsideLocation
                && CoopWorldPresencePolicy.LocationNamesMatch(ol.currentLocationName ?? "", locName))
                return;
            foreach (var kvp in _net.RemoteOutsideLocation)
            {
                if (kvp.Key == playerId) continue;
                if (CoopWorldPresencePolicy.LocationNamesMatch(kvp.Value ?? "", locName))
                    return;
            }
            if (loc.events == null || loc.events.Count == 0) return;

            ModRuntime.LegacyInfo("[LocationSync] host onEnterLocation for '" + locName + "'");
            DialogHostApplyGuard.RunHostWorldFanout(() =>
            {
                for (int i = 0; i < loc.events.Count; i++)
                {
                    if (loc.events[i] != null)
                        Core.sendTriggerInfo(loc.events[i].gameObject, EventTrigger.Type.onEnterLocation);
                }
            });
        }
    }
}
