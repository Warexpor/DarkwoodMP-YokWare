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
        private static bool _localDreamActive;
        private static readonly Dictionary<int, bool> _remoteDreamActive = new Dictionary<int, bool>();
        private static readonly Dictionary<int, string> _currentDreamPreset = new Dictionary<int, string>();
        private static string _localDreamPreset;

        private static readonly Dictionary<int, Vector3> _preDreamPosition = new Dictionary<int, Vector3>();
        private static readonly Dictionary<int, string> _preDreamGridName = new Dictionary<int, string>();

        private static bool _worldFrozen;
        private static int _savedGameTime;
        private static readonly HashSet<Character> _frozenWorldCharacters = new HashSet<Character>();

        /// <summary>Peer already played startTransition via early CutsceneSync (before DreamStarted).</summary>
        private static bool _earlyEntryTransitionPlayed;
        private static float _earlyEntryTransitionDoneAt;
        /// <summary>True while StartRemoteDreamTransition audio/video is running (blocks double Play).</summary>
        private static bool _remoteEntryTransitionPlaying;
        private static string _remoteEntryAudioId;

        /// <summary>Client story-end defer awaiting host acceptance or rejection.</summary>
        private static bool _storyEndDeferPending;
        private static float _storyEndDeferDeadline;
        private const float StoryEndDeferTimeoutSec = 15f;
        private static Coroutine _storyEndWatchdog;

        /// <summary>
        /// Host already broadcast DreamEnded at initiateEndDreaming. endDreaming must not
        /// send a second copy. A host-ordered client exit plays the same transition video.
        /// </summary>
        private static bool _dreamEndBroadcastSent;
        private static bool _hostOrderedDreamEnd;

        /// <summary>True when the local player's entry transition was intercepted by DreamEntryClientPatch.</summary>
        public static bool EntryTransitionPlayedLocally => _earlyEntryTransitionPlayed;

        public static bool IsStoryEndDeferPending => _storyEndDeferPending;

        /// <summary>Client may run vanilla initiateEndDreaming for a host-ordered story exit.</summary>
        public static bool IsHostOrderedDreamEnd => _hostOrderedDreamEnd;

        /// <summary>After sending DreamEnded, wait for host acceptance or rejection.</summary>
    }
}
