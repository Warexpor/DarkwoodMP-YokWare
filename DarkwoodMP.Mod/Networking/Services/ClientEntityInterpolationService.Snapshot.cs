using DWMPHorde.Logging;
using DWMPHorde.Sync;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Networking
{
    public static partial class ClientEntityInterpolationService
    {
        public static void ApplySnapshot(EntityStateMessage msg)
        {
            if (msg.Sequence == 0
                || !_AcceptSnapshotSequence(msg.Sequence))
            {
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

            for (int i = 0; i < msg.Entities.Length; i++)
            {
                EntitySnapshotNet e = msg.Entities[i];
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

                // Far host-range snaps: do not EnsureEntityAwake / spawn phantoms map-wide.
                if (!IsInClientInterest(targetPos))
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
                            _phantomReplaceExclude.Clear();
                            foreach (short sid in _hostSyncedIds)
                                _phantomReplaceExclude.Add(sid);
                            _phantomReplaceExclude.Add(e.Index);
                            Character real = CharacterTracker.FindByPositionAndName(
                                targetPos, e.EntityName, MatchRadius, _phantomReplaceExclude);
                            if (real != null)
                            {
                                CharacterTracker.AssignId(real, e.Index);
                                _hostSyncedIds.Add(e.Index);
                                _everHostSyncedIds.Add(e.Index);
                                _spawnedPhantomIds.Remove(e.Index);
                                Object.Destroy(c.gameObject);
                                c = real;
                                EntitySyncLog.Event(() =>
                                    "[ClientMatch] replaced phantom → real " + e.EntityName
                                    + "(id=" + e.Index + ")");
                            }
                        }
                        _hostSyncedIds.Add(e.Index);
                        _everHostSyncedIds.Add(e.Index);
                        UpdateInterpolation(c, e, targetPos, ref applied);
                        continue;
                    }

                    // The stable ID matched a different local entity.
                    EntitySyncLog.Event(() =>
                        "[ClientMatch] ID COLLISION id=" + e.Index + " found=" + c.name
                        + " expected=" + e.EntityName);
                    CharacterTracker.ClearId(c);
                }

                // If the ID did not match, try position and name.
                c = CharacterTracker.FindByPositionAndName(targetPos, e.EntityName, MatchRadius, _hostSyncedIds);
                if (c != null)
                {
                    CharacterTracker.AssignId(c, e.Index);
                    _hostSyncedIds.Add(e.Index);
                    _everHostSyncedIds.Add(e.Index);
                    EnsureEntityAwake(c);
                    EntitySyncLog.Event(() =>
                        "[ClientMatch] by-position " + e.EntityName + "(id=" + e.Index
                        + ") at (" + targetPos.x.ToString("F0") + "," + targetPos.z.ToString("F0") + ")");
                    UpdateInterpolation(c, e, targetPos, ref applied);
                    continue;
                }

                // Keep one pending entry per host ID until the local object exists.
                if (!TryUpdatePending(e, targetPos))
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
                        Alive = e.Alive,
                        Downed = e.Downed,
                        HealthPct = e.HealthPct,
                        TimeAdded = Time.time
                    });
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
        /// Entity maps stay intact — only the ordering gate resets.
        /// </summary>
        public static void ResetSnapshotSequence()
        {
            _lastSnapshotSequence = 0;
            _hasSnapshotSequence = false;
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
                _states.Remove(hostId);
            }
            _displayPositions.Remove(hostId);
            _displayRotations.Remove(hostId);
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

        private static void UpdateInterpolation(Character c, EntitySnapshotNet e, Vector3 targetPos, ref int applied)
        {
            EnsureEntityAwake(c);

            // Disable CharacterSounds on first snapshot. Client AI is frozen, so
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

            // Hard-snap on first drive or large teleports (unload/reload, claim from a
            // distant twin, knockback). Pure lerp left NPCs sliding map-wide for seconds —
            // same thresholds as RemotePlayerProxy.ApplyNetworkState.
            bool snap = state.isFirst;
            if (!snap && _displayPositions.TryGetValue(e.Index, out Vector3 fromPos))
            {
                Vector3 flat = fromPos - targetPos;
                flat.y = 0f;
                snap = flat.sqrMagnitude > EntityHardSnapXz * EntityHardSnapXz
                    || Mathf.Abs(fromPos.y - targetPos.y) > EntityHardSnapY;
            }
            if (snap)
                HardSnapEntityDisplay(c, e.Index, state, targetPos, e.RotY);
            else if (!_displayPositions.ContainsKey(e.Index))
            {
                _displayPositions[e.Index] = c.transform.position;
                _displayRotations[e.Index] = c.transform.eulerAngles.y;
            }

            state.previousPosition = _displayPositions[e.Index];
            state.previousRotY = _displayRotations[e.Index];
            state.targetPosition = targetPos;
            state.targetRotY = e.RotY;
            state.arrivalTime = Time.time;
            state.hasTarget = true;
            state.fleeing = e.Fleeing;
            c.behaviour = e.PackedBehaviour;

            bool wasAlive = state.alive;
            ApplyAuthoritativeBody(c, e.Index, e.Alive, e.Downed, e.HealthPct, e.Clip, e.ClipFrame, state);
            if (e.Alive && wasAlive && e.HealthPct > 0)
            {
                EntitySyncLog.Interp("hp:" + e.Index,
                    () => "[ClientHP] id=" + e.Index + " " + c.name
                        + " hp%=" + e.HealthPct + " clip=" + (e.Clip ?? ""), 0.75f);
            }

            ApplySleepEatFlags(c, e);
            applied++;
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
        /// vanilla <c>die()</c> stays on the host.
        /// </summary>
        private static void ApplyAuthoritativeBody(
            Character c, short id, bool alive, bool downed, byte healthPct,
            string clip, short clipFrame, EntityInterpState state)
        {
            if (c == null) return;
            ApplyHealth(c, healthPct, alive, downed);

            if (downed)
            {
                PresentHostDowned(c, id, clip);
                if (state != null)
                {
                    state.downed = true;
                    state.alive = false;
                }
                ApplyEntityPresentation(c, id, clip, clipFrame, alive: true);
                return;
            }

            if (!alive)
            {
                bool wasDowned = state != null && state.downed;
                if (state != null)
                {
                    state.downed = false;
                    state.alive = false;
                }
                if (c.alive || wasDowned)
                    PresentHostDeath(c, id, clip, clipFrame);
                else
                    ApplyEntityPresentation(c, id, clip, clipFrame, alive: false);
                return;
            }

            if (!c.alive)
                PresentHostRevive(c, id);
            if (state != null)
            {
                state.alive = true;
                state.downed = false;
            }
            ApplyEntityPresentation(c, id, clip, clipFrame, alive: true);
        }

        private static void ApplyHealth(Character c, byte healthPct, bool alive, bool downed)
        {
            if (!alive && !downed)
            {
                c.Health = 0f;
                return;
            }
            float max = c.maxHealth > 0.01f ? c.maxHealth : 100f;
            if (downed && healthPct == 0)
                healthPct = 1;
            c.Health = (healthPct / 100f) * max;
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
