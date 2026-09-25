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
            public bool isFirst;
            public float staleSince;
            public Rigidbody CachedRb;
        }

        private static readonly Dictionary<short, EntityInterpState> _states = new Dictionary<short, EntityInterpState>(64);
        private static readonly Dictionary<short, Vector3> _displayPositions = new Dictionary<short, Vector3>(64);
        private static readonly Dictionary<short, float> _displayRotations = new Dictionary<short, float>(64);
        private static readonly List<short> _stateKeys = new List<short>(64);
        private static readonly List<short> _staleKeys = new List<short>(16);

        private const float SnapshotInterval = 0.1f;
        private const float MaxInterpDelay = 0.3f;
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
        private static readonly HashSet<short> _phantomReplaceExclude = new HashSet<short>();

        private static float _firstSnapshotTime;

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
