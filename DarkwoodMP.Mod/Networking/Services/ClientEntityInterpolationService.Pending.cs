using DWMPHorde.Logging;
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
                            EntitySyncLog.Trace("pend:drop",
                                "[ClientPending] drop already-synced " + p.EntityName
                                + "(id=" + p.HostId + ")", 2f);
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
                            EntitySyncLog.Event(() =>
                                "[ClientPending] activated " + p.EntityName + "(id=" + p.HostId + ")");

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

                            ApplyAuthoritativeBody(inactive, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, state);

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
                            if (claimDist <= MatchRadius)
                            {
                                CharacterTracker.AssignId(closest, p.HostId);
                                _hostSyncedIds.Add(p.HostId);
                                _everHostSyncedIds.Add(p.HostId);
                                EnsureEntityAwake(closest);
                                EntitySyncLog.Event(() =>
                                    "[ClientPending] claimed " + p.EntityName + "(id=" + p.HostId
                                    + ") d=" + claimDist.ToString("F0"));

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
                                ApplyAuthoritativeBody(closest, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, claimState);
                                _pendingMatches.RemoveAt(i);
                                continue;
                            }
                        }

                        Character sole = FindSoleUnmapped(pendingChars, nPendingChars, p.EntityName);
                        if (sole != null && SamePresentationWorld(sole.transform.position, p.Position))
                        {
                            CharacterTracker.AssignId(sole, p.HostId);
                            _hostSyncedIds.Add(p.HostId);
                            _everHostSyncedIds.Add(p.HostId);
                            sole.transform.position = p.Position;
                            Vector3 euler = sole.transform.eulerAngles;
                            euler.y = p.RotY;
                            sole.transform.eulerAngles = euler;
                            EnsureEntityAwake(sole);
                            EntitySyncLog.Event(() =>
                                "[ClientPending] adopted sole " + p.EntityName + "(id=" + p.HostId + ")");

                            if (!_states.TryGetValue(p.HostId, out var soleState))
                            {
                                soleState = new EntityInterpState { isFirst = true };
                                _states[p.HostId] = soleState;
                            }
                            soleState.staleSince = 0f;
                            _displayPositions[p.HostId] = p.Position;
                            _displayRotations[p.HostId] = p.RotY;
                            soleState.isFirst = false;
                            soleState.previousPosition = p.Position;
                            soleState.previousRotY = p.RotY;
                            soleState.targetPosition = p.Position;
                            soleState.targetRotY = p.RotY;
                            soleState.arrivalTime = now;
                            soleState.hasTarget = true;
                            soleState.alive = p.Alive;
                            ApplyAuthoritativeBody(sole, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, soleState);
                            _pendingMatches.RemoveAt(i);
                            continue;
                        }

                        Character spawned = SpawnEntityLocally(p.EntityName, p.PrefabPath, p.Position, p.RotY);
                        if (spawned != null)
                        {
                            CharacterTracker.AssignId(spawned, p.HostId);
                            _hostSyncedIds.Add(p.HostId);
                            _everHostSyncedIds.Add(p.HostId);
                            _spawnedPhantomIds.Add(p.HostId);
                            EnsureEntityAwake(spawned);
                            EntitySyncLog.Event(() =>
                                "[ClientPending] phantom spawn " + p.EntityName + "(id=" + p.HostId
                                + ") clip=" + (p.Clip ?? ""));

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

                            ApplyAuthoritativeBody(spawned, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, state);
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

                        ApplyAuthoritativeBody(c, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, state);

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

        /// <summary>
        /// The one save body with this name that the host has not tagged yet.
        /// Story characters are unique, so this is that character. Packs of the
        /// same enemy return null and stay on the close-range match.
        /// </summary>
        private static Character FindSoleUnmapped(Character[] chars, int count, string entityName)
        {
            if (chars == null || count <= 0 || string.IsNullOrEmpty(entityName))
                return null;
            string search = entityName;
            if (search.EndsWith("(Clone)"))
                search = search.Substring(0, search.Length - 7);

            Character sole = null;
            for (int i = 0; i < count; i++)
            {
                Character c = chars[i];
                if (c == null) continue;
                if (c.name.Contains("Player") || c.name.Contains("RemotePlayer"))
                    continue;
                if (CharacterTracker.TryGetStableId(c, out short sid)
                    && (_hostSyncedIds.Contains(sid) || sid != 0))
                    continue;

                string cname = c.name;
                if (cname.EndsWith("(Clone)"))
                    cname = cname.Substring(0, cname.Length - 7);
                if (!string.Equals(cname, search, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                if (sole != null)
                    return null;
                sole = c;
            }
            return sole;
        }

        /// <summary>
        /// Dream pad and overworld share NPC names. A sole name match is only
        /// the same character when both positions sit in the same place.
        /// </summary>
        private static bool SamePresentationWorld(Vector3 bodyPos, Vector3 hostPos)
        {
            const float dreamRadiusSq = 5000f * 5000f;
            Transform dreamTf = DreamSyncManager.GetDreamLocationTransform();
            if ((DreamSyncManager.IsLocalDreamActive || DreamSession.IsActive) && dreamTf == null)
                return false;
            if (dreamTf == null)
                return true;
            bool bodyInDream = (bodyPos - dreamTf.position).sqrMagnitude <= dreamRadiusSq;
            bool hostInDream = (hostPos - dreamTf.position).sqrMagnitude <= dreamRadiusSq;
            return bodyInDream == hostInDream;
        }
    }
}
