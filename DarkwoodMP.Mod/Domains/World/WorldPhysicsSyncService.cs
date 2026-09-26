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
    /// <summary>
    /// Scans the local world for physics objects, doors, traps, and generators within range,
    /// builds snapshots for the host to broadcast, and applies received snapshots on clients.
    /// Provides per-frame interpolation for remote objects to smooth out network latency.
    /// </summary>
    public static partial class WorldPhysicsSyncService
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

        /// <summary>NonAlloc overlap into shared <see cref="_overlap3D"/>.</summary>
        private static int OverlapNear(Vector3 pos, float radius)
            => Physics.OverlapSphereNonAlloc(pos, radius, _overlap3D);
        private static readonly Dictionary<int, Vector3> _lastPos = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, float> _lastMoveTime = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _lastClientUpdateTime = new Dictionary<int, float>();
        private static uint _nextSnapshotSequence;
        private static int _clientUpdateCleanupCounter;
        // Tracks rigidbodies made isKinematic on the host due to client PhysicsState
        // updates. Key = InstanceID, value = Rigidbody + time to release.
        private static readonly Dictionary<int, (Rigidbody rb, float releaseTime, string objName)> _clientKinematic = new Dictionary<int, (Rigidbody rb, float releaseTime, string objName)>();
        // Keep only a one-tick cushion after movement stops. SoftStop and the
        // post-stop gate handle late packets.
        private const float BodyPushSoundHold = 0.05f;
        /// <summary>Use this path only for large corrections; normal pushes interpolate.</summary>
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
        // Count stationary ticks so tiny position changes do not restart the fade.
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
        // Position-based debounce for DestroyObjectByPos / outbound WorldObjectRemoved.
        private static readonly Dictionary<int, float> _destroyDebounce = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _outboundRemoveDebounce = new Dictionary<int, float>();
        private static readonly List<int> _outboundRemoveStaleKeys = new List<int>(8);
        private const float DestroyDebounceTime = 0.5f;
        private const float OutboundRemoveDebounceTime = 0.75f;
        /// <summary>
        /// Session consume set for non-GUID world pickups (isDroppedItem without
        /// DroppedItemIdentifier). Mirrors ConsumedDropGuids so the second peer
        /// cannot grant after the first remove is claimed locally or on the wire.
        /// </summary>
        private static readonly HashSet<int> _consumedWorldPickups = new HashSet<int>();

        /// <summary>
        /// Claim a one-shot outbound WorldObjectRemoved for this pose+name.
        /// Returns false if the same remove was already sent recently.
        /// </summary>
        public static bool TryClaimOutboundObjectRemove(float x, float y, float z, string objectName)
        {
            int key = MakePosNameKey(x, y, z, objectName);
            float now = Time.unscaledTime;
            if (_outboundRemoveDebounce.TryGetValue(key, out float last)
                && (now - last) < OutboundRemoveDebounceTime)
                return false;
            _outboundRemoveDebounce[key] = now;
            // Opportunistic prune so the dict cannot grow without bound across a long session.
            if (_outboundRemoveDebounce.Count > 64)
            {
                _outboundRemoveStaleKeys.Clear();
                foreach (var kv in _outboundRemoveDebounce)
                {
                    if (now - kv.Value >= OutboundRemoveDebounceTime)
                        _outboundRemoveStaleKeys.Add(kv.Key);
                }
                for (int i = 0; i < _outboundRemoveStaleKeys.Count; i++)
                    _outboundRemoveDebounce.Remove(_outboundRemoveStaleKeys[i]);
            }
            return true;
        }

        /// <summary>True if first claim wins (local pickup or inbound WorldObjectRemoved).</summary>
        public static bool TryConsumeWorldPickup(float x, float y, float z, string objectName)
        {
            int key = MakePosNameKey(x, y, z, objectName);
            return _consumedWorldPickups.Add(key);
        }

        public static bool IsWorldPickupConsumed(float x, float y, float z, string objectName)
        {
            return _consumedWorldPickups.Contains(MakePosNameKey(x, y, z, objectName));
        }

        public static void ResetConsumedWorldPickups()
        {
            _consumedWorldPickups.Clear();
        }

        internal static int MakePosNameKey(float x, float y, float z, string objectName)
        {
            int posKey = (int)(x * 10f) ^ ((int)(y * 10f) << 10) ^ ((int)(z * 10f) << 20);
            if (!string.IsNullOrEmpty(objectName))
                posKey ^= objectName.GetHashCode();
            return posKey;
        }

        // Cooldown tracker for host body-push sound in ApplySnapshot.
        private static readonly Dictionary<Vector3, bool> _lastDoorOpen = new Dictionary<Vector3, bool>();
        private static readonly Dictionary<Vector3, bool> _lastTrapTriggered = new Dictionary<Vector3, bool>();

        private static int _objApplyLogCounter;
        private static float _objInterpLastLogTime;
        private static float _scanRadius = 40f;
        private static float _fullResyncTimer;
        // Periodic full resync for free bodies in range.
        private static readonly float FullResyncInterval = 5f;
        private static float _lastFullRbScanTime = -999f;
        /// <summary>Full RB walk is last-resort; keep rare (overlap + name cache handle the common path).</summary>
        private const float FullRbScanMinInterval = 2f;
        /// <summary>Last successful FindOrSpawn hit by object name (distance-gated; non-unique names OK).</summary>
        private static readonly Dictionary<string, GameObject> _lastResolvedByName =
            new Dictionary<string, GameObject>(128);
        private const int MaxResolvedByName = 256;
        private const float ResolvedNameMaxDist = 25f;
        /// <summary>
        /// After motion stops, include the object briefly so peers receive a
        /// final quiet sample. Quiet samples do not refresh the motion timer.
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
        /// The network owns the lifetime; keep Flare for flicker and rotation
        /// while Harmony skips waitToDie.
        /// </summary>
        public static void ClaimFlareLifetime(GameObject go)
        {
            if (go == null) return;
            var auth = go.GetComponent<NetworkFlareLifetime>();
            if (auth == null)
                auth = go.AddComponent<NetworkFlareLifetime>();
            auth.NetworkOwnsDie = true;
            // Mark child Flare roots too so parent lookup finds the lifetime.
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
        private static readonly List<int> _stalePushSrcKeys = new List<int>(8);
        private static readonly List<int> _staleKinematicKeys = new List<int>(8);

        // Client free-body packets are ~10 Hz (0.1s). Buffer slightly longer so host
        // retargets mid-lerp instead of finishing each segment into a snap.
        private const float InterpFixedDuration = 0.2f;
        /// <summary>Host applies the client push using the same buffer.</summary>
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
    }
}
