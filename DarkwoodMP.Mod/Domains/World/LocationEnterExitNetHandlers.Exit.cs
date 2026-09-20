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

            // During join load proxies are torn down or not spawnable; teleporting caused an NRE.
            // on destroyed dict entries. First live PlayerState will place them.
            if (!LanNetworkManager.CanSpawnRemoteProxies())
            {
                ModRuntime.LegacyInfo(
                    $"[LocationSync] defer LocationExit p{playerId} (world not ready for proxies)");
                return;
            }

            // Do not call leaveAllLocations(); it deactivates locations the local player
            // may still be inside (2p/3+ desync / blackout). Last occupant: leave
            // only that pad so exit events fire once nobody remains.
            Vector3 worldPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            _net.TeleportRemoteProxyTo(worldPos, playerId: playerId);
            if (_net.Role == NetworkRole.Host)
                TryLeaveUnoccupiedOutsideLocation(leftLoc);
            ModRuntime.LegacyInfo($"[LocationSync] player {playerId} exited → proxy at {worldPos}");
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
                _net.SendToPlayer(targetPlayerId, NetMessageType.LocationEnter,
                    w => new LocationEnterMessage
                    {
                        LocationName = hostLoc,
                        PlayerId = 1
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
