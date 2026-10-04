using DWMPHorde.Networking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace DWMPHorde.Sync
{
    /// <summary>
    /// Host migration in the middle of a dream. Every peer keeps its own copy of the world and of
    /// the dream pad, so the dream carries over: the elected survivor runs the pad's AI and owns
    /// the session (story end, all-dead end); the others reconnect while staying on their pad and
    /// confirm with DreamEntered. A peer that is not inside the dream yet (entry video, pad still
    /// loading) cannot finish the entry without the host that started it, and leaves it.
    /// </summary>
    internal static partial class DreamSyncManager
    {
        /// <summary>How long the new host waits for the survivors to rejoin before acting alone.</summary>
        private const float SurvivorReturnGraceSec = 25f;

        /// <summary>This peer's dream survived a host loss; the new host's session bulk confirms or ends it.</summary>
        private static bool _carriedOverHostLoss; // reset-in: OnDisconnectedCleanup
        /// <summary>Story end this client deferred to the host, kept to re-send to a new host.</summary>
        private static string _storyEndDeferPreset; // reset-in: ClearStoryEndDefer
        private static string _storyEndDeferOutcome; // reset-in: ClearStoryEndDefer
        private static Coroutine _promotedSettle; // reset-in: OnDisconnectedCleanup

        /// <summary>True while this peer is inside the shared dream (pad loaded, dreaming).</summary>
        private static bool IsInsideDream =>
            _localDreamActive && Dreams.Instance != null && Dreams.Instance.dreaming;

        /// <summary>
        /// Survivor, at host loss, before the old host's id is cleaned up. Moves the old host's
        /// dream bookkeeping to the new host (or drops it on the elected peer itself).
        /// </summary>
        internal static void OnHostMigrating(int deadHost, int newHost, bool localElected)
        {
            if (!DreamSession.IsActive && !IsDreamActive)
                return;
            if (!IsInsideDream)
            {
                ModRuntime.LegacyInfo("[DreamSync] Host lost before this peer was inside the dream — leaving it");
                ForceLocalDreamCleanup("hostLostMidDream");
                return;
            }

            CancelPendingEntries();
            Rekey(_remoteDreamActive, deadHost, localElected ? 0 : newHost);
            Rekey(_currentDreamPreset, deadHost, localElected ? 0 : newHost);
            Rekey(_peerEntryDeadline, deadHost, localElected ? 0 : newHost);
            if (_dreamEntryConfirmed.Remove(deadHost) && !localElected)
                _dreamEntryConfirmed.Add(newHost);
            if (localElected)
            {
                // The new host is local, not a remote in its own dream.
                _remoteDreamActive.Remove(newHost);
                _currentDreamPreset.Remove(newHost);
                _peerEntryDeadline.Remove(newHost);
                _dreamEntryConfirmed.Remove(newHost);
            }
            // Pre-dream pose is always the local one; only its key moves.
            Rekey(_preDreamPosition, deadHost, newHost);
            Rekey(_preDreamGridName, deadHost, newHost);
            _carriedOverHostLoss = !localElected;
            // A story end deferred to the old host is re-sent to the new one; the reconnect
            // must not run its timeout out.
            if (_storyEndDeferPending && !localElected)
                _storyEndDeferDeadline = Time.realtimeSinceStartup + StoryEndDeferTimeoutSec + SurvivorReturnGraceSec;
            ModRuntime.LegacyInfo(
                $"[DreamSync] Dream carried over host loss: p{deadHost} → p{newHost}"
                + (localElected ? " (local is the new host)" : ""));
        }

        /// <summary>Remote ids flagged in the dream (connected or not).</summary>
        internal static IEnumerable<int> RemoteDreamParticipantIds()
        {
            foreach (var kv in _remoteDreamActive)
            {
                if (kv.Value)
                    yield return kv.Key;
            }
        }

        private static void Rekey<T>(Dictionary<int, T> map, int from, int to)
        {
            if (!map.TryGetValue(from, out T value))
                return;
            map.Remove(from);
            if (to > 0)
                map[to] = value;
        }

        /// <summary>
        /// Elected survivor, right after it became host mid-dream. The others count as in the
        /// dream while they rejoin; a story end this peer had deferred to the old host, or an
        /// all-dead end, runs once they are back (or the grace ran out), so they get it too.
        /// </summary>
        internal static void OnPromotedToHost(IEnumerable<int> survivors)
        {
            if (!IsInsideDream)
                return;
            var ids = new List<int>();
            foreach (int id in survivors)
            {
                if (id <= 0) continue;
                ids.Add(id);
                NoteRemoteInDream(id);
            }

            string deferredOutcome = _storyEndDeferPending ? _storyEndDeferOutcome : null;
            ClearStoryEndDefer();
            _hostOrderedDreamEnd = false;

            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null)
                return;
            if (_promotedSettle != null)
                ctrl.StopCoroutine(_promotedSettle);
            _promotedSettle = ctrl.StartCoroutine(PromotedHostSettle(ids, deferredOutcome));
            ModRuntime.LegacyInfo(
                $"[DreamSync] Promoted host mid-dream — waiting for {ids.Count} survivor(s)"
                + (deferredOutcome != null ? ", then story end '" + deferredOutcome + "'" : ""));
        }

        private static IEnumerator PromotedHostSettle(List<int> survivors, string deferredOutcome)
        {
            int sid = DreamSession.SessionId;
            float deadline = Time.unscaledTime + SurvivorReturnGraceSec;
            while (Time.unscaledTime < deadline)
            {
                bool all = true;
                for (int i = 0; i < survivors.Count; i++)
                {
                    if (!_dreamEntryConfirmed.Contains(survivors[i]))
                    {
                        all = false;
                        break;
                    }
                }
                if (all)
                    break;
                yield return null;
            }
            _promotedSettle = null;
            if (!IsInsideDream || DreamSession.SessionId != sid)
                yield break;

            if (!string.IsNullOrEmpty(deferredOutcome) && Dreams.Instance != null)
            {
                // The story end this peer asked the old host for; now it is the host's own.
                ModRuntime.LegacyInfo("[DreamSync] Promoted host runs the deferred story end: " + deferredOutcome);
                Dreams.Instance.outcome = deferredOutcome;
                Dreams.Instance.initiateEndDreaming();
                yield break;
            }
            FinalDreamsceneManager.CheckAllDeadAfterPromote();
        }

        /// <summary>
        /// Client, the session bulk of the host it (re)connected to while inside a dream. The same
        /// session: confirm we are on the pad (the host freezes our stand-in until then), re-send a
        /// death or a deferred story end the old host never answered. Any other answer: the dream
        /// is over for the host, so it is over here too.
        /// </summary>
        /// <returns>True when the bulk was about a dream this peer is inside.</returns>
        internal static bool OnSessionBulkWhileInsideDream(DreamSessionBulkMessage msg, LanNetworkManager net)
        {
            if (net == null || net.Role != NetworkRole.Client || !IsInsideDream)
                return false;
            bool carried = _carriedOverHostLoss;
            _carriedOverHostLoss = false;

            bool same = msg.SessionActive && msg.SessionId != 0 && msg.SessionId == DreamSession.SessionId;
            if (!same)
            {
                ModRuntime.LegacyInfo(
                    $"[DreamSync] Host has no dream session {DreamSession.SessionId} (bulk active={msg.SessionActive}"
                    + $" session={msg.SessionId}) — leaving the dream");
                ForceLocalDreamCleanup("hostLostMidDream");
                return true;
            }

            net.Send(NetMessageType.DreamEntered,
                w => new DreamEnteredMessage().Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            if (FinalDreamsceneManager.IsLocalDead)
                FinalDreamsceneManager.ResendLocalDeath();
            if (_storyEndDeferPending && !string.IsNullOrEmpty(_storyEndDeferOutcome))
            {
                string preset = _storyEndDeferPreset ?? "";
                string outcome = _storyEndDeferOutcome;
                net.Send(NetMessageType.DreamEnded,
                    w => DreamEndedMessage.Build(preset, outcome).Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
                BeginStoryEndDefer(preset, outcome);
            }
            ModRuntime.LegacyInfo(
                "[DreamSync] Still in dream session " + DreamSession.SessionId
                + (carried ? " after host migration" : "") + " — DreamEntered re-sent");
            return true;
        }
    }
}
