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
        /// <summary>The body of the local drag in progress (its pose rides on the STOP).</summary>
        private Item _lastDraggedItem;

        /// <summary>
        /// Intentional E-drag release, in the same frame as vanilla
        /// <c>Item.stopDragging</c>.
        /// Push stop is frame-perfect via <see cref="ItemMovingSoundHelper.TickLocalPushScrapeStop"/>;
        /// drag used to wait for the next 30 Hz PlayerState tick, so observers heard scrape longer.
        /// Reliable DragSync stop + local ForceStop; host also emits body-push stop signal so
        /// residual PhysicsState cannot re-arm MOS after claim clears.
        /// </summary>
        /// <param name="endedItem">The body let go of: its pose rides on the STOP so observers
        /// play their copy out to where it really ended (null: no pose, observers release where
        /// they are).</param>
        public void NotifyLocalDragEnded(string objectName, Item endedItem = null)
        {
            if (!_wasDragging && string.IsNullOrEmpty(objectName) && string.IsNullOrEmpty(_lastDraggedItemName))
                return;

            string endedName = !string.IsNullOrEmpty(objectName) ? objectName : (_lastDraggedItemName ?? "");
            if (endedItem == null)
                endedItem = _lastDraggedItem;
            _lastDraggedItem = null;
            _wasDragging = false;
            _lastDraggedItemName = null;
            _dragScrapeActive = false;
            _dragScrapeQuietSince = -1f;

            if (!string.IsNullOrEmpty(endedName)
                && PlayerInteractHandlers.DragClaims.TryGetValue(endedName, out int cid)
                && cid == _localPlayerId)
                PlayerInteractHandlers.DragClaims.Remove(endedName);

            if (!string.IsNullOrEmpty(endedName))
                PlayerInteractHandlers.DragEndedAt[endedName] = Time.unscaledTime;

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
            if (endedItem != null && endedItem.gameObject != null
                && string.Equals(endedItem.gameObject.name, endedName, StringComparison.Ordinal))
            {
                Transform endT = endedItem.transform;
                Vector3 endPos = endT.position;
                Vector3 endEuler = endT.rotation.eulerAngles;
                dragMsg.PosX = endPos.x;
                dragMsg.PosY = endPos.y;
                dragMsg.PosZ = endPos.z;
                dragMsg.RotX = endEuler.x;
                dragMsg.RotY = endEuler.y;
                dragMsg.RotZ = endEuler.z;
                dragMsg.SendTime = RemoteDragTimeline.StampFor(endedItem.GetComponent<Rigidbody>());
                dragMsg.HasPose = true;
            }
            BroadcastHot(NetMessageType.DragSync, w => dragMsg.Serialize(w), LiteNetLib.DeliveryMethod.ReliableOrdered);

            if (!string.IsNullOrEmpty(endedName))
            {
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(endedName);
                // Release the host rigidbody hold after our own drag so peers
                // can interact with it.
                Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(endedName);
                // Host: clear this peer's push tracking too, so residual PhysicsState after the
                // claim release cannot keep a scrape armed here (peers stop on DragSync STOP).
                if (_role == NetworkRole.Host)
                    NotifyBodyPushStopped(endedName);
            }
        }

        private void LateUpdate()
        {
            if (!IsConnected || !_session.Link.HandshakeComplete) return;

            bool perf = IsConnected && _session.Link.HandshakeComplete
                && (_role == NetworkRole.Client || _role == NetworkRole.Host);
            if (perf) ClientPerfProbe.LateBegin();

            // Both sides: interpolate world physics objects
            Sync.WorldPhysicsSyncService.UpdateObjectInterpolation();
            // Both sides: other players' E-drags, posed after vanilla Update moved anything.
            RemoteDragTimeline.Tick();
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
