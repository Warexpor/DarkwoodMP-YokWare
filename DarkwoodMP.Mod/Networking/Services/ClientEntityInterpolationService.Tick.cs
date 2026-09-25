using System.Collections.Generic;
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

                if (elapsed > MaxInterpDelay)
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
