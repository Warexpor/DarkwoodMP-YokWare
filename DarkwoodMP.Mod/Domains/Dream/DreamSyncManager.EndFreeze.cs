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
        public static void OnRemoteDreamEnded(int playerId, string outcomeName = "")
        {
            CancelPendingEntries();
            _chainPocketLoading = null;
            // Before the host's wake-up can reach this peer (its GameEventsFired comes after this).
            NoteOutcomeWorldEventLatches(
                _currentDreamPreset.TryGetValue(playerId, out var notePreset) ? notePreset
                    : Dreams.Instance != null && Dreams.Instance.preset != null ? Dreams.Instance.preset.name : null);
            if (!_remoteDreamActive.TryGetValue(playerId, out bool active) || !active)
            {
                // Host-ordered story end may arrive while we only track via DreamSession /
                // local dreaming (remote flag already true from DreamStarted).
                if (Dreams.Instance != null && Dreams.Instance.dreaming
                    && TryBeginHostOrderedStoryEnd(outcomeName))
                    return;
                ClearOutcomeWorldReplay();
                return;
            }

            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            // Failure cleanup already AbortStarting; do not party-lock from a reject / disconnect.
            if (!DreamSession.IsFailureCleanup(outcomeName))
                MarkDreamCompleted(playerId, presetName);

            _remoteDreamActive[playerId] = false;

            ModRuntime.LegacyInfo($"[DreamSync] Remote dream ended (p{playerId}): {presetName}, outcome={outcomeName}");

            // Story exit: play the same outcome video as host, then vanilla endDreaming.
            // Hard ApplyRemoteDreamCleanup was breaking the client (no video / stuck world).
            if (Dreams.Instance != null && Dreams.Instance.dreaming
                && TryBeginHostOrderedStoryEnd(outcomeName))
            {
                _currentDreamPreset.Remove(playerId);
                // Keep pre-dream restore data until endDreaming; proxies unfreeze after video.
                return;
            }

            if (Dreams.Instance != null && Dreams.Instance.dreaming && Dreams.Instance.preset != null)
            {
                // ApplyRemoteDreamCleanup unfreezes after restore.
                ApplyRemoteDreamCleanup(outcomeName);
            }
            else
            {
                // Ended before our pad loaded (still in the entry video): the cancelled entry
                // coroutine leaves the overlay, EnteringDream and the black screen to us.
                if (_earlyEntryTransitionPlayed || _remoteEntryTransitionPlaying || Core.EnteringDream)
                {
                    FadeOutDreamTransition();
                    _earlyEntryTransitionPlayed = false;
                    _earlyEntryTransitionDoneAt = 0f;
                    _remoteEntryTransitionPlaying = false;
                    _remoteEntryAudioId = null;
                    FadeInDreamBlackScreen();
                    ReleaseDreamInputLocks();
                }
                if (presetName != null)
                {
                    CleanupDreamScene(presetName);
                    RemoveDreamCameraEffects(presetName);
                }
                RestorePreDreamState(playerId);
                UnfreezeWorld();
                var net = ModRuntime.Network;
                if (net != null && net.IsConnected && Player.Instance != null)
                    net.TeleportRemoteProxyTo(Player.Instance._transform.position, 0f);
                ReplayOutcomeWorldOnly(presetName, outcomeName);
            }

            FinalDreamsceneManager.OnDreamEnded();
            ModRuntime.Network?.GameEventHandlers?.ClearPendingDreamGameEvents();

            var unfreezeNet = ModRuntime.Network;
            if (unfreezeNet != null)
            {
                foreach (var proxy in unfreezeNet.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            _currentDreamPreset.Remove(playerId);
            _preDreamPosition.Remove(playerId);
            _preDreamGridName.Remove(playerId);
            _remoteDreamActive.Remove(playerId);
            // A chain that ends by death / all-dead never reaches OnLocalDreamEnded, and the local
            // pocket-id entry OnDreamChain sets stays true: IsDreamActive (and with it every
            // dream-scoped resolver) would stay on after the party is back in the overworld.
            ClearRemoteDreamRoster();
        }

        public static void OnDisconnected()
        {
            try
            {
                OnDisconnectedCleanup();
            }
            finally
            {
                // The freeze statics must not outlive the session even when the dream cleanup
                // above took the ApplyRemoteDreamCleanup branch or threw part-way.
                UnfreezeWorld(restoreTime: false); // clears _worldFrozen
                _savedGameTime = 0;
                _frozenWorldCharacters.Clear();
                _frozenByComponent.Clear();
                ClearOutcomeWorldReplay();
                // The story-end watchdog only stops via ForceLocalDreamCleanup, which runs above
                // only while a dream is still flagged; a stale defer would gate the next session.
                ClearStoryEndDefer();
            }
        }

        private static void OnDisconnectedCleanup()
        {
            CancelPendingEntries();
            _hostEntryFreeze = false;
            FinalDreamsceneManager.OnDisconnected();

            // Unfreeze any frozen proxies
            var net = ModRuntime.Network;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            // Tear down local dream / entry overlay before clearing session maps.
            // Without this, disconnect mid-dream leaves FreezeWorld + EnteringDream stuck.
            bool localDreaming = Dreams.Instance != null && Dreams.Instance.dreaming;
            if (localDreaming || _localDreamActive || _earlyEntryTransitionPlayed || Core.EnteringDream
                || DreamSession.IsActive)
            {
                ForceLocalDreamCleanup("disconnected");
            }
            else
            {
                UnfreezeWorld(restoreTime: false);
                Core.EnteringDream = false;
            }

            // Clean up any active remote dreams
            foreach (var kvp in _currentDreamPreset)
            {
                CleanupDreamScene(kvp.Value);
                RemoveDreamCameraEffects(kvp.Value);
            }
            // The saved pose is only a restore target for a player actually stranded on the pad;
            // a stale entry from an earlier, finished dream used to teleport the client back to
            // that dream's start on any disconnect.
            var localPlayer = Player.Instance;
            if (localPlayer != null && _preDreamPosition.Count > 0
                && ClientStateBackup.IsDreamPadCoordinate(localPlayer._transform.position))
            {
                foreach (var kvp in _preDreamPosition)
                {
                    RestorePreDreamState(kvp.Key);
                    break;
                }
            }

            _localDreamActive = false;
            _localDreamPreset = null;
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
            _entryTransitionSeen = false;
            _remoteEntryHasVideo = true;
            _chainPocketLoading = null;
            _carriedOverHostLoss = false;
            if (_promotedSettle != null && Singleton<Controller>.Instance != null)
                Singleton<Controller>.Instance.StopCoroutine(_promotedSettle);
            _promotedSettle = null;
            _dreamEndBroadcastSent = false;
            _hostOrderedDreamEnd = false;
            ClearRemoteDreamRoster();
            _currentDreamPreset.Clear();
            _preDreamPosition.Clear();
            _preDreamGridName.Clear();
            DreamSession.Reset();
        }

        /// <summary>Pad spawn failed after FreezeWorld; clear the session and unfreeze peers.</summary>
        private static void AbortFailedRemoteDreamLoad(int playerId, string reason)
        {
            CancelPendingEntries();
            if (_remoteDreamActive.ContainsKey(playerId))
                _remoteDreamActive[playerId] = false;
            _remoteDreamActive.Remove(playerId);
            _currentDreamPreset.Remove(playerId);
            _preDreamPosition.Remove(playerId);
            _preDreamGridName.Remove(playerId);
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            Core.EnteringDream = false;
            try
            {
                var ui = Singleton<UI>.Instance;
                if (ui != null)
                {
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                    try { ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f); }
                    catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
            // A prepared flag left behind held saves off and kept this peer out of the open world
            // (it reads as "a dream is on its way") until the next dream; the entry's input locks
            // stayed too.
            try
            {
                if (Dreams.Instance != null && !Dreams.Instance.dreaming)
                {
                    Dreams.Instance.dreamPrepared = false;
                    Dreams.Instance.wantToDream = false;
                }
            }
            catch { /* ignore */ }
            ReleaseDreamInputLocks();
            UnfreezeWorld(restoreTime: false);
            if (DreamSession.IsActive)
                DreamSession.AbortStarting(reason);
            FinalDreamsceneManager.OnDreamEnded();
            ModRuntime.Log?.LogWarning("[DreamSync] AbortFailedRemoteDreamLoad: " + reason);
        }

        /// <summary>Marks a preset completed via DreamSession (sole authority).</summary>
        public static void MarkDreamCompleted(int playerId, string presetName)
        {
            DreamSession.MarkCompleted(presetName);
            ModRuntime.LegacyInfo($"[DreamSync] Marked dream as completed (session): {presetName}");
        }

        public static bool IsHostDreamEntity(Character c)
        {
            if (!_worldFrozen) return false;
            if (c == null) return false;
            if (Player.Instance != null && c.gameObject == Player.Instance.gameObject) return false;
            return !_frozenWorldCharacters.Contains(c);
        }

        public static bool IsHostDreamEntity(Component comp)
        {
            if (!_worldFrozen) return false;
            if (comp == null) return false;
            if (Player.Instance != null && comp.gameObject == Player.Instance.gameObject) return false;
            Character c = comp.GetComponentInParent<Character>();
            return c != null && !_frozenWorldCharacters.Contains(c);
        }

        /// <summary>
        /// Per-freeze answer per component instance. Host AI prefixes ask this every frame for
        /// every AI component; Object.name allocates and GetComponentInParent walks the
        /// hierarchy, while the answer cannot change until the freeze ends.
        /// </summary>
        private static readonly Dictionary<int, bool> _frozenByComponent = new Dictionary<int, bool>(512);

        public static bool IsWorldFrozenForComponent(Component comp)
        {
            if (!_worldFrozen) return false;
            if (comp == null) return false;
            int id = comp.GetInstanceID();
            if (_frozenByComponent.TryGetValue(id, out bool frozen))
                return frozen;
            if (_frozenByComponent.Count > 8192)
                _frozenByComponent.Clear();

            frozen = false;
            if (!(Player.Instance != null && comp.gameObject == Player.Instance.gameObject)
                && !comp.name.Contains("RemotePlayer"))
            {
                Character c = comp.GetComponentInParent<Character>();
                frozen = c != null && _frozenWorldCharacters.Contains(c);
            }
            _frozenByComponent[id] = frozen;
            return frozen;
        }

        /// <summary>
        /// Host, between the start of a dream's entry and its pad: a creature that appears now (a
        /// night spawn, a chunk waking) belongs to the frozen world too. Only creatures alive at
        /// the freeze were frozen, so these attacked players locked in the movie. Creatures of the
        /// dream's own pad stay free.
        /// </summary>
        internal static void NoteCharacterAppeared(Character c)
        {
            if (!_worldFrozen || !_hostEntryFreeze || c == null)
                return;
            if (Player.Instance != null && c.gameObject == Player.Instance.gameObject)
                return;
            // The nearest Location can be a sublocation of the pad (epilog_part1a_dream keeps
            // creatures under sub_epilog_blok_parter_01), so every enclosing one is checked.
            Location loc = c.GetComponentInParent<Location>(true);
            while (loc != null)
            {
                if (IsDreamPadLocation(loc.name))
                    return;
                Transform up = loc.transform.parent;
                loc = up != null ? up.GetComponentInParent<Location>(true) : null;
            }
            if (_frozenWorldCharacters.Add(c))
                _frozenByComponent.Clear();
        }

        private static bool IsDreamPadLocation(string locName)
        {
            if (string.IsNullOrEmpty(locName))
                return false;
            return locName.StartsWith("dream_", StringComparison.OrdinalIgnoreCase)
                || locName.StartsWith("epilog", StringComparison.OrdinalIgnoreCase)
                || IsDreamLocationName(locName);
        }

        public static void FreezeWorld()
        {
            if (_worldFrozen) return;
            _worldFrozen = true;

            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
                _savedGameTime = (int)ctrl.CurrentTime;

            // Record all currently-existing characters as "frozen" (dream characters
            // spawned later are exempt so dream AI continues to work).
            int nAll = CharacterTracker.CopyAll(out Character[] all);
            _frozenWorldCharacters.Clear();
            _frozenByComponent.Clear();
            for (int i = 0; i < nAll; i++)
            {
                Character c = all[i];
                if (c == null) continue;
                if (Player.Instance != null && c.gameObject == Player.Instance.gameObject) continue;
                _frozenWorldCharacters.Add(c);
            }

            ModRuntime.LegacyInfo($"[DreamSync] World frozen (time={_savedGameTime}, {_frozenWorldCharacters.Count} characters frozen)");
        }

        public static void UnfreezeWorld(bool restoreTime = true)
        {
            if (!_worldFrozen) return;
            _worldFrozen = false;

            if (restoreTime)
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                    ctrl.CurrentTime = _savedGameTime;
                ModRuntime.LegacyInfo($"[DreamSync] World unfrozen (time restored to {_savedGameTime})");
            }
            else
            {
                ModRuntime.LegacyInfo("[DreamSync] World unfrozen (time not restored)");
            }

            _frozenWorldCharacters.Clear();
            _frozenByComponent.Clear();
        }

    }
}
