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
            if (pendingCount == 0)
                return;

            // The local world or a location is still loading: the copy may not exist yet. Wait,
            // and give it the full retry once loading is over.
            bool loading = IsLocalWorldLoading();

            int nPendingChars = CharacterTracker.CopyAll(out Character[] pendingChars);
            int tightDone = 0;
            int timeoutDone = 0;
            int scanned = 0;
            int i0 = _pendingScanCursor;
            if (i0 < 0 || i0 >= pendingCount)
                i0 = 0;
            int idx = i0;
            int initial = pendingCount;

            while (scanned < initial && _pendingMatches.Count > 0)
            {
                if (idx >= _pendingMatches.Count)
                    idx = 0;
                PendingEntry p = _pendingMatches[idx];
                scanned++;

                if (_hostSyncedIds.Contains(p.HostId))
                {
                    EntitySyncLog.Trace("pend:drop",
                        "[ClientPending] drop already-synced " + p.EntityName
                        + "(id=" + p.HostId + ")", 2f);
                    _pendingMatches.RemoveAt(idx);
                    continue;
                }

                if (loading)
                {
                    p.TimeAdded = now;
                    _pendingMatches[idx] = p;
                }

                bool timedOut = !loading && now - p.TimeAdded > PendingMatchTimeout;
                if (timedOut)
                {
                    if (timeoutDone >= PendingTimeoutResolvesPerFrame)
                    {
                        idx++;
                        if (tightDone >= PendingTightRetriesPerFrame)
                            break;
                        continue;
                    }
                    timeoutDone++;
                    ResolvePendingTimeout(p, pendingChars, nPendingChars, now);
                    _pendingMatches.RemoveAt(idx);
                    continue;
                }

                // Pre-timeout match: the save twin (it may have just loaded), else a body at the
                // host position that is not another body's save twin.
                if (tightDone >= PendingTightRetriesPerFrame)
                {
                    idx++;
                    if (timeoutDone >= PendingTimeoutResolvesPerFrame)
                        break;
                    continue;
                }
                tightDone++;

                Character c = FindSaveTwin(p.HostId, p.SaveId, p.EntityName, p.Position);
                string how = "save-id";
                if (c == null)
                {
                    _matchHostId = p.HostId;
                    c = CharacterTracker.FindByPositionAndNameIn(
                        pendingChars, nPendingChars, p.Position, p.EntityName, MatchRadius, _hostSyncedIds,
                        RejectOtherSaveTwin);
                    how = "tight";
                }
                if (c != null)
                {
                    AdoptPending(c, p, now, snapToHost: how == "save-id", how);
                    _pendingMatches.RemoveAt(idx);
                    continue;
                }

                idx++;
            }

            _pendingScanCursor = _pendingMatches.Count == 0
                ? 0
                : (idx % _pendingMatches.Count);
        }

        /// <summary>
        /// The pending wait is over: the save twin, then the old by-name fallbacks (a save body
        /// asleep near the host position, the closest of that name, the one story character of
        /// that name), each skipping other bodies' save twins; else a phantom.
        /// </summary>
        private static void ResolvePendingTimeout(PendingEntry p, Character[] chars, int nChars, float now)
        {
            Character twin = FindSaveTwin(p.HostId, p.SaveId, p.EntityName, p.Position);
            if (twin != null)
            {
                AdoptPending(twin, p, now, snapToHost: true, "save-id");
                return;
            }

            _matchHostId = p.HostId;
            Character inactive = FindInactiveCharacter(p.EntityName, p.Position, MatchRadius * 2f);
            if (inactive != null)
            {
                AdoptPending(inactive, p, now, snapToHost: false, "activated");
                return;
            }

            _matchHostId = p.HostId;
            Character closest = CharacterTracker.FindClosestByName(
                p.EntityName, p.Position, _hostSyncedIds, RejectOtherSaveTwin);
            if (closest != null)
            {
                float claimDx = closest.transform.position.x - p.Position.x;
                float claimDz = closest.transform.position.z - p.Position.z;
                if (claimDx * claimDx + claimDz * claimDz <= MatchRadius * MatchRadius)
                {
                    AdoptPending(closest, p, now, snapToHost: false, "claimed");
                    return;
                }
            }

            Character sole = FindSoleUnmapped(chars, nChars, p.EntityName);
            if (sole != null && SamePresentationWorld(sole.transform.position, p.Position))
            {
                AdoptPending(sole, p, now, snapToHost: true, "adopted sole");
                return;
            }

            Character spawned = SpawnEntityLocally(p.EntityName, p.PrefabPath, p.Position, p.RotY);
            if (spawned == null)
                return;
            CharacterTracker.AssignId(spawned, p.HostId);
            ApplyHostLook(spawned, p.HostId);
            _hostSyncedIds.Add(p.HostId);
            _everHostSyncedIds.Add(p.HostId);
            _spawnedPhantomIds.Add(p.HostId);
            EnsureEntityAwake(spawned);
            spawned.transform.position = p.Position;
            EntitySyncLog.Event(() =>
                "[ClientPending] phantom spawn " + p.EntityName + "(id=" + p.HostId
                + ") save=" + p.SaveId + " clip=" + (p.Clip ?? ""));
            StartDrivingPending(spawned, p, now, p.Position, p.RotY);
        }

        /// <summary>
        /// A local body is the copy of a pending host id. <paramref name="snapToHost"/>: put it at
        /// the host pose now (a save twin can be anywhere its save left it); otherwise it blends
        /// from where it stands.
        /// </summary>
        private static void AdoptPending(Character c, PendingEntry p, float now, bool snapToHost, string how)
        {
            BindHostId(c, p.HostId);
            EntitySyncLog.Event(() =>
                "[ClientPending] " + how + " " + p.EntityName + "(id=" + p.HostId + " save=" + p.SaveId + ")");
            if (snapToHost)
            {
                c.transform.position = p.Position;
                Vector3 euler = c.transform.eulerAngles;
                euler.y = p.RotY;
                c.transform.eulerAngles = euler;
                StartDrivingPending(c, p, now, p.Position, p.RotY);
            }
            else
            {
                StartDrivingPending(c, p, now, c.transform.position, c.transform.eulerAngles.y);
            }
        }

        private static void StartDrivingPending(Character c, PendingEntry p, float now, Vector3 shownPos, float shownRotY)
        {
            if (!_states.TryGetValue(p.HostId, out var state))
            {
                state = new EntityInterpState { isFirst = true };
                _states[p.HostId] = state;
            }
            state.staleSince = 0f;
            _displayPositions[p.HostId] = shownPos;
            _displayRotations[p.HostId] = shownRotY;
            state.isFirst = false;
            state.previousPosition = shownPos;
            state.previousRotY = shownRotY;
            state.targetPosition = p.Position;
            state.targetRotY = p.RotY;
            state.arrivalTime = now;
            state.hasTarget = true;
            state.alive = p.Alive;
            ApplyAuthoritativeBody(c, p.HostId, p.Alive, p.Downed, p.HealthPct, p.Clip, p.ClipFrame, state,
                p.HostTime, p.Animating);
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
                if (RejectOtherSaveTwin(c))
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
