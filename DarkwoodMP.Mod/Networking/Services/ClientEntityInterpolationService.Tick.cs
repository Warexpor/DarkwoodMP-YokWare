using System.Collections.Generic;
using DWMPHorde.Audio;
using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>
    /// Per-frame entity interpolation / pending-match / stale cleanup
    /// (split from the main service for ownership).
    /// </summary>
    public static partial class ClientEntityInterpolationService
    {
        public static void TickLateUpdate()
        {
            float now = Time.time;

            TickPendingMatches(now);

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
                    // Snapshots paused (walked away, dream, crowded packet). The body
                    // stays. Only a host despawn removes it. Drop the interp record
                    // after the hold so LateUpdate is not walking a frozen id.
                    if (state.staleSince > 0f && now - state.staleSince > PhantomCleanupDelay)
                    {
                        if (state.fleeing)
                        {
                            Character gone = CharacterTracker.FindByStableId(id);
                            if (gone != null && gone.alive && gone.gameObject != null
                                && gone.gameObject.activeSelf
                                && gone.GetComponent<Item>() == null)
                                gone.gameObject.SetActive(false);
                        }
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
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

                if (state.Timeline.Count > 0 && _hostClock.HasEstimate)
                {
                    TimelineSample pose;
                    if (elapsed > MaxInterpDelay)
                    {
                        // Nothing new: the host skips a body that does not change. Sit on its
                        // last host pose (and clip) until the next snapshot.
                        pose = state.Timeline.Newest;
                        state.hasTarget = false;
                        state.staleSince = now;
                        PresentTimelineClip(tracked, id, state, pose.T);
                    }
                    else
                    {
                        Vector3 listen = LocalAudioService.GetListenPosition();
                        Vector3 at = state.targetPosition;
                        float dx = at.x - listen.x;
                        float dz = at.z - listen.z;
                        float want = dx * dx + dz * dz <= NearBandDistance * NearBandDistance
                            ? NearInterpDelay : FarInterpDelay;
                        state.delay = state.delay <= 0f
                            ? want
                            : Mathf.MoveTowards(state.delay, want, DelaySlewPerSec * Time.unscaledDeltaTime);
                        float renderTime = _hostClock.ToHost(Time.unscaledTime) - state.delay;
                        state.Timeline.Sample(renderTime, MaxExtrapolateSec, out pose);
                        PresentTimelineClip(tracked, id, state, renderTime);
                    }
                    _displayPositions[id] = new Vector3(pose.X, pose.Y, pose.Z);
                    _displayRotations[id] = pose.RotY;
                }
                else if (elapsed > MaxInterpDelay)
                {
                    _displayPositions[id] = state.targetPosition;
                    _displayRotations[id] = state.targetRotY;
                    state.hasTarget = false;
                    state.staleSince = now;
                }
                else if (elapsed > SnapshotInterval)
                {
                    // Short coast, then sit on the host spot. A full extra step
                    // slid past the enemy; a hard hold made the chase stop-go.
                    float extrapT = Mathf.Min(elapsed - SnapshotInterval, 0.08f);
                    Vector3 step = state.targetPosition - state.previousPosition;
                    Vector3 lead = step * (extrapT / SnapshotInterval);
                    float cap = step.magnitude * 0.35f;
                    if (lead.sqrMagnitude > cap * cap && cap > 0.001f)
                        lead *= cap / lead.magnitude;
                    _displayPositions[id] = state.targetPosition + lead;
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
                WriteShownPose(tracked, rbPos, _displayPositions[id], _displayRotations[id]);
            }

            for (int i = 0; i < _staleKeys.Count; i++)
            {
                if (_states.TryGetValue(_staleKeys[i], out EntityInterpState gone))
                    ReleaseDrivenBody(gone.CachedRb);
                _states.Remove(_staleKeys[i]);
            }

            // 3. Clean up unmatched client-only entities (rate-limited).
            // Only cull inside client interest. Far save NPCs must stay so claim can
            // map them when the player walks up (host streams at EntityActivationRange).
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

                if (!IsInClientInterest(c.transform.position))
                {
                    _unmatchedSince.Remove(c);
                    // Outside interest: hide unmapped / never-host-synced locals (look like roamers).
                    if (!CharacterTracker.TryGetStableId(c, out short farSid)
                        || !_everHostSyncedIds.Contains(farSid))
                    {
                        if (c.gameObject != null && c.gameObject.activeSelf
                            && (Player.Instance == null || c.gameObject != Player.Instance.gameObject)
                            && !c.name.Contains("RemotePlayer"))
                            c.gameObject.SetActive(false);
                    }
                    continue;
                }

                // Save bodies with no host id stay in the world. If the host is
                // already driving another body of the same name, hide this twin
                // so the player sees one enemy. Do not delete it.
                if (!CharacterTracker.TryGetStableId(c, out short sid))
                {
                    if (HasSyncedTwin(c, allChars, nChars))
                    {
                        if (c.gameObject != null && c.gameObject.activeSelf)
                            c.gameObject.SetActive(false);
                    }
                    _unmatchedSince.Remove(c);
                    continue;
                }

                // Skip if ever synced by the host (even if currently stale)
                if (_everHostSyncedIds.Contains(sid))
                {
                    _unmatchedSince.Remove(c);
                    continue;
                }

                if (_hostSyncedIds.Contains(sid))
                {
                    _unmatchedSince.Remove(c);
                    continue;
                }

                _unmatchedSince.Remove(c);
            }
        }

        /// <summary>
        /// Interpolation setting each driven body had before the client took it over; handed back
        /// when it stops being driven. Keyed by the body: a session reset forgets the ids, not this.
        /// </summary>
        private static readonly Dictionary<Rigidbody, RigidbodyInterpolation> _drivenInterpolation = new Dictionary<Rigidbody, RigidbodyInterpolation>(32); // process-scoped: keyed by body, restored by ReleaseDrivenBody / ReleaseAllDrivenBodies
        private static readonly List<Rigidbody> _drivenScratch = new List<Rigidbody>(16); // process-scoped: scratch buffer, cleared before each use

        /// <summary>
        /// Put a host-driven body at its rendered pose this frame. Rigidbody.MovePosition on these
        /// non-kinematic bodies only moved them at the next physics step (100 Hz), so a creature
        /// stepped 2-3 rendered frames at a time while the pose was sampled every frame. The
        /// transform is written for the frame being drawn and the body for physics (the player's
        /// collisions and hits test it); its velocity stays zero, the host owns the motion. Rigidbody
        /// interpolation would overwrite the written transform with an older physics pose on the
        /// next frame, so it is off while the client drives the body.
        /// </summary>
        private static void WriteShownPose(Character c, Rigidbody rb, Vector3 pos, float rotY)
        {
            Transform t = c.transform;
            Vector3 euler = t.eulerAngles;
            euler.y = rotY;
            Quaternion rot = Quaternion.Euler(euler);
            if (rb != null)
            {
                if (rb.interpolation != RigidbodyInterpolation.None)
                {
                    if (!_drivenInterpolation.ContainsKey(rb))
                    {
                        if (_drivenInterpolation.Count >= 256)
                            PruneDrivenInterpolation();
                        _drivenInterpolation[rb] = rb.interpolation;
                    }
                    rb.interpolation = RigidbodyInterpolation.None;
                }
                rb.position = pos;
                rb.rotation = rot;
                if (!rb.isKinematic)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            t.SetPositionAndRotation(pos, rot);
        }

        /// <summary>The client stopped driving this body: give it back its own interpolation.</summary>
        private static void ReleaseDrivenBody(Rigidbody rb)
        {
            if (ReferenceEquals(rb, null) || !_drivenInterpolation.TryGetValue(rb, out RigidbodyInterpolation was))
                return;
            _drivenInterpolation.Remove(rb);
            if (rb != null)
                rb.interpolation = was;
        }

        /// <summary>Host promotion: every driven body goes back to its own settings before vanilla runs it.</summary>
        internal static void ReleaseAllDrivenBodies()
        {
            foreach (var kv in _drivenInterpolation)
            {
                if (kv.Key != null)
                    kv.Key.interpolation = kv.Value;
            }
            _drivenInterpolation.Clear();
        }

        private static void PruneDrivenInterpolation()
        {
            _drivenScratch.Clear();
            foreach (var kv in _drivenInterpolation)
            {
                if (kv.Key == null)
                    _drivenScratch.Add(kv.Key);
            }
            for (int i = 0; i < _drivenScratch.Count; i++)
                _drivenInterpolation.Remove(_drivenScratch[i]);
            _drivenScratch.Clear();
        }

        private static bool HasSyncedTwin(Character c, Character[] allChars, int count)
        {
            if (c == null || allChars == null) return false;
            string name = StripCloneName(c.name);
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < count; i++)
            {
                Character other = allChars[i];
                if (other == null || other == c) continue;
                if (!string.Equals(StripCloneName(other.name), name, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!CharacterTracker.TryGetStableId(other, out short sid))
                    continue;
                if (_hostSyncedIds.Contains(sid) || _spawnedPhantomIds.Contains(sid))
                    return true;
            }
            return false;
        }

        private static string StripCloneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.EndsWith("(Clone)", System.StringComparison.Ordinal))
                return name.Substring(0, name.Length - 7);
            return name;
        }

    }
}
