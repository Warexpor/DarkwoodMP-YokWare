using System;
using System.Collections.Generic;
using System.IO;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    public static partial class WorldPhysicsSyncService
    {
        private static void ScanPhysicsAround(Vector3 center, LanNetworkManager net)
        {
            if (_objects.Count >= 256)
                return;

            int hitCount = Physics.OverlapSphereNonAlloc(center, _scanRadius, _overlap3D);
            float now = Time.time;
            bool fullResync = (now - _fullResyncTimer) >= FullResyncInterval;

            for (int i = 0; i < hitCount && i < _overlap3D.Length; i++)
            {
                Collider col = _overlap3D[i];
                if (col == null) continue;

                if (col.isTrigger)
                {
                    DetectTrap(col);
                    continue;
                }

                Rigidbody rb = col.attachedRigidbody;
                if (rb == null) continue;

                GameObject rootGo = rb.gameObject;
                if (rootGo == null || rootGo.isStatic) continue;

                int rootId = rootGo.GetInstanceID();
                if (!_scannedObjectIds.Add(rootId))
                    continue; // already processed from another scan center

                string rootName = rootGo.name;
                if (rootName == "Player" || rootName == "PlayerLegs" || rootName == "RemotePlayer") continue;
                if (rootName.IndexOf("DoorSensor", StringComparison.Ordinal) >= 0) continue;
                // Cheap name reject for common non-freebodies (avoids Character GetComponent).
                if (rootName.IndexOf("RemotePlayer", StringComparison.Ordinal) >= 0) continue;

                // Check for motion before touching components; idle free bodies
                // are common in hideouts. Key by InstanceID (no name+id string alloc).
                int trackingKey = rootId;
                Vector3 pos = rootGo.transform.position;

                if (!_lastPos.TryGetValue(trackingKey, out Vector3 last))
                {
                    _lastPos[trackingKey] = pos;
                    // Seed as "already quiet" so first sighting does not force a 10 Hz stream.
                    _lastMoveTime[trackingKey] = now - QuietConfirmWindow - 1f;
                    continue;
                }

                float distSq = Vector3.SqrMagnitude(pos - last);
                bool reallyMoved = distSq >= 0.0009f;
                _lastMoveTime.TryGetValue(trackingKey, out float timeSinceMoved);
                bool inQuietConfirm = !reallyMoved
                    && (now - timeSinceMoved) < QuietConfirmWindow;
                if (!reallyMoved && !inQuietConfirm && !fullResync)
                    continue;

                if (_lastClientUpdateTime.TryGetValue(trackingKey, out float lastClient) && (now - lastClient) < 0.5f)
                    continue;

                // Heavy filters only for objects we are about to serialize.
                if (rootGo.GetComponent<Player>() != null) continue;
                if (rootGo.GetComponent<RemotePlayerProxy>() != null) continue;
                if (rootGo.GetComponent<Character>() != null) continue;
                if (rootGo.GetComponent<DroppedItemIdentifier>() != null) continue;
                if (rootGo.GetComponent<DeathDrop>() != null) continue;
                // In-flight throwables are owned by ThrowableSpawn + local ThrownItem physics.
                // Streaming them as free-bodies kinematic-locks the peer arc → lands at feet.
                if (IsInFlightThrownItem(rootGo)) continue;

                // World lamps / switch lights: LightState owns on/off. PhysicsState
                // kinematic-lock on client makes them walk-blockers (host stays vanilla).
                if (IsSceneFixedLightItem(rootGo)) continue;

                Item itemComp = rootGo.GetComponent<Item>();
                // E-drag uses DragSync. Do not also stream claimed free bodies
                // through PhysicsState, or both paths can start scrape audio.
                // (start/stop thrash) and could echo MOS onto the dragging client.
                if (net != null && !string.IsNullOrEmpty(rootName)
                    && (net.PlayerInteractHandlers.DragClaims.ContainsKey(rootName)
                        || net.PlayerInteractHandlers.RemoteDragItemNames.Contains(rootName)))
                    continue;
                if (itemComp != null && (itemComp.beingDragged
                    || (Player.Instance != null && Player.Instance.dragging
                        && Player.Instance.itemBeingDragged == itemComp)))
                    continue;

                if (net != null && net.PlayerInteractHandlers.RemoteDragItemIds.Contains(rootId))
                    continue;

                if (rootGo.GetComponentInParent<Jumpable>() != null)
                    continue;
                if (rootGo.GetComponentInChildren<ConfigurableJoint>() != null)
                    continue;

                // While dreaming, only free bodies in the dream pocket.
                if (!DreamSyncManager.ShouldSyncPhysicsObject(rootGo.transform))
                    continue;

                Vector3 rot = rootGo.transform.eulerAngles;
                _lastPos[trackingKey] = pos;
                // Only real motion extends the quiet window.
                if (reallyMoved)
                    _lastMoveTime[trackingKey] = now;

                // Host: keep client-owned free-bodies in interp (posDelta for scrape +
                // smooth apply). Clearing them made every client packet look stationary
                // A zero position delta must not arm body-push audio.
                if (net == null || net.Role == NetworkRole.Host)
                {
                    if (!_clientKinematic.ContainsKey(rootId))
                        _objectInterp.Remove(rootId);
                }

                if (_objects.Count >= 256) return;

                string itemType = itemComp != null && itemComp.invItem != null ? itemComp.invItem.type : "";

                _objects.Add(new WorldObjectState
                {
                    Name = rootName,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    RotX = rot.x,
                    RotY = rot.y,
                    RotZ = rot.z,
                    ItemType = itemType
                });
                // Client free-body send: refresh authority so host PhysicsState echo cannot
                // arm MOS while native ItemSounds owns the scrape (round-trip latency gap).
                if (net != null && net.Role == NetworkRole.Client && !string.IsNullOrEmpty(rootName))
                    ItemMovingSoundHelper.NoteClientPhysicsSent(rootName);
            }
        }

        /// <summary>
        /// Scans non-player, non-static physics objects within <see cref="_scanRadius"/> of the local player
        /// (and, on host, of every remote proxy), plus doors, traps, and generators.
        /// Returns false when nothing has changed (avoids sending empty messages).
        /// </summary>
        /// <param name="msg">Output parameter populated with the current world snapshot.</param>
        /// <returns>True if any state was captured; false if everything is idle.</returns>
        public static bool TryBuildWorldSnapshot(out PhysicsStateMessage msg)
        {
            msg = default;
            Player local = Player.Instance;
            if (local == null) return false;

            _objects.Clear();
            _doors.Clear();
            _traps.Clear();
            _generators.Clear();

            var net = ModRuntime.Network;

            // Scan around the local player and, on the host, every remote proxy so
            // free bodies / traps near a far client enter the snapshot (3+ / split map).
            _scanCenters.Clear();
            _scanCenters.Add(local.transform.position);
            if (net != null && net.Role == NetworkRole.Host)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null)
                        _scanCenters.Add(proxy.transform.position);
                }
            }

            _scannedObjectIds.Clear();
            for (int cIdx = 0; cIdx < _scanCenters.Count; cIdx++)
                ScanPhysicsAround(_scanCenters[cIdx], net);

            // Doors, traps, and generators are host-authoritative.
            // Clients still send free physics objects (pushables) and drag uses DragSync.
            bool isHost = net == null || net.Role == NetworkRole.Host;
            if (isHost)
            {
                SyncDoors();
                SyncTraps();
                SyncGenerators();
            }

            // Reset full-resync timer after processing (so the next call checks
            // elapsed time from this point, not from the previous reset).
            if ((Time.time - _fullResyncTimer) >= FullResyncInterval)
                _fullResyncTimer = Time.time;

            // Early scrape-stop once motion updates go quiet (timer not extended).
            // Start and stop once per active session instead of every tick.
            float nowS = Time.time;
            _snapStaleIntKeys.Clear();
            foreach (var kv in _bodyPushSoundTimer)
            {
                if (nowS < kv.Value) continue;
                _snapStaleIntKeys.Add(kv.Key);
            }
            for (int si = 0; si < _snapStaleIntKeys.Count; si++)
            {
                int id = _snapStaleIntKeys[si];
                string oName = null;
                if (_clientKinematic.TryGetValue(id, out var kinData))
                {
                    if (kinData.rb != null)
                    {
                        kinData.rb.velocity = Vector3.zero;
                        kinData.rb.angularVelocity = Vector3.zero;
                    }
                    oName = kinData.objName;
                }
                if (string.IsNullOrEmpty(oName) && _pushGidToName.TryGetValue(id, out var mapped))
                    oName = mapped;

                if (!string.IsNullOrEmpty(oName) && _bodyPushSoundActive.Remove(oName))
                {
                    // NotifyBodyPushStopped force-stops native+MOS on all roles + broadcast.
                    LanNetworkManager.NotifyBodyPushStopped(oName);
                    ModRuntime.LegacyInfo($"[SND] body-push stop {oName}");
                }

                _bodyPushSoundTimer.Remove(id);
                _pushSoundAO.Remove(id);
                _pushSoundSource.Remove(id);
                _lastPushSoundTime.Remove(id);
                _pushStationaryCount.Remove(id);
                if (_pushGidToName.TryGetValue(id, out var __sn)) _pushNameToGid.Remove(__sn);
                _pushGidToName.Remove(id);
            }

            // Release client-kinematic objects whose timeout has expired (no recent
            // client PhysicsState update for that object).  This lets host physics
            // resume control when the client stops pushing the object.
            float now_ = Time.time;
            _snapStaleIntKeys.Clear();
            foreach (var kv in _clientKinematic)
            {
                if (now_ >= kv.Value.releaseTime)
                {
                    var (rBody, _, objName) = kv.Value;
                    if (rBody != null)
                        rBody.isKinematic = false;
                    // Sound may already have stopped via early timer; ensure once.
                    if (!string.IsNullOrEmpty(objName) && _bodyPushSoundActive.Remove(objName))
                        LanNetworkManager.NotifyBodyPushStopped(objName);
                    _clientKinematicGate[kv.Key] = Time.time;
                    _snapStaleIntKeys.Add(kv.Key);
                }
            }
            for (int ei = 0; ei < _snapStaleIntKeys.Count; ei++)
            {
                int id = _snapStaleIntKeys[ei];
                _clientKinematic.Remove(id);
                _bodyPushSoundTimer.Remove(id);
                _pushSoundAO.Remove(id);
                _pushSoundSource.Remove(id);
                _lastPushSoundTime.Remove(id);
                _pushStationaryCount.Remove(id);
                if (_pushGidToName.TryGetValue(id, out var __ekn))
                {
                    _bodyPushSoundActive.Remove(__ekn);
                    _pushNameToGid.Remove(__ekn);
                }
                _pushGidToName.Remove(id);
            }

            // Periodically purge stale client-update timestamps (every ~10s)
            // to prevent unbounded growth of _lastClientUpdateTime.
            if (++_clientUpdateCleanupCounter % 100 == 0)
            {
                float now2 = Time.time;
                _snapStaleIntKeys.Clear();
                foreach (var kv in _lastClientUpdateTime)
                {
                    if (now2 - kv.Value > 2f)
                        _snapStaleIntKeys.Add(kv.Key);
                }
                for (int i = 0; i < _snapStaleIntKeys.Count; i++)
                    _lastClientUpdateTime.Remove(_snapStaleIntKeys[i]);

                // Purge stale _clientKinematicGate entries (entries > 3s old)
                // This cleans up the re-entry guard after the client's 2.5s
                // PhysicsState grace period has elapsed.
                _snapStaleIntKeys.Clear();
                foreach (var kv in _clientKinematicGate)
                {
                    if (now2 - kv.Value > 3f)
                        _snapStaleIntKeys.Add(kv.Key);
                }
                for (int i = 0; i < _snapStaleIntKeys.Count; i++)
                    _clientKinematicGate.Remove(_snapStaleIntKeys[i]);

                // Also purge stale entries from _lastPos / _lastMoveTime (entries > 5s idle)
                _snapStaleIntKeys.Clear();
                foreach (var kv in _lastMoveTime)
                {
                    if (now2 - kv.Value > 5f)
                        _snapStaleIntKeys.Add(kv.Key);
                }
                for (int i = 0; i < _snapStaleIntKeys.Count; i++)
                {
                    int k = _snapStaleIntKeys[i];
                    _lastMoveTime.Remove(k);
                    _lastPos.Remove(k);
                }

                // Vector3-keyed "last sent state" for doors, traps and generators: forget keys
                // that left range or were destroyed, and re-send a few long-unsent ones per pass.
                PruneStateKeys(_doorKeyAge, _lastDoorOpen, null, now2);
                PruneStateKeys(_trapKeyAge, _lastTrapTriggered, null, now2);
                PruneStateKeys(_generatorKeyAge, _lastGeneratorOn, _lastGeneratorFuel, now2);
                PruneTrapResultCache(now2);
            }

            if (_objects.Count == 0 && _doors.Count == 0 && _traps.Count == 0 && _generators.Count == 0)
                return false;

            CopyGrow(_objects, ref _snapObjects, out int oc);
            CopyGrow(_doors, ref _snapDoors, out int dc);
            CopyGrow(_traps, ref _snapTraps, out int tc);
            CopyGrow(_generators, ref _snapGenerators, out int gc);
            msg = new PhysicsStateMessage
            {
                Sequence = ++_nextSnapshotSequence,
                Reliable = false,
                Objects = oc > 0 ? _snapObjects : null,
                Doors = dc > 0 ? _snapDoors : null,
                Traps = tc > 0 ? _snapTraps : null,
                Generators = gc > 0 ? _snapGenerators : null,
                ObjectCount = oc,
                DoorCount = dc,
                TrapCount = tc,
                GeneratorCount = gc
            };
            return true;
        }

        private static WorldObjectState[] _snapObjects = Array.Empty<WorldObjectState>(); // process-scoped: scratch
        private static DoorState[] _snapDoors = Array.Empty<DoorState>(); // process-scoped: scratch
        private static TrapState[] _snapTraps = Array.Empty<TrapState>(); // process-scoped: scratch
        private static GeneratorState[] _snapGenerators = Array.Empty<GeneratorState>(); // process-scoped: scratch
        private static readonly List<Vector3> _proxyScanPositions = new List<Vector3>(8); // process-scoped: scratch
        private static readonly List<int> _snapStaleIntKeys = new List<int>(16); // process-scoped: scratch

        private static void CopyGrow<T>(List<T> src, ref T[] buf, out int count)
        {
            count = src.Count;
            if (count == 0)
                return;
            if (buf.Length < count)
                buf = new T[Math.Max(count, buf.Length == 0 ? count : buf.Length * 2)];
            for (int i = 0; i < count; i++)
                buf[i] = src[i];
        }

        /// <summary>Fill reusable proxy positions for door/trap/generator interest scans.</summary>
        private static List<Vector3> FillProxyScanPositions()
        {
            _proxyScanPositions.Clear();
            var net = ModRuntime.Network;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null)
                        _proxyScanPositions.Add(proxy.transform.position);
                }
            }
            return _proxyScanPositions;
        }
    }
}
