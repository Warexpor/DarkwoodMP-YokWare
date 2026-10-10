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
        internal static bool _suppressBroadcast; // reset-in: ResetTransientFlags

        private static readonly List<WorldObjectState> _objects = new List<WorldObjectState>(); // process-scoped: per-build scratch
        private static readonly List<DoorState> _doors = new List<DoorState>(); // process-scoped: per-build scratch
        private static readonly List<TrapState> _traps = new List<TrapState>(); // process-scoped: per-build scratch
        private static readonly Collider[] _overlap3D = new Collider[2048];

        /// <summary>NonAlloc overlap into shared <see cref="_overlap3D"/>.</summary>
        private static int OverlapNear(Vector3 pos, float radius)
            => Physics.OverlapSphereNonAlloc(pos, radius, _overlap3D);

        /// <summary>
        /// Freeze a body while it follows another machine's pose. Sync let go of every body it
        /// had followed by clearing isKinematic, also bodies the game keeps kinematic itself (the
        /// tank wreck's non-convex mesh collider): PhysX rejected them on every release ("Dynamic
        /// actor with illegal collision shapes", "Non-convex MeshCollider with non-kinematic
        /// Rigidbody") and the wreck turned into a free body on the client.
        /// </summary>
        internal static void LockKinematic(Rigidbody rb)
        {
            if (rb == null || rb.isKinematic)
                return;
            _s.SyncLockedBodies.Add(rb.GetInstanceID());
            rb.isKinematic = true;
        }

        /// <summary>Let go of a body <see cref="LockKinematic"/> froze; a body kinematic by design stays so.</summary>
        internal static void ReleaseKinematic(Rigidbody rb)
        {
            if (rb == null)
                return;
            if (_s.SyncLockedBodies.Remove(rb.GetInstanceID()) && rb.isKinematic)
                rb.isKinematic = false;
        }
        /// <summary>
        /// How long one moving state from a client pusher keeps the host's scrape going. It has to
        /// outlast the gap to the next state (sent every 0.1 s, arriving with jitter); it was
        /// 0.05 s, so the scrape stopped between nearly every two states, and the post-stop
        /// suppress (0.45 s) then kept the next starts out: on the host a client's push sounded
        /// for a moment and faded. A real stop still ends it promptly: the pusher's last states
        /// are quiet ones (two quiet ticks stop it), or the states end and this runs out.
        /// </summary>
        private const float BodyPushSoundHold = 0.3f;
        /// <summary>
        /// A move this long is a teleport (a pad placed, an object carried across the map), set at
        /// once; anything shorter interpolates. It was 8 units, a fifth of a body: the copy
        /// following a push trails it by about the 0.2 s interpolation (speed x 0.2), so a light
        /// chair or stool shoved at walking pace was more than 8 behind and jumped to the pusher's
        /// pose every few states on the watcher's screen. A push never covers this in one state.
        /// </summary>
        private const float ClientPushSnapDistance = 300f;
        // Manually-managed AudioSource for host->client body-push sound.
        // We bypass AudioController for this because its pooled one-shot
        // AudioObjects get destroyed between 10Hz PhysicsState ticks,
        // making continuous playback impossible.  A looping AudioSource
        // gives us full lifecycle control.
        // Fade-out tracking for body-push AudioSources.  Stores (startVolume, endTime)
        // so UpdateObjectInterpolation can ramp volume to 0 before destroying.
        // Without this, Stop() is instant and the user hears a click.
        // Count stationary ticks so tiny position changes do not restart the fade.
        private const int StationaryFadeThreshold = 2; // ~0.2s guard at 10Hz
        // Maps object name → InstanceID so the NotifyBodyPushStopped signal can
        // look up the manual AudioSource by object name and start the fade.
        // Reverse: GID → object name, for cleanup convenience.
        // Gates against fromClient re-entry after _s.ClientKinematic release.
        // Prevents stale PhysicsState (client's 2.5s grace period) from
        // re-triggering NotifyBodyPushStarted after NotifyBodyPushStopped.
        // Value = Time.time when the gate was set (at NotifyBodyPushStopped).
        // Position-based debounce for DestroyObjectByPos / outbound WorldObjectRemoved.
        private static readonly List<PosNameKey> _destroyDebounceStaleKeys = new List<PosNameKey>(8); // process-scoped: scratch
        private static readonly List<PosNameKey> _outboundRemoveStaleKeys = new List<PosNameKey>(8); // process-scoped: scratch
        private const float DestroyDebounceTime = 0.5f;
        private const float OutboundRemoveDebounceTime = 0.75f;

        /// <summary>
        /// Claim a one-shot outbound WorldObjectRemoved for this pose+name.
        /// Returns false if the same remove was already sent recently.
        /// </summary>
        public static bool TryClaimOutboundObjectRemove(float x, float y, float z, string objectName)
        {
            PosNameKey key = MakePosNameKey(x, y, z, objectName);
            float now = Time.unscaledTime;
            if (_s.OutboundRemoveDebounce.TryGetValue(key, out float last)
                && (now - last) < OutboundRemoveDebounceTime)
                return false;
            _s.OutboundRemoveDebounce[key] = now;
            // Opportunistic prune so the dict cannot grow without bound across a long session.
            if (_s.OutboundRemoveDebounce.Count > 64)
            {
                _outboundRemoveStaleKeys.Clear();
                foreach (var kv in _s.OutboundRemoveDebounce)
                {
                    if (now - kv.Value >= OutboundRemoveDebounceTime)
                        _outboundRemoveStaleKeys.Add(kv.Key);
                }
                for (int i = 0; i < _outboundRemoveStaleKeys.Count; i++)
                    _s.OutboundRemoveDebounce.Remove(_outboundRemoveStaleKeys[i]);
            }
            return true;
        }

        /// <summary>True if first claim wins (local pickup or inbound WorldObjectRemoved).</summary>
        public static bool TryConsumeWorldPickup(float x, float y, float z, string objectName)
        {
            PosNameKey key = MakePosNameKey(x, y, z, objectName);
            if (!_s.ConsumedWorldPickups.Add(key))
                return false;
            if (_s.ConsumedWorldPickupLog.Count < 4096)
                _s.ConsumedWorldPickupLog.Add(new KeyValuePair<Vector3, string>(new Vector3(x, y, z), objectName ?? ""));
            return true;
        }

        internal static List<KeyValuePair<Vector3, string>> ConsumedWorldPickupLog => _s.ConsumedWorldPickupLog;

        /// <summary>A claim that could not be granted after all: the pickup is still there to take.</summary>
        public static void UnconsumeWorldPickup(float x, float y, float z, string objectName)
        {
            PosNameKey key = MakePosNameKey(x, y, z, objectName);
            if (!_s.ConsumedWorldPickups.Remove(key))
                return;
            for (int i = _s.ConsumedWorldPickupLog.Count - 1; i >= 0; i--)
            {
                var e = _s.ConsumedWorldPickupLog[i];
                if (MakePosNameKey(e.Key.x, e.Key.y, e.Key.z, e.Value).Equals(key))
                {
                    _s.ConsumedWorldPickupLog.RemoveAt(i);
                    break;
                }
            }
        }

        public static bool IsWorldPickupConsumed(float x, float y, float z, string objectName)
        {
            return _s.ConsumedWorldPickups.Contains(MakePosNameKey(x, y, z, objectName));
        }

        public static void ResetConsumedWorldPickups()
        {
            _s.ConsumedWorldPickups.Clear();
            _s.ConsumedWorldPickupLog.Clear();
        }

        /// <summary>Position quantized to 0.1 u and packed without overlap, plus the normalized name.</summary>
        internal readonly struct PosNameKey : IEquatable<PosNameKey>
        {
            private readonly long _pos;
            private readonly string _name;

            internal PosNameKey(long pos, string name)
            {
                _pos = pos;
                _name = name ?? "";
            }

            public bool Equals(PosNameKey other) =>
                _pos == other._pos && string.Equals(_name, other._name, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is PosNameKey k && Equals(k);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_pos.GetHashCode() * 397) ^ StringComparer.Ordinal.GetHashCode(_name ?? "");
                }
            }
        }

        internal static PosNameKey MakePosNameKey(float x, float y, float z, string objectName)
        {
            // x/z: 24 bits each (+-838 km at 0.1 u), y: 16 bits (+-3.2 km); fields never overlap.
            long qx = (long)Mathf.Round(x * 10f) & 0xFFFFFF;
            long qz = (long)Mathf.Round(z * 10f) & 0xFFFFFF;
            long qy = (long)Mathf.Round(y * 10f) & 0xFFFF;
            long packed = (qx << 40) | (qz << 16) | qy;
            return new PosNameKey(packed, NormalizeObjectName(objectName));
        }

        // Cooldown tracker for host body-push sound in ApplySnapshot.

        private static int _objApplyLogCounter; // process-scoped: log throttle
        private static float _objInterpLastLogTime; // process-scoped: log throttle
        private static float _scanRadius = 40f; // process-scoped: tuning constant
        // Periodic full resync for free bodies in range.
        private static readonly float FullResyncInterval = 5f;
        /// <summary>Full RB walk is last-resort; keep rare (overlap + name cache handle the common path).</summary>
        private const float FullRbScanMinInterval = 2f;
        private const int MaxResolvedByName = 256;
        private const float ResolvedNameMaxDist = 25f;
        /// <summary>
        /// After motion stops, include the object briefly so peers receive a
        /// final quiet sample. Quiet samples do not refresh the motion timer.
        /// </summary>
        private const float QuietConfirmWindow = 0.15f;
        private const float TrapResultForgetSeconds = 60f;

        private struct TrapClassification
        {
            public bool IsTrap;
            public float LastSeen;
        }

        /// <summary>When a door / trap / generator state key was last scanned and last sent.</summary>
        private struct StateKeyAge
        {
            public float LastSeen;
            public float LastSent;
        }
        private static readonly List<Vector3> _stateKeyScratch = new List<Vector3>(16); // process-scoped: scratch
        /// <summary>Keys not scanned for this long (destroyed / out of range) are forgotten.</summary>
        private const float StateKeyForgetSeconds = 30f;
        /// <summary>A still-scanned key is re-sent once its last send is this old, a few per pass.</summary>
        private const float StateKeyResyncSeconds = 10f;
        private const int StateKeyResyncPerPass = 4;

        /// <summary>Vanilla Flare.waitToDie fade length after longevity elapses.</summary>
        public const float FlareBurnoutFadeSec = 2f;

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

        private static readonly List<GeneratorState> _generators = new List<GeneratorState>(); // process-scoped: per-build scratch
        private static readonly List<int> _stalePushSrcKeys = new List<int>(8); // process-scoped: scratch
        private static readonly List<int> _staleKinematicKeys = new List<int>(8); // process-scoped: scratch

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
        private static readonly List<int> _objectInterpDeadKeys = new List<int>(); // process-scoped: scratch
        private static readonly List<int> _objectInterpKeys = new List<int>(); // process-scoped: scratch



        /// <summary>
        /// OverlapSphere around one center; adds free rigidbodies + detects traps.
        /// Dedupes by GameObject instance id across multi-center host scans.
        /// Hot path: cheap name + motion gate first; heavy GetComponent only for candidates.
        /// </summary>
    }
}
