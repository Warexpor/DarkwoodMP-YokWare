using System;
using System.Collections.Generic;
using DWMPHorde;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Per-frame Update / LateUpdate send and flush tick (split from main for ownership).
    /// </summary>
    public sealed partial class LanNetworkManager
    {

        /// <summary>
        /// Intentional E-drag release, in the same frame as vanilla
        /// <c>Item.stopDragging</c>.
        /// Push stop is frame-perfect via <see cref="ItemMovingSoundHelper.TickLocalPushScrapeStop"/>;
        /// drag used to wait for the next 30 Hz PlayerState tick, so observers heard scrape longer.
        /// Reliable DragSync stop + local ForceStop; host also emits body-push stop signal so
        /// residual PhysicsState cannot re-arm MOS after claim clears.
        /// </summary>
        public void NotifyLocalDragEnded(string objectName)
        {
            if (!_wasDragging && string.IsNullOrEmpty(objectName) && string.IsNullOrEmpty(_lastDraggedItemName))
                return;

            string endedName = !string.IsNullOrEmpty(objectName) ? objectName : (_lastDraggedItemName ?? "");
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;

            if (!string.IsNullOrEmpty(endedName)
                && _dragClaims.TryGetValue(endedName, out int cid)
                && cid == _localPlayerId)
                _dragClaims.Remove(endedName);

            if (!string.IsNullOrEmpty(endedName))
                _dragEndedAt[endedName] = Time.unscaledTime;

            if (!IsConnected)
            {
                if (!string.IsNullOrEmpty(endedName))
                    DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(endedName);
                return;
            }

            var dragMsg = new DragSyncMessage
            {
                IsDragging = false,
                ObjectName = endedName,
                ClaimedByPlayerId = _localPlayerId
            };
            BroadcastHot(NetMessageType.DragSync, w => dragMsg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);

            if (!string.IsNullOrEmpty(endedName))
            {
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(endedName);
                // Release the host rigidbody hold after our own drag so peers
                // can interact with it.
                Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(endedName);
                // Host: dual-path intentional stop (PlayerAudio IsStopSignal) so residual
                // PhysicsState after claim release cannot keep scrape armed on peers.
                if (_role == NetworkRole.Host)
                    NotifyBodyPushStopped(endedName);
            }
        }

        private void LateUpdate()
        {
            if (!IsConnected || !_handshakeComplete) return;

            bool perf = IsConnected && _handshakeComplete
                && (_role == NetworkRole.Client || _role == NetworkRole.Host);
            if (perf) ClientPerfProbe.LateBegin();

            // Both sides: interpolate world physics objects
            Sync.WorldPhysicsSyncService.UpdateObjectInterpolation();
            if (perf) ClientPerfProbe.MarkObjInterp();

            // Client only: interpolate remote entity positions for smooth movement
            if (_role == NetworkRole.Client)
            {
                ClientEntityInterpolationService.TickLateUpdate();
                if (perf) ClientPerfProbe.MarkEntityTick();
            }

            if (perf) ClientPerfProbe.LateEnd();
        }
    }
}
