using DWMPHorde.Sync;
using UnityEngine;

namespace DWMPHorde.Networking
{
    /// <summary>Budgeted pending host-ID match / timeout resolve (LateUpdate).</summary>
    public static partial class ClientEntityInterpolationService
    {
        private static void TickPendingMatches(float now)
        {
            // 1. Retry pending matches (budgeted). Dense nights used to walk
            // O(pending × tracker) every LateUpdate and FoT-storm on timeout.
            int pendingCount = _pendingMatches.Count;
            if (pendingCount > 0)
            {
                // Cheap cull first (no tracker walk).
                for (int i = pendingCount - 1; i >= 0; i--)
                {
                    if (!IsInClientInterest(_pendingMatches[i].Position))
                        _pendingMatches.RemoveAt(i);
                }
                pendingCount = _pendingMatches.Count;
            }

            if (pendingCount > 0)
            {
                int nPendingChars = CharacterTracker.CopyAll(out Character[] pendingChars);
                int tightDone = 0;
                int timeoutDone = 0;
                int scanned = 0;
                int i = _pendingScanCursor;
                if (i < 0 || i >= pendingCount)
                    i = 0;
                int initial = pendingCount;

                while (scanned < initial && _pendingMatches.Count > 0)
                {
                    if (i >= _pendingMatches.Count)
                        i = 0;
                    PendingEntry p = _pendingMatches[i];
                    scanned++;

                    bool timedOut = now - p.TimeAdded > PendingMatchTimeout;
                    if (timedOut)
                    {
                        if (timeoutDone >= PendingTimeoutResolvesPerFrame)
                        {
                            i++;
                            if (tightDone >= PendingTightRetriesPerFrame)
                                break;
                            continue;
                        }
                        timeoutDone++;

                        if (_hostSyncedIds.Contains(p.HostId))
                        {
                            if (ModRuntime.VerboseLogging)
                                ModRuntime.LegacyInfo($"[Entity] dropping pending (already host-synced): {p.EntityName}(id={p.HostId})");
                            _pendingMatches.RemoveAt(i);
                            continue;
                        }

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
                            {
                                inactive.die();
                                NoteLocalDeathPresentation(inactive, p.HostId);
                            }

                            ApplyEntityPresentation(inactive, p.HostId, p.Clip, p.ClipFrame, p.Alive);

                            _pendingMatches.RemoveAt(i);
                            continue;
                        }

                        Character closest = CharacterTracker.FindClosestByName(
                            p.EntityName, p.Position, _hostSyncedIds);
                        if (closest != null)
                        {
                            float claimDx = closest.transform.position.x - p.Position.x;
                            float claimDz = closest.transform.position.z - p.Position.z;
                            float claimDist = Mathf.Sqrt(claimDx * claimDx + claimDz * claimDz);
                            if (claimDist <= ClaimClosestRadius)
                            {
                                CharacterTracker.AssignId(closest, p.HostId);
                                _hostSyncedIds.Add(p.HostId);
                                _everHostSyncedIds.Add(p.HostId);
                                EnsureEntityAwake(closest);
                                if (ModRuntime.VerboseLogging || claimDist > MatchRadius)
                                    ModRuntime.LegacyInfo(
                                        $"[Entity] claimed closest local {p.EntityName}(id={p.HostId}) d={claimDist:F0}");

                                if (!_states.TryGetValue(p.HostId, out var claimState))
                                {
                                    claimState = new EntityInterpState { isFirst = true };
                                    _states[p.HostId] = claimState;
                                }
                                claimState.staleSince = 0f;
                                Vector3 localPos = closest.transform.position;
                                _displayPositions[p.HostId] = localPos;
                                _displayRotations[p.HostId] = closest.transform.eulerAngles.y;
                                claimState.isFirst = false;
                                claimState.previousPosition = localPos;
                                claimState.previousRotY = closest.transform.eulerAngles.y;
                                claimState.targetPosition = p.Position;
                                claimState.targetRotY = p.RotY;
                                claimState.arrivalTime = now;
                                claimState.hasTarget = true;
                                claimState.alive = p.Alive;
                                if (!p.Alive && closest.alive)
                                {
                                    closest.die();
                                    NoteLocalDeathPresentation(closest, p.HostId);
                                }
                                ApplyEntityPresentation(closest, p.HostId, p.Clip, p.ClipFrame, p.Alive);
                                _pendingMatches.RemoveAt(i);
                                continue;
                            }
                        }

                        Character spawned = SpawnEntityLocally(p.EntityName, p.PrefabPath, p.Position, p.RotY);
                        if (spawned != null)
                        {
                            CharacterTracker.AssignId(spawned, p.HostId);
                            _hostSyncedIds.Add(p.HostId);
                            _everHostSyncedIds.Add(p.HostId);
                            _spawnedPhantomIds.Add(p.HostId);
                            EnsureEntityAwake(spawned);
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
                            spawned.transform.position = p.Position;
                            state.isFirst = false;

                            state.previousPosition = p.Position;
                            state.previousRotY = p.RotY;
                            state.targetPosition = p.Position;
                            state.targetRotY = p.RotY;
                            state.arrivalTime = now;
                            state.hasTarget = true;
                            state.alive = p.Alive;

                            if (!p.Alive && spawned.alive)
                            {
                                spawned.die();
                                NoteLocalDeathPresentation(spawned, p.HostId);
                            }

                            ApplyEntityPresentation(spawned, p.HostId, p.Clip, p.ClipFrame, p.Alive);
                        }

                        _pendingMatches.RemoveAt(i);
                        continue;
                    }

                    // Pre-timeout tight match
                    if (tightDone >= PendingTightRetriesPerFrame)
                    {
                        i++;
                        if (timeoutDone >= PendingTimeoutResolvesPerFrame)
                            break;
                        continue;
                    }
                    tightDone++;

                    Character c = CharacterTracker.FindByPositionAndNameIn(
                        pendingChars, nPendingChars, p.Position, p.EntityName, MatchRadius, _hostSyncedIds);
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
                        {
                            c.die();
                            NoteLocalDeathPresentation(c, p.HostId);
                        }

                        ApplyEntityPresentation(c, p.HostId, p.Clip, p.ClipFrame, p.Alive);

                        _pendingMatches.RemoveAt(i);
                        continue;
                    }

                    i++;
                }

                _pendingScanCursor = _pendingMatches.Count == 0
                    ? 0
                    : (i % _pendingMatches.Count);
            }
        }
    }
}
