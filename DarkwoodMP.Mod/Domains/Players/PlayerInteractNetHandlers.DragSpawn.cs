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
                foreach (Item candidate in WorldQueryHelper.GetCachedSceneComponents<Item>())
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

        /// <summary>
        /// Peer disconnect: drop that player's drag claims locally and (host) fan out
        /// reliable DragSync STOP so N-peer observers do not keep a stuck claim.
        /// </summary>
        /// <param name="broadcastStop">Host should broadcast; clients only clear local maps.</param>
        internal void ReleaseDragClaimsForDisconnectedPlayer(int playerId, bool broadcastStop)
        {
            if (playerId <= 0) return;

            var toRemove = new List<string>();
            foreach (var kv in DragClaims)
            {
                if (kv.Value == playerId)
                    toRemove.Add(kv.Key);
            }
            if (toRemove.Count == 0) return;

            for (int i = 0; i < toRemove.Count; i++)
            {
                string key = toRemove[i];
                DragClaims.Remove(key);
                LastDragSyncPos.Remove(key);
                DragEndedAt[key] = Time.unscaledTime;
                ReleaseRemoteDragKinematic(key);
                RemoveRemoteDragIds(key);
                DWMPHorde.Audio.ItemMovingSoundHelper.ForceStopByName(key);
                Sync.WorldPhysicsSyncService.ReleaseClientPushHoldByName(key);

                if (!broadcastStop || !_net.IsConnected)
                    continue;

                var dragMsg = new DragSyncMessage
                {
                    IsDragging = false,
                    ObjectName = key,
                    ClaimedByPlayerId = playerId
                };
                _net.BroadcastHot(NetMessageType.DragSync, w => dragMsg.Serialize(w),
                    DeliveryMethod.ReliableOrdered);
                if (_net.Role == NetworkRole.Host)
                    NotifyBodyPushStopped(key);
            }

            ModLog.Event(LogCat.Network,
                "Released " + toRemove.Count + " drag claim(s) for disconnected p" + playerId
                + (broadcastStop ? " (broadcast STOP)" : " (local only)"));
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
            foreach (Item candidate in WorldQueryHelper.GetCachedSceneComponents<Item>())
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
            foreach (Item candidate in WorldQueryHelper.GetCachedSceneComponents<Item>())
            {
                if (candidate == null) continue;
                if (!candidate.gameObject.name.Equals(objectName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!_net._remoteDragItemIds.Contains(candidate.GetInstanceID()))
                    continue;
                Rigidbody rb = candidate.GetComponent<Rigidbody>();
                if (rb != null && rb.isKinematic)
                    rb.isKinematic = false;
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
            int nearbyN = Physics.OverlapSphereNonAlloc(nearPos, 6f, WorldQueryHelper.SharedOverlapBuf);
            Item best = null;
            float bestDist = 6f;
            for (int i = 0; i < nearbyN; i++)
            {
                Collider col = WorldQueryHelper.SharedOverlapBuf[i];
                if (col == null || col.isTrigger) continue;
                Rigidbody rb = col.attachedRigidbody;
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
                foreach (Item candidate in WorldQueryHelper.GetCachedSceneComponents<Item>())
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
