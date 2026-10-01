using System;
using System.Collections.Generic;
using System.Linq;
using DWMPHorde;
using DWMPHorde.Config;
using DWMPHorde.Logging;
using DWMPHorde.Patches;
using DWMPHorde.Players;
using DWMPHorde.Sync;
using HarmonyLib;
using LiteNetLib;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Drag / body-push interact handlers composed for 0.8.</summary>
    internal sealed partial class PlayerInteractNetHandlers
    {
        private readonly LanNetworkManager _net;

        /// <summary>Tracks which player currently claims each dragged object.</summary>
        internal readonly Dictionary<string, int> DragClaims = new Dictionary<string, int>();
        /// <summary>Instance IDs of items currently dragged by a remote peer.</summary>
        internal readonly HashSet<int> RemoteDragItemIds = new HashSet<int>();
        /// <summary>Names of items currently dragged by a remote peer (cross-peer claim check).</summary>
        internal readonly HashSet<string> RemoteDragItemNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Last synced position per dragged item (drag scrape gating).</summary>
        internal readonly Dictionary<string, Vector3> LastDragSyncPos = new Dictionary<string, Vector3>();
        /// <summary>After reliable DragSync STOP, ignore late Unreliable IsDragging packets.</summary>
        internal readonly Dictionary<string, float> DragEndedAt =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Ignore stale IsDragging packets for this long after a stop.</summary>
        internal const float DragStopStaleGraceConst = 1.0f;

        // Tracks host-spawned copies of items that don't exist on this side
        // (e.g. remote player dragged an item in an unloaded world-grid chunk).
        private readonly HashSet<int> _spawnedDragProxyItems = new HashSet<int>();

        internal PlayerInteractNetHandlers(LanNetworkManager net)
        {
            _net = net ?? throw new System.ArgumentNullException(nameof(net));
        }

        internal void ClearSpawnedDragProxyItems()
        {
            _spawnedDragProxyItems.Clear();
        }

        internal void ClearDragSessionState()
        {
            DragClaims.Clear();
            LastDragSyncPos.Clear();
            DragEndedAt.Clear();
            RemoteDragItemIds.Clear();
            RemoteDragItemNames.Clear();
            _spawnedDragProxyItems.Clear();
        }

        /// <summary>True if the object is claimed by a remote player (not the local one).</summary>
        internal bool IsDragClaimedByOther(string objectName, int localPlayerId)
        {
            if (string.IsNullOrEmpty(objectName)) return false;
            if (DragClaims.TryGetValue(objectName, out int claimerId))
                return claimerId >= 0 && claimerId != localPlayerId;
            return false;
        }

        internal void HandleDragSync(DragSyncMessage msg)
        {
            // Host: the transport peer is the only trustworthy claimer id; an embedded id could
            // claim, steal or release another player's drag.
            if (_net.Role == NetworkRole.Host && _net.CurrentReceivePlayerId > 0)
                msg.ClaimedByPlayerId = _net.CurrentReceivePlayerId;

            Vector3 targetPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Vector3 targetRot = new Vector3(msg.RotX, msg.RotY, msg.RotZ);

            // Late Unreliable IsDragging after reliable STOP re-claimed the object and
            // left host blocked ("already being moved"). Drop stale grab packets.
            if (msg.IsDragging && !string.IsNullOrEmpty(msg.ObjectName)
                && _net.PlayerInteractHandlers.DragEndedAt.TryGetValue(msg.ObjectName, out float endedAt)
                && (Time.unscaledTime - endedAt) < LanNetworkManager.DragStopStaleGraceConst)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo($"[DragSync] ignore stale IsDragging for {msg.ObjectName} (stop {(Time.unscaledTime - endedAt).ToString("F2")}s ago)");
                return;
            }

            // Claim only while actively dragging (end packets release, not re-claim).
            if (msg.IsDragging && msg.ClaimedByPlayerId >= 0 && !string.IsNullOrEmpty(msg.ObjectName))
            {
                _net.PlayerInteractHandlers.DragClaims[msg.ObjectName] = msg.ClaimedByPlayerId;
                _net.PlayerInteractHandlers.DragEndedAt.Remove(msg.ObjectName);
            }

            bool locallyDraggingThis = Player.Instance != null && Player.Instance.dragging &&
                Player.Instance.itemBeingDragged != null &&
                string.Equals(Player.Instance.itemBeingDragged.gameObject.name, msg.ObjectName,
                    System.StringComparison.Ordinal);

            // For the local DragSync echo, native ItemSounds already owns scrape,
            // applying MOS here doubles the sound for the dragging client.
            if (msg.IsDragging
                && (msg.ClaimedByPlayerId == _net.LocalPlayerId || locallyDraggingThis))
                return;

            // If a remote player claims an item we're dragging, force-stop our
            // drag to resolve the conflict and prevent double-drag desync.
            if (locallyDraggingThis && msg.IsDragging
                && msg.ClaimedByPlayerId >= 0 && msg.ClaimedByPlayerId != _net.LocalPlayerId)
            {
                ModRuntime.LegacyInfo($"[DragSync] remote player {msg.ClaimedByPlayerId} claimed {msg.ObjectName} — force-stopping local drag");
                Player.Instance.itemBeingDragged.stopDragging(force: true);
                _net.WasDragging = false;
                _net.LastDraggedItemName = null;
            }

            if (!msg.IsDragging)
            {
                bool ownStop = msg.ClaimedByPlayerId == _net.LocalPlayerId
                    || locallyDraggingThis
                    || (_net.PlayerInteractHandlers.DragClaims.TryGetValue(msg.ObjectName ?? "", out int stopClaim)
                        && stopClaim == _net.LocalPlayerId)
                    || DWMPHorde.Audio.ItemMovingSoundHelper.IsLocalPushOrDragOwner(msg.ObjectName);

                if (ownStop)
                {
                    // The local owner already stopped native audio; clear residual MOS only.
                    // ForceStopByName here zeroed RB + re-armed suppress while native still
                    // owned scrape on the next residual PhysicsState (felt like 2× scrape).
                    DWMPHorde.Audio.ItemMovingSoundHelper.SoftStopNetwork(msg.ObjectName);
                }
                else
                {
                    // Intentional end for observers: kill native residual + MOS.
                    DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(msg.ObjectName);
                    // Host: same intentional stop signal as body-push so residual PhysicsState
                    // after claim clear cannot re-arm scrape on observers.
                    if (_net.Role == NetworkRole.Host && !string.IsNullOrEmpty(msg.ObjectName))
                        NotifyBodyPushStopped(msg.ObjectName);
                    ModRuntime.LegacyInfo($"[DragSync] STOP IsDragging=false for {msg.ObjectName} — ForceStopByName issued");
                }

                _net.PlayerInteractHandlers.LastDragSyncPos.Remove(msg.ObjectName);
                CleanupSpawnedDragProxy(msg.ObjectName);
                // Release while the instance ids are still recorded, then drop them.
                ReleaseRemoteDragKinematic(msg.ObjectName);
                RemoveRemoteDragIds(msg.ObjectName);
                // Host free-body hold from client PhysicsState must also drop on drag end
                // or the object stays kinematic / untouchable for the host.
                if (!string.IsNullOrEmpty(msg.ObjectName))
                    Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(msg.ObjectName);
                // Always clear claim on stop (late unreliables cannot re-block; see grace above).
                if (!string.IsNullOrEmpty(msg.ObjectName))
                {
                    _net.PlayerInteractHandlers.DragClaims.Remove(msg.ObjectName);
                    _net.PlayerInteractHandlers.DragEndedAt[msg.ObjectName] = Time.unscaledTime;
                }
                return;
            }

            Item item = FindDraggedItemLocally(msg.ObjectName, targetPos);
            if (item == null)
            {
                // Item does not exist on this side. Spawn it on demand so we
                // can reflect the remote player's manipulation.
                item = SpawnDraggedItem(msg);
                if (item == null)
                {
                    ModRuntime.LegacyInfo($"[DragSync] cannot spawn \"{msg.ObjectName}\" type={msg.ItemType}");
                    return;
                }
                _spawnedDragProxyItems.Add(item.GetInstanceID());
                ModRuntime.LegacyInfo($"[DragSync] spawned {item.name} for remote drag");
            }
            else if (item.beingDragged || (Player.Instance != null && Player.Instance.dragging && Player.Instance.itemBeingDragged == item))
            {
                // Skip remote drag-updates for an object the local player is also
                // dragging. This prevents tug-of-war jitter between both sides.
                Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
                // Release kinematic so local HingeJoint can drive position.
                if (ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                {
                    Rigidbody kinematicRb = item.GetComponent<Rigidbody>();
                    if (kinematicRb != null)
                        kinematicRb.isKinematic = false;
                }
                // Tag so TryBuildWorldSnapshot skips this item. This prevents
                // PhysicsState (0.3 Hz) from fighting DragSync (30 Hz) even
                // when BOTH peers are dragging the same item (double grab).
                _net.PlayerInteractHandlers.RemoteDragItemIds.Add(item.GetInstanceID());
                _net.PlayerInteractHandlers.RemoteDragItemNames.Add(item.gameObject.name);
                return;
            }

            // Remove from interpolation. DragSyncMessage is more authoritative
            // and instant, while UpdateObjectInterpolation would smooth over
            // the jump and fight subsequent DragSync updates.
            Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
            // Tag so TryBuildWorldSnapshot skips this item. This prevents
            // PhysicsState (0.3 Hz) from fighting DragSync (30 Hz).
            _net.PlayerInteractHandlers.RemoteDragItemIds.Add(item.GetInstanceID());
            _net.PlayerInteractHandlers.RemoteDragItemNames.Add(item.gameObject.name);

            Rigidbody targetRb = item.GetComponent<Rigidbody>();
            if (targetRb != null)
            {
                targetRb.position = targetPos;
                targetRb.rotation = Quaternion.Euler(targetRot);
                targetRb.velocity = Vector3.zero;
                targetRb.angularVelocity = Vector3.zero;
                // Lock to host position between DragSync frames. This prevents proxy
                // collisions on the client from pushing the item away.
                if (ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                    targetRb.isKinematic = true;

                // Scrape: sender sets ScrapeActive from *player walk intent* (body-push style).
                // When false (reliable), stop fade now. Do not wait for posDelta quiet or
                // Unreliable packet loss. First grab packet may still have ScrapeActive false.
                ItemSounds dragSounds = item.GetComponent<ItemSounds>();
                bool hasPrev = _net.PlayerInteractHandlers.LastDragSyncPos.TryGetValue(msg.ObjectName, out Vector3 prevPos);
                float dist = hasPrev ? Vector3.Distance(prevPos, targetPos) : 0f;
                const float dragMoveStart = 0.02f;
                const float dragRearmWhileFading = 0.08f;
                bool fading = DWMPHorde.Audio.MovingObjectSoundService.IsFading(msg.ObjectName);
                bool moved = hasPrev && dist >= (fading ? dragRearmWhileFading : dragMoveStart);

                if (!msg.ScrapeActive)
                {
                    // Intentional quiet while still grabbed. Start vanilla fade immediately.
                    // No ForceStop/Sleep (still remote-kinematic); no suppress so walk-resume re-arms.
                    DWMPHorde.Audio.MovingObjectSoundService.StopNetwork(msg.ObjectName);
                }
                else if (moved)
                {
                    DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(
                        item.gameObject, msg.ObjectName, dragSounds);
                }
                else if (hasPrev)
                {
                    // ScrapeActive but the object barely moved (turning in place). Soft fade.
                    DWMPHorde.Audio.MovingObjectSoundService.NoteStationary(msg.ObjectName);
                }
                _net.PlayerInteractHandlers.LastDragSyncPos[msg.ObjectName] = targetPos;
            }
            else
            {
                // Fallback: no Rigidbody; set the transform directly.
                item.transform.position = targetPos;
                item.transform.rotation = Quaternion.Euler(targetRot);
                ModRuntime.Log?.LogWarning("[DragSync] " + msg.ObjectName + " has no Rigidbody — used transform fallback");
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo($"[DragSync] {item.name} -> {targetPos}");
        }
    }
}
