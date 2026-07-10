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

        private static int _lastApplyCount;
        private static int _lastSkippedCount;
        private static int _totalApplied;
        private static int _totalSkipped;
        private static int _snapshotCount;

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

            // Purge any local-only entities that appeared since the last
            // snapshot — daytime world dogs, save-loaded characters, etc.
            // that have no host stable ID and would appear as immobile ghosts.
            PurgeUnmatchedLocalEntities();

            int applied = 0;
            int skipped = 0;
            var sb = new System.Text.StringBuilder();
            var skippedSb = new System.Text.StringBuilder();

            for (int i = 0; i < msg.Entities.Length; i++)
            {
                EntitySnapshotNet e = msg.Entities[i];
                Vector3 targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);

                if (sb.Length == 0)
                    sb.Append($"[Entity] snapshot IDs: ");
                sb.Append($"{e.Index} ");

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
                        // IMPORTANT: exclude the phantom's own ID from the search so
                        // FindByPositionAndName doesn't return the phantom itself.
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
                                ModRuntime.LegacyInfo($"[Entity] replaced phantom with real entity: {e.EntityName}(id={e.Index})");
                            }
                        }
                        _hostSyncedIds.Add(e.Index);
                        _everHostSyncedIds.Add(e.Index);
                        UpdateInterpolation(c, e, targetPos, ref applied);
                        continue;
                    }

                    // Name mismatch — the stable ID hit a wrong local entity.
                    // Reset its ID so position-based matching can find the correct entity.
                    if (ModRuntime.VerboseLogging || (_snapshotCount % 30 == 0))
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
                    ModRuntime.LegacyInfo($"[Entity] matched by position: {e.EntityName}(id={e.Index}) at ({targetPos.x:F1},{targetPos.z:F1})");
                    UpdateInterpolation(c, e, targetPos, ref applied);
                    continue;
                }

                // Couldn't find locally — defer to pending match (retried each frame)
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
                skipped++;
                if (skippedSb.Length == 0)
                    skippedSb.Append("[Entity] PENDING: ");
                skippedSb.Append($"id={e.Index}({e.EntityName}) ");
            }

            _lastApplyCount = applied;
            _lastSkippedCount = skipped;
            _totalApplied += applied;
            _totalSkipped += skipped;

            _snapshotCount++;
            if (_snapshotCount % 10 == 0 && ModRuntime.VerboseLogging)
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
                if (skippedSb.Length > 0)
                    ModRuntime.LegacyInfo(skippedSb.ToString());
            }
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
        /// - Dead: keep host death frame so corpse doesn't freeze on wrong pose.
        /// Applies to root animator and Character.legsAnimator when present.
        /// </summary>
        private static void ApplyEntityPresentation(Character c, short entityId, string clip, short clipFrame, bool alive)
        {
            if (c == null) return;

            tk2dSpriteAnimator body = c.GetComponent<tk2dSpriteAnimator>();
            // Prefer Character.animator cache when available
            try
            {
                var a = c.animator;
                if (a != null) body = a;
            }
            catch { /* property may throw if dismantled */ }

            ApplyClipToAnimator(body, entityId, clip, clipFrame, alive, trackDeath: true);

            tk2dSpriteAnimator legs = c.legsAnimator;
            if (legs != null && legs != body)
                ApplyClipToAnimator(legs, entityId, clip, clipFrame, alive, trackDeath: false);
        }

        private static void ApplyClipToAnimator(
            tk2dSpriteAnimator anim, short entityId, string clip, short clipFrame, bool alive, bool trackDeath)
        {
            if (anim == null) return;

            if (!string.IsNullOrEmpty(clip))
            {
                bool isDead = !alive;
                bool alreadyPlayedDeath = trackDeath && isDead && _deathAnimationPlayed.Contains(entityId);

                if (!alreadyPlayedDeath)
                {
                    bool clipChanged = anim.CurrentClip == null || anim.CurrentClip.name != clip;
                    if (clipChanged && anim.GetClipByName(clip) != null)
                    {
                        anim.Play(clip);
                        if (trackDeath && isDead)
                            _deathAnimationPlayed.Add(entityId);

                        // Align only at clip boundaries (start of attack/hitreact/death).
                        if (clipFrame >= 0 && anim.CurrentClip != null)
                        {
                            int maxFrame = anim.CurrentClip.frames.Length - 1;
                            if (maxFrame >= 0)
                                anim.SetFrame(Mathf.Clamp(clipFrame, 0, maxFrame), false);
                        }
                    }
                    else if (isDead && clipFrame >= 0 && anim.CurrentClip != null)
                    {
                        // Dead: hold host death frame so multi-peer corpses match.
                        int maxFrame = anim.CurrentClip.frames.Length - 1;
                        if (maxFrame >= 0)
                            anim.SetFrame(Mathf.Clamp(clipFrame, 0, maxFrame), false);
                    }
                    // Alive + same clip: natural playback — do not SetFrame every tick.
                }
            }
            else if (alive && !anim.Playing)
            {
                string idleClip = null;
                try
                {
                    // idleAni is on Character
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

            // 3. Clean up unmatched client-only entities
            // After receiving the first host snapshot + grace period, destroy any
            // character that exists only on the client (not in host's save/night spawns).
            // This prevents ghost entities when host and client have divergent saves.
            if (!_receivedFirstSnapshot) return;
            if (now - _firstSnapshotTime < UnmatchedCleanupDelay) return;

            Player localPlayer = Player.Instance;
            Character[] allChars = CharacterTracker.GetAll();
            for (int i = 0; i < allChars.Length; i++)
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
        private static Character FindInactiveCharacter(string entityName, Vector3 position, float radius)
        {
            string searchName = entityName;
            if (searchName.EndsWith("(Clone)"))
                searchName = searchName.Substring(0, searchName.Length - 7);

            // findObjectsOfType includes ALL loaded Characters, even inactive ones.
            // Guard: only search if there are pending entities; this is called at most
            // once per entity before spawning.
            Character[] all = GameObject.FindObjectsOfType<Character>(true);
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
        /// Destroys all locally-tracked characters that are NOT the local
        /// player, NOT a remote proxy, and NOT yet known to the host.
        /// Called once on the very first snapshot to purge daytime/save
        /// entities that would otherwise appear as immobile ghosts.
        /// </summary>
        private static void PurgeUnmatchedLocalEntities()
        {
            Player localPlayer = Player.Instance;
            Character[] all = CharacterTracker.GetAll();
            int purged = 0;
            for (int i = 0; i < all.Length; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (c == localPlayer) continue;
                if (c.name.Contains("RemotePlayer")) continue;

                if (!CharacterTracker.TryGetStableId(c, out short sid))
                {
                    Object.Destroy(c.gameObject);
                    purged++;
                    continue;
                }

                // If this entity's stable ID is unknown to the host, it's local-only.
                if (!_everHostSyncedIds.Contains(sid) && !_hostSyncedIds.Contains(sid))
                {
                    Object.Destroy(c.gameObject);
                    purged++;
                }
            }
            if (purged > 0)
                ModRuntime.LegacyInfo($"[Entity] purged {purged} unmatched local entities on first snapshot");
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
            _unmatchedSince.Clear();
            _pendingMatches.Clear();
            _lastApplyCount = 0;
            _lastSkippedCount = 0;
            _totalApplied = 0;
            _totalSkipped = 0;
            _receivedFirstSnapshot = false;
            _firstSnapshotTime = 0f;
        }

        public static void LogStats()
        {
            ModRuntime.LegacyInfo($"[Entity] stats — total applied: {_totalApplied}, total skipped: {_totalSkipped}, hostSynced: {_hostSyncedIds.Count}, pending: {_pendingMatches.Count}");
        }
    }
}
