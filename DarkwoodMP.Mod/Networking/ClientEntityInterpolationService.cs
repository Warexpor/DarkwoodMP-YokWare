using DWMPHorde.Audio;
using DWMPHorde.Sync;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static class ClientEntityInterpolationService
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
        private const float PendingMatchTimeout = 0.2f;
        private const float MatchRadius = 15f;
        private const float PhantomCleanupDelay = 5f;
        private const float UnmatchedCleanupDelay = 1f;
        /// <summary>Unmatched ghost scan is not a per-frame job (was GetAll+ToArray every LateUpdate).</summary>
        private const float UnmatchedCleanupInterval = 2f;
        private static float _nextUnmatchedCleanupTime;

        /// <summary>
        /// Host entity broadcast radius is ~3500. Applying those far snapshots called
        /// EnsureEntityAwake (SetActive + isActive) on WorldGrid-culled NPCs map-wide →
        /// client FPS died while co-op connected; recovered when host left (no more snaps).
        /// Only fully drive / wake entities near the local listener.
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
        private static bool _receivedFirstSnapshot;

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
            public float TimeAdded;
        }
        private static readonly List<PendingEntry> _pendingMatches = new List<PendingEntry>(16);

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
        /// XZ only — Darkwood player Y is often ~-1984 while NPC/object Y differs by thousands;
        /// 3D distance was skipping every entity snap (logs: applied=0 skip=N while co-op live).
        /// </summary>
        public static bool IsInClientInterest(Vector3 worldPos)
        {
            Vector3 listen = LocalAudioService.GetListenPosition();
            float dx = worldPos.x - listen.x;
            float dz = worldPos.z - listen.z;
            return dx * dx + dz * dz <= ClientInterestDistanceSq;
        }

        public static void ApplySnapshot(EntityStateMessage msg)
        {
            if (msg.Entities == null || msg.Entities.Length == 0)
            {
                if (_lastApplyCount > 0)
                    ModRuntime.LegacyInfo($"[Entity] received empty snapshot (no entities)");
                _lastApplyCount = 0;
                return;
            }

            bool wasFirst = !_receivedFirstSnapshot;
            _receivedFirstSnapshot = true;
            if (wasFirst)
                _firstSnapshotTime = Time.time;

            int applied = 0;
            int skipped = 0;
            // High-freq dumps only on full Trace preset (Dev playtest must stay light).
            bool dump = ModRuntime.VerboseLogging && ((_snapshotCount + 1) % 50 == 0);
            System.Text.StringBuilder sb = dump ? new System.Text.StringBuilder() : null;
            System.Text.StringBuilder skippedSb = dump ? new System.Text.StringBuilder() : null;

            for (int i = 0; i < msg.Entities.Length; i++)
            {
                EntitySnapshotNet e = msg.Entities[i];
                Vector3 targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);

                // Far host-range snaps: do not EnsureEntityAwake / spawn phantoms map-wide.
                if (!IsInClientInterest(targetPos))
                {
                    StopDriving(e.Index);
                    skipped++;
                    continue;
                }

                if (sb != null)
                {
                    if (sb.Length == 0)
                        sb.Append("[Entity] snapshot IDs: ");
                    sb.Append(e.Index);
                    sb.Append(' ');
                }

                Character c = CharacterTracker.FindByStableId(e.Index);
                if (c != null)
                {
                    // Verify the matched entity's name matches — FindByStableId can return
                    // the wrong entity when local stable IDs collide with host IDs.
                    string cname = c.name;
                    if (cname.EndsWith("(Clone)"))
                        cname = cname.Substring(0, cname.Length - 7);
                    bool nameMatches = string.Equals(cname, e.EntityName, System.StringComparison.OrdinalIgnoreCase);

                    if (nameMatches)
                    {
                        // If the matched entity is a phantom, check if a real local entity
                        // now exists nearby (e.g. world chunk just loaded). If so, replace
                        // the phantom with the real entity to avoid duplicates.
                        if (_spawnedPhantomIds.Contains(e.Index))
                        {
                            HashSet<short> exclude = new HashSet<short>(_hostSyncedIds) { e.Index };
                            Character real = CharacterTracker.FindByPositionAndName(targetPos, e.EntityName, MatchRadius, exclude);
                            if (real != null)
                            {
                                CharacterTracker.AssignId(real, e.Index);
                                _hostSyncedIds.Add(e.Index);
                                _everHostSyncedIds.Add(e.Index);
                                _spawnedPhantomIds.Remove(e.Index);
                                Object.Destroy(c.gameObject);
                                c = real;
                                if (ModRuntime.VerboseLogging)
                                    ModRuntime.LegacyInfo($"[Entity] replaced phantom with real entity: {e.EntityName}(id={e.Index})");
                            }
                        }
                        _hostSyncedIds.Add(e.Index);
                        _everHostSyncedIds.Add(e.Index);
                        UpdateInterpolation(c, e, targetPos, ref applied);
                        continue;
                    }

                    // Name mismatch — the stable ID hit a wrong local entity.
                    if (ModRuntime.VerboseLogging || (_snapshotCount % 100 == 0))
                        ModRuntime.LegacyInfo($"[Entity] stable ID collision: id={e.Index} found {c.name} but expected {e.EntityName}");
                    CharacterTracker.ClearId(c);
                }

                // Not found by ID — try position + name matching
                c = CharacterTracker.FindByPositionAndName(targetPos, e.EntityName, MatchRadius, _hostSyncedIds);
                if (c != null)
                {
                    CharacterTracker.AssignId(c, e.Index);
                    _hostSyncedIds.Add(e.Index);
                    _everHostSyncedIds.Add(e.Index);
                    EnsureEntityAwake(c);
                    if (wasFirst || ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Entity] matched by position: {e.EntityName}(id={e.Index}) at ({targetPos.x:F1},{targetPos.z:F1})");
                    UpdateInterpolation(c, e, targetPos, ref applied);
                    continue;
                }

                // Couldn't find locally — one pending entry per host id (no 10 Hz duplicates).
                if (!TryUpdatePending(e, targetPos))
                {
                    _pendingMatches.Add(new PendingEntry
                    {
                        HostId = e.Index,
                        EntityName = e.EntityName,
                        PrefabPath = e.PrefabPath,
                        Position = targetPos,
                        RotY = e.RotY,
                        Clip = e.Clip,
                        ClipFrame = e.ClipFrame,
                        Alive = e.Alive,
                        TimeAdded = Time.time
                    });
                }
                skipped++;
                if (skippedSb != null)
                {
                    if (skippedSb.Length == 0)
                        skippedSb.Append("[Entity] PENDING: ");
                    skippedSb.Append("id=");
                    skippedSb.Append(e.Index);
                    skippedSb.Append('(');
                    skippedSb.Append(e.EntityName);
                    skippedSb.Append(") ");
                }
            }

            // Do NOT mass-Destroy "unmatched" save NPCs on first snapshot.
            // Host only streams nearby entities (~4 in logs); the rest of the save
            // is still valid world state. Old purge killed ~58 Characters in one frame
            // (client enter FPS crater) and left holes until host walked near and
            // re-spawned phantoms. Local-only AI is already frozen on client.

            _lastApplyCount = applied;
            _lastSkippedCount = skipped;
            _totalApplied += applied;
            _totalSkipped += skipped;

            _snapshotCount++;
            if (dump && sb != null)
            {
                if (sb.Length > 0)
                    ModRuntime.LegacyInfo(sb.ToString());

                Character[] all = CharacterTracker.GetAll();
                var tb = new System.Text.StringBuilder();
                tb.Append($"[Entity] tracker has {all.Length} chars: ");
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null)
                        tb.Append($"{CharacterTracker.GetStableId(all[i])}({all[i].name}) ");
                }
                ModRuntime.LegacyInfo(tb.ToString());
                ModRuntime.LegacyInfo($"[Entity] applied={applied} pending={_pendingMatches.Count} hostSynced={_hostSyncedIds.Count}");
                if (skippedSb != null && skippedSb.Length > 0)
                    ModRuntime.LegacyInfo(skippedSb.ToString());
            }
        }

        /// <summary>Update existing pending row for host id; false if not yet pending.</summary>
        private static bool TryUpdatePending(EntitySnapshotNet e, Vector3 targetPos)
        {
            for (int i = 0; i < _pendingMatches.Count; i++)
            {
                PendingEntry p = _pendingMatches[i];
                if (p.HostId != e.Index)
                    continue;
                p.Position = targetPos;
                p.RotY = e.RotY;
                p.Clip = e.Clip;
                p.ClipFrame = e.ClipFrame;
                p.Alive = e.Alive;
                p.EntityName = e.EntityName;
                p.PrefabPath = e.PrefabPath;
                // Keep TimeAdded so timeout still fires from first sighting.
                _pendingMatches[i] = p;
                return true;
            }
            return false;
        }

        /// <summary>Stop interpolating a host id (left interest radius or promote).</summary>
        private static void StopDriving(short hostId)
        {
            if (_states.TryGetValue(hostId, out var state))
            {
                state.hasTarget = false;
                if (state.CachedRb != null)
                {
                    try { state.CachedRb.isKinematic = false; }
                    catch { /* destroyed */ }
                }
                _states.Remove(hostId);
            }
            _displayPositions.Remove(hostId);
            _displayRotations.Remove(hostId);
            // Keep _hostSyncedIds / ever so we don't thrash rematch when they re-enter range.
        }

        private static void UpdateInterpolation(Character c, EntitySnapshotNet e, Vector3 targetPos, ref int applied)
        {
            EnsureEntityAwake(c);

            // Disable CharacterSounds on first snapshot — client AI is frozen, so
            // local loops would never stop. Host broadcasts AI SFX via EntitySound
            // (growl/idle/attack/gethit/death) and enemy footsteps via PlayerAudio.
            // HandleEntitySound still calls CharacterSounds methods directly while
            // the component stays disabled (method calls do not require enabled).
            if (_audioStoppedIds.Add(e.Index))
            {
                CharacterSounds cs = c.GetComponent<CharacterSounds>();
                if (cs != null)
                {
                    cs.destroySounds();
                    cs.enabled = false;
                }
            }

            if (!_states.TryGetValue(e.Index, out var state))
            {
                state = new EntityInterpState { isFirst = true };
                _states[e.Index] = state;
            }
            state.staleSince = 0f;

            if (state.isFirst)
            {
                _displayPositions[e.Index] = c.transform.position;
                _displayRotations[e.Index] = c.transform.eulerAngles.y;
                state.isFirst = false;
            }

            state.previousPosition = _displayPositions[e.Index];
            state.previousRotY = _displayRotations[e.Index];
            state.targetPosition = targetPos;
            state.targetRotY = e.RotY;
            state.arrivalTime = Time.time;
            state.hasTarget = true;

            if (!e.Alive && c.alive)
            {
                ModRuntime.LegacyInfo($"[Entity] DETECTED DEATH: {c.name}(id={e.Index})");
                c.die();
                // Client Character.Update (processAnims) is AI-suppressed — die() never
                // starts the death clip. Host often later sends empty Clip after
                // destroyComponents2 nukes the animator. Play death anim locally now.
                EnsureDeathAnimation(c, e.Index, e.Clip, e.ClipFrame);
            }

            state.alive = e.Alive;

            // 1.2b presentation: clip + death pose (see ApplyEntityPresentation).
            ApplyEntityPresentation(c, e.Index, e.Clip, e.ClipFrame, e.Alive);

            applied++;
        }

        /// <summary>
        /// Drive client-side entity anim from host snapshot (1.2b).
        /// - On clip change: Play + snap to host frame (attack/hitreact/death start aligned).
        /// - Alive + same clip: let tk2d advance at natural FPS (no 10 Hz SetFrame scrub —
        ///   that killed attack windups and made hitreacts stutter).
        /// - Dead: play death clip once from frame 0; do not re-lock to empty host clip.
        /// Applies to root animator and Character.legsAnimator when present.
        /// </summary>
        private static void ApplyEntityPresentation(Character c, short entityId, string clip, short clipFrame, bool alive)
        {
            if (c == null) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);

            if (!alive)
            {
                // Always ensure death presentation (covers pending-match path + late packets).
                EnsureDeathAnimation(c, entityId, clip, clipFrame);
                return;
            }

            ApplyClipToAnimator(body, entityId, clip, clipFrame, alive: true, trackDeath: false);

            tk2dSpriteAnimator legs = c.legsAnimator;
            if (legs != null && legs != body)
                ApplyClipToAnimator(legs, entityId, clip, clipFrame, alive: true, trackDeath: false);
        }

        private static tk2dSpriteAnimator ResolveBodyAnimator(Character c)
        {
            if (c == null) return null;
            tk2dSpriteAnimator body = null;
            try
            {
                body = c.animator;
            }
            catch { /* dismantled */ }
            if (body == null)
                body = c.GetComponent<tk2dSpriteAnimator>();
            return body;
        }

        /// <summary>
        /// True if clip name is a real death / cut-in-half presentation (not idle/walk).
        /// </summary>
        private static bool IsDeathClipName(string clip)
        {
            if (string.IsNullOrEmpty(clip)) return false;
            if (clip.IndexOf("Death", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (clip.Equals("Cut_half", System.StringComparison.OrdinalIgnoreCase))
                return true;
            if (clip.Equals("BeartrapDeath", System.StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        private static string ResolveDeathClipName(Character c, tk2dSpriteAnimator anim, string hostClip)
        {
            if (anim == null) return null;

            if (IsDeathClipName(hostClip) && anim.GetClipByName(hostClip) != null)
                return hostClip;

            try
            {
                string deathAnim = Traverse.Create(c).Field("deathAnim").GetValue<string>();
                if (string.IsNullOrEmpty(deathAnim))
                {
                    Traverse.Create(c).Method("getDeathAnims").GetValue();
                    deathAnim = Traverse.Create(c).Field("deathAnim").GetValue<string>();
                }
                if (!string.IsNullOrEmpty(deathAnim) && anim.GetClipByName(deathAnim) != null)
                    return deathAnim;
            }
            catch { /* field/method missing on odd prefabs */ }

            string[] fallbacks = { "Death1", "Death2", "Death3", "Death", "Cut_half", "BeartrapDeath" };
            for (int i = 0; i < fallbacks.Length; i++)
            {
                if (anim.GetClipByName(fallbacks[i]) != null)
                    return fallbacks[i];
            }
            return null;
        }

        /// <summary>
        /// Start the death presentation once. Client AI-skip blocks processAnims which
        /// is what vanilla uses to Play(deathAnim). Host clip is often already empty
        /// by the time Alive=false arrives (animator destroyed post-death on host).
        /// </summary>
        private static void EnsureDeathAnimation(Character c, short entityId, string hostClip, short hostFrame)
        {
            if (c == null) return;
            if (_deathAnimationPlayed.Contains(entityId)) return;

            tk2dSpriteAnimator body = ResolveBodyAnimator(c);
            if (body == null) return;

            if (!body.enabled)
                body.enabled = true;

            string deathClip = ResolveDeathClipName(c, body, hostClip);
            if (string.IsNullOrEmpty(deathClip))
            {
                ModRuntime.LegacyInfo($"[Entity] death anim missing for {c.name}(id={entityId}) hostClip={hostClip}");
                // Still mark so we don't spam; corpse stays lootable via die() patch.
                _deathAnimationPlayed.Add(entityId);
                return;
            }

            body.Play(deathClip);
            // Mid-death join: snap to host frame. Fresh kill: play from start.
            if (IsDeathClipName(hostClip) && hostFrame > 0 && body.CurrentClip != null)
            {
                int maxFrame = body.CurrentClip.frames.Length - 1;
                if (maxFrame >= 0)
                    body.SetFrame(Mathf.Clamp(hostFrame, 0, maxFrame), false);
            }

            _deathAnimationPlayed.Add(entityId);
            ModRuntime.LegacyInfo($"[Entity] death anim Play({deathClip}) on {c.name}(id={entityId})");
        }

        private static void ApplyClipToAnimator(
            tk2dSpriteAnimator anim, short entityId, string clip, short clipFrame, bool alive, bool trackDeath)
        {
            if (anim == null) return;

            // Dead entities are handled exclusively by EnsureDeathAnimation.
            if (!alive)
                return;

            if (!string.IsNullOrEmpty(clip))
            {
                bool clipChanged = anim.CurrentClip == null || anim.CurrentClip.name != clip;
                if (clipChanged && anim.GetClipByName(clip) != null)
                {
                    anim.Play(clip);

                    // Align only at clip boundaries (start of attack/hitreact).
                    if (clipFrame >= 0 && anim.CurrentClip != null)
                    {
                        int maxFrame = anim.CurrentClip.frames.Length - 1;
                        if (maxFrame >= 0)
                            anim.SetFrame(Mathf.Clamp(clipFrame, 0, maxFrame), false);
                    }
                }
                // Alive + same clip: natural playback — do not SetFrame every tick.
            }
            else if (!anim.Playing)
            {
                string idleClip = null;
                try
                {
                    Character ch = anim.GetComponent<Character>()
                        ?? anim.GetComponentInParent<Character>();
                    if (ch != null)
                        idleClip = Traverse.Create(ch).Field("idleAni").GetValue<string>();
                }
                catch { /* ignore */ }

                if (!string.IsNullOrEmpty(idleClip) && anim.GetClipByName(idleClip) != null)
                    anim.Play(idleClip);
            }
        }

        public static void TickLateUpdate()
        {
            float now = Time.time;

            // 1. Retry pending matches
            for (int i = _pendingMatches.Count - 1; i >= 0; i--)
            {
                PendingEntry p = _pendingMatches[i];

                // Never FindObjectsOfType-activate or phantom-spawn far map entities.
                if (!IsInClientInterest(p.Position))
                {
                    _pendingMatches.RemoveAt(i);
                    continue;
                }

                // Try position matching again (tight radius)
                Character c = CharacterTracker.FindByPositionAndName(p.Position, p.EntityName, MatchRadius, _hostSyncedIds);
                if (c != null)
                {
                    CharacterTracker.AssignId(c, p.HostId);
                    _hostSyncedIds.Add(p.HostId);
                    _everHostSyncedIds.Add(p.HostId);
                    EnsureEntityAwake(c);
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Entity] pending matched (tight): {p.EntityName}(id={p.HostId})");

                    if (!_states.TryGetValue(p.HostId, out var state))
                    {
                        state = new EntityInterpState { isFirst = true };
                        _states[p.HostId] = state;
                    }
                    state.staleSince = 0f;

                    _displayPositions[p.HostId] = c.transform.position;
                    _displayRotations[p.HostId] = c.transform.eulerAngles.y;
                    state.isFirst = false;

                    state.previousPosition = c.transform.position;
                    state.previousRotY = c.transform.eulerAngles.y;
                    state.targetPosition = p.Position;
                    state.targetRotY = p.RotY;
                    state.arrivalTime = now;
                    state.hasTarget = true;
                    state.alive = p.Alive;

                    if (!p.Alive && c.alive)
                        c.die();

                    ApplyEntityPresentation(c, p.HostId, p.Clip, p.ClipFrame, p.Alive);

                    _pendingMatches.RemoveAt(i);
                    continue;
                }

                // Timeout — first try to find and activate a real (inactive) entity,
                // then fall back to spawning a phantom.
                if (now - p.TimeAdded > PendingMatchTimeout)
                {
                    if (_hostSyncedIds.Contains(p.HostId))
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Entity] dropping pending (already host-synced): {p.EntityName}(id={p.HostId})");
                        _pendingMatches.RemoveAt(i);
                        continue;
                    }

                    // Try to find an inactive character in the scene with matching name.
                    // This avoids creating duplicates for save-related entities that
                    // exist on the client but are deactivated by WorldGrid culling.
                    Character inactive = FindInactiveCharacter(p.EntityName, p.Position, MatchRadius * 2f);
                    if (inactive != null)
                    {
                        CharacterTracker.Add(inactive);
                        CharacterTracker.AssignId(inactive, p.HostId);
                        _hostSyncedIds.Add(p.HostId);
                        _everHostSyncedIds.Add(p.HostId);
                        EnsureEntityAwake(inactive);
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Entity] activated existing entity: {p.EntityName}(id={p.HostId})");

                        if (!_states.TryGetValue(p.HostId, out var state))
                        {
                            state = new EntityInterpState { isFirst = true };
                            _states[p.HostId] = state;
                        }
                        state.staleSince = 0f;

                        _displayPositions[p.HostId] = inactive.transform.position;
                        _displayRotations[p.HostId] = inactive.transform.eulerAngles.y;
                        state.isFirst = false;

                        state.previousPosition = inactive.transform.position;
                        state.previousRotY = inactive.transform.eulerAngles.y;
                        state.targetPosition = p.Position;
                        state.targetRotY = p.RotY;
                        state.arrivalTime = now;
                        state.hasTarget = true;
                        state.alive = p.Alive;

                        if (!p.Alive && inactive.alive)
                            inactive.die();

                        ApplyEntityPresentation(inactive, p.HostId, p.Clip, p.ClipFrame, p.Alive);

                        _pendingMatches.RemoveAt(i);
                        continue;
                    }

                    c = SpawnEntityLocally(p.EntityName, p.PrefabPath, p.Position, p.RotY);
                    if (c != null)
                    {
                        CharacterTracker.AssignId(c, p.HostId);
                        _hostSyncedIds.Add(p.HostId);
                        _everHostSyncedIds.Add(p.HostId);
                        _spawnedPhantomIds.Add(p.HostId);
                        EnsureEntityAwake(c);
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Entity] pending spawned: {p.EntityName}(id={p.HostId})");

                        if (!_states.TryGetValue(p.HostId, out var state))
                        {
                            state = new EntityInterpState { isFirst = true };
                            _states[p.HostId] = state;
                        }
                        state.staleSince = 0f;

                        _displayPositions[p.HostId] = p.Position;
                        _displayRotations[p.HostId] = p.RotY;
                        c.transform.position = p.Position;
                        state.isFirst = false;

                        state.previousPosition = p.Position;
                        state.previousRotY = p.RotY;
                        state.targetPosition = p.Position;
                        state.targetRotY = p.RotY;
                        state.arrivalTime = now;
                        state.hasTarget = true;
                        state.alive = p.Alive;

                        if (!p.Alive && c.alive)
                            c.die();

                        ApplyEntityPresentation(c, p.HostId, p.Clip, p.ClipFrame, p.Alive);
                    }

                    _pendingMatches.RemoveAt(i);
                }
            }

            // 2. Update interpolation + track stale entities
            _staleKeys.Clear();
            _stateKeys.Clear();
            _stateKeys.AddRange(_states.Keys);

            for (int si = 0; si < _stateKeys.Count; si++)
            {
                short id = _stateKeys[si];
                EntityInterpState state = _states[id];

                if (!state.hasTarget)
                {
                    bool isPhantom = _spawnedPhantomIds.Contains(id);
                    if (isPhantom && state.staleSince > 0f && now - state.staleSince > PhantomCleanupDelay)
                    {
                        Character c = CharacterTracker.FindByStableId(id);
                        if (c != null)
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.LegacyInfo($"[Entity] destroying phantom: id={id}");
                            Object.Destroy(c.gameObject);
                        }
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
                        _hostSyncedIds.Remove(id);
                        _spawnedPhantomIds.Remove(id);
                    }
                    else if (!isPhantom && state.staleSince > 0f && now - state.staleSince > PhantomCleanupDelay)
                    {
                        Character c = CharacterTracker.FindByStableId(id);
                        if (c != null)
                        {
                            Rigidbody rb = c.GetComponent<Rigidbody>();
                            if (rb != null)
                                rb.isKinematic = false;
                        }
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
                        _hostSyncedIds.Remove(id);
                    }
                    continue;
                }

                Character tracked = CharacterTracker.FindByStableId(id);
                if (tracked == null)
                {
                    _staleKeys.Add(id);
                    _displayPositions.Remove(id);
                    _displayRotations.Remove(id);
                    _hostSyncedIds.Remove(id);
                    _spawnedPhantomIds.Remove(id);
                    continue;
                }

                float elapsed = now - state.arrivalTime;

                if (elapsed > MaxInterpDelay)
                {
                    _displayPositions[id] = state.targetPosition;
                    _displayRotations[id] = state.targetRotY;
                    state.hasTarget = false;
                    state.staleSince = now;
                }
                else if (elapsed > SnapshotInterval)
                {
                    float extrapT = elapsed - SnapshotInterval;
                    Vector3 velocity = (state.targetPosition - state.previousPosition) / SnapshotInterval;
                    _displayPositions[id] = state.targetPosition + velocity * extrapT;
                    _displayRotations[id] = state.targetRotY;
                }
                else
                {
                    float t = elapsed / SnapshotInterval;
                    float smoothT = t * t * (3f - 2f * t);
                    _displayPositions[id] = Vector3.Lerp(state.previousPosition, state.targetPosition, smoothT);
                    _displayRotations[id] = Mathf.LerpAngle(state.previousRotY, state.targetRotY, smoothT);
                }

                Rigidbody rbPos = state.CachedRb;
                if (rbPos == null || rbPos.gameObject != tracked.gameObject)
                {
                    rbPos = tracked.GetComponent<Rigidbody>();
                    state.CachedRb = rbPos;
                }
                if (rbPos != null)
                    rbPos.MovePosition(_displayPositions[id]);
                else
                    tracked.transform.position = _displayPositions[id];
                Vector3 rot = tracked.transform.eulerAngles;
                rot.y = _displayRotations[id];
                tracked.transform.eulerAngles = rot;
            }

            for (int i = 0; i < _staleKeys.Count; i++)
            {
                _states.Remove(_staleKeys[i]);
            }

            // 3. Clean up unmatched client-only entities (rate-limited).
            // After receiving the first host snapshot + grace period, destroy any
            // character that exists only on the client (not in host's save/night spawns).
            // Running this every LateUpdate allocated CharacterTracker.GetAll() forever while connected.
            if (!_receivedFirstSnapshot) return;
            if (now - _firstSnapshotTime < UnmatchedCleanupDelay) return;
            if (now < _nextUnmatchedCleanupTime) return;
            _nextUnmatchedCleanupTime = now + UnmatchedCleanupInterval;

            Player localPlayer = Player.Instance;
            int nChars = CharacterTracker.CopyAll(out Character[] allChars);
            for (int i = 0; i < nChars; i++)
            {
                Character c = allChars[i];
                if (c == null) continue;

                // Never destroy the local player or phantoms
                if (c == localPlayer || c.name.Contains("RemotePlayer"))
                    continue;

                if (!CharacterTracker.TryGetStableId(c, out short sid))
                    continue;

                // Skip if ever synced by the host (even if currently stale)
                if (_everHostSyncedIds.Contains(sid))
                {
                    _unmatchedSince.Remove(c);
                    continue;
                }

                // Also skip if currently host-synced
                if (_hostSyncedIds.Contains(sid))
                {
                    _unmatchedSince.Remove(c);
                    continue;
                }

                // Track how long this character has been unmatched
                if (!_unmatchedSince.TryGetValue(c, out float firstSeen))
                {
                    _unmatchedSince[c] = now;
                    continue;
                }

                if (now - firstSeen > UnmatchedCleanupDelay)
                {
                    if (ModRuntime.VerboseLogging)
                        ModRuntime.LegacyInfo($"[Entity] destroying unmatched entity: {c.name}(sid={sid})");
                    _unmatchedSince.Remove(c);
                    Object.Destroy(c.gameObject);
                }
            }
        }

        private static float _firstSnapshotTime;

        private static void EnsureEntityAwake(Character c)
        {
            if (c == null) return;

            GameObject go = c.gameObject;
            // Fast path: already fully live — skip GetComponent thrash (called every 10 Hz snap).
            if (go.activeSelf && c.enabled && c.isActive)
            {
                tk2dBaseSprite sp = c.sprite;
                if (sp != null)
                {
                    Color col = sp.color;
                    if (col.a > 0f)
                        return;
                    sp.color = new Color(col.r, col.g, col.b, 1f);
                    return;
                }
                // No sprite ref yet — fall through once to wire anim/renderer.
            }

            if (!go.activeSelf)
                go.SetActive(true);

            if (!c.enabled)
                c.enabled = true;

            if (!c.isActive)
                c.isActive = true;

            tk2dSpriteAnimator anim = c.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && !anim.enabled)
                anim.enabled = true;

            tk2dBaseSprite sprite = c.sprite ?? c.GetComponent<tk2dBaseSprite>();
            if (sprite != null)
            {
                Renderer r = sprite.GetComponent<Renderer>();
                if (r != null && !r.enabled)
                    r.enabled = true;

                Color col = sprite.color;
                if (col.a <= 0f)
                    sprite.color = new Color(col.r, col.g, col.b, 1f);
            }

            // Rigidbody left non-kinematic so the client player can push entities via physics.
            // Host snapshots drive position via Rigidbody.MovePosition, which respects collisions.
        }

        /// <summary>
        /// Searches the entire scene (including inactive GameObjects) for a character
        /// whose name matches <paramref name="entityName"/> within <paramref name="radius"/>
        /// of <paramref name="position"/>.  This avoids spawning phantom duplicates when
        /// a save-related entity exists in the scene but is deactivated by WorldGrid culling.
        /// </summary>
        private static Character[] _inactiveScanCache;
        private static float _inactiveScanCacheTime = -999f;
        private const float InactiveScanCacheTtl = 0.5f;

        private static Character FindInactiveCharacter(string entityName, Vector3 position, float radius)
        {
            string searchName = entityName;
            if (searchName.EndsWith("(Clone)"))
                searchName = searchName.Substring(0, searchName.Length - 7);

            // Scene-wide FindObjectsOfType is expensive; share one scan across pending ids for 0.5s.
            float now = Time.time;
            if (_inactiveScanCache == null || now - _inactiveScanCacheTime >= InactiveScanCacheTtl)
            {
                var footSw = System.Diagnostics.Stopwatch.StartNew();
                _inactiveScanCache = GameObject.FindObjectsOfType<Character>(true);
                footSw.Stop();
                DWMPHorde.Logging.ClientPerfProbe.NoteFindObjectsOfType("Character", footSw.Elapsed.TotalMilliseconds);
                _inactiveScanCacheTime = now;
            }

            Character[] all = _inactiveScanCache;
            float radiusSq = radius * radius;
            Character best = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;

                // Skip the local player and remote proxy
                if (c.name.Contains("Player"))
                    continue;

                // Skip if already host-synced or a phantom
                if (CharacterTracker.TryGetStableId(c, out short sid))
                {
                    if (_hostSyncedIds.Contains(sid) || _spawnedPhantomIds.Contains(sid))
                        continue;
                }

                string cname = c.name;
                if (cname.EndsWith("(Clone)"))
                    cname = cname.Substring(0, cname.Length - 7);
                if (!string.Equals(cname, searchName, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                float dSq = (c.transform.position - position).sqrMagnitude;
                if (dSq < radiusSq && dSq < bestDistSq)
                {
                    bestDistSq = dSq;
                    best = c;
                }
            }
            return best;
        }

        private static Character SpawnEntityLocally(string entityName, string prefabPath, Vector3 position, float rotY)
        {
            if (string.IsNullOrEmpty(entityName) && string.IsNullOrEmpty(prefabPath))
                return null;

            // During shared dream, ignore overworld-distance host spawns (stale EntityState).
            if (DreamSyncManager.IsLocalDreamActive || DreamSession.IsActive)
            {
                var dreamTf = DreamSyncManager.GetDreamLocationTransform();
                if (dreamTf != null)
                {
                    // Dream pads sit far off-map; 5km radius around pad is generous.
                    const float maxDistSq = 5000f * 5000f;
                    if ((position - dreamTf.position).sqrMagnitude > maxDistSq)
                        return null;
                }
            }

            string path = !string.IsNullOrEmpty(prefabPath) ? prefabPath : "Characters/" + entityName;
            try
            {
                Quaternion rotation = Quaternion.Euler(90f, rotY, 0f);

                GameObject go = Core.AddPrefab(path, position, rotation, null);
                if (go == null) return null;

                Character c = go.GetComponent<Character>();
                if (c == null)
                {
                    Object.Destroy(go);
                    return null;
                }

                // Force idle animation so the entity doesn't appear in T-pose
                // until the next host snapshot provides the correct clip.
                tk2dSpriteAnimator anim = c.GetComponent<tk2dSpriteAnimator>();
                if (anim != null)
                {
                    string idleClip = Traverse.Create(c).Field("idleAni").GetValue<string>();
                    if (!string.IsNullOrEmpty(idleClip) && anim.GetClipByName(idleClip) != null)
                    {
                        if (anim.CurrentClip == null || anim.CurrentClip.name != idleClip)
                            anim.Play(idleClip);
                    }
                }

                ModRuntime.LegacyInfo($"[Entity] spawned local entity: {path} at ({position.x:F1},{position.z:F1})");
                return c;
            }
            catch (System.Exception ex)
            {
                ModRuntime.Log?.LogError($"[Entity] failed to spawn {path}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Client→host promote: stop driving positions and release rigidbodies so local AI/physics run.
        /// </summary>
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
            _pendingMatches.Clear();
            _lastApplyCount = 0;
            _lastSkippedCount = 0;
            _totalApplied = 0;
            _totalSkipped = 0;
            _receivedFirstSnapshot = false;
            _firstSnapshotTime = 0f;
            _snapshotCount = 0;
        }

        public static void LogStats()
        {
            ModRuntime.LegacyInfo($"[Entity] stats — total applied: {_totalApplied}, total skipped: {_totalSkipped}, hostSynced: {_hostSyncedIds.Count}, pending: {_pendingMatches.Count}");
        }
    }
}
