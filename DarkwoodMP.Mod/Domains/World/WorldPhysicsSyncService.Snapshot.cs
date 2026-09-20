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
                // are common in hideouts.
                string trackingKey = rootName + "_" + rootId;
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
                    && (net._dragClaims.ContainsKey(rootName)
                        || net._remoteDragItemNames.Contains(rootName)))
                    continue;
                if (itemComp != null && (itemComp.beingDragged
                    || (Player.Instance != null && Player.Instance.dragging
                        && Player.Instance.itemBeingDragged == itemComp)))
                    continue;

                if (net != null && net._remoteDragItemIds.Contains(rootId))
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

            var net = ModRuntime.Network as LanNetworkManager;

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
            List<int> staleSound = null;
            foreach (var kv in _bodyPushSoundTimer)
            {
                if (nowS < kv.Value) continue;
                if (staleSound == null) staleSound = new List<int>();
                staleSound.Add(kv.Key);
            }
            if (staleSound != null)
            {
                foreach (int id in staleSound)
                {
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
                        ModRuntime.LegacyInfo("[SND] body-push stop " + oName);
                    }

                    _bodyPushSoundTimer.Remove(id);
                    _pushSoundAO.Remove(id);
                    _pushSoundSource.Remove(id);
                    _lastPushSoundTime.Remove(id);
                    _pushStationaryCount.Remove(id);
                    if (_pushGidToName.TryGetValue(id, out var __sn)) _pushNameToGid.Remove(__sn);
                    _pushGidToName.Remove(id);
                }
            }

            // Release client-kinematic objects whose timeout has expired (no recent
            // client PhysicsState update for that object).  This lets host physics
            // resume control when the client stops pushing the object.
            float now_ = Time.time;
            List<int> expiredKinematic = null;
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
                    if (expiredKinematic == null) expiredKinematic = new List<int>();
                    expiredKinematic.Add(kv.Key);
                }
            }
            if (expiredKinematic != null)
                foreach (int id in expiredKinematic)
                {
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
                List<string> stale = null;
                foreach (var kv in _lastClientUpdateTime)
                {
                    if (now2 - kv.Value > 2f)
                    {
                        if (stale == null) stale = new List<string>();
                        stale.Add(kv.Key);
                    }
                }
                if (stale != null)
                    foreach (var k in stale)
                        _lastClientUpdateTime.Remove(k);

                // Purge stale _clientKinematicGate entries (entries > 3s old)
                // This cleans up the re-entry guard after the client's 2.5s
                // PhysicsState grace period has elapsed.
                List<int> staleGate = null;
                foreach (var kv in _clientKinematicGate)
                {
                    if (now2 - kv.Value > 3f)
                    {
                        if (staleGate == null) staleGate = new List<int>();
                        staleGate.Add(kv.Key);
                    }
                }
                if (staleGate != null)
                    foreach (int gid in staleGate)
                        _clientKinematicGate.Remove(gid);

                // Also purge stale entries from _lastPos / _lastMoveTime (entries > 5s idle)
                List<string> stalePos = null;
                foreach (var kv in _lastMoveTime)
                {
                    if (now2 - kv.Value > 5f)
                    {
                        if (stalePos == null) stalePos = new List<string>();
                        stalePos.Add(kv.Key);
                    }
                }
                if (stalePos != null)
                {
                    foreach (var k in stalePos)
                    {
                        _lastMoveTime.Remove(k);
                        _lastPos.Remove(k);
                    }
                }

                // Purge Vector3-keyed state dicts (doors, traps, generators).
                // These track "last known state" within scan range; clearing them
                // Periodic cleanup is safe because the next scan redetects objects.
                // and re-populate. Prevents unbounded growth when objects are
                // destroyed or go out of range permanently.
                _lastDoorOpen.Clear();
                _lastTrapTriggered.Clear();
                _lastGeneratorOn.Clear();
                _lastGeneratorFuel.Clear();
            }

            if (_objects.Count == 0 && _doors.Count == 0 && _traps.Count == 0 && _generators.Count == 0)
                return false;

            msg = new PhysicsStateMessage
            {
                Sequence = ++_nextSnapshotSequence,
                Reliable = false,
                Objects = _objects.ToArray(),
                Doors = _doors.ToArray(),
                Traps = _traps.ToArray(),
                Generators = _generators.ToArray()
            };
            return true;
        }

        private static List<Vector3> GetAllProxyPositions()
        {
            var positions = new List<Vector3>();
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                {
                    if (proxy != null)
                        positions.Add(proxy.transform.position);
                }
            }
            return positions;
        }
        /// <summary>
        /// Scans tracked doors near the host player (or near any remote proxy)
        private static void SyncDoors()
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            ListTracker<Door>.Cleanup();

            Player local = Player.Instance;
            if (local == null) return;
            Vector3 center = local.transform.position;

            // Also scan near ALL remote proxies so doors near any client player
            // are detected even if the host player is far away.
            List<Vector3> allProxyPositions = GetAllProxyPositions();

            IList<Door> allDoors = ListTracker<Door>.GetAll();
            for (int i = 0; i < allDoors.Count; i++)
            {
                Door door = allDoors[i];
                if (door == null) continue;

                float distToHost = Vector3.Distance(door.transform.position, center);
                bool nearAnyProxy = false;
                foreach (Vector3 pp in allProxyPositions)
                {
                    if (Vector3.Distance(door.transform.position, pp) <= _scanRadius)
                    {
                        nearAnyProxy = true;
                        break;
                    }
                }
                if (distToHost > _scanRadius && !nearAnyProxy) continue;

                Vector3 dp = door.transform.position;
                Vector3 key = new Vector3((float)Math.Round(dp.x, 1), (float)Math.Round(dp.y, 1), (float)Math.Round(dp.z, 1));

                bool opened = TraverseHack.ReadDoorOpened(door);

                float bodyRotY = 0f;
                Vector3 angVel = Vector3.zero;
                if (door.body != null)
                {
                    bodyRotY = door.body.eulerAngles.y;
                    Rigidbody rb = door.body.GetComponent<Rigidbody>();
                    if (rb != null) angVel = rb.angularVelocity;
                }

                bool stateChanged = !_lastDoorOpen.TryGetValue(key, out bool wasOpened) || wasOpened != opened;
                bool isMoving = opened && angVel.sqrMagnitude > 0.01f;

                if (stateChanged || isMoving)
                {
                    _lastDoorOpen[key] = opened;

                    if (_doors.Count < 64)
                    {
                        _doors.Add(new DoorState
                        {
                            PosX = key.x,
                            PosY = key.y,
                            PosZ = key.z,
                            Opened = opened,
                            BodyRotY = bodyRotY,
                            AngVelX = angVel.x,
                            AngVelY = angVel.y,
                            AngVelZ = angVel.z
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Scans previously detected traps and records any triggered state changes.
        /// </summary>
        private static void SyncTraps()
        {
            // Remove dead entries
            List<int> dead = null;
            foreach (var kv in _knownTraps)
            {
                if (kv.Value == null)
                {
                    if (dead == null) dead = new List<int>();
                    dead.Add(kv.Key);
                }
            }
            if (dead != null)
                foreach (int id in dead)
                    _knownTraps.Remove(id);

            foreach (GameObject go in _knownTraps.Values)
            {
                if (go == null) continue;
                Vector3 pos = go.transform.position;
                Vector3 key = new Vector3((float)Math.Round(pos.x, 1), (float)Math.Round(pos.y, 1), (float)Math.Round(pos.z, 1));
                bool triggered = ReadTrapTriggered(go);
                bool changed = !_lastTrapTriggered.TryGetValue(key, out bool was) || was != triggered;
                if (changed)
                {
                    _lastTrapTriggered[key] = triggered;
                    if (_traps.Count < 32)
                    {
                        int trapId = TrapNetworkId.GetOrMintHost(go);
                        short occupant = ResolveTrapOccupant(trapId, key);
                        _traps.Add(new TrapState
                        {
                            PosX = key.x, PosY = key.y, PosZ = key.z,
                            Triggered = triggered,
                            TrapNetId = trapId,
                            OccupantPlayerId = occupant
                        });
                    }
                }
            }
        }

        /// <summary>Who is currently locked to this trap (local + remotes).</summary>
        internal static short ResolveTrapOccupant(int trapNetId, Vector3 trapPos)
        {
            if (trapNetId <= 0) return 0;
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null) return 0;

            Player local = Player.Instance;
            if (local != null && local.inBearTrap)
            {
                int localTrap = TrapNetworkId.ResolveOccupyingTrapId(local.transform.position,
                    hostMint: net.Role == NetworkRole.Host);
                if (localTrap == trapNetId)
                    return (short)net.LocalPlayerId;
            }

            foreach (var kv in net.EnumerateRemoteTrapOccupancy())
            {
                if (kv.Value == trapNetId)
                    return (short)kv.Key;
            }

            return 0;
        }

        /// <summary>
        /// Scans tracked generators near the host player and records any on/off or fuel changes.
        /// </summary>
        private static void SyncGenerators()
        {
            if (ModRuntime.Network == null || ModRuntime.Network.Role != NetworkRole.Host)
                return;

            Player local = Player.Instance;
            if (local == null) return;
            Vector3 center = local.transform.position;

            // Also scan for generators near ALL remote proxies so they are
            // synced even when the host player is far from any of them.
            List<Vector3> allProxyPositions = GetAllProxyPositions();

            IList<Generator> allGens = ListTracker<Generator>.GetAll();
            for (int i = 0; i < allGens.Count; i++)
            {
                Generator gen = allGens[i];
                if (gen == null) continue;

                float distToHost = Vector3.Distance(gen.transform.position, center);
                bool nearAnyProxy = false;
                foreach (Vector3 pp in allProxyPositions)
                {
                    if (Vector3.Distance(gen.transform.position, pp) <= _scanRadius)
                    {
                        nearAnyProxy = true;
                        break;
                    }
                }
                if (distToHost > _scanRadius && !nearAnyProxy) continue;

                Vector3 dp = gen.transform.position;
                Vector3 key = new Vector3((float)Math.Round(dp.x, 1), (float)Math.Round(dp.y, 1), (float)Math.Round(dp.z, 1));
                bool isOn = gen.isOn;
                float fuel = gen.fuel;

                bool onChanged = !_lastGeneratorOn.TryGetValue(key, out bool was) || was != isOn;
                bool fuelChanged = false;
                if (!onChanged)
                {
                    if (!_lastGeneratorFuel.TryGetValue(key, out float lastFuel))
                        fuelChanged = true;
                    else if (Mathf.Abs(fuel - lastFuel) > 10f)
                        fuelChanged = true;
                }

                if (onChanged || fuelChanged)
                {
                    _lastGeneratorOn[key] = isOn;
                    _lastGeneratorFuel[key] = fuel;

                    if (_generators.Count < 8)
                    {
                        string itemType = "";
                        Item itemComp = gen.GetComponent<Item>();
                        if (itemComp != null && itemComp.invItem != null)
                            itemType = itemComp.invItem.type;

                        _generators.Add(new GeneratorState
                        {
                            PosX = key.x,
                            PosY = key.y,
                            PosZ = key.z,
                            IsOn = isOn,
                            Fuel = fuel,
                            LowPower = gen.lowPower,
                            ItemType = itemType
                        });
                    }
                }
            }
        }

        /// <summary>
        /// Examines a trigger collider to determine if it belongs to a trap
        /// and caches the result so the scan-loop can skip it on subsequent frames.
        /// </summary>
        private static void DetectTrap(Collider col)
        {
            if (col == null) return;
            GameObject root = col.gameObject;
            Rigidbody rb = col.attachedRigidbody;
            if (rb != null) root = rb.gameObject;
            if (root == null) return;

            int id = root.GetInstanceID();
            if (_knownTraps.ContainsKey(id)) return;

            // Already classified
            if (_trapResultCache.TryGetValue(id, out bool knownIsTrap))
            {
                if (knownIsTrap && !_knownTraps.ContainsKey(id))
                    _knownTraps[id] = root;
                return;
            }

            // Quick name check
            string name = root.name.ToLowerInvariant();
            if (!name.Contains("trap") && !name.Contains("bear") && !name.Contains("snap") && !name.Contains("animal") && !name.Contains("mushroom") && !name.Contains("chain") && !name.Contains("glass"))
            {
                _trapResultCache[id] = false;
                return;
            }

            // Verify by checking for a "triggered"/"snapped"/"sprung" bool field
            if (HasTrapField(root))
            {
                _trapResultCache[id] = true;
                _knownTraps[id] = root;
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.Role == NetworkRole.Host)
                    TrapNetworkId.GetOrMintHost(root);
                else
                    TrapNetworkId.RegisterKnown(root);
            }
            else
            {
                _trapResultCache[id] = false;
            }
        }

        /// <summary>Returns true if the GameObject has a component with a trap-related boolean field.</summary>
        private static bool HasTrapField(GameObject go)
        {
            Component[] comps = go.GetComponents<Component>();
            foreach (Component comp in comps)
            {
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                bool val;
                if (TryReadBool(t, "triggered", out val)) return true;
                if (TryReadBool(t, "snapped", out val)) return true;
                if (TryReadBool(t, "sprung", out val)) return true;
                if (TryReadBool(t, "isTriggered", out val)) return true;
            }
            return false;
        }

        /// <summary>Reads the triggered/snapped/sprung/isTriggered field from a trap GameObject.</summary>
        internal static bool ReadTrapTriggered(GameObject go)
        {
            Component[] allComponents = go.GetComponents<Component>();
            foreach (Component comp in allComponents)
            {
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                bool val;
                if (TryReadBool(t, "triggered", out val)) return val;
                if (TryReadBool(t, "snapped", out val)) return val;
                if (TryReadBool(t, "sprung", out val)) return val;
                if (TryReadBool(t, "isTriggered", out val)) return val;
            }
            return false;
        }

        /// <summary>Tries to read a boolean field via Harmony Traverse without throwing.</summary>
        private static bool TryReadBool(Traverse t, string field, out bool val)
        {
            val = false;
            try
            {
                var f = t.Field(field);
                if (f.FieldExists())
                {
                    val = f.GetValue<bool>();
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.Log?.LogWarning($"[TraverseFieldRead] failed to read {field}: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Applies a received <see cref="PhysicsStateMessage"/> to the local world:
        /// positions objects (with interpolation targets), opens/closes doors,
        /// triggers traps, and syncs generators. Skips the local player and remote proxies.
        /// </summary>
        /// <param name="state">The snapshot to apply.</param>
        /// <param name="fromPeer">Identifier of the sender, used for logging.</param>
    }
}
