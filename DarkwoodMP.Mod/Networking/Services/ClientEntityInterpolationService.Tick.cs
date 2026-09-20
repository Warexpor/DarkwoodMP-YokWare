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
                    bool isPhantom = _spawnedPhantomIds.Contains(id);
                    Character staleChar = CharacterTracker.FindByStableId(id);
                    bool isCorpse = false;
                    if (staleChar != null)
                    {
                        if (!staleChar.alive || _deathAnimationPlayed.Contains(id))
                            isCorpse = true;
                        else
                        {
                            if (!state.CorpseItemChecked)
                            {
                                state.HasCorpseItem = staleChar.GetComponent<Item>() != null;
                                state.CorpseItemChecked = true;
                            }
                            isCorpse = state.HasCorpseItem;
                        }
                    }

                    // Keep lootable corpses when host streaming stops.
                    if (isCorpse)
                    {
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
                        _hostSyncedIds.Remove(id);
                        // Keep phantom id so we don't re-spawn; keep ever-synced for unmatched skip.
                        continue;
                    }

                    if (isPhantom && state.staleSince > 0f && now - state.staleSince > PhantomCleanupDelay)
                    {
                        if (staleChar != null)
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.LegacyInfo($"[Entity] destroying phantom: id={id}");
                            Object.Destroy(staleChar.gameObject);
                        }
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
                        _hostSyncedIds.Remove(id);
                        _spawnedPhantomIds.Remove(id);
                        _everHostSyncedIds.Remove(id);
                    }
                    else if (!isPhantom && state.staleSince > 0f && now - state.staleSince > PhantomCleanupDelay)
                    {
                        // Host stopped streaming after removeMe or leaving the
                        // world. Remove the stale local entity.
                        if (staleChar != null && staleChar.alive)
                        {
                            if (!state.CorpseItemChecked)
                            {
                                state.HasCorpseItem = staleChar.GetComponent<Item>() != null;
                                state.CorpseItemChecked = true;
                            }
                            if (!state.HasCorpseItem)
                            {
                                if (ModRuntime.VerboseLogging)
                                    ModRuntime.LegacyInfo($"[Entity] destroying stale host-synced: {staleChar.name}(id={id})");
                                Object.Destroy(staleChar.gameObject);
                            }
                            else
                            {
                                Rigidbody rb = state.CachedRb != null
                                    ? state.CachedRb
                                    : staleChar.GetComponent<Rigidbody>();
                                if (rb != null)
                                    rb.isKinematic = false;
                            }
                        }
                        else if (staleChar != null)
                        {
                            Rigidbody rb = state.CachedRb != null
                                ? state.CachedRb
                                : staleChar.GetComponent<Rigidbody>();
                            if (rb != null)
                                rb.isKinematic = false;
                        }
                        _staleKeys.Add(id);
                        _displayPositions.Remove(id);
                        _displayRotations.Remove(id);
                        _hostSyncedIds.Remove(id);
                        _everHostSyncedIds.Remove(id);
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
            // Only cull inside client interest. Far save NPCs must stay so claim can
            // map them when the player walks up (host streams at EntityActivationRange).
            if (!_receivedFirstSnapshot) return;
            if (now - _firstSnapshotTime < UnmatchedCleanupDelay) return;
            if (now < _nextUnmatchedCleanupTime) return;
            _nextUnmatchedCleanupTime = now + UnmatchedCleanupInterval;

            // While pending claim/phantom processing is busy, do not destroy unmapped save locals;
            // they are the claim targets. Destroying them mid-storm caused mass desync at POIs.
            bool pendingBusy = _pendingMatches.Count > 0;

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

                // Client save NPCs with no host id: AI is frozen and they never
                // receive EntityState → permanent stale dogs/crows. Destroy after grace
                // once host has been streaming (same window as id'd unmatched).
                // Keep pending matches because those objects may still be claim targets.
                if (!CharacterTracker.TryGetStableId(c, out short sid))
                {
                    if (pendingBusy)
                    {
                        _unmatchedSince.Remove(c);
                        continue;
                    }
                    if (!_unmatchedSince.TryGetValue(c, out float firstUnmapped))
                    {
                        _unmatchedSince[c] = now;
                        continue;
                    }
                    if (now - firstUnmapped > UnmatchedCleanupDelay)
                    {
                        if (ModRuntime.VerboseLogging)
                            ModRuntime.LegacyInfo($"[Entity] destroying unmapped local: {c.name}");
                        _unmatchedSince.Remove(c);
                        Object.Destroy(c.gameObject);
                    }
                    continue;
                }

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

                if (pendingBusy)
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

    }
}
