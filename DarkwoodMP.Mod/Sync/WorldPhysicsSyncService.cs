using System;
using System.Collections.Generic;
using DWMPHorde.Audio;
using DWMPHorde.Logging;
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
        // One-tick cushion only (~half a 10Hz PhysicsState interval). Longer hold was
        // the audible "scrape keeps going after I stop" lag; SoftStop re-arms mid-push
        // without PostStop suppress, so we do not need a multi-tenths residual hold.
        private const float BodyPushSoundHold = 0.05f;
        /// <summary>Hard desync only — normal push steps must lerp (1.5 was snappy).</summary>
        private const float ClientPushSnapDistance = 8f;
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
        // Drift correct every 5s (was 3s) — full resync forces every free-body in range into the packet.
        private static readonly float FullResyncInterval = 5f;
        private static float _lastFullRbScanTime = -999f;
        private const float FullRbScanMinInterval = 0.5f;
        /// <summary>
        /// After real motion stops, include the object for this long so peers get a final quiet
        /// sample. Must NOT refresh <see cref="_lastMoveTime"/> on those quiet samples — old 2.5s
        /// window + refresh made any nudged free-body stream forever at 10 Hz (client FPS crater
        /// while connected; recovered when host left and PhysicsState stopped).
        /// </summary>
        private const float QuietConfirmWindow = 0.15f;
        private static readonly Dictionary<int, GameObject> _knownTraps = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, bool> _trapResultCache = new Dictionary<int, bool>();

        private struct ThrownLightTrack
        {
            public int ThrowId;
            public GameObject Go;
            public float ExpireAt;
            public string ItemType;
        }
        private static readonly List<ThrownLightTrack> _thrownLights = new List<ThrownLightTrack>(16);
        private static readonly Dictionary<int, ThrownLightTrack> _thrownById = new Dictionary<int, ThrownLightTrack>(16);

        /// <summary>Vanilla Flare.waitToDie fade length after longevity elapses.</summary>
        public const float FlareBurnoutFadeSec = 2f;

        private struct FlareBurnStart
        {
            public float StartTime;
            public float Longevity;
        }
        /// <summary>GO instanceId → burn clock from Flare.Start (aim time).</summary>
        private static readonly Dictionary<int, FlareBurnStart> _flareBurnStarts =
            new Dictionary<int, FlareBurnStart>(8);

        private struct ThrownLightFade
        {
            public GameObject Go;
            public float EndTime;
            public float Duration;
            public float StartIntensity;
            public Light2D[] Lights;
            /// <summary>Optional sibling (held FlareFx) destroyed after fade completes.</summary>
            public GameObject SiblingDestroy;
        }
        private static readonly List<ThrownLightFade> _thrownLightFades = new List<ThrownLightFade>(8);

        /// <summary>
        /// Network owns die clock — keep Flare for flicker/rotation, skip waitToDie via Harmony.
        /// </summary>
        public static void ClaimFlareLifetime(GameObject go)
        {
            if (go == null) return;
            var auth = go.GetComponent<NetworkFlareLifetime>();
            if (auth == null)
                auth = go.AddComponent<NetworkFlareLifetime>();
            auth.NetworkOwnsDie = true;
            // Also mark any child Flare roots so parent lookup always hits.
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl == null || fl.gameObject == go) continue;
                var childAuth = fl.GetComponent<NetworkFlareLifetime>();
                if (childAuth == null)
                    childAuth = fl.gameObject.AddComponent<NetworkFlareLifetime>();
                childAuth.NetworkOwnsDie = true;
            }
        }

        private static readonly List<GeneratorState> _generators = new List<GeneratorState>();
        private static readonly Dictionary<Vector3, bool> _lastGeneratorOn = new Dictionary<Vector3, bool>();
        private static readonly Dictionary<Vector3, float> _lastGeneratorFuel = new Dictionary<Vector3, float>();
        private static readonly List<Vector3> _scanCenters = new List<Vector3>(8);
        private static readonly HashSet<int> _scannedObjectIds = new HashSet<int>();

        // Client free-body packets are ~10 Hz (0.1s). Buffer slightly longer so host
        // retargets mid-lerp instead of finishing each segment into a snap.
        private const float InterpFixedDuration = 0.2f;
        /// <summary>Host applying client push — same buffer (explicit for clarity).</summary>
        private const float ClientPushInterpDuration = 0.2f;

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
            public Rigidbody CachedRb;
            public Item CachedItem;
            public bool CachedComps;
        }
        private static readonly Dictionary<int, ObjectInterpState> _objectInterp = new Dictionary<int, ObjectInterpState>();
        private static readonly List<int> _objectInterpDeadKeys = new List<int>();
        private static readonly List<int> _objectInterpKeys = new List<int>();



        /// <summary>
        /// OverlapSphere around one center; adds free rigidbodies + detects traps.
        /// Dedupes by GameObject instance id across multi-center host scans.
        /// Hot path: cheap name + motion gate first; heavy GetComponent only for candidates.
        /// </summary>
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

                // Motion gate BEFORE component spam — idle free-bodies dominate hideout OverlapSphere.
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

                // D12: while dreaming, only free bodies in the dream pocket.
                if (!DreamSyncManager.ShouldSyncPhysicsObject(rootGo.transform))
                    continue;

                Vector3 rot = rootGo.transform.eulerAngles;
                _lastPos[trackingKey] = pos;
                // Only real motion extends quiet window — quiet confirms must not refresh it.
                if (reallyMoved)
                    _lastMoveTime[trackingKey] = now;

                // Host: keep client-owned free-bodies in interp (posDelta for scrape +
                // smooth apply). Clearing them made every client packet look stationary
                // (posDelta=0) → host never armed body-push MOS.
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
            bool clientRecv = fromPeer != null
                && fromPeer.Equals("host", System.StringComparison.OrdinalIgnoreCase)
                && ModRuntime.Network != null
                && ModRuntime.Network.Role == Networking.NetworkRole.Client;

            if (state.Objects != null)
            {
                foreach (WorldObjectState obj in state.Objects)
                {
                    if (obj.Name == "Player" || obj.Name == "PlayerLegs" || obj.Name == "RemotePlayer" || obj.Name.Contains("DoorSensor"))
                    {
                        objSkipped++;
                        continue;
                    }

                    // Client: skip far free-bodies (FindOrSpawn / full RB scan was dual-box thrash).
                    if (clientRecv)
                    {
                        Vector3 opos = new Vector3(obj.PosX, obj.PosY, obj.PosZ);
                        if (!Networking.ClientEntityInterpolationService.IsInClientInterest(opos))
                        {
                            objSkipped++;
                            continue;
                        }
                    }

                    GameObject go = FindOrSpawnObject(obj);

                    if (go == null)
                    {
                        objFailed++;
                        continue;
                    }

                    // Never kinematic-lock / interp an in-flight match/flare/molotov.
                    // Peer ThrowableSpawn already set landTarget + setFallSpeed + velocity.
                    if (IsInFlightThrownItem(go))
                    {
                        RemoveObjectFromInterpolation(go);
                        Rigidbody flyRb = go.GetComponent<Rigidbody>();
                        if (flyRb != null && flyRb.isKinematic)
                            flyRb.isKinematic = false;
                        int flyId = go.GetInstanceID();
                        _clientKinematic.Remove(flyId);
                        objSkipped++;
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

                            // Baseline: last interp target, else current host pose.
                            // (Missing baseline forced posDelta=0 → never body-push scrape.)
                            Vector3 baseline = go.transform.position;
                            if (_objectInterp.TryGetValue(goId, out var existingInterp))
                                baseline = existingInterp.TargetPos;
                            float posDelta = Vector3.Distance(baseline, objPos);
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

                            // Scrape: arm on motion; first quiet packet → stop decision *now*.
                            // SoftStop (no suppress) + NoteMoving re-arm cancels a premature
                            // fade if the next tick is motion again. Do not hold residual volume.
                            if (posChanged && !gated)
                            {
                                if (_bodyPushSoundActive.Add(obj.Name))
                                {
                                    ModRuntime.LegacyInfo("[SND] body-push start " + obj.Name + " d=" + posDelta.ToString("F3"));
                                    LanNetworkManager.NotifyBodyPushStarted(go);
                                }
                                else
                                {
                                    // Keep MOS alive mid-push (NoteMoving is idempotent / cancels fade).
                                    LanNetworkManager.NotifyBodyPushStarted(go);
                                }
                                _bodyPushSoundTimer[goId] = Time.time + BodyPushSoundHold;
                                _pushNameToGid[obj.Name] = goId;
                                _pushGidToName[goId] = obj.Name;
                            }
                            else if (!gated && _bodyPushSoundActive.Remove(obj.Name))
                            {
                                // First quiet tick → stop decision now (≤ one PhysicsState interval).
                                ModRuntime.LegacyInfo("[SND] body-push stop (quiet) " + obj.Name);
                                LanNetworkManager.NotifyBodyPushStopped(obj.Name);
                                _bodyPushSoundTimer.Remove(goId);
                                _pushNameToGid.Remove(obj.Name);
                                _pushGidToName.Remove(goId);
                            }

                            // Hard desync only: snap start of interp. Routine push must lerp
                            // (old 1.5u snap every few packets = snappy host motion).
                            if (posDelta >= ClientPushSnapDistance)
                            {
                                rb.position = objPos;
                                rb.rotation = Quaternion.Euler(rotVec);
                                go.transform.position = objPos;
                                go.transform.rotation = Quaternion.Euler(rotVec);
                                _objectInterp.Remove(goId);
                            }
                            SetObjectTarget(go, objPos, rotVec, ClientPushInterpDuration);
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
                        // Local pusher/dragger owns this free-body. Host snapshot echo must not:
                        // - SetObjectTarget (kinematic lock + fight local physics — ObjInterp thrash)
                        // - NoteMoving / ForceStop (double scrape / kill native mid-push)
                        if (!string.IsNullOrEmpty(obj.Name)
                            && ItemMovingSoundHelper.IsLocalPushOrDragOwner(obj.Name))
                        {
                            RemoveObjectFromInterpolation(go);
                            Rigidbody localRb = go.GetComponent<Rigidbody>();
                            if (localRb != null && localRb.isKinematic)
                                localRb.isKinematic = false;
                            if (ItemMovingSoundHelper.IsRemoteScrape(obj.Name)
                                || MovingObjectSoundService.IsPlaying(obj.Name)
                                || MovingObjectSoundService.IsFading(obj.Name))
                                MovingObjectSoundService.StopImmediate(obj.Name);
                            objSkipped++;
                            continue;
                        }

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
                                        // Remote host→client motion: MOS only (MarkRemoteScrape inside NoteMoving).
                                        MovingObjectSoundService.NoteMoving(__ic.gameObject, obj.Name, __isnd);
                                        _pushNameToGid[obj.Name] = __gid;
                                        _pushGidToName[__gid] = obj.Name;
                                        _lastPushSoundTime[__gid] = Time.time;
                                        _pushStationaryCount[__gid] = 0;
                                    }
                                    else if (_lastPushSoundTime.ContainsKey(__gid)
                                        || ItemMovingSoundHelper.IsRemoteScrape(obj.Name))
                                    {
                                        // First quiet net tick — soft stop (no PostStop suppress).
                                        ItemMovingSoundHelper.SoftStopNetwork(obj.Name);
                                        _lastPushSoundTime.Remove(__gid);
                                        _pushStationaryCount.Remove(__gid);
                                        _pushNameToGid.Remove(obj.Name);
                                        _pushGidToName.Remove(__gid);
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
                        GameObject go = ts.TrapNetId > 0
                            ? TrapNetworkId.FindById(ts.TrapNetId)
                            : null;
                        if (go == null)
                            go = FindTrapByPos(tPos);
                        if (go == null)
                        {
                            TrapNetworkId.QueuePending(ts.TrapNetId, tPos, ts.Triggered);
                            trapSkipped++;
                            continue;
                        }

                        if (ts.TrapNetId > 0)
                            TrapNetworkId.Ensure(go, ts.TrapNetId);
                        else if (ModRuntime.Network is LanNetworkManager n
                                 && n.Role == NetworkRole.Host)
                            TrapNetworkId.GetOrMintHost(go);

                        bool silent = ts.OccupantPlayerId == TrapState.OccupantSilentDisarm;
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo("[TrapApply] " + go.name + " id=" + ts.TrapNetId
                                + " at " + tPos + " triggered=" + ts.Triggered + " silent=" + silent);
                        ApplyTrapState(go, ts.Triggered, silentDisarm: silent);
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
                    // Destroy is end-of-frame deferred — ApplyingFromNetwork would already be
                    // false in Object.Destroy Prefix → re-broadcast WorldObjectRemoved thrash.
                    try
                    {
                        TraverseHack.ApplyingFromNetwork = true;
                        UnityEngine.Object.DestroyImmediate(root);
                    }
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
                    try
                    {
                        TraverseHack.ApplyingFromNetwork = true;
                        UnityEngine.Object.DestroyImmediate(named);
                    }
                    finally { TraverseHack.ApplyingFromNetwork = false; }
                    ModRuntime.LegacyInfo("[ObjectDestroy] destroyed by name \"" + named.name + "\" at " + pos);
                }
            }
        }

        /// <summary>
        /// Sets a new position/rotation target for an object and resets the interpolation
        /// state so it smoothly moves from its current position to the target over <paramref name="durationSec"/>.
        /// </summary>
        private static void SetObjectTarget(GameObject go, Vector3 targetPos, Vector3 targetRot, float durationSec = -1f)
        {
            int id = go.GetInstanceID();
            float now = Time.time;
            float duration = durationSec > 0.001f ? durationSec : InterpFixedDuration;

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
                // Prefer live rigidbody pose (kinematic drive) over transform if available.
                Rigidbody rb0 = go.GetComponent<Rigidbody>();
                if (rb0 != null)
                {
                    state.PrevPos = rb0.position;
                    state.PrevRot = rb0.rotation.eulerAngles;
                }
                else
                {
                    state.PrevPos = go.transform.position;
                    state.PrevRot = go.transform.eulerAngles;
                }
            }

            state.Target = go;
            state.TargetPos = targetPos;
            state.TargetRot = targetRot;
            state.PrevTime = now;
            state.TargetTime = now + duration;
            _objectInterp[id] = state;

            // Lock to host position during active sync — prevents proxy collisions
            // on the client from pushing the object away from the host's position.
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                // Host must stay kinematic too while interpolating client-pushed free-bodies.
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

            // Strategy 1b: wider sphere before full-scene scan (client stutter when host
            // pushes objects 2–15u away — OverlapSphere 1.5 miss then FindObjectsOfType).
            {
                Collider[] wide = Physics.OverlapSphere(targetPos, 15f);
                GameObject bestWide = null;
                float bestWideDist = float.MaxValue;
                for (int i = 0; i < wide.Length; i++)
                {
                    if (wide[i] == null || wide[i].attachedRigidbody == null) continue;
                    GameObject candidate = wide[i].attachedRigidbody.gameObject;
                    if (candidate.name != obj.Name) continue;
                    if (candidate.GetComponent<Character>() != null) continue;
                    if (candidate.GetComponent<DroppedItemIdentifier>() != null) continue;
                    if (candidate.GetComponent<DeathDrop>() != null) continue;
                    float d = Vector3.Distance(candidate.transform.position, targetPos);
                    if (d < bestWideDist)
                    {
                        bestWideDist = d;
                        bestWide = candidate;
                    }
                }
                if (bestWide != null)
                    return bestWide;
            }

            // Strategy 2: full Rigidbody scan — rate-limited (scene-wide FindObjectsOfType
            // every PhysicsState packet was a dual-box hitch source).
            float nowScan = Time.time;
            if (nowScan - _lastFullRbScanTime >= FullRbScanMinInterval)
            {
                _lastFullRbScanTime = nowScan;
                DWMPHorde.Logging.ClientPerfProbe.NoteFullRbScan();
                var footSw = System.Diagnostics.Stopwatch.StartNew();
                Rigidbody[] allRbs = UnityEngine.Object.FindObjectsOfType<Rigidbody>();
                footSw.Stop();
                DWMPHorde.Logging.ClientPerfProbe.NoteFindObjectsOfType("Rigidbody", footSw.Elapsed.TotalMilliseconds);
                GameObject best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < allRbs.Length; i++)
                {
                    Rigidbody rb = allRbs[i];
                    if (rb == null) continue;
                    if (rb.name != obj.Name) continue;
                    if (rb.GetComponent<Character>() != null) continue;
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

        /// <summary>
        /// Finds a trap by position via OverlapSphere only — no FindObjectsOfType / GameObject.Find
        /// (those caused periodic maxMs=50–60 stutters with 1 pending trap).
        /// </summary>
        internal static GameObject FindTrapByPos(Vector3 pos, string objectName = null)
        {
            GameObject hit = FindTrapInSphere(pos, 1.5f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 8f);
            if (hit != null) return hit;
            hit = FindTrapInSphere(pos, 20f);
            if (hit != null) return hit;

            // Optional name — only if an instance is already active (no full-scene FoT).
            if (!string.IsNullOrEmpty(objectName))
            {
                GameObject named = GameObject.Find(objectName);
                if (named != null && HasTrapField(named))
                    return named;
            }

            return null;
        }

        private static GameObject FindTrapInSphere(Vector3 pos, float radius)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, radius);
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
        /// <param name="silentDisarm">
        /// True = successful harvest/disarm: mirror vanilla <c>switchToTriggered</c> only
        /// (sprite/name, keep GO). No explosion prefab/sound (stomp still uses silent=false).
        /// </param>
        internal static void ApplyTrapState(GameObject go, bool triggered, bool silentDisarm = false)
        {
            if (go == null) return;

            bool current = ReadTrapTriggered(go);
            if (current == triggered) return;

            // Successful harvest/disarm: vanilla staysAfterDisarming keeps the object and only
            // flips to the "disarmed/triggered" presentation — never boom or Destroy.
            if (triggered && silentDisarm)
            {
                Trigger tSilent = go.GetComponent<Trigger>();
                if (tSilent != null)
                {
                    bool prev = TraverseHack.ApplyingFromNetwork;
                    TraverseHack.ApplyingFromNetwork = true;
                    try { tSilent.switchToTriggered(); }
                    finally { TraverseHack.ApplyingFromNetwork = prev; }
                }
                else
                {
                    Component[] comps = go.GetComponents<Component>();
                    foreach (Component comp in comps)
                    {
                        if (comp == null) continue;
                        Traverse tr = Traverse.Create(comp);
                        if (TryWriteBool(tr, "triggered", true)) break;
                    }
                }
                ModRuntime.LegacyInfo("[TrapApply] silent disarm (no FX) " + go.name);
                return;
            }

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
                Explodes expl = go.GetComponent<Explodes>();

                // Diagnostic: confirm what actually owns this mushroom's boom. World
                // mushrooms (expObj_mushroom_interior_01) have a Trigger but NO Explodes —
                // their blast is the Trigger's prefabToSpawn, which the old isHarvestable
                // skip was hiding.
                if (isHarvestable && ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[TrapApply] mushroom comps name=" + go.name
                        + " hasExplodes=" + (expl != null)
                        + " prefabToSpawn=" + (trig != null && trig.prefabToSpawn != null ? trig.prefabToSpawn.name : "null")
                        + " activateSound=" + (trig != null ? trig.activateSound : ""));

                if (expl == null || !isHarvestable)
                {
                    // Generic trap VFX: activateSound + prefabToSpawn. Runs for every
                    // non-mushroom trap (unchanged) AND for mushrooms without an Explodes
                    // component — world mushrooms like expObj_mushroom_interior_01, whose
                    // boom is the Trigger's prefabToSpawn. The old `!isHarvestable`-only
                    // gate skipped the latter on the false assumption they used Explodes,
                    // which caused the silent snap.
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
                else if (isHarvestable)
                {
                    // Explodes-based mushroom (rare): render the blast via the explosion
                    // visual path and own the end state (destroyOnExplode).
                    string prefabName = expl.explosionPrefab != null ? expl.explosionPrefab.name : "";
                    string soundId = ResolveExplosionSoundId(expl.explodeSound ?? "", go.name, expl) ?? "";
                    ModRuntime.LegacyInfo("[TrapApply] mushroom blast VFX (Explodes) " + go.name
                        + " prefab=" + prefabName + " sound=" + soundId + " at " + go.transform.position);
                    SpawnExplosionVisual(go.transform.position, go.name, prefabName, soundId);

                    if (trig != null && trig.alertRadius > 0f)
                        Character.alertInArea(go.transform.position, trig.alertRadius, dangerousSound: false, 1f);
                    return;
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

                if (!s.CachedComps || s.CachedRb == null && s.CachedItem == null)
                {
                    s.CachedRb = s.Target.GetComponent<Rigidbody>();
                    s.CachedItem = s.Target.GetComponent<Item>();
                    s.CachedComps = true;
                    _objectInterp[key] = s;
                }

                // Skip objects being dragged by the local player — local physics
                // (HingeJoint) already drives the correct position. Interpolation
                // would fight the joint and cause jitter.
                Item item = s.CachedItem;
                if (item != null && item.beingDragged)
                {
                    // Don't remove from interpolation yet; the drag might end
                    // and we want to resume smoothly. Just skip position update.
                    // Release kinematic so the local HingeJoint can drive position.
                    Rigidbody dragRb = s.CachedRb;
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

                Rigidbody rb = s.CachedRb;
                bool hostClientPush = rb != null && _clientKinematic.ContainsKey(key);
                if (t < 1f || hostClientPush)
                {
                    // Active lerp, or hold last target on host while waiting for next
                    // client PhysicsState (avoids freeze-then-teleport between packets).
                    Vector3 pos = t < 1f ? lerpPos : s.TargetPos;
                    Quaternion rot = t < 1f ? lerpRot : Quaternion.Euler(s.TargetRot);
                    if (rb != null)
                    {
                        if (!rb.isKinematic)
                            rb.isKinematic = true;
                        rb.position = pos;
                        rb.rotation = rot;
                        rb.velocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    else
                    {
                        s.Target.transform.position = pos;
                        s.Target.transform.rotation = rot;
                    }
                }
                else
                {
                    // Interpolation complete (non-client-push). Release kinematic so the
                    // local peer can push / interact. Next snapshot re-locks.
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

            // Local free-body push: ForceStop when player stops / leaves contact (T3).
            ItemMovingSoundHelper.TickLocalPushScrapeStop();

            // Safety net if PhysicsState packets stop entirely: soft-stop after ~1 missed
            // 10Hz tick + margin. Decision lag is the bug; SoftStop fade is vanilla 0.5s.
            float __srcCleanupNow = Time.time;
            List<int> __staleSrcKeys = null;
            foreach (var __kv in _lastPushSoundTime)
            {
                if ((__srcCleanupNow - __kv.Value) > 0.15f)
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
                        ItemMovingSoundHelper.SoftStopNetwork(__akn);
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
            bool wasOn = gen.isOn;

            if (gen.isOn != isOn)
            {
                Item item = gen.GetComponent<Item>();
                if (isOn)
                {
                    // Prefer Item.turnOn (playStart + particles + Generator.turnOn).
                    // Generator.turnOn alone never starts ItemSounds — peers heard silence.
                    if (item != null)
                        item.turnOn();
                    else
                        gen.turnOn();
                }
                else
                {
                    if (item != null)
                        item.turnOff();
                    else
                        gen.turnOff();
                }

                // Belt: if item.turnOn early-out (fuel/disabled) left isOn wrong, force gen + SFX.
                ItemSounds snd = item != null ? item.GetComponent<ItemSounds>() : null;
                if (isOn && !gen.isOn && fuel > 0f)
                {
                    gen.turnOn();
                    if (item != null) item.isOn = true;
                }
                if (wasOn != gen.isOn && snd != null)
                {
                    if (gen.isOn)
                        snd.playStart();
                    else
                        snd.playStop();
                }

                ModRuntime.LegacyInfo("[GeneratorApply] isOn " + wasOn + "→" + gen.isOn
                    + " fuel=" + fuel.ToString("F0") + " at " + gen.transform.position);
            }

            if (gen.lowPower != lowPower)
                gen.setLowPower(lowPower);
        }

        /// <summary>
        /// LightState that arrived before the Item existed (unloaded location grid).
        /// Flushed by <see cref="TryFlushPendingLights"/> once the world is ready.
        /// </summary>
        private static readonly List<LightStateMessage> _pendingLights = new List<LightStateMessage>(32);
        private const int MaxPendingLights = 64;

        /// <summary>Pending light applies (CoopPerfProbe).</summary>
        public static int PendingLightCount => _pendingLights.Count;

        /// <summary>
        /// Applies a received light on/off state change from a remote peer.
        /// Looks up the light Item by position and name, then toggles it if needed.
        /// </summary>
        /// <param name="ls">The light state message.</param>
        /// <param name="fromPeer">Sender identifier for logging.</param>
        public static void ApplyLightState(LightStateMessage ls, string fromPeer)
        {
            ApplyLightStateCore(ls, fromPeer, queueIfMissing: true);
        }

        private static float _nextPendingLightFlushTime;
        private const float PendingLightFlushInterval = 3f;
        private static float _pendingLightQueuedAt = -1f;

        /// <summary>
        /// Retry queued LightState after location/grid load.
        /// OverlapSphere only — no FindObjectsOfType (logs: findOfType=2 / 2s → maxMs=50–60 stutters).
        /// </summary>
        public static void TryFlushPendingLights()
        {
            if (_pendingLights.Count == 0)
            {
                _pendingLightQueuedAt = -1f;
                return;
            }
            if (Player.Instance == null || Core.mainMenu || Core.loadingGame) return;

            float now = Time.unscaledTime;
            if (now < _nextPendingLightFlushTime) return;
            _nextPendingLightFlushTime = now + PendingLightFlushInterval;

            if (_pendingLightQueuedAt < 0f)
                _pendingLightQueuedAt = now;
            if (now - _pendingLightQueuedAt > 30f)
            {
                ModLog.Event(LogCat.World,
                    "[LightApply] dropping " + _pendingLights.Count
                    + " pending light(s) after timeout (not in loaded grid)");
                _pendingLights.Clear();
                _pendingLightQueuedAt = -1f;
                return;
            }

            for (int i = _pendingLights.Count - 1; i >= 0; i--)
            {
                LightStateMessage ls = _pendingLights[i];
                Vector3 p = new Vector3(ls.PosX, ls.PosY, ls.PosZ);
                // Far map lights stay unloaded — drop until player walks near (re-bulk not needed).
                if (!Networking.ClientEntityInterpolationService.IsInClientInterest(p))
                {
                    _pendingLights.RemoveAt(i);
                    continue;
                }
                if (ApplyLightStateCore(ls, "pending", queueIfMissing: false))
                    _pendingLights.RemoveAt(i);
            }
            if (_pendingLights.Count == 0)
                _pendingLightQueuedAt = -1f;
        }

        /// <returns>True if applied or already matching; false if still missing.</returns>
        private static bool ApplyLightStateCore(LightStateMessage ls, string fromPeer, bool queueIfMissing)
        {
            Vector3 pos = new Vector3(ls.PosX, ls.PosY, ls.PosZ);

            // Late-join bulk sends every on-lamp on the map. Far ones are not in the client
            // grid — do not queue (was 30+ pending → periodic FoT stutters every few seconds).
            bool clientSide = ModRuntime.Network != null
                && ModRuntime.Network.Role == Networking.NetworkRole.Client;
            if (clientSide && !Networking.ClientEntityInterpolationService.IsInClientInterest(pos))
                return true; // treat as done — host will re-send if player enters area via live LightState

            Item item = FindLightByPos(pos, ls.ItemName);
            if (item == null)
            {
                // Scene lamps only — do not spawn random prefabs from ItemType (duplicates).
                if (queueIfMissing && clientSide
                    && Networking.ClientEntityInterpolationService.IsInClientInterest(pos))
                {
                    QueuePendingLight(ls);
                    ModRuntime.LegacyInfo("[LightApply] " + ls.ItemName + " not found at " + pos + " — queued");
                }
                else if (queueIfMissing && !clientSide)
                {
                    QueuePendingLight(ls);
                    ModRuntime.LegacyInfo("[LightApply] " + ls.ItemName + " not found at " + pos + " — queued");
                }
                return false;
            }

            // Already matching: treat as success (drop pending).
            if (ls.IsOn == item.isOn)
                return true;

            ModRuntime.LegacyInfo("[LightApply] " + item.name + " isOn=" + ls.IsOn + " from " + fromPeer);

            // Vanilla player path is Item.switchMe(): playSwitch() then turnOn/turnOff.
            // Remote only had turnOn/turnOff — many lamps put the click in switchSound only
            // (startSound/endSound empty), so peers saw the light change with no SFX.
            TraverseHack.ApplyingFromNetwork = true;
            try
            {
                ItemSounds sounds = item.GetComponent<ItemSounds>();
                if (sounds != null)
                    sounds.playSwitch();

                if (ls.IsOn)
                    item.turnOn();
                else
                    item.turnOff();

                // turnOff only calls playStop when hasPower — unpowered lamps still need
                // the end one-shot if switchSound was empty and endSound is set.
                if (!ls.IsOn && sounds != null && !item.hasPower
                    && !string.IsNullOrEmpty(sounds.endSound)
                    && string.IsNullOrEmpty(sounds.switchSound))
                {
                    AudioController.Play(sounds.endSound, item.transform, sounds.volumeModifier);
                }
            }
            finally
            {
                TraverseHack.ApplyingFromNetwork = false;
            }
            return true;
        }

        private static void QueuePendingLight(LightStateMessage ls)
        {
            // Dedupe by name+rounded pos — keep latest isOn.
            string key = (ls.ItemName ?? "") + "@"
                + Mathf.Round(ls.PosX) + "," + Mathf.Round(ls.PosY) + "," + Mathf.Round(ls.PosZ);
            for (int i = 0; i < _pendingLights.Count; i++)
            {
                var p = _pendingLights[i];
                string pk = (p.ItemName ?? "") + "@"
                    + Mathf.Round(p.PosX) + "," + Mathf.Round(p.PosY) + "," + Mathf.Round(p.PosZ);
                if (pk == key)
                {
                    _pendingLights[i] = ls;
                    return;
                }
            }
            if (_pendingLights.Count >= MaxPendingLights)
                _pendingLights.RemoveAt(0);
            _pendingLights.Add(ls);
        }

        /// <summary>
        /// Finds a light Item by position (+ optional name). OverlapSphere only —
        /// never FindObjectsOfType (periodic hitch source on dual-box client).
        /// </summary>
        private static Item FindLightByPos(Vector3 pos, string name)
        {
            // Prefer tight sphere; widen once if miss (still O(nearby), not full scene).
            Item best = FindLightInSphere(pos, name, 5f);
            if (best != null) return best;
            return FindLightInSphere(pos, name, 20f);
        }

        private static Item FindLightInSphere(Vector3 pos, string name, float radius)
        {
            Collider[] nearby = Physics.OverlapSphere(pos, radius);
            Item best = null;
            float bestDist = radius;
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
            return best;
        }

        /// <summary>Called from HandlePlayerAudio's IsStopSignal handler when the
        /// host broadcasts NotifyBodyPushStopped. Clears tracking tables; actual
        /// audio stop is handled by SoftStopNetwork / ForceStopByName.</summary>
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
            _thrownLights.Clear();
            _thrownById.Clear();
            _flareBurnStarts.Clear();
            _thrownLightFades.Clear();
            TrapNetworkId.ResetSession();
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
            _pendingLights.Clear();
            MovingObjectSoundService.Reset();
            DoorTracker.Clear();
            GeneratorTracker.Clear();
            CharacterTracker.Clear();
        }

        /// <summary>
        /// True while ThrownItem is mid-arc (not landed). Physics free-body stream must
        /// not own these — kinematic interp cancels velocity and lands short.
        /// </summary>
        public static bool IsInFlightThrownItem(GameObject go)
        {
            if (go == null) return false;
            ThrownItem ti = go.GetComponent<ThrownItem>();
            return ti != null && ti.thrown && !ti.onGround;
        }

        /// <summary>
        /// Strips combat + world-mutation secondaries from a thrown projectile so it can still
        /// fly and play the main explosion VFX while the host alone applies damage, gas-trail
        /// scatter, and fire. Used for client remote copies and the client's own throw.
        ///
        /// Critical for gasBomb/molotov: vanilla <c>spawnObjects()</c> uses random offsets —
        /// if both peers scatter, flame cover is not 1:1 and the client looks "wild".
        /// Nulling <c>spawnObject</c> lets host <c>ExplosionSpawnObject</c> / GasTrail apply
        /// the authoritative puddle positions (see ExplosionSpawnRecv skip when local still
        /// has spawnObject).
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
                // Keep explosionPrefab for boom VFX; kill random secondary scatter (gas puddles).
                expl.spawnObject = null;
                expl.objectAmount = 0;
            }
        }

        /// <summary>
        /// Record Flare.Start time (vanilla burn clock starts on aim when heldItem is spawned).
        /// Total light life = longevity + <see cref="FlareBurnoutFadeSec"/>.
        /// </summary>
        public static void NoteFlareBurnStart(GameObject go, float longevity)
        {
            if (go == null) return;
            float lon = longevity > 0.05f ? longevity : 3f;
            _flareBurnStarts[go.GetInstanceID()] = new FlareBurnStart
            {
                StartTime = Time.time,
                Longevity = lon
            };
        }

        /// <summary>
        /// Remaining seconds until light is fully dark (longevity + fade − elapsed).
        /// Packet LongevitySec uses this; track ExpireAt = now + (remain − fade) so fade starts on time.
        /// </summary>
        public static float GetFlareRemainingUntilDark(GameObject go, float longevityFallback = 3f)
        {
            float lon = longevityFallback > 0.05f ? longevityFallback : 3f;
            float total = lon + FlareBurnoutFadeSec;
            if (go == null)
                return total;
            if (!_flareBurnStarts.TryGetValue(go.GetInstanceID(), out FlareBurnStart b))
                return total;
            float elapsed = Time.time - b.StartTime;
            float remain = (b.Longevity + FlareBurnoutFadeSec) - elapsed;
            return Mathf.Max(0.15f, remain);
        }

        /// <summary>Seconds from now until fade should begin, given remaining-until-dark budget.</summary>
        public static float UntilFadeStart(float remainingUntilDark)
        {
            return Mathf.Max(0.05f, remainingUntilDark - FlareBurnoutFadeSec);
        }

        /// <summary>
        /// Host: track a local thrower's projectile so TickThrownLightExpiry can despawn peers
        /// (host never receives its own ThrowableSpawn).
        /// </summary>
        /// <param name="remainingUntilDark">Seconds until light is fully out (includes fade).</param>
        public static void RegisterLocalThrownLight(int throwId, GameObject go, float remainingUntilDark, string itemType)
        {
            if (go == null || throwId <= 0) return;
            ClaimFlareLifetime(go);
            float expireAt = Time.time + UntilFadeStart(remainingUntilDark);
            var track = new ThrownLightTrack
            {
                ThrowId = throwId,
                Go = go,
                ExpireAt = expireAt,
                ItemType = itemType ?? ""
            };
            // Replace existing same throwId
            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                if (_thrownLights[i].ThrowId == throwId)
                    _thrownLights.RemoveAt(i);
            }
            _thrownLights.Add(track);
            _thrownById[throwId] = track;
            ModRuntime.LegacyInfo("[ThrowableTrack] host local throwId=" + throwId
                + " type=" + itemType
                + " untilFade=" + (expireAt - Time.time).ToString("F2")
                + " untilDark=" + remainingUntilDark.ToString("F2"));
            // Event so Public/Support presets still see flare track (LegacyInfo is Dev-only).
            Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableTrack] host local throwId=" + throwId
                + " type=" + itemType
                + " untilFade=" + (expireAt - Time.time).ToString("F1")
                + "s untilDark=" + remainingUntilDark.ToString("F1") + "s claimedLifetime=1");
        }

        /// <summary>True when packet is a grounded late-join / re-sync (no flight).</summary>
        public static bool IsGroundedThrownSpawn(ThrowableSpawnMessage msg)
        {
            float velSq = msg.VelX * msg.VelX + msg.VelY * msg.VelY + msg.VelZ * msg.VelZ;
            return velSq < 0.01f && msg.Distance < 1f && !msg.HasLandTarget;
        }

        /// <summary>
        /// Ensures thrown flares have exactly one active Light2D (prefab Flare.light2D).
        /// Extra lights cause client-only "glare" halos host never had.
        /// </summary>
        private static void EnsureThrownFlareLight(GameObject go, string itemType)
        {
            if (go == null || string.IsNullOrEmpty(itemType)) return;
            if (itemType.IndexOf("flare", System.StringComparison.OrdinalIgnoreCase) < 0)
                return;

            Flare flare = go.GetComponentInChildren<Flare>(true);
            Light2D primary = flare != null ? flare.light2D : null;
            if (primary == null)
                primary = go.GetComponentInChildren<Light2D>(true);

            // Disable every other Light2D so we don't stack meshes / halos.
            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                if (primary == null)
                {
                    primary = lt;
                    continue;
                }
                if (lt != primary)
                {
                    lt.lightsPlayer = false;
                    lt.updateGraph = false;
                    try { lt.unlightGraphNodes(); } catch { /* ignore */ }
                    lt.gameObject.SetActive(false);
                }
            }

            if (primary == null)
            {
                var lightGo = new GameObject("ThrownFlareLight");
                lightGo.transform.SetParent(go.transform, false);
                lightGo.transform.localPosition = Vector3.zero;
                primary = lightGo.AddComponent<Light2D>();
                if (primary.LightMaterial == null)
                    primary.LightMaterial = Resources.Load("RadialLight") as Material;
                primary.LightRadius = 650f;
                primary.LightIntensity = 1f;
                primary.LightColor = new Color(1f, 0.5f, 0.1f);
                ModRuntime.LegacyInfo("[ThrowableSpawn] added fallback Light2D for " + itemType);
            }

            if (!primary.gameObject.activeSelf)
                primary.gameObject.SetActive(true);
            // Match vanilla prefab: lightsPlayer must draw the radial, but only once.
            primary.lightsPlayer = true;
            primary.updateGraph = true;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && !ctrl.logicLights.Contains(primary))
                ctrl.logicLights.Add(primary);

            if (ModRuntime.VerboseLogging)
                ModRuntime.LegacyInfo("[ThrowableSpawn] flare light ok " + itemType
                    + " radius=" + primary.LightRadius
                    + " intensity=" + primary.LightIntensity);
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

            bool grounded = IsGroundedThrownSpawn(msg);
            bool isFlareItem = !string.IsNullOrEmpty(msg.ItemType)
                && msg.ItemType.IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0;
            // Vanilla throwItem: heldItem.transform.position = player.position (no Y lift).
            Vector3 throwOrigin = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            Vector3 spawnPos = throwOrigin;
            GameObject go = Core.AddPrefab(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f), null);
            if (go == null)
                go = UnityEngine.Object.Instantiate(prefab, spawnPos, Quaternion.Euler(90f, msg.AimY, 0f));

            if (go == null)
            {
                ModRuntime.Log?.LogWarning("[ThrowableSpawn] failed to spawn " + msg.ItemType);
                return;
            }

            // Mirror ThrownItem.Awake ignorePlayerCollisions + avoid proxy / local player clips
            // that fire onCollide / fireOnCollideOnAnyCollision at spawn (molotov/match).
            Collider itemCol = go.GetComponent<Collider>();
            if (itemCol != null)
            {
                if (Player.Instance != null)
                {
                    Collider pc = Player.Instance.GetComponent<Collider>();
                    if (pc != null) Physics.IgnoreCollision(itemCol, pc);
                }
                if (proxyT != null && !grounded)
                {
                    Collider[] proxyCols = proxyT.GetComponentsInChildren<Collider>(true);
                    for (int i = 0; i < proxyCols.Length; i++)
                    {
                        if (proxyCols[i] != null)
                            Physics.IgnoreCollision(itemCol, proxyCols[i]);
                    }
                }
            }

            Rigidbody rb = go.GetComponent<Rigidbody>();
            ThrownItem ti = go.GetComponent<ThrownItem>();
            float distance = grounded ? 0f : Mathf.Clamp(msg.Distance, 10f, 370f);
            Vector3 vel = new Vector3(msg.VelX, msg.VelY, msg.VelZ);
            Vector3 landTarget = spawnPos;

            if (grounded)
            {
                // Late-join / re-sync: already on ground, no flight.
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                    rb.drag = 15f;
                    rb.angularDrag = 15f;
                }
                if (ti != null)
                {
                    ti.thrown = false;
                    ti.onGround = true;
                    ti.landTarget = spawnPos;
                    ti.objectThatSpawnedMe = proxyT;
                }
            }
            else
            {
                // Vanilla Player.throwItem order (decompiled):
                //  pos = player; parent=null; drag=2; dist=clamp(cursor,10,370);
                //  dir ≈ player.up (aim); landTarget = pos + dir * dist;
                //  vel = dir * dist * 2.5 (or dir * initialVelocity);
                //  thrown=true; setFallSpeed(dist); objectThatSpawnedMe = player.
                bool haveLand = msg.HasLandTarget
                    && (msg.LandX * msg.LandX + msg.LandY * msg.LandY + msg.LandZ * msg.LandZ) > 0.01f;

                Vector3 dir;
                if (haveLand)
                {
                    landTarget = new Vector3(msg.LandX, msg.LandY, msg.LandZ);
                    Vector3 toLand = landTarget - throwOrigin;
                    toLand.y = 0f;
                    float landDist = toLand.magnitude;
                    if (landDist > 1f)
                    {
                        distance = Mathf.Clamp(landDist, 10f, 370f);
                        dir = toLand / landDist;
                    }
                    else if (vel.sqrMagnitude > 0.01f)
                        dir = vel.normalized;
                    else
                        dir = Quaternion.Euler(0f, msg.AimY, 0f) * Vector3.forward;
                }
                else if (vel.sqrMagnitude > 0.01f)
                {
                    dir = vel.normalized;
                    landTarget = throwOrigin + dir * distance;
                }
                else
                {
                    dir = Quaternion.Euler(0f, msg.AimY, 0f) * Vector3.forward;
                    landTarget = throwOrigin + dir * distance;
                }

                // Vanilla throwItem force: initialVelocity==0 → dir * distance * 2.5f.
                // Always rebuild from distance + land dir so peer arc matches thrower formula.
                // (Live packet vel can disagree after drag/one-frame capture and looked "weaker".)
                float initV = ti != null ? ti.initialVelocity : 0f;
                float expectedSpeed = initV > 0f ? initV : distance * 2.5f;
                // Prefer packet speed when close to vanilla expected (thrower authority);
                // otherwise force exact formula so both sides share the same kick.
                float pktSpeed = vel.magnitude;
                if (pktSpeed < expectedSpeed * 0.85f || pktSpeed > expectedSpeed * 1.15f
                    || vel.sqrMagnitude < 1f)
                    vel = dir * expectedSpeed;
                else
                    vel = dir * pktSpeed; // keep magnitude, lock direction to land

                // Must not be kinematic — PhysicsState used to force kinematic mid-flight
                // (now excluded). Explicit unlock so a stale lock cannot kill the arc.
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.drag = 2f; // vanilla throwItem
                    // Leave prefab angularDrag — do not invent 0.05 (changes spin feel).
                    rb.velocity = vel;

                    float rotForce = ti != null ? ti.initialRotationForce : 225f;
                    if (rotForce == 0f)
                        rb.angularVelocity = Vector3.zero;
                    else
                        rb.AddTorque(0f, rotForce * 1000f, 0f);
                }

                if (ti != null)
                {
                    // Same field order as vanilla throwItem.
                    ti.landTarget = landTarget;
                    ti.thrown = true;
                    ti.onGround = false;
                    ti.objectThatSpawnedMe = proxyT;
                    // flyTime = distance/150; lands when near landTarget OR time > flyTime.
                    ti.setFallSpeed(distance);
                }

                // Drop any free-body interp that already latched onto this GO by name.
                RemoveObjectFromInterpolation(go);
                if (rb != null)
                    _clientKinematic.Remove(go.GetInstanceID());

                // ThrownItem.Awake schedules init() next frame — can zero/overwrite velocity.
                // Re-assert vanilla flight state after init so peer force matches thrower.
                Vector3 velHold = vel;
                Vector3 landHold = landTarget;
                float distHold = distance;
                Transform proxyHold = proxyT;
                GameObject goHold = go;
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                {
                    ctrl.waitFramesAndRun(() =>
                    {
                        if (goHold == null) return;
                        Rigidbody rb2 = goHold.GetComponent<Rigidbody>();
                        ThrownItem ti2 = goHold.GetComponent<ThrownItem>();
                        if (rb2 != null)
                        {
                            rb2.isKinematic = false;
                            rb2.drag = 2f;
                            rb2.velocity = velHold;
                        }
                        if (ti2 != null)
                        {
                            ti2.landTarget = landHold;
                            ti2.thrown = true;
                            ti2.onGround = false;
                            ti2.objectThatSpawnedMe = proxyHold;
                            ti2.setFallSpeed(distHold);
                        }
                        RemoveObjectFromInterpolation(goHold);
                    }, 1);
                }
            }

            Explodes expl = go.GetComponent<Explodes>();
            if (expl != null)
                expl.objectThatSpawnedMe = proxyT;

            // Client-side / visual copies: keep trajectory + FX, host alone applies combat.
            if (visualOnly)
                MuteThrownCombat(go);

            // Thrown flare: ensure ground light is visible on peers (prefab may arrive disabled).
            EnsureThrownFlareLight(go, msg.ItemType);

            // Lifetime parity: track expire for flare lights (host despawns for all).
            // LongevitySec = remaining burn including fade, from thrower's aim-start clock.
            // Keep Flare for flicker/rotation; ClaimFlareLifetime skips waitToDie (V3/V4).
            if (isFlareItem)
            {
                ClaimFlareLifetime(go);
                // Peer spawn runs Flare.Start → tweenIntensity 1→4 (ignite pulse). Mid-life
                // throws looked over-glared vs host stick already at cruise intensity (~2).
                foreach (var fl in go.GetComponentsInChildren<Flare>(true))
                {
                    if (fl != null)
                        fl.tweenIntensity = 2f;
                }
                // LongevitySec = remaining until fully dark; expire clock starts the 2s fade.
                float untilDark = msg.LongevitySec > 0.05f ? msg.LongevitySec : (3f + FlareBurnoutFadeSec);
                int throwId = msg.ThrowId;
                var track = new ThrownLightTrack
                {
                    ThrowId = throwId,
                    Go = go,
                    ExpireAt = Time.time + UntilFadeStart(untilDark),
                    ItemType = msg.ItemType
                };
                _thrownLights.Add(track);
                if (throwId > 0)
                    _thrownById[throwId] = track;
            }

            if (!visualOnly)
                Core.addToSaveable(go, isDynamic: true);
            ModRuntime.LegacyInfo("[ThrowableSpawn] spawned " + msg.ItemType
                + " throwId=" + msg.ThrowId + " life=" + msg.LongevitySec
                + " at " + spawnPos + " aimY=" + msg.AimY + " dist=" + distance
                + " vel=" + vel.magnitude.ToString("F1")
                + " land=" + landTarget
                + " grounded=" + grounded
                + " visualOnly=" + visualOnly);
        }

        /// <summary>
        /// Host: when remaining life elapses, broadcast despawn and start 2s fade (vanilla waitToDie).
        /// All roles: advance active fades.
        /// </summary>
        public static void TickThrownLightExpiry(LanNetworkManager net)
        {
            TickThrownLightFades();

            if (net == null || !net.IsConnected) return;
            if (net.Role != NetworkRole.Host) return;
            if (_thrownLights.Count == 0) return;

            float now = Time.time;
            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                var t = _thrownLights[i];
                if (t.Go == null)
                {
                    if (t.ThrowId > 0) _thrownById.Remove(t.ThrowId);
                    _thrownLights.RemoveAt(i);
                    continue;
                }
                if (now < t.ExpireAt)
                    continue;

                Vector3 pos = t.Go.transform.position;
                // Broadcast first so peers fade in parallel with host.
                if (t.ThrowId > 0)
                {
                    net.SendThrowableDespawn(new ThrowableDespawnMessage
                    {
                        ThrowId = t.ThrowId,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z
                    });
                    _thrownById.Remove(t.ThrowId);
                }
                BeginThrownLightFade(t.Go, FlareBurnoutFadeSec);
                _thrownLights.RemoveAt(i);
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] host expired throwId=" + t.ThrowId
                    + " type=" + t.ItemType + " pos=" + pos);
            }
        }

        public static void ApplyThrownDespawn(ThrowableDespawnMessage msg)
        {
            GameObject go = null;
            if (msg.ThrowId > 0 && _thrownById.TryGetValue(msg.ThrowId, out var track))
            {
                go = track.Go;
                _thrownById.Remove(msg.ThrowId);
            }
            if (go == null)
            {
                Vector3 pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
                Collider[] hits = Physics.OverlapSphere(pos, 3f);
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i] == null) continue;
                    GameObject root = hits[i].attachedRigidbody != null
                        ? hits[i].attachedRigidbody.gameObject
                        : hits[i].gameObject;
                    string n = root.name.ToLowerInvariant();
                    if (n.Contains("flare") || root.GetComponentInChildren<Light2D>(true) != null)
                    {
                        go = root;
                        break;
                    }
                }
            }

            for (int i = _thrownLights.Count - 1; i >= 0; i--)
            {
                if (_thrownLights[i].ThrowId == msg.ThrowId || _thrownLights[i].Go == go)
                    _thrownLights.RemoveAt(i);
            }

            if (go != null)
            {
                Logging.ModLog.Event(Logging.LogCat.World,
                    "[ThrowableDespawn] peer fade throwId=" + msg.ThrowId + " go=" + go.name);
                BeginThrownLightFade(go, FlareBurnoutFadeSec);
            }
            else
                Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableDespawn] no go for throwId=" + msg.ThrowId);
        }

        /// <summary>Vanilla waitToDie: ramp intensity to 0 over fadeSec, then kill lights/particles/lightFlare.</summary>
        public static void BeginThrownLightFade(GameObject go, float fadeSec = FlareBurnoutFadeSec,
            GameObject siblingDestroy = null)
        {
            if (go == null) return;
            // Already fading?
            for (int i = 0; i < _thrownLightFades.Count; i++)
            {
                if (_thrownLightFades[i].Go == go)
                    return;
            }

            Light2D[] lights = go.GetComponentsInChildren<Light2D>(true);
            float startI = 1f;
            if (lights != null && lights.Length > 0 && lights[0] != null)
                startI = Mathf.Max(0.01f, lights[0].LightIntensity);

            Logging.ModLog.Event(Logging.LogCat.World,
                "[ThrowableFade] begin go=" + go.name
                + " lights=" + (lights != null ? lights.Length : 0)
                + " startI=" + startI.ToString("F2")
                + " fadeSec=" + fadeSec.ToString("F1"));

            // Stop looping audio gently (vanilla AudioObject.Stop(1f)).
            try
            {
                foreach (var ao in go.GetComponentsInChildren<AudioObject>(true))
                {
                    if (ao != null)
                        ao.Stop(1f);
                }
                if (siblingDestroy != null)
                {
                    foreach (var ao in siblingDestroy.GetComponentsInChildren<AudioObject>(true))
                    {
                        if (ao != null)
                            ao.Stop(1f);
                    }
                }
            }
            catch { /* optional */ }

            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
            if (siblingDestroy != null)
            {
                foreach (var ps in siblingDestroy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (ps != null)
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
            }

            if (fadeSec <= 0.05f)
            {
                ExtinguishThrownLightImmediate(go);
                if (siblingDestroy != null)
                    UnityEngine.Object.Destroy(siblingDestroy);
                return;
            }

            _thrownLightFades.Add(new ThrownLightFade
            {
                Go = go,
                EndTime = Time.time + fadeSec,
                Duration = fadeSec,
                StartIntensity = startI,
                Lights = lights,
                SiblingDestroy = siblingDestroy
            });
        }

        private static void TickThrownLightFades()
        {
            if (_thrownLightFades.Count == 0) return;
            float now = Time.time;
            for (int i = _thrownLightFades.Count - 1; i >= 0; i--)
            {
                var f = _thrownLightFades[i];
                if (f.Go == null)
                {
                    if (f.SiblingDestroy != null)
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    _thrownLightFades.RemoveAt(i);
                    continue;
                }
                float left = f.EndTime - now;
                float t = 1f - Mathf.Clamp01(left / Mathf.Max(0.01f, f.Duration));
                float inten = Mathf.Lerp(f.StartIntensity, 0f, t);
                float alphaScale = 1f - t;
                if (f.Lights != null)
                {
                    for (int j = 0; j < f.Lights.Length; j++)
                    {
                        if (f.Lights[j] != null)
                            f.Lights[j].LightIntensity = inten;
                    }
                }
                // Dim lightFlare sprites with the light (vanilla waitToDie fades then destroys).
                DimFlareSprites(f.Go, alphaScale);
                if (f.SiblingDestroy != null)
                    DimFlareSprites(f.SiblingDestroy, alphaScale);

                if (now >= f.EndTime)
                {
                    ExtinguishThrownLightImmediate(f.Go);
                    if (f.SiblingDestroy != null)
                    {
                        ExtinguishThrownLightImmediate(f.SiblingDestroy);
                        UnityEngine.Object.Destroy(f.SiblingDestroy);
                    }
                    _thrownLightFades.RemoveAt(i);
                }
            }
        }

        private static void DimFlareSprites(GameObject go, float alphaScale)
        {
            if (go == null) return;
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl != null && fl.lightFlare != null)
                {
                    Color c = fl.lightFlare.color;
                    c.a = Mathf.Clamp01(alphaScale);
                    fl.lightFlare.color = c;
                }
            }
        }

        private static void ExtinguishThrownLightImmediate(GameObject go)
        {
            if (go == null) return;

            // V2: destroy glow sprites like vanilla waitToDie Destroy(lightFlare).
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
            {
                if (fl == null) continue;
                if (fl.lightFlare != null)
                {
                    UnityEngine.Object.Destroy(fl.lightFlare.gameObject);
                    fl.lightFlare = null;
                }
            }

            foreach (var lt in go.GetComponentsInChildren<Light2D>(true))
            {
                try
                {
                    lt.unlightGraphNodes();
                    var ctrl = Singleton<Controller>.Instance;
                    if (ctrl != null)
                        ctrl.logicLights.Remove(lt);
                }
                catch { /* ignore */ }
                lt.LightIntensity = 0f;
                lt.gameObject.SetActive(false);
            }
            foreach (var fl in go.GetComponentsInChildren<Flare>(true))
                UnityEngine.Object.Destroy(fl);
            foreach (var auth in go.GetComponentsInChildren<NetworkFlareLifetime>(true))
                UnityEngine.Object.Destroy(auth);
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }

            Logging.ModLog.Event(Logging.LogCat.World, "[ThrowableFade] extinguished go=" + go.name);
        }

        /// <summary>True if this GO (or child) is already in an active light fade.</summary>
        public static bool IsThrownLightFading(GameObject go)
        {
            if (go == null) return false;
            for (int i = 0; i < _thrownLightFades.Count; i++)
            {
                if (_thrownLightFades[i].Go == go || _thrownLightFades[i].SiblingDestroy == go)
                    return true;
            }
            return false;
        }

        /// <summary>Legacy name — fades then extinguishes.</summary>
        private static void ExtinguishThrownLight(GameObject go)
        {
            BeginThrownLightFade(go, FlareBurnoutFadeSec);
        }

        /// <summary>Late-join: re-send still-burning thrown flares as grounded burns (no flight).</summary>
        public static void SendActiveThrownLightsTo(LanNetworkManager net, int playerId)
        {
            if (net == null || net.Role != NetworkRole.Host || playerId <= 0)
                return;
            int sent = 0;
            float now = Time.time;
            for (int i = 0; i < _thrownLights.Count; i++)
            {
                var t = _thrownLights[i];
                if (t.Go == null || now >= t.ExpireAt) continue;
                Vector3 p = t.Go.transform.position;
                // Peer needs remaining-until-dark (= until fade start + fade).
                float remain = Mathf.Max(0.15f, (t.ExpireAt - now) + FlareBurnoutFadeSec);
                // Distance=0 + zero vel + no land → grounded spawn branch (F7).
                var msg = new ThrowableSpawnMessage
                {
                    ItemType = t.ItemType ?? "flare",
                    PosX = p.x,
                    PosY = p.y,
                    PosZ = p.z,
                    AimY = 0f,
                    Distance = 0f,
                    VelX = 0f,
                    VelY = 0f,
                    VelZ = 0f,
                    ThrowId = t.ThrowId,
                    LongevitySec = remain,
                    HasLandTarget = false
                };
                net.SendToPlayer(playerId, NetMessageType.ThrowableSpawn, w => msg.Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                sent++;
            }
            if (sent > 0)
                DWMPHorde.Logging.ModLog.Event(DWMPHorde.Logging.LogCat.Session,
                    "[BulkSync] Thrown lights (grounded) → p" + playerId + ": " + sent);
        }

        internal static List<GameObject> GetKnownTrapsSnapshot()
        {
            var list = new List<GameObject>(_knownTraps.Count);
            foreach (var go in _knownTraps.Values)
                if (go != null) list.Add(go);
            return list;
        }

        /// <summary>
        /// Resolve explode audio ID for network apply. Mushrooms often rely on
        /// explodeSound or clip ids like mushroom_explode_01 (Assets/AudioClip).
        /// </summary>
        public static string ResolveExplosionSoundId(string soundId, string objectName, Explodes target = null)
        {
            if (!string.IsNullOrEmpty(soundId))
                return soundId;
            if (target != null && !string.IsNullOrEmpty(target.explodeSound))
                return target.explodeSound;

            string n = (objectName ?? "").ToLowerInvariant();
            if (n.Contains("mushroom") || n.Contains("expobj_m") || n.Contains("exp_mushroom")
                || n.Contains("exp_bio") || n.Contains("nightmushroom"))
            {
                // Vanilla AudioToolkit ids matching exported clips.
                return "mushroom_explode_01";
            }
            return null;
        }

        /// <summary>
        /// Always-play explosion one-shot for peers. Independent of Explodes lifetime /
        /// already-activated state so mushrooms don't go silent when the object is gone.
        /// </summary>
        public static void PlayExplosionSound(Vector3 pos, string soundId, string objectName = null, Explodes target = null)
        {
            string id = ResolveExplosionSoundId(soundId, objectName, target);
            if (string.IsNullOrEmpty(id))
                return;

            // Distance cull (same budget as other world SFX).
            if (!LocalAudioService.IsNearListener(pos, LocalAudioService.DefaultMaxAudioDistance))
                return;

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Positional 3D play (parent null) — same API vanilla explode() uses.
                AudioObject ao = AudioController.Play(id, pos, null, 1f);
                if (ao == null && id == "mushroom_explode_01")
                {
                    // Alternate clip id seen in decompiled assets.
                    ao = AudioController.Play("expObj_mushroom_01", pos, null, 1f);
                    if (ao != null) id = "expObj_mushroom_01";
                }
                if (ao != null && ao.primaryAudioSource != null)
                    ao.primaryAudioSource.spatialBlend = 1f;

                ModRuntime.LegacyInfo("[ExplosionSound] play '" + id + "' at " + pos
                    + (ao != null ? " ok" : " (AudioController returned null)"));
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[ExplosionSound] failed: " + ex.Message);
            }
            finally
            {
                TraverseHack.SetExplicitFlag(prev);
            }
        }

        public static void TriggerExplosion(Vector3 pos, string objectName, bool flaming = false, string soundId = null)
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
                // explode() plays explodeSound when effect is set. If effect is null,
                // vanilla skips sound — force message/fallback audio.
                if (target.effect == null)
                    PlayExplosionSound(pos, soundId, objectName, target);
                // Proxy damage handled by ExplosionFriendlyFirePatch.Postfix on Explodes.explode()
            }
            else
            {
                ModRuntime.Log?.LogWarning("[ExplosionTrigger] no Explodes found at " + pos + " name=" + objectName);
                // Object already destroyed / out of range — still boom for the host.
                PlayExplosionSound(pos, soundId, objectName, null);
            }
        }

        /// <summary>
        /// Client-side explosion VFX without damage: mirrors vanilla onActivate visual half
        /// (spawnObjects + explosionPrefab + sound + optional destroy), never explode().
        /// Host ExplosionSpawnObject remains fallback when no local Explodes exists.
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

            // Sound is independent of visual success — play first so already-activated /
            // destroyed mushrooms still boom on peers.
            PlayExplosionSound(pos, soundId, objectName, target);

            if (target != null)
            {
                // Don't re-spawn VFX if already activated on this side
                try
                {
                    bool activated = (bool)Traverse.Create(target).Field("activated").GetValue();
                    if (activated)
                    {
                        ModRuntime.LegacyInfo("[ExplosionVisual] " + target.name + " already activated, VFX skip (sound already played)");
                        return;
                    }
                }
                catch { if (ModRuntime.VerboseLogging) ModRuntime.Log?.LogWarning("[WPSS] caught exception"); }

                try
                {
                    // Vanilla order: activated first so re-entry / ExplosionSpawnObject won't
                    // run a second full onActivate path against this component.
                    Traverse.Create(target).Field("activated").SetValue(true);

                    // 1) Secondary debris/FX (mushroom white spawnObject)
                    if (target.spawnObject != null)
                    {
                        Traverse.Create(target).Method("spawnObjects").GetValue();
                        ModRuntime.LegacyInfo("[ExplosionVisual] spawnObjects() for " + target.name + " at " + pos);
                    }

                    // 2) Main boom VFX
                    if (target.explosionPrefab != null)
                    {
                        Core.AddPrefab(target.explosionPrefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo("[ExplosionVisual] spawned local prefab " + target.explosionPrefab.name + " at " + pos);
                    }

                    // Dedupe host ExplosionSpawnObject for the secondaries we just spawned
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);

                    // 3) Match vanilla destroy without calling explode() (no double damage)
                    if (target.destroyOnExplode && target.gameObject != null)
                        target.gameObject.DestroyMe();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] failed: " + ex.Message);
                }

                return;
            }

            // Fallback: no local Explodes (already destroyed / never loaded).
            ModRuntime.LegacyInfo("[ExplosionVisual] no local Explodes for \"" + objectName + "\", using message data");

            if (!string.IsNullOrEmpty(prefabName))
            {
                try
                {
                    string[] prefixes = { "", "Items/", "FX/", "Environment/", "Particles/", "Dummies/", "Fire/", "Weapons/" };
                    UnityEngine.Object prefab = null;
                    for (int i = 0; i < prefixes.Length; i++)
                    {
                        prefab = Resources.Load("Prefabs/" + prefixes[i] + prefabName);
                        if (prefab != null) break;
                    }
                    // Also try particles/mushroom_explode style paths from ResourceManager.
                    if (prefab == null)
                        prefab = Resources.Load("Prefabs/Particles/" + prefabName);
                    if (prefab != null)
                    {
                        Core.AddPrefab(prefab, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                        ModRuntime.LegacyInfo("[ExplosionVisual] fallback prefab " + prefabName + " at " + pos);
                    }
                    else
                    {
                        Core.AddPrefab(prefabName, pos, Quaternion.Euler(90f, 0f, 0f), null, worldSpace: true);
                    }
                    DWMPHorde.Patches.ExplosionSpawnFlagTracker.NoteLocalExplodeFx(pos);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[ExplosionVisual] fallback prefab failed: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Spawns a "Items/GasolineTrail" prefab at the given world position.
        /// Used when the remote peer reports a gasoline pour. Nest-safe apply flag
        /// (IgniteGasAtPos may call this while already applying).
        /// </summary>
        public static void SpawnGasTrail(Vector3 pos)
        {
            // Dedupe: slightly wider than host scatter step to avoid double puddles under jitter.
            if (FindFlammableLiquidNear(pos, 1.15f) != null)
            {
                if (ModRuntime.VerboseLogging)
                    ModRuntime.LegacyInfo("[GasTrail] skip spawn — liquid already near " + pos);
                return;
            }

            bool prev = TraverseHack.GetExplicitFlag();
            TraverseHack.SetExplicitFlag(true);
            try
            {
                // Explicit flag so client GasolineTrail Prefix allows this network apply path.
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
                Liquid liquid = FindFlammableLiquidNear(pos, 2.25f);
                if (liquid != null)
                {
                    if (!liquid.burning)
                    {
                        liquid.startBurning();
                        ModRuntime.LegacyInfo("[GasIgnite] ignited " + liquid.name + " at " + pos);
                    }
                    return;
                }

                // Trail not found yet (packet reordering) — spawn then ignite under apply flag.
                SpawnGasTrail(pos);
                liquid = FindFlammableLiquidNear(pos, 2.25f);
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
            Liquid best = null;
            float bestD = radius + 1f;
            for (int i = 0; i < nearby.Length; i++)
            {
                if (nearby[i] == null) continue;
                Liquid liquid = nearby[i].GetComponent<Liquid>();
                if (liquid == null) liquid = nearby[i].GetComponentInParent<Liquid>();
                if (liquid == null || !liquid.flammable) continue;
                float d = Vector3.Distance(liquid.transform.position, pos);
                if (d < bestD)
                {
                    bestD = d;
                    best = liquid;
                }
            }
            return best;
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
        /// <summary>Stable trap net id (0 = position-only legacy).</summary>
        public int TrapNetId;
        /// <summary>Player occupying this trap (0 = none). Special: <see cref="OccupantSilentDisarm"/>.</summary>
        public short OccupantPlayerId;

        /// <summary>
        /// OccupantPlayerId sentinel: successful harvest/disarm — apply triggered visual only,
        /// no explosion prefab/sound (vanilla staysAfterDisarming switchToTriggered).
        /// </summary>
        public const short OccupantSilentDisarm = -2;

        /// <summary>Serializes this trap state into a network writer.</summary>
        /// <param name="w">The network writer.</param>
        public void Serialize(NetWriter w)
        {
            w.Put(PosX); w.Put(PosY); w.Put(PosZ); w.Put(Triggered);
            w.Put(TrapNetId);
            w.Put(OccupantPlayerId);
        }
        /// <summary>Deserializes a trap state from a network reader.</summary>
        /// <param name="r">The network reader.</param>
        public static TrapState Deserialize(NetReader r) => new TrapState
        {
            PosX = r.GetFloat(),
            PosY = r.GetFloat(),
            PosZ = r.GetFloat(),
            Triggered = r.GetBool(),
            TrapNetId = r.GetInt(),
            OccupantPlayerId = r.GetShort()
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

        /// <summary>
        /// True while <see cref="FastProjectile.FixedUpdate"/> is running its sweep
        /// raycast. HitscanImpactSyncPatch must not treat those as player hitscan FF
        /// (frozen/stalled pellets used to ghost-damage via that path).
        /// </summary>
        public static bool IsInsideFastProjectileRaycast = false;

        /// <summary>Clear all transient apply flags on network stop (stuck flags leak rebroadcast blocks).</summary>
        public static void ResetTransientFlags()
        {
            _explicitApplyingFromNetwork = false;
            InsideCharacterSounds = false;
            IsInsideLocalExplosion = false;
            IsInsidePlayerBulletCollision = false;
            IsInsideFastProjectileRaycast = false;
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
