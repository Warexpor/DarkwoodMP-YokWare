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
            /// <summary>Current render delay (slews toward the near/far target).</summary>
            public float delay;
        }

        private struct EntityDescriptor
        {
            public string Name;
            public string PrefabPath;
        }

        /// <summary>Name / prefab per host id; snapshots carry them only on first sends and the 1 s resync.</summary>
        private static readonly Dictionary<short, EntityDescriptor> _descriptors = new Dictionary<short, EntityDescriptor>(64);
        private static readonly HostClockEstimator _hostClock = new HostClockEstimator(); // reset-in: Reset

        private static readonly Dictionary<short, EntityInterpState> _states = new Dictionary<short, EntityInterpState>(64);
        private static readonly Dictionary<short, Vector3> _displayPositions = new Dictionary<short, Vector3>(64);
        private static readonly Dictionary<short, float> _displayRotations = new Dictionary<short, float>(64);
        private static readonly List<short> _stateKeys = new List<short>(64); // process-scoped: scratch buffer, cleared before each use
        private static readonly List<short> _staleKeys = new List<short>(16); // process-scoped: scratch buffer, cleared before each use

        private const float SnapshotInterval = 0.1f;
        private const float MaxInterpDelay = 0.3f;
        /// <summary>
        /// Render delay behind the host clock. The host sends bodies near a remote player at
        /// 20 Hz and the rest at 10 Hz; 1.5 send intervals rides out normal jitter without
        /// coasting. Bodies near this player are always in the host's 20 Hz band.
        /// </summary>
        private const float NearInterpDelay = 0.075f;
        private const float FarInterpDelay = 0.15f;
        /// <summary>Inside this XZ radius of the local listener a body is in the host's 20 Hz band (host uses 800).</summary>
        private const float NearBandDistance = 750f;
        /// <summary>Delay change per second when a body crosses bands (playback runs at most 25% fast/slow).</summary>
        private const float DelaySlewPerSec = 0.25f;
        /// <summary>Coast past the newest sample only this long (a late packet), then hold.</summary>
        private const float MaxExtrapolateSec = 0.05f;
        /// <summary>Hold-gap threshold for a body the host skipped while it rested (far band interval).</summary>
        private const float TimelineGapInterval = 0.1f;
        /// <summary>Allow save-point entities time to match before creating a phantom.</summary>
        private const float PendingMatchTimeout = 1.5f;
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

        /// <summary>
        /// Host entity broadcast radius matches client interest (~1400). Applying far
        /// snapshots called EnsureEntityAwake on WorldGrid-culled NPCs map-wide →
        /// client FPS died while co-op connected; recovered when host left (no more snaps).
        /// Only fully drive / wake entities near the local listener. Far locals are left
        /// alone (not destroyed) so claim can work when the player walks up.
        /// </summary>
        public const float ClientInterestDistance = 1400f;
        private const float ClientInterestDistanceSq = ClientInterestDistance * ClientInterestDistance;
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
        private static readonly Dictionary<short, float> _localHitEchoIgnoreUntil = new Dictionary<short, float>(16);
        private const float LocalHitEchoIgnoreSec = 0.35f;
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
            public bool Alive;
            public bool Downed;
            public byte HealthPct;
            public float TimeAdded;
        }
        private static readonly List<PendingEntry> _pendingMatches = new List<PendingEntry>(16);
        /// <summary>Scratch exclude set for phantom→real replace (no per-entity HashSet alloc).</summary>
        private static readonly HashSet<short> _phantomReplaceExclude = new HashSet<short>(); // process-scoped: scratch buffer, cleared before each use

        private static float _firstSnapshotTime;

        /// <summary>
        /// Seconds since <paramref name="hostTime"/> on the host clock estimate (about the
        /// extra delay over the best-case latency). False before the first snapshot.
        /// </summary>
        public static bool TryGetHostAge(float hostTime, out float age)
        {
            age = 0f;
            if (!_hostClock.HasEstimate)
                return false;
            age = _hostClock.ToHost(Time.unscaledTime) - hostTime;
            return true;
        }

        /// <summary>
        /// Show a host attack (EnemyAttack) on the local copy at the host's attack frame. The
        /// snapshot stream only replays a clip when its name changes, so a repeated swing
        /// (Attack1 after Attack1) needs this to be seen at all.
        /// </summary>
        public static void PresentAttackClip(Character c, short hostId, string clip, short clipFrame)
        {
            if (c == null || string.IsNullOrEmpty(clip) || !c.alive)
                return;
            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null || body.GetClipByName(clip) == null)
                return;
            if (!body.enabled)
                body.enabled = true;
            body.Play(clip);
            if (clipFrame >= 0 && body.CurrentClip != null)
            {
                int maxFrame = body.CurrentClip.frames.Length - 1;
                if (maxFrame >= 0)
                    body.SetFrame(Mathf.Clamp(clipFrame, 0, maxFrame), false);
            }
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

        public static void NoteLocalHitPresentation(Character c, short hostId)
        {
            if (c == null) return;
            short id = hostId;
            if (id == 0)
                CharacterTracker.TryGetStableId(c, out id);
            if (id != 0)
                _localHitEchoIgnoreUntil[id] = Time.unscaledTime + LocalHitEchoIgnoreSec;

            EntitySyncLog.Reaction(id.ToString(),
                "[LocalHit] presentation id=" + id + " " + (c.name ?? ""), 0.25f);

            CharacterSounds cs = c.sounds ?? c.GetComponent<CharacterSounds>();
            if (cs != null)
            {
                TraverseHack.InsideCharacterSounds = true;
                try { cs.playGetHitByAxe1(); }
                finally { TraverseHack.InsideCharacterSounds = false; }
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

        public static bool ShouldIgnoreGetHitEcho(short hostId)
        {
            if (hostId == 0) return false;
            if (!_localHitEchoIgnoreUntil.TryGetValue(hostId, out float until))
                return false;
            if (Time.unscaledTime >= until)
            {
                _localHitEchoIgnoreUntil.Remove(hostId);
                return false;
            }
            return true;
        }

        /// <summary>
        /// Death SFX on Alive→dead snap (lag-comp / Y-cull-safe). EntitySound Death is
        /// deduped via <see cref="ShouldIgnoreDeathEcho"/>.
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
            TraverseHack.InsideCharacterSounds = true;
            try { cs.play(cs.death); }
            finally { TraverseHack.InsideCharacterSounds = false; }
        }

        public static bool ShouldIgnoreDeathEcho(short hostId)
        {
            return hostId != 0 && _localDeathSoundPlayed.Contains(hostId);
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
                return true;

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
            _localHitEchoIgnoreUntil.Clear();
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
