using System;
using System.Collections.Generic;
using DWMPHorde.Audio;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using HarmonyLib;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Scans the local world for physics objects, doors, traps, and generators within range,
    /// builds snapshots for the host to broadcast, and applies received snapshots on clients.
    /// Provides per-frame interpolation for remote objects to smooth out network latency.
    /// </summary>
    public static class WorldPhysicsSyncService
    {
        /// <summary>
        /// Set true around Explodes.onActivate() calls initiated by a network message
        /// to prevent ExplosionTriggerPatch from re-broadcasting and creating a loop.
        /// </summary>
        internal static bool _suppressBroadcast;

        private static readonly List<WorldObjectState> _objects = new List<WorldObjectState>();
        private static readonly List<DoorState> _doors = new List<DoorState>();
        private static readonly List<TrapState> _traps = new List<TrapState>();
        private static readonly Collider[] _overlap3D = new Collider[2048];
        private static readonly Dictionary<string, Vector3> _lastPos = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, float> _lastMoveTime = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> _lastClientUpdateTime = new Dictionary<string, float>();
        private static int _clientUpdateCleanupCounter;
        // Tracks rigidbodies made isKinematic on the host due to client PhysicsState
        // updates. Key = InstanceID, value = Rigidbody + time to release.
        private static readonly Dictionary<int, (Rigidbody rb, float releaseTime, string objName)> _clientKinematic = new Dictionary<int, (Rigidbody rb, float releaseTime, string objName)>();
        // Stops body-push scrape shortly after the last real position change.
        // Hold is one PhysicsState tick (0.1s) so decision lag stays under a frame
        // of net physics; remaining audible tail is mostly vanilla Stop(0.5f) fade.
        private const float BodyPushSoundHold = 0.1f;
        private static readonly Dictionary<int, float> _bodyPushSoundTimer = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _lastPushSoundTime = new Dictionary<int, float>();
        // Manually-managed AudioSource for host->client body-push sound.
        // We bypass AudioController for this because its pooled one-shot
        // AudioObjects get destroyed between 10Hz PhysicsState ticks,
        // making continuous playback impossible.  A looping AudioSource
        // gives us full lifecycle control.
        private static readonly Dictionary<int, AudioSource> _pushSoundSource = new Dictionary<int, AudioSource>();
        // Fade-out tracking for body-push AudioSources.  Stores (startVolume, endTime)
        // so UpdateObjectInterpolation can ramp volume to 0 before destroying.
        // Without this, Stop() is instant and the user hears a click.
        private static readonly Dictionary<int, (float startVol, float endTime)> _pushSoundFade = new Dictionary<int, (float, float)>();
        // Consecutive stationary ticks counter for hysteresis. Prevents PD
        // oscillation around the 0.001f threshold from repeatedly canceling
        // and restarting the fade — only triggers after N consecutive ticks.
        private static readonly Dictionary<int, int> _pushStationaryCount = new Dictionary<int, int>();
        private const int StationaryFadeThreshold = 2; // ~0.2s guard at 10Hz
        // Maps object name → InstanceID so the NotifyBodyPushStopped signal can
        // look up the manual AudioSource by object name and start the fade.
        private static readonly Dictionary<string, int> _pushNameToGid = new Dictionary<string, int>();
        // Reverse: GID → object name, for cleanup convenience.
        private static readonly Dictionary<int, string> _pushGidToName = new Dictionary<int, string>();
        // Gates against fromClient re-entry after _clientKinematic release.
        // Prevents stale PhysicsState (client's 2.5s grace period) from
        // re-triggering NotifyBodyPushStarted after NotifyBodyPushStopped.
        // Value = Time.time when the gate was set (at NotifyBodyPushStopped).
        private static readonly Dictionary<int, float> _clientKinematicGate = new Dictionary<int, float>();
        private static readonly Dictionary<int, AudioObject> _pushSoundAO = new Dictionary<int, AudioObject>();
        /// <summary>Object names with an active body-push scrape (start once, stop once).</summary>
        private static readonly HashSet<string> _bodyPushSoundActive =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Position-based debounce for DestroyObjectByPos to prevent harvest dupes.
        private static readonly Dictionary<int, float> _destroyDebounce = new Dictionary<int, float>();
        private const float DestroyDebounceTime = 0.5f;

        // Cooldown tracker for host body-push sound in ApplySnapshot else branch.
        // Prevents AudioController.Play spam at 10Hz — only plays every ~0.3s.
        private static readonly Dictionary<Vector3, bool> _lastDoorOpen = new Dictionary<Vector3, bool>();
        private static readonly Dictionary<Vector3, bool> _lastTrapTriggered = new Dictionary<Vector3, bool>();

        private static int _objApplyLogCounter;
        private static float _objInterpLastLogTime;
        private static float _scanRadius = 40f;
        private static float _fullResyncTimer;
        private static readonly float FullResyncInterval = 3f;
        private static readonly Dictionary<int, GameObject> _knownTraps = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, bool> _trapResultCache = new Dictionary<int, bool>();

        private static readonly List<GeneratorState> _generators = new List<GeneratorState>();
        private static readonly Dictionary<Vector3, bool> _lastGeneratorOn = new Dictionary<Vector3, bool>();
        private static readonly Dictionary<Vector3, float> _lastGeneratorFuel = new Dictionary<Vector3, float>();
        private static readonly List<Vector3> _scanCenters = new List<Vector3>(8);
        private static readonly HashSet<int> _scannedObjectIds = new HashSet<int>();

        private const float InterpFixedDuration = 0.1f;

        // Client-side per-frame object interpolation (smooth movement for physics objects)
        private struct ObjectInterpState
        {
            public GameObject Target;
            public Vector3 PrevPos;
            public float PrevTime;
            public Vector3 TargetPos;
            public float TargetTime;
            public Vector3 PrevRot;
            public Vector3 TargetRot;
        }
        private static readonly Dictionary<int, ObjectInterpState> _objectInterp = new Dictionary<int, ObjectInterpState>();
        private static readonly List<int> _objectInterpDeadKeys = new List<int>();
        private static readonly List<int> _objectInterpKeys = new List<int>();



        /// <summary>
        /// OverlapSphere around one center; adds free rigidbodies + detects traps.
        /// Dedupes by GameObject instance id across multi-center host scans.
        /// </summary>
        private static void ScanPhysicsAround(Vector3 center, LanNetworkManager net)
        {
            if (_objects.Count >= 256)
                return;

            int hitCount = Physics.OverlapSphereNonAlloc(center, _scanRadius, _overlap3D);
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

                bool isPlayer = rootGo.GetComponent<Player>() != null;
                bool isRemoteProxy = rootGo.GetComponent<RemotePlayerProxy>() != null;
                string rootName = rootGo.name;

                if (isPlayer) continue;
                if (isRemoteProxy) continue;
                if (rootName == "Player" || rootName == "PlayerLegs" || rootName == "RemotePlayer") continue;
                if (rootName.Contains("DoorSensor")) continue;

                // Skip AI characters — entity interpolation owns them.
                if (rootGo.GetComponent<Character>() != null) continue;

                if (rootGo.GetComponent<DroppedItemIdentifier>() != null) continue;
                if (rootGo.GetComponent<DeathDrop>() != null) continue;

                Item itemComp = rootGo.GetComponent<Item>();
                if (itemComp != null && itemComp.beingDragged)
                {
                    if (net != null && net._dragClaims.ContainsKey(rootName))
                        continue;
                }

                if (net != null && net._remoteDragItemIds.Contains(rootId))
                    continue;

                if (rootGo.GetComponentInParent<Jumpable>() != null)
                    continue;
                if (rootGo.GetComponentInChildren<ConfigurableJoint>() != null)
                    continue;

                string trackingKey = rootName + "_" + rootId;
                Vector3 pos = rootGo.transform.position;
                Vector3 rot = rootGo.transform.eulerAngles;

                if (!_lastPos.TryGetValue(trackingKey, out Vector3 last))
                {
                    _lastPos[trackingKey] = pos;
                    _lastMoveTime[trackingKey] = Time.time;
                    continue;
                }

                float timeSinceMoved = 0f;
                _lastMoveTime.TryGetValue(trackingKey, out timeSinceMoved);
                bool fullResync = (Time.time - _fullResyncTimer) >= FullResyncInterval;
                float distSq = Vector3.SqrMagnitude(pos - last);
                bool moved = distSq >= 0.0009f || fullResync;
                if (!moved && (Time.time - timeSinceMoved) < 2.5f)
                    moved = true;
                if (!moved)
                    continue;

                if (_lastClientUpdateTime.TryGetValue(trackingKey, out float lastClient) && (Time.time - lastClient) < 0.5f)
                    continue;

                _lastPos[trackingKey] = pos;
                _lastMoveTime[trackingKey] = Time.time;

                if (net == null || net.Role == NetworkRole.Host)
                    _objectInterp.Remove(rootId);

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

            // Scan centers: local player always; on host also every remote proxy so
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

            // P1.3 ownership: doors / traps / generators are host-authoritative only.
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
            // Stop once per active session — never thrash start/stop every tick.
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
                // periodically is safe — the next scan will re-detect all objects
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

            DoorTracker.Cleanup();

            Player local = Player.Instance;
            if (local == null) return;
            Vector3 center = local.transform.position;

            // Also scan near ALL remote proxies so doors near any client player
            // are detected even if the host player is far away.
            List<Vector3> allProxyPositions = GetAllProxyPositions();

            IList<Door> allDoors = DoorTracker.GetAll();
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
                        _traps.Add(new TrapState { PosX = key.x, PosY = key.y, PosZ = key.z, Triggered = triggered });
                }
            }
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

            IList<Generator> allGens = GeneratorTracker.GetAll();
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
        private static bool ReadTrapTriggered(GameObject go)
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
        public static void ApplySnapshot(PhysicsStateMessage state, string fromPeer = "host")
        {
            int objApplied = 0, objSkipped = 0, objFailed = 0;
            if (state.Objects != null)
            {
                foreach (WorldObjectState obj in state.Objects)
                {
                    if (obj.Name == "Player" || obj.Name == "PlayerLegs" || obj.Name == "RemotePlayer" || obj.Name.Contains("DoorSensor"))
                    {
                        objSkipped++;
                        continue;
                    }

                    GameObject go = FindOrSpawnObject(obj);

                    if (go == null)
                    {
                        objFailed++;
                        continue;
                    }

                    Vector3 pos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                    Vector3 rot = new Vector3(obj.RotX, obj.RotY, obj.RotZ);

                    // When a client sends PhysicsState, the host applies it.
                    // For Rigidbody objects: use the existing interpolation system
                    // (smooth between 10 Hz updates) + isKinematic (prevents host
                    // physics from fighting).  The interpolation loop on the host
                    // never releases kinematic, so _clientKinematic handles that.
                    bool fromClient = fromPeer.Equals("client", StringComparison.OrdinalIgnoreCase);
                    if (fromClient)
                    {
                        Vector3 objPos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                        Vector3 rotVec = new Vector3(obj.RotX, obj.RotY, obj.RotZ);

                        // E-drag owns this object via DragSync (30 Hz). PhysicsState must not
                        // also drive kinematic/interp or start body-push scrape — that fought
                        // DragSync and spammed NotifyBodyPushStarted (logs: 53 starts, thrash).
                        var netMgr = ModRuntime.Network as LanNetworkManager;
                        if (netMgr != null && !string.IsNullOrEmpty(obj.Name)
                            && (netMgr._dragClaims.ContainsKey(obj.Name)
                                || netMgr._remoteDragItemNames.Contains(obj.Name)))
                        {
                            objSkipped++;
                            continue;
                        }

                        string ck = obj.Name + "_" + go.GetInstanceID();
                        _lastClientUpdateTime[ck] = Time.time;

                        Rigidbody rb = go.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            int goId = go.GetInstanceID();
                            rb.isKinematic = true;

                            float posDelta = 0f;
                            if (_objectInterp.TryGetValue(goId, out var existingInterp))
                                posDelta = Vector3.Distance(existingInterp.TargetPos, objPos);
                            // 0.001 was too sensitive (float noise + micro jitter = "always moving").
                            bool posChanged = posDelta >= 0.02f;

                            // Always refresh kinematic hold while client reports the object.
                            // Gate only blocks brand-new sessions right after a clean stop.
                            bool gated = _clientKinematicGate.ContainsKey(goId);
                            if (!gated || posChanged)
                            {
                                if (gated && posChanged)
                                    _clientKinematicGate.Remove(goId);
                                _clientKinematic[goId] = (rb, Time.time + 0.5f, obj.Name);
                            }

                            // Scrape: start once on real motion, extend stop deadline while moving.
                            // Never re-NotifyStarted on every tick (was restarting loops constantly).
                            // Stationary packets arm a short stop — do not keep a long hold from
                            // residual micro-slides that barely clear posChanged.
                            if (posChanged && !gated)
                            {
                                if (_bodyPushSoundActive.Add(obj.Name))
                                {
                                    ModRuntime.LegacyInfo("[SND] body-push start " + obj.Name + " d=" + posDelta.ToString("F3"));
                                    LanNetworkManager.NotifyBodyPushStarted(go);
                                }
                                _bodyPushSoundTimer[goId] = Time.time + BodyPushSoundHold;
                                _pushNameToGid[obj.Name] = goId;
                                _pushGidToName[goId] = obj.Name;
                            }
                            else if (!gated && _bodyPushSoundActive.Contains(obj.Name))
                            {
                                // First quiet tick after motion: pull deadline in if residual
                                // earlier motion left a longer timer, or arm one if missing.
                                float quietDeadline = Time.time + BodyPushSoundHold;
                                if (!_bodyPushSoundTimer.TryGetValue(goId, out float existing) || existing > quietDeadline)
                                    _bodyPushSoundTimer[goId] = quietDeadline;
                                _pushNameToGid[obj.Name] = goId;
                                _pushGidToName[goId] = obj.Name;
                            }

                            SetObjectTarget(go, objPos, rotVec);
                        }
                        else
                        {
                            go.transform.position = objPos;
                            go.transform.rotation = Quaternion.Euler(rotVec);
                            _objectInterp.Remove(go.GetInstanceID());
                        }
                    }
                    else
                    {
                        // Body-push scrape: shared loop service (also used by E-drag).
                        if (!string.IsNullOrEmpty(obj.Name))
                        {
                            var __ic = go.GetComponent<Item>();
                            if (__ic != null)
                            {
                                int __gid = go.GetInstanceID();
                                bool __hi = _objectInterp.TryGetValue(__gid, out var __ei);
                                float __pd = __hi
                                    ? Vector3.Distance(__ei.TargetPos, pos)
                                    : Vector3.Distance(go.transform.position, pos);

                                var __isnd = __ic.GetComponent<ItemSounds>();
                                if (__isnd != null)
                                {
                                    if (__pd >= 0.03f)
                                    {
                                        MovingObjectSoundService.NoteMoving(__ic.gameObject, obj.Name, __isnd);
                                        _pushNameToGid[obj.Name] = __gid;
                                        _pushGidToName[__gid] = obj.Name;
                                        _lastPushSoundTime[__gid] = Time.time;
                                        _pushStationaryCount[__gid] = 0;
                                    }
                                    else
                                    {
                                        MovingObjectSoundService.NoteStationary(obj.Name);
                                    }
                                }
                            }
                        }
                        SetObjectTarget(go, pos, rot);
                    }
                    objApplied++;
                }
            }

            // Log summary every 15 applies to avoid spamming
            if ((objApplied > 0 || objFailed > 0) && ++_objApplyLogCounter % 15 == 0 && ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ObjectApply] applied=" + objApplied + " skipped=" + objSkipped + " failed=" + objFailed + " from " + fromPeer);

            int doorApplied = 0, doorFailed = 0, doorSkipped = 0;
            if (state.Doors != null)
            {
                try
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    foreach (DoorState ds in state.Doors)
                    {
                        Vector3 doorPos = new Vector3(ds.PosX, ds.PosY, ds.PosZ);
                        Door door = FindDoorByPos(doorPos);
                        if (door == null)
                        {
                            doorFailed++;
                            continue;
                        }

                        bool currentOpened = TraverseHack.ReadDoorOpened(door);
                        if (currentOpened == ds.Opened)
                        {
                            doorSkipped++;
                            continue;
                        }

                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[DoorApply] " + door.name + " " + (ds.Opened ? "OPEN" : "CLOSE") + " from " + fromPeer);
                        TraverseHack.SetDoorOpened(door, ds.Opened, new Vector3(ds.OpenerPosX, ds.OpenerPosY, ds.OpenerPosZ), ds.OpenForce, ds.BodyRotY, ds.AngVelX, ds.AngVelY, ds.AngVelZ);
                        doorApplied++;
                    }
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
                if (doorApplied > 0 || doorFailed > 0)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[DoorRecv] applied=" + doorApplied + " failed=" + doorFailed + " skipped=" + doorSkipped + " from " + fromPeer);
            }

            int trapApplied = 0, trapSkipped = 0;
            if (state.Traps != null)
            {
                try
                {
                    TraverseHack.ApplyingFromNetwork = true;
                    foreach (TrapState ts in state.Traps)
                    {
                        Vector3 tPos = new Vector3(ts.PosX, ts.PosY, ts.PosZ);
                        GameObject go = FindTrapByPos(tPos);
                        if (go == null)
                        {
                            trapSkipped++;
                            continue;
                        }

                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[TrapApply] " + go.name + " at " + tPos + " triggered=" + ts.Triggered);
                        ApplyTrapState(go, ts.Triggered);
                        trapApplied++;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogError("[TrapApply] Exception: " + ex);
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
                if (trapApplied > 0 || trapSkipped > 0)
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[TrapRecv] applied=" + trapApplied + " skipped=" + trapSkipped + " from " + fromPeer);
            }

            if (state.Generators != null)
            {
                try
                {
                    // Guard turnOn/turnOff/setLowPower patches from re-sending
                    TraverseHack.ApplyingFromNetwork = true;
                    foreach (GeneratorState gs in state.Generators)
                    {
                        Vector3 gPos = new Vector3(gs.PosX, gs.PosY, gs.PosZ);
                        Generator gen = FindGeneratorByPos(gPos);
                        if (gen == null)
                            gen = SpawnGenerator(gs);
                        if (gen == null) continue;

                        ApplyGeneratorState(gen, gs.IsOn, gs.Fuel, gs.LowPower);
                    }
                }
                finally
                {
                    TraverseHack.ApplyingFromNetwork = false;
                }
            }
        }

        /// <summary>Removes an object from the interpolation dictionary so it stops being smoothed.</summary>
        /// <param name="go">The GameObject to remove.</param>
        public static void RemoveObjectFromInterpolation(GameObject go)
        {
            if (go == null) return;
            _objectInterp.Remove(go.GetInstanceID());
        }

        /// <summary>
        /// Finds a world object (mushroom, exp item, etc.) by position and destroys it.
        /// Used when the remote peer reports that they harvested/picked up the object.
        /// First checks if the object name likely matches, then destroys by calling Core.RemovePooledPrefab
        /// or Object.Destroy depending on how the object was spawned.
        /// </summary>
        public static void DestroyObjectByPos(Vector3 pos, string objectName)
        {
            // AudioObject removal requests are ephemeral sound effects, not actual traps
            if (!string.IsNullOrEmpty(objectName) && objectName.ToLowerInvariant().Contains("audioobject"))
                return;

            Collider[] nearby = Physics.OverlapSphere(pos, 1.5f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                GameObject root = nearby[i].gameObject;
                Rigidbody rb = nearby[i].attachedRigidbody;
                if (rb != null) root = rb.gameObject;
                if (root == null) continue;

                string rootName = root.name.ToLowerInvariant();
                if (rootName.Contains("mushroom") || rootName.Contains("exp") || rootName.Contains("bio")
                    || rootName.Contains("trap") || rootName.Contains("bear") || rootName.Contains("snap") || rootName.Contains("animal")
                    || rootName.Contains("barrel") || rootName.Contains("tank") || rootName.Contains("glass") || rootName.Contains("chain")
                    || rootName.Contains("infect")) // infection_splat ground (4.10)
                {
                    // Position-based debounce to prevent harvest dupes from simultaneous clicks
                    int posKey = (int)(pos.x * 10f) ^ ((int)(pos.y * 10f) << 10) ^ ((int)(pos.z * 10f) << 20);
                    float now = Time.time;
                    if (_destroyDebounce.TryGetValue(posKey, out float lastDestroy) && (now - lastDestroy) < DestroyDebounceTime)
                    {
                        if (ModRuntime.VerboseLogging) ModRuntime.LegacyInfo("[ObjectDestroy] debounced duplicate at " + pos);
                        return;
                    }
                    _destroyDebounce[posKey] = now;

                    RemoveObjectFromInterpolation(root);
                    Core.RemovePooledPrefab(root.transform);
                    try { TraverseHack.ApplyingFromNetwork = true; UnityEngine.Object.Destroy(root); }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                    ModRuntime.LegacyInfo("[ObjectDestroy] destroyed \"" + root.name + "\" at " + pos);
                    return;
                }
            }

            // Fallback: search by name (skip AudioObject requests — they are ephemeral, not actual traps)
            if (!string.IsNullOrEmpty(objectName) && !objectName.ToLowerInvariant().Contains("audioobject"))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null)
                {
                    RemoveObjectFromInterpolation(named);
                    Core.RemovePooledPrefab(named.transform);
                    try { TraverseHack.ApplyingFromNetwork = true; UnityEngine.Object.Destroy(named); }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                    ModRuntime.LegacyInfo("[ObjectDestroy] destroyed by name \"" + named.name + "\" at " + pos);
                }
            }
        }

        /// <summary>
        /// Sets a new position/rotation target for an object and resets the interpolation
        /// state so it smoothly moves from its current position to the target over <see cref="InterpFixedDuration"/>.
        /// </summary>
        private static void SetObjectTarget(GameObject go, Vector3 targetPos, Vector3 targetRot)
        {
            int id = go.GetInstanceID();
            float now = Time.time;

            if (_objectInterp.TryGetValue(id, out var state))
            {
                float dur = state.TargetTime - state.PrevTime;
                if (dur > 0.001f)
                {
                    // Catch up the previous state to the current render-time lerp position
                    float t = Mathf.Clamp01((now - state.PrevTime) / dur);
                    state.PrevPos = Vector3.Lerp(state.PrevPos, state.TargetPos, t);
                    Quaternion prevRotQ = Quaternion.Euler(state.PrevRot);
                    Quaternion targetRotQ = Quaternion.Euler(state.TargetRot);
                    state.PrevRot = Quaternion.Slerp(prevRotQ, targetRotQ, t).eulerAngles;
                }
                else
                {
                    state.PrevPos = state.TargetPos;
                    state.PrevRot = state.TargetRot;
                }
            }
            else
            {
                state.Target = go;
                state.PrevPos = go.transform.position;
                state.PrevRot = go.transform.eulerAngles;
            }

            state.Target = go;
            state.TargetPos = targetPos;
            state.TargetRot = targetRot;
            state.PrevTime = now;
            state.TargetTime = now + InterpFixedDuration;
            _objectInterp[id] = state;

            // Lock to host position during active sync — prevents proxy collisions
            // on the client from pushing the object away from the host's position.
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                    rb.isKinematic = true;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Look up a GameObject by name, then by proximity, then by spawning it
        /// from the <see cref="ItemsDatabase"/> if the <see cref="WorldObjectState.ItemType"/>
        /// is known.  Returns null only when every strategy fails.
        /// </summary>
        private static GameObject FindOrSpawnObject(WorldObjectState obj)
        {
            Vector3 targetPos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);

            // Strategy 1: overlap sphere near the reported position (avoids teleporting
            // objects with non-unique names — GameObject.Find would match any instance)
            Collider[] nearby = Physics.OverlapSphere(targetPos, 1.5f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null || nearby[i].attachedRigidbody == null) continue;
                GameObject candidate = nearby[i].attachedRigidbody.gameObject;
                if (candidate.name != obj.Name) continue;
                if (candidate.GetComponent<Character>() != null) continue;
                // Skip objects handled by dedicated sync systems
                if (candidate.GetComponent<DroppedItemIdentifier>() != null) continue;
                if (candidate.GetComponent<DeathDrop>() != null) continue;
                return candidate;
            }

            // Strategy 2: search ALL Rigidbodies by name, pick closest to target.
            // This handles objects pushed far from their reported position (more than 1.5u).
            // GameObject.Find is unreliable when multiple objects share the same name.
            {
                Rigidbody[] allRbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                GameObject best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < allRbs.Length; i++)
                {
                    Rigidbody rb = allRbs[i];
                    if (rb == null) continue;
                    if (rb.name != obj.Name) continue;
                    if (rb.GetComponent<Character>() != null) continue;
                    // Skip objects handled by dedicated sync systems
                    if (rb.GetComponent<DroppedItemIdentifier>() != null) continue;
                    if (rb.GetComponent<DeathDrop>() != null) continue;
                    float d = Vector3.Distance(rb.transform.position, targetPos);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = rb.gameObject;
                    }
                }
                if (best != null)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo("[ObjectApply] found \"" + best.name + "\" via full scan (" + bestDist.ToString("F1") + " u from target)");
                    return best;
                }
            }

            // Strategy 3: spawn from ItemsDatabase (cross-world-chunk support)
            // Skip items handled by dedicated sync systems (dropped items, death bags)
            string nameLower = obj.Name != null ? obj.Name.ToLowerInvariant() : "";
            if (nameLower.Contains("droppeditem") || nameLower.Contains("deathdrop"))
                return null;

            if (string.IsNullOrEmpty(obj.ItemType))
                return null;

            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(obj.ItemType))
                return null;

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(obj.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
                return null;

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
                return null;

            Quaternion rot = Quaternion.Euler(obj.RotX, obj.RotY, obj.RotZ);
            GameObject spawned;
            try
            {
                TraverseHack.ApplyingFromNetwork = true;
                spawned = Core.AddPrefab(prefab, targetPos, rot, null);
                if (spawned == null)
                    spawned = UnityEngine.Object.Instantiate(prefab, targetPos, rot);
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }

            if (spawned != null)
            {
                Rigidbody rb = spawned.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.position = targetPos;
                    rb.rotation = rot;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                ModRuntime.LegacyInfo("[ObjectApply] spawned \"" + obj.Name + "\" type=" + obj.ItemType + " at " + targetPos);
            }

            return spawned;
        }

        /// <summary>Finds a trap GameObject by position using collider overlap, Trigger component, and name-based fallback.</summary>
        internal static GameObject FindTrapByPos(Vector3 pos, string objectName = null)
        {
            // Primary: collider-based search
            Collider[] nearby = Physics.OverlapSphere(pos, 1.5f);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                GameObject root = nearby[i].gameObject;
                Rigidbody rb = nearby[i].attachedRigidbody;
                if (rb != null) root = rb.gameObject;
                if (root == null) continue;
                if (HasTrapField(root))
                    return root;
            }

            // Fallback: search Trigger components by position (Y-tolerant)
            Trigger[] triggers = UnityEngine.Object.FindObjectsOfType<Trigger>();
            Vector3 rounded = new Vector3((float)Math.Round(pos.x, 1), (float)Math.Round(pos.y, 1), (float)Math.Round(pos.z, 1));
            foreach (Trigger t in triggers)
            {
                if (t == null) continue;
                Vector3 tp = t.transform.position;
                float tx = (float)Math.Round(tp.x, 1);
                float tz = (float)Math.Round(tp.z, 1);
                if (tx == rounded.x && tz == rounded.z)
                {
                    float ty = (float)Math.Round(tp.y, 1);
                    if (Mathf.Abs(ty - rounded.y) <= 5f)
                        return t.gameObject;
                }
            }

            // Last resort: wider search by name + position
            Collider[] wide = Physics.OverlapSphere(pos, 5f);
            for (int i = 0; i < wide.Length; i++)
            {
                if (wide[i] == null) continue;
                GameObject root = wide[i].gameObject;
                Rigidbody rb = wide[i].attachedRigidbody;
                if (rb != null) root = rb.gameObject;
                if (root == null) continue;
                Vector3 rp = root.transform.position;
                Vector3 rk = new Vector3((float)Math.Round(rp.x, 1), (float)Math.Round(rp.y, 1), (float)Math.Round(rp.z, 1));
                if (Mathf.Abs(rk.x - rounded.x) <= 0.2f && Mathf.Abs(rk.z - rounded.z) <= 0.2f && Mathf.Abs(rk.y - rounded.y) <= 5f
                    && (root.name.ToLowerInvariant().Contains("mushroom") || root.name.ToLowerInvariant().Contains("trap") || root.name.ToLowerInvariant().Contains("bear")))
                    return root;
            }

            // Final fallback: name-based if provided
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null && HasTrapField(named))
                    return named;
            }

            return null;
        }

        /// <summary>
        /// Applies the triggered/untriggered state to a trap GameObject.
        /// When triggering, plays the sound, spawns the visual prefab, alerts nearby characters,
        /// switches the sprite, and cleans up the Item/Inventory components.
        /// Skips re-triggering if the trap is already in the target state.
        /// </summary>
        /// <param name="go">The trap GameObject.</param>
        /// <param name="triggered">Whether the trap should be set to triggered.</param>
        internal static void ApplyTrapState(GameObject go, bool triggered)
        {
            if (go == null) return;

            bool current = ReadTrapTriggered(go);
            if (current == triggered) return;

            Component[] allComponents = go.GetComponents<Component>();
            foreach (Component comp in allComponents)
            {
                if (comp == null) continue;
                Traverse t = Traverse.Create(comp);
                if (TryWriteBool(t, "triggered", triggered)) break;
                if (TryWriteBool(t, "snapped", triggered)) break;
                if (TryWriteBool(t, "sprung", triggered)) break;
                if (TryWriteBool(t, "isTriggered", triggered)) break;
            }

            if (triggered)
            {
                bool isHarvestable = go.name.ToLowerInvariant().Contains("mushroom");
                Trigger trig = go.GetComponent<Trigger>();

                if (!isHarvestable)
                {
                    // Play activation sound
                    if (trig != null && !string.IsNullOrEmpty(trig.activateSound))
                        AudioController.Play(trig.activateSound, go.transform);

                    // Spawn explosion visual prefab (blood splatter, etc.) at ground level.
                    // Dropped items have Y=-10.4 (below terrain), so raycast to find ground.
                    if (trig != null && trig.prefabToSpawn != null)
                    {
                        Vector3 spawnPos = go.transform.position + new Vector3(0f, 1f, 0f);
                        // Bump underground traps up to ground level
                        if (spawnPos.y < 0f)
                        {
                            RaycastHit hit;
                            if (Physics.Raycast(spawnPos + Vector3.up * 50f, Vector3.down, out hit, 100f))
                                spawnPos.y = hit.point.y + 0.5f;
                        }
                        try
                        {
                            Core.AddPrefab(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f), null);
                        }
                        catch (Exception ex)
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.Log?.LogWarning($"[PhysicsSpawn] AddPrefab failed for {trig?.prefabToSpawn}: {ex.Message}");
                            UnityEngine.Object.Instantiate(trig.prefabToSpawn, spawnPos, Quaternion.Euler(90f, 0f, 0f));
                        }
                    }
                }

                // Alert characters in radius
                if (trig != null && trig.alertRadius > 0f)
                    Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);

                // Visual sprite + name change (matches original game's OnAfterTrigger call via waitFramesAndRun)
                if (trig != null)
                    trig.switchToTriggered();

                // Cancel disarm in progress
                Item item = go.GetComponent<Item>();
                if (item != null)
                    item.onTriggerFire();

                // Destroy Item only if the prefab is configured to remove it
                // (if dontDestroyItemAfterTriggering is true, Item stays for hover/name display)
                if (trig == null || !trig.dontDestroyItemAfterTriggering)
                {
                    if (item != null)
                        UnityEngine.Object.Destroy(item);
                }

                // Destroy Inventory only if configured to remove it
                if (trig == null || !trig.dontRemoveInventoryAfterTriggering)
                {
                    Inventory inv = go.GetComponent<Inventory>();
                    if (inv != null)
                        UnityEngine.Object.Destroy(inv);
                    if (item != null)
                        item.invItem = null;
                }
            }
        }

        /// <summary>Tries to write a boolean field via Harmony Traverse without throwing.</summary>
        private static bool TryWriteBool(Traverse t, string field, bool val)
        {
            try
            {
                var f = t.Field(field);
                if (f.FieldExists())
                {
                    f.SetValue(val);
                    return true;
                }
            }
            catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }
            return false;
        }

        /// <summary>Finds a Door by position — first checks the tracker, then falls back to a scene-wide search.</summary>
        private static Door FindDoorByPos(Vector3 pos)
        {
            Door door = DoorTracker.FindByPosition(pos);
            if (door != null)
                return door;

            // Fallback: search all Door instances — catches doors that were
            // spawned dynamically after the tracker's Awake patch ran, or
            // doors from world-grid chunks the host has loaded.
            Door[] all = UnityEngine.Object.FindObjectsOfType<Door>();
            for (int i = 0; i < all.Length && i < 128; i++)
            {
                Door d = all[i];
                if (d == null) continue;
                if (Vector3.Distance(d.transform.position, pos) < 2f)
                {
                    DoorTracker.Add(d);
                    return d;
                }
            }
            return null;
        }

        /// <summary>
        /// Spawns a generator on-demand from <see cref="GeneratorState.ItemType"/>
        /// when it doesn't exist locally (e.g. remote player turned on a generator
        /// in an unloaded world chunk).
        /// </summary>
        private static Generator SpawnGenerator(GeneratorState gs)
        {
            if (string.IsNullOrEmpty(gs.ItemType))
                return null;

            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(gs.ItemType))
                return null;

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(gs.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
                return null;

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null)
                return null;

            Vector3 pos = new Vector3(gs.PosX, gs.PosY, gs.PosZ);
            Quaternion rot = Quaternion.identity;
            GameObject go = Core.AddPrefab(prefab, pos, rot, null);
            if (go == null)
                go = UnityEngine.Object.Instantiate(prefab, pos, rot);

            if (go == null) return null;

            Generator gen = go.GetComponent<Generator>();
            if (gen != null)
                GeneratorTracker.Add(gen);

            ModRuntime.LegacyInfo("[GeneratorSync] spawned type=" + gs.ItemType + " at " + pos);
            return gen;
        }

        /// <summary>Finds a Generator by position via the tracker.</summary>
        private static Generator FindGeneratorByPos(Vector3 pos)
        {
            Generator gen = GeneratorTracker.FindByPosition(pos);
            if (gen != null)
                return gen;

            // Fallback: search all loaded Generator instances — catches generators
            // that were spawned dynamically after the tracker's Start patch ran.
            Generator[] all = UnityEngine.Object.FindObjectsOfType<Generator>();
            for (int i = 0; i < all.Length && i < 32; i++)
            {
                Generator g = all[i];
                if (g == null) continue;
                if (Vector3.Distance(g.transform.position, pos) < 2f)
                {
                    GeneratorTracker.Add(g);
                    return g;
                }
            }
            return null;
        }

        /// <summary>
        /// Per-frame interpolation driver. Moves each tracked physics object from its previous
        /// position toward its target over a fixed 0.25 s window. Skips objects being dragged
        /// locally and removes stale entries that haven't received a new snapshot recently.
        /// On the host, resets velocity/angular velocity to prevent physics fighting the interpolation.
        /// </summary>
        public static void UpdateObjectInterpolation()
        {
            if (ModRuntime.Network == null)
                return;

            // Periodic door tracker cleanup on both host and client — rescans
            // for any doors whose Awake was missed by the Harmony patch.
            DoorTracker.Cleanup();

            _objectInterpDeadKeys.Clear();
            _objectInterpKeys.Clear();
            _objectInterpKeys.AddRange(_objectInterp.Keys);
            float now = Time.time;

            if (_objectInterp.Count > 0 && now - _objInterpLastLogTime >= 30f)
            {
                _objInterpLastLogTime = now;
                ModRuntime.LegacyInfo("[ObjInterp] active=" + _objectInterp.Count);
            }

            for (int oi = 0; oi < _objectInterpKeys.Count; oi++)
            {
                int key = _objectInterpKeys[oi];
                var s = _objectInterp[key];

                if (s.Target == null)
                {
                    _objectInterpDeadKeys.Add(key);
                    continue;
                }

                // Skip objects being dragged by the local player — local physics
                // (HingeJoint) already drives the correct position. Interpolation
                // would fight the joint and cause jitter.
                Item item = s.Target.GetComponent<Item>();
                if (item != null && item.beingDragged)
                {
                    // Don't remove from interpolation yet; the drag might end
                    // and we want to resume smoothly. Just skip position update.
                    // Release kinematic so the local HingeJoint can drive position.
                    Rigidbody dragRb = s.Target.GetComponent<Rigidbody>();
                    if (dragRb != null && dragRb.isKinematic)
                        dragRb.isKinematic = false;
                    continue;
                }

                // Remove stale entries: no new snapshot received for a while.
                // TargetTime is in the future (PrevTime + InterpFixedDuration), so
                // the effective timeout is InterpFixedDuration + 1.5s since PrevTime.
                if (s.PrevTime > 0 && now - s.PrevTime > (InterpFixedDuration + 1.5f))
                {
                    _objectInterpDeadKeys.Add(key);
                    continue;
                }

                // Fixed-duration interpolation — move from PrevPos to TargetPos
                // smoothly over InterpFixedDuration seconds.
                float duration = s.TargetTime - s.PrevTime; // always InterpFixedDuration
                float elapsed = now - s.PrevTime;
                float t = duration > 0.001f ? Mathf.Clamp01(elapsed / duration) : 1f;

                Vector3 lerpPos = Vector3.Lerp(s.PrevPos, s.TargetPos, t);
                Quaternion prevRotQ = Quaternion.Euler(s.PrevRot);
                Quaternion targetRotQ = Quaternion.Euler(s.TargetRot);
                Quaternion lerpRot = Quaternion.Slerp(prevRotQ, targetRotQ, t);

                Rigidbody rb = s.Target.GetComponent<Rigidbody>();
                if (t < 1f)
                {
                    // During active interpolation: drive position, keep kinematic
                    // so client physics doesn't fight the snapshot correction.
                    if (rb != null)
                    {
                        rb.position = lerpPos;
                        rb.rotation = lerpRot;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    else
                    {
                        s.Target.transform.position = lerpPos;
                        s.Target.transform.rotation = lerpRot;
                    }
                }
                else
                {
                    // Interpolation complete. Release kinematic so the client can
                    // push / interact with the object.  Next snapshot (host correction
                    // or full resync) will re-lock and re-interpolate.
                    if (rb != null && ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                        rb.isKinematic = false;
                    // Non-rigidbody objects still need transform driven.
                    if (rb == null)
                    {
                        s.Target.transform.position = lerpPos;
                        s.Target.transform.rotation = lerpRot;
                    }
                }
            }

            foreach (int key in _objectInterpDeadKeys)
            {
                if (_objectInterp.TryGetValue(key, out var deadState) && deadState.Target != null
                    && ModRuntime.Network != null && ModRuntime.Network.Role != NetworkRole.Host)
                {
                    Rigidbody rb = deadState.Target.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.isKinematic = false;
                    // Stop drag sound when the remote object falls out of sync
                    // (host walked away, object out of scan range, etc.) so
                    // the sound doesn't play forever on the client.
                    LanNetworkManager.NotifyBodyPushStopped(deadState.Target.name);
                }
                _objectInterp.Remove(key);
            }

            // Unified scrape-loop fades + occlusion (drag + body-push).
            MovingObjectSoundService.Tick();

            // If PhysicsState stops reporting motion, decide stop promptly.
            // One physics interval (0.1s) + a small cushion — not a long hang.
            // Remaining tail is vanilla-style 0.5s fade in StopNetwork.
            float __srcCleanupNow = Time.time;
            List<int> __staleSrcKeys = null;
            foreach (var __kv in _lastPushSoundTime)
            {
                if ((__srcCleanupNow - __kv.Value) > 0.12f)
                {
                    if (__staleSrcKeys == null) __staleSrcKeys = new List<int>();
                    __staleSrcKeys.Add(__kv.Key);
                }
            }
            if (__staleSrcKeys != null)
                foreach (int __k in __staleSrcKeys)
                {
                    if (_pushGidToName.TryGetValue(__k, out var __akn))
                    {
                        MovingObjectSoundService.StopNetwork(__akn);
                        _pushNameToGid.Remove(__akn);
                    }
                    _lastPushSoundTime.Remove(__k);
                    _pushStationaryCount.Remove(__k);
                    _pushGidToName.Remove(__k);
                    _pushSoundSource.Remove(__k);
                    _pushSoundFade.Remove(__k);
                }

            // Release path for client-kinematic objects (primary is in
            // TryBuildWorldSnapshot).  This runs every frame in LateUpdate
            // unconditionally, so the sound is guaranteed to stop even when
            // TryBuildWorldSnapshot doesn't fire (e.g. during dreams).
            float nowK = Time.time;
            List<int> staleKin = null;
            foreach (var kv in _clientKinematic)
            {
                if (nowK >= kv.Value.releaseTime)
                {
                    var (rBody, _, oName) = kv.Value;
                    if (rBody != null)
                        rBody.isKinematic = false;
                    if (!string.IsNullOrEmpty(oName) && _bodyPushSoundActive.Remove(oName))
                        LanNetworkManager.NotifyBodyPushStopped(oName);
                    if (staleKin == null) staleKin = new List<int>();
                    staleKin.Add(kv.Key);
                }
            }
            if (staleKin != null)
                foreach (int id in staleKin)
                {
                    _clientKinematic.Remove(id);
                    _bodyPushSoundTimer.Remove(id);
                    _pushSoundAO.Remove(id);
                    _pushSoundSource.Remove(id);
                    _lastPushSoundTime.Remove(id);
                    _pushStationaryCount.Remove(id);
                    if (_pushGidToName.TryGetValue(id, out var __skn))
                    {
                        _bodyPushSoundActive.Remove(__skn);
                        _pushNameToGid.Remove(__skn);
                    }
                    _pushGidToName.Remove(id);
                }
        }

        /// <summary>
        /// Applies a generator's on/off state and fuel level to the local world.
        /// Uses Item.turnOn/turnOff when present so ItemSounds (start + loop / stop)
        /// match the local player path — Generator.turnOn alone never starts audio.
        /// </summary>
        private static void ApplyGeneratorState(Generator gen, bool isOn, float fuel, bool lowPower = false)
        {
            // Fuel first: Generator.turnOn no-ops when fuel <= 0.
            gen.fuel = fuel;

            if (gen.isOn != isOn)
            {
                Item item = gen.GetComponent<Item>();
                if (isOn)
                {
                    if (item != null)
                        item.turnOn(); // Generator.turnOn + ItemSounds.playStart + particles
                    else
                        gen.turnOn();
                }
                else
                {
                    if (item != null)
                        item.turnOff(); // ItemSounds.playStop + Generator.turnOff
                    else
                        gen.turnOff();
                }
            }

            if (gen.lowPower != lowPower)
                gen.setLowPower(lowPower);
        }

        /// <summary>
        /// Applies a received light on/off state change from a remote peer.
        /// Looks up the light Item by position and name, then toggles it if needed.
        /// </summary>
        /// <param name="ls">The light state message.</param>
        /// <param name="fromPeer">Sender identifier for logging.</param>
        public static void ApplyLightState(LightStateMessage ls, string fromPeer)
        {
            Vector3 pos = new Vector3(ls.PosX, ls.PosY, ls.PosZ);
            Item item = FindLightByPos(pos, ls.ItemName);
            if (item == null)
            {
                // Try to spawn the light on-demand from ItemType
                if (!string.IsNullOrEmpty(ls.ItemType))
                {
                    var objState = new WorldObjectState
                    {
                        Name = ls.ItemName,
                        PosX = ls.PosX,
                        PosY = ls.PosY,
                        PosZ = ls.PosZ,
                        ItemType = ls.ItemType
                    };
                    GameObject go = FindOrSpawnObject(objState);
                    if (go != null)
                        item = go.GetComponent<Item>();
                }

                if (item == null)
                {
                    ModRuntime.LegacyInfo("[LightApply] " + ls.ItemName + " not found at " + pos);
                    return;
                }
            }

            ModRuntime.LegacyInfo("[LightApply] " + item.name + " isOn=" + ls.IsOn + " from " + fromPeer);

            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                if (ls.IsOn && !item.isOn)
                    item.turnOn();
                else if (!ls.IsOn && item.isOn)
                    item.turnOff();
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }
        }

        /// <summary>Finds a light Item by position and optional name within a 1.5 unit radius.</summary>
        private static Item FindLightByPos(Vector3 pos, string name)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, 5f);
            Item best = null;
            float bestDist = 5f;
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Item item = nearby[i].GetComponentInParent<Item>();
                if (item == null) continue;
                if (!string.IsNullOrEmpty(name) && !item.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!item.isLight && !item.switchable)
                    continue;
                float d = Vector3.Distance(item.transform.position, pos);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = item;
                }
            }
            if (best != null)
                return best;

            // Fallback: search all items by name when position-based lookup fails.
            // Some lights may be at different positions between host and client
            // due to world grid loading differences.
            if (!string.IsNullOrEmpty(name))
            {
                Item[] all = UnityEngine.Object.FindObjectsOfType<Item>();
                for (int i = 0; i < all.Length; i++)
                {
                    Item item = all[i];
                    if (item == null) continue;
                    if (!item.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!item.isLight && !item.switchable)
                        continue;
                    return item;
                }
            }
            return null;
        }

        /// <summary>Called from HandlePlayerAudio's IsStopSignal handler when the
        /// host broadcasts NotifyBodyPushStopped. Clears tracking tables; actual
        /// audio stop is handled by ItemMovingSoundHelper.ForceStopByName.</summary>
        public static void TryStopBodyPushSound(string objectName)
        {
            if (string.IsNullOrEmpty(objectName)) return;
            _bodyPushSoundActive.Remove(objectName);
            if (_pushNameToGid.TryGetValue(objectName, out int __gid))
            {
                _lastPushSoundTime.Remove(__gid);
                _pushStationaryCount.Remove(__gid);
                _pushSoundFade.Remove(__gid);
                _pushSoundSource.Remove(__gid);
                _bodyPushSoundTimer.Remove(__gid);
                _pushGidToName.Remove(__gid);
                _pushNameToGid.Remove(objectName);
            }
        }

        /// <summary>Resets all cached state, tracked objects, and interpolation tables (used on scene change or disconnect).</summary>
        public static void Reset()
        {
            _lastPos.Clear();
            _lastMoveTime.Clear();
            _lastDoorOpen.Clear();
            _lastTrapTriggered.Clear();
            _knownTraps.Clear();
            _trapResultCache.Clear();
            _lastGeneratorOn.Clear();
            _scanCenters.Clear();
            _scannedObjectIds.Clear();
            _objectInterp.Clear();
            // Release all client-kinematic rigidbodies on reset
            foreach (var kv in _clientKinematic)
            {
                var (rBody, _, objName) = kv.Value;
                if (rBody != null)
                    rBody.isKinematic = false;
                LanNetworkManager.NotifyBodyPushStopped(objName);
            }
            _clientKinematic.Clear();
            _bodyPushSoundTimer.Clear();
            _bodyPushSoundActive.Clear();
            _pushSoundAO.Clear();
            _pushSoundSource.Clear();
            _pushSoundFade.Clear();
            _lastPushSoundTime.Clear();
            _pushStationaryCount.Clear();
            _pushNameToGid.Clear();
            _pushGidToName.Clear();
            _clientKinematicGate.Clear();
            _lastGeneratorFuel.Clear();
            _lastClientUpdateTime.Clear();
            _destroyDebounce.Clear();
            MovingObjectSoundService.Reset();
            DoorTracker.Clear();
            GeneratorTracker.Clear();
            CharacterTracker.Clear();
        }

        /// <summary>
        /// Strips combat from a thrown projectile so it can still fly and play land/explosion
        /// FX while the host alone applies damage / status / knockback.
        /// Used for client remote copies and the client's own throw after network spawn.
        /// </summary>
        public static void MuteThrownCombat(GameObject go)
        {
            if (go == null) return;

            ThrownItem ti = go.GetComponent<ThrownItem>();
            if (ti != null)
                ti.damage = 0;

            Explodes expl = go.GetComponent<Explodes>();
            if (expl != null)
            {
                expl.damage = 0f;
                expl.affectsPlayer = false;
                expl.force = 0f;
                expl.hasEffect = false;
            }
        }

        /// <summary>
        /// Ensures thrown flares have an active Light2D on the ground projectile.
        /// Prefab usually carries Flare+Light2D; enable and register if present, else add a fallback.
        /// </summary>
        private static void EnsureThrownFlareLight(GameObject go, string itemType)
        {
            if (go == null || string.IsNullOrEmpty(itemType)) return;
            if (itemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            Light2D light = go.GetComponentInChildren<Light2D>(true);
            if (light == null)
            {
                Flare flare = go.GetComponentInChildren<Flare>(true);
                if (flare != null && flare.light2D != null)
                    light = flare.light2D;
            }

            if (light == null)
            {
                var lightGo = new GameObject("ThrownFlareLight");
                lightGo.transform.SetParent(go.transform, false);
                lightGo.transform.localPosition = Vector3.zero;
                light = lightGo.AddComponent<Light2D>();
                if (light.LightMaterial == null)
                    light.LightMaterial = Resources.Load("RadialLight") as Material;
                light.LightRadius = 650f;
                light.LightIntensity = 1f;
                light.LightColor = new Color(1f, 0.5f, 0.1f);
                ModRuntime.LegacyInfo("[ThrowableSpawn] added fallback Light2D for " + itemType);
            }

            if (!light.gameObject.activeSelf)
                light.gameObject.SetActive(true);
            light.lightsPlayer = true;
            light.updateGraph = true;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && !ctrl.logicLights.Contains(light))
                ctrl.logicLights.Add(light);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ThrowableSpawn] flare light ok " + itemType
                    + " radius=" + light.LightRadius
                    + " intensity=" + light.LightIntensity);
        }

        public static void SpawnThrownItem(ThrowableSpawnMessage msg, Transform proxyT, bool visualOnly = false)
        {
            if (Singleton<ItemsDatabase>.Instance == null || !Singleton<ItemsDatabase>.Instance.hasItem(msg.ItemType))
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] unknown item type: " + msg.ItemType);
                return;
            }

            InvItem itemDef = Singleton<ItemsDatabase>.Instance.getItem(msg.ItemType, instantiate: false);
            if (itemDef == null || itemDef.item == null)
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] no prefab for " + msg.ItemType);
                return;
            }

            GameObject prefab = itemDef.item as GameObject;
            if (prefab == null) return;

            Vector3 spawnPos = new Vector3(msg.PosX, msg.PosY + 1.0f, msg.PosZ);
            GameObject go = Core.AddPrefab(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f), null);
            if (go == null)
                go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f));

            if (go == null)
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] failed to spawn " + msg.ItemType);
                return;
            }

            // Prevent the freshly instantiated item from immediately colliding with the proxy
            // (which has active colliders via RemotePlayerProxy.EnableCollision).
            // Without this, Molotov's fireOnCollideOnAnyCollision would trigger onCollide
            // on the next physics step, causing an explosion at the spawn position.
            if (proxyT != null)
            {
                Collider[] itemCols = go.GetComponentsInChildren<Collider>(true);
                Collider[] proxyCols = proxyT.GetComponentsInChildren<Collider>(true);
                if (itemCols.Length > 0 && proxyCols.Length > 0)
                {
                    foreach (var ic in itemCols)
                        foreach (var pc in proxyCols)
                            Physics.IgnoreCollision(ic, pc);
                }
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            ThrownItem ti = go.GetComponent<ThrownItem>();
            float distance = Mathf.Clamp(msg.Distance, 10f, 370f);
            Vector3 vel = new Vector3(msg.VelX, msg.VelY, msg.VelZ);

            if (rb != null)
            {
                rb.isKinematic = false;
                rb.drag = 2f;
                rb.velocity = vel;

                float rotForce = ti != null ? ti.initialRotationForce : 225f;
                if (rotForce == 0f)
                    rb.angularVelocity = Vector3.zero;
                else
                    rb.AddTorque(0f, rotForce * 1000f, 0f);
            }

            if (ti != null)
            {
                ti.thrown = true;
                ti.objectThatSpawnedMe = proxyT;
                Vector3 origin = proxyT != null ? proxyT.position : spawnPos;
                Vector3 dir3 = vel.sqrMagnitude > 0.01f ? vel.normalized : Quaternion.Euler(0f, msg.AimY, 0f) * Vector3.forward;
                ti.landTarget = origin + dir3 * distance;
                ti.setFallSpeed(distance);
            }

            Explodes expl = go.GetComponent<Explodes>();
            if (expl != null)
                expl.objectThatSpawnedMe = proxyT;

            // Client-side / visual copies: keep trajectory + FX, host alone applies combat.
            if (visualOnly)
                MuteThrownCombat(go);

            // Thrown flare: ensure ground light is visible on peers (prefab may arrive disabled).
            EnsureThrownFlareLight(go, msg.ItemType);

            if (!visualOnly)
                Core.addToSaveable(go, isDynamic: true);
            ModRuntime.LegacyInfo("[ThrowableSpawn] spawned " + msg.ItemType + " at " + spawnPos + " aimY=" + msg.AimY + " dist=" + msg.Distance + " vel=" + msg.VelX + "," + msg.VelY + "," + msg.VelZ + " visualOnly=" + visualOnly);
        }

        public static void TriggerExplosion(Vector3 pos, string objectName, bool flaming = false)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, 1.5f);
            Explodes target = null;
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Explodes expl = nearby[i].GetComponentInParent<Explodes>();
                if (expl != null)
                {
                    target = expl;
                    break;
                }
            }

            if (target == null && !string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null)
                    target = named.GetComponent<Explodes>();
            }

            if (target != null)
            {
                ModRuntime.LegacyInfo("[ExplosionTrigger] activating " + target.name + " at " + pos + " flaming=" + flaming);
                _suppressBroadcast = true;
                try
                {
                    if (flaming)
                        target.onActivate(true);
                    else
                        target.onActivate();
                }
                finally { _suppressBroadcast = false; }
                // Proxy damage handled by ExplosionFriendlyFirePatch.Postfix on Explodes.explode()
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ExplosionTrigger] no Explodes found at " + pos + " name=" + objectName);
            }
        }

        /// <summary>
        /// Spawns the explosion visual effect (prefab + sound) on the client side.
        /// Tries to find the Explodes component locally first; if not found, falls back
        /// to spawning the prefab by name and playing the sound from the message fields.
        /// </summary>
        public static void SpawnExplosionVisual(Vector3 pos, string objectName, string prefabName, string soundId)
        {
            Explodes target = null;

            // Try by object name first (works for static world objects like barrels)
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null)
                    target = named.GetComponent<Explodes>();
            }

            // Fallback: search by position
            if (target == null)
            {
                Collider[] nearby = Physics.OverlapSphere(pos, 1.5f);
                for (int i = 0; i < nearby.Length; i++)
                {
                    if (nearby[i] == null) continue;
                    Explodes expl = nearby[i].GetComponentInParent<Explodes>();
                    if (expl != null) { target = expl; break; }
                }
            }

            if (target != null)
            {
                // Don't re-spawn if already activated on this side
                try
                {
                    bool activated = (bool)Traverse.Create(target).Field("activated").GetValue();
                    if (activated)
                    {
                        ModRuntime.LegacyInfo("[ExplosionVisual] " + target.name + " already activated, skipping");
                        return;
                    }
                }
                catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }

                // Spawn the local Explodes' own prefab (exact visual match)
                try
                {
                    UnityEngine.Object prefab = (UnityEngine.Object)Traverse.Create(target).Field("explosionPrefab").GetValue();
                    if (prefab != null)
                    {
                        Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo("[ExplosionVisual] spawned local prefab " + prefab.name + " at " + pos);
                    }
                }
                catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }

                // Sound is already forwarded via PlayerAudio from the host's
                // Play() call — skip local replay to prevent double-audio.

                return;
            }

            // Fallback: no local Explodes found — try spawning via Resources if prefabName is a path,
            // then at minimum play the explosion sound for audio feedback.
            ModRuntime.LegacyInfo("[ExplosionVisual] no local Explodes for \"" + objectName + "\", using message data");

            if (!string.IsNullOrEmpty(soundId))
            {
                AudioController.Play(soundId, pos);
                ModRuntime.LegacyInfo("[ExplosionVisual] played sound " + soundId + " at " + pos);
            }
        }

        /// <summary>
        /// Spawns a "Items/GasolineTrail" prefab at the given world position.
        /// Used when the remote peer reports a gasoline pour. Nest-safe apply flag
        /// (IgniteGasAtPos may call this while already applying).
        /// </summary>
        public static void SpawnGasTrail(Vector3 pos)
        {
            // Dedupe: world save / prior sync may already have a puddle here.
            if (FindFlammableLiquidNear(pos, 0.85f) != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[GasTrail] skip spawn — liquid already near " + pos);
                return;
            }

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                GameObject go = Core.AddPrefab("Items/GasolineTrail", pos, Quaternion.Euler(90f, 0f, 0f), Core.ItemContainer);
                if (go != null)
                {
                    Core.addToSaveable(go, isDynamic: true);
                    ModRuntime.LegacyInfo("[GasTrail] spawned at " + pos);
                }
                else
                {
                    ModRuntime.Log?.LogWarning("[GasTrail] Core.AddPrefab returned null at " + pos);
                }
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        /// <summary>
        /// Finds a Liquid component (gasoline puddle) near the given position and ignites it.
        /// Used when the remote peer reports a gasoline ignition event.
        /// </summary>
        public static void IgniteGasAtPos(Vector3 pos)
        {
            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                Liquid liquid = FindFlammableLiquidNear(pos, 2f);
                if (liquid != null)
                {
                    if (!liquid.burning)
                    {
                        liquid.startBurning();
                        ModRuntime.LegacyInfo("[GasIgnite] ignited " + liquid.name + " at " + pos);
                    }
                    return;
                }

                // Trail not found — spawn the correct trail, then ignite.
                // Nested SpawnGasTrail preserves our apply flag (no mid-call clear).
                SpawnGasTrail(pos);
                liquid = FindFlammableLiquidNear(pos, 2f);
                if (liquid != null && !liquid.burning)
                {
                    liquid.startBurning();
                    ModRuntime.LegacyInfo("[GasIgnite] spawned+ignited trail at " + pos);
                    return;
                }
                ModRuntime.Log?.LogWarning("[GasIgnite] no flammable Liquid found at " + pos + " (even after spawning)");
            }
            finally { TraverseHack.SetExplicitFlag(prev); }
        }

        private static Liquid FindFlammableLiquidNear(Vector3 pos, float radius)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, radius);
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Liquid liquid = nearby[i].GetComponent<Liquid>();
                if (liquid == null) liquid = nearby[i].GetComponentInParent<Liquid>();
                if (liquid != null && liquid.flammable)
                    return liquid;
            }
            return null;
        }
    }

    /// <summary>Serializable snapshot of a physics object's transform (name, position, rotation).</summary>
    public struct WorldObjectState
    {
        /// <summary>Name of the GameObject (used as a lookup key on the receiving end).</summary>
        public string Name;
        /// <summary>World position X coordinate.</summary>
        public float PosX;
        /// <summary>World position Y coordinate.</summary>
        public float PosY;
        /// <summary>World position Z coordinate.</summary>
        public float PosZ;
        /// <summary>Euler rotation X angle.</summary>
        public float RotX;
        /// <summary>Euler rotation Y angle.</summary>
        public float RotY;
        /// <summary>Euler rotation Z angle.</summary>
        public float RotZ;
        /// <summary>
        /// Item type identifier (<see cref="Item.invItem.type"/>), used on the receiving end
        /// to spawn the object on-demand when it doesn't exist locally (e.g. unloaded world chunk).
        /// Empty for objects that have no <see cref="Item"/> component.
        /// </summary>
        public string ItemType;

        /// <summary>Serializes this state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(Name ?? ""); w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(RotX); w.Put(RotY); w.Put(RotZ);
            w.Put(ItemType ?? "");
        }
        /// <summary>Deserializes a state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static WorldObjectState Deserialize(NetReader r) => new WorldObjectState
        {
            Name = r.GetString(),
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            RotX = r.GetFloat(),
            RotY = r.GetFloat(),
            RotZ = r.GetFloat(),
            ItemType = r.GetString()
        };
    }

    /// <summary>Serializable snapshot of a door's open/close state, including swing rotation and opener info.</summary>
    public struct DoorState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the door is open.</summary>
        public bool Opened;
        /// <summary>World position X of the player who opened the door (used for knock-back).</summary>
        public float OpenerPosX;
        /// <summary>World position Y of the opener.</summary>
        public float OpenerPosY;
        /// <summary>World position Z of the opener.</summary>
        public float OpenerPosZ;
        /// <summary>Force magnitude applied when opening (from the original open call).</summary>
        public float OpenForce;
        /// <summary>Y-axis euler angle of the door's body, matching the sender's swing position.</summary>
        public float BodyRotY;
        /// <summary>X component of door body angular velocity (for swing continuity).</summary>
        public float AngVelX;
        /// <summary>Y component of door body angular velocity.</summary>
        public float AngVelY;
        /// <summary>Z component of door body angular velocity.</summary>
        public float AngVelZ;

        /// <summary>Serializes this door state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(Opened);
            w.Put(OpenerPosX); w.Put(OpenerPosY); w.Put(OpenerPosZ);
            w.Put(OpenForce); w.Put(BodyRotY);
            w.Put(AngVelX); w.Put(AngVelY); w.Put(AngVelZ);
        }
        /// <summary>Deserializes a door state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static DoorState Deserialize(NetReader r) => new DoorState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Opened = r.GetBool(),
            OpenerPosX = r.GetFloat(),
            OpenerPosY = r.GetFloat(),
            OpenerPosZ = r.GetFloat(),
            OpenForce = r.GetFloat(),
            BodyRotY = r.GetFloat(),
            AngVelX = r.GetFloat(),
            AngVelY = r.GetFloat(),
            AngVelZ = r.GetFloat()
        };
    }

    /// <summary>Serializable snapshot of a trap's triggered state.</summary>
    public struct TrapState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the trap has been triggered (sprung/snapped).</summary>
        public bool Triggered;

        /// <summary>Serializes this trap state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(Triggered);
        }
        /// <summary>Deserializes a trap state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static TrapState Deserialize(NetReader r) => new TrapState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Triggered = r.GetBool()
        };
    }

    /// <summary>Serializable snapshot of a generator's on/off state and fuel level.</summary>
    public struct GeneratorState
    {
        /// <summary>Rounded world position X (serves as lookup key).</summary>
        public float PosX;
        /// <summary>Rounded world position Y.</summary>
        public float PosY;
        /// <summary>Rounded world position Z.</summary>
        public float PosZ;
        /// <summary>Whether the generator is running.</summary>
        public bool IsOn;
        /// <summary>Current fuel level.</summary>
        public float Fuel;
        /// <summary>Low-power state (lights flicker when fuel < 10%).</summary>
        public bool LowPower;
        /// <summary>
        /// Item type identifier (<see cref="Item.invItem.type"/>), used on the receiving end
        /// to spawn the generator on-demand when it doesn't exist locally.
        /// </summary>
        public string ItemType;

        /// <summary>Serializes this generator state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(IsOn); w.Put(Fuel);
            w.Put(LowPower); w.Put(ItemType ?? "");
        }
        /// <summary>Deserializes a generator state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static GeneratorState Deserialize(NetReader r) => new GeneratorState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            IsOn = r.GetBool(),
            Fuel = r.GetFloat(),
            LowPower = r.GetBool(),
            ItemType = r.GetString()
        };
    }

    /// <summary>Top-level network message containing arrays of object, door, trap, and generator states.</summary>
    public struct PhysicsStateMessage
    {
        /// <summary>All physics object transforms in this snapshot.</summary>
        public WorldObjectState[] Objects;
        /// <summary>All door state changes in this snapshot.</summary>
        public DoorState[] Doors;
        /// <summary>All trap state changes in this snapshot.</summary>
        public TrapState[] Traps;
        /// <summary>All generator state changes in this snapshot.</summary>
        public GeneratorState[] Generators;

        /// <summary>Serializes the full message into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            int oc = Objects != null ? Objects.Length : 0;
            w.Put(oc);
            for (int i = 0; i < oc; i++) Objects[i].Serialize(w);

            int dc = Doors != null ? Doors.Length : 0;
            w.Put(dc);
            for (int i = 0; i < dc; i++) Doors[i].Serialize(w);

            int tc = Traps != null ? Traps.Length : 0;
            w.Put(tc);
            for (int i = 0; i < tc; i++) Traps[i].Serialize(w);

            int gc = Generators != null ? Generators.Length : 0;
            w.Put(gc);
            for (int i = 0; i < gc; i++) Generators[i].Serialize(w);
        }

        /// <summary>Deserializes a full message from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static PhysicsStateMessage Deserialize(NetReader r)
        {
            int oc = r.GetInt();
            if (oc < 0 || oc > 4096) oc = 0;
            var objs = new WorldObjectState[oc];
            for (int i = 0; i < oc; i++) objs[i] = WorldObjectState.Deserialize(r);

            int dc = r.GetInt();
            if (dc < 0 || dc > 4096) dc = 0;
            var doors = new DoorState[dc];
            for (int i = 0; i < dc; i++) doors[i] = DoorState.Deserialize(r);

            int tc = r.GetInt();
            if (tc < 0 || tc > 4096) tc = 0;
            var traps = new TrapState[tc];
            for (int i = 0; i < tc; i++) traps[i] = TrapState.Deserialize(r);

            int gc = r.GetInt();
            if (gc < 0 || gc > 4096) gc = 0;
            var generators = new GeneratorState[gc];
            for (int i = 0; i < gc; i++) generators[i] = GeneratorState.Deserialize(r);

            return new PhysicsStateMessage
            {
                Objects = objs,
                Doors = doors,
                Traps = traps,
                Generators = generators
            };
        }
    }

    /// <summary>
    /// Provides reflection-based helpers for reading and writing private fields
    /// on Door, Trigger, and other game types via Harmony Traverse.
    /// </summary>
    internal static class TraverseHack
    {
        private static bool _explicitApplyingFromNetwork;

        /// <summary>
        /// True while applying remote state. While <see cref="NetworkApplyGuard.IsActive"/>,
        /// always true even if nested code assigns false (prevents split-brain rebroadcast).
        /// </summary>
        public static bool ApplyingFromNetwork
        {
            get => _explicitApplyingFromNetwork || Networking.NetworkApplyGuard.IsActive;
            set => _explicitApplyingFromNetwork = value;
        }

        internal static bool GetExplicitFlag() => _explicitApplyingFromNetwork;
        internal static void SetExplicitFlag(bool value) => _explicitApplyingFromNetwork = value;

        /// <summary>
        /// Set true while inside a CharacterSounds method that EntitySoundSyncPatches
        /// already handles (playGrowl, playSingleInstance, playEscapingLoop, playIdleLoop).
        /// PlayerSoundSyncPatches checks this flag to avoid double-forwarding the
        /// AudioController.Play call that happens inside these methods.
        /// </summary>
        public static bool InsideCharacterSounds = false;

        /// <summary>
        /// Set true on client during a local Explodes.explode() call so
        /// ClientDamageRedirectPatch can redirect AOE splash damage to the host
        /// (the host re-enacts the explosion and applies damage authoritatively).
        /// </summary>
        public static bool IsInsideLocalExplosion = false;

        /// <summary>
        /// Set true on client while inside Bullet.onCollide for a player-fired
        /// projectile (objectThatSpawnedMe == null). ClientDamageRedirectPatch
        /// checks this to detect projectile weapon damage where the vanilla
        /// code never sets objectThatSpawnedMe on player bullets.
        /// </summary>
        public static bool IsInsidePlayerBulletCollision = false;

        /// <summary>Clear all transient apply flags on network stop (stuck flags leak rebroadcast blocks).</summary>
        public static void ResetTransientFlags()
        {
            _explicitApplyingFromNetwork = false;
            InsideCharacterSounds = false;
            IsInsideLocalExplosion = false;
            IsInsidePlayerBulletCollision = false;
        }

        /// <summary>Reads the private "opened" field from a Door instance.</summary>
        /// <param name="door">The door instance.</param>
        public static bool ReadDoorOpened(Door door)
        {
            var t = Traverse.Create(door);
            return t.Field("opened").GetValue<bool>();
        }

        /// <summary>
        /// Opens or closes a door: invokes the original open/close method via reflection,
        /// ensures the "opened" field matches, and syncs the door body's rotation + angular velocity
        /// so the receiver's door swing matches the sender's physical swing.
        /// </summary>
        /// <param name="door">The door instance.</param>
        /// <param name="opened">True to open, false to close.</param>
        /// <param name="openerPos">Position of the player interacting with the door (used as open origin).</param>
        /// <param name="openForce">Force magnitude from the original open call.</param>
        /// <param name="bodyRotY">Target Y euler angle for the door body to match the sender's swing.</param>
        /// <param name="angVelX">X component of sender's door body angular velocity.</param>
        /// <param name="angVelY">Y component of sender's door body angular velocity.</param>
        /// <param name="angVelZ">Z component of sender's door body angular velocity.</param>
        public static void SetDoorOpened(Door door, bool opened, Vector3 openerPos = default, float openForce = 0f, float bodyRotY = 0f, float angVelX = 0f, float angVelY = 0f, float angVelZ = 0f)
        {
            InvokeDoorMethod(door, opened ? "open" : "close", openerPos, openForce);

            var t = Traverse.Create(door);
            if (t.Field("opened").GetValue<bool>() != opened)
                t.Field("opened").SetValue(opened);

            if (door.body != null)
            {
                Rigidbody doorBodyRB = door.body.GetComponent<Rigidbody>();
                if (doorBodyRB != null)
                {
                    Vector3 currentEuler = door.body.eulerAngles;
                    bool closeSnap = !opened && Mathf.Abs(Mathf.DeltaAngle(currentEuler.y, bodyRotY)) > 5f;
                    if (closeSnap || (opened && bodyRotY != 0f))
                    {
                        Quaternion targetRot = Quaternion.Euler(currentEuler.x, bodyRotY, currentEuler.z);
                        door.body.rotation = targetRot;
                        if (opened)
                        {
                            doorBodyRB.velocity = Vector3.zero;
                            doorBodyRB.angularVelocity = Vector3.zero;
                        }
                        else
                        {
                            doorBodyRB.constraints = RigidbodyConstraints.FreezeAll;
                            doorBodyRB.isKinematic = true;
                        }
                    }

                    // Apply sender's angular velocity so the door continues its natural swing
                    Vector3 senderAngVel = new Vector3(angVelX, angVelY, angVelZ);
                    if (senderAngVel.sqrMagnitude > 0f && opened)
                    {
                        doorBodyRB.angularVelocity = senderAngVel;
                    }

                    // Note: the kick sound ("door_hit_run" at thumpForce=45000)
                    // is played by Door.open() inside InvokeDoorMethod above,
                    // which receives the original OpenForce value from the sender.
                }
            }
        }

        /// <summary>
        /// Invokes the public or non-public "open" or "close" method on the Door type via reflection,
        /// matching the method's parameter signature. Falls back to toggling colliders and playing
        /// animation clips if reflection fails.
        /// </summary>
        private static void InvokeDoorMethod(Door door, string methodName, Vector3 openerPos = default, float openForce = 0f)
        {
            try
            {
                bool opening = (methodName == "open");
                bool invoked = false;

                // Try calling the original method via reflection (handles colliders, animation, internal state)
                try
                {
                    var methods = typeof(Door).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name != methodName) continue;
                        var pars = m.GetParameters();
                        object[] args = new object[pars.Length];
                        for (int i = 0; i < pars.Length; i++)
                        {
                            Type pt = pars[i].ParameterType;
                            if (pt == typeof(Vector3)) args[i] = opening ? openerPos : Vector3.zero;
                            else if (pt == typeof(Transform))
                            {
                                // Don't pass a transform so open() doesn't overwrite openerPos with the door's position
                                if (opening && openerPos != default)
                                    args[i] = null;
                                else
                                    args[i] = door.transform;
                            }
                            else if (pt == typeof(float)) args[i] = opening ? openForce : 0f;
                            else if (pt == typeof(bool)) args[i] = opening;
                            else if (pt == typeof(int)) args[i] = 0;
                            else args[i] = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                        }
                        m.Invoke(door, args);
                        invoked = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DoorReflect] failed for " + methodName + ": " + ex);
                }

                if (invoked)
                    return;

                // Fallback: toggle colliders and play animation
                foreach (Collider c in door.GetComponentsInChildren<Collider>(true))
                {
                    if (c != null && !c.isTrigger)
                        c.enabled = !opening;
                }

                tk2dSpriteAnimator anim = door.GetComponentInChildren<tk2dSpriteAnimator>();
                if (anim != null)
                {
                    string clip = methodName;
                    if (anim.GetClipByName(clip) != null) anim.Play(clip);
                    else if (anim.GetClipByName(opening ? "Open" : "Close") != null) anim.Play(opening ? "Open" : "Close");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DoorAnim] " + ex);
            }
        }
    }
}
