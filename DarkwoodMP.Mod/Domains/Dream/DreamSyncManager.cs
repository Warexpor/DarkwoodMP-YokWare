using DG.Tweening;
using DWMPHorde.Networking;
using DWMPHorde.Players;
using DWMPHorde.Spectator;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Video;

namespace DWMPHorde.Sync
{
    internal static partial class DreamSyncManager
    {
        private static bool _localDreamActive; // reset-in: OnDisconnectedCleanup
        private static readonly Dictionary<int, bool> _remoteDreamActive = new Dictionary<int, bool>(); // reset-in: ClearRemoteDreamRoster
        private static readonly Dictionary<int, string> _currentDreamPreset = new Dictionary<int, string>(); // reset-in: OnDisconnectedCleanup
        private static string _localDreamPreset; // reset-in: OnDisconnectedCleanup

        private static readonly Dictionary<int, Vector3> _preDreamPosition = new Dictionary<int, Vector3>(); // reset-in: OnDisconnectedCleanup
        private static readonly Dictionary<int, string> _preDreamGridName = new Dictionary<int, string>(); // reset-in: OnDisconnectedCleanup

        private static bool _worldFrozen; // reset-in: OnDisconnected
        private static int _savedGameTime; // reset-in: OnDisconnected
        private static readonly HashSet<Character> _frozenWorldCharacters = new HashSet<Character>(); // reset-in: OnDisconnected

        /// <summary>Peer already played startTransition via early CutsceneSync (before DreamStarted).</summary>
        private static bool _earlyEntryTransitionPlayed; // reset-in: OnDisconnectedCleanup
        private static float _earlyEntryTransitionDoneAt; // reset-in: OnDisconnectedCleanup
        /// <summary>True while StartRemoteDreamTransition audio/video is running (blocks double Play).</summary>
        private static bool _remoteEntryTransitionPlaying; // reset-in: OnDisconnectedCleanup
        private static string _remoteEntryAudioId; // reset-in: OnDisconnectedCleanup

        /// <summary>Client story-end defer awaiting host acceptance or rejection.</summary>
        private static bool _storyEndDeferPending; // reset-in: ClearStoryEndDefer
        private static float _storyEndDeferDeadline; // reset-in: ClearStoryEndDefer
        private const float StoryEndDeferTimeoutSec = 15f;
        private static Coroutine _storyEndWatchdog; // reset-in: ClearStoryEndDefer

        /// <summary>
        /// Host already broadcast DreamEnded at initiateEndDreaming. endDreaming must not
        /// send a second copy. A host-ordered client exit plays the same transition video.
        /// </summary>
        private static bool _dreamEndBroadcastSent; // reset-in: OnDisconnectedCleanup
        private static bool _hostOrderedDreamEnd; // reset-in: OnDisconnectedCleanup

        /// <summary>
        /// Bumped whenever this peer's dream session is torn down (disconnect, reject, cleanup,
        /// failed load, remote end). Entry coroutines capture it plus the session id and bail after
        /// every yield once either no longer matches, so a cancelled entry cannot load a pad or
        /// teleport the player into a sessionless dream.
        /// </summary>
        private static int _entryGeneration; // process-scoped: monotonic; bumped on disconnect so stale entry coroutines bail

        private static void CancelPendingEntries() => _entryGeneration++;

        private static bool EntryStale(int generation, int sessionId)
        {
            return generation != _entryGeneration
                || !DreamSession.IsActive
                || (sessionId != 0 && DreamSession.SessionId != sessionId);
        }

        /// <summary>True when the local player's entry transition was intercepted by DreamEntryClientPatch.</summary>
        public static bool EntryTransitionPlayedLocally => _earlyEntryTransitionPlayed;

        public static bool IsStoryEndDeferPending => _storyEndDeferPending;

        /// <summary>Client may run vanilla initiateEndDreaming for a host-ordered story exit.</summary>
        public static bool IsHostOrderedDreamEnd => _hostOrderedDreamEnd;

        /// <summary>After sending DreamEnded, wait for host acceptance or rejection.</summary>
    }
}
