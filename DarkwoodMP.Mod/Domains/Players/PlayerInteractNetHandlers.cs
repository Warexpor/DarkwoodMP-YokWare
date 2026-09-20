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
    internal sealed class PlayerInteractNetHandlers
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
            Vector3 targetPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Vector3 targetRot = new Vector3(msg.RotX, msg.RotY, msg.RotZ);

            // Late Unreliable IsDragging after reliable STOP re-claimed the object and
            // left host blocked ("already being moved"). Drop stale grab packets.
            if (msg.IsDragging && !string.IsNullOrEmpty(msg.ObjectName)
                && _net._dragEndedAt.TryGetValue(msg.ObjectName, out float endedAt)
                && (Time.unscaledTime - endedAt) < LanNetworkManager.DragStopStaleGraceConst)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[DragSync] ignore stale IsDragging for " + msg.ObjectName
                        + " (stop " + (Time.unscaledTime - endedAt).ToString("F2") + "s ago)");
                return;
            }

            // Claim only while actively dragging (end packets release, not re-claim).
            if (msg.IsDragging && msg.ClaimedByPlayerId >= 0 && !string.IsNullOrEmpty(msg.ObjectName))
            {
                _net._dragClaims[msg.ObjectName] = msg.ClaimedByPlayerId;
                _net._dragEndedAt.Remove(msg.ObjectName);
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
                ModRuntime.LegacyInfo("[DragSync] remote player " + msg.ClaimedByPlayerId + " claimed " + msg.ObjectName + " — force-stopping local drag");
                Player.Instance.itemBeingDragged.stopDragging(force: true);
                _net._wasDragging = false;
                _net._lastDraggedItemName = null;
            }

            if (!msg.IsDragging)
            {
                bool ownStop = msg.ClaimedByPlayerId == _net.LocalPlayerId
                    || locallyDraggingThis
                    || (_net._dragClaims.TryGetValue(msg.ObjectName ?? "", out int stopClaim)
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
                    ModRuntime.LegacyInfo("[DragSync] STOP IsDragging=false for " + msg.ObjectName + " — ForceStopByName issued");
                }

                _net._lastDragSyncPos.Remove(msg.ObjectName);
                CleanupSpawnedDragProxy(msg.ObjectName);
                // Remove remote-drag tracking for items of this name so
                // PhysicsState can resume sending their position.
                RemoveRemoteDragIds(msg.ObjectName);
                // Release kinematic so local physics can affect the item again.
                ReleaseRemoteDragKinematic(msg.ObjectName);
                // Host free-body hold from client PhysicsState must also drop on drag end
                // or the object stays kinematic / untouchable for the host.
                if (!string.IsNullOrEmpty(msg.ObjectName))
                    Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(msg.ObjectName);
                // Always clear claim on stop (late unreliables cannot re-block; see grace above).
                if (!string.IsNullOrEmpty(msg.ObjectName))
                {
                    _net._dragClaims.Remove(msg.ObjectName);
                    _net._dragEndedAt[msg.ObjectName] = Time.unscaledTime;
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
                    ModRuntime.LegacyInfo("[DragSync] cannot spawn \"" + msg.ObjectName + "\" type=" + msg.ItemType);
                    return;
                }
                _spawnedDragProxyItems.Add(item.GetInstanceID());
                ModRuntime.LegacyInfo("[DragSync] spawned " + item.name + " for remote drag");
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
                _net._remoteDragItemIds.Add(item.GetInstanceID());
                _net._remoteDragItemNames.Add(item.gameObject.name);
                return;
            }

            // Remove from interpolation. DragSyncMessage is more authoritative
            // and instant, while UpdateObjectInterpolation would smooth over
            // the jump and fight subsequent DragSync updates.
            Sync.WorldPhysicsSyncService.RemoveObjectFromInterpolation(item.gameObject);
            // Tag so TryBuildWorldSnapshot skips this item. This prevents
            // PhysicsState (0.3 Hz) from fighting DragSync (30 Hz).
            _net._remoteDragItemIds.Add(item.GetInstanceID());
            _net._remoteDragItemNames.Add(item.gameObject.name);

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
                bool hasPrev = _net._lastDragSyncPos.TryGetValue(msg.ObjectName, out Vector3 prevPos);
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
                _net._lastDragSyncPos[msg.ObjectName] = targetPos;
            }
            else
            {
                // Fallback: no Rigidbody; set the transform directly.
                item.transform.position = targetPos;
                item.transform.rotation = Quaternion.Euler(targetRot);
                ModRuntime.Log?.LogWarning("[DragSync] " + msg.ObjectName + " has no Rigidbody — used transform fallback");
            }

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[DragSync] " + item.name + " -> " + targetPos);
        }

        /// <summary>Bridge called from WorldPhysicsSyncService when host applies a
        /// client's PhysicsState update for a body-pushed object (not E-drag).
        /// Plays scrape through MOS locally. Start is not broadcast as PlayerAudio;
        /// PhysicsState fan-out already drives NoteMoving on observers (T2: single owner).
        /// Reliable stop still uses <see cref="NotifyBodyPushStopped"/>.</summary>
        internal void NotifyBodyPushStarted(GameObject go)
        {
            if (LanNetworkManager.Instance == null || go == null) return;
            // After ForceStop, ignore late residual restarts (5.2).
            if (DWMPHorde.Audio.ItemMovingSoundHelper.IsScrapeSuppressed(go.name))
                return;

            Item item = go.GetComponent<Item>();
            if (item == null) return;

            // Remote ownership on this peer (host applying client push): MOS only.
            ItemSounds sounds = item.GetComponent<ItemSounds>();
            if (sounds != null)
                DWMPHorde.Audio.MovingObjectSoundService.NoteMoving(item.gameObject, item.gameObject.name, sounds);
            // Do not broadcast body-push audio; PhysicsState NoteMoving covers observers.
            // dual start was the observer double-scrape.
        }

        /// <summary>Bridge called from WorldPhysicsSyncService when a body-pushed
        /// object goes quiet. Soft-stops MOS on this peer only.
        /// Do not broadcast a PlayerAudio stop. It can interrupt the pushing
        /// client's native ItemSounds; observers use the physics path.
        /// already fade via PhysicsState quiet / DragSync STOP.</summary>
        internal void NotifyBodyPushStopped(string objectName)
        {
            if (LanNetworkManager.Instance == null) return;
            if (string.IsNullOrEmpty(objectName)) return;

            DWMPHorde.Audio.ItemMovingSoundHelper.SoftStopNetwork(objectName);
            Sync.WorldPhysicsSyncService.TryStopBodyPushSound(objectName);
        }


        /// <summary>Spawn an item on this peer so the remote drag can be reflected.</summary>
        internal Item SpawnDraggedItem(DragSyncMessage msg)
        {
            if (string.IsNullOrEmpty(msg.ItemType))
            {
                ModRuntime.LegacyInfo("[DragSync] no ItemType to spawn \"" + msg.ObjectName + "\"");
                return null;
            }

            if (Singleton<ItemsDatabase>.Instance == null)
            {
                ModRuntime.Log?.LogWarning("[DragSync] ItemsDatabase not available");
                return null;
            }

            if (!Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.LegacyInfo("[DragSync] ItemsDatabase has no item type \"" + msg.ItemType + "\"");
                return null;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.LegacyInfo("[DragSync] no prefab for \"" + msg.ItemType + "\"");
                return null;
            }

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
            {
                ModRuntime.LegacyInfo("[DragSync] prefab is not a GameObject for \"" + msg.ItemType + "\"");
                return null;
            }

            Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Quaternion rot = Quaternion.Euler(msg.RotX, msg.RotY, msg.RotZ);
            GameObject go = Core.AddPrefab(prefab, pos, rot, null);
            if (go == null)
            {
                // Fallback: direct instantiate if Core.AddPrefab fails
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);
            }

            if (go == null) return null;

            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.position = pos;
                rb.rotation = rot;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }

            return go.GetComponent<Item>();
        }

        /// <summary>Destroy a proxy-spawned copy when the remote player stops dragging.</summary>
        internal void CleanupSpawnedDragProxy(string objectName)
        {
            if (_spawnedDragProxyItems.Count == 0) return;

            List<int> toRemove = new List<int>();
            foreach (int id in _spawnedDragProxyItems)
            {
                // Find by name as a safety check
                GameObject go = null;
                foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
                {
                    if (candidate.GetInstanceID() == id)
                    {
                        go = candidate.gameObject;
                        break;
                    }
                }

                if (go != null)
                {
                    // Match by name if provided
                    if (!string.IsNullOrEmpty(objectName) && !go.name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    ModRuntime.LegacyInfo("[DragSync] destroying proxy-spawned " + go.name);
                    UnityEngine.Object.Destroy(go);
                }
                toRemove.Add(id);
            }

            foreach (int id in toRemove)
                _spawnedDragProxyItems.Remove(id);
        }

        /// <summary>Remove all remote-drag tracking for items matching the given name.
        /// Called when a DragSync with IsDragging=false arrives, so PhysicsState
        /// resumes tracking the item.</summary>
        internal void RemoveRemoteDragIds(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;

            // Clean the name-based set (cross-peer check)
            _net._remoteDragItemNames.Remove(objectName);

            if (_net._remoteDragItemIds.Count == 0) return;

            // Clean InstanceID-based set (local PhysicsState skip)
            List<int> toRemove = new List<int>();
            foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
            {
                if (candidate == null) continue;
                if (!candidate.gameObject.name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                int id = candidate.GetInstanceID();
                if (_net._remoteDragItemIds.Contains(id))
                    toRemove.Add(id);
            }
            foreach (int id in toRemove)
                _net._remoteDragItemIds.Remove(id);
        }

        /// <summary>Release isKinematic on items matching the given name.
        /// Called when a remote drag ends so local physics can affect them again.</summary>
        internal void ReleaseRemoteDragKinematic(string objectName)
        {
            if (string.IsNullOrEmpty(objectName) || ModRuntime.Network == null || ModRuntime.Network.Role == NetworkRole.Host)
                return;
            foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
            {
                if (candidate == null) continue;
                if (!candidate.gameObject.name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                Rigidbody rb = candidate.GetComponent<Rigidbody>();
                if (rb != null && rb.isKinematic)
                    rb.isKinematic = false;
                return; // only one item per name needs releasing
            }
        }

        /// <summary>Find a draggable Item on this peer by name and proximity.</summary>
        internal Item FindDraggedItemLocally(string name, Vector3 nearPos)
        {
            // Quick check: if the local player has a reference to a dragged item
            // with this name, return it immediately.  Uses itemBeingDragged (not
            // Player.Instance.dragging) because:
            //   1. startDragging may set beingDragged=true before dragging is set
            //   2. force-stop sets dragging=false but leaves itemBeingDragged set,
            //      so subsequent DragSync messages find the item quickly without
            //      falling to OverlapSphere (which skips beingDragged) or the
            //      expensive FindObjectsOfType<Item>() global scan.
            if (Player.Instance != null && Player.Instance.itemBeingDragged != null &&
                !string.IsNullOrEmpty(name) &&
                Player.Instance.itemBeingDragged.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[DragSync] early-return hit for " + name + " (itemBeingDragged)");
                return Player.Instance.itemBeingDragged;
            }

            // Strategy 1: overlap sphere near the reported position (radius 6u
            // to cover the gap between the sent position and the local copy).
            Collider[] nearby = Physics.OverlapSphere(nearPos, 6f);
            Item best = null;
            float bestDist = 6f;
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null || nearby[i].isTrigger) continue;
                Rigidbody rb = nearby[i].attachedRigidbody;
                if (rb == null) continue;
                Item item = rb.GetComponent<Item>();
                if (item == null || !item.draggable) continue;
                if (item.beingDragged) continue; // skip locally-dragged items
                if (!string.IsNullOrEmpty(name) && !item.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                float d = Vector3.Distance(rb.position, nearPos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = item;
                }
            }
            if (best != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[DragSync] found " + best.gameObject.name + " near pos (" + bestDist.ToString("F1") + " u)");
                return best;
            }

            // Strategy 2: global scan by name; catches objects on unloaded
            // world-grid chunks or far from the reported position.
            // We do NOT skip locally-dragged items here; HandleDragSync
            // handles that check after finding the item.
            if (!string.IsNullOrEmpty(name))
            {
                Item bestGlobal = null;
                float bestGlobalDist = float.MaxValue;
                foreach (Item candidate in UnityEngine.Object.FindObjectsOfType<Item>())
                {
                    if (candidate == null || !candidate.draggable) continue;
                    if (candidate.gameObject.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        float d = Vector3.Distance(candidate.transform.position, nearPos);
                        if (d < bestGlobalDist)
                        {
                            bestGlobalDist = d;
                            bestGlobal = candidate;
                        }
                    }
                }
                if (bestGlobal != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[DragSync] found " + bestGlobal.gameObject.name + " via global scan (" + bestGlobalDist.ToString("F1") + " u)");
                    return bestGlobal;
                }
            }

            return null;
        }
    }
}
