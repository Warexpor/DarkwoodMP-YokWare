using DWMPHorde.Audio;
using DWMPHorde.Logging;
using DWMPHorde.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        /// <summary>Phantom → real re-match runs at most this often (full character scan per phantom).</summary>
        private const float PhantomRematchInterval = 1f;
        private static float _nextPhantomRematchTime; // process-scoped: rate limiter on the monotonic game clock

        public static void ApplySnapshot(EntityStateMessage msg)
        {
            if (msg.Sequence == 0
                || !_AcceptSnapshotSequence(msg.Sequence))
            {
                if (EntitySyncLog.On)
                    EntitySyncLog.Interp("stale",
                        "[ClientSnap] rejected seq=" + msg.Sequence
                        + " last=" + _lastSnapshotSequence, 2f);
                return;
            }

            if (msg.Entities == null || msg.Entities.Length == 0)
            {
                if (_lastApplyCount > 0)
                    EntitySyncLog.Interp("empty",
                        "[ClientSnap] empty snapshot (had applied=" + _lastApplyCount + ")", 2f);
                _lastApplyCount = 0;
                return;
            }

            float localNow = LocalNow();
            bool hadClock = _hostClock.HasEstimate;
            _hostClock.AddSample(msg.HostTime, localNow);
            // How late this batch is against the best case: the render delay rides it out.
            float lateness = _hostClock.ToHost(localNow) - msg.HostTime;
            if (hadClock)
                _jitter.Add(lateness);
            NoteBatchLateness(lateness);

            bool wasFirst = !_receivedFirstSnapshot;
            _receivedFirstSnapshot = true;
            if (wasFirst)
            {
                _firstSnapshotTime = Time.time;
                EntitySyncLog.Event(() =>
                    "[ClientSnap] FIRST snapshot seq=" + msg.Sequence
                    + " entities=" + msg.Entities.Length);
            }

            int applied = 0;
            int skipped = 0;
            int pendingAdded = 0;
            bool dump = EntitySyncLog.On && ((_snapshotCount + 1) % 40 == 0);
            // Phantom re-match: decided once per snapshot, exclude set built lazily once.
            bool phantomRematch = _spawnedPhantomIds.Count > 0 && Time.time >= _nextPhantomRematchTime;
            bool phantomExcludeBuilt = false;
            if (phantomRematch)
                _nextPhantomRematchTime = Time.time + PhantomRematchInterval;

            for (int i = 0; i < msg.Entities.Length; i++)
            {
                EntitySnapshotNet e = msg.Entities[i];
                float batchHostTime = msg.HostTime;
                if (e.HasDescriptor)
                {
                    _descriptors[e.Index] = new EntityDescriptor
                    {
                        Name = e.EntityName ?? "", PrefabPath = e.PrefabPath ?? "", SaveId = e.SaveId,
                        LookKey = e.LookKey
                    };
                    NoteSaveIdOwner(e.SaveId, e.Index);
                }
                else if (_descriptors.TryGetValue(e.Index, out EntityDescriptor known))
                {
                    e.EntityName = known.Name;
                    e.PrefabPath = known.PrefabPath;
                    e.SaveId = known.SaveId;
                    e.LookKey = known.LookKey;
                }
                else
                {
                    // Joined mid-stream: the name arrives with the next resync (at most 1 s).
                    if (EntitySyncLog.On)
                        EntitySyncLog.Interp("nodesc",
                            "[ClientSnap] id=" + e.Index + " waiting for descriptor", 2f);
                    skipped++;
                    continue;
                }
                Vector3 targetPos = new Vector3(e.PosX, e.PosY, e.PosZ);

                // Recently despawned: ignore late EntityState until grace ends (host holds
                // recycled ids longer; this covers in-flight snapshots for the old body).
                if (_recentlyDespawnedUntil.TryGetValue(e.Index, out float despawnUntil))
                {
                    if (Time.unscaledTime < despawnUntil)
                    {
                        skipped++;
                        continue;
                    }
                    _recentlyDespawnedUntil.Remove(e.Index);
                }

                // Far host-range snaps: do not EnsureEntityAwake / spawn phantoms map-wide. A body
                // already driven keeps being driven out to the leave range.
                if (!IsInClientInterest(targetPos, _states.ContainsKey(e.Index)))
                {
                    StopDriving(e.Index);
                    // Hide only when the local object is also outside interest.
                    // Keep corpses and dead entities visible.
                    Character far = CharacterTracker.FindByStableId(e.Index);
                    if (far != null && far.gameObject != null && far.gameObject.activeSelf
                        && (_everHostSyncedIds.Contains(e.Index) || _spawnedPhantomIds.Contains(e.Index))
                        && far.alive && far.GetComponent<Item>() == null
                        && !IsInClientInterest(far.transform.position))
                        far.gameObject.SetActive(false);
                    skipped++;
                    continue;
                }

                Character c = CharacterTracker.FindByStableId(e.Index);
                if (c != null)
                {
                    // Verify the matched entity's name. FindByStableId can return
                    // the wrong entity when local stable IDs collide with host IDs.
                    bool nameMatches = CharacterTracker.BaseNameEquals(c.name, e.EntityName);

                    if (nameMatches)
                    {
                        // A creature of the shared save is its own save twin. A body bound to this id
                        // by position (or a phantom) gives way to the twin once it can be found.
                        bool rebound = false;
                        if (e.SaveId > 0 && SaveIdOf(c) != e.SaveId)
                        {
                            Character twin = FindSaveTwin(e.Index, e.SaveId, e.EntityName, targetPos);
                            if (twin != null)
                            {
                                c = RebindToSaveTwin(c, twin, e.Index, e.EntityName);
                                rebound = true;
                            }
                        }
                        // If the matched entity is a phantom, check if a real local entity
                        // now exists nearby (e.g. world chunk just loaded). If so, replace
                        // the phantom with the real entity to avoid duplicates.
                        if (!rebound && phantomRematch && _spawnedPhantomIds.Contains(e.Index))
                        {
                            if (!phantomExcludeBuilt)
                            {
                                _phantomReplaceExclude.Clear();
                                foreach (short sid in _hostSyncedIds)
                                    _phantomReplaceExclude.Add(sid);
                                foreach (short sid in _spawnedPhantomIds)
                                    _phantomReplaceExclude.Add(sid);
                                phantomExcludeBuilt = true;
                            }
                            _matchHostId = e.Index;
                            Character real = CharacterTracker.FindByPositionAndName(
                                targetPos, e.EntityName, MatchRadius, _phantomReplaceExclude, RejectOtherSaveTwin);
                            if (real != null)
                            {
                                _phantomReplaceExclude.Add(e.Index);
                                c = RebindToSaveTwin(c, real, e.Index, e.EntityName);
                            }
                        }
                        _hostSyncedIds.Add(e.Index);
                        _everHostSyncedIds.Add(e.Index);
                        UpdateInterpolation(c, e, targetPos, msg.HostTime, ref applied);
                        continue;
                    }

                    // The stable ID matched a different local entity.
                    if (EntitySyncLog.On)
                        EntitySyncLog.Event(() =>
                            "[ClientMatch] ID COLLISION id=" + e.Index + " found=" + c.name
                            + " expected=" + e.EntityName);
                    CharacterTracker.ClearId(c);
                }

                // The body's own copy from the shared save, by its save id: exact, and found
                // asleep on an inactive grid node too.
                c = FindSaveTwin(e.Index, e.SaveId, e.EntityName, targetPos);
                if (c != null)
                {
                    BindHostId(c, e.Index);
                    if (phantomExcludeBuilt)
                        _phantomReplaceExclude.Add(e.Index);
                    if (EntitySyncLog.On)
                        EntitySyncLog.Event(() =>
                            "[ClientMatch] by-save-id " + e.EntityName + "(id=" + e.Index + " save=" + e.SaveId + ")");
                    UpdateInterpolation(c, e, targetPos, msg.HostTime, ref applied);
                    continue;
                }

                // No save twin: a body at the host position, never another body's save twin.
                _matchHostId = e.Index;
                c = CharacterTracker.FindByPositionAndName(targetPos, e.EntityName, MatchRadius, _hostSyncedIds,
                    RejectOtherSaveTwin);
                if (c != null)
                {
                    BindHostId(c, e.Index);
                    if (phantomExcludeBuilt)
                        _phantomReplaceExclude.Add(e.Index);
                    if (EntitySyncLog.On)
                        EntitySyncLog.Event(() =>
                            "[ClientMatch] by-position " + e.EntityName + "(id=" + e.Index
                            + ") at (" + targetPos.x.ToString("F0") + "," + targetPos.z.ToString("F0") + ")");
                    UpdateInterpolation(c, e, targetPos, msg.HostTime, ref applied);
                    continue;
                }

                // Keep one pending entry per host ID until the local object exists.
                if (!TryUpdatePending(e, targetPos, batchHostTime))
                {
                    while (_pendingMatches.Count >= MaxPendingMatches)
                        _pendingMatches.RemoveAt(0);
                    _pendingMatches.Add(new PendingEntry
                    {
                        HostId = e.Index,
                        EntityName = e.EntityName,
                        PrefabPath = e.PrefabPath,
                        Position = targetPos,
                        RotY = e.RotY,
                        Clip = e.Clip,
                        ClipFrame = e.ClipFrame,
                        Animating = e.Animating,
                        Alive = e.Alive,
                        Downed = e.Downed,
                        HealthPct = e.HealthPct,
                        SaveId = e.SaveId,
                        HostTime = msg.HostTime,
                        TimeAdded = Time.time
                    });
                    WarmPhantomPrefab(e.PrefabPath);
                    pendingAdded++;
                }
                skipped++;
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
            if (dump)
            {
                EntitySyncLog.Interp("snap:sum", () =>
                    "[ClientSnap] seq=" + msg.Sequence
                    + " n=" + msg.Entities.Length
                    + " applied=" + applied
                    + " skipped=" + skipped
                    + " pendingNew=" + pendingAdded
                    + " pendingQ=" + _pendingMatches.Count
                    + " hostSynced=" + _hostSyncedIds.Count
                    + " phantoms=" + _spawnedPhantomIds.Count, 1.5f);
            }
        }

        /// <summary>
        /// Forget the last accepted EntityState sequence. A new sender (host migration, soft
        /// reconnect) counts from 1 again; keeping the old host's high-water mark made survivors
        /// drop every snapshot from the promoted host until it overtook that number.
        /// Entity maps stay intact; the ordering gate, the host clock, the timelines and the
        /// id descriptors (all per sender) reset.
        /// </summary>
        public static void ResetSnapshotSequence()
        {
            _lastSnapshotSequence = 0;
            _hasSnapshotSequence = false;
            // The new sender has its own clock: old-host timestamps would reject every new sample.
            _hostClock.Reset();
            _jitter.Reset();
            _localEpoch = -1;
            foreach (var kv in _states)
            {
                kv.Value.Timeline.Clear();
                kv.Value.hasRendered = false;
                kv.Value.blendErr = Vector3.zero;
            }
            // Its ids name its own bodies; its first sends of each id carry the descriptor.
            _descriptors.Clear();
            _saveIdOwners.Clear();
        }

        private static bool _AcceptSnapshotSequence(uint sequence)
        {
            if (!SnapshotSequencePolicy.IsNewer(sequence, _lastSnapshotSequence, _hasSnapshotSequence))
                return false;
            _lastSnapshotSequence = sequence;
            _hasSnapshotSequence = true;
            return true;
        }

        /// <summary>Update existing pending row for host id; false if not yet pending.</summary>
        private static bool TryUpdatePending(EntitySnapshotNet e, Vector3 targetPos, float hostTime)
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
                p.Animating = e.Animating;
                p.SaveId = e.SaveId;
                p.HostTime = hostTime;
                p.Alive = e.Alive;
                p.Downed = e.Downed;
                p.HealthPct = e.HealthPct;
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
            Character driven = CharacterTracker.FindByStableId(hostId);
            bool hostFleeing = false;
            if (_states.TryGetValue(hostId, out var state))
            {
                hostFleeing = state.fleeing;
                state.hasTarget = false;
                if (state.CachedRb != null)
                {
                    try { state.CachedRb.isKinematic = false; }
                    catch { /* destroyed */ }
                }
                ReleaseDrivenBody(state.CachedRb);
                _states.Remove(hostId);
            }
            _displayPositions.Remove(hostId);
            _displayRotations.Remove(hostId);
            // Undriven, the copy stands where it was last seen; its loop restarts when it is driven again.
            if (driven != null)
                Audio.EntityLoopSync.Stop(driven);
            // Keep _hostSyncedIds / ever so we don't thrash rematch when they re-enter range.
            // Flee/fly left interest: hide only if local GO is also outside interest (and alive).
            if (driven != null && driven.gameObject != null && driven.gameObject.activeSelf
                && driven.alive && driven.GetComponent<Item>() == null
                && !IsInClientInterest(driven.transform.position))
            {
                if (hostFleeing || _spawnedPhantomIds.Contains(hostId))
                    driven.gameObject.SetActive(false);
            }
        }

        private static void UpdateInterpolation(Character c, EntitySnapshotNet e, Vector3 targetPos, float hostTime, ref int applied)
        {
            EnsureEntityAwake(c);
            // The host's look for this body (a no-op once it has it).
            DWMPHorde.Sync.CosmeticRolls.ApplyCharacterKey(c, e.LookKey);

            // The host's AI owns this body's voice: its one-shots arrive as EntitySound, its loop
            // with every snapshot (EntityLoopSync, below). The component's own OnEnable /
            // OnDisable loop handling stays off.
            if (_audioStoppedIds.Add(e.Index))
            {
                CharacterSounds cs = c.GetComponent<CharacterSounds>();
                if (cs != null)
                    cs.enabled = false;
            }

            if (!_states.TryGetValue(e.Index, out var state))
            {
                state = new EntityInterpState { isFirst = true };
                _states[e.Index] = state;
            }
            state.staleSince = 0f;
            if (state.interval <= 0f)
                state.interval = IsNearBand(targetPos) ? NearSendInterval : FarSendInterval;

            // A sample far older than the newest is a different host clock (migration the
            // sequence reset missed): start the timeline over instead of dropping every sample.
            if (state.Timeline.Count > 0 && hostTime < state.Timeline.Newest.T - HostClockEstimator.ResyncThreshold)
                state.Timeline.Clear();

            // Teleport (unload/reload, bunker pad, knockback): judged host sample to host sample.
            // Against the shown pose, which trails the host by the render delay, a fast creature
            // looked like one, snapped and froze for a delay. The timeline jumps at its moment.
            bool cut = false;
            bool prevIsHost = state.Timeline.Count > 0 && !state.Timeline.Newest.Synthetic;
            float gap = 0f;
            if (state.Timeline.Count > 0)
            {
                TimelineSample last = state.Timeline.Newest;
                gap = hostTime - last.T;
                float dx = last.X - targetPos.x;
                float dz = last.Z - targetPos.z;
                cut = dx * dx + dz * dz > EntityHardSnapXz * EntityHardSnapXz
                    || Mathf.Abs(last.Y - targetPos.y) > EntityHardSnapY;
            }

            // Hard-snap on first drive, or a body with no timeline yet shown far from the host
            // pose (claim from a distant twin): same thresholds as RemotePlayerProxy.
            bool snap = state.isFirst;
            if (!snap && state.Timeline.Count == 0 && _displayPositions.TryGetValue(e.Index, out Vector3 fromPos))
            {
                Vector3 flat = fromPos - targetPos;
                flat.y = 0f;
                snap = flat.sqrMagnitude > EntityHardSnapXz * EntityHardSnapXz
                    || Mathf.Abs(fromPos.y - targetPos.y) > EntityHardSnapY;
            }
            if (snap)
            {
                HardSnapEntityDisplay(c, e.Index, state, targetPos, e.RotY);
                state.Timeline.Clear();
                state.hasRendered = false;
                state.blendErr = Vector3.zero;
                cut = false;
            }
            else if (!_displayPositions.ContainsKey(e.Index))
            {
                _displayPositions[e.Index] = c.transform.position;
                _displayRotations[e.Index] = c.transform.eulerAngles.y;
            }

            if (state.Timeline.Count == 0 && !snap)
            {
                // First timed sample for a body already on screen (pending match, claim,
                // resume after stale): blend from where it is shown now.
                Vector3 shown = _displayPositions[e.Index];
                state.Timeline.Add(new TimelineSample
                {
                    T = hostTime - state.interval,
                    X = shown.x, Y = shown.y, Z = shown.z,
                    RotY = _displayRotations[e.Index],
                    Synthetic = true
                }, 0f);
                prevIsHost = false;
            }
            bool added = state.Timeline.Add(new TimelineSample
            {
                T = hostTime,
                X = targetPos.x, Y = targetPos.y, Z = targetPos.z,
                RotY = e.RotY,
                HasClip = true,
                Clip = e.Clip,
                ClipFrame = e.ClipFrame,
                Animating = e.Animating,
                Cut = cut,
                PrevClip = e.HasPrevClip ? e.PrevClip : null,
                PrevClipT = e.HasPrevClip ? hostTime - e.PrevClipAgeMs * 0.001f : 0f
            }, state.interval);

            // The body's send interval (20 / 10 Hz as the host ticks it): a gap of a resting
            // body the host skipped is not one.
            if (added && prevIsHost && gap > 0f)
            {
                NoteSampleGap(gap);
                if (gap < state.interval * EntityTimeline.HoldGapIntervals)
                    state.interval += (gap - state.interval) * IntervalGain;
            }

            // The body's own clip (run, turn, idle, defensive) is shown when its pose is: the
            // timeline renders the pose a delay behind the host, and a clip played on arrival
            // turned or stopped the dog before its body did (sliding, stutter). Events stay on
            // arrival, where the damage, hit sounds and death they belong to land: a reaction
            // clip (attack, hit), going down, dying, getting back up, and the first sight or a
            // hard snap (nothing older to wait for).
            bool clipOnTimeline = !snap && e.Alive && !e.Downed && c.alive && !IsReactionClipName(e.Clip);

            state.previousPosition = _displayPositions[e.Index];
            state.previousRotY = _displayRotations[e.Index];
            state.targetPosition = targetPos;
            state.targetRotY = e.RotY;
            state.arrivalTime = Time.time;
            state.hasTarget = true;
            state.fleeing = e.Fleeing;
            c.behaviour = e.PackedBehaviour;
            // Flight state: Character.setBehaviour / BirdArea set these on the host; the copy's
            // altitude, scale, shadow and hittability (MeleeSensor) follow from them.
            Flier flier = c.flier;
            if (flier != null)
            {
                flier.inFlight = e.InFlight;
                flier.diving = e.Diving;
            }
            DWMPHorde.Sync.CreatureSightState.Apply(c, e.InSight);

            bool wasAlive = state.alive;
            ApplyAuthoritativeBody(c, e.Index, e.Alive, e.Downed, e.HealthPct, e.Clip, e.ClipFrame, state,
                hostTime, e.Animating, presentAliveClip: !clipOnTimeline);
            if (!clipOnTimeline)
                state.Timeline.HoldClipsThrough(hostTime);
            if (EntitySyncLog.On && e.Alive && wasAlive && e.HealthPct > 0)
            {
                EntitySyncLog.Interp("hp:" + e.Index,
                    () => "[ClientHP] id=" + e.Index + " " + c.name
                        + " hp%=" + e.HealthPct + " clip=" + (e.Clip ?? ""), 0.75f);
            }

            ApplySleepEatFlags(c, e);
            Audio.EntityLoopSync.Apply(c, e.Loop);
            applied++;
        }

        /// <summary>Inside the host's 20 Hz band as seen from this listener.</summary>
        private static bool IsNearBand(Vector3 pos)
        {
            Vector3 listen = LocalAudioService.GetListenPosition();
            float dx = pos.x - listen.x;
            float dz = pos.z - listen.z;
            return dx * dx + dz * dz <= NearBandDistance * NearBandDistance;
        }

        /// <summary>
        /// Snap display + rigidbody to host pose and clear first-frame so the next
        /// tick does not lerp from a stale pre-teleport position.
        /// </summary>
        private static void HardSnapEntityDisplay(
            Character c, short id, EntityInterpState state, Vector3 targetPos, float rotY)
        {
            _displayPositions[id] = targetPos;
            _displayRotations[id] = rotY;
            state.previousPosition = targetPos;
            state.previousRotY = rotY;
            state.targetPosition = targetPos;
            state.targetRotY = rotY;
            state.isFirst = false;

            Rigidbody rb = state.CachedRb;
            if (rb == null || rb.gameObject != c.gameObject)
            {
                rb = c.GetComponent<Rigidbody>();
                state.CachedRb = rb;
            }
            if (rb != null)
            {
                rb.position = targetPos;
                rb.velocity = Vector3.zero;
            }
            else if (c != null && c.transform != null)
            {
                c.transform.position = targetPos;
            }
            if (c != null && c.transform != null)
            {
                Vector3 euler = c.transform.eulerAngles;
                euler.y = rotY;
                c.transform.eulerAngles = euler;
            }
        }

        /// <summary>
        /// Copy host life onto the local body. Death is presentation only:
        /// vanilla <c>die()</c> stays on the host. <paramref name="presentAliveClip"/> false: a
        /// living body's clip comes from its timeline (<see cref="PresentTimelineClip"/>).
        /// <paramref name="hostTime"/> stamps <paramref name="clip"/>: shown now, it has run since.
        /// </summary>
        private static void ApplyAuthoritativeBody(
            Character c, short id, bool alive, bool downed, byte healthPct,
            string clip, short clipFrame, EntityInterpState state, float hostTime, bool animating,
            bool presentAliveClip = true)
        {
            if (c == null) return;
            ApplyHealth(c, healthPct, alive, downed);
            float elapsed = ArrivalElapsed(hostTime);

            if (downed)
            {
                PresentHostDowned(c, id, clip);
                if (state != null)
                {
                    state.downed = true;
                    state.alive = false;
                }
                ApplyEntityPresentation(c, id, clip, clipFrame, elapsed, animating);
                return;
            }

            if (!alive)
            {
                // Seen alive (or down) here before: its death plays. A body first seen dead
                // (late join, coming back into view, an old corpse) lies on its last frame.
                bool watched = state != null && (state.alive || state.downed);
                bool wasDowned = state != null && state.downed;
                if (state != null)
                {
                    state.downed = false;
                    state.alive = false;
                }
                if (c.alive || wasDowned)
                    PresentHostDeath(c, id, clip, clipFrame, watched, elapsed);
                else
                    EnsureDeathAnimation(c, id, clip, clipFrame, watched, elapsed);
                return;
            }

            if (!c.alive)
                PresentHostRevive(c, id);
            if (state != null)
            {
                state.alive = true;
                state.downed = false;
            }
            if (presentAliveClip)
                ApplyEntityPresentation(c, id, clip, clipFrame, elapsed, animating);
        }

        /// <summary>
        /// Show the host clip the rendered pose has reached (from <see cref="TickLateUpdate"/>),
        /// on the frame the host had moved it to by then. A body that went down or died since is
        /// shown by its death path, not by older clips; an attack, hit or death the host passed
        /// through between samples was shown on arrival by its own message.
        /// </summary>
        private static void PresentTimelineClip(Character c, short id, EntityInterpState state, float hostTime)
        {
            if (!state.Timeline.TakeClip(hostTime, out TimelineSample s))
                return;
            if (!state.alive || state.downed || !c.alive)
                return;
            if (IsReactionClipName(s.Clip) || IsDeathClipName(s.Clip))
                return;
            ApplyEntityPresentation(c, id, s.Clip, s.ClipFrame, hostTime - s.T, s.Animating);
        }

        /// <summary>
        /// A host attack (EnemyAttack) was played on arrival: snapshot clips up to its host time
        /// must not replace it once their pose is rendered.
        /// </summary>
        internal static void HoldTimelineClips(short id, float hostTime)
        {
            if (_states.TryGetValue(id, out EntityInterpState state))
                state.Timeline.HoldClipsThrough(hostTime);
        }

        /// <summary>
        /// This client showed its own hit on the body just now (speculative hit clip): snapshot
        /// clips the host sent before it saw the hit must not replace it.
        /// </summary>
        internal static void HoldTimelineClipsNow(short id)
        {
            if (id != 0 && _hostClock.HasEstimate)
                HoldTimelineClips(id, HostNowEstimate());
        }

        private static void ApplyHealth(Character c, byte healthPct, bool alive, bool downed)
        {
            float before = c.Health;
            if (!alive && !downed)
            {
                c.Health = 0f;
            }
            else
            {
                float max = c.maxHealth > 0.01f ? c.maxHealth : 100f;
                if (downed && healthPct == 0)
                    healthPct = 1;
                c.Health = (healthPct / 100f) * max;
            }
            // The enemy health bar shown after this player's hit follows the host's numbers,
            // the kill included (it returned before the refresh: a killed dog kept a full bar).
            if (!Mathf.Approximately(before, c.Health))
                HealthBarRefresh.IfShowing(c.gameObject);
        }

        /// <summary>
        /// This player hit a creature: vanilla shows its health bar from the hit, which on a client
        /// lands on the host. Show it here; the host's health snapshot refreshes it.
        /// </summary>
        internal static void ShowHitHealthBar(Character c)
        {
            if (c == null || !c.alive)
                return;
            var bar = Singleton<UI>.Instance != null ? Singleton<UI>.Instance.enemyHealthBar : null;
            if (bar != null)
                bar.show(c.gameObject);
        }

        private static void ApplySleepEatFlags(Character c, EntitySnapshotNet e)
        {
            if (c == null || !e.Alive) return;
            bool sleeping = e.Sleeping;
            bool eating = e.Eating;
            if (c.sleeping != sleeping)
                c.sleeping = sleeping;
            if (c.eating != eating)
                c.eating = eating;
        }

    }
}
