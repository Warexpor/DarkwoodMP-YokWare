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
    internal static class DreamSyncManager
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

        /// <summary>C4: client story-end defer awaiting host accept / rejected nack.</summary>
        private static bool _storyEndDeferPending;
        private static float _storyEndDeferDeadline;
        private const float StoryEndDeferTimeoutSec = 15f;
        private static Coroutine _storyEndWatchdog;

        /// <summary>
        /// Host already broadcast DreamEnded at initiateEndDreaming — endDreaming must not
        /// send a second copy. Client host-ordered exit plays the same transition video.
        /// </summary>
        private static bool _dreamEndBroadcastSent;
        private static bool _hostOrderedDreamEnd;

        /// <summary>True when the local player's entry transition was intercepted by DreamEntryClientPatch.</summary>
        public static bool EntryTransitionPlayedLocally => _earlyEntryTransitionPlayed;

        public static bool IsStoryEndDeferPending => _storyEndDeferPending;

        /// <summary>Client may run vanilla initiateEndDreaming for a host-ordered story exit.</summary>
        public static bool IsHostOrderedDreamEnd => _hostOrderedDreamEnd;

        /// <summary>C4: after client sends DreamEnded, wait for host accept or rejected nack.</summary>
        public static void BeginStoryEndDefer()
        {
            _storyEndDeferPending = true;
            _storyEndDeferDeadline = Time.realtimeSinceStartup + StoryEndDeferTimeoutSec;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null)
            {
                if (_storyEndWatchdog != null)
                    ctrl.StopCoroutine(_storyEndWatchdog);
                _storyEndWatchdog = ctrl.StartCoroutine(StoryEndDeferWatchdog());
            }
        }

        public static void ClearStoryEndDefer()
        {
            _storyEndDeferPending = false;
            _storyEndDeferDeadline = 0f;
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl != null && _storyEndWatchdog != null)
            {
                ctrl.StopCoroutine(_storyEndWatchdog);
                _storyEndWatchdog = null;
            }
        }

        private static IEnumerator StoryEndDeferWatchdog()
        {
            while (_storyEndDeferPending && Time.realtimeSinceStartup < _storyEndDeferDeadline)
                yield return null;
            if (!_storyEndDeferPending)
                yield break;
            ModRuntime.Log?.LogWarning(
                "[DreamSync] Story-end defer timed out — forcing local dream cleanup");
            _storyEndDeferPending = false;
            _storyEndWatchdog = null;
            ForceLocalDreamCleanup("storyEndTimeout");
        }

        /// <summary>Forced cleanup after rejected nack or story-end timeout (C4).</summary>
        public static void ForceLocalDreamCleanup(string reason)
        {
            ClearStoryEndDefer();
            ModRuntime.LegacyInfo("[DreamSync] ForceLocalDreamCleanup: " + reason);
            if (DreamSession.IsActive)
                DreamSession.End(reason);
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
                ApplyRemoteDreamCleanup(reason);
            else
            {
                UnfreezeWorld(restoreTime: false);
                FinalDreamsceneManager.OnDreamEnded();
            }
            _localDreamActive = false;
        }

        /// <summary>
        /// Called from DreamEntryClientPatch when client intercepts onFinishedVideo.
        /// Records the transition as already-played so ProcessRemoteDreamCoroutine
        /// can skip the wait (video already ended) and proceed to fadeout immediately.
        /// </summary>
        public static void MarkLocalEntryTransitionPlayed()
        {
            _earlyEntryTransitionPlayed = true;
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Multiplayer-facing dream gate: session is authoritative when networked;
        /// falls back to local/remote flags for solo or mid-transition.
        /// </summary>
        public static bool IsDreamActive =>
            DreamSession.IsActive || _localDreamActive || _remoteDreamActive.Values.Any(v => v)
            || _earlyEntryTransitionPlayed;

        public static bool IsLocalDreamActive => _localDreamActive;

        /// <summary>Returns the dream Location's transform during an active dream, or null.</summary>
        public static Transform GetDreamLocationTransform()
        {
            if (!IsDreamActive) return null;
            if (Dreams.Instance != null && Dreams.Instance.dreamLocation != null)
                return Dreams.Instance.dreamLocation.transform;
            return null;
        }

        /// <summary>Delegates to DreamSession for pool mirror bookkeeping (not a re-entry ban).</summary>
        public static bool IsDreamCompleted(int playerId, string presetName)
        {
            // H4: never abort remote load for named re-entry — MirrorPoolRemove is separate.
            return false;
        }

        /// <summary>Delegates to DreamSession for pool mirror bookkeeping (not a re-entry ban).</summary>
        public static bool IsDreamCompleted(string presetName)
        {
            return false;
        }

        public static void OnLocalDreamStarted(string presetName, Vector3 locationPosition)
        {
            if (_localDreamActive) return;
            // H4: completions are pool-mirror only — do not skip named re-entry.
            _localDreamActive = true;
            _localDreamPreset = presetName;

            FreezeWorld();

            // Session already started by DreamStartPatch on host; ensure death tracking if needed
            if (!FinalDreamsceneManager.IsActive)
                FinalDreamsceneManager.OnDreamStarted();

            // Teleport the remote proxy (other player's character) to the dream
            // position so both players see each other immediately.
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected)
            {
                Vector3 proxyPos = Player.Instance != null
                    ? Player.Instance._transform.position
                    : locationPosition;
                net.TeleportRemoteProxyTo(proxyPos, 0f);

                // Freeze all remote proxies until they confirm dream entry.
                // This prevents the proxy from drifting back to the real-world position
                // while the remote player is still loading the dream scene.
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = true;

                // Safety timeout: unfreeze after 10s even if DreamEntered never arrives
                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.StartCoroutine(UnfreezeProxiesAfterDelay(10f));

                // Host alone initiates DreamStarted; clients enter via OnRemoteDreamStarted
                // and confirm with DreamEntered after scene load.
                if (net.Role == NetworkRole.Host)
                {
                    var started = DreamStartedMessage.Build(
                        presetName, locationPosition.x, locationPosition.y, locationPosition.z);
                    net.Broadcast(NetMessageType.DreamStarted,
                        w => started.Serialize(w),
                        LiteNetLib.DeliveryMethod.ReliableOrdered);
                    if (Singleton<Controller>.Instance != null)
                        Singleton<Controller>.Instance.StartCoroutine(HostDreamPropColliderDelayed());
                }
            }

            // Fix 1: If an early entry transition was played (peer's video overlay),
            // clean it up now that local entry is complete — prevents permanent
            // black screen + paralysis from EnteringDream never being reset.
            if (_earlyEntryTransitionPlayed)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                Singleton<Controller>.Instance.StartCoroutine(
                    LocalEntryFadeoutCoroutine(Mathf.Max(0f, remain)));
            }

            ModRuntime.LegacyInfo($"[DreamSync] Local dream started: {presetName}, pos={locationPosition}");
        }

        private static System.Collections.IEnumerator HostDreamPropColliderDelayed()
        {
            yield return new WaitForSecondsRealtime(1.5f);
            WorldPhysicsSyncService.HostBroadcastDreamPropColliders(force: true);
            yield return new WaitForSecondsRealtime(2f);
            WorldPhysicsSyncService.HostBroadcastDreamPropColliders(force: true);
        }

        /// <summary>
        /// Strip vanilla completed-location suffix so dream LocationEnter stays on the live pad.
        /// </summary>
        public static string CanonicalDreamLocationName(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return locationName;
            if (locationName.Length > 5
                && locationName.EndsWith("_done", StringComparison.OrdinalIgnoreCase))
                return locationName.Substring(0, locationName.Length - 5);
            return locationName;
        }

        public static bool IsDreamLocationName(string locationName)
        {
            if (string.IsNullOrEmpty(locationName) || !IsDreamActive) return false;
            string canon = CanonicalDreamLocationName(locationName);
            if (!string.IsNullOrEmpty(DreamSession.PresetName)
                && string.Equals(canon, DreamSession.PresetName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(_localDreamPreset)
                && string.Equals(canon, _localDreamPreset, StringComparison.OrdinalIgnoreCase))
                return true;
            return canon.StartsWith("dream_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Best-known active dream preset (local → session → Dreams.preset).</summary>
        public static string ResolveActivePresetName()
        {
            if (!string.IsNullOrEmpty(_localDreamPreset))
                return _localDreamPreset;
            if (!string.IsNullOrEmpty(DreamSession.PresetName))
                return DreamSession.PresetName;
            if (Dreams.Instance?.preset != null && !string.IsNullOrEmpty(Dreams.Instance.preset.name))
                return Core.getTrueLocationName(Dreams.Instance.preset.name);
            return "";
        }

        /// <summary>
        /// Overworld pose snapped before remote dream pad entry (any keyed pre-dream entry).
        /// Used by ClientStateBackup when live pose is still on the pad after dream flags clear.
        /// </summary>
        public static bool TryGetPreDreamOverworldPosition(out Vector3 pos)
        {
            foreach (var kvp in _preDreamPosition)
            {
                Vector3 p = kvp.Value;
                if (p.sqrMagnitude < 0.01f) continue;
                if (Mathf.Abs(p.x) >= 40000f || Mathf.Abs(p.z) >= 40000f) continue;
                pos = p;
                return true;
            }
            pos = Vector3.zero;
            return false;
        }

        public static void OnLocalDreamEnded()
        {
            if (!_localDreamActive && !_hostOrderedDreamEnd) return;

            string endedPreset = ResolveActivePresetName();
            MarkDreamCompleted(0, endedPreset);
            UnfreezeWorld();

            FinalDreamsceneManager.OnDreamEnded();
            (ModRuntime.Network as LanNetworkManager)?.ClearPendingDreamGameEvents();

            _localDreamActive = false;
            bool hostOrdered = _hostOrderedDreamEnd;
            _hostOrderedDreamEnd = false;

            string outcomeName = (Dreams.Instance != null) ? (Dreams.Instance.outcome ?? "") : "";

            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null && net.IsConnected)
            {
                // Host already fan-out at initiateEndDreaming; host-ordered clients never send.
                if (!_dreamEndBroadcastSent && !hostOrdered)
                {
                    var ended = DreamEndedMessage.Build(endedPreset ?? "", outcomeName);
                    if (net.Role == NetworkRole.Host)
                    {
                        net.Broadcast(NetMessageType.DreamEnded,
                            w => ended.Serialize(w),
                            LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                    else
                    {
                        net.Send(NetMessageType.DreamEnded,
                            w => ended.Serialize(w),
                            LiteNetLib.DeliveryMethod.ReliableOrdered);
                    }
                }
                _dreamEndBroadcastSent = false;

                // Unfreeze all proxies — dream has ended regardless of confirmation state
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
            else if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            ModRuntime.LegacyInfo($"[DreamSync] Local dream ended: {endedPreset}, outcome={outcomeName}");

            _localDreamPreset = null;
        }

        /// <summary>
        /// Host story exit: notify peers at initiateEndDreaming so they play the same
        /// outcome video in parallel (DreamEnded used to arrive only after the video).
        /// </summary>
        public static void NotifyPeersStoryEndBeginning(string presetName, string outcomeName)
        {
            var net = ModRuntime.Network as LanNetworkManager;
            if (net == null || !net.IsConnected || net.Role != NetworkRole.Host)
                return;
            if (_dreamEndBroadcastSent)
                return;
            if (string.IsNullOrEmpty(outcomeName) || outcomeName == "playerDeath")
                return;
            if (DreamSession.IsRejectedOutcome(outcomeName))
                return;

            _dreamEndBroadcastSent = true;
            if (DreamSession.IsActive)
                DreamSession.End(outcomeName);

            string resolved = !string.IsNullOrEmpty(presetName)
                ? presetName
                : ResolveActivePresetName();
            var ended = DreamEndedMessage.Build(resolved ?? "", outcomeName);
            net.Broadcast(NetMessageType.DreamEnded,
                w => ended.Serialize(w),
                LiteNetLib.DeliveryMethod.ReliableOrdered);
            ModRuntime.LegacyInfo(
                "[DreamSync] Host broadcast DreamEnded at initiateEndDreaming outcome="
                + outcomeName);
        }

        /// <summary>
        /// Client: host ordered a story exit — play vanilla outcome transition then endDreaming.
        /// </summary>
        public static bool TryBeginHostOrderedStoryEnd(string outcomeName)
        {
            if (string.IsNullOrEmpty(outcomeName) || outcomeName == "playerDeath")
                return false;
            if (DreamSession.IsRejectedOutcome(outcomeName))
                return false;
            var dreams = Dreams.Instance;
            if (dreams == null || !dreams.dreaming)
                return false;

            ClearStoryEndDefer();
            _hostOrderedDreamEnd = true;
            dreams.outcome = outcomeName;
            ModRuntime.LegacyInfo(
                "[DreamSync] Host-ordered story end — playing exit transition outcome="
                + outcomeName);
            try
            {
                dreams.initiateEndDreaming();
                return true;
            }
            catch (Exception ex)
            {
                _hostOrderedDreamEnd = false;
                ModRuntime.Log?.LogWarning(
                    "[DreamSync] Host-ordered initiateEndDreaming failed: " + ex.Message);
                return false;
            }
        }

        public static void OnRemoteDreamStarted(int playerId, string presetName, Vector3 locationPosition)
        {
            if (_remoteDreamActive.TryGetValue(playerId, out bool active) && active) return;
            if (DreamSession.IsPresetCompleted(presetName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Skipping completed dream on remote (p{playerId}): {presetName}");
                return;
            }
            _remoteDreamActive[playerId] = true;
            _currentDreamPreset[playerId] = presetName;

            FreezeWorld();

            if (!FinalDreamsceneManager.IsActive)
                FinalDreamsceneManager.OnDreamStarted();

            ModRuntime.LegacyInfo($"[DreamSync] Remote dream started (p{playerId}): {presetName}, pos={locationPosition}");

            SavePreDreamState(playerId);
            ProcessRemoteDream(playerId, locationPosition);
        }

        private static void ProcessRemoteDream(int playerId, Vector3 locationPosition)
        {
            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;
            if (presetName == null) return;
            ApplyDreamCameraEffects(presetName);
            Singleton<Controller>.Instance.StartCoroutine(ProcessRemoteDreamCoroutine(playerId, locationPosition));
        }

        /// <summary>
        /// Peer started Dreams.startTransition — play the same video now (not after DreamStarted).
        /// </summary>
        public static void OnPeerDreamEntryTransition()
        {
            if (_localDreamActive) return;
            if (_earlyEntryTransitionPlayed) return;
            // DreamStarted path already started the video — do not stack a second Play.
            if (_remoteEntryTransitionPlaying) return;

            _earlyEntryTransitionPlayed = true;
            FreezeWorld();

            float wait = StartRemoteDreamTransition();
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, wait);
            // So DreamTransition.skip / ActionSkipTransition can cut the wait.
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = true;

            // Arm safety watchdog: if nothing resolves the transition within 20s of
            // the expected completion, force-clear the stuck overlay + EnteringDream.
            Singleton<Controller>.Instance.StartCoroutine(
                EntryTransitionWatchdog(_earlyEntryTransitionDoneAt + 20f));

            ModRuntime.LegacyInfo($"[DreamSync] Early entry transition (peer), wait={wait:F1}s");
        }

        /// <summary>Skip / cancel early entry wait so DreamStarted load is not blocked.</summary>
        public static void OnEntryTransitionSkipped()
        {
            if (!_earlyEntryTransitionPlayed) return;
            _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup;
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = false;
            FadeOutDreamTransition();
        }

        private static IEnumerator ProcessRemoteDreamCoroutine(int playerId, Vector3 locationPosition)
        {
            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

            // Snapshot parity with host prepareDream (D4).
            if (Dreams.Instance != null && !Dreams.Instance.dreaming && !Dreams.Instance.switchingDream)
            {
                try { Dreams.Instance.saveCurrentPlayerState(); }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] saveCurrentPlayerState: " + ex.Message);
                }
            }

            // Host prepareDream shows Saving; SaveSync is suppressed for the whole dream
            // window (avoids hitch mid-video). Peer path never ran prepareDream — mirror a
            // local Save+indicator so clients see the same cue. Fan-out stays suppressed.
            try
            {
                Singleton<SaveManager>.Instance?.Save(
                    doJson: true, doSaveProfile: true, force: true,
                    forceSaveStatic: false, showSavingIndicator: true);
                ModRuntime.LegacyInfo("[DreamSync] peer dream-entry local Save (Saving UI)");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] peer dream-entry Save: " + ex.Message);
            }

            // 1. Entry video: already started on CutsceneSync DreamEntry, or play now (late/missed).
            if (_earlyEntryTransitionPlayed || _remoteEntryTransitionPlaying)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                if (remain > 0.05f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting remaining entry transition {remain:F1}s");
                    // Realtime: EnteringDream / pause can zero timescale and stall WaitForSeconds,
                    // which prolonged the black screen and made the stinger feel doubled.
                    yield return new WaitForSecondsRealtime(remain);
                }
            }
            else
            {
                float waitTime = StartRemoteDreamTransition();
                if (waitTime > 0f)
                {
                    ModRuntime.LegacyInfo($"[DreamSync] Waiting {waitTime:F1}s for remote dream transition");
                    yield return new WaitForSecondsRealtime(waitTime);
                }
            }

            // 2. Clean up the video overlay (fade out)
            FadeOutDreamTransition();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;

            // 3. NOW load the dream scene (after transition is complete)
            if (presetName != null)
                yield return LoadDreamSceneCoroutine(presetName, locationPosition, false, playerId);
            else
            {
                ModRuntime.Log?.LogError("[DreamSync] Remote dream entry missing preset — unfreezing");
                UnfreezeWorld();
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
            }
        }

        /// <summary>Host broadcast chain: load next pocket without full session Idle.</summary>
        public static void OnDreamChain(string nextPreset)
        {
            if (string.IsNullOrEmpty(nextPreset)) return;
            _localDreamPreset = nextPreset;
            _localDreamActive = true;
            if (Player.Instance != null)
            {
                int pid = 0;
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null)
                    pid = net.LocalPlayerId;
                _currentDreamPreset[pid] = nextPreset;
                _remoteDreamActive[pid] = true;
            }
            Vector3 pos = Dreams.Instance?.dreamLocation != null
                ? Dreams.Instance.dreamLocation.transform.position
                : (Player.Instance != null ? Player.Instance._transform.position : Vector3.zero);
            if (Singleton<Controller>.Instance != null)
                Singleton<Controller>.Instance.StartCoroutine(ProcessChainCoroutine(nextPreset, pos));
        }

        private static IEnumerator ProcessChainCoroutine(string presetName, Vector3 locationPosition)
        {
            // Keep world frozen; tear previous dream location if still present.
            if (Dreams.Instance != null && Dreams.Instance.dreaming)
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    Dreams.Instance.dreaming = false;
                    Dreams.Instance.destroyDream();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] chain destroy: " + ex.Message);
                }
                finally { LanNetworkManager.IsApplyingRemoteState = false; }
            }

            // C3: preserve inventory/time copies across pocket (vanilla switchingDream).
            if (Dreams.Instance != null)
                Dreams.Instance.switchingDream = true;

            yield return LoadDreamSceneCoroutine(presetName, locationPosition, false, 0);
            DreamSession.MarkActive();
        }

        /// <summary>
        /// Host-only: waits out the remaining early transition time, then fades the
        /// video overlay and clears EnteringDream. Mirrors the peer-path cleanup
        /// that ProcessRemoteDreamCoroutine performs for clients.
        /// </summary>
        private static IEnumerator LocalEntryFadeoutCoroutine(float delay)
        {
            if (delay > 0.05f)
                yield return new WaitForSecondsRealtime(delay);
            FadeOutDreamTransition();
            // Peer path sets base UI.blackScreen opaque; vanilla startDreaming only clears
            // blackScreenTop — without this the host stays black forever.
            FadeInDreamBlackScreen();
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
        }

        /// <summary>
        /// Safety watchdog: if the early entry transition is still active after the
        /// timeout and neither a local nor remote dream session started, force-clear
        /// the stuck state so the player is not permanently blinded + paralysed.
        /// </summary>
        private static IEnumerator EntryTransitionWatchdog(float expireAt)
        {
            float delay = expireAt - Time.realtimeSinceStartup;
            if (delay > 0f)
                yield return new WaitForSeconds(delay);
            yield return null; // one frame for any pending transitions to settle
            if (_earlyEntryTransitionPlayed && !DreamSession.IsActive && !_localDreamActive)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] Watchdog: early entry transition stuck — force-clearing");
                FadeOutDreamTransition();
                _earlyEntryTransitionPlayed = false;
                _earlyEntryTransitionDoneAt = 0f;
                try
                {
                    if (Dreams.Instance != null && !Dreams.Instance.dreaming)
                        Dreams.Instance.dreamPrepared = false;
                }
                catch { /* ignore */ }
                Core.EnteringDream = false;
                UnfreezeWorld();
            }
        }

        /// <summary>
        /// Client sent DreamStartRequest and is holding opaque black. If host never
        /// delivers DreamStarted, clear the void so the player is not stuck blind.
        /// </summary>
        public static void ArmClientEntryWatchdog()
        {
            var ctrl = Singleton<Controller>.Instance;
            if (ctrl == null) return;
            ctrl.StartCoroutine(ClientEntryWatchdog());
        }

        private static IEnumerator ClientEntryWatchdog()
        {
            float deadline = Time.realtimeSinceStartup + 25f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (DreamSession.IsActive || _localDreamActive || IsDreamActive)
                    yield break;
                if (ModRuntime.Network == null || !ModRuntime.Network.IsConnected)
                    break;
                yield return null;
            }
            if (DreamSession.IsActive || _localDreamActive || IsDreamActive)
                yield break;

            ModRuntime.Log?.LogWarning(
                "[DreamSync] Client entry watchdog — no DreamStarted, clearing black void");
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
            UnfreezeWorld(restoreTime: false);
        }

        private static void FadeOutDreamTransition()
        {
            // Keep EnteringDream until LoadDreamSceneCoroutine fades in — clearing it here
            // left a frame of overworld between video teardown and scene load.
            _remoteEntryTransitionPlaying = false;
            // Stop the entry stinger so it cannot overlap dream-scene music after a long wait.
            if (!string.IsNullOrEmpty(_remoteEntryAudioId))
            {
                try { AudioController.Stop(_remoteEntryAudioId); } catch { /* ignore */ }
                _remoteEntryAudioId = null;
            }
            if (Dreams.Instance?.startTransition != null)
                Dreams.Instance.startTransition.isPlaying = false;
            if (Singleton<UI>.Instance == null) return;
            try
            {
                var ui = Singleton<UI>.Instance;
                // Snap black opaque first so any video fade cannot expose overworld.
                try
                {
                    if (ui.blackScreen != null)
                    {
                        var baseSprite = ui.blackScreen.GetComponent<tk2dBaseSprite>();
                        if (baseSprite != null)
                            baseSprite.color = new Color(0f, 0f, 0f, 1f);
                    }
                    if (ui.blackScreenTop != null)
                    {
                        var topSprite = ui.blackScreenTop.GetComponent<tk2dBaseSprite>();
                        if (topSprite != null)
                            topSprite.color = new Color(0f, 0f, 0f, 1f);
                    }
                }
                catch { /* ignore */ }
                ui.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0f);

                var overlay = ui.videoOverlay;
                if (overlay != null && overlay.gameObject.activeSelf)
                {
                    var renderer = overlay.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        VideoPlayer vp = renderer.GetComponent<VideoPlayer>();
                        if (vp != null && vp.isPlaying)
                            vp.Stop();
                        // Snap off — DOFade(0.5s) exposed overworld when black was not yet solid.
                        if (renderer.material != null)
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                        renderer.enabled = false;
                    }
                    overlay.gameObject.SetActive(false);
                    Core.showGameCursor();
                }
                else
                {
                    Core.showGameCursor();
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Error fading out transition: {ex}");
            }
        }

        public static void OnRemoteDreamEnded(int playerId, string outcomeName = "")
        {
            if (!_remoteDreamActive.TryGetValue(playerId, out bool active) || !active)
            {
                // Host-ordered story end may arrive while we only track via DreamSession /
                // local dreaming (remote flag already true from DreamStarted).
                if (Dreams.Instance != null && Dreams.Instance.dreaming
                    && TryBeginHostOrderedStoryEnd(outcomeName))
                    return;
                return;
            }

            string presetName = _currentDreamPreset.TryGetValue(playerId, out var p) ? p : null;

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
                // ApplyRemoteDreamCleanup unfreezes after restore (D8).
                ApplyRemoteDreamCleanup(outcomeName);
            }
            else
            {
                if (presetName != null)
                {
                    CleanupDreamScene(presetName);
                    RemoveDreamCameraEffects(presetName);
                }
                RestorePreDreamState(playerId);
                UnfreezeWorld();
                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.IsConnected && Player.Instance != null)
                    net.TeleportRemoteProxyTo(Player.Instance._transform.position, 0f);
            }

            FinalDreamsceneManager.OnDreamEnded();
            (ModRuntime.Network as LanNetworkManager)?.ClearPendingDreamGameEvents();

            var unfreezeNet = ModRuntime.Network as LanNetworkManager;
            if (unfreezeNet != null)
            {
                foreach (var proxy in unfreezeNet.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            _currentDreamPreset.Remove(playerId);
            _preDreamPosition.Remove(playerId);
            _preDreamGridName.Remove(playerId);
            _remoteDreamActive.Remove(playerId);
        }

        public static void OnDisconnected()
        {
            FinalDreamsceneManager.OnDisconnected();

            // Unfreeze any frozen proxies
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }

            // Clean up any active remote dreams
            foreach (var kvp in _currentDreamPreset)
            {
                CleanupDreamScene(kvp.Value);
                RemoveDreamCameraEffects(kvp.Value);
            }
            foreach (var kvp in _preDreamPosition)
            {
                RestorePreDreamState(kvp.Key);
            }

            _localDreamActive = false;
            _localDreamPreset = null;
            _earlyEntryTransitionPlayed = false;
            _earlyEntryTransitionDoneAt = 0f;
            _remoteEntryTransitionPlaying = false;
            _remoteEntryAudioId = null;
            _dreamEndBroadcastSent = false;
            _hostOrderedDreamEnd = false;
            _remoteDreamActive.Clear();
            _currentDreamPreset.Clear();
            _preDreamPosition.Clear();
            _preDreamGridName.Clear();
            FreezeTracker.Reset();
            DreamSession.Reset();
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

        public static bool IsWorldFrozenForComponent(Component comp)
        {
            if (!_worldFrozen) return false;
            if (comp == null) return false;
            if (Player.Instance != null && comp.gameObject == Player.Instance.gameObject) return false;
            if (comp.name.Contains("RemotePlayer")) return false;
            Character c = comp.GetComponentInParent<Character>();
            return c != null && _frozenWorldCharacters.Contains(c);
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
            Character[] all = CharacterTracker.GetAll();
            _frozenWorldCharacters.Clear();
            foreach (var c in all)
            {
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
        }

        private static void ApplyRemoteDreamCleanup(string outcomeName = "")
        {
            var dreams = Dreams.Instance;
            var player = Player.Instance;
            if (player == null || dreams == null) return;

            // D8 order: restore player → destroy dream → unfreeze → world/journal effects.
            string pendingOutcome = outcomeName ?? "";

            LanNetworkManager.IsApplyingRemoteState = true;
            try
            {
                if (dreams.preset != null)
                    Core.modifyCamEffects(active: false, dreams.preset.gameObject);

                AudioController.StopMusic(1f);
                Core.spawnCharactersAtNight = true;

                player.Hotbar.clear();
                player.Inventory.clear();
                if (dreams.inventorySlotsCopy.Count > 0)
                {
                    Inventory.moveSlots(dreams.inventorySlotsCopy, player.Inventory.slots);
                    Inventory.moveSlots(dreams.hotbarSlotsCopy, player.Hotbar.slots);
                    player.Hotbar.hide();
                    player.Hotbar.show();
                }

                // Personal rewards first (items/journal) — defer fireGameEvent/world until unfreeze.
                if (!string.IsNullOrEmpty(pendingOutcome))
                    ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: false);

                // Vanilla endDreaming parity: journal dream entries, rain, unique teleport, time.
                try { Singleton<UI>.Instance?.journal?.clearDreamEntries(); }
                catch (Exception) { /* journal may be null mid-teardown */ }

                Vector3 restorePos = dreams.positionCopy;
                if (dreams.preset != null)
                {
                    string trueName = Core.getTrueLocationName(dreams.preset.name);
                    if (trueName == "dream_tutorial_01")
                    {
                        var wg = Singleton<WorldGenerator>.Instance;
                        if (wg?.playerBase != null)
                        {
                            var loc = wg.playerBase.GetComponent<Location>();
                            if (loc?.playerSpawn != null)
                                restorePos = loc.playerSpawn.transform.position;
                        }
                        player.firstPlay = false;
                        dreams.timeCopy = 5f;
                    }
                    if (!string.IsNullOrEmpty(dreams.preset.uniqueObjectToTransportToAfterDreamEnd)
                        && Singleton<UniqueObjects>.Instance != null)
                    {
                        GameObject uo = Singleton<UniqueObjects>.Instance.getObject(
                            dreams.preset.uniqueObjectToTransportToAfterDreamEnd);
                        if (uo != null)
                            restorePos = uo.transform.position;
                    }
                }

                if (dreams.placeStartedDreaming != null)
                {
                    if (!dreams.placeStartedDreaming.isOutsideLocation)
                    {
                        Singleton<OutsideLocations>.Instance.playerInOutsideLocation = false;
                        Singleton<OutsideLocations>.Instance.currentLocationName = "";
                        Singleton<Rain>.Instance?.unhide();
                    }
                    else
                    {
                        Singleton<OutsideLocations>.Instance.currentLocationName =
                            Core.getTrueLocationName(dreams.placeStartedDreaming.name);
                        if (!dreams.placeStartedDreaming.isUnderground)
                            Singleton<Rain>.Instance?.unhide();
                    }
                }
                else
                {
                    Singleton<OutsideLocations>.Instance.playerInOutsideLocation = false;
                    Singleton<OutsideLocations>.Instance.currentLocationName = "";
                    Singleton<Rain>.Instance?.unhide();
                }

                player.teleportTo(restorePos, Quaternion.Euler(90f, 0f, 0f));
                player.Hotbar.selectSlot(0, noiseless: true, force: true);

                // Prefer freeze snapshot over timeCopy when remote startDreaming overwrote it.
                int restoreTime = _worldFrozen && _savedGameTime > 0
                    ? _savedGameTime
                    : (int)dreams.timeCopy;
                dreams.timeCopy = restoreTime;
                Singleton<Controller>.Instance.CurrentTime = restoreTime;
                UnfreezeWorld(restoreTime: false);

                bool endDiving = dreams.preset != null && dreams.preset.endDivingOut;
                dreams.destroyDream();
                dreams.dreaming = false;
                dreams.dreamPrepared = false;
                dreams.wantToDream = false;
                _localDreamActive = false;

                if (dreams.placeStartedDreaming != null)
                {
                    string locName = Core.getTrueLocationName(dreams.placeStartedDreaming.name);
                    if (dreams.placeStartedDreaming.isOutsideLocation &&
                        Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(locName))
                    {
                        Singleton<OutsideLocations>.Instance.spawnedLocations[locName].enter();
                    }
                    Singleton<WorldGrid>.Instance.setGrid(
                        dreams.placeStartedDreaming.isOutsideLocation ? locName : "World");
                    Singleton<WorldGrid>.Instance.refreshPosition(player._transform.position, true, true);
                }
                else
                {
                    Singleton<WorldGrid>.Instance.setGrid("World");
                }

                player.endDreaming(true);

                if (endDiving && Singleton<Controller>.Instance != null)
                {
                    Singleton<Controller>.Instance.Invoke(delegate
                    {
                        if (Player.Instance != null)
                            Player.Instance.diveOut();
                    }, 1f, timeScaleDependent: true);
                }

                // Restore pre-dream effects (vanilla endDreaming).
                try
                {
                    if (dreams.effectsCopy != null)
                    {
                        dreams.effectsCopy.loadValues(player.effects);
                        dreams.effectsCopy.effects.Clear();
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] effectsCopy restore: " + ex.Message);
                }

                var net = ModRuntime.Network as LanNetworkManager;
                if (net != null && net.IsConnected)
                    net.TeleportRemoteProxyTo(player._transform.position, 0f);

                dreams.placeStartedDreaming = null;
                DreamSession.ClearPendingHostPreset();

                try
                {
                    Singleton<RandomWorldSounds>.Instance?.resumeGlobalSounds();
                    if (Singleton<Controller>.Instance != null && Singleton<Controller>.Instance.isAfterNight)
                        Singleton<Controller>.Instance.addAfterNightEffect();
                    Singleton<Controller>.Instance?.refreshTimeNoLogic();
                    Singleton<Controller>.Instance?.updateAmbientLight();
                    player.whereAmI?.checkWhereAmI();
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning("[DreamSync] post-cleanup world hooks: " + ex.Message);
                }

                ModRuntime.LegacyInfo($"[DreamSync] Remote dream cleanup applied");
            }
            finally
            {
                LanNetworkManager.IsApplyingRemoteState = false;
            }

            UnfreezeWorld();

            var spec = SpectatorModeController.Instance;
            if (spec != null && spec.IsSpectating)
                spec.ExitWithoutPositionRestore();

            // World events after forest is live again.
            if (!string.IsNullOrEmpty(pendingOutcome) && dreams.preset != null)
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try { ApplyOutcomeEffects(dreams, player, pendingOutcome, worldEvents: true); }
                finally { LanNetworkManager.IsApplyingRemoteState = false; }
            }
        }

        /// <param name="worldEvents">
        /// false = personal rewards only; true = fireGameEvent / fireWorldEvent only (D8).
        /// </param>
        private static void ApplyOutcomeEffects(Dreams dreams, Player player, string outcomeName, bool worldEvents = true)
        {
            if (dreams.preset == null || dreams.preset.outcomes == null) return;

            // Hard cleanup can still carry a story outcome name; dead peers restore inventory
            // only (createInvItem / journal grants skipped).
            if (!worldEvents && FinalDreamsceneManager.IsLocalDead)
            {
                ModRuntime.LegacyInfo(
                    "[DreamDeath] ApplyOutcomeEffects — local dead, personal rewards skipped");
                return;
            }

            DreamPreset.Outcome outcomePreset = null;
            foreach (var oc in dreams.preset.outcomes)
            {
                if (oc != null && oc.name == outcomeName)
                {
                    outcomePreset = oc;
                    break;
                }
            }
            if (outcomePreset == null)
            {
                foreach (var oc in dreams.preset.outcomes)
                {
                    if (oc != null && oc.name == "default")
                    {
                        outcomePreset = oc;
                        break;
                    }
                }
            }
            if (outcomePreset == null && dreams.preset.outcomes.Count > 0)
                outcomePreset = dreams.preset.outcomes[0];

            if (outcomePreset == null) return;

            foreach (var effect in outcomePreset.effects)
            {
                switch (effect.type)
                {
                    case global::DreamPreset.Outcome.Effect.Type.createInvItem:
                        if (worldEvents) break;
                        if (effect.invItem != null)
                        {
                            var go = effect.invItem as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var invItem = go.GetComponent<InvItem>();
                                if (invItem != null)
                                    player.Inventory.addItemTypeToPlayer(invItem.type, effect.amount, dropIfNoRoom: true);
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.addJournalItem:
                        if (worldEvents) break;
                        if (effect.invItem != null)
                        {
                            var go = effect.invItem as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var invItem = go.GetComponent<InvItem>();
                                if (invItem != null)
                                    player.Inventory.addJournalItem(invItem.type, showImmediately: false, noPopup: true);
                                var journalEntry = go.GetComponent<JournalEntry>();
                                if (journalEntry != null)
                                    Singleton<UI>.Instance.journal.addJournalEntry(journalEntry.name, noPopup: true);
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.fireGameEvent:
                        if (!worldEvents) break;
                        if (effect.destPrefab != null)
                        {
                            var go = effect.destPrefab as UnityEngine.GameObject;
                            if (go != null)
                            {
                                var gameEvents = go.GetComponent<GameEvents>();
                                if (gameEvents != null)
                                {
                                    gameEvents.fired = false;
                                    gameEvents.fire();
                                }
                            }
                        }
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.fireWorldEvent:
                        if (!worldEvents) break;
                        if (!string.IsNullOrEmpty(effect.worldEventType))
                            Singleton<Events>.Instance.fireWorldEvent(effect.worldEventType);
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.transferToDream:
                        // Host owns chain via DreamChainStart / prepareDream(switchingDream).
                        break;

                    case global::DreamPreset.Outcome.Effect.Type.addCharacterEffect:
                        // Vanilla endDreaming loop does not apply this type either; effectsCopy restores pre-dream.
                        break;
                    default:
                        if (!worldEvents)
                            ModRuntime.Log?.LogWarning($"[DreamSync] Unhandled outcome effect type: {effect.type}");
                        break;
                }
            }

            if (!worldEvents && outcomePreset.customEndTime)
                Singleton<Controller>.Instance.CurrentTime = outcomePreset.endTime;
        }

        /// <summary>True when a physics object should sync during an active dream (D12).</summary>
        public static bool ShouldSyncPhysicsObject(Transform t)
        {
            if (t == null) return true;
            if (!IsLocalDreamActive && !DreamSession.IsActive)
                return true; // overworld: all free bodies
            Transform dreamLoc = GetDreamLocationTransform();
            if (dreamLoc == null)
                return true;
            // Only free bodies under the dream pocket (or near player in dream grid).
            if (t.IsChildOf(dreamLoc) || t == dreamLoc)
                return true;
            if (Player.Instance != null
                && Dreams.Instance?.preset != null
                && Singleton<WorldGrid>.Instance?.currentGrid != null
                && string.Equals(Singleton<WorldGrid>.Instance.currentGrid.name, Dreams.Instance.preset.name,
                    StringComparison.OrdinalIgnoreCase)
                && Vector3.Distance(t.position, Player.Instance._transform.position) < 80f)
                return true;
            return false;
        }

        private static void SavePreDreamState(int playerId)
        {
            var player = Player.Instance;
            _preDreamPosition[playerId] = player != null ? player._transform.position : Vector3.zero;
            _preDreamGridName[playerId] = Singleton<WorldGrid>.Instance != null && Singleton<WorldGrid>.Instance.currentGrid != null
                ? Singleton<WorldGrid>.Instance.currentGrid.name
                : "World";
        }

        private static void RestorePreDreamState(int playerId)
        {
            if (!_preDreamPosition.TryGetValue(playerId, out var position)) return;
            if (!_preDreamGridName.TryGetValue(playerId, out var gridName)) gridName = "World";

            var player = Player.Instance;
            if (player != null)
            {
                player.invulnerable = false;
                if (player.immobilised)
                    player.stopImmobilise();
                player.switchVisibilty(true);
                player.teleportTo(position, Quaternion.Euler(90f, 0f, 0f));
            }

            if (Singleton<WorldGrid>.Instance != null)
            {
                if (Singleton<WorldGrid>.Instance.currentGrid != null)
                    Singleton<WorldGrid>.Instance.currentGrid.leave();
                Singleton<WorldGrid>.Instance.setGrid(gridName ?? "World");
                Vector3 restorePos = player != null ? player._transform.position : position;
                Singleton<WorldGrid>.Instance.refreshPosition(restorePos, instant: true, force: true);
            }

            if (Singleton<UI>.Instance != null)
                Singleton<UI>.Instance.showVisibleUI();
        }

        private static IEnumerator LoadDreamSceneCoroutine(string locationName, Vector3 position, bool _, int playerId = 0)
        {
            yield return null;

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream load — already completed: {locationName}");
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
                UnfreezeWorld();
                FinalDreamsceneManager.OnDreamEnded();
                _currentDreamPreset.Remove(playerId);
                yield break;
            }

            Location component = null;
            yield return StartLoadDreamScene(locationName, position, result => component = result);

            if (component == null)
            {
                yield break;
            }

            if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Aborting remote dream entry — completed during load: {locationName}");
                if (_remoteDreamActive.ContainsKey(playerId))
                    _remoteDreamActive[playerId] = false;
                UnfreezeWorld();
                FinalDreamsceneManager.OnDreamEnded();
                _currentDreamPreset.Remove(playerId);
                yield break;
            }

            var player = Player.Instance;
            if (player == null) yield break;

            // Preserve overworld restore pose BEFORE any pad teleport. Vanilla
            // startDreaming() calls saveCurrentPlayerState() first — if we already
            // teleported to the pad, that overwrites positionCopy with abyss coords
            // and endDreaming leaves the client stuck at −75k.
            Vector3 overworldPosCopy = Vector3.zero;
            bool haveOverworldPosCopy = false;
            if (Dreams.Instance != null)
            {
                Vector3 copy = Dreams.Instance.positionCopy;
                if (copy.sqrMagnitude > 0.01f
                    && (Mathf.Abs(copy.x) < 40000f && Mathf.Abs(copy.z) < 40000f))
                {
                    overworldPosCopy = copy;
                    haveOverworldPosCopy = true;
                }
            }
            if (!haveOverworldPosCopy
                && _preDreamPosition.TryGetValue(playerId, out var prePos)
                && prePos.sqrMagnitude > 0.01f
                && Mathf.Abs(prePos.x) < 40000f && Mathf.Abs(prePos.z) < 40000f)
            {
                overworldPosCopy = prePos;
                haveOverworldPosCopy = true;
            }

            Vector3 spawnPos = component.playerSpawn != null
                ? component.playerSpawn.transform.position
                : position;

            player.teleportTo(spawnPos, Quaternion.Euler(90f, 0f, 0f));
            ApplyDreamCameraEffects(locationName);

            if (Singleton<WorldGrid>.Instance != null)
            {
                if (Singleton<WorldGrid>.Instance.currentGrid != null)
                    Singleton<WorldGrid>.Instance.currentGrid.leave();
                Singleton<WorldGrid>.Instance.setGrid(locationName);
                // Dream pads are small pockets — enter every node so props behind the
                // dialogue door are not left Cullable-hidden if any registered late.
                try { Singleton<WorldGrid>.Instance.enterAllNodes(); }
                catch { /* ignore */ }
                Singleton<WorldGrid>.Instance.refreshPosition(player._transform.position, instant: true, force: true);
            }
            FinishDreamOutsideLoadFlags();

            // Call startDreaming() on the remote player so they receive dream items,
            // proper animation library, dream health, etc. (same as vanilla initiator).
            if (Dreams.Instance != null && !Dreams.Instance.dreaming && !IsDreamCompleted(playerId, locationName))
            {
                LanNetworkManager.IsApplyingRemoteState = true;
                try
                {
                    if (Dreams.Instance.preset == null)
                    {
                        GameObject presetGO = Resources.Load("DreamPresets/" + locationName) as GameObject;
                        if (presetGO != null)
                            Dreams.Instance.preset = presetGO.GetComponent<DreamPreset>();
                    }
                    // Resources path skips getPreset random remove — keep one-shot pool aligned.
                    DreamSession.MirrorPoolRemove(locationName);
                    Dreams.Instance.dreamLocation = component;
                    ApplyEpilogueModeIfNeeded(component, locationName);
                    Dreams.Instance.startDreaming();
                    // Vanilla startDreaming() re-saves timeCopy from CurrentTime — but by now
                    // TimeSync/dream may have already bumped the clock to dream time. Restore
                    // the freeze-time snapshot so exit doesn't leave the client stuck at 900.
                    if (_worldFrozen && _savedGameTime > 0)
                        Dreams.Instance.timeCopy = _savedGameTime;
                    // Mirror timeCopy fix: restore overworld positionCopy after startDreaming
                    // overwrote it with pad feet (teleport-before-startDreaming remote path).
                    if (haveOverworldPosCopy)
                        Dreams.Instance.positionCopy = overworldPosCopy;
                    _localDreamActive = true;
                    _localDreamPreset = locationName;
                }
                finally
                {
                    LanNetworkManager.IsApplyingRemoteState = false;
                }
            }
            else if (IsDreamCompleted(playerId, locationName))
            {
                ModRuntime.LegacyInfo($"[DreamSync] Blocked remote startDreaming — already completed: {locationName}");
            }

            // Vanilla: onLocationSpawned → startDreaming → enter → OnActivated → checkEnterEvents.
            // We entered earlier for render; wait for activateOverTime, then apply queued
            // onEnterLocation_* so door_underground gets welcome_opening_dream.
            float waitLoad = 0f;
            while (component != null && !component.finishedLoading && waitLoad < 15f)
            {
                waitLoad += Time.unscaledDeltaTime;
                yield return null;
            }
            try
            {
                var netFlush = ModRuntime.Network as LanNetworkManager;
                netFlush?.TryFlushPendingGameEventsAfterDreamLoad();
            }
            catch { /* non-fatal */ }

            // Snap host/peer proxies from PlayerPositionManager (true network pos), not
            // local player feet — old code stacked everyone on the client spawn and then
            // LocationEnter overwrote with a bad playerSpawn Y.
            var network = ModRuntime.Network as LanNetworkManager;
            if (network != null && network.IsConnected)
            {
                network.ResyncDreamProxiesAfterLocalLoad(locationName);

                // Send confirmation back to the dream initiator so they unfreeze our proxy.
                // From this point forward, position updates will come from the dream scene.
                network.Send(NetMessageType.DreamEntered,
                    w => new DreamEnteredMessage().Serialize(w),
                    LiteNetLib.DeliveryMethod.ReliableOrdered);
            }

            // Host startDreaming fades black via vanilla; remote path left blackScreen solid.
            FadeInDreamBlackScreen();

            // Dream ambient/time: force after startDreaming + any late TimeSync (bunker washout).
            try
            {
                if (Dreams.Instance != null && Dreams.Instance.dreaming && Dreams.Instance.preset != null
                    && Singleton<Controller>.Instance != null)
                {
                    Singleton<Controller>.Instance.CurrentTime = (int)Dreams.Instance.preset.time;
                    Singleton<Controller>.Instance.updateAmbientLight();
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] post-load ambient: " + ex.Message);
            }

            // Flush world lights that arrived while the pad was unloading.
            try { WorldPhysicsSyncService.TryFlushPendingLights(); }
            catch { /* non-fatal */ }

            ModRuntime.LegacyInfo($"[DreamSync] Player positioned at dream location: {locationName}");
        }

        /// <summary>
        /// Match vanilla <c>Dreams.startDreaming</c>: wait 1 frame, fade only
        /// <c>blackScreenTop</c> over 0.5s when still opaque. Also clear base blackScreen
        /// if remote path left it solid (host rarely uses it for dream entry).
        /// </summary>
        private static void FadeInDreamBlackScreen()
        {
            try
            {
                var ctrl = Singleton<Controller>.Instance;
                if (ctrl != null)
                {
                    ctrl.waitFramesAndRun(DoFadeInDreamBlackScreen, 1);
                    return;
                }
                DoFadeInDreamBlackScreen();
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] FadeInDreamBlackScreen: " + ex.Message);
            }
        }

        private static void DoFadeInDreamBlackScreen()
        {
            try
            {
                Core.EnteringDream = false;
                var ui = Singleton<UI>.Instance;
                if (ui == null) return;

                // Vanilla: only blackScreenTop, 0.5s, if alpha != 0.
                if (ui.blackScreenTop != null)
                {
                    var topSprite = ui.blackScreenTop.GetComponent<tk2dBaseSprite>();
                    if (topSprite == null || topSprite.color.a != 0f)
                        ui.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                }

                // Remote entry may also leave base blackScreen full — clear at same pace.
                try
                {
                    var baseSprite = ui.blackScreen != null
                        ? ui.blackScreen.GetComponent<tk2dBaseSprite>()
                        : null;
                    if (baseSprite != null && baseSprite.color.a > 0.01f)
                        ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                }
                catch
                {
                    ui.tweenBlackScreen(new Color(0f, 0f, 0f, 0f), 0.5f);
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] DoFadeInDreamBlackScreen: " + ex.Message);
            }
        }

        private static IEnumerator StartLoadDreamScene(string locationName, Vector3 position, Action<Location> onComplete)
        {
            yield return null;

            // H5: epilog_part1a_dream — destroy road before pocket load (vanilla GE path).
            if (string.Equals(locationName, "epilog_part1a_dream", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var outside = Singleton<OutsideLocations>.Instance;
                    if (outside != null
                        && outside.spawnedLocations != null
                        && outside.spawnedLocations.ContainsKey("outside_roadToHome_01"))
                    {
                        outside.destroyLocation("outside_roadToHome_01");
                        ModRuntime.LegacyInfo(
                            "[DreamSync] epilog_part1a — destroyed outside_roadToHome_01");
                    }
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DreamSync] epilog_part1a road destroy: " + ex.Message);
                }

                try
                {
                    // Mirror vanilla prepareDream forceSaveStatic for epilog 1a.
                    Singleton<SaveManager>.Instance?.Save(
                        doJson: true, doSaveProfile: true, force: true,
                        forceSaveStatic: true, showSavingIndicator: false);
                }
                catch (Exception ex)
                {
                    ModRuntime.Log?.LogWarning(
                        "[DreamSync] epilog_part1a forceSaveStatic: " + ex.Message);
                }
            }

            // Mirror OutsideLocations.prepareLocation / prepareDream:
            // - loading=true → CullableObject.Awake skips registerMe (otherwise objects
            //   register onto World grid at -75k and WorldGrid.refresh hides far nodes —
            //   client missing props "behind the door" while host looks complete).
            // - dreamPrepared=true → Location.activateOverTime uses loadFrames=1.
            var outsideLoc = Singleton<OutsideLocations>.Instance;
            bool prevDreamPrepared = false;
            if (outsideLoc != null)
            {
                outsideLoc.loading = true;
            }
            if (Dreams.Instance != null)
            {
                prevDreamPrepared = Dreams.Instance.dreamPrepared;
                Dreams.Instance.dreamPrepared = true;
            }

            Location component = null;
            try
            {
                // Must unload textures before spawning new location — vanilla
                // OutsideLocations.spawnLocation does this first thing.
                if (Singleton<Controller>.Instance != null)
                    Singleton<Controller>.Instance.unloadTextures();

                GameObject markerObj = Core.AddPrefab("LocationMarker",
                    position,
                    Quaternion.Euler(90f, 0f, 0f),
                    null);

                if (markerObj == null)
                {
                    ModRuntime.Log?.LogError("[DreamSync] Failed to create LocationMarker prefab");
                    onComplete?.Invoke(null);
                    yield break;
                }

                LocationMarker marker = markerObj.GetComponent<LocationMarker>();
                marker.locationName = locationName;

                if (Singleton<WorldGenerator>.Instance != null)
                    OutsideLocations.createGrid(locationName, marker.transform.position);

                GameObject holder = markerObj;
                Transform parentTransform = null;
                if (Singleton<WorldGenerator>.Instance != null && Singleton<WorldGenerator>.Instance.OutsideLocationsGO != null)
                {
                    holder = Singleton<WorldGenerator>.Instance.OutsideLocationsGO;
                    parentTransform = holder.transform;
                }

                yield return marker.StartCoroutine(marker.spawnLocation(holder));

                if (marker.thisLocation == null)
                {
                    ModRuntime.Log?.LogError("[DreamSync] marker.thisLocation is null after spawnLocation");
                    onComplete?.Invoke(null);
                    yield break;
                }

                component = marker.thisLocation.GetComponent<Location>();

                if (parentTransform != null)
                    marker.thisLocation.transform.parent = parentTransform;

                Singleton<OutsideLocations>.Instance.spawnedLocations[locationName] = component;
                Dreams.Instance.dreamLocation = component;
                RemapDreamUniqueObjects(component.transform);

                // Activate all child objects — vanilla transportToLocation calls
                // spawnedLocations[locationName].enter() which does activateChildren(true).
                // Without this, terrain renderers stay inactive → all-black scene.
                // Do NOT flush onEnterLocation here — enter() only starts activateOverTime;
                // GE children need player teleported + startDreaming + finishedLoading first
                // (otherwise door_underground keeps welcome_opening, not welcome_opening_dream).
                component.enter();

                // Vanilla Dreams.onLocationSpawned sets inEpilogue for epilogue locations.
                // Remote load path never hits that — clients would miss crawl/death/UI mode.
                ApplyEpilogueModeIfNeeded(component, locationName);

                if (holder != markerObj)
                    UnityEngine.Object.Destroy(markerObj);

                ModRuntime.LegacyInfo($"[DreamSync] Dream scene loaded: {locationName} at {position}");
                onComplete?.Invoke(component);
            }
            finally
            {
                // Spawn finished (or failed). CullableObject.Awake already skipped while
                // loading was true — clear so WorldGrid.refreshPlayerPos can run again.
                // Do this here so abort paths after a successful spawn cannot leave loading stuck.
                if (outsideLoc != null)
                    outsideLoc.loading = false;
                if (component == null && Dreams.Instance != null)
                    Dreams.Instance.dreamPrepared = prevDreamPrepared;
            }
        }

        /// <summary>
        /// Safety clear after dream setGrid (StartLoadDreamScene already clears loading).
        /// </summary>
        internal static void FinishDreamOutsideLoadFlags()
        {
            try
            {
                var outside = Singleton<OutsideLocations>.Instance;
                if (outside != null)
                    outside.loading = false;
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// UniqueObjects keeps the first registrant. Overworld bunker wins before the pad
        /// exists; leave-door / setActive GEs then mutate the wrong twin. Force-map pad
        /// UniqueObjects into the registry after spawn.
        /// </summary>
        internal static void RemapDreamUniqueObjects(Transform dreamRoot)
        {
            if (dreamRoot == null) return;
            try
            {
                var uo = Singleton<UniqueObjects>.Instance;
                if (uo == null || uo.objects == null) return;

                UniqueObject[] all = UnityEngine.Object.FindObjectsOfType<UniqueObject>(true);
                int remapped = 0;
                for (int i = 0; i < all.Length; i++)
                {
                    UniqueObject u = all[i];
                    if (u == null || string.IsNullOrEmpty(u.type)) continue;
                    if (!u.transform.IsChildOf(dreamRoot)
                        && Vector3.Distance(u.transform.position, dreamRoot.position) > 250f)
                        continue;

                    if (!uo.objects.TryGetValue(u.type, out UniqueObject cur) || cur != u)
                    {
                        uo.objects[u.type] = u;
                        remapped++;
                    }
                }
                if (remapped > 0)
                    ModRuntime.LegacyInfo(
                        "[DreamSync] Remapped " + remapped + " UniqueObject(s) onto dream pad");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning("[DreamSync] RemapDreamUniqueObjects: " + ex.Message);
            }
        }

        /// <summary>
        /// Mirror vanilla Dreams.onLocationSpawned epilogue branch for remote/host co-op entry.
        /// </summary>
        internal static void ApplyEpilogueModeIfNeeded(Location location, string locationName = null)
        {
            try
            {
                bool isEpilogue = (location != null && location.isEpilogueLocation)
                    || (!string.IsNullOrEmpty(locationName)
                        && locationName.IndexOf("epilog", System.StringComparison.OrdinalIgnoreCase) >= 0);

                if (!isEpilogue) return;
                if (Player.Instance == null || Player.Instance.inEpilogue) return;

                Player.Instance.inEpilogue = true;
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.hideVisibleUI();

                var cam = Singleton<CamMain>.Instance;
                if (cam != null && cam.FireMaskCam != null)
                    cam.FireMaskCam.gameObject.SetActive(true);

                // First-entry epilogue title (same delay spirit as OutsideLocations.onSpawnedLocation).
                if (Singleton<Controller>.Instance != null && Singleton<UI>.Instance != null)
                {
                    Singleton<Controller>.Instance.Invoke(
                        Singleton<UI>.Instance.showEpilogueText, 1f, timeScaleDependent: true);
                }

                ModRuntime.LegacyInfo($"[DreamSync] Epilogue mode applied (loc={locationName ?? location?.name})");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] ApplyEpilogueMode failed: {ex.Message}");
            }
        }

        private static void CleanupDreamScene(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return;

            try
            {
                if (Singleton<WorldGrid>.Instance != null)
                {
                    var grid = Singleton<WorldGrid>.Instance.getGrid(locationName);
                    if (grid != null)
                        Singleton<WorldGrid>.Instance.grids.Remove(grid);
                }

                if (Singleton<OutsideLocations>.Instance != null &&
                    Singleton<OutsideLocations>.Instance.spawnedLocations.ContainsKey(locationName))
                {
                    Singleton<OutsideLocations>.Instance.spawnedLocations.Remove(locationName);
                }

                GameObject targetObj = null;
                if (Dreams.Instance != null && Dreams.Instance.dreamLocation != null && Dreams.Instance.dreamLocation.gameObject != null)
                {
                    string objName = Dreams.Instance.dreamLocation.gameObject.name.Replace("_done", "");
                    if (string.Equals(objName, locationName, StringComparison.OrdinalIgnoreCase))
                        targetObj = Dreams.Instance.dreamLocation.gameObject;
                }

                if (targetObj == null)
                    targetObj = GameObject.Find(locationName + "_done");

                if (targetObj != null)
                {
                    UnityEngine.Object.Destroy(targetObj, 2f);
                    if (Dreams.Instance != null)
                        Dreams.Instance.dreamLocation = null;
                }

                ModRuntime.LegacyInfo($"[DreamSync] Dream scene cleaned up: {locationName}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Error during dream scene cleanup: {ex}");
            }
        }

        private static void ApplyDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO != null)
                {
                    Core.modifyCamEffects(active: true, presetGO);
                    ModRuntime.LegacyInfo($"[DreamSync] Applied camera effects for dream: {presetName}");
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to apply camera effects: {ex}");
            }
        }

        private static void RemoveDreamCameraEffects(string presetName)
        {
            if (string.IsNullOrEmpty(presetName)) return;
            try
            {
                GameObject presetGO = Resources.Load("DreamPresets/" + presetName) as GameObject;
                if (presetGO == null) return;
                // modifyCamEffects can NRE if CamMain/effects torn down mid-quit.
                if (Singleton<CamMain>.Instance == null) return;
                Core.modifyCamEffects(active: false, presetGO);
                ModRuntime.LegacyInfo($"[DreamSync] Removed camera effects for dream: {presetName}");
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to remove camera effects: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Starts the dream transition (video/audio overlay) on the remote client.
        /// Returns the number of seconds to wait for the transition to finish,
        /// or 0 if the transition should be skipped (fallback to black screen).
        /// </summary>
        private static float StartRemoteDreamTransition()
        {
            if (_remoteEntryTransitionPlaying)
            {
                float remain = _earlyEntryTransitionDoneAt - Time.realtimeSinceStartup;
                return Mathf.Max(0f, remain);
            }

            var transition = Dreams.Instance?.startTransition;
            if (transition == null || transition.transitionObjects == null)
            {
                ShowDreamTransitionFallback();
                return 0f;
            }

            try
            {
                Core.EnteringDream = true;
                _remoteEntryTransitionPlaying = true;

                // Stop all audio (same as DreamTransition.transition)
                AudioController.StopAll(transition.fadeAllAudioTime);
                Singleton<Controller>.Instance?.fadeAudio(fadeOut: true, 2f, musicToo: false);

                float videoLength = 0f;
                bool hasVideo = false;
                _remoteEntryAudioId = null;

                for (int i = 0; i < transition.transitionObjects.Count; i++)
                {
                    var obj = transition.transitionObjects[i];
                    if (obj == null) continue;

                    if (obj.type == DreamTransition.TransitionObject.Type.Audio)
                    {
                        // Play only the first entry stinger — multiple Audio objects stacked
                        // with the video soundtrack as a doubled/prolonged enter sound.
                        if (string.IsNullOrEmpty(_remoteEntryAudioId)
                            && !string.IsNullOrEmpty(obj.audioItemName))
                        {
                            AudioController.Play(obj.audioItemName);
                            _remoteEntryAudioId = obj.audioItemName;
                        }
                    }
                    else if (obj.type == DreamTransition.TransitionObject.Type.Video)
                    {
                        hasVideo = true;
                        Renderer renderer = Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>();
                        if (renderer == null) continue;

                        string path = "Video/" + obj.videoName;
                        if (obj.localizedVideo)
                            path = path + "_" + GameSettings.GetString("LanguageCode");

                        VideoClip clip = Resources.Load(path, typeof(VideoClip)) as VideoClip;
                        if (clip == null) continue;

                        videoLength = (float)clip.length;
                        renderer.enabled = false;
                        VideoPlayer vp = renderer.GetComponent<VideoPlayer>();
                        vp.clip = clip;
                        // Mute video audio when we already play the dedicated Audio stinger —
                        // otherwise client hears stinger + video track (doubled enter sound).
                        if (!string.IsNullOrEmpty(_remoteEntryAudioId))
                            vp.SetDirectAudioMute(0, true);

                        vp.prepareCompleted += OnRemoteTransitionVideoPrepared;
                        vp.Prepare();
                        Singleton<UI>.Instance.videoOverlay.gameObject.SetActive(true);

                        // Fade in
                        if (obj.fadeIn > 0f)
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 0f);
                            renderer.material.DOFade(1f, obj.fadeIn).SetUpdate(true);
                        }
                        else
                        {
                            renderer.material.color = new Color(1f, 1f, 1f, 1f);
                        }
                    }
                }

                // Use durationOverride if set (same logic as DreamTransition.transition)
                if (transition.durationOverride > 0f)
                    videoLength = transition.durationOverride;

                if (!hasVideo)
                {
                    ShowDreamTransitionFallback();
                    _remoteEntryTransitionPlaying = false;
                    return 0f;
                }

                // Set black screen behind the video overlay so there's no
                // visual gap when the video ends and the dream loads.
                if (Singleton<UI>.Instance != null)
                    Singleton<UI>.Instance.tweenBlackScreen(new Color(0f, 0f, 0f, 1f), 0.1f);

                _earlyEntryTransitionDoneAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, videoLength);
                ModRuntime.LegacyInfo($"[DreamSync] Remote dream transition started, wait={videoLength:F1}s");
                return videoLength;
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to play remote dream transition: {ex}");
                _remoteEntryTransitionPlaying = false;
                ShowDreamTransitionFallback();
                return 0f;
            }
        }

        private static void OnRemoteTransitionVideoPrepared(VideoPlayer player)
        {
            player.prepareCompleted -= OnRemoteTransitionVideoPrepared;
            Singleton<UI>.Instance.videoOverlay.GetComponent<Renderer>().enabled = true;
            player.Play();
        }

        private static void ShowDreamTransitionFallback()
        {
            if (Singleton<UI>.Instance == null) return;

            try
            {
                var blackTop = Singleton<UI>.Instance.blackScreenTop;
                if (blackTop != null)
                {
                    var sprite = blackTop.GetComponent<tk2dBaseSprite>();
                    if (sprite != null)
                    {
                        Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 1f), 0.3f);
                        Singleton<Controller>.Instance?.waitFramesAndRun(delegate
                        {
                            if (sprite != null && sprite.color.a != 0f)
                            {
                                Singleton<UI>.Instance.tweenBlackScreenTop(new Color(0f, 0f, 0f, 0f), 0.5f);
                            }
                        }, 1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModRuntime.Log?.LogWarning($"[DreamSync] Failed to show dream transition fallback: {ex}");
            }
        }

        /// <summary>
        /// Safety timeout: unfreezes all remote proxies after <paramref name="delay"/> seconds
        /// of real time. Prevents permanent proxy freeze if DreamEntered never arrives.
        /// </summary>
        private static System.Collections.IEnumerator UnfreezeProxiesAfterDelay(float delay)
        {
            yield return new UnityEngine.WaitForSecondsRealtime(delay);
            var net = ModRuntime.Network as LanNetworkManager;
            if (net != null)
            {
                foreach (var proxy in net.GetAllProxies())
                    proxy.FreezePosition = false;
            }
        }
    }
}
