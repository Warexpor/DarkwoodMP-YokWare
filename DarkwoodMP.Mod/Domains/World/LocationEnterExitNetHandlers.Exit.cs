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

            MovePeerOutOfLocation(playerId, msg, leftLoc);

            // Last one out: leave the pad so its exit events fire and its AI stops. After the
            // proxy moved: HostLocationLeaveKeepRemotePatch keeps a pad with a proxy standing
            // in it, and the leaver's proxy was still on the pad. Runs on every path, also when
            // the proxy could not be moved yet (join load).
            if (_net.Role == NetworkRole.Host)
                TryLeaveUnoccupiedOutsideLocation(leftLoc);
        }

        private void MovePeerOutOfLocation(int playerId, LocationExitMessage msg, string leftLoc)
        {
            // Disconnect fan-out: peer already gone from roster → destroy proxy, do NOT
            // Teleport (EnsureRemoteProxy would recreate a frozen ghost). Living peers
            // who walked out remain in roster and teleport below.
            // Skip while roster is still empty (pre-first-gossip) so a normal LocationExit
            // before PeerRoster arrives does not false-destroy.
            if (playerId != _net.LocalPlayerId
                && _net.PeerRosterCount > 0
                && !_net.IsPlayerListedInPeerRoster(playerId))
            {
                _net.WorldProxyLifecycleHandlers.DestroyRemoteProxy(playerId);
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
            // may still be inside (2p/3+ desync / blackout).
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

            // Host-owned pad slot/yaw map first: the joiner must spawn any pad it does not
            // have yet at the host's coordinates, not at its own first-spawn order.
            OutsidePadSlots.SendSnapshotTo(_net, targetPlayerId);

            int sent = 0;

            // Host's own outside location
            var ol = Singleton<OutsideLocations>.Instance;
            // Not the host's own prologue pad: it exists on the host's machine only.
            if (ol != null && ol.playerInOutsideLocation
                && !string.IsNullOrEmpty(ol.currentLocationName)
                && !PersonalPrologue.IsPrologueDream(ol.currentLocationName))
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
    }
}
