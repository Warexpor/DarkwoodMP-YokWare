using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        private class EntityInterpState
        {
            public Vector3 previousPosition;
            public Vector3 targetPosition;
            public float previousRotY;
            public float targetRotY;
            public float arrivalTime;
            public bool hasTarget;
            public bool alive;
            /// <summary>Host has this body in the pre-death downed phase.</summary>
            public bool downed;
            /// <summary>Host aggro says this body is running off. Client AI never updates behaviour.</summary>
            public bool fleeing;
            public bool isFirst;
            public float staleSince;
            public Rigidbody CachedRb;
            /// <summary>Host-time-stamped poses; rendered <see cref="delay"/> behind the host clock.</summary>
            public readonly EntityTimeline Timeline = new EntityTimeline();
            /// <summary>Current render delay (slews toward this body's target, see <see cref="TargetDelay"/>).</summary>
            public float delay;
            /// <summary>Host time between this body's samples (smoothed; 0 until measured).</summary>
            public float interval;
            /// <summary>Shown minus sampled position after new data replaced a coast or hold; fades out.</summary>
            public Vector3 blendErr;
            /// <summary>Last frame's sampled pose (before <see cref="blendErr"/>), render time and kind.</summary>
            public Vector3 lastRaw;
            public float lastRenderT;
            public TimelinePoseKind lastKind;
            /// <summary>Host time of the newest sample when the last frame was sampled.</summary>
            public float lastNewestT;
            public bool hasRendered;
            /// <summary>Report window counters (frames while samples are arriving).</summary>
            public int statFrames, statCoast, statHold;
        }

        private struct EntityDescriptor
        {
            public string Name;
            public string PrefabPath;
            public int SaveId;
            /// <summary>The host's cosmetic roll key for the body (Sync.CosmeticRolls).</summary>
            public int LookKey;
        }

        /// <summary>Name / prefab per host id; snapshots carry them only on first sends and the 1 s resync.</summary>
        private static readonly Dictionary<short, EntityDescriptor> _descriptors = new Dictionary<short, EntityDescriptor>(64);
        private static readonly HostClockEstimator _hostClock = new HostClockEstimator(); // reset-in: Reset
        /// <summary>Lateness of each snapshot batch against the clock estimate: the render delay's margin.</summary>
        private static readonly ArrivalJitter _jitter = new ArrivalJitter(); // reset-in: Reset
        /// <summary>Real time of the local clock's zero, restarted with the host clock estimate (-1: not started).</summary>
        private static double _localEpoch = -1; // reset-in: Reset

        private static readonly Dictionary<short, EntityInterpState> _states = new Dictionary<short, EntityInterpState>(64);
        private static readonly Dictionary<short, Vector3> _displayPositions = new Dictionary<short, Vector3>(64);
        private static readonly Dictionary<short, float> _displayRotations = new Dictionary<short, float>(64);
        private static readonly List<short> _stateKeys = new List<short>(64); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<short> _staleKeys = new List<short>(16); // process-scoped: scratch buffer, cleared before each use

        private const float SnapshotInterval = 0.1f;
        /// <summary>No sample for this long: the body is resting on the host (or out of its range); the counters skip it.</summary>
        private const float MaxInterpDelay = 0.3f;
        /// <summary>Host send interval of a body near a remote player (20 Hz) and of the rest (10 Hz), before one is measured.</summary>
        private const float NearSendInterval = 0.05f;
        private const float FarSendInterval = 0.1f;
        /// <summary>
        /// Render delay behind the host clock: the body's measured send interval plus the
        /// stream's arrival lateness (<see cref="ArrivalJitter.Margin"/>) plus this safety, so
        /// the rendered moment stays behind the newest sample until the next one lands.
        /// </summary>
        private const float DelaySafety = 0.01f;
        private const float NearDelayMin = 0.05f;
        private const float NearDelayMax = 0.25f;
        private const float FarDelayMin = 0.1f;
        private const float FarDelayMax = 0.35f;
        /// <summary>Inside this XZ radius of the local listener a body is in the host's 20 Hz band (host uses 800).</summary>
        private const float NearBandDistance = 750f;
        /// <summary>Delay change per second: growing (late packets) quickly, shrinking slowly (playback runs 5% fast).</summary>
        private const float DelayGrowPerSec = 0.25f;
        private const float DelayShrinkPerSec = 0.05f;
        /// <summary>Gain of the per-body send interval estimate.</summary>
        private const float IntervalGain = 0.15f;
        /// <summary>Seconds for a blend error (new data replacing a coast) to fall to 37%.</summary>
        private const float BlendTau = 0.1f;
        /// <summary>Interval used for the first pose a timeline blends from (a body already on screen).</summary>
        private const float TimelineGapInterval = 0.1f;
        /// <summary>
        /// A body with no copy here (no save twin by id, none at the host position) gets a phantom
        /// after this long: one more retry for a copy the grid is waking. While the local world
        /// or a location is still loading it keeps waiting.
        /// </summary>
        private const float PendingMatchTimeout = 0.2f;
        private const float MatchRadius = 25f;
        /// <summary>Cap pending host-ID matches so dense nights cannot queue unbounded LateUpdate work.</summary>
        private const int MaxPendingMatches = 96;
        /// <summary>Tight position retries per LateUpdate (round-robin); avoids O(pending×chars) every frame.</summary>
        private const int PendingTightRetriesPerFrame = 12;
        /// <summary>Timeout resolve (inactive FoT / claim / phantom) budget per LateUpdate.</summary>
        private const int PendingTimeoutResolvesPerFrame = 3;
        private static int _pendingScanCursor;
        /// <summary>Claim an existing same-name NPC only when it is already at the host position.</summary>
        private const float PhantomCleanupDelay = 5f;
        /// <summary>Grace before destroying local-only NPCs inside interest.</summary>
        private const float UnmatchedCleanupDelay = 3f;
        /// <summary>Unmatched ghost scan is not a per-frame job (was GetAll+ToArray every LateUpdate).</summary>
        private const float UnmatchedCleanupInterval = 2f;
        private static float _nextUnmatchedCleanupTime;
        private const float CorpseFinalizeDelay = 1.2f;
        /// <summary>A death clip that never ends (stuck, looping by mistake) still becomes a corpse.</summary>
        private const float CorpseDeathClipMaxWait = 10f;

        /// <summary>
        /// Host entity broadcast radius matches client interest (~1400). Applying far
        /// snapshots called EnsureEntityAwake on WorldGrid-culled NPCs map-wide →
        /// client FPS died while co-op connected; recovered when host left (no more snaps).
        /// Only fully drive / wake entities near the local listener. Far locals are left
        /// alone (not destroyed) so claim can work when the player walks up.
        /// </summary>
        public const float ClientInterestDistance = GameplayConstants.EntityActivationRange;
        private const float ClientInterestDistanceSq = ClientInterestDistance * ClientInterestDistance;
        /// <summary>A driven body stops being driven only past this (the host stops sending there too).</summary>
        private const float ClientInterestLeaveSq = GameplayConstants.EntityInterestLeaveRange * GameplayConstants.EntityInterestLeaveRange;
        /// <summary>Match RemotePlayerProxy hard-snap: large XZ jumps (unload/claim/teleport).</summary>
        private const float EntityHardSnapXz = 150f;
        /// <summary>Match RemotePlayerProxy hard-snap: large Y jumps (bunker pad / knockback).</summary>
        private const float EntityHardSnapY = 40f;

        private static int _lastApplyCount;
        private static int _lastSkippedCount;
        private static int _totalApplied;
        private static int _totalSkipped;
        private static int _snapshotCount;

        /// <summary>Last ApplySnapshot applied/skipped counts (for ClientPerfProbe).</summary>
        public static int LastApplyCount => _lastApplyCount;
        public static int LastSkippedCount => _lastSkippedCount;

        private static readonly HashSet<short> _hostSyncedIds = new HashSet<short>();
        private static readonly HashSet<short> _spawnedPhantomIds = new HashSet<short>();
        private static readonly HashSet<short> _audioStoppedIds = new HashSet<short>();
        private static readonly HashSet<short> _deathAnimationPlayed = new HashSet<short>();
        private static readonly HashSet<short> _everHostSyncedIds = new HashSet<short>();
        private static readonly Dictionary<Character, float> _unmatchedSince = new Dictionary<Character, float>(64);
        private static readonly Dictionary<Character, float> _pendingCorpseSince = new Dictionary<Character, float>(16);
        private struct LocalHit
        {
            public int Count;
            public float LastAt;
        }
        /// <summary>
        /// This client's own melee hits it already showed (sound + flinch), per host id (0: target
        /// not yet matched), until the host's GetHit for each comes back stamped with this
        /// client as attacker. A hit the host rejected never comes back; it ages out.
        /// </summary>
        private static readonly Dictionary<short, LocalHit> _localHitsAwaitingEcho = new Dictionary<short, LocalHit>(16);
        private const float LocalHitEchoWindowSec = 2f;
        /// <summary>
        /// After ApplyHostDespawn: ignore EntityState for this id briefly so late
        /// snapshots cannot re-claim the deferred-Destroy GO (same-name recycle race).
        /// Host recycle grace is the primary fix; this is the client belt.
        /// </summary>
        private static readonly Dictionary<short, float> _recentlyDespawnedUntil = new Dictionary<short, float>(32);
        private const float DespawnSnapshotIgnoreSec = 2.5f;
        private static readonly HashSet<short> _localDeathSoundPlayed = new HashSet<short>();
        private static bool _receivedFirstSnapshot;
        private static uint _lastSnapshotSequence;
        private static bool _hasSnapshotSequence;

        /// <summary>Whether at least one entity snapshot has been received from the host.</summary>
        public static bool HasReceivedFirstSnapshot => _receivedFirstSnapshot;

        private struct PendingEntry
        {
            public short HostId;
            public string EntityName;
            public string PrefabPath;
            public Vector3 Position;
            public float RotY;
            public string Clip;
            public short ClipFrame;
            public bool Animating;
            public bool Alive;
            public bool Downed;
            public byte HealthPct;
            public int SaveId;
            /// <summary>Host time of the newest snapshot folded into this row.</summary>
            public float HostTime;
            public float TimeAdded;
        }
        private static readonly List<PendingEntry> _pendingMatches = new List<PendingEntry>(16);
        /// <summary>Scratch exclude set for phantom→real replace (no per-entity HashSet alloc).</summary>
        private static readonly HashSet<short> _phantomReplaceExclude = new HashSet<short>(); // process-scoped: scratch buffer, cleared before each use

        private static float _firstSnapshotTime;

        /// <summary>
        /// Local clock the host clock estimate is kept against: seconds since it started, as a
        /// float that stays precise (the process uptime as a float loses milliseconds after hours).
        /// </summary>
        private static float LocalNow()
        {
            double now = Time.unscaledTimeAsDouble;
            if (_localEpoch < 0)
                _localEpoch = now;
            return (float)(now - _localEpoch);
        }

        /// <summary>The estimated host clock now (0 before the first snapshot).</summary>
        private static float HostNowEstimate() => _hostClock.HasEstimate ? _hostClock.ToHost(LocalNow()) : 0f;

        /// <summary>
        /// Seconds since <paramref name="hostTime"/> on the host clock estimate (about the
        /// extra delay over the best-case latency). False before the first snapshot.
        /// </summary>
        public static bool TryGetHostAge(float hostTime, out float age)
        {
            age = 0f;
            if (!_hostClock.HasEstimate)
                return false;
            age = _hostClock.ToHost(LocalNow()) - hostTime;
            return true;
        }

        /// <summary>Host seconds a clip stamped <paramref name="hostTime"/> has already run when shown now (0..1).</summary>
        private static float ArrivalElapsed(float hostTime)
        {
            if (!TryGetHostAge(hostTime, out float age))
                return 0f;
            return Mathf.Clamp(age, 0f, 1f);
        }

        /// <summary>
        /// Show a host attack (EnemyAttack) on the local copy at the host's attack frame, moved on
        /// by the time the message took. The snapshot stream only replays a clip when it
        /// changes, so a repeated swing (Attack1 after Attack1) needs this to be seen at all.
        /// </summary>
        public static void PresentAttackClip(Character c, short hostId, string clip, short clipFrame, float hostTime)
        {
            if (c == null || string.IsNullOrEmpty(clip) || !c.alive)
                return;
            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null)
                return;
            tk2dSpriteAnimationClip def = body.GetClipByName(clip);
            if (def == null)
                return;
            if (!body.enabled)
                body.enabled = true;
            PlayAligned(body, def, clipFrame, ArrivalElapsed(hostTime), hostLoops: false);
            if (EntitySyncLog.On)
                EntitySyncLog.Anim(hostId.ToString(),
                    "[ClientAnim] id=" + hostId + " attack " + clip + "@" + clipFrame, 0.25f);
        }

        public static bool IsHostSynced(short id)
        {
            return _hostSyncedIds.Contains(id);
        }

        public static bool IsHostSynced(Character c)
        {
            if (c == null) return false;
            if (CharacterTracker.TryGetStableId(c, out short id))
                return _hostSyncedIds.Contains(id);
            return false;
        }

        /// <summary>
        /// True if worldPos is near the local listen camera/player (client interest).
        /// XZ only. Darkwood objects can use different Y planes, so a 3D
        /// distance check would reject valid snapshots.
        /// </summary>
        public static bool IsInClientInterest(Vector3 worldPos)
        {
            Vector3 listen = LocalAudioService.GetListenPosition();
            float dx = worldPos.x - listen.x;
            float dz = worldPos.z - listen.z;
            return dx * dx + dz * dz <= ClientInterestDistanceSq;
        }

        /// <summary>
        /// Interest with hysteresis: a body this client drives stays driven out to the leave
        /// range, the same margin the host's send range keeps, so an edge creature does not
        /// flap between driven and undriven (state wipe, hard snap, loop restart).
        /// </summary>
        private static bool IsInClientInterest(Vector3 worldPos, bool driven)
        {
            Vector3 listen = LocalAudioService.GetListenPosition();
            float dx = worldPos.x - listen.x;
            float dz = worldPos.z - listen.z;
            return dx * dx + dz * dz <= (driven ? ClientInterestLeaveSq : ClientInterestDistanceSq);
        }

        public static void NoteLocalHitPresentation(Character c, short hostId)
        {
            if (c == null) return;
            short id = hostId;
            if (id == 0)
                CharacterTracker.TryGetStableId(c, out id);
            _localHitsAwaitingEcho.TryGetValue(id, out LocalHit pending);
            if (Time.unscaledTime - pending.LastAt > LocalHitEchoWindowSec)
                pending.Count = 0;
            pending.Count++;
            pending.LastAt = Time.unscaledTime;
            _localHitsAwaitingEcho[id] = pending;

            EntitySyncLog.Reaction(id.ToString(),
                "[LocalHit] presentation id=" + id + " " + (c.name ?? ""), 0.25f);

            CharacterSounds cs = c.sounds ?? c.GetComponent<CharacterSounds>();
            if (cs != null)
            {
                bool prevInside = TraverseHack.InsideCharacterSounds;
                TraverseHack.InsideCharacterSounds = true;
                try { cs.playGetHitByAxe1(); }
                finally { TraverseHack.InsideCharacterSounds = prevInside; }
            }

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null) return;
            string hitClip = PickHitClip(body);
            if (string.IsNullOrEmpty(hitClip)) return;
            if (body.GetClipByName(hitClip) != null)
            {
                EntitySyncLog.Anim(id.ToString(),
                    "[LocalHit] speculative clip=" + hitClip + " id=" + id, 0.25f);
                body.Play(hitClip);
            }
        }

        /// <summary>
        /// True if this host GetHit is the echo of a hit this client already showed: it carries this
        /// client as attacker and one of its shown hits on that body (or on a then-unmatched one)
        /// is still waiting. Another player's hit, an AI hit or a gun hit (nothing shown) plays.
        /// </summary>
        public static bool ConsumeLocalHitEcho(short hostId, int attackerId)
        {
            var net = ModRuntime.Network;
            if (net == null || attackerId < 0 || attackerId != net.LocalPlayerId)
                return false;
            return TakeLocalHit(hostId) || TakeLocalHit(0);
        }

        private static bool TakeLocalHit(short id)
        {
            if (!_localHitsAwaitingEcho.TryGetValue(id, out LocalHit pending))
                return false;
            if (pending.Count <= 0 || Time.unscaledTime - pending.LastAt > LocalHitEchoWindowSec)
            {
                _localHitsAwaitingEcho.Remove(id);
                return false;
            }
            pending.Count--;
            if (pending.Count == 0)
                _localHitsAwaitingEcho.Remove(id);
            else
                _localHitsAwaitingEcho[id] = pending;
            return true;
        }

        /// <summary>
        /// The host's death line for this body (EntitySound Death), played once per death: the
        /// host can send it from both play and playSingleInstance. An old corpse coming into
        /// view gets none (the host's own init-dead path is soundless too).
        /// </summary>
        public static void NoteLocalDeathPresentation(Character c, short hostId)
        {
            if (c == null) return;
            short id = hostId;
            if (id == 0)
                CharacterTracker.TryGetStableId(c, out id);
            if (id == 0) return;
            if (_localDeathSoundPlayed.Contains(id)) return;

            CharacterSounds cs = c.sounds ?? c.GetComponent<CharacterSounds>();
            if (cs == null || string.IsNullOrEmpty(cs.death)) return;

            _localDeathSoundPlayed.Add(id);
            bool prevInside = TraverseHack.InsideCharacterSounds;
            TraverseHack.InsideCharacterSounds = true;
            try { cs.play(cs.death); }
            finally { TraverseHack.InsideCharacterSounds = prevInside; }
        }

        public static void NoteClientDeathForCorpse(Character c)
        {
            if (c == null) return;
            if (!_pendingCorpseSince.ContainsKey(c))
                _pendingCorpseSince[c] = Time.unscaledTime;
        }

        /// <summary>
        /// True when client may add corpse Item + set isActive=false (death anim done or delay).
        /// </summary>
        public static bool ShouldFinalizeClientCorpse(Character c)
        {
            if (c == null) return false;
            if (c.GetComponent<Item>() != null) return false;
            if (c.alive && c.Health > 0) return false;

            if (CharacterTracker.TryGetStableId(c, out short sid)
                && _deathAnimationPlayed.Contains(sid))
            {
                // Vanilla makes the body a corpse (setDeathCollider's Item) when the death clip
                // completes. An Item added mid-clip hooks the animator, and the clip's remaining
                // frame events ran as an item's: the banshee's death-scream frame played a clip
                // named "MeleeAttack1" that it does not have.
                tk2dSpriteAnimator a = c.animator;
                bool clipRunning = a != null && a.Playing && a.CurrentClip != null && !IsLoopingWrap(a.CurrentClip.wrapMode);
                bool waitedLong = _pendingCorpseSince.TryGetValue(c, out float since0)
                    && Time.unscaledTime - since0 >= CorpseDeathClipMaxWait;
                return !clipRunning || waitedLong;
            }

            if (_pendingCorpseSince.TryGetValue(c, out float since)
                && Time.unscaledTime - since >= CorpseFinalizeDelay)
                return true;

            // Host-dead before we noted die() (late join / missed patch): allow after delay
            // from first observation. TickClientCorpseSetup arms Note if needed.
            return false;
        }

        public static void ClearPendingCorpse(Character c)
        {
            if (c != null)
                _pendingCorpseSince.Remove(c);
        }

        public static void ReleaseAuthorityForPromote()
        {
            foreach (var kv in _states)
            {
                try
                {
                    Character c = CharacterTracker.FindByStableId(kv.Key);
                    if (c == null) continue;
                    Rigidbody rb = kv.Value.CachedRb != null ? kv.Value.CachedRb : c.GetComponent<Rigidbody>();
                    if (rb != null)
                        rb.isKinematic = false;
                }
                catch { /* dismantled */ }
            }
            Reset();
        }

        public static void Reset()
        {
            _states.Clear();
            _descriptors.Clear();
            _hostClock.Reset();
            _jitter.Reset();
            _localEpoch = -1;
            ResetTimelineStats();
            _clipKinds.Clear();
            _saveIdOwners.Clear();
            _displayPositions.Clear();
            _displayRotations.Clear();
            _hostSyncedIds.Clear();
            _spawnedPhantomIds.Clear();
            _audioStoppedIds.Clear();
            _deathAnimationPlayed.Clear();
            _everHostSyncedIds.Clear();
            _nextUnmatchedCleanupTime = 0f;
            _inactiveScanCache = null;
            _inactiveScanCacheTime = -999f;
            _unmatchedSince.Clear();
            _pendingCorpseSince.Clear();
            _localHitsAwaitingEcho.Clear();
            _localDeathSoundPlayed.Clear();
            _recentlyDespawnedUntil.Clear();
            _pendingMatches.Clear();
            _pendingScanCursor = 0;
            _lastApplyCount = 0;
            _lastSkippedCount = 0;
            _totalApplied = 0;
            _totalSkipped = 0;
            _receivedFirstSnapshot = false;
            _lastSnapshotSequence = 0;
            _hasSnapshotSequence = false;
            _firstSnapshotTime = 0f;
            _snapshotCount = 0;
        }

        public static void LogStats()
        {
            ModRuntime.LegacyInfo($"[Entity] stats — total applied: {_totalApplied}, total skipped: {_totalSkipped}, hostSynced: {_hostSyncedIds.Count}, pending: {_pendingMatches.Count}");
        }

    }
}
